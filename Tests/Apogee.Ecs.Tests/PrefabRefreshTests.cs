using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Content.Xml;
using Apogee.Ecs;
using Apogee.Reflection;
using Xunit;

namespace Apogee.Ecs.Tests;

/// <summary>
/// Instances follow their prefab: a file that changes (edited by hand, saved by an editor, hot reloaded) makes the instances in the open worlds follow it in place, keeping the entities, what the
/// instance changed and what it could not apply, and an instance that cannot follow is left exactly as it was.
/// </summary>
public sealed class PrefabRefreshTests : PrefabTestBase
{
    private const string BaseLamp = "<Entity id=\"3\" name=\"Base\"><Transform Position=\"0 0 0\" Rotation=\"0 0 0 1\" Scale=\"1 1 1\"/></Entity>";
    private const string BulbHealth = "<Health Current=\"5\" Max=\"9\" Regen=\"0.5\"/>";

    private string TwoLamps(string body = "") => $"""
        <Scene version="2">
          <Entity id="1" name="Player"><Transform Position="1 2 3" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
          <Entity id="2" name="Lamp" prefab="{Lamp}">{body}</Entity>
          <Entity id="3" name="Lamp 2" prefab="{Lamp}"/>
          <Entity id="4" name="Switch"><Marks Target="2"/></Entity>
        </Scene>
        """;

    // Writes a prefab file as `edit` changes it, reads it again and applies the update: what a hot reload does.
    private PrefabUpdate Change(World world, string path, Func<string, string> edit)
    {
        Write(path, edit(Read(path)));
        PrefabReload reload = world.Prefabs.Reload("res://" + path);
        Assert.Equal(PrefabReloadStatus.Reloaded, reload.Status);
        return world.ApplyPrefabUpdates() ?? throw new InvalidOperationException("nothing to apply");
    }

    private PrefabUpdate ChangeLamp(Func<string, string> edit) => Change(World, "prefabs/lamp.aprefab", edit);

    private static string Replace(string text, string from, string to)
    {
        Assert.Contains(from, text, StringComparison.Ordinal);
        return text.Replace(from, to, StringComparison.Ordinal);
    }

    private static string Names(World world, Entity root) => string.Join(",", world.Subtree(root).Select(e => world.GetName(e) ?? "-"));

    [Fact]
    public void EditingAPrefabUpdatesItsInstances()
    {
        World.LoadScene(TwoLamps());
        Entity lamp = World.Lookup("Lamp");
        Entity lamp2 = World.Lookup("Lamp 2");

        PrefabUpdate update = ChangeLamp(text => Replace(Replace(Replace(text, BulbHealth, "<Health Current=\"8\" Max=\"9\" Regen=\"4\"/>"), BaseLamp, ""),
            "<Entity id=\"2\" name=\"Bulb\">", "<Entity id=\"4\" name=\"Shade\"><Transform Position=\"0 2 0\" Rotation=\"0 0 0 1\" Scale=\"1 1 1\"/></Entity><Entity id=\"2\" name=\"Bulb\">"));

        Assert.Equal([lamp, lamp2], update.Updated);
        foreach (Entity instance in new[] { lamp, lamp2 })
        {
            Assert.Equal(["Bulb", "Shade"], NamesBelow(World, instance));                                           // Base is gone, Shade is new
            Entity bulb = Child(instance, "Bulb");
            Assert.Equal(8, World.Get<Health>(bulb).Current);
            Assert.Equal(4f, World.Get<Health>(bulb).Regen);
        }

        Assert.Equal(2, update.Created.Count);                       // a Shade each
        Assert.Equal(2, update.Destroyed.Count);                     // a Base each
        Assert.Equal([LampPath], update.Prefabs);
    }

    [Fact]
    public void AnOverrideSurvivesAPrefabChange()
    {
        // The scene changes the lamp's Max to 40; the prefab moves Max to 90 and Regen to 2: the instance keeps its 40 and follows the rest.
        World.LoadScene(TwoLamps("<Override entity=\"2\"><Health Max=\"40\"/></Override>"));
        Entity bulb = Child(World.Lookup("Lamp"), "Bulb");
        Assert.Equal(40, World.Get<Health>(bulb).Max);

        ChangeLamp(text => Replace(text, BulbHealth, "<Health Current=\"5\" Max=\"90\" Regen=\"2\"/>"));

        Assert.Equal(40, World.Get<Health>(bulb).Max);
        Assert.Equal(2f, World.Get<Health>(bulb).Regen);
        Assert.Equal(90, World.Get<Health>(Child(World.Lookup("Lamp 2"), "Bulb")).Max);       // the one that changed nothing follows
        Assert.Equal([("Max", "40")], XmlNode.Parse(World.SaveScene()).Children.Single(c => c.Attribute("name") == "Lamp").Child("Override")!.Child("Health")!.Attributes.Select(a => (a.Name, a.Value)));
    }

    [Fact]
    public void InstancesKeepTheirEntityIdsWhenThePrefabChanges()
    {
        World.LoadScene(TwoLamps());
        Entity lamp = World.Lookup("Lamp");
        Entity bulb = Child(lamp, "Bulb");
        Entity switchEntity = World.Lookup("Switch");
        Assert.Equal(lamp, World.Get<Marks>(switchEntity).Target);
        Entity before = bulb;

        ChangeLamp(text => Replace(text, BulbHealth, "<Health Current=\"1\" Max=\"2\" Regen=\"3\"/>"));

        Assert.Equal(before, Child(lamp, "Bulb"));
        Assert.True(World.IsAlive(lamp));
        Assert.Equal(lamp, World.Get<Marks>(switchEntity).Target);                              // what pointed at the instance still does
        Assert.Equal(1, World.Get<Health>(bulb).Current);
        Assert.Equal(lamp, World.InstanceOf(bulb));
        Assert.True(World.IsPrefabMember(bulb));
    }

    [Fact]
    public void AnEntityAddedToThePrefabAppearsInEveryInstance()
    {
        World.LoadScene(TwoLamps());

        PrefabUpdate update = ChangeLamp(text => Replace(text, BaseLamp, BaseLamp + "<Entity id=\"4\" name=\"Cord\"><Transform Position=\"0 0 1\" Rotation=\"0 0 0 1\" Scale=\"1 1 1\"/><Marks Target=\"3\"/></Entity>"));

        Assert.Equal(2, update.Created.Count);
        foreach (string name in new[] { "Lamp", "Lamp 2" })
        {
            Entity instance = World.Lookup(name);
            Entity cord = Child(instance, "Cord");
            Assert.Equal(["Bulb", "Base", "Cord"], NamesBelow(World, instance));
            Assert.Equal(Child(instance, "Base"), World.Get<Marks>(cord).Target);             // the new entity's reference is to the instance's own Base
            Assert.True(World.IsPrefabMember(cord));
            Assert.Equal(4u, World.Get<PrefabMember>(cord).Id);
        }
    }

    [Fact]
    public void AnEntityTheInstanceRemovedStaysRemoved()
    {
        World.LoadScene(TwoLamps("<Override entity=\"3\" remove=\"true\"/>"));
        Assert.Equal(["Bulb"], NamesBelow(World, World.Lookup("Lamp")));

        ChangeLamp(text => Replace(text, BulbHealth, "<Health Current=\"6\" Max=\"9\" Regen=\"0.5\"/>"));

        Assert.Equal(["Bulb"], NamesBelow(World, World.Lookup("Lamp")));
        Assert.Equal(["Bulb", "Base"], NamesBelow(World, World.Lookup("Lamp 2")));
        Assert.Contains("remove=\"true\"", World.SaveScene(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntityThePrefabDeletedKeepsItsAddedChildrenUnapplied()
    {
        World.LoadScene(TwoLamps("<Override entity=\"3\"><Marks Level=\"4\" append=\"true\"/><Entity id=\"7\" name=\"Sign\"><Transform Position=\"0 1 0\" Rotation=\"0 0 0 1\" Scale=\"1 1 1\"/></Entity></Override>"));
        Entity lamp = World.Lookup("Lamp");
        Assert.Equal(["Sign"], NamesBelow(World, Child(lamp, "Base")));
        string mended = Read("prefabs/lamp.aprefab");

        PrefabUpdate update = ChangeLamp(text => Replace(text, BaseLamp, ""));

        Assert.Equal(["Bulb"], NamesBelow(World, lamp));
        Assert.Equal(3, update.Destroyed.Count);                                              // the Base of each lamp and the Sign
        Assert.Contains("Sign", World.SaveScene(), StringComparison.Ordinal);               // not lost: it is in the instance's element, kept aside
        string whileGone = World.SaveScene();
        using World reloaded = OtherWorld();
        reloaded.LoadScene(whileGone);
        Assert.Equal(whileGone, reloaded.SaveScene());

        // The prefab gets its Base back (a version control merge): what was added to it comes back with it.
        Write("prefabs/lamp.aprefab", mended);
        Assert.Equal(PrefabReloadStatus.Reloaded, World.Prefabs.Reload(LampPath).Status);
        World.ApplyPrefabUpdates();

        Entity back = Child(lamp, "Base");
        Assert.Equal(["Sign"], NamesBelow(World, back));
        Assert.Equal(4, World.Get<Marks>(back).Level);
        Assert.Equal(["Bulb", "Base"], NamesBelow(World, lamp));
    }

    [Fact]
    public void ANestedPrefabChangeReachesOuterInstances()
    {
        World.LoadScene($"<Scene version=\"2\"><Entity id=\"1\" name=\"Corner\" prefab=\"{Corner}\"/></Scene>");
        Entity corner = World.Lookup("Corner");
        Entity lamp = Child(corner, "Lamp");
        Entity bulb = Child(lamp, "Bulb");
        Assert.Equal(9, World.Get<Health>(bulb).Current);                                       // the corner's own override of its lamp

        PrefabUpdate update = ChangeLamp(text => Replace(text, BulbHealth, "<Health Current=\"5\" Max=\"77\" Regen=\"0.5\"/>"));

        Assert.Equal([corner], update.Updated);                                                  // the instance of corner, not its inner lamps
        Assert.Equal(bulb, Child(Child(corner, "Lamp"), "Bulb"));
        Assert.Equal(77, World.Get<Health>(bulb).Max);
        Assert.Equal(9, World.Get<Health>(bulb).Current);                                       // the inner override is applied over the new lamp
        Assert.Equal(77, World.Get<Health>(Child(Child(corner, "Lamp 2"), "Bulb")).Max);

        // An edit of the corner itself works the same way.
        PrefabUpdate again = Change(World, "prefabs/corner.aprefab", text => Replace(text, "<Entity id=\"4\" name=\"Bench\">", "<Entity id=\"5\" name=\"Stool\"/><Entity id=\"4\" name=\"Bench\">"));
        Assert.Equal([corner], again.Updated);
        Assert.Equal(["Lamp", "Lamp 2", "Bench", "Stool"], NamesBelow(World, corner));
    }

    [Fact]
    public void IgnoredComponentsAreNotTouchedByARefresh()
    {
        World.IgnoreInScenes("Furnished");
        World.LoadScene(TwoLamps());
        Entity bulb = Child(World.Lookup("Lamp"), "Bulb");
        World.Set(bulb, new Furnished { Count = 3 });                                           // like a physics body: only this run has it

        ChangeLamp(text => Replace(text, BulbHealth, "<Health Current=\"6\" Max=\"9\" Regen=\"0.5\"/>"));

        Assert.Equal(3, World.Get<Furnished>(bulb).Count);
        Assert.Equal(6, World.Get<Health>(bulb).Current);
    }

    [Fact]
    public void ABrokenPrefabFileKeepsTheOldVersion()
    {
        World.LoadScene(TwoLamps());
        string good = Read("prefabs/lamp.aprefab");
        string before = World.SaveScene();

        Write("prefabs/lamp.aprefab", "<Prefab version=\"1\"><Entity id=\"1\" name=\"Lamp\">");     // cut off in the middle of a save
        PrefabReload reload = World.Prefabs.Reload(LampPath);

        Assert.Equal(PrefabReloadStatus.Failed, reload.Status);
        Assert.NotNull(reload.Error);
        Assert.Null(World.ApplyPrefabUpdates());                                                 // nothing was announced
        Assert.Equal(["Bulb", "Base"], NamesBelow(World, World.Lookup("Lamp")));
        Assert.Equal(before, World.SaveScene());

        // Not a prefab at all, or a prefab whose entities are numbered twice: the same.
        Write("prefabs/lamp.aprefab", "<Scene version=\"1\"/>");
        Assert.Equal(PrefabReloadStatus.Failed, World.Prefabs.Reload(LampPath).Status);
        Write("prefabs/lamp.aprefab", Replace(good, "<Entity id=\"3\" name=\"Base\">", "<Entity id=\"2\" name=\"Base\">"));
        Assert.Equal(PrefabReloadStatus.Failed, World.Prefabs.Reload(LampPath).Status);

        // Mended, it is read again.
        Write("prefabs/lamp.aprefab", Replace(good, BulbHealth, "<Health Current=\"5\" Max=\"11\" Regen=\"0.5\"/>"));
        Assert.Equal(PrefabReloadStatus.Reloaded, World.Prefabs.Reload(LampPath).Status);
        World.ApplyPrefabUpdates();
        Assert.Equal(11, World.Get<Health>(Child(World.Lookup("Lamp"), "Bulb")).Max);
    }

    [Fact]
    public void APrefabThatContainsItselfIsNotAdopted()
    {
        World.LoadScene(TwoLamps());
        string before = World.SaveScene();
        string good = Read("prefabs/lamp.aprefab");

        Write("prefabs/lamp.aprefab", Replace(good, BaseLamp, $"<Entity id=\"4\" name=\"Again\" prefab=\"{LampPath}\"/>"));
        PrefabReload direct = World.Prefabs.Reload(LampPath);

        Assert.Equal(PrefabReloadStatus.Failed, direct.Status);
        Assert.Contains("contains itself", direct.Error, StringComparison.Ordinal);
        Assert.Null(World.ApplyPrefabUpdates());
        Assert.Equal(before, World.SaveScene());

        // Through another prefab: the corner holds lamps, so a lamp that holds a corner is a loop too.
        Write("prefabs/lamp.aprefab", Replace(good, BaseLamp, $"<Entity id=\"4\" name=\"Loop\" prefab=\"{CornerPath}\"/>"));
        PrefabReload indirect = World.Prefabs.Reload(LampPath);
        Assert.Equal(PrefabReloadStatus.Failed, indirect.Status);
        Assert.Contains("contains itself", indirect.Error, StringComparison.Ordinal);
        Assert.Equal(before, World.SaveScene());
    }

    [Fact]
    public void APrefabThatDisappearsKeepsItsInstances()
    {
        World.LoadScene(TwoLamps("<Override entity=\"2\"><Health Max=\"40\"/></Override>"));
        string before = World.SaveScene();
        string text = Read("prefabs/lamp.aprefab");

        Delete("prefabs/lamp.aprefab");
        PrefabReload reload = World.Prefabs.Reload(LampPath);

        Assert.Equal(PrefabReloadStatus.Failed, reload.Status);
        Assert.Null(World.ApplyPrefabUpdates());
        Assert.Equal(["Bulb", "Base"], NamesBelow(World, World.Lookup("Lamp")));
        Assert.Equal(before, World.SaveScene());                                                  // nothing about the instances was lost

        // It comes back (a branch is switched back): the same file is not a change.
        Write("prefabs/lamp.aprefab", text);
        Assert.Equal(PrefabReloadStatus.Unchanged, World.Prefabs.Reload(LampPath).Status);
    }

    [Fact]
    public void AMissingPrefabThatAppearsMakesItsInstancesWhole()
    {
        const string Later = "res://prefabs/later.aprefab";
        string scene = $"""
            <Scene version="2">
              <Entity id="1" name="Thing" prefab="{Later}">
                <Transform Position="3 0 0"/>
                <Override entity="2"><Health Max="40"/></Override>
              </Entity>
            </Scene>
            """;
        World.LoadScene(scene);
        Entity thing = World.Lookup("Thing");
        Assert.Empty(World.Children(thing));

        Write("prefabs/later.aprefab", """
            <Prefab version="1" nextId="3">
              <Entity id="1" name="Thing"><Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/>
                <Entity id="2" name="Part"><Health Current="1" Max="2" Regen="0"/></Entity>
              </Entity>
            </Prefab>
            """);
        PrefabReload reload = World.Prefabs.Reload(Later);
        Assert.Equal(PrefabReloadStatus.Reloaded, reload.Status);
        PrefabUpdate update = World.ApplyPrefabUpdates()!;

        Assert.Equal([thing], update.Updated);
        Entity part = Child(thing, "Part");
        Assert.Equal(40, World.Get<Health>(part).Max);                                           // what the scene said about it all along
        Assert.Equal(3.0, World.Get<Transform>(thing).Position.X);
    }

    [Fact]
    public void AFileSavedByAnEditorUpdatesInstancesInEveryWorldThatFollowsTheLibrary()
    {
        using World other = OtherWorld();
        other.Prefabs = World.Prefabs;                                                          // one library for two worlds
        World.LoadScene(TwoLamps());
        other.LoadScene(TwoLamps());

        // A third world makes a lamp, changes it and saves it as the prefab.
        using World editing = OtherWorld();
        editing.Prefabs = World.Prefabs;
        Entity made = editing.Instantiate(LampPath);
        editing.Set(Child(editing, made, "Bulb"), new Health { Current = 12, Max = 13, Regen = 0 });
        editing.SavePrefabFile(LampPath, made);

        foreach (World world in new[] { World, other, editing })
        {
            PrefabUpdate update = world.ApplyPrefabUpdates()!;
            Assert.NotNull(update);
        }

        Assert.Equal(12, World.Get<Health>(Child(World.Lookup("Lamp"), "Bulb")).Current);
        Assert.Equal(12, other.Get<Health>(Child(other, other.Lookup("Lamp 2"), "Bulb")).Current);
        Assert.Equal(12, editing.Get<Health>(Child(editing, made, "Bulb")).Current);                       // the instance it was saved from has what it was saved with
        Assert.Equal(PrefabReloadStatus.Unchanged, World.Prefabs.Reload(LampPath).Status);         // the watcher reporting the same save again is no change
    }

    [Fact]
    public void SeveralChangesAreOneUpdate()
    {
        World.LoadScene($"<Scene version=\"2\"><Entity id=\"1\" name=\"Corner\" prefab=\"{Corner}\"/><Entity id=\"2\" name=\"Lamp\" prefab=\"{Lamp}\"/></Scene>");
        var seen = new List<PrefabUpdate>();
        var signalled = new List<string>();
        World.PrefabsChanged += seen.Add;
        _ = World.PrefabChanged.Connect(signalled.Add);

        Write("prefabs/lamp.aprefab", Replace(Read("prefabs/lamp.aprefab"), BulbHealth, "<Health Current=\"5\" Max=\"31\" Regen=\"0.5\"/>"));
        Write("prefabs/corner.aprefab", Replace(Read("prefabs/corner.aprefab"), "<Entity id=\"4\" name=\"Bench\">", "<Entity id=\"5\" name=\"Stool\"/><Entity id=\"4\" name=\"Bench\">"));
        Assert.Equal(PrefabReloadStatus.Reloaded, World.Prefabs.Reload(LampPath).Status);
        Assert.Equal(PrefabReloadStatus.Reloaded, World.Prefabs.Reload(CornerPath).Status);
        Assert.True(World.HasPendingPrefabUpdates);
        Assert.Empty(seen);                                                                       // nothing changes until the host says it is safe

        PrefabUpdate update = World.ApplyPrefabUpdates()!;

        Assert.False(World.HasPendingPrefabUpdates);
        Assert.Same(update, Assert.Single(seen));
        Assert.Equal([LampPath, CornerPath], update.Prefabs);
        Assert.Equal(2, update.Updated.Count);                                                    // the corner once, though two files changed under it
        Assert.Equal([LampPath, CornerPath], signalled);
        Assert.Equal(31, World.Get<Health>(Child(Child(World.Lookup("Corner"), "Lamp 2"), "Bulb")).Max);
        Assert.Equal(["Lamp", "Lamp 2", "Bench", "Stool"], NamesBelow(World, World.Lookup("Corner")));
        Assert.Null(World.ApplyPrefabUpdates());
    }

    [Fact]
    public void TheUpdateSaysWhatItMadeAndDestroyed()
    {
        World.LoadScene(TwoLamps());
        Entity lamp = World.Lookup("Lamp");
        Entity oldBase = Child(lamp, "Base");

        PrefabUpdate update = ChangeLamp(text => Replace(text, BaseLamp, "<Entity id=\"5\" name=\"Cap\"/>"));

        Assert.Contains(oldBase, update.Destroyed);
        Assert.False(World.IsAlive(oldBase));
        Assert.Contains(Child(lamp, "Cap"), update.Created);
        Assert.Contains(lamp, update.Updated);
        Assert.Empty(update.Failed);
        Assert.True(update.Touched);
    }

    [Fact]
    public void AnInstanceMadeAfterTheChangeIsLeftAlone()
    {
        World.LoadScene(TwoLamps());
        Write("prefabs/lamp.aprefab", Replace(Read("prefabs/lamp.aprefab"), BaseLamp, BaseLamp + "<Entity id=\"4\" name=\"Cord\"/>"));
        Assert.Equal(PrefabReloadStatus.Reloaded, World.Prefabs.Reload(LampPath).Status);

        Entity fresh = World.Instantiate(LampPath);                                              // made from the new file, before the update
        PrefabUpdate update = World.ApplyPrefabUpdates()!;

        Assert.DoesNotContain(fresh, update.Updated);
        Assert.Equal(["Bulb", "Base", "Cord"], NamesBelow(World, fresh));                       // one Cord, not two
        Assert.Equal(["Bulb", "Base", "Cord"], NamesBelow(World, World.Lookup("Lamp")));
    }

    [Fact]
    public void AnInstanceThatCannotBeUpdatedIsLeftAsItWas()
    {
        World.LoadScene(TwoLamps("<Override entity=\"2\"><Health Max=\"40\"/></Override>"));
        World.MaxLoadEntities = 4;                                                               // the lamp is three entities
        Entity lamp = World.Lookup("Lamp");
        Entity bulb = Child(lamp, "Bulb");

        // The new lamp is eight: too many to expand, so no instance of it can be updated.
        string many = string.Concat(Enumerable.Range(10, 5).Select(n => $"<Entity id=\"{n}\" name=\"Extra{n}\"/>"));
        Write("prefabs/lamp.aprefab", Replace(Read("prefabs/lamp.aprefab"), BaseLamp, BaseLamp + many));
        Assert.Equal(PrefabReloadStatus.Reloaded, World.Prefabs.Reload(LampPath).Status);
        PrefabUpdate update = WithWarnings(() => World.ApplyPrefabUpdates()!, out _);

        Assert.Empty(update.Updated);
        Assert.Equal([lamp, World.Lookup("Lamp 2")], update.Failed.Select(f => f.Instance));
        Assert.Contains("more than", update.Failed[0].Message, StringComparison.Ordinal);
        Assert.Equal(["Bulb", "Base"], NamesBelow(World, lamp));
        Assert.Equal(bulb, Child(lamp, "Bulb"));
        Assert.Equal(40, World.Get<Health>(bulb).Max);
        Assert.Empty(update.Created);
        Assert.Empty(update.Destroyed);
    }

    [Fact]
    public void ANameThePrefabChangesIsFollowedAndANameTheInstanceChangedIsKept()
    {
        World.LoadScene(TwoLamps("<Override entity=\"2\" name=\"Filament\"/>"));
        Entity lamp = World.Lookup("Lamp");
        Entity lamp2 = World.Lookup("Lamp 2");

        // The prefab swaps the names of its two children.
        ChangeLamp(text => Replace(Replace(Replace(text, "name=\"Bulb\"", "name=\"Tmp\""), "name=\"Base\"", "name=\"Bulb\""), "name=\"Tmp\"", "name=\"Base\""));

        Assert.Equal(["Filament", "Bulb"], NamesBelow(World, lamp));                             // the renamed one kept its own name; the other took "Bulb" without becoming "Bulb 2"
        Assert.Equal(["Base", "Bulb"], NamesBelow(World, lamp2));
        Assert.Equal(5, World.Get<Health>(Child(lamp2, "Base")).Current);                        // the entity with the health is still the first one
    }

    [Fact]
    public void WhatTheInstanceHoldsOutsideItselfIsNotLostByAnUpdate()
    {
        World.LoadScene(TwoLamps());
        Entity lamp = World.Lookup("Lamp");
        Entity bulb = Child(lamp, "Bulb");
        Entity player = World.Lookup("Player");
        Entity switchEntity = World.Lookup("Switch");
        World.Set(bulb, World.Get<Marks>(bulb) with { Target = player });                        // a member points at an entity outside the instance
        Assert.Equal(player, World.Get<Marks>(bulb).Target);

        ChangeLamp(text => Replace(text, BulbHealth, "<Health Current=\"1\" Max=\"2\" Regen=\"0\"/>"));

        Assert.Equal(player, World.Get<Marks>(Child(lamp, "Bulb")).Target);
        Assert.Equal(lamp, World.Get<Marks>(switchEntity).Target);
        Assert.Equal(lamp, World.Get<Marks>(World.Lookup("Switch")).Target);
        _ = bulb;
    }

    [Fact]
    public void AnInstanceUpdatesWhenItsPrefabWasDroppedAndHandedBackByAnotherLibrary()
    {
        // A world that has been told its library changed, then moved to another one, forgets what it was waiting for: the handles mean nothing in the new one.
        World.LoadScene(TwoLamps());
        Write("prefabs/lamp.aprefab", Replace(Read("prefabs/lamp.aprefab"), BulbHealth, "<Health Current=\"5\" Max=\"12\" Regen=\"0.5\"/>"));
        Assert.Equal(PrefabReloadStatus.Reloaded, World.Prefabs.Reload(LampPath).Status);
        Assert.True(World.HasPendingPrefabUpdates);

        World.Prefabs = new PrefabLibrary(Files, Assets);

        Assert.False(World.HasPendingPrefabUpdates);
        Assert.Null(World.ApplyPrefabUpdates());
    }

    [Fact]
    public void ADisposedWorldStopsFollowingItsLibrary()
    {
        var library = new PrefabLibrary(Files, Assets);
        uint changes = 0;
        library.Changed += _ => changes++;
        World doomed = OtherWorld();
        doomed.Prefabs = library;
        doomed.Dispose();

        Write("prefabs/lamp.aprefab", Replace(Read("prefabs/lamp.aprefab"), BulbHealth, "<Health Current=\"5\" Max=\"12\" Regen=\"0.5\"/>"));
        _ = library.Load(LampPath);
        Assert.Equal(PrefabReloadStatus.Reloaded, library.Reload(LampPath).Status);               // first read of it: nothing was held, so it is a change

        Assert.Equal(1u, changes);
    }

    [Fact]
    public void HeldUpdatesWaitAndAreNotLost()
    {
        World.LoadScene(TwoLamps());
        World.HoldPrefabUpdates = true;
        Write("prefabs/lamp.aprefab", Replace(Read("prefabs/lamp.aprefab"), BulbHealth, "<Health Current=\"5\" Max=\"12\" Regen=\"0.5\"/>"));
        Assert.Equal(PrefabReloadStatus.Reloaded, World.Prefabs.Reload(LampPath).Status);

        Assert.True(World.HoldPrefabUpdates);
        Assert.True(World.HasPendingPrefabUpdates);
        Assert.Equal(9, World.Get<Health>(Child(World.Lookup("Lamp"), "Bulb")).Max);              // the flag is for the host: the world applies when asked
        World.HoldPrefabUpdates = false;
        World.ApplyPrefabUpdates();
        Assert.Equal(12, World.Get<Health>(Child(World.Lookup("Lamp"), "Bulb")).Max);
    }

    [Fact]
    public void ASavedSceneAfterAnUpdateIsWhatSavingBeforeAndLoadingAfterGives()
    {
        string scene = TwoLamps("<Override entity=\"2\" name=\"Filament\"><Health Max=\"40\"/><Frozen append=\"true\"/></Override><Override entity=\"3\"><Entity id=\"7\" name=\"Sign\"/></Override><Entity id=\"8\" name=\"Cable\"/>");
        World.LoadScene(scene);
        string saved = World.SaveScene();

        PrefabUpdate update = ChangeLamp(text => Replace(Replace(text, BaseLamp, ""), BulbHealth, "<Health Current=\"7\" Max=\"90\" Regen=\"0.5\"/>"));
        Assert.NotEmpty(update.Updated);

        using World loaded = OtherWorld();
        loaded.LoadScene(saved);
        Assert.Equal(loaded.SaveScene(), World.SaveScene());
    }
}
