using System;
using System.Collections.Generic;

namespace Apogee.Editor.Inspector;

/// <summary>
/// The widgets the inspector draws with, as an interface: the drawers and the inspector view only talk to this, so a test drives them with a fake that answers as a person would, and only
/// <see cref="ImGuiInspectorUi"/> knows ImGui. Every widget method returns true when the person changed the value this frame (and has written it into the argument). The calls are immediate-mode:
/// draw, look at <see cref="ItemActive"/>, draw the next.
/// </summary>
public interface IInspectorUi
{
    /// <summary>The last widget is being dragged or typed in: a change is not the end of the gesture yet.</summary>
    bool ItemActive { get; }

    /// <summary>The last widget was let go of after a change: the gesture is over.</summary>
    bool ItemDeactivatedAfterEdit { get; }

    /// <summary>Starts a scope in which ids are unique (a field's name); also forgets the state of the last widget.</summary>
    void PushId(string id);

    /// <summary>Ends the scope of <see cref="PushId"/>.</summary>
    void PopId();

    /// <summary>Greys out and blocks what is drawn until <see cref="EndDisabled"/>.</summary>
    void BeginDisabled(bool disabled);

    /// <summary>Ends <see cref="BeginDisabled"/>.</summary>
    void EndDisabled();

    /// <summary>A heading over a group of fields (a category).</summary>
    void Heading(string text);

    /// <summary>A line of text.</summary>
    void Text(string text);

    /// <summary>A tooltip for the last widget, shown while it is hovered.</summary>
    void Tooltip(string text);

    /// <summary>
    /// One to four numbers as drag fields (a slider for a single number with both limits). <paramref name="min"/> and <paramref name="max"/> are NaN for no limit. With <paramref name="mixed"/> the
    /// field shows that the selected entities differ instead of a number.
    /// </summary>
    bool Drag(string label, Span<double> values, double speed, double min, double max, string format, bool mixed);

    /// <summary>A checkbox; <paramref name="mixed"/> shows the dash of a field the selected entities disagree on.</summary>
    bool Toggle(string label, ref bool value, bool mixed);

    /// <summary>A combo box that shows <paramref name="preview"/> and lists <paramref name="items"/>; returns the index of the one picked this frame, or -1.</summary>
    int Combo(string label, string preview, IReadOnlyList<string> items, int selected);

    /// <summary>A node that opens to show widgets (the checkboxes of a flags field), with <paramref name="text"/> as its caption and <paramref name="id"/> to tell it apart; call <see cref="EndNode"/> only when it returns true.</summary>
    bool BeginNode(string id, string text);

    /// <summary>Ends a node that <see cref="BeginNode"/> opened.</summary>
    void EndNode();

    /// <summary>A colour picker over three linear floats: values above one are allowed (a light can be brighter than white) and kept.</summary>
    bool Color(string label, Span<float> rgb, bool mixed);

    /// <summary>
    /// A reference to another entity: a button with <paramref name="text"/> that accepts a dropped entity, and a clear button. <paramref name="id"/> is the entity id (0 for none); it changes when an
    /// entity is dropped or cleared.
    /// </summary>
    bool EntityReference(string label, string text, ref ulong id, bool mixed);

    /// <summary>A text field for a path (Enter commits) that also takes an asset dragged from the asset browser when its file name ends with one of <paramref name="dropExtensions"/>.</summary>
    bool PathInput(string label, ref string path, IReadOnlyList<string> dropExtensions);

    /// <summary>The menu of a right click on the last widget, with one entry; true when it was chosen.</summary>
    bool ContextMenuItem(string label);
}
