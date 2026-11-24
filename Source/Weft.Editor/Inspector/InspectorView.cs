using System;
using System.Collections.Generic;
using System.Linq;
using Weft.Ecs;
using Weft.Reflection;

namespace Weft.Editor.Inspector;

/// <summary>
/// Draws the fields of one component through an <see cref="IInspectorUi"/>: fields grouped under their category (in the order the categories first appear), hidden ones left out, read-only ones greyed,
/// each drawn by the drawer the <see cref="DrawerTable"/> picks, a tooltip from the field's doc, and a right-click <i>Reset to default</i>. What a drawer changed is written to every selected entity
/// by <see cref="InspectorEditor"/>, as one undo step per gesture: the history is sealed when the widget was let go of (or changed without being held, a checkbox or a pick).
/// </summary>
public sealed class InspectorView(EditorProject project, DrawerTable? drawers = null)
{
    private readonly InspectorEditor _editor = new(project);
    private readonly DrawerTable _drawers = drawers ?? InspectorDrawers.Shared;

    /// <summary>
    /// Draws <paramref name="info"/> of <paramref name="active"/>, editing it on all of <paramref name="targets"/>. With <paramref name="locked"/> (the game is playing) everything is greyed and
    /// nothing is written.
    /// </summary>
    public void DrawComponent(IInspectorUi ui, IReadOnlyList<Entity> targets, Entity active, Entity component, ComponentInfo info, bool locked)
    {
        if (_drawers.FindComponent(info.Name) is { } whole)
        {
            DrawWhole(ui, targets, active, component, info, locked, whole);
            return;
        }

        string? category = null;
        foreach (FieldDef field in info.Fields.OrderBy(f => CategoryOrder(info, f)))
        {
            InspectorMeta? meta = field.Meta;
            if (meta?.Hidden == true)
            {
                continue;
            }

            if (meta?.Category is { } fieldCategory && fieldCategory != category)
            {
                category = fieldCategory;
                ui.Heading(fieldCategory);
            }

            if (!_editor.Read(active, targets, component, info, out byte[] bytes, out bool[] mixed))
            {
                return;
            }

            int index = IndexOf(info, field);
            int size = field.Kind.SizeOf();
            bool editable = !locked && meta?.ReadOnly != true;
            DrawerChoice choice = _drawers.Find(info, field);

            ui.PushId(field.Name);
            ui.BeginDisabled(!editable);
            bool changed = choice.Field!(new FieldEdit(info, field, bytes.AsSpan(field.Offset, size), mixed[index], project, ui, field.Name, active));
            bool done = ui.ItemDeactivatedAfterEdit || (changed && !ui.ItemActive);
            ui.EndDisabled();

            if (editable && ui.ContextMenuItem("Reset to default"))
            {
                _editor.ResetField(targets, component, info, field);
            }

            ui.PopId();

            if (changed && editable)
            {
                _editor.WriteField(targets, component, info, field, bytes.AsSpan(field.Offset, size));
            }

            if (done)
            {
                _editor.Seal();
            }

            if (field.Doc is { } doc)
            {
                ui.Tooltip(doc);
            }
        }
    }

    private void DrawWhole(IInspectorUi ui, IReadOnlyList<Entity> targets, Entity active, Entity component, ComponentInfo info, bool locked, ComponentDrawer drawer)
    {
        if (!_editor.Read(active, targets, component, info, out byte[] bytes, out bool[] mixed))
        {
            return;
        }

        byte[] original = (byte[])bytes.Clone();
        ui.PushId(info.Name);
        ui.BeginDisabled(locked);
        bool changed = drawer(new ComponentEdit(info, bytes, mixed, project, ui));
        bool done = ui.ItemDeactivatedAfterEdit || (changed && !ui.ItemActive);
        ui.EndDisabled();
        ui.PopId();

        if (changed && !locked)
        {
            // Only the fields whose bytes the drawer changed are written, so the other entities keep their own values for the rest.
            var regions = new List<(int Offset, byte[] Value)>();
            foreach (FieldDef field in info.Fields)
            {
                int size = field.Kind.SizeOf();
                if (!bytes.AsSpan(field.Offset, size).SequenceEqual(original.AsSpan(field.Offset, size)))
                {
                    regions.Add((field.Offset, bytes.AsSpan(field.Offset, size).ToArray()));
                }
            }

            if (regions.Count > 0)
            {
                _editor.WriteRegions(targets, component, info, regions);
            }
        }

        if (done)
        {
            _editor.Seal();
        }
    }

    private static int IndexOf(ComponentInfo info, FieldDef field)
    {
        for (int i = 0; i < info.Fields.Count; i++)
        {
            if (ReferenceEquals(info.Fields[i], field))
            {
                return i;
            }
        }

        return 0;
    }

    private static int CategoryOrder(ComponentInfo info, FieldDef field)
    {
        // The index of the first field that has the same category (uncategorised fields keep their place at the front).
        string? category = field.Meta?.Category;
        if (category is null)
        {
            return -1;
        }

        for (int i = 0; i < info.Fields.Count; i++)
        {
            if (info.Fields[i].Meta?.Category == category)
            {
                return i;
            }
        }

        return 0;
    }
}
