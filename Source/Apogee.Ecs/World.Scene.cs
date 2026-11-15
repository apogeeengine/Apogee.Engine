using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Apogee.Content;
using Apogee.Core.Assets;
using Apogee.Core.Logging;
using Apogee.Core.Numerics;
using Apogee.Reflection;

namespace Apogee.Ecs;

// Scenes: the user entities of a world as JSON text (a `.ascene` file). Written and read field by field from the registry's descriptions with Utf8JsonWriter and JsonDocument, so no reflection is
// involved and it works in a NativeAOT program. The format is ours, not Flecs': Flecs' own JSON would carry the entities of the types and systems the engine registers.
//
//   { "format": "apogee-scene", "version": 1,
//     "components": { "Ticker": [ { "name": "count", "kind": "Int32" } ] },             // only the ones a script defined: the file defines them again when it is loaded
//     "entities": [ { "id": 1, "name": "ground", "parent": null,
//                     "components": { "Transform": { "Position": [0, -0.5, 0], "Rotation": [0, 0, 0, 1], "Scale": [1, 1, 1] }, "MainCamera": {} } } ] }
//
// "id" is the entity's number in the file (what "parent" and Entity-typed fields refer to), not its id in a world.
public sealed unsafe partial class World
{
    private const string SceneFormat = "apogee-scene";
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

    /// <summary>The user entities (<see cref="IsUserEntity"/>) as scene text.</summary>
    [Method]
    public string SaveScene() => WriteScene(Roots());

    /// <summary>One entity and everything below it as scene text: a prefab, a copy for the clipboard.</summary>
    public string SaveScene(Entity root) => WriteScene([root]);

    private string WriteScene(IReadOnlyList<Entity> roots)
    {
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
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("format", SceneFormat);
            json.WriteNumber("version", SceneVersion);

            // The entities first into a buffer of their own, so the schemas of the components they use can be written before them.
            var entitiesBuffer = new ArrayBufferWriter<byte>(4096);
            using (var entities = new Utf8JsonWriter(entitiesBuffer, new JsonWriterOptions { Indented = true }))
            {
                entities.WriteStartArray();
                foreach (Entity entity in order)
                {
                    entities.WriteStartObject();
                    entities.WriteNumber("id", local[entity.Id]);
                    if (GetName(entity) is { } name)
                    {
                        entities.WriteString("name", name);
                    }

                    Entity parent = GetParent(entity);
                    if (local.TryGetValue(parent.Id, out int parentLocal))
                    {
                        entities.WriteNumber("parent", parentLocal);
                    }

                    entities.WriteStartObject("components");
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

                        entities.WriteStartObject(info.Name);
                        if (!info.IsTag)
                        {
                            byte* data = GetPointer(entity, id);
                            foreach (FieldDef field in info.Fields)
                            {
                                entities.WritePropertyName(field.Name);
                                WriteField(entities, info, field, data + field.Offset, local);
                            }
                        }

                        entities.WriteEndObject();
                    }

                    entities.WriteEndObject();
                    entities.WriteEndObject();
                }

                entities.WriteEndArray();
            }

            json.WriteStartObject("components");
            foreach ((string name, ComponentInfo info) in scriptComponents)
            {
                json.WriteStartArray(name);
                foreach (FieldDef field in info.Fields)
                {
                    json.WriteStartObject();
                    json.WriteString("name", field.Name);
                    json.WriteString("kind", field.Kind.ToString());
                    json.WriteEndObject();
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
            json.WritePropertyName("entities");
            json.WriteRawValue(entitiesBuffer.WrittenSpan, skipInputValidation: true);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private void WriteField(Utf8JsonWriter json, ComponentInfo info, FieldDef field, byte* at, Dictionary<ulong, int> local)
    {
        switch (field.Kind)
        {
            case FieldKind.Bool: json.WriteBooleanValue(*at != 0); break;
            case FieldKind.Int8: json.WriteNumberValue(*(sbyte*)at); break;
            case FieldKind.UInt8: json.WriteNumberValue(*at); break;
            case FieldKind.Int16: json.WriteNumberValue(*(short*)at); break;
            case FieldKind.UInt16: json.WriteNumberValue(*(ushort*)at); break;
            case FieldKind.Int32: json.WriteNumberValue(*(int*)at); break;
            case FieldKind.UInt32: json.WriteNumberValue(*(uint*)at); break;
            case FieldKind.AssetRef:
                // The handle means nothing in another run: what it names goes instead, as "path|uid" (null for none, and for what cannot be saved).
                if (AssetRefOf(info, field, *(uint*)at) is { } named)
                {
                    json.WriteStringValue(named.ToString());
                }
                else
                {
                    json.WriteNullValue();
                }

                break;
            case FieldKind.Int64: WriteLong(json, *(long*)at); break;
            case FieldKind.UInt64: WriteULong(json, *(ulong*)at); break;
            case FieldKind.Float32: WriteDouble(json, *(float*)at); break;
            case FieldKind.Float64: WriteDouble(json, *(double*)at); break;
            case FieldKind.Vector3: WriteFloats(json, (float*)at, 3); break;
            case FieldKind.Quaternion: WriteFloats(json, (float*)at, 4); break;
            case FieldKind.Double3:
                json.WriteStartArray();
                for (int i = 0; i < 3; i++)
                {
                    WriteDouble(json, ((double*)at)[i]);
                }

                json.WriteEndArray();
                break;
            case FieldKind.Entity:
                // A reference to another entity of the scene is its number in the file; to anything else, nothing.
                if (local.TryGetValue(*(ulong*)at, out int target))
                {
                    json.WriteNumberValue(target);
                }
                else
                {
                    json.WriteNullValue();
                }

                break;
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

    private static void WriteDouble(Utf8JsonWriter json, double value)
    {
        if (double.IsFinite(value))
        {
            json.WriteNumberValue(value);
        }
        else
        {
            json.WriteStringValue(double.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity");
        }
    }

    private static void WriteLong(Utf8JsonWriter json, long value)
    {
        // A number in JSON is a double to most readers: what it cannot hold exactly goes as text.
        if (value is >= -9007199254740992 and <= 9007199254740992)
        {
            json.WriteNumberValue(value);
        }
        else
        {
            json.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void WriteULong(Utf8JsonWriter json, ulong value)
    {
        if (value <= 9007199254740992)
        {
            json.WriteNumberValue(value);
        }
        else
        {
            json.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void WriteFloats(Utf8JsonWriter json, float* values, int count)
    {
        json.WriteStartArray();
        for (int i = 0; i < count; i++)
        {
            WriteDouble(json, values[i]);
        }

        json.WriteEndArray();
    }

    /// <summary>
    /// Creates the entities of scene text, under <paramref name="parent"/> when given, and returns them in the order of the file (parents first). A component the file names that this world cannot
    /// find, and a field it does not have, are logged and skipped; components that scripts defined are defined again from the file. Throws <see cref="FormatException"/> when the text is not a scene.
    /// </summary>
    [Method]
    public List<Entity> LoadScene(string text, Entity parent = default)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException e)
        {
            throw new FormatException($"the scene is not valid JSON: {e.Message}", e);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out JsonElement format) || format.GetString() != SceneFormat)
            {
                throw new FormatException($"the text is not an {SceneFormat} scene");
            }

            if (root.TryGetProperty("version", out JsonElement version) && version.TryGetInt32(out int number) && number > SceneVersion)
            {
                throw new FormatException($"the scene is version {number}; this engine reads up to {SceneVersion}");
            }

            if (root.TryGetProperty("components", out JsonElement schemas) && schemas.ValueKind == JsonValueKind.Object)
            {
                DefineSceneComponents(schemas);
            }

            var created = new List<Entity>();
            if (!root.TryGetProperty("entities", out JsonElement entities) || entities.ValueKind != JsonValueKind.Array)
            {
                return created;
            }

            // Every entity first, so that parents and entity-typed fields can refer to the ones that come later in the file.
            var byLocal = new Dictionary<int, Entity>();
            foreach (JsonElement entry in entities.EnumerateArray())
            {
                Entity entity = CreateEntity();
                if (entry.TryGetProperty("id", out JsonElement id) && id.TryGetInt32(out int localId))
                {
                    byLocal[localId] = entity;
                }

                created.Add(entity);
            }

            int index = 0;
            foreach (JsonElement entry in entities.EnumerateArray())
            {
                Entity entity = created[index++];
                Entity under = parent;
                if (entry.TryGetProperty("parent", out JsonElement parentEntry) && parentEntry.TryGetInt32(out int parentLocal) && byLocal.TryGetValue(parentLocal, out Entity found))
                {
                    under = found;
                }

                if (!under.IsNull)
                {
                    SetParent(entity, under);
                }
            }

            index = 0;
            foreach (JsonElement entry in entities.EnumerateArray())
            {
                Entity entity = created[index++];
                if (entry.TryGetProperty("name", out JsonElement name) && name.GetString() is { Length: > 0 } entityName)
                {
                    TrySetName(entity, entityName);
                }

                if (entry.TryGetProperty("components", out JsonElement components) && components.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty component in components.EnumerateObject())
                    {
                        ReadComponent(entity, component.Name, component.Value, byLocal);
                    }
                }
            }

            return created;
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

    private void DefineSceneComponents(JsonElement schemas)
    {
        foreach (JsonProperty schema in schemas.EnumerateObject())
        {
            if (!_componentsByName.ContainsKey(schema.Name) && TypeRegistry.FindComponent(schema.Name) is not null)
            {
                continue; // a struct of the engine or the game: its layout is the code's
            }

            var fields = new List<(string Name, FieldKind Kind)>();
            foreach (JsonElement field in schema.Value.EnumerateArray())
            {
                string? fieldName = field.GetProperty("name").GetString();
                if (fieldName is null || !Enum.TryParse(field.GetProperty("kind").GetString(), out FieldKind kind))
                {
                    Log.Warn("Scene", $"component {schema.Name} has a field the engine does not know: {field}");
                    continue;
                }

                fields.Add((fieldName, kind));
            }

            try
            {
                DefineComponent(schema.Name, fields);
            }
            catch (InvalidOperationException e)
            {
                Log.Warn("Scene", $"component {schema.Name} in the scene does not match the one in this world: {e.Message}");
            }
        }
    }

    private void ReadComponent(Entity entity, string name, JsonElement value, Dictionary<int, Entity> byLocal)
    {
        Entity id = FindComponent(name);
        if (id.IsNull && TypeRegistry.FindComponent(name) is { } known)
        {
            id = RegisterComponent(known);
        }

        if (id.IsNull || !_infoById.TryGetValue(id, out ComponentInfo? info))
        {
            Log.Warn("Scene", $"the scene has a component '{name}' that this world does not have: skipped");
            return;
        }

        if (info.IsTag)
        {
            Add(entity, id);
            return;
        }

        byte[] data = new byte[info.Size];
        fixed (byte* bytes = data)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty field in value.EnumerateObject())
                {
                    FieldDef? def = info.FindField(field.Name);
                    if (def is null)
                    {
                        Log.Warn("Scene", $"component {name} has no field '{field.Name}': skipped");
                        continue;
                    }

                    try
                    {
                        ReadField(info, def, field.Value, bytes + def.Offset, byLocal);
                    }
                    catch (Exception e) when (e is FormatException or InvalidOperationException or OverflowException or KeyNotFoundException)
                    {
                        Log.Warn("Scene", $"{name}.{field.Name} in the scene is not a {def.Kind}: {e.Message}");
                    }
                }
            }

            SetPointer(entity, id, info.Size, bytes);
        }
    }

    private void ReadField(ComponentInfo info, FieldDef field, JsonElement value, byte* at, Dictionary<int, Entity> byLocal)
    {
        switch (field.Kind)
        {
            case FieldKind.Bool: *at = (byte)(value.ValueKind == JsonValueKind.True ? 1 : 0); break;
            case FieldKind.Int8: *(sbyte*)at = value.GetSByte(); break;
            case FieldKind.UInt8: *at = value.GetByte(); break;
            case FieldKind.Int16: *(short*)at = value.GetInt16(); break;
            case FieldKind.UInt16: *(ushort*)at = value.GetUInt16(); break;
            case FieldKind.Int32: *(int*)at = value.GetInt32(); break;
            case FieldKind.UInt32: *(uint*)at = value.GetUInt32(); break;
            case FieldKind.AssetRef:
                // null, or any other JSON than a string, is no reference. A path alone is one too, which is how scenes before UIDs hold a model.
                *(uint*)at = value.ValueKind == JsonValueKind.String ? HandleOf(info, field, value.GetString()!) : 0;
                break;
            case FieldKind.Int64: *(long*)at = value.ValueKind == JsonValueKind.String ? long.Parse(value.GetString()!, CultureInfo.InvariantCulture) : value.GetInt64(); break;
            case FieldKind.UInt64: *(ulong*)at = value.ValueKind == JsonValueKind.String ? ulong.Parse(value.GetString()!, CultureInfo.InvariantCulture) : value.GetUInt64(); break;
            case FieldKind.Float32: *(float*)at = (float)ReadDouble(value); break;
            case FieldKind.Float64: *(double*)at = ReadDouble(value); break;
            case FieldKind.Vector3: ReadFloats(value, (float*)at, 3); break;
            case FieldKind.Quaternion: ReadFloats(value, (float*)at, 4); break;
            case FieldKind.Double3:
                if (value.GetArrayLength() != 3)
                {
                    throw new FormatException("expected 3 numbers");
                }

                int i = 0;
                foreach (JsonElement item in value.EnumerateArray())
                {
                    ((double*)at)[i++] = ReadDouble(item);
                }

                break;
            case FieldKind.Entity:
                *(ulong*)at = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int local) && byLocal.TryGetValue(local, out Entity target) ? target.Id : 0;
                break;
        }
    }

    private static double ReadDouble(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString() switch
        {
            "NaN" => double.NaN,
            "Infinity" => double.PositiveInfinity,
            "-Infinity" => double.NegativeInfinity,
            { } other => throw new FormatException($"'{other}' is not a number"),
            _ => throw new FormatException("expected a number"),
        }
        : value.GetDouble();

    private static void ReadFloats(JsonElement value, float* into, int count)
    {
        if (value.GetArrayLength() != count)
        {
            throw new FormatException($"expected {count} numbers");
        }

        int i = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            into[i++] = (float)ReadDouble(item);
        }
    }
}
