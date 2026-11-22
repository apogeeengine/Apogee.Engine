using System;
using System.IO;
using System.Linq;
using Apogee.Core.Assets;
using Apogee.Ecs;
using Apogee.Editor.Inspector;
using Apogee.Reflection;
using Apogee.Rendering;
using Xunit;

namespace Apogee.Editor.Tests;

/// <summary>
/// The asset field drawer over a real project (files, a session, the model library, the history), driven through the fake UI: what a field shows, what a pick, a drop, <i>Clear</i> and <i>Show in assets</i>
/// do, and that a pick that does not give a handle says so instead of writing 0.
/// </summary>
[Collection("editor")]
public sealed class AssetPickerTests
{
    private static readonly ComponentInfo s_info = Info();

    private static ComponentInfo Info()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(ModelRenderer).Module.ModuleHandle);
        return TypeRegistry.GetComponent<ModelRenderer>();
    }

    private sealed class Rig : IDisposable
    {
        private readonly InspectorRig _inspector = new();

        public Rig()
        {
            Id = _inspector.World.Component<ModelRenderer>();
            Car = Model("car", CarUid);
            Truck = Model("truck", AssetUid.New());
        }

        public InspectorRig Inspector => _inspector;

        public EditorProject Project => _inspector.Project;

        public World World => _inspector.World;

        public FakeInspectorUi Ui => _inspector.Ui;

        public Entity Id { get; }

        public AssetUid CarUid { get; } = AssetUid.New();

        public string Car { get; }

        public string Truck { get; }

        public uint Handle(string path) => Project.Session.Models.Load(path);

        public string Model(string name, AssetUid uid)
        {
            _inspector.Write($"game/models/{name}.glb", "not a real model");           // only that the file is there matters to the library
            _inspector.Write($"game/models/{name}.glb.import.xml", $"<Import uid=\"{uid}\"/>");
            return $"res://models/{name}.glb";
        }

        public Entity Renderer(string name, uint model)
        {
            Entity e = World.CreateEntity(name);
            World.Set(e, new ModelRenderer { Model = model });
            return e;
        }

        public void Select(params Entity[] entities) => _inspector.Select(entities);

        public void Draw(Entity active, bool keep = false)
        {
            if (keep)
            {
                _inspector.DrawKeeping(active, info: s_info, id: Id);
            }
            else
            {
                _inspector.Draw(active, info: s_info, id: Id);
            }
        }

        public uint ModelOf(Entity e) => World.Get<ModelRenderer>(e).Model;

        public void Dispose() => _inspector.Dispose();
    }

    [Fact]
    public void AFieldShowsTheFileNameWithItsPathAndUidInTheTooltip()
    {
        using var rig = new Rig();
        Entity e = rig.Renderer("e", rig.Handle(rig.Car));
        rig.Select(e);
        rig.Project.Catalog.Refresh();

        rig.Draw(e, keep: true);

        DrawnWidget widget = rig.Ui.Single("asset", "Model");
        Assert.Equal("car.glb", widget.Text);
        Assert.False(widget.Asset!.Value.Broken);
        Assert.True(widget.Asset.Value.CanShow);
        Assert.Contains("res://models/car.glb", widget.Asset.Value.Detail, StringComparison.Ordinal);
        Assert.Contains(rig.CarUid.ToString(), widget.Asset.Value.Detail, StringComparison.Ordinal);
        Assert.Equal(["res://models/car.glb", "res://models/truck.glb"], rig.Ui.PopupListed["Model"]);       // the popup lists the game's models
    }

    [Fact]
    public void AnEmptyFieldSaysNoneAndTheSelectedEntitiesThatDifferShowADash()
    {
        using var rig = new Rig();
        Entity none = rig.Renderer("none", 0);
        Entity car = rig.Renderer("car", rig.Handle(rig.Car));
        Entity truck = rig.Renderer("truck", rig.Handle(rig.Truck));

        rig.Select(none);
        rig.Draw(none, keep: true);
        Assert.Equal("None", rig.Ui.Single("asset", "Model").Text);
        Assert.False(rig.Ui.Single("asset", "Model").Asset!.Value.Broken);
        Assert.False(rig.Ui.Single("asset", "Model").Asset!.Value.CanShow);

        rig.Ui.NextFrame();
        rig.Select(car, truck);
        rig.Draw(car, keep: true);
        DrawnWidget mixed = rig.Ui.Single("asset", "Model");
        Assert.True(mixed.Mixed);
        Assert.Equal("---", mixed.Text);
    }

    [Fact]
    public void PickingAFileSetsTheHandleAndUndoPutsTheOldOneBack()
    {
        using var rig = new Rig();
        uint car = rig.Handle(rig.Car);
        Entity e = rig.Renderer("e", car);
        rig.Select(e);
        rig.Inspector.ForgetHistory();

        rig.Ui.AssetPickTo["Model"] = rig.Truck;
        rig.Draw(e);

        Assert.Equal(rig.Handle(rig.Truck), rig.ModelOf(e));
        Assert.NotEqual(car, rig.ModelOf(e));
        Assert.Single(rig.Project.History.Steps);
        rig.Project.History.Undo();
        Assert.Equal(car, rig.ModelOf(e));
        rig.Project.History.Redo();
        Assert.Equal(rig.Handle(rig.Truck), rig.ModelOf(e));
    }

    [Fact]
    public void ADroppedFileOfTheRightKindIsPickedAndOneOfAnotherKindIsRefused()
    {
        using var rig = new Rig();
        rig.Inspector.Write("game/textures/wall.png", "not a png");
        Entity e = rig.Renderer("e", 0);
        rig.Select(e);
        rig.Inspector.ForgetHistory();

        rig.Ui.AssetDropTo["Model"] = "res://textures/wall.png";
        rig.Draw(e);
        Assert.Equal(0u, rig.ModelOf(e));
        Assert.Empty(rig.Project.History.Steps);
        rig.Draw(e, keep: true);
        Assert.Equal("wall.png is not a model.", rig.Ui.Single("asset", "Model").Asset!.Value.Problem);

        rig.Ui.NextFrame();
        rig.Ui.AssetDropTo["Model"] = rig.Car;
        rig.Draw(e);
        Assert.Equal(rig.Handle(rig.Car), rig.ModelOf(e));

        rig.Draw(e, keep: true);
        Assert.Null(rig.Ui.Single("asset", "Model").Asset!.Value.Problem);        // a good pick takes the message away
    }

    [Fact]
    public void APickThatGivesNoHandleSaysSoAndLeavesTheFieldAlone()
    {
        using var rig = new Rig();
        uint car = rig.Handle(rig.Car);
        Entity e = rig.Renderer("e", car);
        rig.Select(e);
        rig.Inspector.ForgetHistory();

        // The file was in the list and is gone when it is picked.
        rig.Ui.AssetPickTo["Model"] = "res://models/vanished.glb";
        rig.Draw(e);

        Assert.Equal(car, rig.ModelOf(e));                         // not 0: 0 would look like a clear
        Assert.Empty(rig.Project.History.Steps);
        rig.Draw(e, keep: true);
        Assert.Contains("vanished.glb could not be used as a model", rig.Ui.Single("asset", "Model").Asset!.Value.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsNotImportedYetCanBePicked()
    {
        using var rig = new Rig();
        rig.Inspector.Write("game/models/fresh.glb", "just copied: no sidecar, nothing imported");
        Entity e = rig.Renderer("e", 0);
        rig.Select(e);

        rig.Ui.AssetPickTo["Model"] = "res://models/fresh.glb";
        rig.Draw(e);

        Assert.Equal(rig.Handle("res://models/fresh.glb"), rig.ModelOf(e));
        Assert.NotEqual(0u, rig.ModelOf(e));
        rig.Draw(e, keep: true);
        Assert.Contains("no uid", rig.Ui.Single("asset", "Model").Asset!.Value.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearPutsZeroAndUndoBringsTheModelBack()
    {
        using var rig = new Rig();
        uint car = rig.Handle(rig.Car);
        Entity e = rig.Renderer("e", car);
        rig.Select(e);
        rig.Inspector.ForgetHistory();

        rig.Ui.AssetClear.Add("Model");
        rig.Draw(e);

        Assert.Equal(0u, rig.ModelOf(e));
        rig.Project.History.Undo();
        Assert.Equal(car, rig.ModelOf(e));
    }

    [Fact]
    public void PickingForSeveralEntitiesIsOneStepThatUndoesForAll()
    {
        using var rig = new Rig();
        uint car = rig.Handle(rig.Car);
        Entity a = rig.Renderer("a", car);
        Entity b = rig.Renderer("b", 0);
        rig.Select(a, b);
        rig.Inspector.ForgetHistory();

        rig.Ui.AssetPickTo["Model"] = rig.Truck;
        rig.Draw(a);

        uint truck = rig.Handle(rig.Truck);
        Assert.Equal(truck, rig.ModelOf(a));
        Assert.Equal(truck, rig.ModelOf(b));
        Assert.Single(rig.Project.History.Steps);
        rig.Project.History.Undo();
        Assert.Equal(car, rig.ModelOf(a));
        Assert.Equal(0u, rig.ModelOf(b));
    }

    [Fact]
    public void AReferenceThatDidNotLoadIsRedWithItsTextAndClearingItSurvivesASave()
    {
        using var rig = new Rig();
        rig.Project.World.LoadScene("<Scene version=\"1\"><Entity id=\"1\" name=\"lost\"><ModelRenderer Model=\"res://models/gone.glb|uid://0000000000zzz\"/></Entity></Scene>");
        Entity lost = rig.World.Roots().Single();
        rig.Select(lost);
        rig.Inspector.ForgetHistory();

        rig.Draw(lost, keep: true);
        AssetFieldView view = rig.Ui.Single("asset", "Model").Asset!.Value;
        Assert.True(view.Broken);
        Assert.Equal("res://models/gone.glb|uid://0000000000zzz", view.Text);       // the text is kept
        Assert.False(view.CanShow);
        Assert.Contains("could not be loaded", view.Detail, StringComparison.Ordinal);

        // A save writes it back as it was ...
        Assert.Contains("res://models/gone.glb", rig.World.SaveScene(), StringComparison.Ordinal);

        // ... until the person clears it: then it is gone from the scene text, and undo brings it back.
        rig.Ui.NextFrame();
        rig.Ui.AssetClear.Add("Model");
        rig.Draw(lost);
        Assert.DoesNotContain("gone.glb", rig.World.SaveScene(), StringComparison.Ordinal);
        Assert.Single(rig.Project.History.Steps);
        rig.Project.History.Undo();
        Assert.Contains("res://models/gone.glb", rig.World.SaveScene(), StringComparison.Ordinal);
        rig.Project.History.Redo();

        // Picking a file instead replaces it too, and the text of the old reference does not come back with a later clear.
        rig.Project.History.Undo();
        rig.Ui.AssetPickTo["Model"] = rig.Car;
        rig.Draw(lost);
        string saved = rig.World.SaveScene();
        Assert.Contains($"Model=\"res://models/car.glb|{rig.CarUid}\"", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("gone.glb", saved, StringComparison.Ordinal);
        rig.Ui.AssetClear.Add("Model");
        rig.Draw(lost);
        Assert.DoesNotContain("gone.glb", rig.World.SaveScene(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADeletedFileIsABrokenReferenceThatKeepsItsPath()
    {
        using var rig = new Rig();
        Entity e = rig.Renderer("e", rig.Handle(rig.Truck));
        rig.Select(e);
        rig.Draw(e, keep: true);
        Assert.False(rig.Ui.Single("asset", "Model").Asset!.Value.Broken);

        File.Delete(Path.Combine(rig.Inspector.Root, "game/models/truck.glb"));
        File.Delete(Path.Combine(rig.Inspector.Root, "game/models/truck.glb.import.xml"));
        rig.Project.Catalog.Invalidate();                              // what the reloader's report does
        rig.Ui.NextFrame();
        rig.Draw(e, keep: true);

        AssetFieldView view = rig.Ui.Single("asset", "Model").Asset!.Value;
        Assert.True(view.Broken);
        Assert.StartsWith("res://models/truck.glb", view.Text, StringComparison.Ordinal);
        Assert.Contains("no file", view.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AHandleMadeInCodeIsBrokenBecauseASceneCannotSaveIt()
    {
        using var rig = new Rig();
        Entity e = rig.Renderer("e", 4242);                           // a number the library never gave out
        rig.Select(e);

        rig.Draw(e, keep: true);

        AssetFieldView view = rig.Ui.Single("asset", "Model").Asset!.Value;
        Assert.True(view.Broken);
        Assert.Equal("#4242", view.Text);
        Assert.Contains("names no file", view.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowInAssetsAsksTheBrowserForTheFile()
    {
        using var rig = new Rig();
        Entity e = rig.Renderer("e", rig.Handle(rig.Car));
        Entity none = rig.Renderer("none", 0);

        rig.Select(none);
        rig.Ui.AssetShow.Add("Model");
        rig.Draw(none);
        Assert.Null(rig.Project.Catalog.TakeReveal());                  // nothing to show

        rig.Select(e);
        rig.Ui.AssetShow.Add("Model");
        rig.Draw(e);
        Assert.Equal("res://models/car.glb", rig.Project.Catalog.TakeReveal());
    }

    [Fact]
    public void ADrawerRegisteredForAnAssetTypeReplacesThePicker()
    {
        var table = DrawerTable.CreateDefault();
        FieldDrawer mine = (in FieldEdit _) => false;
        table.RegisterAssetType("model", mine);

        Assert.Equal(DrawerLevel.AssetType, table.Find(s_info, s_info.FindField("Model")!).Level);
        Assert.Same(mine, table.Find(s_info, s_info.FindField("Model")!).Field);
    }
}
