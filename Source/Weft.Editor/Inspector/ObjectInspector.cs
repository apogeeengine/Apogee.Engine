using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Weft.Editor.Editing;
using Weft.Host.Editing;
using Weft.Reflection;

// The inspector shows what getters, setters and drawers of any registered class do, code the editor does not own: whatever they throw is shown next to the property and never unwinds the editor.
#pragma warning disable CA1031

namespace Weft.Editor.Inspector;

/// <summary>
/// Draws and edits an <see cref="EngineObject"/> from what the registry says about its class: every property of <see cref="ClassInfo.Properties"/> (the base classes' first), with the registered
/// <see cref="InspectorMeta"/> (range, step, category, hidden, read-only, colour). It reads and writes through the properties' own getters and setters, the way scripts do, so nothing reflects.
/// The widgets come from <see cref="IInspectorUi"/>, so a test drives it with a fake; this class only decides what to draw and what a change means.
/// <list type="bullet">
/// <item>Bool, numbers, strings (one line, or a box for text of several lines), vectors (a colour picker for <see cref="InspectorMeta.Color"/>), rotations (angles), enums (a combo, or checkboxes for flags),
/// entities, and whatever else a property holds shown as text.</item>
/// <item>Objects are a node that opens to their own properties, down to <see cref="MaxDepth"/> levels; an object that is already being drawn above (a cycle), or has been destroyed, is named and not opened.</item>
/// <item>Lists show their count and the items a page at a time (<see cref="PageSize"/>), with add, remove and move up or down when the property can be set.</item>
/// <item>A property without a setter, or marked read-only, is drawn greyed out. A property whose getter throws shows the message; a setter that refuses a value leaves the property as it was and shows its message under it.</item>
/// </list>
/// With an <see cref="EditHistory"/> an edit is a <see cref="SetPropertyCommand"/> on it (the frames of one drag are one step); without one it is written straight away. A drawer for a whole class can
/// be registered (<see cref="InspectorDrawers.RegisterClass"/>). One inspector draws one object at a time on the editor's main thread.
/// </summary>
public sealed class ObjectInspector
{
    /// <summary>How many objects deep nested objects are opened: an object at this depth shows its properties, but not the objects inside them.</summary>
    public const int MaxDepth = 4;

    /// <summary>How many items of a list are drawn at a time (the rest is a page away).</summary>
    public const int PageSize = 200;

    /// <summary>A list with more items than this is shown but not edited (each edit copies the list).</summary>
    public const int MaxEditableItems = 5000;

    /// <summary>A text longer than this many characters is shown, cut, and not edited in place.</summary>
    public const int MaxEditableText = 16384;

    /// <summary>Beyond this many characters a text gets a box of several lines.</summary>
    private const int MultilineFrom = 200;

    private const int MaxErrors = 64;

    private readonly EditHistory? _history;
    private readonly DrawerTable _drawers;
    private readonly Func<Entity, string?>? _entityName;
    private readonly HashSet<EngineObject> _ancestors = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pages = new(StringComparer.Ordinal);
    private readonly HashSet<string> _multiline = new(StringComparer.Ordinal);
    private readonly Dictionary<ClassInfo, PropertyDef[]> _properties = [];
    private EngineObject? _root;
    private int _written;

    /// <summary>The widgets of one property over a frame: some are held, some were let go of.</summary>
    private struct Gesture
    {
        public bool Active;
        public bool Done;

        public void Note(IInspectorUi ui)
        {
            Active |= ui.ItemActive;
            Done |= ui.ItemDeactivatedAfterEdit;
        }
    }

    /// <summary>An inspector that writes through <paramref name="history"/> (null: straight to the setters), with the drawers of <paramref name="drawers"/> (the editor's shared table by default).</summary>
    /// <param name="history">Where edits are recorded as commands, or null.</param>
    /// <param name="drawers">The drawers registered for classes and multi-line properties.</param>
    /// <param name="entityName">The name to show for an entity reference: null when the entity is gone. Without it only the id is shown.</param>
    public ObjectInspector(EditHistory? history = null, DrawerTable? drawers = null, Func<Entity, string?>? entityName = null)
    {
        _history = history;
        _drawers = drawers ?? InspectorDrawers.Shared;
        _entityName = entityName;
    }

    /// <summary>The history edits go to, or null when they are written straight to the setters.</summary>
    public EditHistory? History => _history;

    /// <summary>The messages of setters that refused a value, by the property's path (<c>name</c>, <c>child/name</c>); shown under the property until it is written successfully.</summary>
    public IReadOnlyDictionary<string, string> Errors => _errors;

    /// <summary>
    /// The properties of <paramref name="info"/> and of its base classes, base classes first, a property of a derived class hiding one of the same script name below it; those with a category follow the
    /// uncategorised ones, grouped by category in the order the categories first appear.
    /// </summary>
    public static IReadOnlyList<PropertyDef> PropertiesOf(ClassInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var chain = new List<ClassInfo>();
        for (ClassInfo? current = info; current is not null && chain.Count < 64; current = current.Base)
        {
            chain.Add(current);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hiding = new List<PropertyDef>();
        foreach (ClassInfo link in chain)       // derived first: it hides
        {
            foreach (PropertyDef property in link.Properties)
            {
                if (seen.Add(property.ScriptName))
                {
                    hiding.Add(property);
                }
            }
        }

        // Base classes first, each class's own order inside; then the categories, in the order they first appear.
        var ordered = new List<PropertyDef>(hiding.Count);
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            ordered.AddRange(chain[i].Properties.Where(hiding.Contains));
        }

        var firstOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Meta?.Category is { } category)
            {
                firstOf.TryAdd(category, i);
            }
        }

        return [.. ordered.OrderBy(p => p.Meta?.Category is { } c ? firstOf[c] : -1)];
    }

    /// <summary>
    /// Draws <paramref name="target"/> and what it holds. With <paramref name="locked"/> nothing can be written. Returns true when a property was written this frame. Call it every frame while the object is
    /// shown; the object may be destroyed at any time (it then shows <c>(destroyed)</c>).
    /// </summary>
    public bool Draw(IInspectorUi ui, EngineObject target, bool locked = false)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(target);
        if (!ReferenceEquals(_root, target))
        {
            _root = target;
            _errors.Clear();
            _pages.Clear();
            _multiline.Clear();
        }

        _written = 0;
        _ancestors.Clear();
        DrawObject(ui, target, "", 0, locked);
        return _written > 0;
    }

    /// <summary>Ends the gesture: the next edit is a step of its own.</summary>
    public void Seal() => _history?.Seal();

    /// <summary>Forgets the messages of refused values.</summary>
    public void ClearErrors() => _errors.Clear();

    /// <summary>
    /// Writes <paramref name="after"/> to the property: as a command on the history when there is one (<paramref name="before"/> is what undo puts back), else straight to the setter. False when it was not
    /// written: the object is destroyed, the property has no setter, or the setter threw; its message is then in <see cref="Errors"/> under <paramref name="path"/> and nothing was recorded.
    /// </summary>
    public bool SetProperty(EngineObject target, PropertyDef property, Value before, Value after, string path)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        if (target.IsDestroyed)
        {
            Fail(path, "The object was destroyed.");
            return false;
        }

        if (property.Setter is null)
        {
            Fail(path, "The property is read-only.");
            return false;
        }

        try
        {
            if (_history is not null)
            {
                _history.Execute(new SetPropertyCommand(target, property, before, after));
            }
            else
            {
                property.Setter(target, in after);
            }
        }
        catch (Exception ex)
        {
            Fail(path, Describe(ex));
            return false;
        }

        _errors.Remove(path);
        _written++;
        return true;
    }

    /// <summary>The default look of an object: the properties in order, under their category headings. What <see cref="ObjectEdit.DrawProperties"/> calls. True when a property was written.</summary>
    public bool DrawProperties(in ObjectEdit edit)
    {
        int before = _written;
        string? category = null;
        if (!_properties.TryGetValue(edit.Class, out PropertyDef[]? properties))
        {
            _properties[edit.Class] = properties = [.. PropertiesOf(edit.Class)];
        }

        foreach (PropertyDef property in properties)
        {
            if (property.Meta?.Hidden == true)
            {
                continue;
            }

            if (property.Meta?.Category is { } own && own != category)
            {
                category = own;
                edit.Ui.Heading(own);
            }

            DrawProperty(in edit, property);
        }

        return _written > before;
    }

    private void DrawObject(IInspectorUi ui, EngineObject target, string path, int depth, bool locked)
    {
        if (target.IsDestroyed)
        {
            ui.Text("(destroyed)");
            return;
        }

        _ancestors.Add(target);
        try
        {
            var edit = new ObjectEdit(target, target.Class, ui, this, path, depth, locked);
            if (_drawers.FindClass(edit.Class) is { } custom)
            {
                try
                {
                    custom(in edit);
                }
                catch (Exception ex)
                {
                    ui.ErrorText($"The drawer of {edit.Class.Name} failed: {Describe(ex)}");
                }
            }
            else
            {
                DrawProperties(in edit);
            }
        }
        finally
        {
            _ancestors.Remove(target);
        }
    }

    private void DrawProperty(in ObjectEdit edit, PropertyDef property)
    {
        IInspectorUi ui = edit.Ui;
        string path = edit.Path.Length == 0 ? property.ScriptName : edit.Path + "/" + property.ScriptName;
        ui.PushId(property.ScriptName);
        try
        {
            Value current;
            try
            {
                current = property.Getter(edit.Target);
            }
            catch (Exception ex)
            {
                ui.Text($"{property.ScriptName}: (cannot be read: {Describe(ex)})");
                return;
            }

            bool editable = !edit.Locked && property.Setter is not null && property.Meta?.ReadOnly != true;
            bool multiline = _drawers.IsMultiline(edit.Class, property.ScriptName);
            var gesture = default(Gesture);
            bool changed;
            Value edited;
            ui.BeginDisabled(!editable);
            try
            {
                changed = DrawValue(ui, property.ScriptName, property.Type, in current, editable, property.Meta, path, edit.Depth, edit.Locked, multiline, ref gesture, out edited);
            }
            catch (Exception ex)
            {
                ui.Text($"{property.ScriptName}: (cannot be shown: {Describe(ex)})");
                changed = false;
                edited = default;
            }
            finally
            {
                ui.EndDisabled();
            }

            if (changed && editable)
            {
                SetProperty(edit.Target, property, current, edited, path);
            }

            if (gesture.Done || (changed && !gesture.Active))
            {
                Seal();
            }

            if (property.Doc is { } doc)
            {
                ui.Tooltip(doc);
            }

            if (_errors.TryGetValue(path, out string? error))
            {
                ui.ErrorText(error);
            }
        }
        finally
        {
            ui.PopId();
        }
    }

    /// <summary>
    /// Draws one value, of any property or list item, by its type. True when it was edited, with the new value in <paramref name="edited"/>; what a nested object changes it writes itself. The
    /// exceptions a value of the wrong kind throws are the caller's to catch.
    /// </summary>
    private bool DrawValue(IInspectorUi ui, string label, TypeRef declared, in Value value, bool editable, InspectorMeta? meta, string path, int depth, bool locked, bool multiline, ref Gesture gesture, out Value edited)
    {
        edited = value;
        TypeRef type = Effective(declared, in value);
        if (value.IsNil && type.Kind != ValueKind.String)
        {
            return DrawMissing(ui, label, type, editable, ref gesture, out edited);
        }

        switch (type.Kind)
        {
            case ValueKind.Bool:
                bool on = value.ToBool();
                bool toggled = ui.Toggle(label, ref on, false);
                gesture.Note(ui);
                edited = Value.Bool(on);
                return toggled;

            case ValueKind.Int:
                Span<double> whole = [value.ToInt64()];
                bool wholeChanged = ui.Drag(label, whole, meta is { Step: > 0 } ? meta.Step : 1, meta?.Min ?? double.NaN, meta?.Max ?? double.NaN, "%.0f", false);
                gesture.Note(ui);
                edited = Value.Int((long)Math.Round(whole[0]));
                return wholeChanged;

            case ValueKind.Float:
                Span<double> number = [value.ToDouble()];
                bool numberChanged = ui.Drag(label, number, meta is { Step: > 0 } ? meta.Step : 0.05, meta?.Min ?? double.NaN, meta?.Max ?? double.NaN, "%.3f", false);
                gesture.Note(ui);
                edited = Value.Float(number[0]);
                return numberChanged;

            case ValueKind.String:
                return DrawString(ui, label, value.IsNil ? "" : value.ToStringValue(), path, multiline, ref gesture, out edited);

            case ValueKind.Vector3:
                Vector3 vector = value.ToVector3();
                if (meta?.Color == true)
                {
                    Span<float> rgb = [vector.X, vector.Y, vector.Z];
                    bool picked = ui.Color(label, rgb, false);
                    gesture.Note(ui);
                    edited = Value.Vector(new Vector3(rgb[0], rgb[1], rgb[2]));
                    return picked;
                }

                Span<double> axes = [vector.X, vector.Y, vector.Z];
                bool moved = ui.Drag(label, axes, meta is { Step: > 0 } ? meta.Step : 0.05, meta?.Min ?? double.NaN, meta?.Max ?? double.NaN, "%.3f", false);
                gesture.Note(ui);
                edited = Value.Vector(new Vector3((float)axes[0], (float)axes[1], (float)axes[2]));
                return moved;

            case ValueKind.Quaternion:
                (float pitch, float yaw, float roll) = FieldCodec.ToEuler(value.ToQuaternion());
                const double Degrees = 180.0 / Math.PI;
                Span<double> angles = [pitch * Degrees, yaw * Degrees, roll * Degrees];
                bool turned = ui.Drag(label, angles, meta is { Step: > 0 } ? meta.Step : 0.5, double.NaN, double.NaN, "%.1f deg", false);
                gesture.Note(ui);
                edited = Value.Quat(FieldCodec.FromEuler((float)(angles[0] / Degrees), (float)(angles[1] / Degrees), (float)(angles[2] / Degrees)));
                return turned;

            case ValueKind.Enum:
                return DrawEnum(ui, label, type, in value, path, ref gesture, out edited);

            case ValueKind.Entity:
                ulong id = value.ToEntity().Id;
                string name = id == 0 ? "None" : _entityName is null ? $"#{id & 0xFFFFFFFF}" : _entityName(new Entity(id)) ?? $"#{id & 0xFFFFFFFF} (gone)";
                bool referenced = ui.EntityReference(label, name, ref id, false);
                gesture.Note(ui);
                edited = Value.Entity(new Entity(id));
                return referenced;

            case ValueKind.Object:
                DrawObjectValue(ui, label, in value, path, depth, locked);
                return false;

            case ValueKind.List:
                return DrawList(ui, label, type, in value, editable && !locked, path, depth, locked, ref gesture, out edited);

            default:
                ui.Text($"{label}: {value}");
                return false;
        }
    }

    // A value that is nil: a text, and for a nullable one with an easy default a button to give it a value.
    private static bool DrawMissing(IInspectorUi ui, string label, TypeRef type, bool editable, ref Gesture gesture, out Value edited)
    {
        edited = default;
        ui.Text($"{label}: {(type.Nullable || type.IsAny ? "none" : "nil")}");
        if (editable && type.Nullable && DefaultOf(type) is { } fresh)
        {
            ui.SameLine();
            bool pressed = ui.Button("Set");
            gesture.Note(ui);
            if (pressed)
            {
                edited = fresh;
                return true;
            }
        }

        return false;
    }

    private bool DrawString(IInspectorUi ui, string label, string text, string path, bool multiline, ref Gesture gesture, out Value edited)
    {
        edited = Value.String(text);
        if (text.Length > MaxEditableText)
        {
            ui.Text($"{label}: ({text.Length} characters, too long to edit here) {text[..80]}...");
            return false;
        }

        if (multiline || text.Contains('\n', StringComparison.Ordinal) || text.Length > MultilineFrom)
        {
            _multiline.Add(path);       // it stays a box, so that the field being typed in does not change under the person's hands
        }

        string typed = text;
        bool changed = ui.TextInput(label, ref typed, _multiline.Contains(path));
        gesture.Note(ui);
        edited = Value.String(typed);
        return changed;
    }

    private static bool DrawEnum(IInspectorUi ui, string label, TypeRef type, in Value value, string path, ref Gesture gesture, out Value edited)
    {
        edited = value;
        EnumInfo? info = value.Kind == ValueKind.Enum ? value.EnumType : type.Name is { } enumName ? TypeRegistry.FindEnum(enumName) : null;
        long raw = value.Kind == ValueKind.Enum ? value.ToEnum() : value.ToInt64();
        if (info is null)
        {
            Span<double> number = [raw];
            bool numeric = ui.Drag(label, number, 1, double.NaN, double.NaN, "%.0f", false);
            gesture.Note(ui);
            edited = Value.Int((long)Math.Round(number[0]));
            return numeric;
        }

        if (info.IsFlags)
        {
            if (!ui.BeginNode(path, $"{label}: {info.GetName(raw)}"))
            {
                return false;
            }

            bool changedFlags = false;
            try
            {
                foreach (EnumMemberInfo member in info.Members)
                {
                    if (member.Value == 0)
                    {
                        continue;
                    }

                    bool on = (raw & member.Value) == member.Value;
                    if (ui.Toggle(member.Name, ref on, false))
                    {
                        raw = on ? raw | member.Value : raw & ~member.Value;
                        changedFlags = true;
                    }

                    gesture.Note(ui);
                }
            }
            finally
            {
                ui.EndNode();
            }

            edited = Value.Enum(info, raw);
            return changedFlags;
        }

        IReadOnlyList<string> names = [.. info.Members.Select(m => m.Name)];
        int selected = -1;
        for (int i = 0; i < info.Members.Count; i++)
        {
            if (info.Members[i].Value == raw)
            {
                selected = i;
                break;
            }
        }

        int picked = ui.Combo(label, info.GetName(raw), names, selected);
        gesture.Note(ui);
        if (picked < 0 || picked >= info.Members.Count || picked == selected)
        {
            return false;
        }

        edited = Value.Enum(info, info.Members[picked].Value);
        return true;
    }

    private void DrawObjectValue(IInspectorUi ui, string label, in Value value, string path, int depth, bool locked)
    {
        EngineObject child;
        try
        {
            child = value.ToObject<EngineObject>();
        }
        catch (ValueException)
        {
            ui.Text($"{label}: (destroyed)");
            return;
        }

        string className = child.Class.Name;
        if (_ancestors.Contains(child))
        {
            ui.Text($"{label}: {className} (shown above: not opened again)");
            return;
        }

        if (depth >= MaxDepth)
        {
            ui.Text($"{label}: {className} (not opened: {MaxDepth} levels deep)");
            return;
        }

        if (!ui.BeginNode(path, $"{label}: {className}"))
        {
            return;
        }

        try
        {
            DrawObject(ui, child, path, depth + 1, locked);
        }
        finally
        {
            ui.EndNode();
        }
    }

    private bool DrawList(IInspectorUi ui, string label, TypeRef type, in Value value, bool editable, string path, int depth, bool locked, ref Gesture gesture, out Value edited)
    {
        edited = value;
        IReadOnlyList<Value> items = value.ToList();
        int count = items.Count;
        TypeRef element = type.Element ?? TypeRef.Any;
        if (!ui.BeginNode(path, $"{label} [{count}]"))
        {
            return false;
        }

        List<Value>? working = null;
        try
        {
            bool canEdit = editable && count <= MaxEditableItems;
            if (editable && !canEdit)
            {
                ui.Text($"{count} items: too many to edit here.");
            }

            int lastPage = Math.Max(0, (count - 1) / PageSize * PageSize);
            int first = Math.Clamp(_pages.GetValueOrDefault(path), 0, lastPage);
            _pages[path] = first;
            if (count > PageSize)
            {
                ui.Text($"Items {first + 1} to {Math.Min(count, first + PageSize)} of {count}");
                ui.SameLine();
                ui.BeginDisabled(first == 0);
                bool previous = ui.Button("Previous page");
                ui.EndDisabled();
                ui.SameLine();
                ui.BeginDisabled(first >= lastPage);
                bool next = ui.Button("Next page");
                ui.EndDisabled();
                if (previous)
                {
                    _pages[path] = Math.Max(0, first - PageSize);
                }
                else if (next)
                {
                    _pages[path] = Math.Min(lastPage, first + PageSize);
                }
            }

            int removeAt = -1;
            int moveFrom = -1;
            int moveBy = 0;
            int end = Math.Min(count, first + PageSize);
            for (int i = first; i < end; i++)
            {
                ui.PushId(i.ToString(CultureInfo.InvariantCulture));
                try
                {
                    Value item = items[i];
                    string itemLabel = $"[{i}]";
                    string itemPath = $"{path}/{i.ToString(CultureInfo.InvariantCulture)}";
                    bool itemChanged;
                    Value itemEdited;
                    ui.BeginDisabled(!canEdit);
                    try
                    {
                        itemChanged = DrawValue(ui, itemLabel, element, in item, canEdit, null, itemPath, depth, locked, false, ref gesture, out itemEdited);
                    }
                    catch (Exception ex)
                    {
                        ui.Text($"{itemLabel}: (cannot be shown: {Describe(ex)})");
                        itemChanged = false;
                        itemEdited = item;
                    }
                    finally
                    {
                        ui.EndDisabled();
                    }

                    if (itemChanged && canEdit)
                    {
                        working ??= [.. items];
                        working[i] = itemEdited;
                    }

                    if (canEdit)
                    {
                        ui.SameLine();
                        ui.BeginDisabled(i == 0);
                        if (ui.Button("Up"))
                        {
                            (moveFrom, moveBy) = (i, -1);
                        }

                        ui.EndDisabled();
                        ui.SameLine();
                        ui.BeginDisabled(i == count - 1);
                        if (ui.Button("Down"))
                        {
                            (moveFrom, moveBy) = (i, 1);
                        }

                        ui.EndDisabled();
                        ui.SameLine();
                        if (ui.Button("Remove"))
                        {
                            removeAt = i;
                        }
                    }
                }
                finally
                {
                    ui.PopId();
                }
            }

            if (canEdit)
            {
                bool add = DefaultOf(element) is not null && ui.Button("Add");
                if (removeAt >= 0)
                {
                    working ??= [.. items];
                    working.RemoveAt(removeAt);
                    gesture.Done = true;
                }
                else if (moveFrom >= 0)
                {
                    working ??= [.. items];
                    (working[moveFrom], working[moveFrom + moveBy]) = (working[moveFrom + moveBy], working[moveFrom]);
                    gesture.Done = true;
                }
                else if (add)
                {
                    working ??= [.. items];
                    working.Add(DefaultOf(element)!.Value);
                    gesture.Done = true;
                }
            }
        }
        finally
        {
            ui.EndNode();
        }

        if (working is null)
        {
            return false;
        }

        edited = Value.List(working);
        return true;
    }

    /// <summary>What an <i>Add</i> puts in a list or a <i>Set</i> in a missing value: the zero of the type; null for a type with no obvious value (an object, a function).</summary>
    private static Value? DefaultOf(TypeRef type) => type.Kind switch
    {
        ValueKind.Bool => Value.Bool(false),
        ValueKind.Int => Value.Int(0),
        ValueKind.Float => Value.Float(0),
        ValueKind.String => Value.String(""),
        ValueKind.Vector3 => Value.Vector(Vector3.Zero),
        ValueKind.Quaternion => Value.Quat(Quaternion.Identity),
        ValueKind.Entity => Value.Entity(default),
        ValueKind.List => Value.List([]),
        ValueKind.Enum when type.Name is { } name && TypeRegistry.FindEnum(name) is { } info => Value.Enum(info, info.Members.Count > 0 ? info.Members[0].Value : 0),
        _ => null,
    };

    // A property declared as any value is drawn as what it holds.
    private static TypeRef Effective(TypeRef declared, in Value value)
    {
        if (!declared.IsAny || value.IsNil)
        {
            return declared;
        }

        return value.Kind switch
        {
            ValueKind.Enum => TypeRef.Enum(value.EnumType.Name),
            ValueKind.List => TypeRef.List(TypeRef.Any),
            ValueKind.Object => TypeRef.Object(""),
            _ => new TypeRef(value.Kind),
        };
    }

    private void Fail(string path, string message)
    {
        if (_errors.Count >= MaxErrors)
        {
            _errors.Clear();
        }

        _errors[path] = message;
    }

    private static string Describe(Exception ex) => string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
}
