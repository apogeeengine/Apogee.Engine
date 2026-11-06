using System;
using System.Linq;
using System.Numerics;
using System.Text;
using Apogee.Interop;
using Apogee.Native.CImGui;

namespace Apogee.DevTools;

/// <summary>The few Dear ImGui calls the dev tools use, with C# strings and <c>ref</c> values instead of C pointers. Call them between BeginFrame and EndFrame.</summary>
public static unsafe class Ui
{
    private static ImVec2 V(Vector2 v) => new() { X = v.X, Y = v.Y };

    public static bool Begin(string title, ref bool open, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        CBool isOpen = open;
        fixed (byte* name = Utf8.ToZeroTerminated(title))
        {
            bool visible = ImGui.Begin(name, &isOpen, flags);
            open = isOpen;
            return visible;
        }
    }

    /// <summary>Begins a window without a close button.</summary>
    public static bool Begin(string title, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        fixed (byte* name = Utf8.ToZeroTerminated(title))
        {
            return ImGui.Begin(name, null, flags);
        }
    }

    public static void End() => ImGui.End();

    public static void SetNextWindow(Vector2 position, Vector2 size, ImGuiCond condition = ImGuiCond.FirstUseEver)
    {
        ImGui.SetNextWindowPos(V(position), condition, default);
        ImGui.SetNextWindowSize(V(size), condition);
    }

    public static void Text(string text)
    {
        fixed (byte* bytes = Utf8.ToZeroTerminated(text))
        {
            ImGui.TextUnformatted(bytes, null);
        }
    }

    /// <summary>Text in a color (red for errors).</summary>
    public static void TextColored(Vector4 color, string text)
    {
        ImGui.PushStyleColorVec4(ImGuiCol.Text, new ImVec4 { X = color.X, Y = color.Y, Z = color.Z, W = color.W });
        Text(text);
        ImGui.PopStyleColor(1);
    }

    public static bool Checkbox(string label, ref bool value)
    {
        CBool v = value;
        fixed (byte* text = Utf8.ToZeroTerminated(label))
        {
            bool changed = ImGui.Checkbox(text, &v);
            value = v;
            return changed;
        }
    }

    public static bool SliderFloat(string label, ref float value, float min, float max, string format = "%.2f")
    {
        float v = value;
        fixed (byte* text = Utf8.ToZeroTerminated(label))
        fixed (byte* formatText = Utf8.ToZeroTerminated(format))
        {
            bool changed = ImGui.SliderFloat(text, &v, min, max, formatText, 0);
            value = v;
            return changed;
        }
    }

    public static bool Combo(string label, ref int current, params string[] items)
    {
        // ImGui wants the items as one string with a zero after each.
        byte[] joined = Encoding.UTF8.GetBytes(string.Concat(items.Select(i => i + "\0")) + "\0");
        int index = current;
        fixed (byte* text = Utf8.ToZeroTerminated(label))
        fixed (byte* itemBytes = joined)
        {
            bool changed = ImGui.ComboStr(text, &index, itemBytes, -1);
            current = index;
            return changed;
        }
    }

    public static void PlotLines(string label, ReadOnlySpan<float> values, int offset, string overlay, float min, float max, Vector2 size)
    {
        fixed (byte* text = Utf8.ToZeroTerminated(label))
        fixed (byte* overlayText = Utf8.ToZeroTerminated(overlay))
        fixed (float* data = values)
        {
            ImGui.PlotLinesFloatPtr(text, data, values.Length, offset, overlayText, min, max, V(size), sizeof(float));
        }
    }

    public static void ProgressBar(float fraction, Vector2 size, string? overlay = null)
    {
        fixed (byte* text = overlay is null ? null : Utf8.ToZeroTerminated(overlay))
        {
            ImGui.ProgressBar(fraction, V(size), text);
        }
    }

    /// <summary>A header that opens and closes its content; returns true while open. <paramref name="openByDefault"/> sets its first state.</summary>
    public static bool CollapsingHeader(string label, bool openByDefault = true)
    {
        fixed (byte* text = Utf8.ToZeroTerminated(label))
        {
            return ImGui.CollapsingHeaderTreeNodeFlags(text, openByDefault ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
        }
    }

    public static bool BeginTable(string id, int columns, ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp)
    {
        fixed (byte* text = Utf8.ToZeroTerminated(id))
        {
            return ImGui.BeginTable(text, columns, flags, default, 0f);
        }
    }

    public static void EndTable() => ImGui.EndTable();

    public static void TableHeader(params string[] columns)
    {
        foreach (string column in columns)
        {
            fixed (byte* text = Utf8.ToZeroTerminated(column))
            {
                ImGui.TableSetupColumn(text, ImGuiTableColumnFlags.None, 0f, 0);
            }
        }

        ImGui.TableHeadersRow();
    }

    /// <summary>Starts a row; follow with <see cref="Cell"/> for each column.</summary>
    public static void Row() => ImGui.TableNextRow(ImGuiTableRowFlags.None, 0f);

    public static void Cell(string text)
    {
        ImGui.TableNextColumn();
        Text(text);
    }

    public static void Separator() => ImGui.Separator();

    public static void SameLine() => ImGui.SameLine(0f, -1f);

    public static void Spacing() => ImGui.Spacing();
}
