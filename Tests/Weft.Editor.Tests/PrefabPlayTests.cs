using System;
using System.IO;
using System.Linq;
using Weft.Core.Assets;
using Weft.Ecs;
using Weft.Editor.Editing;
using Weft.Host.Editing;
using Weft.Reflection;
using Weft.Rendering;
using Xunit;

namespace Weft.Editor.Tests;

/// <summary>Prefab instances in the editor's Play and Stop: the run is made from the scene's text and Stop puts the snapshot back into a new session, so what an instance holds has to survive both.</summary>
[Collection("editor")]
public sealed class PrefabPlayTests
{
    private const string BoxPath = "res://models/box.glb";
    private const string BallPath = "res://models/ball.glb";

    [Fact]
    public void PlayAndStopKeepInstancesTheirOverridesAndModels()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        AssetUid uid = AssetUid.New();
        rig.Write("game/models/box.glb", "not a real model");               // only the paths matter here
        rig.Write("game/models/ball.glb", "not a real model");
        rig.Write("game/models/other.glb", "not a real model");
        rig.Write("game/prefabs/lamp.wprf", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Prefab version="1" uid="{uid}" nextId="3">
              <Entity id="1" name="Lamp">
                <Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/>
                <ModelRenderer Model="{BoxPath}"/>
                <Entity id="2" name="Bulb">
                  <Transform Position="0 1.8 0" Rotation="0 0 0 1" Scale="1 1 1"/>
                  <Light Color="1 0.9 0.7" Intensity="80" Range="12" InnerCone="0" OuterCone="0" Kind="1"/>
                </Entity>
              </Entity>
            </Prefab>
            """);
        rig.Write("game/scenes/yard.wscn", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Scene version="2">
              <Entity id="1" name="Lamp" prefab="res://prefabs/lamp.wprf|{uid}">
                <Transform Position="3 0 0"/>
                <ModelRenderer Model="{BallPath}"/>
                <Override entity="2"><Light Intensity="40"/></Override>
              </Entity>
            </Scene>
            """);

        // A model that the scene does not use takes the first number, so that the numbers of the models of the scene are not the ones a fresh session gives them.
        uint other = project.Session.Models.Load("res://models/other.glb");
        project.OpenScene("res://scenes/yard.wscn");
        Entity first = rig.World.Lookup("Lamp");
        Entity firstBulb = rig.World.Children(first).Single();

        // A second instance, made in the editor as a step of its history, with a bulb that was edited and not saved.
        project.History.Execute(PrefabCommands.Instantiate(project.WorldRef, "res://prefabs/lamp.wprf"));
        Entity second = rig.World.Lookup("Lamp 2");
        Entity secondBulb = rig.World.Children(second).Single();
        Light dim = rig.World.Get<Light>(secondBulb);
        dim.Intensity = 7;
        rig.World.Set(secondBulb, dim);
        project.Selection = first;
        Assert.NotEqual(other, rig.World.Get<ModelRenderer>(first).Model);
        string before = rig.World.SaveScene();
        Assert.Contains("<Scene version=\"2\"", before, StringComparison.Ordinal);

        Assert.True(project.Play(), project.PlayError);

        // The run holds the same two instances, made from the text of the scene by a session of its own: overrides, models and the instance itself.
        Assert.True(project.IsPlaying);
        Entity played = rig.World.Lookup("Lamp");
        Entity played2 = rig.World.Lookup("Lamp 2");
        Assert.False(played.IsNull);
        Assert.False(played2.IsNull);
        Assert.NotNull(rig.World.PrefabOf(played));
        Assert.Equal(40f, rig.World.Get<Light>(rig.World.Children(played).Single()).Intensity);
        Assert.Equal(7f, rig.World.Get<Light>(rig.World.Children(played2).Single()).Intensity);
        Assert.Equal(BallPath, project.Session.Models.PathOf(rig.World.Get<ModelRenderer>(played).Model));
        Assert.Equal(BoxPath, project.Session.Models.PathOf(rig.World.Get<ModelRenderer>(played2).Model));
        project.Update(1f / 60f);

        project.Stop();

        // The editing world is back with the same entities under the same ids, and the models are the ones of the new session's library.
        Assert.False(project.IsPlaying);
        Assert.True(rig.World.IsAlive(first));
        Assert.True(rig.World.IsAlive(firstBulb));
        Assert.True(rig.World.IsAlive(second));
        Assert.True(rig.World.IsAlive(secondBulb));
        Assert.Equal(first, rig.World.Lookup("Lamp"));
        Assert.Equal(BallPath, project.Session.Models.PathOf(rig.World.Get<ModelRenderer>(first).Model));
        Assert.Equal(BoxPath, project.Session.Models.PathOf(rig.World.Get<ModelRenderer>(second).Model));
        Assert.Equal($"res://prefabs/lamp.wprf|{uid}", rig.World.PrefabOf(first));
        Assert.Equal($"res://prefabs/lamp.wprf|{uid}", rig.World.PrefabOf(second));
        Assert.Equal(40f, rig.World.Get<Light>(firstBulb).Intensity);
        Assert.Equal(7f, rig.World.Get<Light>(secondBulb).Intensity);
        Assert.Equal(first, project.Selection);
        Assert.Equal(before, rig.World.SaveScene());                          // overrides and all: what is saved is what was there

        // The history still works on the entities of the instance that was made before Play.
        Assert.True(project.History.CanUndo);
        project.History.Undo();
        Assert.False(rig.World.IsAlive(second));
        Assert.False(rig.World.IsAlive(secondBulb));
        Assert.True(rig.World.IsAlive(first));
        project.History.Redo();
        Assert.Equal(1, rig.World.Roots().Count(r => rig.World.GetName(r) == "Lamp 2"));
        Assert.True(File.Exists(Path.Combine(rig.Root, "game", "prefabs", "lamp.wprf")));   // Play does not touch the files
    }
}
