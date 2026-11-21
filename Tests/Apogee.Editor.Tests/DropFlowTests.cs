using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Editor.Assets;
using Apogee.Native.Stb;
using Apogee.Native.Tests;
using Apogee.Platform;
using Apogee.Reflection;
using Apogee.Rendering;
using Xunit;
using CgltfApi = Apogee.Native.Cgltf.Cgltf;
using KtxApi = Apogee.Native.Ktx.Ktx;
using MeshoptApi = Apogee.Native.Meshopt.Meshopt;

namespace Apogee.Editor.Tests;

/// <summary>The editor's screen as a test sets it.</summary>
internal sealed class FakeSurface : IDropSurface
{
    public DropArea Area { get; set; } = DropArea.AssetBrowser;

    public string AssetFolder { get; set; } = "res://";

    public bool ModalOpen { get; set; }

    public Double3? Placement { get; set; } = new Double3(3, 0, -2);

    public int AreaCalls { get; private set; }

    public DropArea AreaAt(float x, float y)
    {
        AreaCalls++;
        return Area;
    }

    public Double3? PlacementAt(float x, float y) => Placement;
}

/// <summary>What the editor decides about a drop: where it goes, when it is refused, the question about names, and the entity a model on the viewport becomes. The drop event itself (SDL) is faked: it needs a display.</summary>
[Collection("editor")]
public sealed class DropFlowTests
{
    private const uint Window = 5;
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(60);

    public DropFlowTests()
    {
        NativeRequirements.Require(typeof(CgltfApi).Assembly, "cgltf");
        NativeRequirements.Require(typeof(Stbi).Assembly, "stb_image");
        NativeRequirements.Require(typeof(MeshoptApi).Assembly, "meshoptimizer");
        NativeRequirements.Require(typeof(KtxApi).Assembly, "ktx");
    }

    private static string Game(ProjectRig rig) => Path.Combine(rig.Root, "game");

    private static List<Entity> Models(ProjectRig rig) => [.. rig.World.UserEntities().Where(e => rig.World.Has<ModelRenderer>(e))];

    private static FileDrop Drop(bool position, params string[] paths) => new(Window, paths, 40, 50, position);

    private sealed class Rig
    {
        public Rig(ProjectRig project)
        {
            Open = project.Project;
            Flow = new DropFlow(() => Open, Surface, Window, Messages.Add);
        }

        public EditorProject? Open { get; set; }

        public FakeSurface Surface { get; } = new();

        public List<string> Messages { get; } = [];

        public DropFlow Flow { get; }
    }

    private static Rig Make(ProjectRig project) => new(project);

    private static void Finish(EditorProject project, DropFlow flow)
    {
        Assert.NotNull(project.Job);
        Assert.True(project.Job.Wait(s_wait));
        flow.Update();
    }

    [Fact]
    public void ADroppedModelOnTheViewportBecomesAnEntityAfterItsImport()
    {
        using var rig = new ProjectRig();
        using var source = new DropSource();
        Rig editor = Make(rig);
        editor.Surface.Area = DropArea.Viewport;
        string model = source.Write("box.glb", TestAssets.Glb());

        editor.Flow.Handle(Drop(true, model));

        Assert.Empty(editor.Messages);
        Assert.NotNull(rig.Project.Job);
        Assert.True(editor.Flow.Status is not null || rig.Project.Job!.IsEnded);                     // the menu bar has something to say while it runs
        Finish(rig.Project, editor.Flow);

        Assert.True(File.Exists(Path.Combine(Game(rig), "models", "box.glb")));                      // the viewport's folder when the browser is at the top
        Assert.Null(rig.Project.Job);
        Entity entity = Assert.Single(Models(rig));
        Assert.Equal("box", rig.World.GetName(entity));
        Assert.Equal(new Double3(3, 0, -2), rig.World.Get<Transform>(entity).Position);               // where it was dropped, found when the drop happened
        Assert.Equal(entity, rig.Project.Selection);

        rig.Project.History.Undo();                                                                 // one step takes the entity away
        Assert.Empty(Models(rig));
        Assert.True(File.Exists(Path.Combine(Game(rig), "models", "box.glb")));                      // the files are not undone
    }

    [Fact]
    public void SeveralModelsOnTheViewportAreOneStepSideBySide()
    {
        using var rig = new ProjectRig();
        using var source = new DropSource();
        Rig editor = Make(rig);
        editor.Surface.Area = DropArea.Viewport;
        editor.Surface.AssetFolder = "res://props";

        editor.Flow.Handle(Drop(true, source.Write("a.glb", TestAssets.Glb()), source.Write("b.glb", TestAssets.Glb())));
        Finish(rig.Project, editor.Flow);

        Entity[] made = [.. Models(rig).OrderBy(e => rig.World.GetName(e), StringComparer.Ordinal)];
        Assert.Equal(2, made.Length);
        Assert.True(File.Exists(Path.Combine(Game(rig), "props", "a.glb")));                         // the asset browser's folder
        Assert.Equal(3.0, rig.World.Get<Transform>(made[0]).Position.X);
        Assert.Equal(5.0, rig.World.Get<Transform>(made[1]).Position.X);
        rig.Project.History.Undo();
        Assert.Empty(Models(rig));
    }

    [Fact]
    public void ADropOnTheAssetBrowserJustImports()
    {
        using var rig = new ProjectRig();
        using var source = new DropSource();
        Rig editor = Make(rig);
        editor.Surface.AssetFolder = "res://textures";

        editor.Flow.Handle(Drop(true, source.Write("a.png", TestAssets.Png()), source.Write("m.glb", TestAssets.Glb())));
        Finish(rig.Project, editor.Flow);

        Assert.True(File.Exists(Path.Combine(Game(rig), "textures", "a.png")));
        Assert.True(File.Exists(Path.Combine(Game(rig), "textures", "m.glb")));
        Assert.True(File.Exists(Path.Combine(Game(rig), "imported", "index.xml")));
        Assert.Empty(Models(rig));                                             // no entity: it was not dropped on the scene
        Assert.False(rig.Project.Dirty);
    }

    [Fact]
    public void ADropWithNoPositionGoesToTheAssetBrowserWithoutAskingWhereItLanded()
    {
        using var rig = new ProjectRig();
        using var source = new DropSource();
        Rig editor = Make(rig);
        editor.Surface.Area = DropArea.Viewport;                                                    // would send it to the viewport if asked

        editor.Flow.Handle(Drop(false, source.Write("a.png", TestAssets.Png())));
        Finish(rig.Project, editor.Flow);

        Assert.Equal(0, editor.Surface.AreaCalls);
        Assert.True(File.Exists(Path.Combine(Game(rig), "a.png")));
    }

    [Fact]
    public void ADropIsRefusedWithAMessageWhenItCannotBeTaken()
    {
        using var rig = new ProjectRig();
        using var source = new DropSource();
        Rig editor = Make(rig);
        string file = source.Write("a.png", TestAssets.Png());

        editor.Surface.ModalOpen = true;
        editor.Flow.Handle(Drop(true, file));
        Assert.Contains("dialog", Assert.Single(editor.Messages), StringComparison.Ordinal);
        editor.Surface.ModalOpen = false;
        editor.Messages.Clear();

        editor.Surface.Area = DropArea.None;                                                        // over the inspector, say
        editor.Flow.Handle(Drop(true, file));
        Assert.Contains("Assets panel", Assert.Single(editor.Messages), StringComparison.Ordinal);
        editor.Surface.Area = DropArea.AssetBrowser;
        editor.Messages.Clear();

        editor.Flow.Handle(new FileDrop(Window + 1, [file], 1, 1, true));                           // another window's: not ours, not even a message
        Assert.Empty(editor.Messages);

        editor.Open = null;
        editor.Flow.Handle(Drop(true, file));
        Assert.Contains("Open a project", Assert.Single(editor.Messages), StringComparison.Ordinal);
        editor.Open = rig.Project;
        editor.Messages.Clear();

        Assert.True(rig.Project.Play());
        editor.Flow.Handle(Drop(true, file));
        Assert.Contains("playing", Assert.Single(editor.Messages), StringComparison.Ordinal);
        rig.Project.Stop();
        editor.Messages.Clear();

        Assert.Null(rig.Project.Job);
        Assert.False(File.Exists(Path.Combine(Game(rig), "a.png")));                                // none of them copied anything
    }

    [Fact]
    public void ADropWhileAnImportRunsIsRefused()
    {
        using var rig = new ProjectRig();
        using var source = new DropSource();
        Rig editor = Make(rig);
        Assert.True(rig.Project.ImportGate.TryEnter());                                             // a job (or the reloader) has the importer

        editor.Flow.Handle(Drop(true, source.Write("a.png", TestAssets.Png())));

        Assert.Contains("imported", Assert.Single(editor.Messages), StringComparison.Ordinal);
        rig.Project.ImportGate.Exit();
        Assert.False(File.Exists(Path.Combine(Game(rig), "a.png")));
    }

    [Fact]
    public void NothingTakenMeansAMessageNamingWhy()
    {
        using var rig = new ProjectRig();
        using var source = new DropSource();
        Rig editor = Make(rig);

        editor.Flow.Handle(Drop(true, source.Write("tool.exe", "MZ")));

        string message = Assert.Single(editor.Messages);
        Assert.Contains("Nothing was added", message, StringComparison.Ordinal);
        Assert.Contains("tool.exe", message, StringComparison.Ordinal);
        Assert.Null(rig.Project.Job);
    }

    [Fact]
    public void AClashIsAskedAndTheAnswerDecides()
    {
        using var rig = new ProjectRig();
        using var source = new DropSource();
        Rig editor = Make(rig);
        string existing = Path.Combine(Game(rig), "a.png");
        File.WriteAllBytes(existing, TestAssets.Png(1));
        string dropped = source.Write("a.png", TestAssets.Png(2));

        editor.Flow.Handle(Drop(true, dropped));

        Assert.NotNull(editor.Flow.Pending);
        Assert.Equal(["res://a.png"], editor.Flow.Pending.Clashes);
        Assert.Null(rig.Project.Job);                                                               // nothing starts before the answer
        editor.Flow.Handle(Drop(true, dropped));
        Assert.Contains("question", Assert.Single(editor.Messages), StringComparison.Ordinal);       // and a second drop waits for it
        editor.Messages.Clear();

        editor.Flow.Resolve(ClashPolicy.KeepBoth);                                                  // the default answer: keep both
        Finish(rig.Project, editor.Flow);
        Assert.Null(editor.Flow.Pending);
        Assert.Equal(TestAssets.Png(1), File.ReadAllBytes(existing));
        Assert.Equal(TestAssets.Png(2), File.ReadAllBytes(Path.Combine(Game(rig), "a 2.png")));

        editor.Flow.Handle(Drop(true, dropped));                                                    // asked again: a and a 2 are there now
        editor.Flow.Resolve(ClashPolicy.Skip);
        Assert.Null(rig.Project.Job);
        Assert.False(File.Exists(Path.Combine(Game(rig), "a 3.png")));

        editor.Flow.Handle(Drop(true, dropped));
        editor.Flow.Resolve(null);                                                                  // cancelled
        Assert.Null(rig.Project.Job);
        Assert.Null(editor.Flow.Pending);

        editor.Flow.Handle(Drop(true, dropped));
        editor.Flow.Resolve(ClashPolicy.Replace);
        Finish(rig.Project, editor.Flow);
        Assert.Equal(TestAssets.Png(2), File.ReadAllBytes(existing));                               // only now was the project's file overwritten
        Assert.Empty(editor.Messages);
    }

    [Fact]
    public void AnAnswerThatArrivesAfterTheProjectChangedIsDropped()
    {
        using var rig = new ProjectRig();
        using var source = new DropSource();
        Rig editor = Make(rig);
        File.WriteAllBytes(Path.Combine(Game(rig), "a.png"), TestAssets.Png(1));
        editor.Flow.Handle(Drop(true, source.Write("a.png", TestAssets.Png(2))));
        Assert.NotNull(editor.Flow.Pending);

        editor.Open = null;                                                                         // another project was opened (or none)
        editor.Flow.Resolve(ClashPolicy.Replace);

        Assert.Equal(TestAssets.Png(1), File.ReadAllBytes(Path.Combine(Game(rig), "a.png")));
        Assert.Null(editor.Flow.Pending);
    }

    [Fact]
    public void CancelStopsTheRunningJobAndTheReportSaysSo()
    {
        using var rig = new ProjectRig();
        using var source = new DropSource();
        Rig editor = Make(rig);
        string[] files = [.. Enumerable.Range(0, 4).Select(i => source.Write($"f{i}.png", TestAssets.Png((byte)i)))];

        editor.Flow.Handle(Drop(true, files));
        editor.Flow.Cancel();
        Finish(rig.Project, editor.Flow);

        Assert.Null(rig.Project.Job);
        Assert.Null(editor.Flow.Status);
        Assert.Empty(editor.Messages);
    }

    [Fact]
    public void FilesAreRoutedToTheDrawnPanelUnderThePointer()
    {
        var surface = new PanelDropSurface
        {
            ViewportRect = (new System.Numerics.Vector2(100, 20), new System.Numerics.Vector2(600, 400)),
            AssetsRect = (new System.Numerics.Vector2(100, 420), new System.Numerics.Vector2(600, 200)),
            Placer = point => new Double3(point.X, 0, point.Y),
        };

        Assert.Equal(DropArea.Viewport, surface.AreaAt(100, 20));                                    // the top-left corner is inside
        Assert.Equal(DropArea.Viewport, surface.AreaAt(699.5f, 419.5f));
        Assert.Equal(DropArea.AssetBrowser, surface.AreaAt(300, 420));                               // the edge belongs to the panel below
        Assert.Equal(DropArea.None, surface.AreaAt(50, 50));                                         // the hierarchy, say
        Assert.Equal(DropArea.None, surface.AreaAt(700, 100));
        Assert.Equal(new Double3(7, 0, 9), surface.PlacementAt(7, 9));

        surface.AssetsRect = null;                                                                  // a panel behind another tab was not drawn: it takes nothing
        Assert.Equal(DropArea.None, surface.AreaAt(300, 500));
    }
}
