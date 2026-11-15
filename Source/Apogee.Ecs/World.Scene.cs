using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Apogee.Content;
using Apogee.Content.Xml;
using Apogee.Core.Assets;
using Apogee.Core.Logging;
using Apogee.Reflection;

namespace Apogee.Ecs;

// Scenes: the user entities of a world as XML text (a `.ascene` file), read and written through the engine's XmlNode and field by field from the registry's descriptions, so no reflection is
// involved and it works in a NativeAOT program. The format is ours, not Flecs': Flecs' own JSON would carry the entities of the types and systems the engine registers.
//
//   <?xml version="1.0" encoding="utf-8"?>
//   <Scene version="1">
//     <Components>                                                          <!-- only the ones a script defined: the file defines them again when it is loaded -->
//       <Component name="Ticker"><Field name="count" kind="Int32"/><Field name="look" kind="AssetRef" assetType="model"/></Component>
//     </Components>
//     <Entity id="1" name="ground">                                          <!-- one element per component, one attribute per field; a child <Entity> is a child entity -->
//       <Transform Position="0 -0.5 0" Rotation="0 0 0 1" Scale="1 1 1"/>
//       <MainCamera/>
//       <Entity id="2" name="crate"><Ticker count="3" target="1"/></Entity>
//     </Entity>
//   </Scene>
//
// "id" is the entity's number in the file (what Entity-typed fields refer to, and what a patch matches on: DefDatabase.Merge pairs children by name and id), not its id in a world.
// docs/manual/content/scenes.md is the reference; Scene/scene.xsd describes the structure.
public sealed unsafe partial class World
{
    private const string SceneRoot = "Scene";
    private const string SceneEntity = "Entity";
    private const int SceneVersion = 1;

    private readonly Dictionary<string, (Func<uint, AssetRef?> ToRef, Func<AssetRef, uint> FromRef)> _assetTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _assetTypesWarned = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sceneIgnored = new(StringComparer.Ordinal) { "WorldTransform" };

    /// <summary>
    /// Says how the fields of an asset type (<see cref="FieldDef.AssetType"/>, such as <c>model</c> for <c>ModelRenderer.Model</c>) are saved. Such a field is a number in memory, a handle that only means
    /// something in this run (a model's number in the library); a scene holds the <see cref="AssetRef"/> it names, the path and the UID, which is what lets it load in another run, and after the file moved.
    /// </summary>
    /// <param name="type">The asset type, as <c>[Property(Asset = "…")]</c> spells it.</param>
    /// <param name="toRef">The reference a handle names, or null when it names nothing that can be saved (a model made in code has no path).</param>
    /// <param name="fromRef">The handle of this run for a reference (loading what it names when needed), or 0 when there is none. When it throws, the failure is logged, the field stays 0 and the scene loads on.</param>
    public void RegisterAssetType(string type, Func<uint, AssetRef?> toRef, Func<AssetRef, uint> fromRef)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentNullException.ThrowIfNull(toRef);
        ArgumentNullException.ThrowIfNull(fromRef);
        _assetTypes[type] = (toRef, fromRef);
    }

    /// <summary>Leaves a component out of scenes: what is derived (<c>WorldTransform</c>, which is always left out) or only means something in this run (physics bodies).</summary>
    public void IgnoreInScenes(string component) => _sceneIgnored.Add(component);

    /// <summary>The user entities (<see cref="IsUserEntity"/>) as scene text (XML). It has no <c>uid</c>: text is a copy, and a copy must not carry the identity of a file.</summary>
    [Method]
    public string SaveScene() => SceneText(SaveSceneDocument(Roots()));

    /// <summary>One entity and everything below it as scene text: a prefab, a copy for the clipboard.</summary>
    public string SaveScene(Entity root) => SceneText(SaveSceneDocument([root]));

    private static string SceneText(XmlNode document) => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + document.ToXml();

    /// <summary>
    /// The entities in <paramref name="roots"/>, each with everything below it, as a <c>Scene</c> element: what <see cref="SaveScene()"/> writes as text, for code that wants the tree (to add
    /// a <c>uid</c> to, to patch with <c>DefDatabase.Merge</c>, to cook).
    /// </summary>
    public XmlNode SaveSceneDocument(IReadOnlyList<Entity> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        // The entity's number in the file is its place in this order, parents before children. A tree is written by one pass over the order: a node is added to its parent's node when the parent is in the
        // scene, and to the document otherwise, so the nesting follows the order and the order is what a loaded scene is created in: a saved scene saves again to the same text.
        var order = new List<Entity>();
        foreach (Entity root in roots)
        {
            order.AddRange(Subtree(root));
        }

        var local = new Dictionary<ulong, int>();
        for (int i = 0; i < order.Count; i++)
        {
            local[order[i].Id] = i + 1;
        }

        var scriptComponents = new SortedDictionary<string, ComponentInfo>(StringComparer.Ordinal);
        var nodes = new Dictionary<ulong, XmlNode>();
        var top = new List<XmlNode>();
        foreach (Entity entity in order)
        {
            var node = new XmlNode(SceneEntity);
            node.SetAttribute("id", local[entity.Id].ToString(CultureInfo.InvariantCulture));
            if (GetName(entity) is { } name)
            {
                node.SetAttribute("name", name);
            }

            foreach ((Entity id, ComponentInfo info) in ComponentsOf(entity))
            {
                if (_sceneIgnored.Contains(info.Name))
                {
                    continue;
                }

                if (info.ClrType is null)
                {
                    scriptComponents[info.Name] = info;
                }

                // A tag, or a component with no fields, is an empty element.
                var component = new XmlNode(info.Name);
                if (!info.IsTag)
                {
                    byte* data = GetPointer(entity, id);
                    foreach (FieldDef field in info.Fields)
                    {
                        if (FieldTextOf(info, field, data + field.Offset, local) is { } text)
                        {
                            component.SetAttribute(field.Name, text);
                        }
                    }
                }

                node.Children.Add(component);
            }

            nodes[entity.Id] = node;
            if (nodes.TryGetValue(GetParent(entity).Id, out XmlNode? parentNode))
            {
                parentNode.Children.Add(node);
            }
            else
            {
                top.Add(node);
            }
        }

        var scene = new XmlNode(SceneRoot);
        scene.SetAttribute("version", SceneVersion.ToString(CultureInfo.InvariantCulture));
        if (scriptComponents.Count > 0)
        {
            var schemas = new XmlNode("Components");
            foreach ((string name, ComponentInfo info) in scriptComponents)
            {
                var schema = new XmlNode("Component");
                schema.SetAttribute("name", name);
                foreach (FieldDef field in info.Fields)
                {
                    var entry = new XmlNode("Field");
                    entry.SetAttribute("name", field.Name);
                    entry.SetAttribute("kind", field.Kind.ToString());
                    if (field.Kind == FieldKind.AssetRef && field.AssetType is not null)
                    {
                        entry.SetAttribute("assetType", field.AssetType);
                    }

                    schema.Children.Add(entry);
                }

                schemas.Children.Add(schema);
            }

            scene.Children.Add(schemas);
        }

        scene.Children.AddRange(top);
        return scene;
    }

    // The text of a field, or null when the attribute is left out: an Entity outside the scene, a handle of 0, an asset the type cannot name.
    private string? FieldTextOf(ComponentInfo info, FieldDef field, byte* at, Dictionary<ulong, int> local)
    {
        switch (field.Kind)
        {
            case FieldKind.Entity:
                // A reference to another entity of the scene is its number in the file; to anything else, nothing.
                return local.TryGetValue(*(ulong*)at, out int target) ? target.ToString(CultureInfo.InvariantCulture) : null;
            case FieldKind.AssetRef:
                // The handle means nothing in another run: what it names goes instead, as "path|uid".
                return AssetRefOf(info, field, *(uint*)at)?.ToString();
            default:
                return FieldText.Format(field, at);
        }
    }

    // What the handle of an AssetRef field names; null for 0, for a type nobody registered (said once) and for what the type cannot name.
    private AssetRef? AssetRefOf(ComponentInfo info, FieldDef field, uint handle)
    {
        if (handle == 0)
        {
            return null;
        }

        if (field.AssetType is not { } type || !_assetTypes.TryGetValue(type, out var registered))
        {
            WarnAssetTypeOnce(info, field);
            return null;
        }

        return registered.ToRef(handle) is { IsEmpty: false } named ? named : null;
    }

    // The handle of this run for the text of a reference; 0 for no text, for a type nobody registered (said once), and for an asset that cannot be loaded (logged).
    private uint HandleOf(ComponentInfo info, FieldDef field, string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        if (field.AssetType is not { } type || !_assetTypes.TryGetValue(type, out var registered))
        {
            WarnAssetTypeOnce(info, field);
            return 0;
        }

        if (!AssetRef.TryParse(text, out AssetRef named))
        {
            throw new FormatException($"'{text}' is not an asset reference (a path, path|uid://… or uid://…)");
        }

        try
        {
            uint handle = registered.FromRef(named);
            if (handle == 0)
            {
                Log.Warn("Scene", $"{info.Name}.{field.Name}: {named} names no {field.AssetType} that this world can find: left empty");
            }

            return handle;
        }
        catch (Exception e) when (e is IOException or ContentException or FormatException or InvalidOperationException or ArgumentException)
        {
            Log.Warn("Scene", $"{info.Name}.{field.Name}: {named} could not be loaded: {e.Message}");
            return 0;
        }
    }

    private void WarnAssetTypeOnce(ComponentInfo info, FieldDef field)
    {
        string key = $"{info.Name}.{field.Name}";
        if (_assetTypesWarned.Add(key))
        {
            Log.Warn("Scene", field.AssetType is null
                ? $"{key} is an asset field with no asset type: scenes cannot save what it names"
                : $"{key} has the asset type '{field.AssetType}', which this world has no RegisterAssetType for: scenes cannot save what it names");
        }
    }

    /// <summary>
    /// Creates the entities of scene text, under <paramref name="parent"/> when given, and returns them in the order of the file (parents first). A component the file names that this world cannot
    /// find, and a field it does not have, are logged and skipped; components that scripts defined are defined again from the file. Throws <see cref="FormatException"/> when the text is not a scene
    /// (including JSON text from before scenes were XML).
    /// </summary>
    [Method]
    public List<Entity> LoadScene(string text, Entity parent = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        // A BOM or blank lines may come before the first character; a scene written before the format was XML starts with a brace.
        if (text.AsSpan().TrimStart("\uFEFF \t\r\n").StartsWith("{", StringComparison.Ordinal))
        {
            throw new FormatException("the scene is JSON text; scenes are XML now and the JSON format is no longer read");
        }

        XmlNode root;
        try
        {
            root = XmlNode.Parse(text, "scene");
        }
        catch (XmlParseException e)
        {
            // XmlNode reports a ContentException; a scene text that cannot be read is a FormatException, as it always was, for the callers that tell the two apart.
            throw new FormatException($"the scene is not valid XML: {e.Message}", e);
        }

        return LoadScene(root, parent);
    }

    /// <summary>
    /// Creates the entities of a <c>Scene</c> element (<see cref="SaveSceneDocument"/>, or a file read with <see cref="XmlNode.Parse"/>, possibly patched), under <paramref name="parent"/> when given.
    /// See <see cref="LoadScene(string, Entity)"/>.
    /// </summary>
    public List<Entity> LoadScene(XmlNode root, Entity parent = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.Name != SceneRoot)
        {
            throw new FormatException($"the document is not a scene: its root is <{root.Name}>, not <{SceneRoot}>");
        }

        if (root.Attribute("version") is { } versionText)
        {
            if (!int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
            {
                throw new FormatException($"the scene has the version '{versionText}', which is not a number");
            }

            if (version > SceneVersion)
            {
                throw new FormatException($"the scene is version {version}; this engine reads up to {SceneVersion}");
            }
        }

        if (root.Child("Components") is { } schemas)
        {
            DefineSceneComponents(schemas);
        }

        // Every entity first, in the order of the file, so that entity-typed fields can refer to the ones that come later in it; the parents next (they decide whether a name is free), then the names and components.
        var created = new List<Entity>();
        var nodes = new List<XmlNode>();
        var under = new List<Entity>();
        var byLocal = new Dictionary<int, Entity>();
        CreateSceneEntities(root, parent, created, nodes, under, byLocal);

        for (int i = 0; i < created.Count; i++)
        {
            if (!under[i].IsNull)
            {
                SetParent(created[i], under[i]);
            }
        }

        for (int i = 0; i < created.Count; i++)
        {
            Entity entity = created[i];
            if (nodes[i].Attribute("name") is { Length: > 0 } entityName)
            {
                TrySetName(entity, entityName);
            }

            foreach (XmlNode component in nodes[i].Children)
            {
                if (component.Name != SceneEntity)
                {
                    ReadComponent(entity, component, byLocal);
                }
            }
        }

        return created;
    }

    // The <Entity> children of a node, depth first and in order (the order the ids of a scene count in). Recursion is bounded by XmlNode.MaxDepth, which the parser and the cooked reader enforce.
    private void CreateSceneEntities(XmlNode container, Entity parent, List<Entity> created, List<XmlNode> nodes, List<Entity> under, Dictionary<int, Entity> byLocal)
    {
        foreach (XmlNode node in container.Children)
        {
            if (node.Name != SceneEntity)
            {
                continue;
            }

            Entity entity = CreateEntity();
            if (node.Attribute("id") is { } idText)
            {
                if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int localId))
                {
                    Log.Warn("Scene", $"{node.Origin}:{node.Line}: the entity id '{idText}' is not a number: nothing can refer to it");
                }
                else if (!byLocal.TryAdd(localId, entity))
                {
                    Log.Warn("Scene", $"{node.Origin}:{node.Line}: the entity id {localId} is used twice: references to it go to the later one");
                    byLocal[localId] = entity;
                }
            }

            created.Add(entity);
            nodes.Add(node);
            under.Add(parent);
            CreateSceneEntities(node, entity, created, nodes, under, byLocal);
        }
    }

    private void TrySetName(Entity entity, string name)
    {
        Entity parent = GetParent(entity);
        if (!IsNameFree(name, parent))
        {
            // The scene is loaded into a world that has an entity of that name there: the new one gets a free name.
            for (int n = 2; ; n++)
            {
                string candidate = $"{name} {n}";
                if (IsNameFree(candidate, parent))
                {
                    name = candidate;
                    break;
                }
            }
        }

        SetName(entity, name);
    }

    private void DefineSceneComponents(XmlNode schemas)
    {
        foreach (XmlNode schema in schemas.Children)
        {
            if (schema.Name != "Component" || schema.Attribute("name") is not { Length: > 0 } name)
            {
                Log.Warn("Scene", $"{schema.Origin}:{schema.Line}: <{schema.Name}> in <Components> is not a <Component name=\"…\">: skipped");
                continue;
            }

            if (!_componentsByName.ContainsKey(name) && TypeRegistry.FindComponent(name) is not null)
            {
                continue; // a struct of the engine or the game: its layout is the code's
            }

            var fields = new List<(string Name, FieldKind Kind)>();
            Dictionary<string, string>? assetTypes = null;
            foreach (XmlNode field in schema.Children)
            {
                string? fieldName = field.Attribute("name");
                if (field.Name != "Field" || fieldName is null || !Enum.TryParse(field.Attribute("kind"), out FieldKind kind) || !Enum.IsDefined(kind))
                {
                    Log.Warn("Scene", $"{field.Origin}:{field.Line}: component {name} has a field the engine does not know: skipped");
                    continue;
                }

                fields.Add((fieldName, kind));
                if (kind == FieldKind.AssetRef && field.Attribute("assetType") is { Length: > 0 } assetType)
                {
                    (assetTypes ??= new Dictionary<string, string>(StringComparer.Ordinal))[fieldName] = assetType;
                }
            }

            try
            {
                DefineComponent(name, fields, assetTypes: assetTypes);
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
            {
                Log.Warn("Scene", $"{schema.Origin}:{schema.Line}: component {name} in the scene does not match the one in this world: {e.Message}");
            }
        }
    }

    private void ReadComponent(Entity entity, XmlNode node, Dictionary<int, Entity> byLocal)
    {
        string name = node.Name;
        Entity id = FindComponent(name);
        if (id.IsNull && TypeRegistry.FindComponent(name) is { } known)
        {
            id = RegisterComponent(known);
        }

        if (id.IsNull || !_infoById.TryGetValue(id, out ComponentInfo? info))
        {
            Log.Warn("Scene", $"{node.Origin}:{node.Line}: the scene has a component '{name}' that this world does not have: skipped");
            return;
        }

        if (info.IsTag)
        {
            Add(entity, id);
            return;
        }

        // A field the element does not name stays zero.
        byte[] data = new byte[info.Size];
        fixed (byte* bytes = data)
        {
            foreach (XmlProperty attribute in node.Attributes)
            {
                FieldDef? def = info.FindField(attribute.Name);
                if (def is null)
                {
                    Log.Warn("Scene", $"{node.Origin}:{node.Line}: component {name} has no field '{attribute.Name}': skipped");
                    continue;
                }

                try
                {
                    ReadField(info, def, attribute.Value, bytes + def.Offset, byLocal);
                }
                catch (Exception e) when (e is FormatException or InvalidOperationException)
                {
                    Log.Warn("Scene", $"{node.Origin}:{node.Line}: {name}.{attribute.Name} in the scene is not a {def.Kind}: {e.Message}");
                }
            }

            SetPointer(entity, id, info.Size, bytes);
        }
    }

    private void ReadField(ComponentInfo info, FieldDef field, string text, byte* at, Dictionary<int, Entity> byLocal)
    {
        switch (field.Kind)
        {
            case FieldKind.Entity:
                // The number of an entity of the file; one that is not there is no reference (the same as the attribute left out).
                *(ulong*)at = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int local) && byLocal.TryGetValue(local, out Entity target) ? target.Id : 0;
                break;
            case FieldKind.AssetRef:
                // A path alone is a reference too, which is how a scene written before UIDs holds a model.
                *(uint*)at = HandleOf(info, field, text);
                break;
            default:
                FieldText.Parse(field, text, at);
                break;
        }
    }
}
