using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Apogee.DevTools;
using Apogee.Ecs;
using Apogee.Editor.Editing;
using Apogee.Host.Editing;
using Apogee.Native.CImGui;
using Apogee.Reflection;
using Apogee.Rendering;

namespace Apogee.Editor.Panels;

/// <summary>
/// The components of the selected entity, drawn from the registry's descriptions: a widget for each kind of field (<see cref="FieldCodec"/>), ranges as sliders, categories as headings, hidden fields
/// left out, read-only ones greyed. Every change is a command on the history, and the changes of one drag are one step.
/// </summary>
public sealed unsafe class InspectorPanel
{
    private string _filter = "";
    private string _nameText = "";
    private Entity? _nameFor;

    public void Draw(EditorContext ctx)
    {
        if (!Ui.Begin("Inspector"))
        {
            Ui.End();
            return;
        }

        EditorProject project = ctx.Project;
        World world = project.World;
        if (project.Selection is not { } entity)
        {
            Ui.Text("Nothing selected.");
            Ui.End();
            return;
        }

        bool locked = project.IsPlaying;
        if (_nameFor != entity)
        {
            _nameFor = entity;
            _nameText = world.GetName(entity) ?? "";
        }

        ImGui.BeginDisabled(locked);
        if (Gui.InputText("Name", ref _nameText, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            if (!project.Rename(entity, _nameText))
            {
                _nameText = world.GetName(entity) ?? "";
            }
        }

        ImGui.EndDisabled();
        Ui.Text($"Entity {entity.Id & 0xFFFFFFFF}" + (locked ? "   (playing: values show the game's, stop to edit)" : ""));
        Ui.Separator();

        foreach ((Entity id, ComponentInfo info) in world.ComponentsOf(entity))
        {
            if (info.Name == nameof(EditorOnly))
            {
                continue;
            }

            Gui.PushId((int)(id.Id & 0x7FFFFFFF));
            bool open = Gui.TreeNode(info.Name, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Framed | ImGuiTreeNodeFlags.SpanAvailWidth);
            if (!locked && Gui.BeginContextMenu())
            {
                if (Gui.MenuItem("Remove component", null, false, info.Name != nameof(Transform)))
                {
                    project.History.Execute(new RemoveComponentCommand(project.WorldRef, entity, id));
                    project.History.Seal();
                }

                Gui.EndPopup();
            }

            if (open)
            {
                if (info.IsTag)
                {
                    Ui.Text("(a tag: it has no fields)");
                }
                else
                {
                    DrawFields(ctx, entity, id, info, locked);
                }

                Gui.TreePop();
            }

            Gui.PopId();
        }

        Ui.Separator();
        ImGui.BeginDisabled(locked);
        if (Gui.Button("Add component", new System.Numerics.Vector2(-1, 0)))
        {
            Gui.OpenPopup("add_component");
        }

        ImGui.EndDisabled();
        DrawAddPopup(ctx, entity);
        Ui.End();
    }

    private void DrawAddPopup(EditorContext ctx, Entity entity)
    {
        if (!Gui.BeginPopup("add_component"))
        {
            return;
        }

        EditorProject project = ctx.Project;
        World world = project.World;
        Gui.InputText("##filter", ref _filter);
        HashSet<string> has = [.. world.ComponentsOf(entity).Select(c => c.Info.Name)];
        var candidates = new SortedDictionary<string, ComponentInfo>(StringComparer.Ordinal);
        foreach (ComponentInfo info in TypeRegistry.Components.Concat(world.Components))
        {
            if (!has.Contains(info.Name) && info.Name is not (nameof(WorldTransform) or nameof(EditorOnly)) && info.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            {
                candidates[info.Name] = info;
            }
        }

        foreach (ComponentInfo info in candidates.Values)
        {
            if (Gui.MenuItem(info.Name))
            {
                Entity id = world.RegisterComponent(info);
                project.History.Execute(new AddComponentCommand(project.WorldRef, entity, id, InitialValue(info)));
                project.History.Seal();
                Gui.ClosePopup();
            }
        }

        Gui.EndPopup();
    }

    /// <summary>What a component starts as when it is added: the engine's components have a usable default (a transform with a scale of zero has no size), the rest are zero.</summary>
    private static byte[]? InitialValue(ComponentInfo info)
    {
        switch (info.Name)
        {
            case nameof(Transform):
                Transform transform = Transform.Identity;
                return MemoryMarshal.AsBytes(new ReadOnlySpan<Transform>(in transform)).ToArray();
            case nameof(CameraComponent):
                CameraComponent camera = CameraComponent.Default;
                return MemoryMarshal.AsBytes(new ReadOnlySpan<CameraComponent>(in camera)).ToArray();
            case nameof(Light):
                Light light = Light.Point(System.Numerics.Vector3.One, 80f, 12f);
                return MemoryMarshal.AsBytes(new ReadOnlySpan<Light>(in light)).ToArray();
            default:
                return null;
        }
    }

    private void DrawFields(EditorContext ctx, Entity entity, Entity componentId, ComponentInfo info, bool locked)
    {
        EditorProject project = ctx.Project;
        World world = project.World;
        string? category = null;

        // Fields with the same category are drawn together, under a heading, in the order the categories first appear.
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
                Gui.SeparatorText(fieldCategory);
            }

            byte* current = world.GetPointer(entity, componentId);
            if (current is null)
            {
                return;
            }

            byte[] bytes = new ReadOnlySpan<byte>(current, info.Size).ToArray();
            bool changed;
            Gui.PushId(field.Name);
            ImGui.BeginDisabled(locked || meta?.ReadOnly == true);
            fixed (byte* at = &bytes[field.Offset])
            {
                changed = info.Name == nameof(ModelRenderer) && field.Name == nameof(ModelRenderer.Model)
                    ? EditModel(project, at)
                    : field.Kind == FieldKind.Entity ? EditReference(project, field.Name, at) : EditNumbers(field, at, meta);
            }

            bool done = ImGui.IsItemDeactivatedAfterEdit() || (changed && !ImGui.IsItemActive());
            ImGui.EndDisabled();
            Gui.PopId();

            if (changed)
            {
                SetComponentCommand? command = SetComponentCommand.Create(project.WorldRef, entity, componentId, bytes, $"Edit {info.Name}.{field.Name}");
                if (command is not null)
                {
                    project.History.Execute(command);
                }
            }

            if (done)
            {
                project.History.Seal();
            }

            if (field.Doc is { } doc)
            {
                Gui.Tooltip(doc);
            }
        }
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

    private static bool EditNumbers(FieldDef field, byte* at, InspectorMeta? meta)
    {
        FieldWidget widget = FieldCodec.WidgetOf(field.Kind);
        double[] values = FieldCodec.Read(at, field.Kind);
        bool changed;
        switch (widget)
        {
            case FieldWidget.Toggle:
                bool on = values[0] != 0;
                changed = Gui.Checkbox(field.Name, ref on);
                values[0] = on ? 1 : 0;
                break;
            case FieldWidget.WholeNumber:
                changed = Gui.DragDoubles(field.Name, values, meta is { Step: > 0 } ? meta.Step : 1, meta?.Min ?? double.NaN, meta?.Max ?? double.NaN, "%.0f");
                break;
            case FieldWidget.Rotation:
                changed = Gui.DragDoubles(field.Name, values, meta is { Step: > 0 } ? meta.Step : 0.5, double.NaN, double.NaN, "%.1f deg");
                break;
            default:
                changed = Gui.DragDoubles(field.Name, values, meta is { Step: > 0 } ? meta.Step : 0.05, meta?.Min ?? double.NaN, meta?.Max ?? double.NaN, "%.3f");
                break;
        }

        if (changed)
        {
            FieldCodec.Write(at, field.Kind, values);
        }

        return changed;
    }

    private static bool EditReference(EditorProject project, string label, byte* at)
    {
        ulong id = *(ulong*)at;
        var target = new Entity(id);
        string text = id != 0 && project.World.IsAlive(target) ? project.World.GetName(target) ?? $"#{id & 0xFFFFFFFF}" : "None";
        bool changed = false;
        Gui.Button($"{text}##ref", new System.Numerics.Vector2(-60, 0));
        if (Gui.BeginDropTarget())
        {
            if (Gui.AcceptId("ENTITY") is { } dropped)
            {
                *(ulong*)at = dropped;
                changed = true;
            }

            Gui.EndDropTarget();
        }

        Gui.SameLine();
        if (Gui.SmallButton("Clear"))
        {
            *(ulong*)at = 0;
            changed = true;
        }

        Gui.SameLine();
        Ui.Text(label);
        return changed;
    }

    private static bool EditModel(EditorProject project, byte* at)
    {
        uint model = *(uint*)at;
        string path = project.Session.Models.PathOf(model) ?? "";
        bool changed = false;
        if (Gui.InputText("Model", ref path, ImGuiInputTextFlags.EnterReturnsTrue) || DroppedModel(ref path))
        {
            try
            {
                *(uint*)at = path.Length == 0 ? 0 : project.Session.Models.Load(path);
                changed = true;
            }
            catch (System.IO.FileNotFoundException e)
            {
                Apogee.Core.Logging.Log.Warn("Editor", e.Message);
            }
        }

        return changed;
    }

    private static bool DroppedModel(ref string path)
    {
        bool dropped = false;
        if (Gui.BeginDropTarget())
        {
            if (Gui.AcceptText("ASSET") is { } text && (text.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || text.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)))
            {
                path = text;
                dropped = true;
            }

            Gui.EndDropTarget();
        }

        return dropped;
    }
}
