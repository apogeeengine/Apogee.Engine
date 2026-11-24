using System;
using System.IO;
using System.Linq;
using Weft.Content.FileSystem;
using Weft.Core.Numerics;
using Weft.Reflection;
using Xunit;

namespace Weft.Ecs.Tests;

/// <summary>
/// A prefab loaded as the file itself (<see cref="World.LoadPrefabContents"/>) and written back (<see cref="World.SavePrefabContents"/>): the world a prefab mode edits in, next to the world whose
/// instances follow what it saves.
/// </summary>
public sealed class PrefabContentsTests : PrefabTestBase
{
    // A world that shares this one's library (as a session's editing world does), so that a prefab saved in one is announced to the other.
    private World Editing()
    {
        World world = OtherWorld();
        world.Prefabs = World.Prefabs;
        return world;
    }

    private static void SetHeight(World world, Entity entity, double y)
    {
        Transform transform = world.Get<Transform>(entity);
        transform.Position = new Double3(transform.Position.X, y, transform.Position.Z);
        world.Set(entity, transform);
    }

    [Fact]
    public void ThePrefabsOwnEntitiesAreMadeAsMembersOfTheFileWithItsIds()
    {
        using World editing = Editing();
        uint lamp = editing.Prefabs.Load(LampPath);

        Entity root = editing.LoadPrefabContents(lamp);

        Assert.Equal("Lamp", editing.GetName(root));
        Assert.True(editing.GetParent(root).IsNull);
        Assert.False(editing.IsPrefabInstance(root));                        // the root is the prefab's own: nothing is an instance of it
        Assert.Equal(["Bulb", "Base"], NamesBelow(editing, root));
        Assert.Equal(1u, editing.Get<PrefabMember>(root).Id);
        Assert.Equal(2u, editing.Get<PrefabMember>(Child(editing, root, "Bulb")).Id);
        Assert.Equal(lamp, editing.Get<PrefabMember>(Child(editing, root, "Base")).Prefab);
        Assert.False(editing.IsPrefabMember(Child(editing, root, "Bulb")));  // so it can be moved, unlike a member of an instance
        Assert.Equal(root, editing.Get<Marks>(Child(editing, root, "Bulb")).Target);   // an Entity field of the file points at the file's entity
    }

    [Fact]
    public void APrefabInsideThePrefabIsAnInstanceWithItsChanges()
    {
        using World editing = Editing();
        uint corner = editing.Prefabs.Load(CornerPath);

        Entity root = editing.LoadPrefabContents(corner);

        Assert.False(editing.IsPrefabInstance(root));
        Entity lamp = Child(editing, root, "Lamp");
        Assert.True(editing.IsPrefabInstance(lamp));
        Assert.Equal(Lamp, editing.PrefabOf(lamp));
        Assert.Equal(9, editing.Get<Health>(Child(editing, lamp, "Bulb")).Current);     // what the corner says about its lamp
        Assert.True(editing.IsPrefabMember(Child(editing, lamp, "Bulb")));             // inside the instance: the lamp's
        Assert.Equal(2u, editing.Get<PrefabMember>(lamp).Id);                           // the instance is entity 2 of the corner's file
        Assert.Equal(["Lamp", "Lamp 2", "Bench"], NamesBelow(editing, root));
        Assert.Equal(["Bulb"], NamesBelow(editing, Child(editing, root, "Lamp 2")));      // the second lamp has no Base
    }

    [Fact]
    public void ARootThatIsAnInstanceOfAnotherPrefabHasNoEntitiesOfItsOwn()
    {
        Write("prefabs/wrapper.aprefab", $"""<Prefab version="1" nextId="2"><Entity id="1" name="Wrapper" prefab="{Lamp}"/></Prefab>""");
        using World editing = Editing();

        FormatException error = Assert.Throws<FormatException>(() => editing.LoadPrefabContents(editing.Prefabs.Load("res://prefabs/wrapper.aprefab")));

        Assert.Contains("instance of another prefab", error.Message, StringComparison.Ordinal);
        Assert.Empty(editing.UserEntities());
    }

    [Fact]
    public void ASaveKeepsTheIdsAndNeverReusesOne()
    {
        using World editing = Editing();
        uint lamp = editing.Prefabs.Load(LampPath);
        Entity root = editing.LoadPrefabContents(lamp);
        Entity bulb = Child(editing, root, "Bulb");
        editing.Destroy(Child(editing, root, "Base"));
        Entity cord = editing.CreateEntity();
        editing.SetParent(cord, root);
        editing.SetName(cord, "Cord");
        editing.Set(cord, Transform.Identity);
        SetHeight(editing, bulb, 4);

        editing.SavePrefabContents(lamp, root);

        string saved = Read("prefabs/lamp.aprefab");
        Assert.Contains($"uid=\"{LampUid}\"", saved, StringComparison.Ordinal);
        Assert.Contains("nextId=\"5\"", saved, StringComparison.Ordinal);
        Assert.Contains("<Entity id=\"2\" name=\"Bulb\">", saved, StringComparison.Ordinal);
        Assert.Contains("<Entity id=\"4\" name=\"Cord\">", saved, StringComparison.Ordinal);
        Assert.Contains("lamp=\"1\"", saved, StringComparison.Ordinal);                  // the script component and its Entity field are written as the file has them
        Assert.Equal(4u, editing.Get<PrefabMember>(cord).Id);
        Assert.Equal(bulb, Child(editing, root, "Bulb"));
    }

    [Fact]
    public void AFileThatSaysWhatTheEntitiesSayIsNotWritten()
    {
        // The file as the engine writes it (the fixture's is laid out another way), then a person puts a comment in it.
        using World editing = Editing();
        uint lamp = editing.Prefabs.Load(LampPath);
        Entity root = editing.LoadPrefabContents(lamp);
        editing.SavePrefabContents(lamp, root);
        string hand = Read("prefabs/lamp.aprefab").Replace("<Entity id=\"1\"", "<!-- written by hand -->\n  <Entity id=\"1\"", StringComparison.Ordinal);
        Write("prefabs/lamp.aprefab", hand);
        Assert.Equal(PrefabReloadStatus.Unchanged, World.Prefabs.Reload(LampPath).Status); // the same prefab: the comment is no content
        Faults.FailOn(1);                                                            // a write at all would fail

        editing.SavePrefabContents(lamp, root);

        Assert.Equal(0, Faults.Writes);
        Assert.Equal(hand, Read("prefabs/lamp.aprefab"));
    }

    [Fact]
    public void AnInstanceInAnotherWorldFollowsASaveAndNothingIsToldWhenTheWriteFails()
    {
        using World editing = Editing();
        World.LoadScene($"<Scene version=\"2\"><Entity id=\"1\" name=\"One\" prefab=\"{Lamp}\"/></Scene>");
        Entity bulbOne = Child(World.Lookup("One"), "Bulb");
        uint lamp = editing.Prefabs.Load(LampPath);
        Entity root = editing.LoadPrefabContents(lamp);
        SetHeight(editing, Child(editing, root, "Bulb"), 4);
        string before = Read("prefabs/lamp.aprefab");

        Faults.FailOn(1);                                                            // the write fails
        Assert.Throws<IOException>(() => editing.SavePrefabContents(lamp, root));
        Assert.Equal(before, Read("prefabs/lamp.aprefab"));
        Assert.False(World.HasPendingPrefabUpdates);                                 // nobody was told: the instance has nothing to catch up with
        Assert.False(editing.HasPendingPrefabUpdates);

        Faults.FailOn();
        editing.SavePrefabContents(lamp, root);
        Assert.True(World.HasPendingPrefabUpdates);                                  // the library announced it to the world that shares it
        Assert.Equal(1.8, World.Get<Transform>(bulbOne).Position.Y);
        PrefabUpdate? update = World.ApplyPrefabUpdates();

        Assert.NotNull(update);
        Assert.Equal(4.0, World.Get<Transform>(bulbOne).Position.Y);
        Assert.Equal(bulbOne, Child(World.Lookup("One"), "Bulb"));
        Assert.False(editing.HasPendingPrefabUpdates);
    }

    [Fact]
    public void AFileThatChangedOnTheDiskSinceItWasReadIsNotWrittenOver()
    {
        using World editing = Editing();
        uint lamp = editing.Prefabs.Load(LampPath);
        Entity root = editing.LoadPrefabContents(lamp);
        SetHeight(editing, Child(editing, root, "Bulb"), 4);
        string edited = Read("prefabs/lamp.aprefab").Replace("0 1.8 0", "0 2.5 0", StringComparison.Ordinal);
        Write("prefabs/lamp.aprefab", edited);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => editing.SavePrefabContents(lamp, root));

        Assert.Contains("changed on the disk", error.Message, StringComparison.Ordinal);
        Assert.Equal(edited, Read("prefabs/lamp.aprefab"));
        Assert.Equal(0, Faults.Writes);
    }

    [Fact]
    public void APrefabCannotBeSavedHoldingItself()
    {
        using World editing = Editing();
        uint lamp = editing.Prefabs.Load(LampPath);
        Entity root = editing.LoadPrefabContents(lamp);
        _ = editing.Instantiate(CornerPath, root);                                   // the corner holds lamps
        string before = Read("prefabs/lamp.aprefab");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => editing.SavePrefabContents(lamp, root));

        Assert.Contains("itself", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, Read("prefabs/lamp.aprefab"));
        Assert.Equal(0, Faults.Writes);
    }

    [Fact]
    public void AFileInASourceThatCannotBeWrittenIsRefusedWhateverTheEntitiesSay()
    {
        var files = new VirtualFileSystem();
        files.Mount("res://", new DirectorySource(Folder, writable: false));
        using World reader = new() { Files = files, Assets = null };
        uint lamp = reader.Prefabs.Load(LampPath);
        Entity root = reader.LoadPrefabContents(lamp);

        Assert.False(reader.CanWritePrefab(lamp));
        Assert.True(World.CanWritePrefab(World.Prefabs.Load(LampPath)));
        Assert.Throws<UnauthorizedAccessException>(() => reader.SavePrefabContents(lamp, root));            // not even for what is already there
        SetHeight(reader, Child(reader, root, "Bulb"), 4);
        Assert.Throws<UnauthorizedAccessException>(() => reader.SavePrefabContents(lamp, root));
        Assert.Contains("0 1.8 0", Read("prefabs/lamp.aprefab"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOversizedPrefabMakesNoEntities()
    {
        using World editing = Editing();
        editing.MaxLoadEntities = 1;
        uint corner = editing.Prefabs.Load(CornerPath);

        Assert.Throws<FormatException>(() => editing.LoadPrefabContents(corner));

        Assert.Empty(editing.UserEntities());
    }
}
