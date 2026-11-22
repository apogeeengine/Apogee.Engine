using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Apogee.Content.FileSystem;
using Apogee.Core.Assets;
using Apogee.Ecs;
using Apogee.Editor.Assets;
using Apogee.Editor.Editing;
using Apogee.Host.Editing;
using Apogee.Reflection;

namespace Apogee.Editor.Inspector;

/// <summary>
/// The drawers the inspector starts with. They are public so that a drawer of your own can use one for part of a field, or fall back to it. Each follows the contract of <see cref="FieldDrawer"/>: it
/// draws the value in <see cref="FieldEdit.Bytes"/> through <see cref="FieldEdit.Ui"/> and, when the person changed it, writes the new value back into the same bytes and returns true.
/// </summary>
public static class BuiltInDrawers
{
    /// <summary>The numbers of a field through <see cref="FieldCodec"/>: a checkbox, a drag field or slider, three drag fields for a vector, three angles for a rotation. Also what an asset handle shows until a picker draws it.</summary>
    public static unsafe bool Numbers(in FieldEdit edit)
    {
        FieldDef field = edit.Field;
        InspectorMeta? meta = field.Meta;
        if (edit.Bytes.Length < field.Kind.SizeOf())
        {
            return false;
        }

        double[] values;
        fixed (byte* at = edit.Bytes)
        {
            values = FieldCodec.Read(at, field.Kind);
        }

        bool changed;
        switch (FieldCodec.WidgetOf(field.Kind))
        {
            case FieldWidget.Toggle:
                bool on = values[0] != 0;
                changed = edit.Ui.Toggle(edit.Label, ref on, edit.Mixed);
                values[0] = on ? 1 : 0;
                break;
            case FieldWidget.WholeNumber:
                changed = edit.Ui.Drag(edit.Label, values, meta is { Step: > 0 } ? meta.Step : 1, meta?.Min ?? double.NaN, meta?.Max ?? double.NaN, "%.0f", edit.Mixed);
                break;
            case FieldWidget.Rotation:
                changed = edit.Ui.Drag(edit.Label, values, meta is { Step: > 0 } ? meta.Step : 0.5, double.NaN, double.NaN, "%.1f deg", edit.Mixed);
                break;
            default:
                changed = edit.Ui.Drag(edit.Label, values, meta is { Step: > 0 } ? meta.Step : 0.05, meta?.Min ?? double.NaN, meta?.Max ?? double.NaN, "%.3f", edit.Mixed);
                break;
        }

        if (changed)
        {
            fixed (byte* at = edit.Bytes)
            {
                FieldCodec.Write(at, field.Kind, values);
            }
        }

        return changed;
    }

    /// <summary>A reference to another entity: its name on a button that accepts a dropped entity, and <i>Clear</i>.</summary>
    public static bool EntityReference(in FieldEdit edit)
    {
        if (edit.Bytes.Length < sizeof(ulong))
        {
            return false;
        }

        ulong id = MemoryMarshal.Read<ulong>(edit.Bytes);
        World world = edit.Project.World;
        var target = new Entity(id);
        string text = edit.Mixed ? "---" : id != 0 && world.IsAlive(target) ? world.GetName(target) ?? $"#{id & 0xFFFFFFFF}" : "None";
        if (!edit.Ui.EntityReference(edit.Label, text, ref id, edit.Mixed))
        {
            return false;
        }

        MemoryMarshal.Write(edit.Bytes, in id);
        return true;
    }

    /// <summary>
    /// An integer field that names an enum (<see cref="FieldDef.Enum"/>): a combo of the enum's members, or for a flags enum a node of checkboxes, one for each member that has a bit. A value that is no
    /// member shows as its number (and a flags value keeps the bits no member names). A field whose enum is not registered falls back to <see cref="Numbers"/>.
    /// </summary>
    public static bool Enum(in FieldEdit edit)
    {
        FieldKind kind = edit.Field.Kind;
        if (edit.Field.Enum is not { } name || TypeRegistry.FindEnum(name) is not { } info || !IsInteger(kind) || edit.Bytes.Length < kind.SizeOf())
        {
            return Numbers(in edit);
        }

        long value = ReadInteger(edit.Bytes, kind);
        if (info.IsFlags)
        {
            string caption = edit.Mixed ? "---" : info.GetName(value);
            if (!edit.Ui.BeginNode(edit.Field.Name, $"{edit.Label}: {caption}"))
            {
                return false;
            }

            bool changedFlags = false;
            foreach (EnumMemberInfo member in info.Members)
            {
                if (member.Value == 0)
                {
                    continue;
                }

                bool on = (value & member.Value) == member.Value;
                if (edit.Ui.Toggle(member.Name, ref on, edit.Mixed))
                {
                    value = on ? value | member.Value : value & ~member.Value;
                    changedFlags = true;
                }
            }

            edit.Ui.EndNode();
            if (changedFlags)
            {
                WriteInteger(edit.Bytes, kind, value);
            }

            return changedFlags;
        }

        IReadOnlyList<string> names = [.. info.Members.Select(m => m.Name)];
        int selected = -1;
        for (int i = 0; i < info.Members.Count && !edit.Mixed; i++)
        {
            if (info.Members[i].Value == value)
            {
                selected = i;
                break;
            }
        }

        int picked = edit.Ui.Combo(edit.Label, edit.Mixed ? "---" : info.GetName(value), names, selected);
        if (picked < 0 || picked >= info.Members.Count || (picked == selected && !edit.Mixed))
        {
            return false;
        }

        WriteInteger(edit.Bytes, kind, info.Members[picked].Value);
        return true;
    }

    /// <summary>A <c>Vector3</c> the registry marks as a colour (<see cref="InspectorMeta.Color"/>): a colour picker that keeps values outside 0 to 1 (a light can be brighter than white).</summary>
    public static bool Color(in FieldEdit edit)
    {
        if (edit.Bytes.Length < 3 * sizeof(float))
        {
            return false;
        }

        Span<float> rgb = stackalloc float[3];
        for (int i = 0; i < 3; i++)
        {
            rgb[i] = MemoryMarshal.Read<float>(edit.Bytes[(i * sizeof(float))..]);
        }

        if (!edit.Ui.Color(edit.Label, rgb, edit.Mixed))
        {
            return false;
        }

        for (int i = 0; i < 3; i++)
        {
            float channel = rgb[i];
            MemoryMarshal.Write(edit.Bytes[(i * sizeof(float))..], in channel);
        }

        return true;
    }

    /// <summary>
    /// A reference to an asset (a <see cref="FieldKind.AssetRef"/> field with an asset type): the file's name on a button that opens a picker over the game's files of that type, takes a file dragged from the
    /// asset browser, and has <i>Clear</i> and <i>Show in assets</i>. A choice becomes the handle of this run through <see cref="World.AssetHandleOf"/>, so what the field holds is what a scene would
    /// load; a file that gives no handle (it does not load) is said so under the field and the field stays as it was, never 0 by accident. A reference that is broken (the scene named a file that is not there,
    /// or the file was deleted) is shown in red with its text.
    /// </summary>
    public static bool AssetReference(in FieldEdit edit)
    {
        if (edit.Field.AssetType is not { } type || edit.Bytes.Length < sizeof(uint))
        {
            return Numbers(in edit);
        }

        AssetCatalog catalog = edit.Project.Catalog;
        World world = edit.Project.World;
        string key = $"{edit.Component.Name}.{edit.Field.Name}";
        uint handle = MemoryMarshal.Read<uint>(edit.Bytes);
        AssetFieldView view = ViewOf(in edit, type, handle, catalog, world, key);
        AssetFieldResult result = edit.Ui.AssetReference(edit.Label, in view, catalog.PickerFor(type));
        switch (result.Action)
        {
            case AssetFieldAction.Show:
                if (Shown(in edit, type, handle, catalog, world) is { } path)
                {
                    catalog.Reveal(path);
                }

                return false;

            case AssetFieldAction.Clear:
                catalog.ClearNotice(key);
                MemoryMarshal.Write(edit.Bytes, 0u);
                return true;

            case AssetFieldAction.Pick:
            case AssetFieldAction.Drop:
                return Choose(in edit, type, result.Path!, catalog, world, key);

            default:
                return false;
        }
    }

    // Turns the file the person chose into the handle of this run and writes it. False, with a notice under the field, when it cannot be used.
    private static bool Choose(in FieldEdit edit, string type, string path, AssetCatalog catalog, World world, string key)
    {
        string label = AssetKinds.LabelOf(type).ToLowerInvariant();
        if (AssetKinds.TypeOfPath(path) != type)
        {
            catalog.PostNotice(key, $"{VfsPath.FileName(path)} is not a {label}.");
            return false;
        }

        AssetRef reference = catalog.RefOf(path);
        uint handle = world.AssetHandleOf(type, reference);
        if (handle == 0)
        {
            // The converter found no file or could not load it (it has said why in the console): 0 would pass for "cleared".
            catalog.PostNotice(key, $"{path} could not be used as a {label}: it is not there or does not load (the console says why). The field was left as it was.");
            return false;
        }

        catalog.ClearNotice(key);
        if (MemoryMarshal.Read<uint>(edit.Bytes) == handle)
        {
            return false;
        }

        MemoryMarshal.Write(edit.Bytes, in handle);
        return true;
    }

    private static AssetFieldView ViewOf(in FieldEdit edit, string type, uint handle, AssetCatalog catalog, World world, string key)
    {
        string? problem = catalog.NoticeFor(key);
        if (edit.Mixed)
        {
            return new AssetFieldView("---", "The selected entities have different values.", false, true, false, problem);
        }

        if (handle == 0)
        {
            // A scene that named something that did not load leaves the field at 0 and remembers the text: it is shown, and a save would write it again.
            if (edit.Entity.Id != 0 && world.UnresolvedAssetOf(edit.Entity, edit.Component.Name, edit.Field.Name) is { Length: > 0 } kept)
            {
                return new AssetFieldView(kept, $"{kept}\nThis could not be loaded, so the field is empty. The scene keeps the text and loads it again when the file is back; choose another file or Clear to replace it.", true, false, false, problem);
            }

            return new AssetFieldView("None", null, false, false, false, problem);
        }

        if (world.AssetRefOf(type, handle) is not { } reference)
        {
            return new AssetFieldView($"#{handle}", $"Handle {handle} names no file (it was made in code), so a scene cannot save it.", true, false, false, problem);
        }

        ReferenceStatus status = catalog.StatusOf(reference);
        string where = status.Path ?? reference.Path;
        string uid = reference.Uid.IsNone ? "no uid (not imported)" : reference.Uid.ToString();
        switch (status.State)
        {
            case ReferenceState.Found:
                return new AssetFieldView(VfsPath.FileName(where), $"{where}\n{uid}", false, false, true, problem);
            case ReferenceState.Moved:
                return new AssetFieldView(VfsPath.FileName(where), $"{where}\n{uid}\nMoved from {reference.Path}: save the scene to write the new path.", false, false, true, problem);
            default:
                return new AssetFieldView(reference.ToString(), $"{reference}\nThere is no file at this path, and no file has this uid.", true, false, false, problem);
        }
    }

    // The file Show in assets goes to: the one the field names (or where its uid finds it).
    private static string? Shown(in FieldEdit edit, string type, uint handle, AssetCatalog catalog, World world) =>
        handle != 0 && world.AssetRefOf(type, handle) is { } reference ? catalog.StatusOf(reference).Path : null;

    internal static bool IsInteger(FieldKind kind) => kind is FieldKind.Int8 or FieldKind.UInt8 or FieldKind.Int16 or FieldKind.UInt16 or FieldKind.Int32 or FieldKind.UInt32 or FieldKind.Int64 or FieldKind.UInt64;

    /// <summary>The integer in the field's bytes, sign-extended for a signed kind (a <c>ulong</c> above the range of a <c>long</c> wraps, as an enum's value does).</summary>
    internal static long ReadInteger(ReadOnlySpan<byte> bytes, FieldKind kind) => kind switch
    {
        FieldKind.Int8 => (sbyte)bytes[0],
        FieldKind.UInt8 => bytes[0],
        FieldKind.Int16 => MemoryMarshal.Read<short>(bytes),
        FieldKind.UInt16 => MemoryMarshal.Read<ushort>(bytes),
        FieldKind.Int32 => MemoryMarshal.Read<int>(bytes),
        FieldKind.UInt32 => MemoryMarshal.Read<uint>(bytes),
        FieldKind.Int64 => MemoryMarshal.Read<long>(bytes),
        FieldKind.UInt64 => unchecked((long)MemoryMarshal.Read<ulong>(bytes)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not an integer kind"),
    };

    /// <summary>Writes the integer into the field's bytes, truncated to the kind's size.</summary>
    internal static void WriteInteger(Span<byte> bytes, FieldKind kind, long value)
    {
        switch (kind)
        {
            case FieldKind.Int8:
            case FieldKind.UInt8:
                bytes[0] = unchecked((byte)value);
                break;
            case FieldKind.Int16:
            case FieldKind.UInt16:
                ushort narrow = unchecked((ushort)value);
                MemoryMarshal.Write(bytes, in narrow);
                break;
            case FieldKind.Int32:
            case FieldKind.UInt32:
                uint word = unchecked((uint)value);
                MemoryMarshal.Write(bytes, in word);
                break;
            case FieldKind.Int64:
            case FieldKind.UInt64:
                ulong wide = unchecked((ulong)value);
                MemoryMarshal.Write(bytes, in wide);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "not an integer kind");
        }
    }
}
