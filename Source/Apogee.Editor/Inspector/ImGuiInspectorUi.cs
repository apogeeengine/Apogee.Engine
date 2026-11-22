using System;
using System.Collections.Generic;
using Apogee.DevTools;
using Apogee.Editor.Assets;
using Apogee.Interop;
using Apogee.Native.CImGui;

namespace Apogee.Editor.Inspector;

/// <summary>
/// The inspector's widgets as Dear ImGui draws them. It has no logic of its own (what a field shows and what an edit does is in <see cref="BuiltInDrawers"/> and <see cref="InspectorView"/>, which are
/// tested through a fake), only the calls: a field the selected entities disagree on has ImGui's <c>MixedValue</c> flag pushed around it, so a drag field or a checkbox shows that.
/// </summary>
public sealed unsafe class ImGuiInspectorUi : IInspectorUi
{
    private bool _active;
    private bool _deactivated;

    /// <inheritdoc />
    public bool ItemActive => _active;

    /// <inheritdoc />
    public bool ItemDeactivatedAfterEdit => _deactivated;

    /// <inheritdoc />
    public void PushId(string id)
    {
        Gui.PushId(id);
        _active = false;
        _deactivated = false;
    }

    /// <inheritdoc />
    public void PopId() => Gui.PopId();

    /// <inheritdoc />
    public void BeginDisabled(bool disabled) => ImGui.BeginDisabled(disabled);

    /// <inheritdoc />
    public void EndDisabled() => ImGui.EndDisabled();

    /// <inheritdoc />
    public void Heading(string text) => Gui.SeparatorText(text);

    /// <inheritdoc />
    public void Text(string text) => Ui.Text(text);

    /// <inheritdoc />
    public void Tooltip(string text) => Gui.Tooltip(text);

    /// <inheritdoc />
    public bool Drag(string label, Span<double> values, double speed, double min, double max, string format, bool mixed)
    {
        Mixed(mixed);
        bool changed = Gui.DragDoubles(label, values, speed, min, max, format);
        Unmixed(mixed);
        Remember(false);
        return changed;
    }

    /// <inheritdoc />
    public bool Toggle(string label, ref bool value, bool mixed)
    {
        Mixed(mixed);
        bool changed = Gui.Checkbox(label, ref value);
        Unmixed(mixed);
        Remember(false);
        return changed;
    }

    /// <inheritdoc />
    public int Combo(string label, string preview, IReadOnlyList<string> items, int selected)
    {
        int picked = -1;
        fixed (byte* text = Utf8.ToZeroTerminated(label))
        fixed (byte* shown = Utf8.ToZeroTerminated(preview))
        {
            if (ImGui.BeginCombo(text, shown, ImGuiComboFlags.None))
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (Gui.Selectable($"{items[i]}##{i}", i == selected))
                    {
                        picked = i;
                    }
                }

                ImGui.EndCombo();
            }
        }

        Remember(false);
        return picked;
    }

    /// <inheritdoc />
    public bool BeginNode(string id, string text) => Gui.TreeNode($"{text}##{id}", ImGuiTreeNodeFlags.None);

    /// <inheritdoc />
    public void EndNode() => Gui.TreePop();

    /// <inheritdoc />
    public bool Color(string label, Span<float> rgb, bool mixed)
    {
        bool changed;
        Mixed(mixed);
        fixed (byte* text = Utf8.ToZeroTerminated(label))
        fixed (float* channels = rgb)
        {
            // HDR keeps what is above one (the fields take any number); Float shows the channels as 0 to 1 numbers, not 0 to 255.
            changed = ImGui.ColorEdit3(text, channels, ImGuiColorEditFlags.Hdr | ImGuiColorEditFlags.Float);
        }

        Unmixed(mixed);

        // The picker is a popup of its own: while a drag in it goes on, some item is active even though the colour button is not.
        Remember(true);
        return changed;
    }

    /// <inheritdoc />
    public bool EntityReference(string label, string text, ref ulong id, bool mixed)
    {
        bool changed = false;
        Gui.Button($"{text}##ref", new System.Numerics.Vector2(-60, 0));
        if (Gui.BeginDropTarget())
        {
            if (Gui.AcceptId("ENTITY") is { } dropped)
            {
                id = dropped;
                changed = true;
            }

            Gui.EndDropTarget();
        }

        Gui.SameLine();
        if (Gui.SmallButton("Clear"))
        {
            id = 0;
            changed = true;
        }

        Gui.SameLine();
        Ui.Text(label);
        return changed;
    }

    /// <inheritdoc />
    public AssetFieldResult AssetReference(string label, in AssetFieldView view, AssetPicker picker)
    {
        AssetFieldResult result = default;
        bool broken = view.Broken;
        if (broken)
        {
            ImGui.PushStyleColorVec4(ImGuiCol.Text, new ImVec4 { X = 1f, Y = 0.35f, Z = 0.3f, W = 1f });
        }

        bool open = Gui.Button($"{view.Text}##asset", new System.Numerics.Vector2(-60, 0));
        if (broken)
        {
            ImGui.PopStyleColor(1);
        }

        if (view.Detail is { } detail)
        {
            Gui.Tooltip(detail);
        }

        if (Gui.BeginDropTarget())
        {
            if (Gui.AcceptText("ASSET") is { } dropped)
            {
                result = new AssetFieldResult(AssetFieldAction.Drop, dropped);
            }

            Gui.EndDropTarget();
        }

        if (Gui.BeginContextMenu("assetmenu"))
        {
            if (Gui.MenuItem("Show in assets", null, false, view.CanShow))
            {
                result = new AssetFieldResult(AssetFieldAction.Show);
            }

            Gui.EndPopup();
        }

        if (open)
        {
            picker.Open();
            Gui.OpenPopup("pick");
        }

        if (DrawPicker(picker) is { } picked)
        {
            result = new AssetFieldResult(AssetFieldAction.Pick, picked);
        }

        Gui.SameLine();
        if (Gui.SmallButton("Clear"))
        {
            result = new AssetFieldResult(AssetFieldAction.Clear);
        }

        Gui.SameLine();
        Ui.Text(label);
        if (view.Problem is { } problem)
        {
            Ui.TextColored(new System.Numerics.Vector4(1f, 0.35f, 0.3f, 1f), problem);
        }

        Remember(false);
        return result;
    }

    // The popup: a filter box and the files that match, drawn through a clipper so that only the rows on the screen cost anything. Returns the path chosen this frame.
    private static string? DrawPicker(AssetPicker picker)
    {
        if (!Gui.BeginPopup("pick"))
        {
            return null;
        }

        string? picked = null;
        Ui.Text($"Pick a {picker.Label.ToLowerInvariant()}");
        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere(0);
        }

        ImGui.SetNextItemWidth(420);
        string filter = picker.Filter;
        if (Gui.InputText("##filter", ref filter))
        {
            picker.Filter = filter;
        }

        IReadOnlyList<CatalogEntry> results = picker.Results;
        if (picker.Loading)
        {
            Ui.Text("Listing the assets...");
        }
        else if (results.Count == 0)
        {
            Ui.Text(picker.Filter.Length == 0 ? $"The game has no {picker.Label.ToLowerInvariant()} files." : "Nothing matches.");
        }
        else
        {
            Ui.Text(picker.Truncated ? $"{results.Count} files (the list is cut short: the project has too many)" : $"{results.Count} files");
        }

        if (Gui.BeginChild("##list", new System.Numerics.Vector2(420, 260)))
        {
            ImGuiListClipper* clipper = ImGui.ImGuiListClipperImGuiListClipper();
            ImGui.ImGuiListClipperBegin(clipper, results.Count, -1f);
            while (ImGui.ImGuiListClipperStep(clipper))
            {
                for (int i = clipper->DisplayStart; i < clipper->DisplayEnd; i++)
                {
                    CatalogEntry entry = results[i];
                    string folder = VfsFolder(entry.Path);
                    if (Gui.Selectable($"{entry.Name}   {folder}##{i}", false))
                    {
                        picked = entry.Path;
                    }

                    Gui.Tooltip(entry.Path);
                }
            }

            ImGui.ImGuiListClipperEnd(clipper);
            ImGui.ImGuiListClipperDestroy(clipper);
        }

        Gui.EndChild();
        if (picked is not null)
        {
            Gui.ClosePopup();
        }

        Gui.EndPopup();
        return picked;
    }

    private static string VfsFolder(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash > 0 ? path[..slash] : "";
    }

    /// <inheritdoc />
    public bool ContextMenuItem(string label)
    {
        if (!Gui.BeginContextMenu("context"))
        {
            return false;
        }

        bool chosen = Gui.MenuItem(label);
        Gui.EndPopup();
        return chosen;
    }

    private static void Mixed(bool mixed)
    {
        if (mixed)
        {
            ImGui.PushItemFlag(ImGuiItemFlags.MixedValue, true);
        }
    }

    private static void Unmixed(bool mixed)
    {
        if (mixed)
        {
            ImGui.PopItemFlag();
        }
    }

    /// <summary>Takes the state of the item just drawn: held, or let go of after a change. A colour picker counts as held while any item is.</summary>
    private void Remember(bool anyItem)
    {
        _active = ImGui.IsItemActive() || (anyItem && ImGui.IsAnyItemActive());
        _deactivated = ImGui.IsItemDeactivatedAfterEdit();
    }
}
