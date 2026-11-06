using System;
using System.Linq;
using System.Text;
using Apogee.Interop;
using Apogee.Native.CImGui;
using Apogee.Reflection;

namespace Apogee.DevTools;

/// <summary>Whether a Dear ImGui frame is open: the Luau <c>imgui</c> calls only do something between <see cref="ImGuiHost.BeginFrame(float)"/> and <see cref="ImGuiHost.EndFrame"/>.</summary>
internal static class ImGuiState
{
    public static bool FrameOpen { get; set; }
}

/// <summary>
/// Dear ImGui for Luau scripts, as the global table <c>imgui</c>: snake_case functions over the same calls the dev tools use. A widget returns
/// <c>changed, value</c>; a window or tree that can be open returns whether it is; every begin has its end (ImGui recovers from a script that forgets,
/// at the end of the frame). With no frame open every call does nothing and returns what it was given, so tool code can stay in a shipped game.
/// Ids are the labels, with ImGui's <c>##</c> and <c>###</c> conventions.
/// </summary>
[ScriptLibrary("imgui")]
public static unsafe partial class ImGuiLibrary
{
    private static ImVec2 Size(float x, float y) => new() { X = x, Y = y };

    private static ImVec4 Color(float[] color) => color.Length switch
    {
        3 => new ImVec4 { X = color[0], Y = color[1], Z = color[2], W = 1 },
        4 => new ImVec4 { X = color[0], Y = color[1], Z = color[2], W = color[3] },
        _ => throw new ValueException("a color is a table of 3 or 4 numbers (red, green, blue, alpha), from 0 to 1"),
    };

    // ---- Windows ---------------------------------------------------------------------------------------------------------------------

    /// <summary>Begins a window. Pass <c>true</c> for <c>open</c> to give it a close button; the second result is whether it is still open. Always call <c>end_window</c>.</summary>
    [Method]
    public static (bool Visible, bool Open) BeginWindow(string title, Value open = default, ImGuiWindowFlags flags = Apogee.Native.CImGui.ImGuiWindowFlags.None)
    {
        bool isOpen = open.Kind == ValueKind.Bool && open.ToBool();
        if (!ImGuiState.FrameOpen)
        {
            return (false, isOpen);
        }

        fixed (byte* name = Utf8.ToZeroTerminated(title))
        {
            if (open.Kind != ValueKind.Bool)
            {
                return (ImGui.Begin(name, null, flags), true);
            }

            CBool flag = isOpen;
            bool visible = ImGui.Begin(name, &flag, flags);
            return (visible, flag);
        }
    }

    [Method]
    public static void EndWindow()
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.End();
        }
    }

    [Method]
    public static void SetNextWindowPos(float x, float y, ImGuiCond condition = Apogee.Native.CImGui.ImGuiCond.FirstUseEver)
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.SetNextWindowPos(Size(x, y), condition, default);
        }
    }

    [Method]
    public static void SetNextWindowSize(float width, float height, ImGuiCond condition = Apogee.Native.CImGui.ImGuiCond.FirstUseEver)
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.SetNextWindowSize(Size(width, height), condition);
        }
    }

    /// <summary>A scrolling region inside a window. Always call <c>end_child</c>.</summary>
    [Method]
    public static bool BeginChild(string id, float width = 0, float height = 0)
    {
        if (!ImGuiState.FrameOpen)
        {
            return false;
        }

        fixed (byte* name = Utf8.ToZeroTerminated(id))
        {
            return ImGui.BeginChildStr(name, Size(width, height), ImGuiChildFlags.None, ImGuiWindowFlags.None);
        }
    }

    [Method]
    public static void EndChild()
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.EndChild();
        }
    }

    // ---- Text -------------------------------------------------------------------------------------------------------------------------

    [Method]
    public static void Text(string text)
    {
        if (ImGuiState.FrameOpen)
        {
            Ui.Text(text);
        }
    }

    /// <summary>Text in a color: a table of 3 or 4 numbers from 0 to 1.</summary>
    [Method]
    public static void TextColored(float[] color, string text)
    {
        if (!ImGuiState.FrameOpen)
        {
            return;
        }

        ImGui.PushStyleColorVec4(ImGuiCol.Text, Color(color));
        Ui.Text(text);
        ImGui.PopStyleColor(1);
    }

    [Method]
    public static void TextWrapped(string text)
    {
        if (!ImGuiState.FrameOpen)
        {
            return;
        }

        ImGui.PushTextWrapPos(0f);
        Ui.Text(text);
        ImGui.PopTextWrapPos();
    }

    [Method]
    public static void TextDisabled(string text)
    {
        if (!ImGuiState.FrameOpen)
        {
            return;
        }

        ImGui.BeginDisabled(true);
        Ui.Text(text);
        ImGui.EndDisabled();
    }

    // ---- Widgets ---------------------------------------------------------------------------------------------------------------------

    [Method]
    public static bool Button(string label)
    {
        if (!ImGuiState.FrameOpen)
        {
            return false;
        }

        fixed (byte* text = Utf8.ToZeroTerminated(label))
        {
            return ImGui.Button(text, default);
        }
    }

    [Method]
    public static (bool Changed, bool Value) Checkbox(string label, bool value)
    {
        bool changed = ImGuiState.FrameOpen && Ui.Checkbox(label, ref value);
        return (changed, value);
    }

    [Method]
    public static (bool Changed, float Value) SliderFloat(string label, float value, float min, float max)
    {
        bool changed = ImGuiState.FrameOpen && Ui.SliderFloat(label, ref value, min, max);
        return (changed, value);
    }

    [Method]
    public static (bool Changed, int Value) SliderInt(string label, int value, int min, int max)
    {
        if (!ImGuiState.FrameOpen)
        {
            return (false, value);
        }

        fixed (byte* text = Utf8.ToZeroTerminated(label))
        {
            int v = value;
            bool changed = ImGui.SliderInt(text, &v, min, max, null, ImGuiSliderFlags.None);
            return (changed, v);
        }
    }

    [Method]
    public static (bool Changed, float Value) DragFloat(string label, float value, float speed = 0.1f, float min = 0, float max = 0)
    {
        if (!ImGuiState.FrameOpen)
        {
            return (false, value);
        }

        fixed (byte* text = Utf8.ToZeroTerminated(label))
        {
            float v = value;
            bool changed = ImGui.DragFloat(text, &v, speed, min, max, null, ImGuiSliderFlags.None);
            return (changed, v);
        }
    }

    [Method]
    public static (bool Changed, System.Numerics.Vector3 Value) DragFloat3(string label, System.Numerics.Vector3 value, float speed = 0.1f)
    {
        if (!ImGuiState.FrameOpen)
        {
            return (false, value);
        }

        fixed (byte* text = Utf8.ToZeroTerminated(label))
        {
            float* v = stackalloc float[3] { value.X, value.Y, value.Z };
            bool changed = ImGui.DragFloat3(text, v, speed, 0, 0, null, ImGuiSliderFlags.None);
            return (changed, new System.Numerics.Vector3(v[0], v[1], v[2]));
        }
    }

    [Method]
    public static (bool Changed, int Value) InputInt(string label, int value)
    {
        if (!ImGuiState.FrameOpen)
        {
            return (false, value);
        }

        fixed (byte* text = Utf8.ToZeroTerminated(label))
        {
            int v = value;
            bool changed = ImGui.InputInt(text, &v, 1, 100, ImGuiInputTextFlags.None);
            return (changed, v);
        }
    }

    [Method]
    public static (bool Changed, float Value) InputFloat(string label, float value)
    {
        if (!ImGuiState.FrameOpen)
        {
            return (false, value);
        }

        fixed (byte* text = Utf8.ToZeroTerminated(label))
        {
            float v = value;
            bool changed = ImGui.InputFloat(text, &v, 0, 0, null, ImGuiInputTextFlags.None);
            return (changed, v);
        }
    }

    /// <summary>A text field of at most <c>max_length</c> bytes.</summary>
    [Method]
    public static (bool Changed, string Value) InputText(string label, string value, int maxLength = 256)
    {
        if (!ImGuiState.FrameOpen)
        {
            return (false, value);
        }

        byte[] buffer = new byte[Math.Max(maxLength, 2) + 1];
        byte[] current = Encoding.UTF8.GetBytes(value);
        Array.Copy(current, buffer, Math.Min(current.Length, buffer.Length - 1));
        fixed (byte* text = Utf8.ToZeroTerminated(label))
        fixed (byte* data = buffer)
        {
            bool changed = ImGui.InputText(text, data, (nuint)buffer.Length, ImGuiInputTextFlags.None, null, null);
            return (changed, Utf8.FromPointer(data) ?? "");
        }
    }

    /// <summary>A color editor: red, green, blue and alpha, from 0 to 1.</summary>
    [Method]
    public static (bool Changed, float[] Color) ColorEdit(string label, float[] color)
    {
        if (!ImGuiState.FrameOpen)
        {
            return (false, color);
        }

        ImVec4 value = Color(color);
        float* v = stackalloc float[4] { value.X, value.Y, value.Z, value.W };
        fixed (byte* text = Utf8.ToZeroTerminated(label))
        {
            bool changed = ImGui.ColorEdit4(text, v, ImGuiColorEditFlags.None);
            return (changed, color.Length == 3 ? [v[0], v[1], v[2]] : [v[0], v[1], v[2], v[3]]);
        }
    }

    /// <summary>A drop-down: <c>index</c> counts from 1, like a Luau list.</summary>
    [Method]
    public static (bool Changed, int Index) Combo(string label, int index, string[] items)
    {
        int zeroBased = index - 1;
        bool changed = ImGuiState.FrameOpen && Ui.Combo(label, ref zeroBased, items);
        return (changed, zeroBased + 1);
    }

    [Method]
    public static bool Selectable(string label, bool selected = false)
    {
        if (!ImGuiState.FrameOpen)
        {
            return false;
        }

        fixed (byte* text = Utf8.ToZeroTerminated(label))
        {
            return ImGui.SelectableBool(text, selected, ImGuiSelectableFlags.None, default);
        }
    }

    [Method]
    public static void ProgressBar(float fraction, float width = -1, float height = 0, string? overlay = null)
    {
        if (ImGuiState.FrameOpen)
        {
            Ui.ProgressBar(fraction, new System.Numerics.Vector2(width, height), overlay);
        }
    }

    [Method]
    public static void PlotLines(string label, float[] values, float min = 0, float max = 0, float height = 60)
    {
        if (ImGuiState.FrameOpen)
        {
            Ui.PlotLines(label, values, 0, "", min, max == 0 ? (values.Length == 0 ? 1 : values.Max()) : max, new System.Numerics.Vector2(0, height));
        }
    }

    // ---- Layout and ids -------------------------------------------------------------------------------------------------------------------

    [Method]
    public static void SameLine()
    {
        if (ImGuiState.FrameOpen)
        {
            Ui.SameLine();
        }
    }

    [Method]
    public static void Separator()
    {
        if (ImGuiState.FrameOpen)
        {
            Ui.Separator();
        }
    }

    [Method]
    public static void Spacing()
    {
        if (ImGuiState.FrameOpen)
        {
            Ui.Spacing();
        }
    }

    [Method]
    public static void Indent(float width = 0)
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.Indent(width);
        }
    }

    [Method]
    public static void Unindent(float width = 0)
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.Unindent(width);
        }
    }

    [Method]
    public static void Dummy(float width, float height)
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.Dummy(Size(width, height));
        }
    }

    [Method]
    public static void SetNextItemWidth(float width)
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.SetNextItemWidth(width);
        }
    }

    /// <summary>The room left in the window: width and height.</summary>
    [Method]
    public static (float Width, float Height) ContentRegionAvail()
    {
        if (!ImGuiState.FrameOpen)
        {
            return (0, 0);
        }

        ImVec2 size = ImGui.GetContentRegionAvail();
        return (size.X, size.Y);
    }

    [Method]
    public static void PushId(string id)
    {
        if (!ImGuiState.FrameOpen)
        {
            return;
        }

        fixed (byte* text = Utf8.ToZeroTerminated(id))
        {
            ImGui.PushIDStr(text);
        }
    }

    [Method]
    public static void PushIdNumber(int id)
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.PushIDInt(id);
        }
    }

    [Method]
    public static void PopId()
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.PopID();
        }
    }

    // ---- Trees, tables, menus, tabs, popups ----------------------------------------------------------------------------------------

    [Method]
    public static bool CollapsingHeader(string label, bool openByDefault = false) => ImGuiState.FrameOpen && Ui.CollapsingHeader(label, openByDefault);

    /// <summary>A tree node; when it returns true, call <c>tree_pop</c> after its content.</summary>
    [Method]
    public static bool TreeNode(string label)
    {
        if (!ImGuiState.FrameOpen)
        {
            return false;
        }

        fixed (byte* text = Utf8.ToZeroTerminated(label))
        {
            return ImGui.TreeNodeStr(text);
        }
    }

    [Method]
    public static void TreePop()
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.TreePop();
        }
    }

    /// <summary>A table; when it returns true, fill it and call <c>end_table</c>.</summary>
    [Method]
    public static bool BeginTable(string id, int columns, ImGuiTableFlags flags = Apogee.Native.CImGui.ImGuiTableFlags.RowBg | Apogee.Native.CImGui.ImGuiTableFlags.BordersInnerH | Apogee.Native.CImGui.ImGuiTableFlags.SizingStretchProp)
        => ImGuiState.FrameOpen && Ui.BeginTable(id, columns, flags);

    [Method]
    public static void EndTable()
    {
        if (ImGuiState.FrameOpen)
        {
            Ui.EndTable();
        }
    }

    [Method]
    public static void TableSetupColumn(string label)
    {
        if (!ImGuiState.FrameOpen)
        {
            return;
        }

        fixed (byte* text = Utf8.ToZeroTerminated(label))
        {
            ImGui.TableSetupColumn(text, ImGuiTableColumnFlags.None, 0f, 0);
        }
    }

    [Method]
    public static void TableHeadersRow()
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.TableHeadersRow();
        }
    }

    [Method]
    public static void TableNextRow()
    {
        if (ImGuiState.FrameOpen)
        {
            Ui.Row();
        }
    }

    [Method]
    public static bool TableNextColumn() => ImGuiState.FrameOpen && ImGui.TableNextColumn();

    [Method]
    public static bool BeginMainMenuBar() => ImGuiState.FrameOpen && ImGui.BeginMainMenuBar();

    [Method]
    public static void EndMainMenuBar()
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.EndMainMenuBar();
        }
    }

    [Method]
    public static bool BeginMenu(string label)
    {
        if (!ImGuiState.FrameOpen)
        {
            return false;
        }

        fixed (byte* text = Utf8.ToZeroTerminated(label))
        {
            return ImGui.BeginMenu(text, true);
        }
    }

    [Method]
    public static void EndMenu()
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.EndMenu();
        }
    }

    [Method]
    public static bool MenuItem(string label, string? shortcut = null, bool selected = false)
    {
        if (!ImGuiState.FrameOpen)
        {
            return false;
        }

        fixed (byte* text = Utf8.ToZeroTerminated(label))
        fixed (byte* keys = shortcut is null ? null : Utf8.ToZeroTerminated(shortcut))
        {
            return ImGui.MenuItemBool(text, keys, selected, true);
        }
    }

    [Method]
    public static bool BeginTabBar(string id)
    {
        if (!ImGuiState.FrameOpen)
        {
            return false;
        }

        fixed (byte* text = Utf8.ToZeroTerminated(id))
        {
            return ImGui.BeginTabBar(text, ImGuiTabBarFlags.None);
        }
    }

    [Method]
    public static void EndTabBar()
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.EndTabBar();
        }
    }

    [Method]
    public static bool BeginTabItem(string label)
    {
        if (!ImGuiState.FrameOpen)
        {
            return false;
        }

        fixed (byte* text = Utf8.ToZeroTerminated(label))
        {
            return ImGui.BeginTabItem(text, null, ImGuiTabItemFlags.None);
        }
    }

    [Method]
    public static void EndTabItem()
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.EndTabItem();
        }
    }

    [Method]
    public static void OpenPopup(string id)
    {
        if (!ImGuiState.FrameOpen)
        {
            return;
        }

        fixed (byte* text = Utf8.ToZeroTerminated(id))
        {
            ImGui.OpenPopupStr(text, ImGuiPopupFlags.None);
        }
    }

    /// <summary>A popup opened with <c>open_popup</c>; when it returns true, fill it and call <c>end_popup</c>.</summary>
    [Method]
    public static bool BeginPopup(string id)
    {
        if (!ImGuiState.FrameOpen)
        {
            return false;
        }

        fixed (byte* text = Utf8.ToZeroTerminated(id))
        {
            return ImGui.BeginPopup(text, ImGuiWindowFlags.None);
        }
    }

    [Method]
    public static bool BeginPopupModal(string title)
    {
        if (!ImGuiState.FrameOpen)
        {
            return false;
        }

        fixed (byte* text = Utf8.ToZeroTerminated(title))
        {
            return ImGui.BeginPopupModal(text, null, ImGuiWindowFlags.None);
        }
    }

    [Method]
    public static void EndPopup()
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.EndPopup();
        }
    }

    [Method]
    public static void CloseCurrentPopup()
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.CloseCurrentPopup();
        }
    }

    [Method]
    public static void SetTooltip(string text)
    {
        if (ImGuiState.FrameOpen && ImGui.BeginTooltip())
        {
            Ui.Text(text);
            ImGui.EndTooltip();
        }
    }

    // ---- Queries and style -----------------------------------------------------------------------------------------------------------------

    [Method]
    public static bool IsItemHovered() => ImGuiState.FrameOpen && ImGui.IsItemHovered(ImGuiHoveredFlags.None);

    [Method]
    public static bool IsItemClicked() => ImGuiState.FrameOpen && ImGui.IsItemClicked(ImGuiMouseButton.Left);

    /// <summary>True while ImGui wants the mouse: the pointer is over a window, so the game should not also react to a click.</summary>
    [Method]
    public static bool WantCaptureMouse() => ImGuiState.FrameOpen && ImGui.GetIOContextPtr(ImGui.GetCurrentContext())->WantCaptureMouse;

    [Method]
    public static bool WantCaptureKeyboard() => ImGuiState.FrameOpen && ImGui.GetIOContextPtr(ImGui.GetCurrentContext())->WantCaptureKeyboard;

    [Method]
    public static (float Width, float Height) DisplaySize()
    {
        if (!ImGuiState.FrameOpen)
        {
            return (0, 0);
        }

        ImVec2 size = ImGui.GetIOContextPtr(ImGui.GetCurrentContext())->DisplaySize;
        return (size.X, size.Y);
    }

    [Method]
    public static void PushStyleColor(ImGuiCol color, float[] value)
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.PushStyleColorVec4(color, Color(value));
        }
    }

    [Method]
    public static void PopStyleColor(int count = 1)
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.PopStyleColor(count);
        }
    }

    [Method]
    public static void PushStyleVar(ImGuiStyleVar variable, float value)
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.PushStyleVarFloat(variable, value);
        }
    }

    [Method]
    public static void PopStyleVar(int count = 1)
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.PopStyleVar(count);
        }
    }

    [Method]
    public static void BeginDisabled(bool disabled = true)
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.BeginDisabled(disabled);
        }
    }

    [Method]
    public static void EndDisabled()
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.EndDisabled();
        }
    }

    /// <summary>Dear ImGui's own demo window: every widget there is, for a look at what is possible.</summary>
    [Method]
    public static void ShowDemoWindow()
    {
        if (ImGuiState.FrameOpen)
        {
            ImGui.ShowDemoWindow(null);
        }
    }
}
