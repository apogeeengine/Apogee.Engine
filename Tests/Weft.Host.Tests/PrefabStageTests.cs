using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Weft.Content.FileSystem;
using Weft.Core.Numerics;
using Weft.Ecs;
using Weft.Host.Editing;
using Weft.Native.Tests;
using Weft.Reflection;
using Xunit;
using Flecs = Weft.Native.Flecs.Ecs;
using Lua = Weft.Native.Luau.Lua;

namespace Weft.Host.Tests;

/// <summary>A prefab opened on its own in a second world of the session (<see cref="PrefabStage"/>): edited there, saved with its ids, and followed by the instances in the session's world.</summary>
public sealed class PrefabStageTests : IDisposable
{
    private const string LampPath = "res://prefabs/lamp.aprefab";
    private const string CornerPath = "res://prefabs/corner.aprefab";
    private const string HolderPath = "res://prefabs/holder.aprefab";
    private const string LampUid = "uid://3c5v7b9n1m2kx";
    private const string CornerUid = "uid://2b4n6m8k0j1hq";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "weft-prefab-stage-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');

    private string Game => _root + "/game";

    public PrefabStageTests()
    {
        NativeRequirements.Require(typeof(Lua).Assembly, "luau");
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // a scratch folder: the next run's names differ
        }

        GC.SuppressFinalize(this);
    }

    private const string LampText = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Prefab version="1" uid="{LampUid}" nextId="4">
          <Entity id="1" name="Lamp">
            <Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/>
            <Entity id="2" name="Bulb"><Transform Position="0 1.8 0" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
            <Entity id="3" name="Base"><Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
          </Entity>
        </Prefab>
        """;

    // A corner holds a lamp, whose bulb it moves, written by hand: with a comment and a layout of its own.
    private const string CornerText = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Prefab version="1" uid="{CornerUid}" nextId="4">
          <!-- written by hand -->
          <Entity id="1" name="Corner">
            <Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/>
            <Entity id="2" name="Lamp" prefab="{LampPath}|{LampUid}">
              <Transform Position="2 0 0"/>
              <Override entity="2"><Transform Position="0 4 0"/></Override>
            </Entity>
            <Entity id="3" name="Bench"><Transform Position="0 0 3" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
          </Entity>
        </Prefab>
        """;

    private const string HolderText = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Prefab version="1" uid="uid://4d6f8h0j2k4mn" nextId="3">
          <Entity id="1" name="Holder">
            <Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/>
            <Entity id="2" name="Lamp" prefab="{LampPath}|{LampUid}"/>
          </Entity>
        </Prefab>
        """;

    private string Write(string relative, string text)
    {
        string path = Path.Combine(Game, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private string Prefab(string name) => Path.Combine(Game, "prefabs", name + ".aprefab");

    private string ReadPrefab(string name) => File.ReadAllText(Prefab(name));

    // A game with the lamp, the corner and the holder, and a scene: two lamps (the second with a bulb of its own height) and a corner.
    private ScriptSession Start(bool watch = true)
    {
        Write("prefabs/lamp.aprefab", LampText);
        Write("prefabs/corner.aprefab", CornerText);
        Write("prefabs/holder.aprefab", HolderText);
        string scene = $"""
            <Scene version="2">
              <Entity id="1" name="One" prefab="{LampPath}"/>
              <Entity id="2" name="Two" prefab="{LampPath}"><Override entity="2"><Transform Position="0 7 0"/></Override></Entity>
              <Entity id="3" name="Turn" prefab="{CornerPath}"/>
            </Scene>
            """;
        return ScriptSession.StartGame(Game, _root + "/user", watch: watch, runScripts: false, scene: scene);
    }

    private static Entity Child(World world, Entity parent, string name) => world.Children(parent).Single(child => world.GetName(child) == name);

    private static double Height(World world, Entity entity) => world.Get<Transform>(entity).Position.Y;

    private static void SetHeight(World world, Entity entity, double y)
    {
        Transform transform = world.Get<Transform>(entity);
        transform.Position = new Double3(transform.Position.X, y, transform.Position.Z);
        world.Set(entity, transform);
    }

    [Fact]
    public void AStageSavesWhatWasEditedWithStableIds()
    {
        using ScriptSession session = Start();
        using PrefabStage stage = PrefabStage.Open(session, LampPath);
        World world = stage.World;
        Entity bulb = Child(world, stage.Root, "Bulb");

        Assert.Equal(LampPath, stage.Path);
        Assert.Equal("Lamp", world.GetName(stage.Root));
        Assert.False(world.IsPrefabInstance(stage.Root));           // the prefab's own root, not an instance of it
        Assert.False(world.IsPrefabMember(bulb));                    // an ordinary entity of the file: it can be moved
        Assert.Equal(2u, world.Get<PrefabMember>(bulb).Id);
        Assert.Equal(stage.Prefab, world.Get<PrefabMember>(bulb).Prefab);

        world.Destroy(Child(world, stage.Root, "Base"));
        Entity cord = world.CreateEntity();
        world.SetParent(cord, stage.Root);
        world.SetName(cord, "Cord");
        world.Set(cord, Transform.At(new Double3(0, 0, 1)));
        SetHeight(world, bulb, 3);
        stage.Save();

        string saved = ReadPrefab("lamp");
        Assert.Contains($"uid=\"{LampUid}\"", saved, StringComparison.Ordinal);
        Assert.Contains("nextId=\"5\"", saved, StringComparison.Ordinal);                // the deleted 3 is never given again
        Assert.Contains("<Entity id=\"2\" name=\"Bulb\">", saved, StringComparison.Ordinal);
        Assert.Contains("<Entity id=\"4\" name=\"Cord\">", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("Base", saved, StringComparison.Ordinal);
        Assert.Equal(4u, world.Get<PrefabMember>(cord).Id);                              // a new entity is a member of the file from then on

        // The same entities again: a rename and another save change nothing about the ids.
        world.SetName(cord, "Wire");
        stage.Save();
        string again = ReadPrefab("lamp");
        Assert.Contains("<Entity id=\"4\" name=\"Wire\">", again, StringComparison.Ordinal);
        Assert.Contains("nextId=\"5\"", again, StringComparison.Ordinal);
        Assert.Equal(bulb, Child(world, stage.Root, "Bulb"));

        // A stage opened again reads what was saved, under the same ids.
        using PrefabStage reopened = PrefabStage.Open(session, LampPath);
        Assert.Equal(3.0, Height(reopened.World, Child(reopened.World, reopened.Root, "Bulb")));
        Assert.Equal(4u, reopened.World.Get<PrefabMember>(Child(reopened.World, reopened.Root, "Wire")).Id);
    }

    [Fact]
    public void SavingAStageUpdatesInstancesInTheMainWorld()
    {
        using ScriptSession session = Start();
        World main = session.World;
        Entity one = main.Lookup("One");
        Entity two = main.Lookup("Two");
        Entity bulbOne = Child(main, one, "Bulb");
        Entity bulbTwo = Child(main, two, "Bulb");
        Assert.Equal(1.8, Height(main, bulbOne));
        Assert.Equal(7.0, Height(main, bulbTwo));

        using PrefabStage stage = PrefabStage.Open(session, LampPath);
        Entity bulb = Child(stage.World, stage.Root, "Bulb");
        SetHeight(stage.World, bulb, 3);
        Entity lens = stage.World.CreateEntity();
        stage.World.SetParent(lens, bulb);
        stage.World.SetName(lens, "Lens");
        stage.World.Set(lens, Transform.Identity);

        Assert.Equal(1.8, Height(main, bulbOne));                   // nothing changes in the session's world until the stage is saved
        PrefabUpdate? update = stage.Save();

        Assert.NotNull(update);
        Assert.Contains(one, update.Updated);
        Assert.Contains(two, update.Updated);
        Assert.Equal(3.0, Height(main, bulbOne));
        Assert.Equal(7.0, Height(main, bulbTwo));                   // what an instance changed stays
        Assert.Equal(bulbOne, Child(main, one, "Bulb"));            // updated in place: the same entities
        Assert.Equal(bulbTwo, Child(main, two, "Bulb"));
        Assert.Equal(["Lens"], main.Children(bulbOne).Select(child => main.GetName(child)).ToArray());
        Assert.False(main.HasPendingPrefabUpdates);
        Assert.False(stage.World.HasPendingPrefabUpdates);
    }

    [Fact]
    public void ASessionThatHoldsPrefabUpdatesLeavesThemToItsTool()
    {
        using ScriptSession session = Start();
        World main = session.World;
        Entity bulbOne = Child(main, main.Lookup("One"), "Bulb");
        main.HoldPrefabUpdates = true;

        using PrefabStage stage = PrefabStage.Open(session, LampPath);
        SetHeight(stage.World, Child(stage.World, stage.Root, "Bulb"), 3);
        PrefabUpdate? update = stage.Save();

        Assert.Null(update);
        Assert.True(main.HasPendingPrefabUpdates);
        Assert.Equal(1.8, Height(main, bulbOne));
        Assert.NotNull(main.ApplyPrefabUpdates());
        Assert.Equal(3.0, Height(main, bulbOne));
    }

    [Fact]
    public void SavingAStageOfAPrefabNestedInAnotherUpdatesTheInstancesOfTheOuterOne()
    {
        using ScriptSession session = Start();
        World main = session.World;
        Entity turn = main.Lookup("Turn");
        Entity lamp = Child(main, turn, "Lamp");
        Entity bulb = Child(main, lamp, "Bulb");
        Assert.Equal(4.0, Height(main, bulb));                      // the corner moved the bulb

        using PrefabStage stage = PrefabStage.Open(session, LampPath);
        Entity stageBulb = Child(stage.World, stage.Root, "Bulb");
        Transform transform = stage.World.Get<Transform>(stageBulb);
        transform.Scale = new System.Numerics.Vector3(2, 2, 2);
        stage.World.Set(stageBulb, transform);
        PrefabUpdate? update = stage.Save();

        Assert.NotNull(update);
        Assert.Contains(turn, update.Updated);
        Assert.Equal(bulb, Child(main, Child(main, turn, "Lamp"), "Bulb"));
        Assert.Equal(2f, main.Get<Transform>(bulb).Scale.X);        // the lamp's own change reached the corner's lamp
        Assert.Equal(4.0, Height(main, bulb));                      // and what the corner says is still said
    }

    [Fact]
    public void AStageOnAPrefabWithNestedInstancesKeepsThemAsInstances()
    {
        using ScriptSession session = Start();
        using PrefabStage stage = PrefabStage.Open(session, CornerPath);
        World world = stage.World;
        Entity lamp = Child(world, stage.Root, "Lamp");
        Entity bulb = Child(world, lamp, "Bulb");

        Assert.False(world.IsPrefabInstance(stage.Root));
        Assert.True(world.IsPrefabInstance(lamp));                  // an instance of the lamp, as it is in a scene
        Assert.Equal(LampPath + "|" + LampUid, world.PrefabOf(lamp));
        Assert.True(world.IsPrefabMember(bulb));                    // what is inside it is the lamp's: it cannot be moved
        Assert.Equal(4.0, Height(world, bulb));                     // with what the corner says about it
        Assert.Equal(2.0, world.Get<Transform>(lamp).Position.X);
        Assert.Equal(2u, world.Get<PrefabMember>(lamp).Id);         // the instance is entity 2 of the corner's file

        // Nothing changed: the file is not written at all, so its comment and layout stay.
        string before = ReadPrefab("corner");
        stage.Save();
        Assert.Equal(before, ReadPrefab("corner"));

        // A change inside the instance is written as the instance's change, not as the lamp's entities.
        SetHeight(world, bulb, 5);
        stage.Save();
        string saved = ReadPrefab("corner");
        Assert.Contains("<Override entity=\"2\">", saved, StringComparison.Ordinal);
        Assert.Contains("Position=\"0 5 0\"", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Bulb\"", saved, StringComparison.Ordinal);
        Assert.Contains("<Entity id=\"3\" name=\"Bench\">", saved, StringComparison.Ordinal);
        Assert.Equal(lamp, Child(world, stage.Root, "Lamp"));

        using PrefabStage reopened = PrefabStage.Open(session, CornerPath);
        Assert.Equal(5.0, Height(reopened.World, Child(reopened.World, Child(reopened.World, reopened.Root, "Lamp"), "Bulb")));
    }

    [Fact]
    public void AStageWorldIsInvisibleToScripts()
    {
        using ScriptSession session = Start();
        int before = session.World.UserEntities().Count;
        using PrefabStage stage = PrefabStage.Open(session, LampPath);

        Assert.NotSame(session.World, stage.World);
        Assert.Equal(before, session.World.UserEntities().Count);
        Assert.True(stage.World.UserEntities().Count > 0);
        Assert.True(session.World.Lookup("Lamp").IsNull);           // the stage's root is not in the world scripts see

        // The session steps its own world only: the frame counter is the proof, and a stage's entities do not move.
        session.Progress(0.1f);
        session.Progress(0.1f);
        Assert.Equal(2L, session.World.Frame);
        Assert.Equal(0L, stage.World.Frame);

        // It shares what the session shares: the library (so a prefab has one number in both) and the files.
        Assert.Same(session.World.Prefabs, stage.World.Prefabs);
        Assert.Equal(stage.Prefab, session.World.Prefabs.Load(LampPath));
        Assert.Same(session.World.Files, stage.World.Files);
    }

    [Fact]
    public void AStageRefusesToSaveACycle()
    {
        using ScriptSession session = Start();
        using PrefabStage stage = PrefabStage.Open(session, LampPath);
        string before = ReadPrefab("lamp");

        // The holder holds a lamp: put in the lamp, the lamp would hold itself.
        stage.World.Instantiate(HolderPath, stage.Root);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => stage.Save());

        Assert.Contains("itself", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, ReadPrefab("lamp"));
        Assert.False(session.World.HasPendingPrefabUpdates);
        Assert.False(stage.World.HasPendingPrefabUpdates);
    }

    [Fact]
    public void AReadOnlyPrefabOpensButCannotSave()
    {
        using ScriptSession session = Start(watch: false);         // a game that is not watched has a folder that is not written
        using PrefabStage stage = PrefabStage.Open(session, LampPath);
        string before = ReadPrefab("lamp");

        Assert.False(stage.CanSave);
        Assert.Equal("Lamp", stage.World.GetName(stage.Root));      // it can be looked at and edited
        SetHeight(stage.World, Child(stage.World, stage.Root, "Bulb"), 3);
        Assert.Throws<UnauthorizedAccessException>(() => stage.Save());
        stage.World.Destroy(Child(stage.World, stage.Root, "Base"));
        Assert.Throws<UnauthorizedAccessException>(() => stage.Save());

        Assert.Equal(before, ReadPrefab("lamp"));
        Assert.False(session.World.HasPendingPrefabUpdates);
        Assert.Equal(1.8, Height(session.World, Child(session.World, session.World.Lookup("One"), "Bulb")));
    }

    [Fact]
    public void AFileThatIsReadOnlyOnTheDiskCannotBeSaved()
    {
        using ScriptSession session = Start();
        using PrefabStage stage = PrefabStage.Open(session, LampPath);
        string before = ReadPrefab("lamp");
        var info = new FileInfo(Prefab("lamp")) { IsReadOnly = true };
        try
        {
            if (!new FileInfo(Prefab("lamp")).IsReadOnly)
            {
                return; // this system cannot make a file read-only for this user
            }

            Assert.False(stage.CanSave);
            SetHeight(stage.World, Child(stage.World, stage.Root, "Bulb"), 3);
            Assert.Throws<UnauthorizedAccessException>(() => stage.Save());
            Assert.Equal(before, ReadPrefab("lamp"));
        }
        finally
        {
            info.IsReadOnly = false;
        }

        Assert.True(stage.CanSave);
        stage.Save();
        Assert.NotEqual(before, ReadPrefab("lamp"));
    }

    [Fact]
    public void DisposingTheSessionDisposesItsStages()
    {
        ScriptSession session = Start();
        PrefabStage first = PrefabStage.Open(session, LampPath);
        PrefabStage second = PrefabStage.Open(session, CornerPath);
        World editing = session.CreateEditingWorld();
        PrefabStage closed = PrefabStage.Open(session, LampPath);
        closed.Dispose();                                          // a stage closed on its own, before the session ends
        Assert.True(closed.IsDisposed);
        Assert.False(first.IsDisposed);

        session.Dispose();

        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
        Assert.True(editing.IsDestroyed);
        first.Dispose();                                           // closing a stage whose session is gone is not an error
        Assert.Throws<ObjectDisposedException>(() => first.Save());
        Assert.Throws<ObjectDisposedException>(() => session.CreateEditingWorld());
        Assert.Throws<ObjectDisposedException>(() => PrefabStage.Open(session, LampPath));
    }

    [Fact]
    public void AStageOpenedOnAnUnusablePrefabMakesNothing()
    {
        using ScriptSession session = Start();
        Write("prefabs/broken.aprefab", "<Prefab version=\"1\"><Entity id=\"1\"/><Entity id=\"2\"/></Prefab>");
        Write("prefabs/wrapper.aprefab", $"<Prefab version=\"1\" nextId=\"2\"><Entity id=\"1\" name=\"Wrapper\" prefab=\"{LampPath}\"/></Prefab>");

        Assert.Throws<FormatException>(() => PrefabStage.Open(session, "res://prefabs/broken.aprefab"));
        FormatException wrapper = Assert.Throws<FormatException>(() => PrefabStage.Open(session, "res://prefabs/wrapper.aprefab"));
        Assert.Contains("instance of another prefab", wrapper.Message, StringComparison.Ordinal);
        Assert.Throws<FileNotFoundException>(() => PrefabStage.Open(session, "res://prefabs/nothing.aprefab"));
        Assert.Equal(3, session.World.UserEntities().Count(entity => session.World.GetParent(entity).IsNull));
    }

    [Fact]
    public void AStageOfAFileThatChangedMeanwhileRefusesUnlessToldToOverwrite()
    {
        using ScriptSession session = Start();
        using PrefabStage stage = PrefabStage.Open(session, LampPath);
        SetHeight(stage.World, Child(stage.World, stage.Root, "Bulb"), 3);

        // Somebody else saves the prefab (another stage, an editor, a hot reload): the library reads it.
        string elsewhere = LampText.Replace("0 1.8 0", "0 2.5 0", StringComparison.Ordinal);
        File.WriteAllText(Prefab("lamp"), elsewhere);
        session.World.Prefabs.Reload(LampPath);
        session.World.ApplyPrefabUpdates();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => stage.Save());
        Assert.Contains("changed since", error.Message, StringComparison.Ordinal);
        Assert.Equal(elsewhere, ReadPrefab("lamp"));

        stage.Save(overwrite: true);
        Assert.Contains("Position=\"0 3 0\"", ReadPrefab("lamp"), StringComparison.Ordinal);
        stage.Save();                                               // saved: the stage is what the file is
    }

    [Fact]
    public void AStageOfAPrefabWithoutAUidGivesItOne()
    {
        using ScriptSession session = Start();
        Write("prefabs/plain.aprefab", "<Prefab version=\"1\" nextId=\"2\"><Entity id=\"1\" name=\"Plain\"/></Prefab>");
        using PrefabStage stage = PrefabStage.Open(session, "res://prefabs/plain.aprefab");
        stage.World.SetName(stage.Root, "Other");

        stage.Save();

        string saved = ReadPrefab("plain");
        Assert.Contains("uid=\"uid://", saved, StringComparison.Ordinal);
        Assert.Contains("name=\"Other\"", saved, StringComparison.Ordinal);
        Weft.Core.Assets.AssetRef? reference = session.World.Prefabs.RefOf(stage.Prefab);
        Assert.NotNull(reference);
        Assert.False(reference.Value.Uid.IsNone);
    }

    // A source over the game's folder whose writes can be made to fail or to come out damaged.
    private sealed class FaultyFolder(DirectorySource inner) : IFileSource
    {
        public bool FailNext { get; set; }

        public bool DamageNext { get; set; }

        public int Writes { get; private set; }

        public string Description => inner.Description;

        public bool CanWrite => true;

        public FileKind Kind(string path) => inner.Kind(path);

        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);

        public Stream OpenRead(string path) => inner.OpenRead(path);

        public void List(string directory, List<(string Name, FileKind Kind)> into) => inner.List(directory, into);

        public string? PhysicalPath(string path) => inner.PhysicalPath(path);

        public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
        {
            Writes++;
            if (FailNext)
            {
                FailNext = false;
                throw new IOException($"No space left on device (injected, {path}).");
            }

            if (DamageNext)
            {
                DamageNext = false;
                inner.WriteAllBytes(path, bytes[..(bytes.Length / 2)]); // cut off in the middle, as a full disk does
                return;
            }

            inner.WriteAllBytes(path, bytes);
        }

        public void Delete(string path) => inner.Delete(path);

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public void Move(string source, string target) => inner.Move(source, target);
    }

    [Fact]
    public void ASaveThatFailsLeavesTheFileAndTheMainWorldAsTheyWere()
    {
        using ScriptSession session = Start();
        var faulty = new FaultyFolder(new DirectorySource(Game, writable: true));
        session.Files!.Mount("res://", faulty);
        World main = session.World;
        Entity bulbOne = Child(main, main.Lookup("One"), "Bulb");
        using PrefabStage stage = PrefabStage.Open(session, LampPath);
        SetHeight(stage.World, Child(stage.World, stage.Root, "Bulb"), 3);
        string before = ReadPrefab("lamp");
        var announced = 0;
        main.PrefabsChanged += _ => announced++;

        // The write itself fails.
        faulty.FailNext = true;
        Assert.Throws<IOException>(() => stage.Save());
        Assert.Equal(before, ReadPrefab("lamp"));
        Assert.False(main.HasPendingPrefabUpdates);

        // The write comes out damaged: it is read back, found not to be the prefab, and put back before anything is told.
        faulty.DamageNext = true;
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => stage.Save());
        Assert.Contains("put back", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, ReadPrefab("lamp"));
        Assert.False(main.HasPendingPrefabUpdates);
        Assert.False(stage.World.HasPendingPrefabUpdates);

        Assert.Equal(0, announced);
        Assert.Equal(1.8, Height(main, bulbOne));

        // Nothing was lost: the same stage saves once the disk is well.
        stage.Save();
        Assert.Equal(3.0, Height(main, bulbOne));
        Assert.Equal(1, announced);
    }
}
