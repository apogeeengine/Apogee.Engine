using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Content.Assets;
using Apogee.Core.Assets;
using Apogee.Ecs;
using Apogee.Reflection;
using Xunit;

namespace Apogee.Ecs.Tests;

/// <summary>
/// A prefab that is renamed or moved keeps its instances working: through <see cref="AssetDatabase.Move"/>, which rewrites the scenes and the prefabs that name it, and when a person moves the file by
/// hand, through the UID that every reference carries.
/// </summary>
public sealed class PrefabRenameTests : PrefabTestBase
{
    private const string ScenePath = "scenes/yard.ascene";
    private const string NewLampPath = "res://prefabs/lights/street_lamp.aprefab";

    // A scene with a corner (which holds two lamps and overrides some of them), a lamp of its own with an override, and a switch that points into the corner's lamp.
    private string Scene() => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Scene version="2" uid="uid://6m3q8c2v7x4ta">
          <Entity id="1" name="Corner" prefab="{Corner}">
            <Transform Position="10 0 4"/>
            <Override entity="2/2"><Health Current="1"/></Override>
          </Entity>
          <Entity id="2" name="Spare" prefab="{Lamp}">
            <Transform Position="0 0 -7"/>
            <Override entity="2"><Health Max="50"/></Override>
          </Entity>
          <Entity id="3" name="Switch"><Marks Target="1/2"/></Entity>
        </Scene>
        """;

    // What a scene is, as the world sees it: every entity with its place in the tree, its position and its health. A rename must not change any of it.
    private static string Describe(World world)
    {
        var lines = new List<string>();
        foreach (Entity root in world.Roots())
        {
            foreach (Entity entity in world.Subtree(root))
            {
                int depth = 0;
                for (Entity parent = world.GetParent(entity); !parent.IsNull; parent = world.GetParent(parent))
                {
                    depth++;
                }

                string at = world.Has<Transform>(entity) ? $" at {world.Get<Transform>(entity).Position.X} {world.Get<Transform>(entity).Position.Y} {world.Get<Transform>(entity).Position.Z}" : "";
                string health = world.Has<Health>(entity) ? $" health {world.Get<Health>(entity).Current}/{world.Get<Health>(entity).Max}" : "";
                string target = world.Has<Marks>(entity) ? $" -> {world.GetName(world.Get<Marks>(entity).Target) ?? "nothing"}" : "";
                lines.Add($"{new string(' ', depth)}{world.GetName(entity)}{at}{health}{target}");
            }
        }

        return string.Join('\n', lines);
    }

    private string DescribeTheScene()
    {
        using World world = OtherWorld();
        world.LoadSceneFile("res://" + ScenePath);
        return Describe(world);
    }

    [Fact]
    public void ARenamedPrefabKeepsItsInstancesWorking()
    {
        Write(ScenePath, Scene());
        string before = DescribeTheScene();
        Assert.Contains("Corner at 10 0 4", before, StringComparison.Ordinal);
        Assert.Contains("Spare at 0 0 -7", before, StringComparison.Ordinal);
        Assert.Contains("Switch -> Lamp", before, StringComparison.Ordinal);
        Assert.Contains("health 1/9", before, StringComparison.Ordinal);    // the override in the corner's lamp
        Assert.Contains("health 5/50", before, StringComparison.Ordinal);   // the spare lamp's

        // The lamp is held by the scene and, twice, by the corner prefab. A preview changes nothing, then the move does everything.
        MoveReport preview = Assets.Move(LampPath, NewLampPath, dryRun: true);
        Assert.False(preview.Applied);
        Assert.Equal(Lamp, ReadFirstPrefabAttribute(Read("prefabs/corner.aprefab")));

        MoveReport report = Assets.Move(LampPath, NewLampPath);
        Assert.True(report.Applied);
        Assert.Empty(report.Warnings);
        Assert.False(File.Exists(Path.Combine(Folder, "prefabs", "lamp.aprefab")));
        string moved = Path.Combine(Folder, "prefabs", "lights", "street_lamp.aprefab");
        Assert.True(File.Exists(moved));

        // The nested prefab and the scene say the new path and still carry the UID.
        string corner = Read("prefabs/corner.aprefab");
        string scene = Read(ScenePath);
        string renamed = $"{NewLampPath}|{LampUid}";
        Assert.Equal(2, Count(corner, $"prefab=\"{renamed}\""));
        Assert.DoesNotContain(LampPath, corner, StringComparison.Ordinal);
        Assert.Equal(1, Count(scene, $"prefab=\"{renamed}\""));
        Assert.Equal(1, Count(scene, $"prefab=\"{Corner}\""));          // the corner did not move
        Assert.DoesNotContain(LampPath, scene, StringComparison.Ordinal);
        Assert.Equal(LampUid, AuthoredAssets.ReadUid(File.ReadAllBytes(moved)));

        // The scene loads the same, in a world that has never heard of the old name...
        Assert.Equal(before, DescribeTheScene());

        // ...and saves with the new one.
        using World world = OtherWorld();
        world.LoadSceneFile("res://" + ScenePath);
        Assert.Equal(renamed, world.PrefabOf(world.Lookup("Spare")));
        Assert.Contains($"prefab=\"{renamed}\"", world.SaveScene(), StringComparison.Ordinal);
    }

    [Fact]
    public void APrefabMovedByHandLoadsThroughItsUid()
    {
        Write(ScenePath, Scene());
        string before = DescribeTheScene();
        string sceneText = Read(ScenePath);
        string cornerText = Read("prefabs/corner.aprefab");

        // Nothing rewrites anything: the file is moved, and every reference still names the old path next to the UID that finds it.
        Directory.CreateDirectory(Path.Combine(Folder, "props"));
        File.Move(Path.Combine(Folder, "prefabs", "lamp.aprefab"), Path.Combine(Folder, "props", "light.aprefab"));
        Assets.Refresh();

        Assert.Equal(before, DescribeTheScene());
        Assert.Equal(sceneText, Read(ScenePath));
        Assert.Equal(cornerText, Read("prefabs/corner.aprefab"));

        // A reference by UID alone, and one with the old path and the UID, find it too.
        using World world = OtherWorld();
        Entity byUid = world.Instantiate(AssetRef.FromUid(LampUid).ToString());
        Entity byBoth = world.Instantiate(Lamp);
        Assert.Equal(["Bulb", "Base"], NamesBelow(world, byUid));
        Assert.Equal(["Bulb", "Base"], NamesBelow(world, byBoth));
        Assert.Equal("res://props/light.aprefab|" + LampUid, world.PrefabOf(byBoth));
    }

    [Fact]
    public void FindListsInstancesAndNestedReferences()
    {
        Write(ScenePath, Scene());

        IReadOnlyList<AssetReferenceSite> found = AssetReferences.Find(Assets, new AssetRef(LampPath, LampUid));

        // The prefab's own `uid` is a place that holds its UID too; the others are the instances: two in the corner prefab (a prefab inside a prefab) and one in the scene. The scene's corner is a
        // reference to the corner, not to the lamp.
        AssetReferenceSite own = Assert.Single(found, s => s.Attribute == "uid");
        Assert.Equal((LampPath, "Prefab"), (own.Document, own.Element));
        List<AssetReferenceSite> sites = [.. found.Where(s => s.Attribute != "uid")];
        Assert.Equal(["res://prefabs/corner.aprefab", "res://prefabs/corner.aprefab", "res://" + ScenePath], sites.Select(s => s.Document).Order(StringComparer.Ordinal));
        Assert.All(sites, s => Assert.Equal("Entity", s.Element));
        Assert.All(sites, s => Assert.Equal("prefab", s.Attribute));
        Assert.All(sites, s => Assert.Equal(LampUid, s.Value.Uid));

        // The scene's corner is found as the one reference to the corner.
        AssetReferenceSite inScene = Assert.Single(AssetReferences.Find(Assets, new AssetRef(CornerPath, CornerUid)), s => s.Attribute == "prefab");
        Assert.Equal("res://" + ScenePath, inScene.Document);
        Assert.Equal(CornerPath, inScene.Value.Path);

        // After the file was moved by hand the references still name the old path: the UID finds them, whether it is asked for alone or with the new path.
        Directory.CreateDirectory(Path.Combine(Folder, "props"));
        File.Move(Path.Combine(Folder, "prefabs", "lamp.aprefab"), Path.Combine(Folder, "props", "light.aprefab"));
        Assets.Refresh();
        Assert.Equal(sites.Count, AssetReferences.Find(Assets, AssetRef.FromUid(LampUid)).Count(s => s.Attribute == "prefab"));
        Assert.Equal(sites.Count, AssetReferences.Find(Assets, new AssetRef("res://props/light.aprefab", LampUid)).Count(s => s.Attribute == "prefab"));
    }

    private static string ReadFirstPrefabAttribute(string document)
    {
        const string Start = "prefab=\"";
        int from = document.IndexOf(Start, StringComparison.Ordinal) + Start.Length;
        return document[from..document.IndexOf('"', from)];
    }

    private static int Count(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
