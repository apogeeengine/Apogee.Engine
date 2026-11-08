using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Apogee.Interop;
using Apogee.Native.Flecs;
using Apogee.Reflection;
using Flecs = Apogee.Native.Flecs.Ecs;
using NativeWorld = Apogee.Native.Flecs.World;

namespace Apogee.Ecs;

/// <summary>
/// A Flecs world: entities, components, queries and systems. Components are the unmanaged structs marked <c>[Component]</c>
/// (reflected, so Flecs knows their fields too) or ones a script defined at run time. Not thread safe: use it from one thread.
/// </summary>
[ApogeeClass(Creatable = true)]
public sealed unsafe partial class World : EngineObject, IDisposable
{
    private readonly NativeWorld* _world;
    private readonly Dictionary<string, Entity> _componentsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<Entity, ComponentInfo> _infoById = [];
    private readonly HashSet<Query> _queries = [];
    private readonly List<Exception> _failures = [];
    private Entity[] _byIndex = new Entity[16];
    private Entity _vector3;
    private Entity _double3;
    private Entity _quaternion;
    private readonly Entity _editorOnly;

    private static readonly System.Threading.Lock s_osLock = new();
    private static bool s_osInitialized;

    /// <summary>
    /// Flecs sets up its operating system layer (the clock, on macOS the timebase) the first time a world is made, and it marks that done before it has finished. A second thread that makes a world
    /// at that moment reads a zero timebase and divides by it: a SIGFPE on Intel Macs. The layer is set up once, under a lock, before any world.
    /// </summary>
    private static NativeWorld* CreateNativeWorld()
    {
        lock (s_osLock)
        {
            if (!s_osInitialized)
            {
                Flecs.OsInit();
                s_osInitialized = true;
            }
        }

        return Flecs.Init();
    }

    public World()
    {
        _world = CreateNativeWorld();
        if (_world is null)
        {
            throw new InvalidOperationException("Flecs could not create a world.");
        }

        // A script's `vector` is three floats; Flecs needs a struct type for it to describe a component field of that kind.
        _vector3 = CreateNamedEntity("Vector3");
        var vector = new ComponentDesc { Entity = _vector3.Id, Type = new Apogee.Native.Flecs.TypeInfo { Size = 12, Alignment = 4 } };
        Flecs.ComponentInit(_world, &vector);
        DescribeFields(_vector3, [new FieldDef("x", FieldKind.Float32, 0), new FieldDef("y", FieldKind.Float32, 4), new FieldDef("z", FieldKind.Float32, 8)]);

        // The same for a world position (three doubles) and a rotation (four floats).
        _double3 = CreateNamedEntity("Double3");
        var position = new ComponentDesc { Entity = _double3.Id, Type = new Apogee.Native.Flecs.TypeInfo { Size = 24, Alignment = 8 } };
        Flecs.ComponentInit(_world, &position);
        DescribeFields(_double3, [new FieldDef("x", FieldKind.Float64, 0), new FieldDef("y", FieldKind.Float64, 8), new FieldDef("z", FieldKind.Float64, 16)]);

        _quaternion = CreateNamedEntity("Quaternion");
        var rotation = new ComponentDesc { Entity = _quaternion.Id, Type = new Apogee.Native.Flecs.TypeInfo { Size = 16, Alignment = 4 } };
        Flecs.ComponentInit(_world, &rotation);
        DescribeFields(_quaternion, [new FieldDef("x", FieldKind.Float32, 0), new FieldDef("y", FieldKind.Float32, 4), new FieldDef("z", FieldKind.Float32, 8), new FieldDef("w", FieldKind.Float32, 12)]);

        InstallTransforms();

        // Registered now, not on first use: enumerating entities asks for it, and a new entity may move the array being enumerated.
        _editorOnly = Component<EditorOnly>();
    }

    /// <summary>The time step of the current <see cref="Progress"/>; what systems read for their delta time.</summary>
    [Property]
    public float DeltaTime { get; private set; }

    /// <summary>The number of frames <see cref="Progress"/> has run.</summary>
    [Property]
    public long Frame { get; private set; }

    internal NativeWorld* Native => _world is null || IsDestroyed ? throw new ObjectDisposedException(nameof(World)) : _world;

    public void Dispose() => Destroy();

    protected override void OnDestroyed()
    {
        foreach (Query query in new List<Query>(_queries))
        {
            query.Dispose();
        }

        _ = Flecs.Fini(_world); // frees the contexts of systems and observers through their free callbacks
    }

    // ---- Entities ------------------------------------------------------------------------------------------------------

    /// <summary>A new entity; with a name, the entity of that name (created when it does not exist). A parent makes the name relative to it.</summary>
    [Method]
    public Entity CreateEntity(string? name = null, Entity parent = default)
    {
        NativeWorld* world = Native;
        if (name is null && parent.IsNull)
        {
            return new Entity(Flecs.New(world));
        }

        return CreateNamedEntity(name, parent);
    }

    private Entity CreateNamedEntity(string? name, Entity parent = default)
    {
        byte[]? utf8 = name is null ? null : Utf8.ToZeroTerminated(name);
        fixed (byte* text = utf8)
        {
            var desc = new EntityDesc { Name = text, Parent = parent.Id };
            return new Entity(Flecs.EntityInit(Native, &desc));
        }
    }

    [Method]
    public bool IsAlive(Entity entity) => Flecs.IsAlive(Native, entity.Id);

    /// <summary>Deletes the entity with its components; its id is never alive again, even when Flecs reuses the number.</summary>
    [Method]
    public void Destroy(Entity entity) => Flecs.Delete(Native, entity.Id);

    /// <summary>Finds an entity by name or path (<c>"parent.child"</c>); the null entity (id 0, which scripts see as the number 0) when there is none.</summary>
    [Method]
    public Entity Lookup(string path)
    {
        fixed (byte* text = Utf8.ToZeroTerminated(path))
        {
            return new Entity(Flecs.Lookup(Native, text));
        }
    }

    /// <summary>The entity's name, or null when it has none or is not alive (Flecs does not check, and a dead entity's memory is gone).</summary>
    [Method]
    public string? GetName(Entity entity) => IsAlive(entity) ? Utf8.FromPointer(Flecs.GetName(Native, entity.Id)) : null;

    [Method]
    public void SetName(Entity entity, string name)
    {
        fixed (byte* text = Utf8.ToZeroTerminated(name))
        {
            Flecs.SetName(Native, entity.Id, text);
        }
    }

    [Method]
    public Entity GetParent(Entity entity) => new(Flecs.GetParent(Native, entity.Id));

    /// <summary>Makes <paramref name="parent"/> the parent of <paramref name="entity"/> (its <see cref="Transform"/> becomes local to it), or with no parent (0) a root. It does not change the transform: set it to keep the entity where it is.</summary>
    public void SetParent(Entity entity, Entity parent)
    {
        Entity current = GetParent(entity);
        if (current.Id == parent.Id)
        {
            return;
        }

        if (current.Id != 0)
        {
            Flecs.RemoveId(Native, entity.Id, Flecs.MakePair(Flecs.ChildOf, current.Id));
        }

        if (parent.Id != 0)
        {
            Flecs.AddId(Native, entity.Id, Flecs.MakePair(Flecs.ChildOf, parent.Id));
        }
    }

    /// <summary>The id that says "<paramref name="first"/> with <paramref name="second"/>": a relationship such as <c>(ChildOf, parent)</c>.</summary>
    public static Entity Pair(Entity first, Entity second) => new(Flecs.MakePair(first.Id, second.Id));

    // ---- Components, by id: what scripts use ---------------------------------------------------------------------------------------

    [Method]
    public void Add(Entity entity, Entity id) => Flecs.AddId(Native, entity.Id, id.Id);

    [Method]
    public void Remove(Entity entity, Entity id) => Flecs.RemoveId(Native, entity.Id, id.Id);

    [Method]
    public bool Has(Entity entity, Entity id) => Flecs.HasId(Native, entity.Id, id.Id);

    /// <summary>The component's memory on the entity, or null; valid until the next structural change.</summary>
    public byte* GetPointer(Entity entity, Entity component) => (byte*)Flecs.GetId(Native, entity.Id, component.Id);

    /// <summary>The component's memory on the entity, added (zeroed) when missing. Call <see cref="Modified"/> after writing.</summary>
    public byte* EnsurePointer(Entity entity, Entity component, int size) => (byte*)Flecs.EnsureId(Native, entity.Id, component.Id, (nuint)size);

    public void Modified(Entity entity, Entity component) => Flecs.ModifiedId(Native, entity.Id, component.Id);

    /// <summary>Sets the component from <paramref name="data"/> (copied) and adds it when missing.</summary>
    public void SetPointer(Entity entity, Entity component, int size, void* data) => Flecs.SetId(Native, entity.Id, component.Id, (nuint)size, data);

    // ---- Components, typed -------------------------------------------------------------------------------------------------------------

    /// <summary>The id of the component <typeparamref name="T"/>, registering it (and its fields, for Flecs' reflection) on first use.</summary>
    public Entity Component<T>()
        where T : unmanaged
    {
        int index = ComponentIndex<T>.Value;
        if (index < _byIndex.Length && !_byIndex[index].IsNull)
        {
            return _byIndex[index];
        }

        Entity id = RegisterComponent(TypeRegistry.GetComponent<T>());
        if (index >= _byIndex.Length)
        {
            System.Array.Resize(ref _byIndex, Math.Max(index + 1, _byIndex.Length * 2));
        }

        _byIndex[index] = id;
        return id;
    }

    public void Set<T>(Entity entity, in T value)
        where T : unmanaged
    {
        Entity id = Component<T>();
        fixed (T* data = &value)
        {
            Flecs.SetId(Native, entity.Id, id.Id, (nuint)sizeof(T), data);
        }
    }

    /// <summary>The component's value; a reference into the world's memory, valid until the next structural change.</summary>
    public ref readonly T Get<T>(Entity entity)
        where T : unmanaged
    {
        void* data = Flecs.GetId(Native, entity.Id, Component<T>().Id);
        if (data is null)
        {
            throw new KeyNotFoundException($"Entity {entity} has no {typeof(T).Name}.");
        }

        return ref *(T*)data;
    }

    public bool TryGet<T>(Entity entity, out T value)
        where T : unmanaged
    {
        void* data = Flecs.GetId(Native, entity.Id, Component<T>().Id);
        value = data is null ? default : *(T*)data;
        return data is not null;
    }

    /// <summary>A writable reference to the component, added (zeroed) when missing. Call <see cref="Modified{T}"/> after writing for observers to see it.</summary>
    public ref T Ensure<T>(Entity entity)
        where T : unmanaged =>
        ref *(T*)Flecs.EnsureId(Native, entity.Id, Component<T>().Id, (nuint)sizeof(T));

    public void Modified<T>(Entity entity)
        where T : unmanaged =>
        Flecs.ModifiedId(Native, entity.Id, Component<T>().Id);

    public void Add<T>(Entity entity)
        where T : unmanaged =>
        Flecs.AddId(Native, entity.Id, Component<T>().Id);

    public void Remove<T>(Entity entity)
        where T : unmanaged =>
        Flecs.RemoveId(Native, entity.Id, Component<T>().Id);

    public bool Has<T>(Entity entity)
        where T : unmanaged =>
        Flecs.HasId(Native, entity.Id, Component<T>().Id);

    // ---- Registering components ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Registers a component: an entity with the component's name, its size and alignment, and its fields as Flecs reflection
    /// data. Registering the same name again returns the same entity when the layout is the same and fails when it is not.
    /// </summary>
    public Entity RegisterComponent(ComponentInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (_componentsByName.TryGetValue(info.Name, out Entity existing))
        {
            ComponentInfo known = _infoById[existing];
            if (!SameLayout(known, info))
            {
                throw new InvalidOperationException($"Component {info.Name} is already registered with another layout.");
            }

            return existing;
        }

        if (info.Fields.Count > 32)
        {
            throw new NotSupportedException($"Component {info.Name} has {info.Fields.Count} fields; Flecs describes at most 32.");
        }

        Entity id = CreateNamedEntity(info.Name);
        if (!info.IsTag)
        {
            var desc = new ComponentDesc { Entity = id.Id, Type = new Apogee.Native.Flecs.TypeInfo { Size = info.Size, Alignment = info.Alignment } };
            Flecs.ComponentInit(Native, &desc);
            DescribeFields(id, info.Fields);
        }

        _componentsByName[info.Name] = id;
        _infoById[id] = info;
        return id;
    }

    /// <summary>What the registry knows about a component of this world, or null for an id that is not one.</summary>
    public ComponentInfo? GetComponentInfo(Entity component) => _infoById.GetValueOrDefault(component);

    /// <summary>The component of that name registered in this world, or null.</summary>
    public Entity FindComponent(string name) => _componentsByName.GetValueOrDefault(name);

    /// <summary>
    /// Defines a component at run time (what a script does): the fields are laid out in name order, so a schema always gives the same
    /// layout. The same name and fields again return the same component; other fields under a known name fail.
    /// </summary>
    public Entity DefineComponent(string name, IReadOnlyList<(string Name, FieldKind Kind)> fields, IReadOnlyDictionary<string, InspectorMeta>? meta = null)
    {
        var sorted = new List<(string Name, FieldKind Kind)>(fields);
        sorted.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        var layout = new List<FieldDef>();
        int offset = 0;
        int alignment = 1;
        foreach ((string fieldName, FieldKind kind) in sorted)
        {
            int fieldAlignment = kind.AlignmentOf();
            offset = (offset + fieldAlignment - 1) / fieldAlignment * fieldAlignment;
            layout.Add(new FieldDef(fieldName, kind, offset, null, meta is not null && meta.TryGetValue(fieldName, out InspectorMeta? hints) ? hints : null));
            offset += kind.SizeOf();
            alignment = Math.Max(alignment, fieldAlignment);
        }

        int size = layout.Count == 0 ? 0 : (offset + alignment - 1) / alignment * alignment;
        return RegisterComponent(new ComponentInfo(name, null, size, alignment, layout));
    }

    private static bool SameLayout(ComponentInfo a, ComponentInfo b)
    {
        if (a.Size != b.Size || a.Alignment != b.Alignment || a.Fields.Count != b.Fields.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Fields.Count; i++)
        {
            if (a.Fields[i].Name != b.Fields[i].Name || a.Fields[i].Kind != b.Fields[i].Kind || a.Fields[i].Offset != b.Fields[i].Offset)
            {
                return false;
            }
        }

        return true;
    }

    private void DescribeFields(Entity component, IReadOnlyList<FieldDef> fields)
    {
        if (fields.Count == 0)
        {
            return;
        }

        // Flecs copies the names (ecs_os_strset), so they only have to live for the call.
        var names = new string[fields.Count];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = fields[i].Name;
        }

        using var utf8 = new Utf8Array(names);
        var desc = new StructDesc { Entity = component.Id };
        for (int i = 0; i < fields.Count; i++)
        {
            desc.Members[i] = new EcsMember { Name = utf8.Items[i], Type = MemberType(fields[i].Kind), Offset = fields[i].Offset, UseOffset = true };
        }

        if (Flecs.StructInit(Native, &desc) == 0)
        {
            throw new InvalidOperationException($"Flecs could not describe the fields of {Utf8.FromPointer(Flecs.GetName(Native, component.Id))}.");
        }
    }

    private ulong MemberType(FieldKind kind) => kind switch
    {
        FieldKind.Bool => Flecs.BoolId,
        FieldKind.Int8 => Flecs.I8Id,
        FieldKind.UInt8 => Flecs.U8Id,
        FieldKind.Int16 => Flecs.I16Id,
        FieldKind.UInt16 => Flecs.U16Id,
        FieldKind.Int32 => Flecs.I32Id,
        FieldKind.UInt32 => Flecs.U32Id,
        FieldKind.Int64 => Flecs.I64Id,
        FieldKind.UInt64 => Flecs.U64Id,
        FieldKind.Float32 => Flecs.F32Id,
        FieldKind.Float64 => Flecs.F64Id,
        FieldKind.Vector3 => _vector3.Id,
        FieldKind.Double3 => _double3.Id,
        FieldKind.Quaternion => _quaternion.Id,
        _ => Flecs.EntityId,
    };

    /// <summary>A component value as the JSON Flecs' reflection writes (<c>{"x":1,"y":2}</c>); for tools and tests.</summary>
    public string ToJson(Entity component, void* data)
    {
        byte* json = Flecs.PtrToJson(Native, component.Id, data);
        try
        {
            return Utf8.FromPointer(json) ?? "";
        }
        finally
        {
            Flecs.OsApi.Free(json);
        }
    }

    public string ToJson<T>(in T value)
        where T : unmanaged
    {
        fixed (T* data = &value)
        {
            return ToJson(Component<T>(), data);
        }
    }

    // ---- Queries -------------------------------------------------------------------------------------------------------------------------------

    /// <summary>A query for entities that have all of the components.</summary>
    public Query<T1> Query<T1>()
        where T1 : unmanaged =>
        new(this, Component<T1>());

    public Query<T1, T2> Query<T1, T2>()
        where T1 : unmanaged
        where T2 : unmanaged =>
        new(this, Component<T1>(), Component<T2>());

    public Query<T1, T2, T3> Query<T1, T2, T3>()
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged =>
        new(this, Component<T1>(), Component<T2>(), Component<T3>());

    public Query<T1, T2, T3, T4> Query<T1, T2, T3, T4>()
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where T4 : unmanaged =>
        new(this, Component<T1>(), Component<T2>(), Component<T3>(), Component<T4>());

    /// <summary>A query from terms, for callers that do not know the components at compile time (scripts).</summary>
    public Query CreateQuery(ReadOnlySpan<QueryTerm> terms) => new(this, terms);

    internal void Track(Query query) => _queries.Add(query);

    internal void Untrack(Query query) => _queries.Remove(query);

    // ---- Running ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs the systems of one frame in their phases. A system or observer that throws does not stop the others: the exceptions are
    /// collected and thrown together when the frame is done (a single one as itself, several as an <see cref="AggregateException"/>).
    /// </summary>
    [Method]
    public void Progress(float deltaTime)
    {
        DeltaTime = deltaTime;
        Frame++;
        Flecs.Progress(Native, deltaTime);
        ThrowFailures();
    }

    internal void Record(Exception failure) => _failures.Add(failure);

    internal void ThrowFailures()
    {
        if (_failures.Count == 0)
        {
            return;
        }

        Exception[] failures = [.. _failures];
        _failures.Clear();
        if (failures.Length == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException("Systems failed during the frame.", failures);
    }

    private static class ComponentIndex
    {
        private static int s_next;

        public static int Next() => System.Threading.Interlocked.Increment(ref s_next) - 1;
    }

    private static class ComponentIndex<T>
    {
        public static readonly int Value = ComponentIndex.Next();
    }
}
