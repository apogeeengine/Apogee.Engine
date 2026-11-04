using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Apogee.Core.Logging;
using Apogee.Ecs;
using Apogee.Native.Luau;
using Apogee.Reflection;
using EcsWorld = Apogee.Ecs.World;
using LuaType = Apogee.Native.Luau.Type;

namespace Apogee.Scripting;

public sealed unsafe partial class LuauVm
{
    /// <summary>A component of an entity, seen from a script during one call. <c>Data</c> is null once the call is over.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ViewRef
    {
        public byte* Data;
        public int Component;
    }

    /// <summary>Which terms of a query give the callback a component, and how big each is.</summary>
    private sealed class ViewPlan(int[] termIndex, int[] componentIndex, int[] size)
    {
        public int[] TermIndex { get; } = termIndex;

        public int[] ComponentIndex { get; } = componentIndex;

        public int[] Size { get; } = size;

        public int Count => TermIndex.Length;
    }

    private readonly List<ComponentInfo> _componentInfos = [];
    private readonly Dictionary<ComponentInfo, int> _componentIndex = [];
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<EcsWorld, Dictionary<string, Query>> _queries = [];

    private int ComponentIndexOf(ComponentInfo info)
    {
        if (!_componentIndex.TryGetValue(info, out int index))
        {
            index = _componentInfos.Count;
            _componentInfos.Add(info);
            _componentIndex[info] = index;
        }

        return index;
    }

    /// <summary>Registers the ECS methods of <c>World</c> that take tables and functions. They come with their definitions.</summary>
    internal static void RegisterEcsNatives()
    {
        ScriptNatives.Register("World", "component", &WorldComponentBody,
            "function component(self, name: string, fields: ({ [string]: FieldType } | {})?): number");
        ScriptNatives.Register("World", "set", &WorldSetBody,
            "function set(self, entity: number, component: number, values: { [string]: any }?): ()");
        ScriptNatives.Register("World", "get", &WorldGetBody,
            "function get(self, entity: number, component: number): { [string]: any }?");
        ScriptNatives.Register("World", "count", &WorldCountBody,
            "function count(self, components: { Term }): number");
        ScriptNatives.Register("World", "each", &WorldEachBody,
            "function each(self, components: { Term }, callback: (entity: number, ...ComponentView) -> ()): ()");
        ScriptNatives.Register("World", "system", &WorldSystemBody,
            "function system(self, name: string, components: { Term }, callback: (entity: number, ...ComponentView) -> (), phase: Phase?): number");
    }


    // ---- Helpers --------------------------------------------------------------------------------------------------------------------------

    private EcsWorld GetWorld(State* L, int index) =>
        GetObject(L, index) as EcsWorld ?? throw new ValueException($"expected a World, got {Stack.TypeName(L, index)}");

    private static string ReadStringArgument(State* L, int index, string what) =>
        Stack.Is(L, index, LuaType.Tstring) ? Stack.ReadString(L, index) : throw new ValueException($"{what} must be a string, got {Stack.TypeName(L, index)}");

    private static FieldKind ParseFieldKind(string name) => name switch
    {
        "bool" => FieldKind.Bool,
        "i8" => FieldKind.Int8,
        "u8" => FieldKind.UInt8,
        "i16" => FieldKind.Int16,
        "u16" => FieldKind.UInt16,
        "i32" => FieldKind.Int32,
        "u32" => FieldKind.UInt32,
        "i64" => FieldKind.Int64,
        "u64" => FieldKind.UInt64,
        "f32" => FieldKind.Float32,
        "f64" => FieldKind.Float64,
        "vec3" => FieldKind.Vector3,
        "dvec3" => FieldKind.Double3,
        "quat" => FieldKind.Quaternion,
        "entity" => FieldKind.Entity,
        _ => throw new ValueException($"'{name}' is not a field type (bool, i8, u8, i16, u16, i32, u32, i64, u64, f32, f64, vec3, dvec3, quat, entity)"),
    };

    private static Phase ParsePhase(string name) => name switch
    {
        "on_load" => Phase.OnLoad,
        "post_load" => Phase.PostLoad,
        "pre_update" => Phase.PreUpdate,
        "on_update" => Phase.OnUpdate,
        "on_validate" => Phase.OnValidate,
        "post_update" => Phase.PostUpdate,
        "pre_store" => Phase.PreStore,
        "on_store" => Phase.OnStore,
        _ => throw new ValueException($"'{name}' is not a phase (on_load, post_load, pre_update, on_update, on_validate, post_update, pre_store, on_store)"),
    };

    /// <summary>A list of components: each a number, <c>{id, "not"}</c> or <c>{id, "optional"}</c>, or a pair <c>{first, second}</c>.</summary>
    private static List<QueryTerm> ReadTerms(State* L, int index)
    {
        if (!Stack.Is(L, index, LuaType.Ttable))
        {
            throw new ValueException($"expected a table of components, got {Stack.TypeName(L, index)}");
        }

        int table = Lua.Absindex(L, index);
        int count = Lua.Objlen(L, table);
        var terms = new List<QueryTerm>(count);
        Stack.Reserve(L, 4);
        for (int i = 1; i <= count; i++)
        {
            _ = Stack.RawGetI(L, table, i);
            try
            {
                if (Stack.Is(L, -1, LuaType.Tnumber))
                {
                    terms.Add(new QueryTerm(ReadEntity(L, -1)));
                }
                else if (Stack.Is(L, -1, LuaType.Ttable))
                {
                    _ = Stack.RawGetI(L, -1, 2);
                    bool hasOperator = Stack.Is(L, -1, LuaType.Tstring);
                    string? name = hasOperator ? Stack.ReadString(L, -1) : null;
                    Lua.Settop(L, -2);
                    if (hasOperator)
                    {
                        _ = Stack.RawGetI(L, -1, 1);
                        Entity id = ReadEntity(L, -1);
                        Lua.Settop(L, -2);
                        terms.Add(new QueryTerm(id, name switch
                        {
                            "not" => QueryOperator.Not,
                            "optional" => QueryOperator.Optional,
                            "and" => QueryOperator.And,
                            _ => throw new ValueException($"'{name}' is not a query operator (not, optional)"),
                        }));
                    }
                    else
                    {
                        terms.Add(new QueryTerm(ReadEntity(L, -1))); // a pair
                    }
                }
                else
                {
                    throw new ValueException($"component {i} is a {Stack.TypeName(L, -1)}, not an id");
                }
            }
            finally
            {
                Lua.Settop(L, -2);
            }
        }

        return terms;
    }

    private ViewPlan PlanViews(EcsWorld world, List<QueryTerm> terms)
    {
        var termIndex = new List<int>();
        var componentIndex = new List<int>();
        var size = new List<int>();
        for (int i = 0; i < terms.Count; i++)
        {
            if (terms[i].Operator == QueryOperator.Not)
            {
                continue;
            }

            ComponentInfo? info = world.GetComponentInfo(terms[i].Id);
            if (info is null || info.IsTag)
            {
                continue;
            }

            termIndex.Add(i);
            componentIndex.Add(ComponentIndexOf(info));
            size.Add(info.Size);
        }

        return new ViewPlan([.. termIndex], [.. componentIndex], [.. size]);
    }

    /// <summary>A query for the terms; kept for the next call, as long as the world lives.</summary>
    private Query GetQuery(EcsWorld world, List<QueryTerm> terms)
    {
        Dictionary<string, Query> cache = _queries.GetOrCreateValue(world);
        var key = new StringBuilder();
        foreach (QueryTerm term in terms)
        {
            key.Append(term.Id.Id).Append(':').Append((int)term.Operator).Append(',');
        }

        string text = key.ToString();
        if (!cache.TryGetValue(text, out Query? query) || query.IsDisposed)
        {
            query = world.CreateQuery(terms.ToArray());
            cache[text] = query;
        }

        return query;
    }

    private void PushViews(State* S, ViewPlan plan)
    {
        for (int j = 0; j < plan.Count; j++)
        {
            var view = (ViewRef*)Lua.Newuserdatataggedwithmetatable(S, (nuint)sizeof(ViewRef), ViewTag);
            view->Data = null;
            view->Component = plan.ComponentIndex[j];
        }
    }

    private static void ClearViews(State* S, int firstView, int count)
    {
        for (int j = 0; j < count; j++)
        {
            var view = (ViewRef*)Lua.Touserdatatagged(S, firstView + j, ViewTag);
            if (view is not null)
            {
                view->Data = null;
            }
        }
    }

    /// <summary>Calls the function (at <paramref name="functionIndex"/>) for each entity of the table with the entity and a view of each component.</summary>
    private static void CallRows(State* S, in TableView table, int functionIndex, ViewPlan plan, int firstView, int budget)
    {
        ReadOnlySpan<Entity> entities = table.Entities;
        Span<nint> bases = stackalloc nint[Math.Max(plan.Count, 1)];
        for (int j = 0; j < plan.Count; j++)
        {
            bases[j] = (nint)table.FieldPointer(plan.TermIndex[j], plan.Size[j]);
        }

        for (int row = 0; row < entities.Length; row++)
        {
            Stack.Reserve(S, plan.Count + 3);
            Lua.Pushvalue(S, functionIndex);
            Lua.Pushnumber(S, entities[row].Id);
            for (int j = 0; j < plan.Count; j++)
            {
                var view = (ViewRef*)Lua.Touserdatatagged(S, firstView + j, ViewTag);
                if (bases[j] == 0)
                {
                    view->Data = null;
                    Lua.Pushnil(S); // an optional component this table does not have
                }
                else
                {
                    view->Data = (byte*)bases[j] + (long)row * plan.Size[j];
                    Lua.Pushvalue(S, firstView + j);
                }
            }

            ThrowIfFailed(Lua.ProtectedCall(S, 1 + plan.Count, 0, budget), S);
        }
    }

    // ---- World:component, set, get, count ------------------------------------------------------------------------------------------------------

    /// <summary>Reads <c>{ type = "f32", min = 0, max = 10, step = 0.5, category = "...", hidden = true, read_only = true }</c> at <paramref name="index"/>.</summary>
    private static (FieldKind Kind, InspectorMeta? Meta) ReadFieldTable(State* L, int index, string fieldName)
    {
        int table = Lua.Absindex(L, index);
        Stack.Reserve(L, 4);
        FieldKind? kind = null;
        double min = double.NaN;
        double max = double.NaN;
        double step = double.NaN;
        string? category = null;
        bool hidden = false;
        bool readOnly = false;
        for (int iterator = 0; (iterator = Lua.Rawiter(L, table, iterator)) >= 0;)
        {
            try
            {
                if (!Stack.Is(L, -2, LuaType.Tstring))
                {
                    throw new ValueException($"the table of the field {fieldName} has named entries only");
                }

                string key = Stack.ReadString(L, -2);
                switch (key)
                {
                    case "type":
                        kind = ParseFieldKind(ReadFieldString(L, fieldName, key));
                        break;
                    case "min":
                        min = ReadFieldNumber(L, fieldName, key);
                        break;
                    case "max":
                        max = ReadFieldNumber(L, fieldName, key);
                        break;
                    case "step":
                        step = ReadFieldNumber(L, fieldName, key);
                        break;
                    case "category":
                        category = ReadFieldString(L, fieldName, key);
                        break;
                    case "hidden":
                        hidden = Lua.Toboolean(L, -1) != 0;
                        break;
                    case "read_only":
                        readOnly = Lua.Toboolean(L, -1) != 0;
                        break;
                    default:
                        throw new ValueException($"the field {fieldName} has an unknown entry '{key}' (type, min, max, step, category, hidden, read_only)");
                }
            }
            finally
            {
                Lua.Settop(L, -3);
            }
        }

        if (kind is null)
        {
            throw new ValueException($"the field {fieldName} needs a type");
        }

        bool any = !double.IsNaN(min) || !double.IsNaN(max) || !double.IsNaN(step) || category is not null || hidden || readOnly;
        return (kind.Value, any ? new InspectorMeta(min, max, step, category, hidden, readOnly) : null);
    }

    private static string ReadFieldString(State* L, string fieldName, string key) =>
        Stack.Is(L, -1, LuaType.Tstring) ? Stack.ReadString(L, -1) : throw new ValueException($"{fieldName}.{key} must be a string");

    private static double ReadFieldNumber(State* L, string fieldName, string key) =>
        Stack.Is(L, -1, LuaType.Tnumber) ? Lua.Tonumberx(L, -1, null) : throw new ValueException($"{fieldName}.{key} must be a number");

    private static int WorldComponentBody(State* L)
    {
        LuauVm vm = VmOf(L);
        EcsWorld world = vm.GetWorld(L, 1);
        string name = ReadStringArgument(L, 2, "the component name");

        Entity id;
        if (Lua.Type(L, 3) <= (int)LuaType.Tnil)
        {
            id = world.FindComponent(name);
            if (id.IsNull)
            {
                ComponentInfo info = TypeRegistry.FindComponent(name)
                    ?? throw new ValueException($"no component named {name} is registered; give its fields to define one");
                id = world.RegisterComponent(info);
            }
        }
        else
        {
            if (!Stack.Is(L, 3, LuaType.Ttable))
            {
                throw new ValueException($"the fields of a component are a table of name = type, got {Stack.TypeName(L, 3)}");
            }

            var fields = new List<(string Name, FieldKind Kind)>();
            var meta = new Dictionary<string, InspectorMeta>(StringComparer.Ordinal);
            Stack.Reserve(L, 6);
            for (int iterator = 0; (iterator = Lua.Rawiter(L, 3, iterator)) >= 0;)
            {
                try
                {
                    if (!Stack.Is(L, -2, LuaType.Tstring))
                    {
                        throw new ValueException("the fields of a component are a table of name = type, or name = { type = ..., min = ... }");
                    }

                    string fieldName = Stack.ReadString(L, -2);
                    if (Stack.Is(L, -1, LuaType.Tstring))
                    {
                        fields.Add((fieldName, ParseFieldKind(Stack.ReadString(L, -1))));
                    }
                    else if (Stack.Is(L, -1, LuaType.Ttable))
                    {
                        // name = { type = "f32", min = 0, max = 10, step = 0.5, category = "Movement", hidden = false, read_only = false }: the type and what an inspector shows.
                        (FieldKind kind, InspectorMeta? fieldMeta) = ReadFieldTable(L, -1, fieldName);
                        fields.Add((fieldName, kind));
                        if (fieldMeta is not null)
                        {
                            meta[fieldName] = fieldMeta;
                        }
                    }
                    else
                    {
                        throw new ValueException($"the field {fieldName} is a type name or a table with a type, got {Stack.TypeName(L, -1)}");
                    }
                }
                finally
                {
                    Lua.Settop(L, -3);
                }
            }

            try
            {
                id = world.DefineComponent(name, fields, meta);
            }
            catch (InvalidOperationException e)
            {
                throw new ValueException(e.Message);
            }
        }

        Lua.Pushnumber(L, id.Id);
        return 1;
    }

    private static int WorldSetBody(State* L)
    {
        LuauVm vm = VmOf(L);
        EcsWorld world = vm.GetWorld(L, 1);
        Entity entity = ReadEntity(L, 2);
        Entity component = ReadEntity(L, 3);
        ComponentInfo? info = world.GetComponentInfo(component);
        bool hasValues = Stack.Is(L, 4, LuaType.Ttable);

        if (!world.IsAlive(entity))
        {
            throw new ValueException($"entity {entity} is not alive");
        }

        if (info is null || info.IsTag)
        {
            if (hasValues)
            {
                throw new ValueException("a tag has no fields to set");
            }

            world.Add(entity, component);
            return 0;
        }

        // Read and check every field before writing any, so a bad value leaves the component as it was.
        var writes = new List<(FieldDef Field, Value Value)>();
        if (hasValues)
        {
            Stack.Reserve(L, 4);
            for (int iterator = 0; (iterator = Lua.Rawiter(L, 4, iterator)) >= 0;)
            {
                try
                {
                    string fieldName = Stack.Is(L, -2, LuaType.Tstring) ? Stack.ReadString(L, -2) : throw new ValueException("the fields are named by strings");
                    FieldDef field = info.FindField(fieldName) ?? throw new ValueException($"'{fieldName}' is not a field of {info.Name}");
                    writes.Add((field, vm.ReadValue(L, -1, TypeRef.Any)));
                }
                finally
                {
                    Lua.Settop(L, -3);
                }
            }
        }

        // A component that does not exist yet starts zeroed; EnsurePointer adds it.
        byte* data = world.EnsurePointer(entity, component, info.Size);
        foreach ((FieldDef field, Value value) in writes)
        {
            try
            {
                FieldAccess.Write(data, field, value);
            }
            catch (ValueException e)
            {
                throw new ValueException($"{info.Name}.{field.Name}: {e.Message}");
            }
        }

        world.Modified(entity, component);
        return 0;
    }

    private static int WorldGetBody(State* L)
    {
        LuauVm vm = VmOf(L);
        EcsWorld world = vm.GetWorld(L, 1);
        Entity entity = ReadEntity(L, 2);
        Entity component = ReadEntity(L, 3);
        ComponentInfo? info = world.GetComponentInfo(component);

        if (info is null)
        {
            Lua.Pushboolean(L, world.Has(entity, component) ? 1 : 0); // a plain tag or entity: whether it has it
            return 1;
        }

        if (!world.Has(entity, component))
        {
            Lua.Pushnil(L);
            return 1;
        }

        // A copy: a table of the values the component has now. Views in each and system are the way to write in place.
        Stack.Reserve(L, 4);
        Lua.Createtable(L, 0, info.Fields.Count);
        byte* data = info.IsTag ? null : world.GetPointer(entity, component);
        foreach (FieldDef field in info.Fields)
        {
            vm.PushValue(L, FieldAccess.Read(data, field));
            fixed (byte* key = Apogee.Interop.Utf8.ToZeroTerminated(field.Name))
            {
                Lua.Rawsetfield(L, -2, key);
            }
        }

        return 1;
    }

    private static int WorldCountBody(State* L)
    {
        LuauVm vm = VmOf(L);
        EcsWorld world = vm.GetWorld(L, 1);
        List<QueryTerm> terms = ReadTerms(L, 2);
        Lua.Pushnumber(L, vm.GetQuery(world, terms).Count());
        return 1;
    }

    // ---- World:each and system ---------------------------------------------------------------------------------------------------------------------------

    private static int WorldEachBody(State* L)
    {
        LuauVm vm = VmOf(L);
        EcsWorld world = vm.GetWorld(L, 1);
        List<QueryTerm> terms = ReadTerms(L, 2);
        if (!Stack.Is(L, 3, LuaType.Tfunction))
        {
            throw new ValueException($"each expects a function, got {Stack.TypeName(L, 3)}");
        }

        ViewPlan plan = vm.PlanViews(world, terms);
        Query query = vm.GetQuery(world, terms);
        Stack.Reserve(L, plan.Count + 4);
        int firstView = Lua.Gettop(L) + 1;
        vm.PushViews(L, plan);

        nint thread = (nint)L;
        try
        {
            query.ForEachTable((in TableView table) => CallRows((State*)thread, in table, 3, plan, firstView, 0));
        }
        finally
        {
            ClearViews(L, firstView, plan.Count);
        }

        return 0;
    }

    private static int WorldSystemBody(State* L)
    {
        LuauVm vm = VmOf(L);
        EcsWorld world = vm.GetWorld(L, 1);
        string name = ReadStringArgument(L, 2, "the system name");
        List<QueryTerm> terms = ReadTerms(L, 3);
        if (!Stack.Is(L, 4, LuaType.Tfunction))
        {
            throw new ValueException($"system expects a function, got {Stack.TypeName(L, 4)}");
        }

        Phase phase = Lua.Type(L, 5) <= (int)LuaType.Tnil ? Phase.OnUpdate : ParsePhase(ReadStringArgument(L, 5, "the phase"));
        LuauFunction function = vm.CreateFunction(L, 4);
        ViewPlan plan = vm.PlanViews(world, terms);

        Entity system = world.AddSystem(name, terms.ToArray(), phase, (in TableView table) => vm.RunSystem(world, function, plan, name, in table));
        ScriptScope.OnReload(() =>
        {
            if (world.IsAlive(system))
            {
                world.Destroy(system);
            }
        });
        Lua.Pushnumber(L, system.Id);
        return 1;
    }

    /// <summary>Runs a system a script made: on the owner's thread, with views of the components. A failure is logged and the system skips the rest of the frame.</summary>
    private void RunSystem(EcsWorld world, LuauFunction function, ViewPlan plan, string name, in TableView table)
    {
        if (function.FailedFrame == world.Frame)
        {
            return;
        }

        State* thread = CallThread(function.Owner);
        int top = Lua.Gettop(thread);
        try
        {
            Stack.Reserve(thread, plan.Count + 4);
            _ = Stack.RawGetI(thread, Lua.Registryindex, function.Reference);
            PushViews(thread, plan);
            try
            {
                CallRows(thread, in table, top + 1, plan, top + 2, function.Owner.TimeBudgetMilliseconds);
            }
            finally
            {
                ClearViews(thread, top + 2, plan.Count);
            }
        }
        catch (ScriptException failure)
        {
            function.FailedFrame = world.Frame;
            Log.Error("Script:" + function.Owner.Id, $"The system {name} failed and is skipped for the rest of this frame: {failure.Message}");
        }
        finally
        {
            Lua.Settop(thread, top);
        }
    }

    // ---- Views ---------------------------------------------------------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ViewIndex(State* L) => Guard(L, &ViewIndexBody);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ViewNewIndex(State* L) => Guard(L, &ViewNewIndexBody);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ViewToString(State* L) => Guard(L, &ViewToStringBody);

    private static (nint View, ComponentInfo Info, FieldDef Field) ResolveField(LuauVm vm, State* L)
    {
        var view = (ViewRef*)Lua.Touserdatatagged(L, 1, ViewTag);
        if (view is null)
        {
            throw new ValueException("expected a component view");
        }

        if (view->Data is null)
        {
            throw new ValueException("a component view is only valid during the call it was given to");
        }

        ComponentInfo info = vm._componentInfos[view->Component];
        string name = Stack.Is(L, 2, LuaType.Tstring) ? Stack.ReadString(L, 2) : throw new ValueException("a component is indexed by the names of its fields");
        return ((nint)view, info, info.FindField(name) ?? throw new ValueException($"'{name}' is not a field of {info.Name}"));
    }

    private static int ViewIndexBody(State* L)
    {
        LuauVm vm = VmOf(L);
        (nint view, _, FieldDef field) = ResolveField(vm, L);
        vm.PushValue(L, FieldAccess.Read(((ViewRef*)view)->Data, field));
        return 1;
    }

    private static int ViewNewIndexBody(State* L)
    {
        LuauVm vm = VmOf(L);
        (nint view, ComponentInfo info, FieldDef field) = ResolveField(vm, L);
        try
        {
            FieldAccess.Write(((ViewRef*)view)->Data, field, vm.ReadValue(L, 3, TypeRef.Any));
        }
        catch (ValueException e)
        {
            throw new ValueException($"{info.Name}.{field.Name}: {e.Message}");
        }

        return 0;
    }

    private static int ViewToStringBody(State* L)
    {
        var view = (ViewRef*)Lua.Touserdatatagged(L, 1, ViewTag);
        Stack.PushString(L, view is null ? "component" : VmOf(L)._componentInfos[view->Component].Name);
        return 1;
    }
}
