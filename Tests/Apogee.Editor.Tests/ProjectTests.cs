using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Editor.Editing;
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
        Assert.Contains("res://models/box.glb", text, StringComparison.Ordinal);   // the model by its path, not by a number

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
