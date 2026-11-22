using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Apogee.Ecs;
using Apogee.Editor.Inspector;
using Apogee.Host.Editing;
using Apogee.Reflection;
using Apogee.Rendering;
using Xunit;

namespace Apogee.Editor.Tests;

/// <summary>A project, an inspector over it, and a fake UI to draw with: the inspector without ImGui.</summary>
internal sealed unsafe class InspectorRig : IDisposable
{
    private readonly ProjectRig _rig = new();

    public InspectorRig(DrawerTable? table = null)
    {
        RuntimeHelpersInit();
        Editor = new InspectorEditor(Project);
        View = new InspectorView(Project, table);
        LightId = World.Component<Light>();
        LightInfo = World.GetComponentInfo(LightId)!;
    }

    public EditorProject Project => _rig.Project;

    public World World => _rig.World;

    public InspectorEditor Editor { get; }

    public InspectorView View { get; }

    public FakeInspectorUi Ui { get; } = new();

    public Entity LightId { get; }

    public ComponentInfo LightInfo { get; }

    public IReadOnlyList<Entity> Targets => Editor.Targets;

    private static void RuntimeHelpersInit() => System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(Light).Module.ModuleHandle);

    public Entity MakeLight(string name, float intensity, float range, Vector3? color = null, byte kind = 1)
    {
        Entity entity = World.CreateEntity(name);
        World.Set(entity, new Light { Color = color ?? Vector3.One, Intensity = intensity, Range = range, Kind = kind });
        return entity;
    }

    /// <summary>Selects the entities, with the last one active unless one is named.</summary>
    public void Select(IEnumerable<Entity> entities, Entity? active = null) => Project.Selected.Set(entities, active);

    /// <summary>One frame of the Light component of <paramref name="active"/>, with whatever the fake UI was told to do; forgets the script afterwards.</summary>
    public void Draw(Entity active, bool locked = false, ComponentInfo? info = null, Entity? id = null)
    {
        View.DrawComponent(Ui, Targets, active, id ?? LightId, info ?? LightInfo, locked);
        Ui.NextFrame();
    }

    /// <summary>One frame, keeping what was drawn.</summary>
    public void DrawKeeping(Entity active, bool locked = false, ComponentInfo? info = null, Entity? id = null) =>
        View.DrawComponent(Ui, Targets, active, id ?? LightId, info ?? LightInfo, locked);

    public Light LightOf(Entity entity) => World.Get<Light>(entity);

    public Entity Register(ComponentInfo info) => World.RegisterComponent(info);

    public void Poke(Entity entity, Entity id, byte[] bytes)
    {
        fixed (byte* data = bytes)
        {
            World.SetPointer(entity, id, bytes.Length, data);
        }
    }

    public byte[] Peek(Entity entity, Entity id, int size) => new ReadOnlySpan<byte>(World.GetPointer(entity, id), size).ToArray();

    public void ForgetHistory() => Project.History.Clear();

    public void Dispose() => _rig.Dispose();
}

/// <summary>The inspector's drawer table, field drawers, defaults and multi-edit, driven through a fake UI (no ImGui, no window).</summary>
[Collection("editor")]
public sealed class InspectorTests
{
    private static void RuntimeHelpersInit() => System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(Light).Module.ModuleHandle);

    private static ComponentInfo Component(string name, params FieldDef[] fields)
    {
        int size = fields.Length == 0 ? 0 : (fields.Max(f => f.Offset + f.Kind.SizeOf()) + 7) / 8 * 8;
        return new ComponentInfo(name, null, size, 8, fields);
    }

    private static void Enum(string name, bool flags, params (string Name, long Value)[] members) =>
        TypeRegistry.RegisterIfMissing(new EnumInfo(name, flags, [.. members.Select(m => new EnumMemberInfo(m.Name, m.Value))]));

    private static bool Same(FieldDrawer? a, FieldDrawer b) => a is not null && a == b;

    // ---- The table ----

    [Fact]
    public void LookupPrefersComponentThenFieldThenAssetTypeThenKind()
    {
        Enum("InspLookupEnum", false, ("a", 0), ("b", 1));
        ComponentInfo info = Component(
            "InspLookup",
            new FieldDef("Plain", FieldKind.Float32, 0),
            new FieldDef("Ref", FieldKind.Entity, 8),
            new FieldDef("Handle", FieldKind.AssetRef, 16, AssetType: "inspTexture"),
            new FieldDef("Kind", FieldKind.UInt8, 20, Enum: "InspLookupEnum"),
            new FieldDef("Tint", FieldKind.Vector3, 24, Meta: new InspectorMeta(Color: true)),
            new FieldDef("Ghost", FieldKind.UInt8, 21, Enum: "InspNoSuchEnum"));
        FieldDef plain = info.FindField("Plain")!;
        FieldDef reference = info.FindField("Ref")!;
        FieldDef handle = info.FindField("Handle")!;
        FieldDef kind = info.FindField("Kind")!;
        FieldDef tint = info.FindField("Tint")!;
        FieldDef ghost = info.FindField("Ghost")!;

        var table = DrawerTable.CreateDefault();

        // The defaults: first what the registry says (an enum, a colour), then the kind.
        DrawerChoice choice = table.Find(info, plain);
        Assert.Equal(DrawerLevel.Kind, choice.Level);
        Assert.True(Same(choice.Field, BuiltInDrawers.Numbers));
        Assert.True(Same(table.Find(info, reference).Field, BuiltInDrawers.EntityReference));
        Assert.True(Same(table.Find(info, handle).Field, BuiltInDrawers.Numbers));           // until a picker is registered
        Assert.Equal(DrawerLevel.Hint, table.Find(info, kind).Level);
        Assert.True(Same(table.Find(info, kind).Field, BuiltInDrawers.Enum));
        Assert.Equal(DrawerLevel.Hint, table.Find(info, tint).Level);
        Assert.True(Same(table.Find(info, tint).Field, BuiltInDrawers.Color));
        Assert.Equal(DrawerLevel.Kind, table.Find(info, ghost).Level);                        // an enum nobody registered is only a number

        // An asset type beats the defaults ...
        FieldDrawer byAsset = (in FieldEdit _) => false;
        table.RegisterAssetType("inspTexture", byAsset);
        choice = table.Find(info, handle);
        Assert.Equal(DrawerLevel.AssetType, choice.Level);
        Assert.Same(byAsset, choice.Field);

        // ... a field beats the asset type and a hint ...
        FieldDrawer byField = (in FieldEdit _) => false;
        table.RegisterField("InspLookup", "Handle", byField);
        table.RegisterField("InspLookup", "Kind", byField);
        Assert.Equal(DrawerLevel.Field, table.Find(info, handle).Level);
        Assert.Same(byField, table.Find(info, handle).Field);
        Assert.Same(byField, table.Find(info, kind).Field);
        Assert.Equal(DrawerLevel.Kind, table.Find(info, plain).Level);                       // another field of the same component is not affected

        // ... and a drawer of the whole component beats all of them.
        ComponentDrawer whole = (in ComponentEdit _) => false;
        table.RegisterComponent("InspLookup", whole);
        foreach (FieldDef field in info.Fields)
        {
            choice = table.Find(info, field);
            Assert.Equal(DrawerLevel.Component, choice.Level);
            Assert.Same(whole, choice.Component);
            Assert.Null(choice.Field);
        }

        Assert.Same(whole, table.FindComponent("InspLookup"));
        Assert.Null(table.FindComponent("Light"));

        // Registering again for the same key replaces the drawer.
        FieldDrawer newer = (in FieldEdit _) => true;
        table.RegisterAssetType("inspTexture", newer);
        var second = new DrawerTable();
        second.RegisterAssetType("inspTexture", byAsset);
        second.RegisterAssetType("inspTexture", newer);
        Assert.Same(newer, second.Find(info, handle).Field);
    }

    [Fact]
    public void TheModelRendererModelIsAFieldDrawerThatThePickerWillReplace()
    {
        RuntimeHelpersInit();
        var table = DrawerTable.CreateDefault();
        ComponentInfo info = TypeRegistry.GetComponent<ModelRenderer>();
        DrawerChoice choice = table.Find(info, info.FindField("Model")!);

        Assert.Equal(DrawerLevel.Field, choice.Level);
        Assert.True(Same(choice.Field, BuiltInDrawers.ModelPath));
    }

    [Fact]
    public void ARegistrationOnTheSharedTableIsSeenByTheInspector()
    {
        ComponentInfo info = Component("InspShared", new FieldDef("V", FieldKind.Float32, 0));
        FieldDrawer drawer = (in FieldEdit _) => false;
        InspectorDrawers.RegisterField("InspShared", "V", drawer);

        Assert.Same(drawer, InspectorDrawers.Shared.Find(info, info.Fields[0]).Field);
        Assert.Equal(DrawerLevel.Field, InspectorDrawers.Shared.Find(info, info.Fields[0]).Level);
    }

    // ---- The drawers ----

    [Fact]
    public void NumbersAreDrawnAsTheyAlwaysWere()
    {
        using var rig = new InspectorRig();
        Entity a = rig.MakeLight("a", 80, 12);
        rig.Select([a]);

        rig.DrawKeeping(a);

        Assert.Equal(["Light", "Spot cone"], rig.Ui.Headings);                 // grouped by category, in the order they first appear
        Assert.Equal(["Color", "Intensity", "Range", "Kind", "InnerCone", "OuterCone"], rig.Ui.Drawn.Select(w => w.Label));

        DrawnWidget intensity = rig.Ui.Single("drag", "Intensity");
        Assert.Equal([80.0], intensity.Numbers);
        Assert.Equal(10, intensity.Speed);                                     // the field's Step
        Assert.Equal(0, intensity.Min);
        Assert.True(double.IsNaN(intensity.Max));
        Assert.Equal("%.3f", intensity.Format);
        Assert.False(intensity.Mixed);
        Assert.False(intensity.Disabled);

        DrawnWidget cone = rig.Ui.Single("drag", "InnerCone");
        Assert.Equal((0.01, 0.0, 1.5707964), (cone.Speed, cone.Min, cone.Max));
    }

    [Fact]
    public void WholeNumbersTogglesRotationsAndVectorsKeepTheirWidgets()
    {
        using var rig = new InspectorRig();
        ComponentInfo info = Component(
            "InspNumbers",
            new FieldDef("Count", FieldKind.Int32, 0),
            new FieldDef("On", FieldKind.Bool, 4),
            new FieldDef("Turn", FieldKind.Quaternion, 8),
            new FieldDef("Where", FieldKind.Double3, 24),
            new FieldDef("Handle", FieldKind.AssetRef, 48, AssetType: "inspNone"));
        Entity id = rig.Register(info);
        Entity e = rig.World.CreateEntity("e");
        byte[] bytes = new byte[info.Size];
        BitConverter.GetBytes(7).CopyTo(bytes, 0);
        bytes[4] = 1;
        BitConverter.GetBytes(1f).CopyTo(bytes, 20);           // the identity rotation: w = 1
        BitConverter.GetBytes(2.5).CopyTo(bytes, 24);
        BitConverter.GetBytes(9u).CopyTo(bytes, 48);
        rig.Poke(e, id, bytes);
        rig.Select([e]);

        rig.DrawKeeping(e, info: info, id: id);

        DrawnWidget count = rig.Ui.Single("drag", "Count");
        Assert.Equal([7.0], count.Numbers);
        Assert.Equal((1.0, "%.0f"), (count.Speed, count.Format));
        Assert.Equal([1.0], rig.Ui.Single("toggle", "On").Numbers);
        Assert.Equal(("%.1f deg", 0.5), (rig.Ui.Single("drag", "Turn").Format, rig.Ui.Single("drag", "Turn").Speed));
        Assert.Equal([2.5, 0, 0], rig.Ui.Single("drag", "Where").Numbers);
        DrawnWidget handle = rig.Ui.Single("drag", "Handle");        // an asset handle is still a plain number until the picker lands
        Assert.Equal([9.0], handle.Numbers);
        Assert.Equal("%.0f", handle.Format);
    }

    [Fact]
    public void AnEnumFieldIsAComboOfItsMembersAndAnUnknownValueShowsItsNumber()
    {
        using var rig = new InspectorRig();
        Entity a = rig.MakeLight("a", 1, 1, kind: 1);
        rig.Select([a]);

        rig.DrawKeeping(a);
        DrawnWidget combo = rig.Ui.Single("combo", "Kind");
        Assert.Equal(("point", 1), (combo.Text, combo.Selected));
        Assert.Equal(["directional", "point", "spot"], combo.Items);

        // Picking a member writes its value.
        rig.Ui.NextFrame();
        rig.Ui.PickIndex["Kind"] = 2;
        rig.ForgetHistory();
        rig.Draw(a);
        Assert.Equal((byte)2, rig.LightOf(a).Kind);
        Assert.Equal(["Edit Light.Kind"], rig.Project.History.Steps);

        // Picking the member that is already there changes nothing.
        rig.Ui.PickIndex["Kind"] = 2;
        rig.Draw(a);
        Assert.Single(rig.Project.History.Steps);

        // A value no member has (a scene from a newer engine, a script) shows as its number and stays until the person picks a member.
        Entity odd = rig.MakeLight("odd", 1, 1, kind: 7);
        rig.Select([odd]);
        rig.DrawKeeping(odd);
        DrawnWidget unknown = rig.Ui.Single("combo", "Kind");
        Assert.Equal(("7", -1), (unknown.Text, unknown.Selected));
        rig.Ui.NextFrame();
        Assert.Equal((byte)7, rig.LightOf(odd).Kind);
    }

    [Fact]
    public void AFlagsEnumHasACheckboxForEachFlagAndKeepsTheBitsItDoesNotKnow()
    {
        Enum("InspParts", true, ("none", 0), ("head", 1), ("arm", 2), ("leg", 4));
        using var rig = new InspectorRig();
        ComponentInfo info = Component("InspFlags", new FieldDef("Parts", FieldKind.UInt32, 0, Enum: "InspParts"));
        Entity id = rig.Register(info);
        Entity e = rig.World.CreateEntity("e");
        rig.Poke(e, id, BitConverter.GetBytes(0x81u).Concat(new byte[4]).ToArray());      // head and a bit nobody named
        rig.Select([e]);

        rig.DrawKeeping(e, info: info, id: id);
        Assert.Equal("Parts: 129", rig.Ui.Single("node", "Parts").Text);                    // a value that is not a combination of members shows its number
        Assert.Equal(["head", "arm", "leg"], rig.Ui.Drawn.Where(w => w.Kind == "toggle").Select(w => w.Label));   // none has no bit
        Assert.Equal([1.0], rig.Ui.Single("toggle", "head").Numbers);
        Assert.Equal([0.0], rig.Ui.Single("toggle", "arm").Numbers);

        rig.Ui.NextFrame();
        rig.Ui.ToggleTo["arm"] = true;
        rig.Draw(e, info: info, id: id);
        Assert.Equal(0x83u, BitConverter.ToUInt32(rig.Peek(e, id, info.Size)));             // arm set, the unknown bit kept

        rig.Ui.ToggleTo["head"] = false;
        rig.Draw(e, info: info, id: id);
        Assert.Equal(0x82u, BitConverter.ToUInt32(rig.Peek(e, id, info.Size)));

        // A closed node draws no checkboxes and changes nothing.
        rig.Ui.NodesOpen = false;
        rig.DrawKeeping(e, info: info, id: id);
        Assert.DoesNotContain(rig.Ui.Drawn, w => w.Kind == "toggle");
        Assert.Equal("Parts: 130", rig.Ui.Single("node", "Parts").Text);
    }

    [Fact]
    public void AColourKeepsValuesOutsideZeroToOne()
    {
        using var rig = new InspectorRig();
        Entity a = rig.MakeLight("a", 1, 1, new Vector3(2.5f, -0.5f, 1f));
        rig.Select([a]);
        rig.ForgetHistory();

        rig.DrawKeeping(a);
        DrawnWidget color = rig.Ui.Single("color", "Color");
        Assert.Equal([2.5, -0.5, 1.0], color.Numbers);                      // shown as they are, not clamped
        Assert.Empty(rig.Project.History.Steps);                            // and drawing alone changes nothing

        rig.Ui.NextFrame();
        rig.Ui.ColorTo["Color"] = [4f, 0.25f, -3f];
        rig.Draw(a);
        Assert.Equal(new Vector3(4f, 0.25f, -3f), rig.LightOf(a).Color);
        Assert.Equal(1f, rig.LightOf(a).Intensity);                         // only the colour's bytes
    }

    [Fact]
    public void AnEntityReferenceShowsTheNameAndTakesADroppedEntity()
    {
        using var rig = new InspectorRig();
        ComponentInfo info = Component("InspRef", new FieldDef("Target", FieldKind.Entity, 0));
        Entity id = rig.Register(info);
        Entity target = rig.World.CreateEntity("Target");
        Entity other = rig.World.CreateEntity("Other");
        Entity holder = rig.World.CreateEntity("holder");
        rig.Poke(holder, id, BitConverter.GetBytes(target.Id));
        rig.Select([holder]);

        rig.DrawKeeping(holder, info: info, id: id);
        Assert.Equal("Target", rig.Ui.Single("entity", "Target").Text);

        rig.Ui.NextFrame();
        rig.Ui.EntityTo["Target"] = other.Id;
        rig.Draw(holder, info: info, id: id);
        Assert.Equal(other.Id, BitConverter.ToUInt64(rig.Peek(holder, id, 8)));

        rig.Ui.EntityTo["Target"] = 0;                                      // Clear
        rig.Draw(holder, info: info, id: id);
        rig.DrawKeeping(holder, info: info, id: id);
        Assert.Equal("None", rig.Ui.Single("entity", "Target").Text);

        // An entity that is gone is "None" too.
        rig.Ui.NextFrame();
        Entity gone = rig.World.CreateEntity("gone");
        rig.Poke(holder, id, BitConverter.GetBytes(gone.Id));
        rig.World.Destroy(gone);
        rig.DrawKeeping(holder, info: info, id: id);
        Assert.Equal("None", rig.Ui.Single("entity", "Target").Text);
    }

    [Fact]
    public void HiddenFieldsAreLeftOutReadOnlyOnesAreGreyedAndADocIsATooltip()
    {
        using var rig = new InspectorRig();
        ComponentInfo info = Component(
            "InspMeta",
            new FieldDef("Shown", FieldKind.Float32, 0, Doc: "what it says"),
            new FieldDef("Secret", FieldKind.Float32, 4, Meta: new InspectorMeta(Hidden: true)),
            new FieldDef("Fixed", FieldKind.Float32, 8, Meta: new InspectorMeta(ReadOnly: true)));
        Entity id = rig.Register(info);
        Entity e = rig.World.CreateEntity("e");
        rig.Poke(e, id, new byte[info.Size]);
        rig.Select([e]);
        rig.ForgetHistory();

        rig.Ui.DragTo["Fixed"] = [5];
        rig.DrawKeeping(e, info: info, id: id);

        Assert.Equal(["Shown", "Fixed"], rig.Ui.Drawn.Select(w => w.Label));
        Assert.False(rig.Ui.Single("drag", "Shown").Disabled);
        Assert.True(rig.Ui.Single("drag", "Fixed").Disabled);
        Assert.Equal(["what it says"], rig.Ui.Tooltips);
        Assert.Empty(rig.Project.History.Steps);                        // a greyed field cannot be written even if the widget said so
    }

    [Fact]
    public void WhilePlayingEverythingIsGreyedAndNothingIsWritten()
    {
        using var rig = new InspectorRig();
        Entity a = rig.MakeLight("a", 1, 1);
        rig.Select([a]);
        rig.ForgetHistory();

        rig.Ui.DragTo["Intensity"] = [50];
        rig.DrawKeeping(a, locked: true);

        Assert.All(rig.Ui.Drawn, w => Assert.True(w.Disabled));
        Assert.Equal(1f, rig.LightOf(a).Intensity);
        Assert.Empty(rig.Project.History.Steps);
    }

    // ---- Several entities ----

    [Fact]
    public void AnEditOfSeveralEntitiesChangesOnlyThatField()
    {
        using var rig = new InspectorRig();
        Entity a = rig.MakeLight("a", 10, 1);
        Entity b = rig.MakeLight("b", 20, 2);
        Entity c = rig.MakeLight("c", 30, 3);
        rig.Select([a, b, c], b);
        rig.ForgetHistory();

        // The frames of one drag: held for two, then let go.
        rig.Ui.DragTo["Intensity"] = [50];
        rig.Ui.HeldOn.Add("Intensity");
        rig.Draw(b);
        rig.Ui.DragTo["Intensity"] = [60];
        rig.Ui.HeldOn.Add("Intensity");
        rig.Draw(b);
        rig.Ui.DragTo["Intensity"] = [70];
        rig.Ui.ReleasedOn.Add("Intensity");
        rig.Draw(b);

        Assert.Equal([70f, 70f, 70f], new[] { a, b, c }.Select(e => rig.LightOf(e).Intensity));
        Assert.Equal([1f, 2f, 3f], new[] { a, b, c }.Select(e => rig.LightOf(e).Range));          // each keeps its own values for the other fields
        Assert.Equal(["Edit Light.Intensity"], rig.Project.History.Steps);                          // the whole drag is one step

        rig.Project.History.Undo();
        Assert.Equal([10f, 20f, 30f], new[] { a, b, c }.Select(e => rig.LightOf(e).Intensity));
        rig.Project.History.Redo();
        Assert.Equal([70f, 70f, 70f], new[] { a, b, c }.Select(e => rig.LightOf(e).Intensity));

        // The next gesture is a step of its own.
        rig.Ui.DragTo["Range"] = [9];
        rig.Draw(b);
        Assert.Equal(2, rig.Project.History.Steps.Count);
        Assert.Equal([9f, 9f, 9f], new[] { a, b, c }.Select(e => rig.LightOf(e).Range));
    }

    [Fact]
    public void AnEditOfOneEntityChangesOnlyIt()
    {
        using var rig = new InspectorRig();
        Entity a = rig.MakeLight("a", 10, 1);
        Entity b = rig.MakeLight("b", 20, 2);
        rig.Select([b]);                       // a is not selected
        rig.ForgetHistory();

        rig.Ui.DragTo["Intensity"] = [33];
        rig.Draw(b);

        Assert.Equal((10f, 33f), (rig.LightOf(a).Intensity, rig.LightOf(b).Intensity));
        Assert.Single(rig.Project.History.Steps);
    }

    [Fact]
    public void MixedIsSetWhenTheValuesDiffer()
    {
        using var rig = new InspectorRig();
        Entity a = rig.MakeLight("a", 10, 5, new Vector3(1, 0, 0));
        Entity b = rig.MakeLight("b", 20, 5, new Vector3(1, 0, 0));
        rig.Select([a, b], a);

        rig.DrawKeeping(a);

        Assert.True(rig.Ui.Single("drag", "Intensity").Mixed);
        Assert.Equal([10.0], rig.Ui.Single("drag", "Intensity").Numbers);        // the widget holds the active entity's value
        Assert.False(rig.Ui.Single("drag", "Range").Mixed);                      // the same on both
        Assert.False(rig.Ui.Single("color", "Color").Mixed);

        // The kind is the same on both, then not.
        Assert.Equal("point", rig.Ui.Single("combo", "Kind").Text);
        rig.Ui.NextFrame();
        Entity c = rig.MakeLight("c", 10, 5, kind: 2);
        rig.Select([a, c], a);
        rig.DrawKeeping(a);
        Assert.Equal("---", rig.Ui.Single("combo", "Kind").Text);
        Assert.Equal(-1, rig.Ui.Single("combo", "Kind").Selected);

        // A single entity is never mixed.
        rig.Ui.NextFrame();
        rig.Select([a]);
        rig.DrawKeeping(a);
        Assert.All(rig.Ui.Drawn, w => Assert.False(w.Mixed));
    }

    [Fact]
    public void PickingAMemberOfAMixedEnumSetsItOnEveryEntity()
    {
        using var rig = new InspectorRig();
        Entity a = rig.MakeLight("a", 1, 1, kind: 0);
        Entity b = rig.MakeLight("b", 1, 1, kind: 2);
        rig.Select([a, b], a);
        rig.ForgetHistory();

        rig.Ui.PickIndex["Kind"] = 0;           // the active one already is directional, but the others are not
        rig.Draw(a);

        Assert.Equal([(byte)0, (byte)0], new[] { a, b }.Select(e => rig.LightOf(e).Kind));
    }

    [Fact]
    public void OnlyTheComponentsAllSelectedEntitiesHaveAreShown()
    {
        using var rig = new InspectorRig();
        Entity both1 = rig.MakeLight("both1", 1, 1);
        Entity both2 = rig.MakeLight("both2", 1, 1);
        Entity bare = rig.World.CreateEntity("bare");
        rig.World.Set(both1, Transform.Identity);
        rig.World.Set(both2, Transform.Identity);
        rig.World.Set(bare, Transform.Identity);

        Assert.Equal(["Light", "Transform"], rig.Editor.SharedComponents(both1, [both1]).Select(c => c.Info.Name).Where(n => n is "Light" or "Transform").Order());
        Assert.Equal(["Light", "Transform"], rig.Editor.SharedComponents(both1, [both1, both2]).Select(c => c.Info.Name).Where(n => n is "Light" or "Transform").Order());
        Assert.Equal(["Transform"], rig.Editor.SharedComponents(both1, [both1, bare]).Select(c => c.Info.Name).Where(n => n is "Light" or "Transform"));
    }

    [Fact]
    public void AWholeComponentDrawerWritesOnlyTheFieldsItChanged()
    {
        var table = DrawerTable.CreateDefault();
        int intensityAt = TypeRegistry.GetComponent<Light>().FindField("Intensity")!.Offset;
        int fields = 0;
        bool anyMixed = false;
        table.RegisterComponent("Light", (in ComponentEdit edit) =>
        {
            fields = edit.Mixed.Length;
            anyMixed = edit.Mixed.Contains(true);
            float value = 99f;
            MemoryMarshal.Write(edit.Bytes[intensityAt..], in value);
            return true;
        });
        using var rig = new InspectorRig(table);
        Entity a = rig.MakeLight("a", 1, 11);
        Entity b = rig.MakeLight("b", 2, 22);
        rig.Select([a, b], a);
        rig.ForgetHistory();

        rig.Draw(a);

        Assert.Equal(rig.LightInfo.Fields.Count, fields);
        Assert.True(anyMixed);
        Assert.Equal([99f, 99f], new[] { a, b }.Select(e => rig.LightOf(e).Intensity));
        Assert.Equal([11f, 22f], new[] { a, b }.Select(e => rig.LightOf(e).Range));
        Assert.Single(rig.Project.History.Steps);
    }

    [Fact]
    public void ADrawerThatChangesNothingWritesNothing()
    {
        using var rig = new InspectorRig();
        Entity a = rig.MakeLight("a", 1, 1);
        rig.Select([a]);
        rig.ForgetHistory();

        rig.Draw(a);

        Assert.Empty(rig.Project.History.Steps);
        Assert.False(rig.Project.Dirty);
    }

    // ---- Defaults ----

    [Fact]
    public void AddComponentStartsFromItsDefault()
    {
        using var rig = new InspectorRig();
        Entity e = rig.World.CreateEntity("e");
        rig.Select([e]);
        rig.ForgetHistory();

        Assert.True(rig.Editor.AddComponent([e], rig.LightInfo));
        Light light = rig.LightOf(e);
        Assert.Equal((Vector3.One, 80f, 12f, (byte)1), (light.Color, light.Intensity, light.Range, light.Kind));    // the same point light as Create > Point Light

        ComponentInfo camera = TypeRegistry.GetComponent<CameraComponent>();
        Assert.True(rig.Editor.AddComponent([e], camera));
        Assert.Equal(60f, rig.World.Get<CameraComponent>(e).FovDegrees);

        Assert.True(rig.Editor.AddComponent([e], TypeRegistry.GetComponent<Transform>()));
        Assert.Equal(Vector3.One, rig.World.Get<Transform>(e).Scale);                // not a transform with no size

        Assert.Equal(["Add Light", "Add CameraComponent", "Add Transform"], rig.Project.History.Steps);
        Assert.False(rig.Editor.AddComponent([e], rig.LightInfo));                    // already there: nothing to do

        // A component of a game: its registered default, or zeros.
        ComponentInfo plain = Component("InspAddZero", new FieldDef("V", FieldKind.Int32, 0));
        ComponentInfo styled = Component("InspAddStyled", new FieldDef("V", FieldKind.Int32, 0));
        ComponentDefaults.Register("InspAddStyled", BitConverter.GetBytes(42).Concat(new byte[4]).ToArray());
        try
        {
            Assert.True(rig.Editor.AddComponent([e], plain));
            Assert.True(rig.Editor.AddComponent([e], styled));
            Assert.Equal(0, BitConverter.ToInt32(rig.Peek(e, rig.World.RegisterComponent(plain), plain.Size)));
            Assert.Equal(42, BitConverter.ToInt32(rig.Peek(e, rig.World.RegisterComponent(styled), styled.Size)));
        }
        finally
        {
            ComponentDefaults.Remove("InspAddStyled");
        }
    }

    [Fact]
    public void AddingToSeveralEntitiesSkipsThoseThatHaveItAndIsOneStep()
    {
        using var rig = new InspectorRig();
        Entity has = rig.MakeLight("has", 5, 6);
        Entity lacks1 = rig.World.CreateEntity("lacks1");
        Entity lacks2 = rig.World.CreateEntity("lacks2");
        rig.Select([has, lacks1, lacks2]);
        rig.ForgetHistory();

        Assert.True(rig.Editor.AddComponent(rig.Targets, rig.LightInfo));

        Assert.Equal(5f, rig.LightOf(has).Intensity);                // untouched
        Assert.Equal(80f, rig.LightOf(lacks1).Intensity);
        Assert.Equal(80f, rig.LightOf(lacks2).Intensity);
        Assert.Equal(["Add Light"], rig.Project.History.Steps);
        rig.Project.History.Undo();
        Assert.True(rig.World.Has<Light>(has));
        Assert.False(rig.World.Has<Light>(lacks1));
        Assert.False(rig.World.Has<Light>(lacks2));
    }

    [Fact]
    public void ResetPutsTheDefaultBackAsOneStep()
    {
        using var rig = new InspectorRig();
        Entity a = rig.MakeLight("a", 5, 99, new Vector3(1, 0, 0), kind: 2);
        rig.Select([a]);
        rig.ForgetHistory();

        Assert.True(rig.Editor.ResetComponent([a], rig.LightId, rig.LightInfo));

        Light light = rig.LightOf(a);
        Assert.Equal((Vector3.One, 80f, 12f, (byte)1), (light.Color, light.Intensity, light.Range, light.Kind));
        Assert.Equal(["Reset Light"], rig.Project.History.Steps);
        rig.Project.History.Undo();
        Assert.Equal((5f, 99f, (byte)2), (rig.LightOf(a).Intensity, rig.LightOf(a).Range, rig.LightOf(a).Kind));
        Assert.False(rig.Project.History.CanUndo);

        Assert.False(rig.Editor.ResetComponent([a], rig.World.Component<MainCamera>(), TypeRegistry.GetComponent<MainCamera>()));    // a tag has no value
    }

    [Fact]
    public void ResettingAFieldPutsBackThatFieldOnEverySelectedEntityAndNothingElse()
    {
        using var rig = new InspectorRig();
        Entity a = rig.MakeLight("a", 5, 7);
        Entity b = rig.MakeLight("b", 6, 8);
        rig.Select([a, b], a);
        rig.ForgetHistory();

        // The person right-clicks the Intensity field and chooses Reset to default.
        rig.Ui.ResetOn.Add("Intensity");
        rig.Draw(a);

        Assert.Equal([80f, 80f], new[] { a, b }.Select(e => rig.LightOf(e).Intensity));
        Assert.Equal([7f, 8f], new[] { a, b }.Select(e => rig.LightOf(e).Range));
        Assert.Equal(["Reset Light.Intensity"], rig.Project.History.Steps);
        rig.Project.History.Undo();
        Assert.Equal([5f, 6f], new[] { a, b }.Select(e => rig.LightOf(e).Intensity));
    }

    [Fact]
    public void ResetOfAComponentWithoutADefaultGivesZeros()
    {
        using var rig = new InspectorRig();
        ComponentInfo info = Component("InspResetZero", new FieldDef("A", FieldKind.Int32, 0), new FieldDef("B", FieldKind.Int32, 4));
        Entity id = rig.Register(info);
        Entity e = rig.World.CreateEntity("e");
        rig.Poke(e, id, [1, 0, 0, 0, 2, 0, 0, 0]);
        rig.Select([e]);
        rig.ForgetHistory();

        Assert.True(rig.Editor.ResetField([e], id, info, info.Fields[1]));
        Assert.Equal(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }, rig.Peek(e, id, 8));
        Assert.True(rig.Editor.ResetComponent([e], id, info));
        Assert.Equal(new byte[8], rig.Peek(e, id, 8));
    }

    // ---- Add and remove ----

    [Fact]
    public void RemovingAComponentTakesItFromEverySelectedEntityAndNeverFromTheTransform()
    {
        using var rig = new InspectorRig();
        Entity a = rig.MakeLight("a", 1, 1);
        Entity b = rig.MakeLight("b", 2, 2);
        rig.World.Set(a, Transform.Identity);
        rig.Select([a, b]);
        rig.ForgetHistory();

        Assert.False(rig.Editor.RemoveComponent(rig.Targets, rig.World.Component<Transform>(), TypeRegistry.GetComponent<Transform>()));
        Assert.True(rig.World.Has<Transform>(a));

        Assert.True(rig.Editor.RemoveComponent(rig.Targets, rig.LightId, rig.LightInfo));
        Assert.False(rig.World.Has<Light>(a));
        Assert.False(rig.World.Has<Light>(b));
        Assert.Equal(["Remove Light"], rig.Project.History.Steps);
        rig.Project.History.Undo();
        Assert.Equal([1f, 2f], new[] { a, b }.Select(e => rig.LightOf(e).Intensity));
    }

    [Fact]
    public void TheAddListHasWhatSomeSelectedEntityLacks()
    {
        using var rig = new InspectorRig();
        Entity a = rig.MakeLight("a", 1, 1);
        Entity b = rig.World.CreateEntity("b");
        rig.Select([a, b]);

        IEnumerable<string> names = rig.Editor.AddCandidates(rig.Targets, "").Select(c => c.Name);
        Assert.Contains("Light", names);                                    // b lacks it
        Assert.DoesNotContain(nameof(WorldTransform), names);
        Assert.DoesNotContain(nameof(EditorOnly), names);
        Assert.Equal(["Light"], rig.Editor.AddCandidates(rig.Targets, "ligh").Select(c => c.Name));

        rig.Select([a]);
        Assert.DoesNotContain("Light", rig.Editor.AddCandidates(rig.Targets, "").Select(c => c.Name));      // a has it, and is the only one
    }

    [Fact]
    public void TheModelOfAModelRendererIsAPathAndAMissingFileLeavesItAlone()
    {
        using var rig = new InspectorRig();
        ComponentInfo info = TypeRegistry.GetComponent<ModelRenderer>();
        Entity id = rig.World.Component<ModelRenderer>();
        Entity e = rig.World.CreateEntity("e");
        rig.World.Set(e, new ModelRenderer());
        rig.Select([e]);
        rig.ForgetHistory();

        rig.DrawKeeping(e, info: info, id: id);
        DrawnWidget path = rig.Ui.Single("path", "Model");
        Assert.Equal("", path.Text);
        Assert.Equal([".glb", ".gltf"], path.Items);

        rig.Ui.NextFrame();
        rig.Ui.PathTo["Model"] = "res://models/not-there.glb";
        rig.Draw(e, info: info, id: id);
        Assert.Equal(0u, rig.World.Get<ModelRenderer>(e).Model);
        Assert.Empty(rig.Project.History.Steps);
    }
}
