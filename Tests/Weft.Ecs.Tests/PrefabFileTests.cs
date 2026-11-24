using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Weft.Content;
using Weft.Content.Assets;
using Weft.Content.FileSystem;
using Weft.Content.Xml;
using Weft.Core.Assets;
using Weft.Core.Logging;
using Weft.Core.Numerics;
using Weft.Ecs;
using Weft.Native.Tests;
using Weft.Reflection;
using Xunit;
using Flecs = Weft.Native.Flecs.Ecs;

namespace Weft.Ecs.Tests;

/// <summary>Prefab files: instantiating one, the ids its entities keep, the library's handles and saving a prefab with stable ids.</summary>
public sealed class PrefabFileTests : IDisposable
{
    private sealed class Sink : ILogSink
    {
        public List<string> Warnings { get; } = [];

        public void Write(in LogEntry entry)
        {
            if (entry.Level == LogLevel.Warning)
            {
                lock (Warnings)
                {
                    Warnings.Add(entry.Message);
                }
            }
        }
    }

    private readonly string _folder = Directory.CreateTempSubdirectory("weft-prefab-files").FullName;
    private readonly VirtualFileSystem _files = new();
    private readonly AssetDatabase _assets;
    private readonly World _world;
    private readonly AssetUid _lampUid = AssetUid.New();

    public PrefabFileTests()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        _files.Mount("res://", new DirectorySource(_folder, writable: true));
        _assets = new AssetDatabase(_files);
        _world = new World { Files = _files, Assets = _assets };
        Write("prefabs/lamp.wprf", Lamp(_lampUid));
    }

    public void Dispose()
    {
        _world.Dispose();
        Directory.Delete(_folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string Lamp(AssetUid uid) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Prefab version="1" uid="{uid}" nextId="4">
          <Components>
            <Component name="Flicker"><Field name="speed" kind="Float32"/><Field name="lamp" kind="Entity"/></Component>
          </Components>
          <Entity id="1" name="Lamp">
            <Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/>
            <Entity id="2" name="Bulb">
              <Transform Position="0 1.8 0" Rotation="0 0 0 1" Scale="1 1 1"/>
              <Health Current="5" Max="9" Regen="0.5"/>
              <Marks Alive="true" Level="2" Target="1" Mass="1.5"/>
              <Flicker speed="2" lamp="1"/>
            </Entity>
            <Entity id="3" name="Base"><Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
          </Entity>
        </Prefab>
        """;

    private void Write(string path, string text)
    {
        string full = Path.Combine(_folder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private string Read(string path) => File.ReadAllText(Path.Combine(_folder, path));

    private Entity Child(Entity parent, string name) => _world.Children(parent).Single(c => _world.GetName(c) == name);

    private static T WithWarnings<T>(Func<T> action, out List<string> warnings)
    {
        var sink = new Sink();
        Log.AddSink(sink);
        try
        {
            return action();
        }
        finally
        {
            Log.RemoveSink(sink);
            warnings = sink.Warnings;
        }
    }

    [Fact]
    public void InstantiateMakesThePrefabsEntitiesUnderAParent()
    {
        Entity level = _world.CreateEntity("level");

        Entity lamp = _world.Instantiate("res://prefabs/lamp.wprf", level);

        Assert.Equal(level, _world.GetParent(lamp));
        Assert.Equal("Lamp", _world.GetName(lamp));
        Assert.Equal(["Bulb", "Base"], _world.Children(lamp).Select(c => _world.GetName(c)));   // in the order of the file
        Entity bulb = Child(lamp, "Bulb");
        Assert.Equal(new Double3(0, 1.8, 0), _world.Get<Transform>(bulb).Position);
        Assert.Equal(5, _world.Get<Health>(bulb).Current);
        Assert.Equal(0.5f, _world.Get<Health>(bulb).Regen);
        Assert.Equal(1.5, _world.Get<Marks>(bulb).Mass);
        Assert.True(_world.Has(bulb, _world.FindComponent("Flicker")));   // the file's own component definition came with it

        // Without a parent the root is at the top level, and a second instance is a separate set of entities (its root gets a free name).
        Entity second = _world.Instantiate("res://prefabs/lamp.wprf");
        Assert.True(_world.GetParent(second).IsNull);
        Assert.NotEqual(lamp, second);
        Assert.Equal(2, _world.UserEntities().Count(e => _world.GetName(e) == "Bulb"));
    }

    [Fact]
    public void TheRootIsAnInstanceAndEachEntityKnowsItsId()
    {
        Entity lamp = _world.Instantiate("res://prefabs/lamp.wprf");
        Entity bulb = Child(lamp, "Bulb");
        Entity lampBase = Child(lamp, "Base");

        Assert.True(_world.IsPrefabInstance(lamp));
        Assert.False(_world.IsPrefabInstance(bulb));
        Assert.Equal($"res://prefabs/lamp.wprf|{_lampUid}", _world.PrefabOf(lamp));
        Assert.Null(_world.PrefabOf(bulb));
        Assert.Equal(lamp, _world.InstanceOf(bulb));
        Assert.Equal(lamp, _world.InstanceOf(lamp));
        Assert.True(_world.InstanceOf(_world.CreateEntity("other")).IsNull);

        uint handle = _world.Prefabs.Load("res://prefabs/lamp.wprf");
        Assert.Equal(handle, _world.Get<PrefabInstance>(lamp).Prefab);
        Assert.Equal([1u, 2u, 3u], new[] { lamp, bulb, lampBase }.Select(e => _world.Get<PrefabMember>(e).Id));
        Assert.All(new[] { lamp, bulb, lampBase }, e => Assert.Equal(handle, _world.Get<PrefabMember>(e).Prefab));
    }

    [Fact]
    public void InstantiateAcceptsAPathAUidAndBoth()
    {
        Entity byPath = _world.Instantiate("res://prefabs/lamp.wprf");
        Entity byUid = _world.Instantiate(_lampUid.ToString());
        Entity byBoth = _world.Instantiate($"res://prefabs/lamp.wprf|{_lampUid}");

        Assert.All(new[] { byPath, byUid, byBoth }, e => Assert.Equal(2, _world.Children(e).Count));
        Assert.Single(new[] { byPath, byUid, byBoth }.Select(e => _world.Get<PrefabInstance>(e).Prefab).Distinct());

        // A prefab that moved is found through its UID, and the instance says where the file is now.
        Directory.CreateDirectory(Path.Combine(_folder, "moved"));
        File.Move(Path.Combine(_folder, "prefabs/lamp.wprf"), Path.Combine(_folder, "moved/lamp.wprf"));
        _assets.Refresh();
        using var other = new World { Files = _files, Assets = _assets };
        Entity moved = other.Instantiate($"res://prefabs/lamp.wprf|{_lampUid}");
        Assert.Equal($"res://moved/lamp.wprf|{_lampUid}", other.PrefabOf(moved));

        // What cannot be instantiated says why, with the exception a script turns into its error.
        Assert.Throws<FileNotFoundException>(() => _world.Instantiate("res://prefabs/none.wprf"));
        Assert.Throws<ContentException>(() => _world.Instantiate("uid://4k2m9x7q1w3ze"));
        Assert.Throws<FormatException>(() => _world.Instantiate("res://prefabs/a|b|c"));
        using var bare = new World();
        Assert.Throws<InvalidOperationException>(() => bare.Instantiate("res://prefabs/lamp.wprf"));
    }

    [Fact]
    public void APrefabWithNoRootOrTwoRootsIsAFormatError()
    {
        Write("bad/none.wprf", "<Prefab version=\"1\" nextId=\"1\"/>");
        Write("bad/two.wprf", "<Prefab version=\"1\" nextId=\"3\"><Entity id=\"1\"/><Entity id=\"2\"/></Prefab>");
        Write("bad/scene.wprf", "<Scene version=\"1\"><Entity id=\"1\"/></Scene>");
        Write("bad/twice.wprf", "<Prefab version=\"1\" nextId=\"3\"><Entity id=\"1\"><Entity id=\"1\"/></Entity></Prefab>");
        Write("bad/zero.wprf", "<Prefab version=\"1\" nextId=\"3\"><Entity id=\"0\"/></Prefab>");
        Write("bad/noid.wprf", "<Prefab version=\"1\" nextId=\"3\"><Entity name=\"x\"/></Prefab>");
        Write("bad/newer.wprf", "<Prefab version=\"2\" nextId=\"2\"><Entity id=\"1\"/></Prefab>");
        Write("bad/broken.wprf", "<Prefab version=\"1\"><Entity id=\"1\">");

        foreach (string name in new[] { "none", "two", "scene", "twice", "zero", "noid", "newer", "broken" })
        {
            Assert.Throws<FormatException>(() => _world.Instantiate($"res://bad/{name}.wprf"));
        }

        Assert.Empty(_world.UserEntities());   // nothing was made by any of them
        Assert.Contains("2 root", Assert.Throws<FormatException>(() => _world.Instantiate("res://bad/two.wprf")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntityFieldInAPrefabPointsInsideItsInstance()
    {
        Entity first = _world.Instantiate("res://prefabs/lamp.wprf");
        Entity second = _world.Instantiate("res://prefabs/lamp.wprf");

        Assert.Equal(first, _world.Get<Marks>(Child(first, "Bulb")).Target);
        Assert.Equal(second, _world.Get<Marks>(Child(second, "Bulb")).Target);
        Entity flicker = _world.FindComponent("Flicker");
        unsafe
        {
            ComponentInfo info = _world.GetComponentInfo(flicker)!;
            Assert.Equal(second.Id, *(ulong*)(_world.GetPointer(Child(second, "Bulb"), flicker) + info.FindField("lamp")!.Offset));
        }
    }

    [Fact]
    public void TheSamePrefabHasOneHandle()
    {
        var library = new PrefabLibrary(_files, _assets);

        uint byPath = library.Load("res://prefabs/lamp.wprf");
        uint byUid = library.Load(_lampUid.ToString());
        uint byBoth = library.Load($"res://prefabs/lamp.wprf|{_lampUid}");
        uint other = library.Load("res://prefabs/other.wprf");

        Assert.NotEqual(0u, byPath);
        Assert.Equal(byPath, byUid);
        Assert.Equal(byPath, byBoth);
        Assert.NotEqual(byPath, other);
        Assert.Equal("res://prefabs/lamp.wprf", library.PathOf(byPath));
        Assert.Equal(new AssetRef("res://prefabs/lamp.wprf", _lampUid), library.RefOf(byPath));
        Assert.Equal("Prefab", library.Document(byPath)!.Name);
        Assert.Same(library.Document(byPath), library.Document(byUid));   // read once
        Assert.Equal(0u, library.Load(""));
        Assert.Null(library.RefOf(99));

        // A reference that names no file still has a handle, which keeps what it was asked for; once the file is there it is that file's handle.
        Assert.Null(library.PathOf(other));
        Assert.Null(library.Document(other));
        Assert.Equal(AssetRef.FromPath("res://prefabs/other.wprf"), library.RefOf(other));
        Assert.Equal(other, library.Load("res://prefabs/other.wprf"));
        Write("prefabs/other.wprf", "<Prefab version=\"1\" nextId=\"2\"><Entity id=\"1\"/></Prefab>");
        Assert.Equal(other, library.Load("res://prefabs/other.wprf"));
        Assert.Equal("res://prefabs/other.wprf", library.PathOf(other));
        Assert.NotNull(library.Document(other));
    }

    [Fact]
    public void SavePrefabFileKeepsIdsUidAndNextId()
    {
        Entity lamp = _world.Instantiate("res://prefabs/lamp.wprf");
        _world.Destroy(Child(lamp, "Base"));   // id 3 goes
        Entity sign = _world.CreateEntity("Sign", lamp);
        _world.Set(sign, new Transform(new Double3(0, 2, 0), Quaternion.Identity, Vector3.One));

        _world.SavePrefabFile("res://prefabs/lamp.wprf", lamp);

        XmlNode saved = XmlNode.Parse(Read("prefabs/lamp.wprf"));
        Assert.Equal("Prefab", saved.Name);
        Assert.Equal(_lampUid.ToString(), saved.Attribute("uid"));
        Assert.Equal("5", saved.Attribute("nextId"));
        XmlNode root = saved.Children.Single(c => c.Name == "Entity");
        Assert.Equal("1", root.Attribute("id"));
        Assert.Equal([("2", "Bulb"), ("4", "Sign")], root.Children.Where(c => c.Name == "Entity").Select(c => (c.Attribute("id")!, c.Attribute("name")!)));
        Assert.Equal("1", root.Children.Single(c => c.Name == "Entity" && c.Attribute("id") == "2").Child("Marks")!.Attribute("Target"));   // the entity field is the id of the root

        // The new entity is a member of the file now: it keeps 4 on the next save, and the next new one is 5, never 3.
        Assert.Equal(4u, _world.Get<PrefabMember>(sign).Id);
        Entity lamp2 = _world.CreateEntity("Lens", lamp);
        _world.Destroy(sign);
        _world.SavePrefabFile("res://prefabs/lamp.wprf", lamp);
        XmlNode again = XmlNode.Parse(Read("prefabs/lamp.wprf"));
        Assert.Equal("6", again.Attribute("nextId"));
        Assert.Equal([("2", "Bulb"), ("5", "Lens")], again.Children.Single(c => c.Name == "Entity").Children.Where(c => c.Name == "Entity").Select(c => (c.Attribute("id")!, c.Attribute("name")!)));
        Assert.Equal(5u, _world.Get<PrefabMember>(lamp2).Id);

        // The file still instantiates, and the script component it defines is in it.
        using var fresh = new World { Files = _files, Assets = _assets };
        Entity loaded = fresh.Instantiate("res://prefabs/lamp.wprf");
        Assert.Equal(["Bulb", "Lens"], fresh.Children(loaded).Select(c => fresh.GetName(c)));
        Assert.Equal(_lampUid, _assets.UidOf("res://prefabs/lamp.wprf"));
    }

    [Fact]
    public void SavingANewPrefabMintsAUidAndNumbersFromOne()
    {
        Entity crate = _world.CreateEntity("Crate");
        _world.Set(crate, new Health { Current = 3, Max = 3 });
        Entity lid = _world.CreateEntity("Lid", crate);
        _world.Set(lid, new Marks { Target = crate });
        Entity elsewhere = _world.CreateEntity("Elsewhere");
        _world.Set(_world.CreateEntity("Pointer", crate), new Marks { Target = elsewhere });   // points outside what is saved

        _world.SavePrefabFile("res://prefabs/props/crate.wprf", crate);

        string text = Read("prefabs/props/crate.wprf");
        AssetUid uid = AuthoredAssets.ReadUid(File.ReadAllBytes(Path.Combine(_folder, "prefabs/props/crate.wprf")));
        Assert.False(uid.IsNone);
        Assert.Equal(uid, _assets.UidOf("res://prefabs/props/crate.wprf"));
        XmlNode saved = XmlNode.Parse(text);
        Assert.Equal("4", saved.Attribute("nextId"));
        XmlNode root = saved.Children.Single(c => c.Name == "Entity");
        Assert.Equal(["1", "2", "3"], new[] { root }.Concat(root.Children.Where(c => c.Name == "Entity")).Select(c => c.Attribute("id")));
        Assert.Equal("1", root.Child("Entity")!.Child("Marks")!.Attribute("Target"));
        Assert.Equal("", root.Children.Last(c => c.Name == "Entity").Child("Marks")!.Attribute("Target") ?? "");

        // Instantiating it by UID gives the same shape, and the entities in the saving world are members of it now.
        Entity copy = _world.Instantiate(uid.ToString());
        Assert.Equal(3, _world.Subtree(copy).Count);
        Assert.Equal(1u, _world.Get<PrefabMember>(crate).Id);
        Assert.Equal(_world.Get<PrefabMember>(crate).Prefab, _world.Get<PrefabInstance>(copy).Prefab);

        // A path that is not a prefab's, and something that is not an entity of the world, are refused before anything is written.
        Assert.Throws<ArgumentException>(() => _world.SavePrefabFile("res://prefabs/crate.xml", crate));
        Assert.Throws<ArgumentException>(() => _world.SavePrefabFile("res://prefabs/none.wprf", default));
        Assert.False(File.Exists(Path.Combine(_folder, "prefabs/none.wprf")));
        using var bare = new World();
        Assert.Throws<InvalidOperationException>(() => bare.SavePrefabFile("res://prefabs/x.wprf", bare.CreateEntity("x")));
    }

    [Fact]
    public void AnInstanceSavedAsASceneIsOneElementThatLoadsBackAsAnInstance()
    {
        Entity lamp = _world.Instantiate("res://prefabs/lamp.wprf");

        string scene = _world.SaveScene();

        Assert.DoesNotContain("PrefabInstance", scene, StringComparison.Ordinal);   // the link is the element's prefab attribute, not a component
        Assert.DoesNotContain("PrefabMember", scene, StringComparison.Ordinal);
        Assert.Contains($"prefab=\"res://prefabs/lamp.wprf|{_lampUid}\"", scene, StringComparison.Ordinal);
        using var other = new World { Files = _files, Assets = _assets };
        List<Entity> loaded = other.LoadScene(scene);
        Assert.Equal(3, loaded.Count);
        Assert.True(other.IsPrefabInstance(loaded[0]));
        Assert.Equal(_world.PrefabOf(lamp), other.PrefabOf(loaded[0]));

        // A file that names the two components is not trusted with them: they are the engine's to make.
        Entity forged = WithWarnings(() => Assert.Single(other.LoadScene("<Scene version=\"1\"><Entity id=\"1\" name=\"f\"><PrefabInstance Prefab=\"7\"/><PrefabMember Id=\"1\" Prefab=\"7\"/></Entity></Scene>")), out List<string> warnings);
        Assert.False(other.IsPrefabInstance(forged));
        Assert.False(other.Has<PrefabMember>(forged));
        Assert.Equal(2, warnings.Count(w => w.Contains("made by the engine", StringComparison.Ordinal)));
    }

    [Fact]
    public void ASnapshotKeepsInstancesInAnotherWorld()
    {
        Write("prefabs/first.wprf", "<Prefab version=\"1\" nextId=\"2\"><Entity id=\"1\" name=\"First\"/></Prefab>");
        Entity lamp = _world.Instantiate("res://prefabs/lamp.wprf");
        WorldSnapshot snapshot = _world.Snapshot();

        // The other world numbers its prefabs differently (the lamp is its second one): the snapshot names the file, not the number.
        using var other = new World { Files = _files, Assets = _assets };
        _ = other.Prefabs.Load("res://prefabs/first.wprf");
        other.Restore(snapshot);

        Assert.Equal(_world.PrefabOf(lamp), other.PrefabOf(lamp));
        Assert.Equal(other.Prefabs.Load("res://prefabs/lamp.wprf"), other.Get<PrefabMember>(Child2(other, lamp, "Bulb")).Prefab);
        Assert.NotEqual(_world.Prefabs.Load("res://prefabs/lamp.wprf"), other.Prefabs.Load("res://prefabs/lamp.wprf"));

        static Entity Child2(World world, Entity parent, string name) => world.Children(parent).Single(c => world.GetName(c) == name);
    }

    [Fact]
    public void AHandWrittenPrefabWithoutNextIdIsWorkedOutWithAWarning()
    {
        Write("prefabs/hand.wprf", "<Prefab version=\"1\"><Entity id=\"1\" name=\"Hand\"><Entity id=\"7\" name=\"Finger\"/></Entity></Prefab>");

        Entity hand = WithWarnings(() => _world.Instantiate("res://prefabs/hand.wprf"), out List<string> warnings);

        Assert.Contains(warnings, w => w.Contains("nextId", StringComparison.Ordinal));
        Assert.Equal(7u, _world.Get<PrefabMember>(_world.Children(hand).Single()).Id);
        _world.SavePrefabFile("res://prefabs/hand.wprf", hand);
        Assert.Equal("8", XmlNode.Parse(Read("prefabs/hand.wprf")).Attribute("nextId"));
    }

    [Fact]
    public void AnInstanceElementInsideAPrefabIsExpandedWithItsPatches()
    {
        Write("prefabs/nested.wprf", "<Prefab version=\"1\" nextId=\"4\"><Entity id=\"1\" name=\"Outer\"><Entity id=\"2\" name=\"Inner\" prefab=\"res://prefabs/lamp.wprf\"><Transform Position=\"2 0 0\"/></Entity><Entity id=\"3\" name=\"Plain\"/></Entity></Prefab>");

        Entity outer = _world.Instantiate("res://prefabs/nested.wprf");

        Assert.Equal(["Inner", "Plain"], _world.Children(outer).Select(c => _world.GetName(c)));
        Entity inner = Child(outer, "Inner");
        Assert.Equal(new Double3(2, 0, 0), _world.Get<Transform>(inner).Position);
        Assert.Equal(["Bulb", "Base"], _world.Children(inner).Select(c => _world.GetName(c)));
        Assert.True(_world.IsPrefabInstance(inner));
    }

    [Fact]
    public void TheWrittenPrefabMatchesItsSchema()
    {
        Entity lamp = _world.Instantiate("res://prefabs/lamp.wprf");
        _world.SavePrefabFile("res://prefabs/copy.wprf", lamp);
        XmlNode saved = XmlFile.Read(_files, "res://prefabs/copy.wprf");

        var schemas = new SchemaSet();
        using (Stream stream = typeof(World).Assembly.GetManifestResourceStream("Scene/prefab.xsd") ?? throw new InvalidOperationException("prefab.xsd is not embedded"))
        using (var reader = new StreamReader(stream))
        {
            schemas.LoadText(reader.ReadToEnd(), "prefab.xsd");
        }

        DecodeResult result = schemas.Decode(saved, "prefab");
        Assert.True(result.Declared);
        Assert.Empty(result.Problems);
        Assert.NotNull(saved.Child("Components"));   // the script component travels with the file

        // The hand-written file is valid too, and a second root is not.
        Assert.Empty(schemas.Decode(XmlFile.Read(_files, "res://prefabs/lamp.wprf"), "prefab").Problems);
        Assert.NotEmpty(schemas.Decode(XmlNode.Parse("<Prefab version=\"1\"><Entity id=\"1\"/><Entity id=\"2\"/></Prefab>"), "prefab").Problems);
    }

    [Fact]
    public void OverrideIsNotAComponentName()
    {
        Assert.Throws<InvalidOperationException>(() => _world.DefineComponent("Override", [("x", FieldKind.Int32)]));
        Assert.Throws<InvalidOperationException>(() => _world.DefineComponent("Entity", [("x", FieldKind.Int32)]));
        _world.DefineComponent("Overrides", [("x", FieldKind.Int32)]);   // only the exact name is taken
    }
}
