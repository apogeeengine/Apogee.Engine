using System.Linq;
using System.Numerics;
using Apogee.Editor.Inspector;
using Apogee.Reflection;
using Xunit;

namespace Apogee.Editor.Tests;

/// <summary>The Debug &gt; Engine objects window without its drawing: which objects it lists, and edits that are live and not on the scene's history.</summary>
[Collection("editor")]
public sealed class EngineObjectsTests
{
    [Fact]
    public void TheSessionsObjectsAreListedByName()
    {
        using var rig = new ProjectRig();
        var model = new EngineObjectsModel(rig.Project);
        string[] names = [.. model.Entries().Select(e => e.Name)];
        foreach (string expected in new[] { "World", "UI", "Physics", "Audio", "Assets", "Models", "Settings" })
        {
            Assert.Contains(expected, names);
        }

        // The first is shown until another is picked; the pick is by name.
        Assert.Same(rig.Project.Session.World, model.Selected);
        model.SelectedName = "Physics";
        Assert.Same(rig.Project.Session.Physics, model.Selected);
        model.SelectedName = "no such object";
        Assert.Same(rig.Project.Session.World, model.Selected);
    }

    [Fact]
    public void AnEditIsLiveAndNotOnTheScenesHistory()
    {
        using var rig = new ProjectRig();
        var model = new EngineObjectsModel(rig.Project);
        var ui = new FakeInspectorUi();
        model.SelectedName = "Physics";
        Assert.False(rig.Project.Dirty);
        Vector3 original = rig.Project.Session.Physics.Gravity;

        ui.ToggleTo["paused"] = true;
        ui.DragTo["gravity"] = [0, -3, 0];
        Assert.True(model.Draw(ui));
        Assert.True(rig.Project.Session.Physics.Paused);
        Assert.Equal(new Vector3(0, -3, 0), rig.Project.Session.Physics.Gravity);

        // The scene knows nothing of it: nothing to undo there, and it is not modified.
        Assert.False(rig.Project.History.CanUndo);
        Assert.False(rig.Project.Dirty);

        // The window has a history of its own.
        Assert.Equal(2, model.History.Steps.Count);
        Assert.True(model.Undo());
        Assert.Equal(original, rig.Project.Session.Physics.Gravity);
        Assert.True(model.Undo());
        Assert.False(rig.Project.Session.Physics.Paused);
        Assert.False(model.Undo());
        Assert.True(model.Redo());
        Assert.True(rig.Project.Session.Physics.Paused);
    }

    [Fact]
    public void ObjectsHeldByAnObjectAreOpenedInPlace()
    {
        using var rig = new ProjectRig();
        var model = new EngineObjectsModel(rig.Project) { SelectedName = "Audio" };
        var ui = new FakeInspectorUi();
        model.Draw(ui);

        // The audio system holds the physics world it plays the sounds of: a node to open, whatever that holds.
        Assert.Contains(ui.Drawn, w => w.Kind == "node" && w.Label == "physics");
        Assert.Contains(ui.Drawn, w => w.Kind == "drag" && w.Label == "volume");
    }

    [Fact]
    public void EditsWorkWhilePlayingAndTheHistoryEndsWithTheSession()
    {
        using var rig = new ProjectRig();
        var model = new EngineObjectsModel(rig.Project) { SelectedName = "Physics" };
        var ui = new FakeInspectorUi();
        ui.ToggleTo["interpolate"] = true;
        model.Draw(ui);
        Assert.True(model.History.CanUndo);

        // Play replaces the session: the objects are new ones, the choice by name holds, and the old steps are dropped.
        Assert.True(rig.Project.Play());
        ui.NextFrame();
        Assert.Same(rig.Project.Session.Physics, model.Selected);
        Assert.False(model.History.CanUndo);
        Assert.False(model.Undo());
        ui.ToggleTo["paused"] = true;
        Assert.True(model.Draw(ui));                       // allowed while the game plays
        Assert.True(rig.Project.Session.Physics.Paused);
        Assert.True(model.History.CanUndo);

        rig.Project.Stop();
        Assert.False(model.History.CanUndo);
        Assert.Equal("Physics", model.SelectedName);
        Assert.Same(rig.Project.Session.Physics, model.Selected);
    }

    [Fact]
    public void UndoWithNothingToUndoDoesNothingAndSaysNothing()
    {
        using var rig = new ProjectRig();
        var model = new EngineObjectsModel(rig.Project) { SelectedName = "Physics" };
        Assert.False(model.Undo());
        Assert.Null(model.Message);
        Assert.False(model.Redo());
        Assert.Contains("own Undo and Redo", EngineObjectsModel.Notice, System.StringComparison.Ordinal);
    }
}
