using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Editor.Inspector;

namespace Apogee.Editor.Tests;

/// <summary>One widget the inspector drew: what kind, its label, and what it was shown with.</summary>
internal sealed record DrawnWidget(string Kind, string Label, bool Mixed, bool Disabled, string Text = "", double[] Numbers = null!, IReadOnlyList<string>? Items = null, int Selected = -1, double Speed = 0, double Min = 0, double Max = 0, string Format = "");

/// <summary>
/// An <see cref="IInspectorUi"/> that records what was drawn and answers as a person would: a script says which widget the person changed (by its label) and to what. No ImGui, so the drawers and the
/// inspector view are driven without a window.
/// </summary>
internal sealed class FakeInspectorUi : IInspectorUi
{
    private readonly Stack<string> _ids = new();
    private readonly Stack<bool> _disabledStack = new();

    private bool IsDisabled => _disabledStack.Contains(true);

    public List<DrawnWidget> Drawn { get; } = [];

    public List<string> Headings { get; } = [];

    public List<string> Tooltips { get; } = [];

    public List<string> Texts { get; } = [];

    /// <summary>The numbers a drag field is changed to, by label.</summary>
    public Dictionary<string, double[]> DragTo { get; } = [];

    /// <summary>What a checkbox is changed to, by label.</summary>
    public Dictionary<string, bool> ToggleTo { get; } = [];

    /// <summary>Which item of a combo is picked, by label.</summary>
    public Dictionary<string, int> PickIndex { get; } = [];

    /// <summary>The colour a picker is changed to, by label.</summary>
    public Dictionary<string, float[]> ColorTo { get; } = [];

    /// <summary>The entity id a reference is changed to, by label (0 clears it).</summary>
    public Dictionary<string, ulong> EntityTo { get; } = [];

    /// <summary>The text a path field is changed to, by label.</summary>
    public Dictionary<string, string> PathTo { get; } = [];

    /// <summary>The fields (by name) on which the person right-clicks and chooses <i>Reset to default</i>.</summary>
    public HashSet<string> ResetOn { get; } = [];

    /// <summary>The nodes (flags fields) are open.</summary>
    public bool NodesOpen { get; set; } = true;

    /// <summary>The widgets (by label) the person is still holding: <see cref="ItemActive"/> is true right after one of them is drawn.</summary>
    public HashSet<string> HeldOn { get; } = [];

    /// <summary>The widgets (by label) the person let go of after a change: <see cref="ItemDeactivatedAfterEdit"/> is true right after one of them is drawn.</summary>
    public HashSet<string> ReleasedOn { get; } = [];

    private string _last = "";

    public bool ItemActive => HeldOn.Contains(_last);

    public bool ItemDeactivatedAfterEdit => ReleasedOn.Contains(_last);

    public void PushId(string id)
    {
        _ids.Push(id);
        _last = "";
    }

    public void PopId() => _ids.Pop();

    public void BeginDisabled(bool disabled) => _disabledStack.Push(disabled);

    public void EndDisabled() => _disabledStack.Pop();

    public void Heading(string text) => Headings.Add(text);

    public void Text(string text) => Texts.Add(text);

    public void Tooltip(string text) => Tooltips.Add(text);

    public DrawnWidget Single(string kind, string label) => Drawn.Single(w => w.Kind == kind && w.Label == label);

    public bool Drag(string label, Span<double> values, double speed, double min, double max, string format, bool mixed)
    {
        _last = label;
        Drawn.Add(new DrawnWidget("drag", label, mixed, IsDisabled, Numbers: values.ToArray(), Speed: speed, Min: min, Max: max, Format: format));
        if (!DragTo.TryGetValue(label, out double[]? to))
        {
            return false;
        }

        to.CopyTo(values);
        return true;
    }

    public bool Toggle(string label, ref bool value, bool mixed)
    {
        _last = label;
        Drawn.Add(new DrawnWidget("toggle", label, mixed, IsDisabled, Numbers: [value ? 1 : 0]));
        if (!ToggleTo.TryGetValue(label, out bool to))
        {
            return false;
        }

        value = to;
        return true;
    }

    public int Combo(string label, string preview, IReadOnlyList<string> items, int selected)
    {
        _last = label;
        Drawn.Add(new DrawnWidget("combo", label, false, IsDisabled, Text: preview, Items: items, Selected: selected));
        return PickIndex.GetValueOrDefault(label, -1);
    }

    public bool BeginNode(string id, string text)
    {
        _last = id;
        Drawn.Add(new DrawnWidget("node", id, false, IsDisabled, Text: text));
        return NodesOpen;
    }

    public void EndNode()
    {
    }

    public bool Color(string label, Span<float> rgb, bool mixed)
    {
        _last = label;
        Drawn.Add(new DrawnWidget("color", label, mixed, IsDisabled, Numbers: rgb.ToArray().Select(c => (double)c).ToArray()));
        if (!ColorTo.TryGetValue(label, out float[]? to))
        {
            return false;
        }

        to.CopyTo(rgb);
        return true;
    }

    public bool EntityReference(string label, string text, ref ulong id, bool mixed)
    {
        _last = label;
        Drawn.Add(new DrawnWidget("entity", label, mixed, IsDisabled, Text: text));
        if (!EntityTo.TryGetValue(label, out ulong to))
        {
            return false;
        }

        id = to;
        return true;
    }

    public bool PathInput(string label, ref string path, IReadOnlyList<string> dropExtensions)
    {
        _last = label;
        Drawn.Add(new DrawnWidget("path", label, false, IsDisabled, Text: path, Items: dropExtensions));
        if (!PathTo.TryGetValue(label, out string? to))
        {
            return false;
        }

        path = to;
        return true;
    }

    public bool ContextMenuItem(string label) => label == "Reset to default" && _ids.Count > 0 && ResetOn.Contains(_ids.Peek());

    /// <summary>Forgets what was drawn and what the person is doing, for the next frame.</summary>
    public void NextFrame()
    {
        Drawn.Clear();
        Headings.Clear();
        Tooltips.Clear();
        Texts.Clear();
        DragTo.Clear();
        ToggleTo.Clear();
        PickIndex.Clear();
        ColorTo.Clear();
        EntityTo.Clear();
        PathTo.Clear();
        ResetOn.Clear();
        HeldOn.Clear();
        ReleasedOn.Clear();
    }
}
