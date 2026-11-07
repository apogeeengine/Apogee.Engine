using System;
using System.Numerics;
using System.Text;
using Apogee.DevTools;
using Apogee.Interop;
using Apogee.Native.CImGui;

namespace Apogee.Editor;

/// <summary>The ImGui calls the editor's panels make, with C# strings and numerics types instead of pointers and <c>ImVec2</c>s. <see cref="Ui"/> has the basic ones; these are the rest.</summary>
public static unsafe class Gui
{
    public static ImVec2 V(Vector2 v) => new() { X = v.X, Y = v.Y };

    public static Vector2 N(ImVec2 v) => new(v.X, v.Y);

    public static Vector2 Mouse => N(ImGui.GetMousePos());

    public static Vector2 Available => N(ImGui.GetContentRegionAvail());

    public static Vector2 CursorScreen => N(ImGui.GetCursorScreenPos());

    // ---- Widgets ----

    public static bool Button(string label, Vector2 size = default)
    {
        fixed (byte* text = Utf8.ToZeroTerminated(label))
        {
            return ImGui.Button(text, V(size));
        }
    }

    public static bool InvisibleButton(string id, Vector2 size)
    {
        fixed (byte* text = Utf8.ToZeroTerminated(id))
        {
            return ImGui.InvisibleButton(text, V(size), ImGuiButtonFlags.None);
        }
    }

    public static bool BeginChild(string id, Vector2 size = default)
    {
        fixed (byte* text = Utf8.ToZeroTerminated(id))
        {
            return ImGui.BeginChildStr(text, V(size), ImGuiChildFlags.None, ImGuiWindowFlags.None);
        }
    }

    public static void EndChild() => ImGui.EndChild();

    public static bool SmallButton(string label)
    {
        fixed (byte* text = Utf8.ToZeroTerminated(label))
        {
            return ImGui.SmallButton(text);
        }
    }

    public static bool Selectable(string label, bool selected, ImGuiSelectableFlags flags = ImGuiSelectableFlags.None)
    {
        fixed (byte* text = Utf8.ToZeroTerminated(label))
        {
            return ImGui.SelectableBool(text, selected, flags, default);
        }
    }

    /// <summary>A tree node; call <see cref="TreePop"/> when it returns true and has no <see cref="ImGuiTreeNodeFlags.NoTreePushOnOpen"/>.</summary>
    public static bool TreeNode(string label, ImGuiTreeNodeFlags flags)
    {
        fixed (byte* text = Utf8.ToZeroTerminated(label))
        {
            return ImGui.TreeNodeExStr(text, flags);
        }
    }

    public static void TreePop() => ImGui.TreePop();

    /// <summary>A text field that edits <paramref name="value"/> (up to <paramref name="capacity"/> bytes of UTF-8). True when it changed.</summary>
    public static bool InputText(string label, ref string value, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, int capacity = 512)
    {
        byte[] buffer = new byte[capacity];
        Encoding.UTF8.GetBytes(value, 0, Math.Min(value.Length, capacity / 4), buffer, 0);
        bool changed;
        fixed (byte* text = Utf8.ToZeroTerminated(label))
        fixed (byte* data = buffer)
        {
            changed = ImGui.InputText(text, data, (nuint)capacity, flags, null, null);
        }

        if (changed)
        {
            int length = Array.IndexOf(buffer, (byte)0);
            value = Encoding.UTF8.GetString(buffer, 0, length < 0 ? capacity : length);
        }

        return changed;
    }

    public static bool DragFloat(string label, ref float value, float speed, float min, float max, string format = "%.3f")
    {
        fixed (byte* text = Utf8.ToZeroTerminated(label))
        fixed (byte* fmt = Utf8.ToZeroTerminated(format))
        fixed (float* v = &value)
        {
            return ImGui.DragFloat(text, v, speed, min, max, fmt, ImGuiSliderFlags.None);
        }
    }

    /// <summary>Edits <paramref name="values"/> (1 to 4 numbers) with drag fields as doubles. <paramref name="min"/> and <paramref name="max"/> both NaN: no limits; both given: a slider.</summary>
    public static bool DragDoubles(string label, Span<double> values, double speed, double min, double max, string format = "%.3f")
    {
        bool slider = !double.IsNaN(min) && !double.IsNaN(max) && values.Length == 1;
        fixed (byte* text = Utf8.ToZeroTerminated(label))
        fixed (byte* fmt = Utf8.ToZeroTerminated(format))
        fixed (double* v = values)
        {
            double lo = double.IsNaN(min) ? double.MinValue : min;
            double hi = double.IsNaN(max) ? double.MaxValue : max;
            void* pMin = double.IsNaN(min) && double.IsNaN(max) ? null : &lo;
            void* pMax = double.IsNaN(min) && double.IsNaN(max) ? null : &hi;
            return slider
                ? ImGui.SliderScalar(text, ImGuiDataType.Double, v, &lo, &hi, fmt, ImGuiSliderFlags.None)
                : ImGui.DragScalarN(text, ImGuiDataType.Double, v, values.Length, (float)speed, pMin, pMax, fmt, ImGuiSliderFlags.None);
        }
    }

    public static bool Checkbox(string label, ref bool value) => Ui.Checkbox(label, ref value);

    public static void Label(string text) => Ui.Text(text);

    public static void SeparatorText(string text)
    {
        fixed (byte* bytes = Utf8.ToZeroTerminated(text))
        {
            ImGui.SeparatorText(bytes);
        }
    }

    public static void Disabled(bool disabled, Action body)
    {
        ImGui.BeginDisabled(disabled);
        try
        {
            body();
        }
        finally
        {
            ImGui.EndDisabled();
        }
    }

    public static void PushId(string id)
    {
        fixed (byte* text = Utf8.ToZeroTerminated(id))
        {
            ImGui.PushIDStr(text);
        }
    }

    public static void PushId(int id) => ImGui.PushIDInt(id);

    public static void PopId() => ImGui.PopID();

    public static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal) && ImGui.BeginTooltip())
        {
            Ui.Text(text);
            ImGui.EndTooltip();
        }
    }

    public static void SameLine(float spacing = -1f) => ImGui.SameLine(0f, spacing);

    // ---- Menus and popups ----

    public static bool BeginMainMenuBar() => ImGui.BeginMainMenuBar();

    public static void EndMainMenuBar() => ImGui.EndMainMenuBar();

    public static bool BeginMenu(string label, bool enabled = true)
    {
        fixed (byte* text = Utf8.ToZeroTerminated(label))
        {
            return ImGui.BeginMenu(text, enabled);
        }
    }

    public static void EndMenu() => ImGui.EndMenu();

    public static bool MenuItem(string label, string? shortcut = null, bool selected = false, bool enabled = true)
    {
        fixed (byte* text = Utf8.ToZeroTerminated(label))
        fixed (byte* keys = shortcut is null ? null : Utf8.ToZeroTerminated(shortcut))
        {
            return ImGui.MenuItemBool(text, keys, selected, enabled);
        }
    }

    public static void OpenPopup(string id)
    {
        fixed (byte* text = Utf8.ToZeroTerminated(id))
        {
            ImGui.OpenPopupStr(text, ImGuiPopupFlags.None);
        }
    }

    public static bool BeginPopup(string id)
    {
        fixed (byte* text = Utf8.ToZeroTerminated(id))
        {
            return ImGui.BeginPopup(text, ImGuiWindowFlags.None);
        }
    }

    /// <summary>The popup that a right click on the last item (or, with <paramref name="id"/> null, the window's empty space) opens.</summary>
    public static bool BeginContextMenu(string? id = null, bool onItem = true)
    {
        fixed (byte* text = id is null ? null : Utf8.ToZeroTerminated(id))
        {
            return onItem ? ImGui.BeginPopupContextItem(text, ImGuiPopupFlags.MouseButtonRight) : ImGui.BeginPopupContextWindow(text, ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems);
        }
    }

    public static void EndPopup() => ImGui.EndPopup();

    public static void ClosePopup() => ImGui.CloseCurrentPopup();

    // ---- Drag and drop: a payload is bytes with a type name ----

    public static bool BeginDragSource()
    {
        return ImGui.BeginDragDropSource(ImGuiDragDropFlags.None);
    }

    public static void EndDragSource() => ImGui.EndDragDropSource();

    public static void SetPayload(string type, ReadOnlySpan<byte> data)
    {
        fixed (byte* name = Utf8.ToZeroTerminated(type))
        fixed (byte* bytes = data)
        {
            ImGui.SetDragDropPayload(name, bytes, (nuint)data.Length, ImGuiCond.None);
        }
    }

    public static bool BeginDropTarget() => ImGui.BeginDragDropTarget();

    public static void EndDropTarget() => ImGui.EndDragDropTarget();

    /// <summary>The payload of this type that was dropped on the target, or null (it also says whether one is hovering with <paramref name="preview"/>).</summary>
    public static byte[]? AcceptPayload(string type)
    {
        fixed (byte* name = Utf8.ToZeroTerminated(type))
        {
            ImGuiPayload* payload = ImGui.AcceptDragDropPayload(name, ImGuiDragDropFlags.None);
            return payload is null || payload->Delivery == false ? null : new ReadOnlySpan<byte>(payload->Data, payload->DataSize).ToArray();
        }
    }

    public static void PayloadText(string type, string text) => SetPayload(type, Encoding.UTF8.GetBytes(text));

    public static string? AcceptText(string type) => AcceptPayload(type) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    public static void PayloadId(string type, ulong id) => SetPayload(type, BitConverter.GetBytes(id));

    public static ulong? AcceptId(string type) => AcceptPayload(type) is { Length: 8 } bytes ? BitConverter.ToUInt64(bytes) : null;

    // ---- Input ----

    public static bool KeyPressed(ImGuiKey key, bool repeat = false) => ImGui.IsKeyPressedBool(key, repeat);

    public static bool KeyDown(ImGuiKey key) => ImGui.IsKeyDownNil(key);

    public static bool Ctrl => ImGui.IsKeyDownNil(ImGuiKey.ImGuiKeyLeftCtrl) || ImGui.IsKeyDownNil(ImGuiKey.ImGuiKeyRightCtrl);

    public static bool Shift => ImGui.IsKeyDownNil(ImGuiKey.ImGuiKeyLeftShift) || ImGui.IsKeyDownNil(ImGuiKey.ImGuiKeyRightShift);

    public static bool MouseDown(ImGuiMouseButton button) => ImGui.IsMouseDownNil(button);

    public static bool MouseClicked(ImGuiMouseButton button) => ImGui.IsMouseClickedBool(button, false);

    public static bool MouseReleased(ImGuiMouseButton button) => ImGui.IsMouseReleasedNil(button);

    public static bool WantsTextInput(ImGuiHost host) => host.IO->WantTextInput;
}
