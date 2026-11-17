using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Apogee.Content.Assets;
using Apogee.Core.Assets;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Editor.Editing;
using Apogee.Host.Editing;
using Apogee.Native.Tests;
using Apogee.Reflection;
using Apogee.Rendering;
using Xunit;
using Flecs = Apogee.Native.Flecs.Ecs;
using Lua = Apogee.Native.Luau.Lua;

namespace Apogee.Editor.Tests;

/// <summary>A temporary game folder with one package, and the editor project open on it.</summary>
internal sealed class ProjectRig : IDisposable
{
    public ProjectRig(string? script = null)
    {
        NativeRequirements.Require(typeof(Lua).Assembly, "luau");
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        Root = Path.Combine(Path.GetTempPath(), "apogee-editor-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');
        Write("game/packages/scene/mod.xml", "<Mod id=\"scene\" version=\"1.0.0\"><Script path=\"main.luau\"/></Mod>");
        Write("game/packages/scene/main.luau", script ?? "print('scene ran')");
        Project = new EditorProject(Path.Combine(Root, "game"), Path.Combine(Root, "user"));
    }

    public string Root { get; }

    public EditorProject Project { get; }

    public World World => Project.World;

    public string Write(string name, string text)
    {
        string path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        Project.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder is not a failure.
        }
    }
}

/// <summary>The editor's project: edits through the history, scene files, and Play and Stop. One session at a time, so these run one at a time.</summary>
[Collection("editor")]
public sealed class ProjectTests
{
    [Fact]
    public void CreatingEntitiesNamesThemUniquelySelectsThemAndCanBeUndone()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;

        Entity a = project.CreateEmpty();
        Entity b = project.CreateEmpty();
        Entity light = project.CreatePointLight(a, new Double3(0, 3, 0));
        Entity sun = project.CreateSun();
        Entity camera = project.CreateCamera();

        Assert.Equal("Empty", rig.World.GetName(a));
        Assert.Equal("Empty 2", rig.World.GetName(b));
        Assert.Equal("Point Light", rig.World.GetName(light));
        Assert.Equal(a, rig.World.GetParent(light));
        Assert.Equal(camera, project.Selection);                       // the last one made is selected
        Assert.Equal(80f, rig.World.Get<Light>(light).Intensity);
        Assert.Equal(3f, rig.World.Get<Light>(sun).Intensity);
        Assert.True(rig.World.Has<MainCamera>(camera));
        Assert.True(project.Dirty);

        project.History.Undo();                                        // the camera goes
        Assert.False(rig.World.IsAlive(camera));
        Assert.Null(project.Selection);                                // and so does the selection of something that is not there
        project.History.Redo();
        Assert.True(rig.World.IsAlive(camera));                        // the same id: the selection is valid again
        Assert.Equal(camera, project.Selection);
    }

    [Fact]
    public void RenamingReparentingDuplicatingAndDeletingGoThroughTheHistoryAndKeepNamesUnique()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        Entity a = project.CreateEmpty();
        Entity b = project.CreateEmpty();
        Entity child = project.CreateEmpty(a);

        Assert.False(project.Rename(b, "Empty"));                      // a root already has that name
        Assert.False(project.Rename(b, ""));
        Assert.True(project.Rename(b, "Boss"));
        Assert.Equal("Boss", rig.World.GetName(b));

        Assert.False(project.Reparent(a, child));                      // under its own child
        Assert.True(project.Reparent(child, b));
        Assert.Equal(b, rig.World.GetParent(child));
        project.History.Undo();
        Assert.Equal(a, rig.World.GetParent(child));

        Entity copy = project.Duplicate(a);
        Assert.Equal("Empty (copy)", rig.World.GetName(copy));
        Assert.Single(rig.World.Children(copy));
        Assert.Equal(copy, project.Selection);

        project.Delete(a);
        Assert.False(rig.World.IsAlive(a));
        Assert.False(rig.World.IsAlive(child));
        project.History.Undo();
        Assert.True(rig.World.IsAlive(child));
        Assert.Equal(a, rig.World.GetParent(child));
    }

    [Fact]
    public void ASceneIsSavedToItsFileAndOpenedAgainWithTheModelsPath()
    {
        using var rig = new ProjectRig();
        rig.Write("game/models/box.glb", "not a real model");              // only the path matters here
        EditorProject project = rig.Project;
        Entity a = project.CreateEmpty();
        rig.World.Set(a, new Transform(new Double3(1, 2, 3), Quaternion.Identity, new Vector3(2, 2, 2)));
        Entity box = project.CreateModel("res://models/box.glb", new Double3(4, 0, 0), a);
        Assert.Equal("box", rig.World.GetName(box));

        Assert.False(project.SaveScene());                              // no file yet
        project.SaveSceneAs("res://scenes/level");
        Assert.Equal("res://scenes/level.ascene", project.ScenePath);
        Assert.False(project.Dirty);
        Assert.Equal(["res://scenes/level.ascene"], project.SceneFiles());
        string text = File.ReadAllText(Path.Combine(rig.Root, "game/scenes/level.ascene"));
        Assert.StartsWith("<?xml", text, StringComparison.Ordinal);                // a scene file is XML
        Assert.Contains("<Scene version=\"1\" uid=\"uid://", text, StringComparison.Ordinal);       // and has an identity
        Assert.Contains("Model=\"res://models/box.glb\"", text, StringComparison.Ordinal);   // the model by its path, not by a number

        project.NewScene();
        Assert.Empty(rig.World.Roots());
        Assert.Null(project.ScenePath);

        project.OpenScene("res://scenes/level.ascene");
        Assert.Equal("res://scenes/level.ascene", project.ScenePath);
        Entity root = rig.World.Roots().Single();
        Assert.Equal(new Vector3(2, 2, 2), rig.World.Get<Transform>(root).Scale);
        Entity loaded = rig.World.Children(root).Single();
        Assert.Equal("res://models/box.glb", project.Session.Models.PathOf(rig.World.Get<ModelRenderer>(loaded).Model));
        Assert.False(project.History.CanUndo);                          // a scene opened is a clean start
        Assert.False(project.Dirty);

        project.CreateEmpty();
        Assert.True(project.Dirty);
        Assert.True(project.SaveScene());
        Assert.False(project.Dirty);
    }

    [Fact]
    public void SaveAsThenSaveKeepsTheSceneUid()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        project.CreateEmpty();

        project.SaveSceneAs("res://scenes/level");
        string file = Path.Combine(rig.Root, "game/scenes/level.ascene");
        AssetUid uid = AuthoredAssets.ReadUid(File.ReadAllBytes(file));
        Assert.False(uid.IsNone);

        project.CreateEmpty();
        Assert.True(project.SaveScene());
        Assert.Equal(uid, AuthoredAssets.ReadUid(File.ReadAllBytes(file)));

        // Open again, change, save: still the same asset, and it opens by its UID.
        project.NewScene();
        project.OpenScene("res://scenes/level.ascene");
        project.CreateEmpty();
        Assert.True(project.SaveScene());
        Assert.Equal(uid, AuthoredAssets.ReadUid(File.ReadAllBytes(file)));

        project.NewScene();
        project.OpenScene(uid.ToString());
        Assert.Equal(3, rig.World.Roots().Count);
        Assert.Throws<FileNotFoundException>(() => project.OpenScene("res://scenes/none.ascene"));
        Assert.Equal(3, rig.World.Roots().Count); // a scene that is not there leaves the open one alone
    }

    [Fact]
    public void PlayRunsTheGamesScriptsOverTheSceneAndStopPutsTheWorldBackUnderTheSameIds()
    {
        string script = """
            local Mark = world:component("Mark", { n = "i32" })
            local spawned = world:create_entity("from_script")
            world:set(spawned, Mark, { n = 7 })
            -- the scene's own entities are there before the scripts run
            local hero = world:lookup("Hero")
            assert(hero ~= 0, "the scene was loaded before the scripts ran")
            world:destroy(hero)
            """;
        using var rig = new ProjectRig(script);
        EditorProject project = rig.Project;
        Entity hero = project.CreateEmpty();
        project.Rename(hero, "Hero");
        rig.World.Set(hero, new Transform(new Double3(5, 6, 7), Quaternion.Identity, Vector3.One));
        Entity keep = project.CreateEmpty();
        project.Selection = hero;
        project.History.Seal();

        Assert.True(project.Play(), project.PlayError);
        Assert.True(project.IsPlaying);
        Entity playHero = rig.World.Lookup("Hero");
        Assert.True(playHero.IsNull);                                    // the game's script destroyed it
        Assert.False(rig.World.Lookup("from_script").IsNull);           // and made one
        project.Update(1f / 60f);
        Assert.True(rig.World.Frame > 0);

        project.Stop();
        Assert.False(project.IsPlaying);
        Assert.True(rig.World.Lookup("from_script").IsNull);            // the play run's entity is gone
        Assert.True(rig.World.IsAlive(hero));                           // the destroyed one is back, under the same id
        Assert.Equal("Hero", rig.World.GetName(hero));
        Assert.Equal(new Double3(5, 6, 7), rig.World.Get<Transform>(hero).Position);
        Assert.True(rig.World.IsAlive(keep));
        Assert.Equal(hero, project.Selection);                           // the selection survived
        Assert.True(project.History.CanUndo);                            // so did the history, and it still works on the same entities
        project.History.Undo();
        Assert.False(rig.World.IsAlive(keep));
    }

    [Fact]
    public void AGameWhoseScriptFailsLeavesTheEditorEditingWhereItWas()
    {
        using var rig = new ProjectRig("error('the game is broken')");
        EditorProject project = rig.Project;
        Entity a = project.CreateEmpty();

        Assert.False(project.Play());
        Assert.False(project.IsPlaying);
        Assert.Contains("the game is broken", project.PlayError, StringComparison.Ordinal);
        Assert.True(rig.World.IsAlive(a));                               // the scene is as it was
        Assert.Equal(a, project.Selection);
        project.CreateEmpty();                                           // and editing works
    }

    [Fact]
    public void DeletingSeveralIsOneStep()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        Entity a = project.CreateEmpty();
        Entity child = project.CreateEmpty(a);
        Entity b = project.CreateEmpty();
        Entity keep = project.CreateEmpty();
        int steps = project.History.Steps.Count;

        project.Selected.Set([a, child, b]);
        project.Delete(project.Selected.TopLevel());                      // the child rides with a: two branches, three entities

        Assert.False(rig.World.IsAlive(a));
        Assert.False(rig.World.IsAlive(child));
        Assert.False(rig.World.IsAlive(b));
        Assert.True(rig.World.IsAlive(keep));
        Assert.Equal(steps + 1, project.History.Steps.Count);
        Assert.Equal("Delete 2 entities", project.History.UndoName);
        Assert.True(project.Selected.IsEmpty);                            // what was deleted leaves the selection

        project.History.Undo();                                           // one undo brings every one back, under the same ids and parents
        Assert.True(rig.World.IsAlive(a));
        Assert.True(rig.World.IsAlive(child));
        Assert.True(rig.World.IsAlive(b));
        Assert.Equal(a, rig.World.GetParent(child));
        Assert.True(project.Selected.IsEmpty);                            // the selection is not part of the undo
        Assert.Equal(steps, project.History.Steps.Count);

        project.Delete(child);                                            // one entity keeps its own name
        Assert.Equal("Delete Empty", project.History.UndoName);
        project.Delete([]);                                               // nothing: no step
        Assert.Equal(steps + 1, project.History.Steps.Count);
    }

    [Fact]
    public void ReparentingSeveralIsOneStepAndKeepsNamesUnique()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        Entity target = project.CreateEmpty();
        Entity other = project.CreateEmpty();
        Entity x = project.CreateEmpty();
        Entity y = project.CreateEmpty(other);
        project.Rename(target, "Target");
        project.Rename(other, "Other");
        project.Rename(x, "Item");
        project.Rename(y, "Item");
        int steps = project.History.Steps.Count;

        Assert.True(project.Reparent([x, y], target));                    // two entities with the same name end up under one parent
        Assert.Equal(steps + 1, project.History.Steps.Count);
        Assert.Equal("Move 2 entities", project.History.UndoName);
        Assert.Equal(target, rig.World.GetParent(x));
        Assert.Equal(target, rig.World.GetParent(y));
        Assert.Equal(["Item", "Item 2"], rig.World.Children(target).Select(rig.World.GetName).Order());
        Assert.False(project.Reparent([x, y], target));                   // already there: nothing to do
        Assert.False(project.Reparent([target], target));                 // not under itself

        project.History.Undo();
        Assert.True(rig.World.GetParent(x).IsNull);
        Assert.Equal(other, rig.World.GetParent(y));
        Assert.Equal("Item", rig.World.GetName(y));                       // and the names as they were
        Assert.Equal("Item", rig.World.GetName(x));
    }

    [Fact]
    public void SeveralSelectedEntitiesSurvivePlayAndStop()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        Entity a = project.CreateEmpty();
        Entity b = project.CreateEmpty();
        Entity c = project.CreateEmpty();
        project.Selected.Set([a, c, b], active: c);

        Assert.True(project.Play(), project.PlayError);
        project.Stop();                                                   // the editing world is back under the same ids

        Assert.Equal([a, c, b], project.Selected.Items);
        Assert.Equal(c, project.Selection);
        project.Selected.Remove(c);                                       // and the selection is still editable
        Assert.Equal(b, project.Selection);
    }

    [Fact]
    public void UndoingBackToTheSavedSceneIsNotDirty()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        Assert.False(project.Dirty);                                      // nothing yet

        Entity a = project.CreateEmpty();
        Assert.True(project.Dirty);
        project.SaveSceneAs("res://scenes/level");
        Assert.False(project.Dirty);

        project.CreateEmpty();
        Assert.True(project.Dirty);
        project.History.Undo();
        Assert.False(project.Dirty);                                      // back at the saved state
        project.History.Undo();
        Assert.True(project.Dirty);                                       // before it
        project.History.Redo();
        Assert.False(project.Dirty);
        project.History.Redo();
        Assert.True(project.Dirty);
        project.History.Undo();

        // A drag after the save is one step that merges within itself and leaves the saved state when it starts.
        Assert.False(project.Dirty);
        Entity transform = rig.World.Component<Transform>();
        for (int i = 1; i <= 3; i++)
        {
            Transform moved = Transform.At(new Double3(i, 0, 0));
            byte[] bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<Transform>(in moved)).ToArray();
            project.History.Execute(SetComponentCommand.Create(project.WorldRef, a, transform, bytes, "Move")!);
            Assert.True(project.Dirty);
        }

        project.History.Seal();
        project.History.Undo();                                           // the whole drag in one undo
        Assert.False(project.Dirty);

        // Saving again moves the save point; a new or opened scene starts clean whatever the history held.
        project.CreateEmpty();
        Assert.True(project.SaveScene());
        Assert.False(project.Dirty);
        project.History.Undo();
        Assert.True(project.Dirty);
        project.NewScene();
        Assert.False(project.Dirty);
        project.CreateEmpty();
        project.OpenScene("res://scenes/level.ascene");
        Assert.False(project.Dirty);
    }

    [Fact]
    public void ModelBoundsCanBeMeasuredWithNoGpuAndPickingFindsEntitiesByThem()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        Entity far = project.CreateEmpty(default, new Double3(0, 0, -20));
        Entity near = project.CreateEmpty(default, new Double3(0, 0, -5));
        Entity aside = project.CreateEmpty(default, new Double3(8, 0, -5));
        project.World.Progress(0f);                                      // WorldTransform is computed by the world's systems

        var ray = new PickRay(new Double3(0, 0, 0), new Vector3(0, 0, -1));
        Assert.Equal(near, Picking.Pick(rig.World, ray, _ => null));     // no models: each is a marker sphere, the nearer wins
        var miss = new PickRay(new Double3(0, 0, 0), Vector3.Normalize(new Vector3(0, 1, -1)));
        Assert.Null(Picking.Pick(rig.World, miss, _ => null));
        Assert.Equal(aside, Picking.Pick(rig.World, new PickRay(new Double3(8, 0, 0), new Vector3(0, 0, -1)), _ => null));
        Assert.Equal(far, Picking.Pick(rig.World, ray, _ => null, [far]));   // a candidate list narrows the search
    }
}
