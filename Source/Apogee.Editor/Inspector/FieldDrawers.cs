using System;
using System.Collections.Generic;
using Apogee.Ecs;
using Apogee.Reflection;

namespace Apogee.Editor.Inspector;

/// <summary>
/// What a drawer of one field is given: the field, its bytes (a copy: the drawer writes the new value into <see cref="Bytes"/> and returns true), whether the selected entities disagree on it, the
/// project, the active entity and the widgets to draw with. A drawer that edits several fields at once (see <see cref="ComponentDrawer"/>) gets a <see cref="ComponentEdit"/> instead.
/// </summary>
public readonly ref struct FieldEdit
{
    public FieldEdit(ComponentInfo component, FieldDef field, Span<byte> bytes, bool mixed, EditorProject project, IInspectorUi ui, string label, Entity entity = default)
    {
        Component = component;
        Field = field;
        Bytes = bytes;
        Mixed = mixed;
        Project = project;
        Ui = ui;
        Label = label;
        Entity = entity;
    }

    /// <summary>The entity whose value <see cref="Bytes"/> holds (the active one when several are selected); the default entity when the caller does not say.</summary>
    public Entity Entity { get; }

    /// <summary>The component the field belongs to.</summary>
    public ComponentInfo Component { get; }

    /// <summary>The field.</summary>
    public FieldDef Field { get; }

    /// <summary>The field's bytes (<see cref="FieldKinds.SizeOf"/> of its kind): the active entity's value to show, and where the edited one goes.</summary>
    public Span<byte> Bytes { get; }

    /// <summary>Several entities are selected and their values for this field differ: the widget shows that instead of a number. <see cref="Bytes"/> holds the active entity's.</summary>
    public bool Mixed { get; }

    /// <summary>The project, for what a drawer looks up (an entity's name, a model's path).</summary>
    public EditorProject Project { get; }

    /// <summary>The widgets to draw with.</summary>
    public IInspectorUi Ui { get; }

    /// <summary>The text to label the widget with: the field's name.</summary>
    public string Label { get; }
}

/// <summary>What a drawer of a whole component is given: the component's bytes (a copy), and for each field of <see cref="Component"/> whether the selected entities disagree on it.</summary>
public readonly ref struct ComponentEdit
{
    public ComponentEdit(ComponentInfo component, Span<byte> bytes, ReadOnlySpan<bool> mixed, EditorProject project, IInspectorUi ui)
    {
        Component = component;
        Bytes = bytes;
        Mixed = mixed;
        Project = project;
        Ui = ui;
    }

    /// <summary>The component.</summary>
    public ComponentInfo Component { get; }

    /// <summary>The whole value (<see cref="ComponentInfo.Size"/> bytes). The drawer changes it and returns true; only the fields whose bytes changed are written to the entities.</summary>
    public Span<byte> Bytes { get; }

    /// <summary>One entry for each of <see cref="ComponentInfo.Fields"/>, in order: the selected entities' values differ.</summary>
    public ReadOnlySpan<bool> Mixed { get; }

    /// <summary>The project.</summary>
    public EditorProject Project { get; }

    /// <summary>The widgets to draw with.</summary>
    public IInspectorUi Ui { get; }
}

/// <summary>Draws and edits one field. Writes the new value into <see cref="FieldEdit.Bytes"/> and returns true when the person changed it.</summary>
public delegate bool FieldDrawer(in FieldEdit edit);

/// <summary>Draws and edits a whole component in place of its fields. Writes the new value into <see cref="ComponentEdit.Bytes"/> and returns true when the person changed it.</summary>
public delegate bool ComponentDrawer(in ComponentEdit edit);

/// <summary>Which rule of the table picked a drawer, in the order the table tries them.</summary>
public enum DrawerLevel
{
    /// <summary>A drawer registered for the whole component.</summary>
    Component,

    /// <summary>A drawer registered for this component's field.</summary>
    Field,

    /// <summary>A drawer registered for the asset type of an <see cref="FieldKind.AssetRef"/> field.</summary>
    AssetType,

    /// <summary>The default for what the registry says about the field: an enum, a colour.</summary>
    Hint,

    /// <summary>The default for the field's kind: an entity reference, an asset picker, or numbers.</summary>
    Kind,
}

/// <summary>The drawer a lookup found and the rule that found it: <see cref="Component"/> for a <see cref="DrawerLevel.Component"/> choice, <see cref="Field"/> for every other.</summary>
public readonly record struct DrawerChoice(DrawerLevel Level, FieldDrawer? Field, ComponentDrawer? Component);

/// <summary>
/// The table that says which drawer edits a field. A lookup tries, in this order: a drawer for the whole component (by name), a drawer for the field (component and field name), a drawer for the
/// field's asset type, then the defaults, which look at what the registry says about the field (an enum, a colour) and then at its kind. Registering again for the same key replaces the drawer. A
/// table is used from the editor's one UI thread. <see cref="InspectorDrawers"/> holds the editor's own.
/// </summary>
public sealed class DrawerTable
{
    private readonly Dictionary<string, ComponentDrawer> _components = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Component, string Field), FieldDrawer> _fields = [];
    private readonly Dictionary<string, FieldDrawer> _assetTypes = new(StringComparer.Ordinal);

    /// <summary>A table with the built-in drawers: numbers for every kind, entity references, enum combos and flag checkboxes, colours, and a picker for every asset field.</summary>
    public static DrawerTable CreateDefault()
    {
        return new DrawerTable();
    }

    /// <summary>Draws the whole component called <paramref name="componentName"/> with <paramref name="drawer"/> instead of one drawer for each field.</summary>
    public void RegisterComponent(string componentName, ComponentDrawer drawer)
    {
        ArgumentException.ThrowIfNullOrEmpty(componentName);
        ArgumentNullException.ThrowIfNull(drawer);
        _components[componentName] = drawer;
    }

    /// <summary>Draws the field <paramref name="fieldName"/> of the component <paramref name="componentName"/> with <paramref name="drawer"/>.</summary>
    public void RegisterField(string componentName, string fieldName, FieldDrawer drawer)
    {
        ArgumentException.ThrowIfNullOrEmpty(componentName);
        ArgumentException.ThrowIfNullOrEmpty(fieldName);
        ArgumentNullException.ThrowIfNull(drawer);
        _fields[(componentName, fieldName)] = drawer;
    }

    /// <summary>Draws every <see cref="FieldKind.AssetRef"/> field of <paramref name="assetType"/> (<c>model</c>, <c>texture</c>) with <paramref name="drawer"/>, unless a drawer is registered for the field itself.</summary>
    public void RegisterAssetType(string assetType, FieldDrawer drawer)
    {
        ArgumentException.ThrowIfNullOrEmpty(assetType);
        ArgumentNullException.ThrowIfNull(drawer);
        _assetTypes[assetType] = drawer;
    }

    /// <summary>The drawer registered for the whole component called <paramref name="componentName"/>, or null.</summary>
    public ComponentDrawer? FindComponent(string componentName) => _components.GetValueOrDefault(componentName);

    /// <summary>The drawer for <paramref name="field"/> of <paramref name="component"/>, and which rule found it. A component drawer, when one is registered, wins over every field rule.</summary>
    public DrawerChoice Find(ComponentInfo component, FieldDef field)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(field);
        if (_components.TryGetValue(component.Name, out ComponentDrawer? whole))
        {
            return new DrawerChoice(DrawerLevel.Component, null, whole);
        }

        if (_fields.TryGetValue((component.Name, field.Name), out FieldDrawer? own))
        {
            return new DrawerChoice(DrawerLevel.Field, own, null);
        }

        if (field.AssetType is { } assetType && _assetTypes.TryGetValue(assetType, out FieldDrawer? byAsset))
        {
            return new DrawerChoice(DrawerLevel.AssetType, byAsset, null);
        }

        if (field.Enum is { } enumName && TypeRegistry.FindEnum(enumName) is not null)
        {
            return new DrawerChoice(DrawerLevel.Hint, BuiltInDrawers.Enum, null);
        }

        if (field.Meta?.Color == true && field.Kind == FieldKind.Vector3)
        {
            return new DrawerChoice(DrawerLevel.Hint, BuiltInDrawers.Color, null);
        }

        FieldDrawer byKind = field.Kind switch
        {
            FieldKind.Entity => BuiltInDrawers.EntityReference,
            FieldKind.AssetRef when field.AssetType is not null => BuiltInDrawers.AssetReference,   // whatever its type: kinds that packages register later get the picker too
            _ => BuiltInDrawers.Numbers,
        };
        return new DrawerChoice(DrawerLevel.Kind, byKind, null);
    }
}

/// <summary>
/// The editor's own <see cref="DrawerTable"/>, where an editor extension registers its drawers (C# only: games define their components in scripts and get the defaults). Register before the
/// inspector first draws, from the editor's main thread.
/// </summary>
public static class InspectorDrawers
{
    /// <summary>The table the inspector draws with.</summary>
    public static DrawerTable Shared { get; } = DrawerTable.CreateDefault();

    /// <summary>See <see cref="DrawerTable.RegisterComponent"/>.</summary>
    public static void RegisterComponent(string componentName, ComponentDrawer drawer) => Shared.RegisterComponent(componentName, drawer);

    /// <summary>See <see cref="DrawerTable.RegisterField"/>.</summary>
    public static void RegisterField(string componentName, string fieldName, FieldDrawer drawer) => Shared.RegisterField(componentName, fieldName, drawer);

    /// <summary>See <see cref="DrawerTable.RegisterAssetType"/>.</summary>
    public static void RegisterAssetType(string assetType, FieldDrawer drawer) => Shared.RegisterAssetType(assetType, drawer);
}
