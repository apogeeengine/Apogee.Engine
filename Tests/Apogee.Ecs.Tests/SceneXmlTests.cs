using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Apogee.Content.Xml;
using Apogee.Core.Assets;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Native.Tests;
using Apogee.Reflection;
using Xunit;
using Flecs = Apogee.Native.Flecs.Ecs;

namespace Apogee.Ecs.Tests;

/// <summary>Scenes as XML text: what <see cref="World.SaveScene()"/> writes, what <see cref="World.LoadScene(string, Entity)"/> reads, and how a bad file is told apart.</summary>
public sealed class SceneXmlTests : IDisposable
{
    private readonly World _world;

    public SceneXmlTests()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        _world = new World();
    }

    public void Dispose() => _world.Dispose();

    private Entity Make(string name, Entity parent = default)
    {
        Entity e = _world.CreateEntity(name, parent);
        _world.Set(e, new Transform(new Double3(1, 2, 3), Quaternion.Identity, Vector3.One));
        return e;
    }

    // Writes one field of a script-defined component.
    private static unsafe void Put<T>(World world, Entity entity, Entity component, string field, T value)
        where T : unmanaged
    {
        ComponentInfo info = world.GetComponentInfo(component)!;
        byte* data = world.EnsurePointer(entity, component, info.Size);
        *(T*)(data + info.FindField(field)!.Offset) = value;
    }

    private static unsafe T Read<T>(World world, Entity entity, string component, string field)
        where T : unmanaged
    {
        Entity id = world.FindComponent(component);
        ComponentInfo info = world.GetComponentInfo(id)!;
        return *(T*)(world.GetPointer(entity, id) + info.FindField(field)!.Offset);
    }

    [Fact]
    public void ASceneRoundTripsEntitiesComponentsHierarchyAndReferences()
    {
        Entity a = Make("root");
        _world.Set(a, new Health { Current = 7, Max = 9, Regen = 0.25f });
        Entity child = Make("child", a);
        Entity target = Make("target");
        _world.Set(child, new Marks { Alive = true, Level = 3, Target = target, Mass = 12.5 });
        _world.Add<Frozen>(child);
        _world.Set(child, new Transform(new Double3(1e12 + 0.5, -2, 3), new Quaternion(0.1f, 0.2f, 0.3f, 0.9f), new Vector3(2, 3, 4)));

        string text = _world.SaveScene();

        using var other = new World();
        List<Entity> loaded = other.LoadScene(text);

        Assert.Equal(3, loaded.Count);
        Entity root2 = other.Lookup("root");
        Entity child2 = other.Lookup("root.child");
        Entity target2 = other.Lookup("target");
        Assert.Equal(7, other.Get<Health>(root2).Current);
        Assert.Equal(0.25f, other.Get<Health>(root2).Regen);
        Assert.Equal(root2, other.GetParent(child2));
        Marks marks = other.Get<Marks>(child2);
        Assert.True(marks.Alive);
        Assert.Equal(3, marks.Level);
        Assert.Equal(12.5, marks.Mass);
        Assert.Equal(target2, marks.Target);                         // the reference follows the entity to its new id
        Assert.True(other.Has<Frozen>(child2));
        Transform t = other.Get<Transform>(child2);
        Assert.Equal(new Double3(1e12 + 0.5, -2, 3), t.Position);    // a double position keeps its precision
        Assert.Equal(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f), t.Rotation);
        Assert.Equal(new Vector3(2, 3, 4), t.Scale);

        // Saved again from the new world, the same text: nothing was lost or invented (WorldTransform is derived, so it is not in it).
        Assert.DoesNotContain("WorldTransform", text, StringComparison.Ordinal);
        Assert.Equal(text, other.SaveScene());
    }

    [Fact]
    public void ASceneIsAnXmlDocumentWithNestedEntitiesAndNoUid()
    {
        Entity a = Make("root");
        Make("child", a);
        Entity tagged = Make("tagged");
        _world.Add<Frozen>(tagged);

        string text = _world.SaveScene();

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Scene version=\"1\">", text, StringComparison.Ordinal);
        Assert.DoesNotContain("uid", text, StringComparison.Ordinal);   // text is a copy: only a file has an identity
        XmlNode scene = XmlNode.Parse(text);
        Assert.Equal(["Entity", "Entity"], scene.Children.Select(c => c.Name));
        XmlNode root = scene.Children[0];
        Assert.Equal("1", root.Attribute("id"));
        Assert.Equal("root", root.Attribute("name"));
        Assert.Equal(["Transform", "Entity"], root.Children.Select(c => c.Name));   // components first, then the children
        Assert.Equal("2", root.Children[1].Attribute("id"));
        Assert.Equal("1 2 3", scene.Children[1].Child("Transform")!.Attribute("Position"));
        XmlNode frozen = scene.Children[1].Child("Frozen")!;                          // a tag is an empty element
        Assert.Empty(frozen.Attributes);
        Assert.Empty(frozen.Children);
    }

    [Fact]
    public void ASceneDefinesTheComponentsAScriptMadeAndLoadsUnderAParent()
    {
        Entity script = _world.DefineComponent("Stamina", [("max", FieldKind.Float32), ("count", FieldKind.Int32), ("big", FieldKind.UInt64), ("ok", FieldKind.Bool)]);
        Entity e = Make("runner");
        Put(_world, e, script, "max", 1.5f);
        Put(_world, e, script, "count", 4);
        Put(_world, e, script, "big", ulong.MaxValue);
        Put(_world, e, script, "ok", (byte)1);

        string text = _world.SaveScene();
        Assert.Contains("<Component name=\"Stamina\">", text, StringComparison.Ordinal);
        Assert.Contains("<Field name=\"big\" kind=\"UInt64\"/>", text, StringComparison.Ordinal);

        using var fresh = new World();                // a world that has never heard of Stamina
        Entity holder = fresh.CreateEntity("holder");
        List<Entity> loaded = fresh.LoadScene(text, holder);

        Entity runner = Assert.Single(loaded);
        Assert.Equal(holder, fresh.GetParent(runner));
        Assert.False(fresh.FindComponent("Stamina").IsNull);
        Assert.Equal(1.5f, Read<float>(fresh, runner, "Stamina", "max"));
        Assert.Equal(4, Read<int>(fresh, runner, "Stamina", "count"));
        Assert.Equal(ulong.MaxValue, Read<ulong>(fresh, runner, "Stamina", "big"));
        Assert.Equal(1, Read<byte>(fresh, runner, "Stamina", "ok"));
    }

    [Fact]
    public void AScriptComponentCanDeclareTheAssetTypeOfAField()
    {
        static void Register(World world) => world.RegisterAssetType("model", handle => handle == 0 ? null : AssetRef.FromPath($"res://m{handle}.glb"), reference => uint.Parse(reference.Path.AsSpan(7, reference.Path.Length - 11), CultureInfo.InvariantCulture));

        Register(_world);
        Entity decorated = _world.DefineComponent("Decorated", [("look", FieldKind.AssetRef), ("plain", FieldKind.Int32)], assetTypes: new Dictionary<string, string> { ["look"] = "model", ["plain"] = "ignored: not an asset field" });
        Assert.Equal("model", _world.GetComponentInfo(decorated)!.FindField("look")!.AssetType);
        Assert.Null(_world.GetComponentInfo(decorated)!.FindField("plain")!.AssetType);
        Entity e = Make("e");
        Put(_world, e, decorated, "look", 7u);

        string text = _world.SaveScene();
        Assert.Contains("<Field name=\"look\" kind=\"AssetRef\" assetType=\"model\"/>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<Field name=\"plain\" kind=\"Int32\" assetType", text, StringComparison.Ordinal);
        Assert.Contains("look=\"res://m7.glb\"", text, StringComparison.Ordinal);

        // A world that has never heard of Decorated learns the asset type from the file, so the handle is made again.
        using var fresh = new World();
        Register(fresh);
        fresh.LoadScene(text);
        Assert.Equal(7u, Read<uint>(fresh, fresh.Lookup("e"), "Decorated", "look"));
        Assert.Equal("model", fresh.GetComponentInfo(fresh.FindComponent("Decorated"))!.FindField("look")!.AssetType);
    }

    [Fact]
    public void ARuntimeHandleIsSavedAsWhatItNamesThroughAnAssetTypeAndIgnoredComponentsAreLeftOut()
    {
        _world.RegisterAssetType("model", handle => handle == 0 ? null : AssetRef.FromPath($"res://m{handle}.glb"), reference => uint.Parse(reference.Path.AsSpan(7, reference.Path.Length - 11), CultureInfo.InvariantCulture));
        _world.IgnoreInScenes("Velocity");
        Entity e = Make("e");
        _world.Set(e, new Furnished { Count = 5, Model = 42 });
        _world.Set(e, new Velocity { Value = new Vector3(1, 1, 1) });

        string text = _world.SaveScene();
        Assert.Contains("Model=\"res://m42.glb\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Velocity", text, StringComparison.Ordinal);

        using var fresh = new World();
        fresh.RegisterAssetType("model", handle => AssetRef.FromPath($"res://m{handle}.glb"), reference => uint.Parse(reference.Path.AsSpan(7, reference.Path.Length - 11), CultureInfo.InvariantCulture));
        fresh.LoadScene(text);
        Assert.Equal(42u, fresh.Get<Furnished>(fresh.Lookup("e")).Model);
        Assert.Equal(5, fresh.Get<Furnished>(fresh.Lookup("e")).Count);
        Assert.False(fresh.Has<Velocity>(fresh.Lookup("e")));
    }

    [Fact]
    public void ASceneWithUnknownThingsLoadsWhatItCanAndABadFileIsAnError()
    {
        const string text = """
            <Scene version="1">
              <Entity id="1" name="x">
                <NoSuchThing a="1"/>
                <Health Current="3" Nope="1" Max="not a number"/>
              </Entity>
              <Entity id="2" name="x">
                <Frozen/>
              </Entity>
            </Scene>
            """;
        List<Entity> loaded = _world.LoadScene(text);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(3, _world.Get<Health>(loaded[0]).Current);   // the known field of a known component was read
        Assert.Equal(0, _world.Get<Health>(loaded[0]).Max);       // the one that was not a number stayed zero
        Assert.True(_world.Has<Frozen>(loaded[1]));
        Assert.Equal("x", _world.GetName(loaded[0]));
        Assert.Equal("x 2", _world.GetName(loaded[1]));            // the name was taken: a free one

        Assert.Throws<FormatException>(() => _world.LoadScene("not xml"));
        Assert.Throws<FormatException>(() => _world.LoadScene("<Scene version=\"1\"><Entity id=\"1\">"));       // not well-formed
        Assert.Throws<FormatException>(() => _world.LoadScene("<Level version=\"1\"/>"));                       // the wrong root
        Assert.Throws<FormatException>(() => _world.LoadScene("<Scene version=\"99\"/>"));                      // from a newer engine
        Assert.Throws<FormatException>(() => _world.LoadScene("<Scene version=\"one\"/>"));
        Assert.Throws<FormatException>(() => _world.LoadScene(""));
    }

    [Fact]
    public void JsonTextIsAnErrorThatSaysTheFormatIsNotReadAnyMore()
    {
        const string json = "{ \"entities\": [] }";

        FormatException e = Assert.Throws<FormatException>(() => _world.LoadScene(json));
        Assert.Contains("JSON", e.Message, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => _world.LoadScene("\uFEFF  \n" + json));   // a BOM and blank lines before it make no difference
        Assert.Empty(_world.Roots());
    }

    [Fact]
    public void ABomOrAnXmlDeclarationBeforeTheSceneIsFine()
    {
        List<Entity> loaded = _world.LoadScene("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!-- a comment -->\n<Scene version=\"1\"><Entity id=\"1\" name=\"a\"/></Scene>");
        Assert.Equal("a", _world.GetName(Assert.Single(loaded)));
    }

    [Fact]
    public void OneEntityAndItsBranchCanBeSavedAndLoadedAlone()
    {
        Entity a = Make("a");
        Entity b = Make("b", a);
        Make("unrelated");

        string text = _world.SaveScene(a);
        using var other = new World();
        List<Entity> loaded = other.LoadScene(text);

        Assert.Equal(2, loaded.Count);
        Assert.False(other.Lookup("a").IsNull);
        Assert.False(other.Lookup("a.b").IsNull);
        Assert.True(other.Lookup("unrelated").IsNull);
        Assert.Equal(b, _world.Children(a).Single());
    }

    [Fact]
    public void ADocumentCanBeSavedAndLoadedWithoutTheText()
    {
        Entity a = Make("a");
        Make("b", a);

        XmlNode document = _world.SaveSceneDocument(_world.Roots());
        Assert.Equal("Scene", document.Name);
        document.SetAttribute("uid", "uid://4k2m9x7q1w3ze");   // what a file adds; loading ignores it

        using var other = new World();
        Assert.Equal(2, other.LoadScene(document).Count);
        Assert.False(other.Lookup("a.b").IsNull);
        Assert.Throws<FormatException>(() => other.LoadScene(new XmlNode("Level")));
    }

    [Fact]
    public void NamesWithMarkupAndLineBreaksSurviveTheTrip()
    {
        const string name = "a <b> & \"c\" 'd'\nline two\ttab";
        _world.CreateEntity(name);

        string text = _world.SaveScene();
        using var other = new World();
        other.LoadScene(text);

        Assert.Equal(name, other.GetName(Assert.Single(other.Roots())));
        Assert.Equal(text, other.SaveScene());
    }

    [Fact]
    public void FloatsDoublesAndSpecialValuesRoundTripExactly()
    {
        Entity numbers = _world.DefineComponent("Numbers", [
            ("f", FieldKind.Float32), ("d", FieldKind.Float64), ("eps", FieldKind.Float32), ("big", FieldKind.Float64),
            ("nan", FieldKind.Float64), ("inf", FieldKind.Float32), ("ninf", FieldKind.Float64),
            ("lmin", FieldKind.Int64), ("lmax", FieldKind.Int64), ("umax", FieldKind.UInt64), ("beyond", FieldKind.Int64),
            ("sbyte", FieldKind.Int8), ("short", FieldKind.Int16), ("ushort", FieldKind.UInt16), ("uint", FieldKind.UInt32),
            ("v", FieldKind.Vector3), ("p", FieldKind.Double3), ("q", FieldKind.Quaternion)]);
        Entity e = Make("e");
        Put(_world, e, numbers, "f", 0.1f);
        Put(_world, e, numbers, "d", Math.PI);
        Put(_world, e, numbers, "eps", float.Epsilon);
        Put(_world, e, numbers, "big", double.MaxValue);
        Put(_world, e, numbers, "nan", double.NaN);
        Put(_world, e, numbers, "inf", float.PositiveInfinity);
        Put(_world, e, numbers, "ninf", double.NegativeInfinity);
        Put(_world, e, numbers, "lmin", long.MinValue);
        Put(_world, e, numbers, "lmax", long.MaxValue);
        Put(_world, e, numbers, "umax", ulong.MaxValue);
        Put(_world, e, numbers, "beyond", (1L << 53) + 1);        // the first integer a double cannot hold
        Put(_world, e, numbers, "sbyte", sbyte.MinValue);
        Put(_world, e, numbers, "short", short.MinValue);
        Put(_world, e, numbers, "ushort", ushort.MaxValue);
        Put(_world, e, numbers, "uint", uint.MaxValue);
        Put(_world, e, numbers, "v", new Vector3(float.MaxValue, -0f, 1e-30f));
        Put(_world, e, numbers, "p", new Double3(1e300, -1e-300, 123456789.123456789));
        Put(_world, e, numbers, "q", new Quaternion(0.1f, 0.2f, 0.3f, 0.9f));

        string text = _world.SaveScene();
        using var other = new World();
        Entity back = Assert.Single(other.LoadScene(text));

        Assert.Equal(0.1f, Read<float>(other, back, "Numbers", "f"));
        Assert.Equal(Math.PI, Read<double>(other, back, "Numbers", "d"));
        Assert.Equal(float.Epsilon, Read<float>(other, back, "Numbers", "eps"));
        Assert.Equal(double.MaxValue, Read<double>(other, back, "Numbers", "big"));
        Assert.True(double.IsNaN(Read<double>(other, back, "Numbers", "nan")));
        Assert.Equal(float.PositiveInfinity, Read<float>(other, back, "Numbers", "inf"));
        Assert.Equal(double.NegativeInfinity, Read<double>(other, back, "Numbers", "ninf"));
        Assert.Equal(long.MinValue, Read<long>(other, back, "Numbers", "lmin"));
        Assert.Equal(long.MaxValue, Read<long>(other, back, "Numbers", "lmax"));
        Assert.Equal(ulong.MaxValue, Read<ulong>(other, back, "Numbers", "umax"));
        Assert.Equal((1L << 53) + 1, Read<long>(other, back, "Numbers", "beyond"));
        Assert.Equal(sbyte.MinValue, Read<sbyte>(other, back, "Numbers", "sbyte"));
        Assert.Equal(short.MinValue, Read<short>(other, back, "Numbers", "short"));
        Assert.Equal(ushort.MaxValue, Read<ushort>(other, back, "Numbers", "ushort"));
        Assert.Equal(uint.MaxValue, Read<uint>(other, back, "Numbers", "uint"));
        Vector3 v = Read<Vector3>(other, back, "Numbers", "v");
        Assert.Equal(float.MaxValue, v.X);
        Assert.True(float.IsNegative(v.Y));                          // negative zero keeps its sign
        Assert.Equal(1e-30f, v.Z);
        Assert.Equal(new Double3(1e300, -1e-300, 123456789.123456789), Read<Double3>(other, back, "Numbers", "p"));
        Assert.Equal(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f), Read<Quaternion>(other, back, "Numbers", "q"));
        Assert.Equal(text, other.SaveScene());
    }

    [Fact]
    public void AnEntityFieldOutsideTheSceneIsOmitted()
    {
        Entity inside = Make("inside");
        Entity outside = Make("outside");
        _world.Set(inside, new Marks { Level = 1, Target = outside });
        Entity pointsAtInside = Make("points");
        _world.Set(pointsAtInside, new Marks { Level = 2, Target = inside });

        // The branch holds "inside" only: its target is not in the file, so the attribute is not either; "points" is not in it at all.
        string text = _world.SaveScene(inside);
        XmlNode marks = XmlNode.Parse(text).Child("Entity")!.Child("Marks")!;
        Assert.Null(marks.Attribute("Target"));
        Assert.Equal("1", marks.Attribute("Level"));

        using var other = new World();
        Entity loaded = Assert.Single(other.LoadScene(text));
        Assert.True(other.Get<Marks>(loaded).Target.IsNull);

        // A reference to an id the file does not have is no reference either.
        Entity dangling = Assert.Single(other.LoadScene("<Scene version=\"1\"><Entity id=\"1\" name=\"d\"><Marks Target=\"42\"/></Entity></Scene>"));
        Assert.True(other.Get<Marks>(dangling).Target.IsNull);
    }

    [Fact]
    public void TheWrittenSceneMatchesItsSchema()
    {
        _world.RegisterAssetType("model", handle => AssetRef.FromPath($"res://m{handle}.glb"), reference => 1);
        Entity script = _world.DefineComponent("Ticker", [("count", FieldKind.Int32), ("look", FieldKind.AssetRef)], assetTypes: new Dictionary<string, string> { ["look"] = "model" });
        Entity a = Make("root");
        _world.Set(a, new Furnished { Count = 2, Model = 3 });
        Entity b = Make("child", a);
        _world.Add(b, script);
        _world.Add<Frozen>(b);
        Make("second");

        var schemas = new SchemaSet();
        using (Stream stream = typeof(World).Assembly.GetManifestResourceStream("Scene/scene.xsd") ?? throw new InvalidOperationException("scene.xsd is not embedded"))
        using (var reader = new StreamReader(stream))
        {
            schemas.LoadText(reader.ReadToEnd(), "scene.xsd");
        }

        XmlNode document = _world.SaveSceneDocument(_world.Roots());
        DecodeResult result = schemas.Decode(document, "scene");
        Assert.True(result.Declared);
        Assert.Empty(result.Problems);

        // A file adds its UID, which the schema checks the form of.
        document.SetAttribute("uid", "uid://4k2m9x7q1w3ze");
        Assert.Empty(schemas.Decode(document, "scene").Problems);
        document.SetAttribute("uid", "uid://not-one");
        Assert.NotEmpty(schemas.Decode(document, "scene").Problems);
    }

    [Fact]
    public void ASceneCanBePatchedByDefMerge()
    {
        Make("one");
        Entity two = Make("two");
        _world.Set(two, new Health { Current = 5, Max = 8 });
        Entity three = Make("three");
        _world.Set(three, new Health { Current = 9, Max = 9 });
        XmlNode scene = XmlNode.Parse(_world.SaveScene());

        // A mod's patch: entity 2 moves; nothing else is named, so nothing else changes.
        XmlNode patch = XmlNode.Parse("<Scene version=\"1\"><Entity id=\"2\"><Transform Position=\"5 0 0\"/></Entity></Scene>");
        XmlNode merged = DefDatabase.Merge(scene, patch);

        using var other = new World();
        other.LoadScene(merged);
        Assert.Equal(new Double3(5, 0, 0), other.Get<Transform>(other.Lookup("two")).Position);
        Assert.Equal(new Quaternion(0, 0, 0, 1), other.Get<Transform>(other.Lookup("two")).Rotation);   // the other attributes of the element stayed
        Assert.Equal(5, other.Get<Health>(other.Lookup("two")).Current);
        Assert.Equal(new Double3(1, 2, 3), other.Get<Transform>(other.Lookup("one")).Position);
        Assert.Equal(new Double3(1, 2, 3), other.Get<Transform>(other.Lookup("three")).Position);
        Assert.Equal(9, other.Get<Health>(other.Lookup("three")).Current);
    }
}
