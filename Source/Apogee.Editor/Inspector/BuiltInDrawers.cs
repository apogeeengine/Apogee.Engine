using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Apogee.Core.Logging;
using Apogee.Ecs;
using Apogee.Editor.Editing;
using Apogee.Reflection;

namespace Apogee.Editor.Inspector;

/// <summary>
/// The drawers the inspector starts with. They are public so that a drawer of your own can use one for part of a field, or fall back to it. Each follows the contract of <see cref="FieldDrawer"/>: it
/// draws the value in <see cref="FieldEdit.Bytes"/> through <see cref="FieldEdit.Ui"/> and, when the person changed it, writes the new value back into the same bytes and returns true.
/// </summary>
public static class BuiltInDrawers
{
    private static readonly string[] s_modelExtensions = [".glb", ".gltf"];

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

    /// <summary>The path of a model for <c>ModelRenderer.Model</c>: type a path (Enter loads it) or drop a model file; an empty path clears it. A file that is not there is logged and the field stays as it was.</summary>
    public static bool ModelPath(in FieldEdit edit)
    {
        if (edit.Bytes.Length < sizeof(uint))
        {
            return false;
        }

        uint model = MemoryMarshal.Read<uint>(edit.Bytes);
        string path = edit.Project.Session.Models.PathOf(model) ?? "";
        if (!edit.Ui.PathInput(edit.Label, ref path, s_modelExtensions))
        {
            return false;
        }

        try
        {
            uint loaded = path.Length == 0 ? 0 : edit.Project.Session.Models.Load(path);
            MemoryMarshal.Write(edit.Bytes, in loaded);
            return true;
        }
        catch (System.IO.FileNotFoundException e)
        {
            Log.Warn("Editor", e.Message);
            return false;
        }
    }

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
