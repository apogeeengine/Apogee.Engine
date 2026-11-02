using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Apogee.Reflection;
using Apogee.Reflection.Generator;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Apogee.Reflection.Tests;

public class GeneratorTests
{
    private const string Header = "using System;\nusing System.Linq;\nusing System.Numerics;\nusing System.Collections.Generic;\nusing Apogee.Reflection;\n";

    [Theory]
    [InlineData("Speed", "speed")]
    [InlineData("GetPackageIds", "get_package_ids")]
    [InlineData("GetHTTPResponse", "get_http_response")]
    [InlineData("Vector3Length", "vector3_length")]
    [InlineData("NoTitleBar", "no_title_bar")]
    [InlineData("ID", "id")]
    [InlineData("x", "x")]
    public void ScriptNamesAreSnakeCase(string name, string expected) => Assert.Equal(expected, Analyzer.SnakeCase(name));

    private const string ActorSource = Header + """
        namespace Fx.Actors;

        public enum Mode { Idle, FastMove, Stop }

        [Flags] public enum Parts { None = 0, Head = 1, Arm = 2, Leg = 4 }

        /// <summary>Something that moves.</summary>
        [ApogeeClass(Creatable = true)]
        public partial class Actor : EngineObject
        {
            /// <summary>How fast it goes, in meters per second.</summary>
            [Property] public float Speed { get; set; } = 2;
            [Property] public double Mass { get; set; } = 70;
            [Property] public string Label { get; set; } = "x";
            [Property] public Vector3 Position { get; set; }
            [Property] public Mode Mode { get; set; }
            [Property] public Parts Hurt { get; set; }
            [Property] public int Count { get; private set; } = 3;
            [Property] public int? Maybe { get; set; }
            [Property] public ulong Big { get; set; }
            [Property] public string[] Tags { get; set; } = [];
            [Property] public List<int> Numbers { get; set; } = new();
            [Property] public Actor? Target { get; set; }
            [Property(Name = "flag")] public bool IsOn;

            /// <summary>Moves to a place, and says
            /// whether it could (a line
            /// more).</summary>
            [Method] public bool TryMove(Vector3 to, float speed = 1f) { Position = to; return speed > 0; }
            [Method] public (int, string) Pair() => (7, "seven");
            [Method] public static int Twice(int x) => x * 2;
            [Method] public Mode Next(Mode from) => from == Mode.Stop ? Mode.Idle : from + 1;
            [Method] public void Take(Actor other, string? note = null) { Target = other; Label = note ?? "none"; }
            [Method] public int Sum(IReadOnlyList<int> numbers) => numbers.Sum();
            [Method] public Value Echo(Value value) => value;

            [Signal] public Signal<int, string> Damaged { get; } = new();
            [Signal] public Signal Died { get; } = new();
            [Method] public void Hit(int amount) => Damaged.Emit(amount, "hit");
            [Method] public void Die() => Died.Emit();
        }

        [ApogeeClass]
        public partial class Hero : Actor
        {
            [Property] public int Level { get; set; } = 1;
        }

        [ApogeeClass(Name = "secret_thing", Scriptable = false, ModsBlocked = true)]
        public partial class Hidden : EngineObject
        {
        }
        """;

    // A snippet registers its types when it loads, and a name can be registered once: load it once per process.
    private static readonly Lazy<System.Reflection.Assembly> s_actors = new(() => GeneratedCode.Build(ActorSource, "FxActors"));

    private static Actors LoadActors() => new(s_actors.Value);

    /// <summary>The loaded snippet, to create its objects by name.</summary>
    private readonly struct Actors(System.Reflection.Assembly assembly)
    {
        public EngineObject Create(string name) => (EngineObject)Activator.CreateInstance(assembly.GetType("Fx.Actors." + name)!)!;
    }

    [Fact]
    public void AClassIsRegisteredWithItsMembers()
    {
        LoadActors();

        ClassInfo actor = TypeRegistry.GetClass("Actor");
        Assert.Equal("EngineObject", actor.BaseName);
        Assert.Equal("Something that moves.", actor.Doc);
        Assert.True(actor.Creatable);
        Assert.True(actor.Scriptable);

        Assert.Equal(
            ["speed", "mass", "label", "position", "mode", "hurt", "count", "maybe", "big", "tags", "numbers", "target", "flag"],
            actor.Properties.Select(p => p.ScriptName));
        Assert.Equal(["try_move", "pair", "twice", "next", "take", "sum", "echo", "hit", "die"], actor.Methods.Select(m => m.ScriptName));
        Assert.Equal(["damaged", "died"], actor.Signals.Select(s => s.ScriptName));

        PropertyDef speed = actor.FindProperty("speed")!;
        Assert.Equal("How fast it goes, in meters per second.", speed.Doc);
        Assert.Equal(TypeRef.Float, speed.Type);
        Assert.Equal(TypeRef.Int.AsNullable(), actor.FindProperty("maybe")!.Type);
        Assert.Equal(TypeRef.Enum("Mode"), actor.FindProperty("mode")!.Type);
        Assert.Equal(TypeRef.List(TypeRef.String), actor.FindProperty("tags")!.Type);
        Assert.Equal(TypeRef.Object("Actor", nullable: true), actor.FindProperty("target")!.Type);
    }

    [Fact]
    public void AClassWithADescribedBaseInheritsItsMembers()
    {
        LoadActors();

        ClassInfo hero = TypeRegistry.GetClass("Hero");
        Assert.Equal("Actor", hero.BaseName);
        Assert.Equal(["level"], hero.Properties.Select(p => p.ScriptName));
        Assert.NotNull(hero.FindProperty("speed"));
        Assert.NotNull(hero.FindMethod("try_move"));
        Assert.NotNull(hero.FindSignal("damaged"));
        Assert.Null(TypeRegistry.GetClass("Actor").FindProperty("level"));
        Assert.True(hero.IsSubclassOf(TypeRegistry.GetClass("Actor")));
        Assert.True(hero.IsSubclassOf(TypeRegistry.GetClass("EngineObject")));
        Assert.False(TypeRegistry.GetClass("Actor").IsSubclassOf(hero));
    }

    [Fact]
    public void ClassFlagsAndNamesComeFromTheAttribute()
    {
        LoadActors();

        ClassInfo hidden = TypeRegistry.GetClass("secret_thing");
        Assert.False(hidden.Scriptable);
        Assert.True(hidden.ModsBlocked);
        Assert.False(hidden.Creatable);
    }

    [Fact]
    public void PropertiesReadAndWriteThroughValues()
    {
        Actors actors = LoadActors();
        EngineObject actor = actors.Create("Actor");
        ClassInfo info = actor.Class;
        Assert.Equal("Actor", info.Name);

        PropertyDef speed = info.FindProperty("speed")!;
        Assert.Equal(2.0, speed.Getter(actor).ToDouble());
        speed.Setter!(actor, Value.Float(5.5));
        Assert.Equal(5.5, speed.Getter(actor).ToDouble());

        PropertyDef position = info.FindProperty("position")!;
        position.Setter!(actor, Value.Vector(new Vector3(1, 2, 3)));
        Assert.Equal(new Vector3(1, 2, 3), position.Getter(actor).ToVector3());

        PropertyDef label = info.FindProperty("label")!;
        label.Setter!(actor, Value.String("hello"));
        Assert.Equal("hello", label.Getter(actor).ToStringValue());

        PropertyDef flag = info.FindProperty("flag")!;
        Assert.Equal(Value.Bool(false), flag.Getter(actor));
        flag.Setter!(actor, Value.Bool(true));
        Assert.True(flag.Getter(actor).ToBool());

        PropertyDef big = info.FindProperty("big")!;
        big.Setter!(actor, Value.Int(123456789012));
        Assert.Equal(123456789012L, big.Getter(actor).ToInt64());
    }

    [Fact]
    public void ReadOnlyPropertiesHaveNoSetter()
    {
        ClassInfo info = LoadActorsClass();

        Assert.Null(info.FindProperty("count")!.Setter);
        Assert.NotNull(info.FindProperty("speed")!.Setter);
    }

    private static ClassInfo LoadActorsClass()
    {
        LoadActors();
        return TypeRegistry.GetClass("Actor");
    }

    [Fact]
    public void NullablesAndListsRoundTrip()
    {
        Actors actors = LoadActors();
        EngineObject actor = actors.Create("Actor");
        ClassInfo info = actor.Class;

        PropertyDef maybe = info.FindProperty("maybe")!;
        Assert.True(maybe.Getter(actor).IsNil);
        maybe.Setter!(actor, Value.Int(4));
        Assert.Equal(4, maybe.Getter(actor).ToInt32());
        maybe.Setter!(actor, default);
        Assert.True(maybe.Getter(actor).IsNil);

        PropertyDef tags = info.FindProperty("tags")!;
        tags.Setter!(actor, Value.List([Value.String("a"), Value.String("b")]));
        Assert.Equal(["a", "b"], tags.Getter(actor).ToList().Select(v => v.ToStringValue()));

        PropertyDef target = info.FindProperty("target")!;
        Assert.True(target.Getter(actor).IsNil);
        target.Setter!(actor, Value.Object(actor));
        Assert.Same(actor, target.Getter(actor).ToObject<EngineObject>());
        target.Setter!(actor, default);
        Assert.True(target.Getter(actor).IsNil);
    }

    [Fact]
    public void EnumsAreMembersByName()
    {
        Actors actors = LoadActors();
        EngineObject actor = actors.Create("Actor");
        EnumInfo mode = TypeRegistry.GetEnum("Mode");
        Assert.False(mode.IsFlags);
        Assert.Equal(["idle", "fast_move", "stop"], mode.Members.Select(m => m.Name));

        PropertyDef property = actor.Class.FindProperty("mode")!;
        property.Setter!(actor, Value.Enum(mode, 1));
        Value read = property.Getter(actor);
        Assert.Equal(ValueKind.Enum, read.Kind);
        Assert.Equal("fast_move", read.EnumType.GetName(read.ToEnum()));

        EnumInfo parts = TypeRegistry.GetEnum("Parts");
        Assert.True(parts.IsFlags);
        Assert.Equal("head|leg", parts.GetName(5));
    }

    [Fact]
    public void MethodsTakeValuesAndReturnValues()
    {
        Actors actors = LoadActors();
        EngineObject actor = actors.Create("Actor");
        MethodDef tryMove = actor.Class.FindMethod("try_move")!;
        Assert.Equal([TypeRef.Bool], tryMove.Results);
        Assert.Equal(["to", "speed"], tryMove.Parameters.Select(p => p.Name));
        Assert.Equal([false, true], tryMove.Parameters.Select(p => p.Optional));
        Assert.Equal("Moves to a place, and says whether it could (a line more).", tryMove.Doc); // a summary over several lines is one line

        var results = new Value[1];
        tryMove.Invoke(actor, [Value.Vector(new Vector3(1, 0, 0)), Value.Float(2)], results);
        Assert.True(results[0].ToBool());
        tryMove.Invoke(actor, [Value.Vector(Vector3.Zero)], results); // the default speed
        Assert.True(results[0].ToBool());
        Assert.Equal(Vector3.Zero, actor.Class.FindProperty("position")!.Getter(actor).ToVector3());
    }

    [Fact]
    public void ATupleResultFillsOneValuePerItem()
    {
        Actors actors = LoadActors();
        EngineObject actor = actors.Create("Actor");
        MethodDef pair = actor.Class.FindMethod("pair")!;
        Assert.Equal([TypeRef.Int, TypeRef.String], pair.Results);

        var results = new Value[2];
        pair.Invoke(actor, [], results);
        Assert.Equal(7, results[0].ToInt32());
        Assert.Equal("seven", results[1].ToStringValue());
    }

    [Fact]
    public void StaticMethodsNeedNoTarget()
    {
        LoadActors();
        MethodDef twice = TypeRegistry.GetClass("Actor").FindMethod("twice")!;
        Assert.True(twice.IsStatic);

        var results = new Value[1];
        twice.Invoke(null, [Value.Int(21)], results);
        Assert.Equal(42, results[0].ToInt32());
    }

    [Fact]
    public void EnumsListsAndOptionalObjectsPassThroughMethods()
    {
        Actors actors = LoadActors();
        EngineObject actor = actors.Create("Actor");
        EngineObject other = actors.Create("Actor");
        EnumInfo mode = TypeRegistry.GetEnum("Mode");

        var results = new Value[1];
        actor.Class.FindMethod("next")!.Invoke(actor, [Value.Enum(mode, 2)], results);
        Assert.Equal(0, results[0].ToEnum()); // Stop → Idle

        actor.Class.FindMethod("sum")!.Invoke(actor, [Value.List([Value.Int(1), Value.Int(2), Value.Int(3)])], results);
        Assert.Equal(6, results[0].ToInt32());

        actor.Class.FindMethod("take")!.Invoke(actor, [Value.Object(other)], []);
        Assert.Equal("none", actor.Class.FindProperty("label")!.Getter(actor).ToStringValue());
        actor.Class.FindMethod("take")!.Invoke(actor, [Value.Object(other), Value.String("note")], []);
        Assert.Equal("note", actor.Class.FindProperty("label")!.Getter(actor).ToStringValue());

        actor.Class.FindMethod("echo")!.Invoke(actor, [Value.Float(1.5)], results);
        Assert.Equal(1.5, results[0].ToDouble());
    }

    [Fact]
    public void ABadCallIsAValueExceptionNotAnInvalidCast()
    {
        Actors actors = LoadActors();
        EngineObject actor = actors.Create("Actor");
        MethodDef twice = actor.Class.FindMethod("twice")!;
        var results = new Value[1];

        Assert.Contains("expected number, got string", Assert.Throws<ValueException>(() => twice.Invoke(null, [Value.String("x")], results)).Message);
        Assert.Contains("expects 1 arguments, got 0", Assert.Throws<ValueException>(() => twice.Invoke(null, [], results)).Message);
        Assert.Contains("expects 1 arguments, got 2", Assert.Throws<ValueException>(() => twice.Invoke(null, [Value.Int(1), Value.Int(2)], results)).Message);
        Assert.Contains("out of range", Assert.Throws<ValueException>(() => twice.Invoke(null, [Value.Int(long.MaxValue)], results)).Message);
        Assert.Contains("expects 1 to 2 arguments", Assert.Throws<ValueException>(() => actor.Class.FindMethod("take")!.Invoke(actor, [], [])).Message);
    }

    [Fact]
    public void SignalsReachScriptsAsValues()
    {
        Actors actors = LoadActors();
        EngineObject actor = actors.Create("Actor");
        SignalDef damaged = actor.Class.FindSignal("damaged")!;
        Assert.Equal([TypeRef.Int, TypeRef.String], damaged.Parameters.Select(p => p.Type));

        var received = new List<string>();
        Connection connection = damaged.Connect(actor, arguments => received.Add($"{arguments[0].ToInt32()}:{arguments[1].ToStringValue()}"));
        Assert.True(connection.IsConnected);

        var results = Array.Empty<Value>();
        actor.Class.FindMethod("hit")!.Invoke(actor, [Value.Int(9)], results);
        Assert.Equal(["9:hit"], received);

        connection.Disconnect();
        actor.Class.FindMethod("hit")!.Invoke(actor, [Value.Int(1)], results);
        Assert.Equal(["9:hit"], received);

        int died = 0;
        actor.Class.FindSignal("died")!.Connect(actor, arguments => died += arguments.Length + 1);
        actor.Class.FindMethod("die")!.Invoke(actor, [], results);
        Assert.Equal(1, died);
    }

    [Fact]
    public void ACreatableClassMakesObjects()
    {
        LoadActors();
        EngineObject made = TypeRegistry.GetClass("Actor").Create();
        Assert.Equal("Actor", made.Class.Name);
        Assert.False(made.IsDestroyed);
        Assert.Throws<InvalidOperationException>(() => TypeRegistry.GetClass("Hero").Create());
    }

    // ---- Components and libraries --------------------------------------------------------------------------------------------------

    [Fact]
    public void AComponentRecordsItsLayout()
    {
        GeneratedCode.Build(Header + """
            namespace Fx.Components;

            /// <summary>Where something is.</summary>
            [Component]
            public struct Mixed
            {
                public byte A;
                public int B;
                public Vector3 C;
                public Entity D;
                public bool E;
            }

            [Component(Name = "Frozen")]
            public struct FrozenTag { }
            """, "FxComponents");

        ComponentInfo mixed = TypeRegistry.FindComponent("Mixed")!;
        Assert.Equal("Where something is.", mixed.Doc);
        Assert.Equal(8, mixed.Alignment);
        Assert.Equal(40, mixed.Size);
        Assert.Equal(
            [("A", FieldKind.UInt8, 0), ("B", FieldKind.Int32, 4), ("C", FieldKind.Vector3, 8), ("D", FieldKind.Entity, 24), ("E", FieldKind.Bool, 32)],
            mixed.Fields.Select(f => (f.Name, f.Kind, f.Offset)));

        ComponentInfo frozen = TypeRegistry.FindComponent("Frozen")!;
        Assert.True(frozen.IsTag);
        Assert.Empty(frozen.Fields);
    }

    [Fact]
    public void WorldPositionsAndRotationsAreSupportedMembersAndFields()
    {
        System.Reflection.Assembly assembly = GeneratedCode.Build(Header + """
            using Apogee.Core.Numerics;
            namespace Fx.Spatial;

            [Component]
            public struct Placed
            {
                public byte Flag;
                public Double3 At;
                public Quaternion Facing;
                public Vector3 Size;
            }

            [ApogeeClass(Creatable = true)]
            public partial class Anchor : EngineObject
            {
                [Property] public Double3 Position { get; set; }
                [Property] public Quaternion Facing { get; set; } = Quaternion.Identity;
                [Method] public Double3 Offset(Double3 by, Quaternion turn) => Position + Vector3.Transform((Vector3)by, turn);
            }
            """, "FxSpatial");

        ComponentInfo placed = TypeRegistry.FindComponent("Placed")!;
        Assert.Equal(8, placed.Alignment);
        Assert.Equal(64, placed.Size); // 60, padded to the 8-byte alignment of the position
        Assert.Equal(
            [("Flag", FieldKind.UInt8, 0), ("At", FieldKind.Double3, 8), ("Facing", FieldKind.Quaternion, 32), ("Size", FieldKind.Vector3, 48)],
            placed.Fields.Select(f => (f.Name, f.Kind, f.Offset)));

        var anchor = (EngineObject)Activator.CreateInstance(assembly.GetType("Fx.Spatial.Anchor")!)!;
        ClassInfo info = anchor.Class;
        Assert.Equal(TypeRef.Vector3, info.FindProperty("position")!.Type);
        Assert.Equal(TypeRef.Quaternion, info.FindProperty("facing")!.Type);

        // A position crosses as a float vector and comes back as a double one.
        info.FindProperty("position")!.Setter!(anchor, Value.Vector(new Vector3(1, 2, 3)));
        Assert.Equal(new Vector3(1, 2, 3), info.FindProperty("position")!.Getter(anchor).ToVector3());
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        info.FindProperty("facing")!.Setter!(anchor, Value.Quat(turn));
        Assert.Equal(turn, info.FindProperty("facing")!.Getter(anchor).ToQuaternion());

        // (1, 2, 3) plus +Z turned a quarter about +Y, which is +X.
        var results = new Value[1];
        info.FindMethod("offset")!.Invoke(anchor, [Value.Vector(new Vector3(0, 0, 1)), Value.Quat(turn)], results);
        Vector3 moved = results[0].ToVector3();
        Assert.Equal(2, moved.X, 1e-4f);
        Assert.Equal(2, moved.Y, 1e-4f);
        Assert.Equal(3, moved.Z, 1e-4f);
    }

    [Fact]
    public void PropertiesAndComponentFieldsCarryWhatAnInspectorShows()
    {
        GeneratedCode.Build(Header + """
            namespace Fx.Inspector;

            [Component]
            public struct Engine
            {
                [Property(Min = 0, Max = 100, Step = 0.5, Category = "Power")]
                public float Throttle;

                [Property(Min = 0)]
                public int Gear;

                [Property(Hidden = true)]
                public uint Handle;

                [Property(ReadOnly = true, Category = "Quote \"q\"")]
                public double Hours;

                public bool Running;
            }

            /// <summary>A gauge.</summary>
            [ApogeeClass]
            public partial class Gauge : EngineObject
            {
                /// <summary>How full it is.</summary>
                [Property(Min = 0, Max = 1)] public float Level { get; set; }

                [Property(Max = 5)] public int Marks { get; set; }

                [Property] public string Label { get; set; } = "";
            }
            """, "FxInspector");

        ComponentInfo engine = TypeRegistry.FindComponent("Engine")!;
        FieldDef throttle = engine.FindField("Throttle")!;
        Assert.Equal(0, throttle.Meta!.Min);
        Assert.Equal(100, throttle.Meta.Max);
        Assert.Equal(0.5, throttle.Meta.Step);
        Assert.Equal("Power", throttle.Meta.Category);
        Assert.True(throttle.Meta.HasRange);
        Assert.Equal("0 to 100", throttle.Meta.RangeText);

        FieldDef gear = engine.FindField("Gear")!;
        Assert.Equal("from 0", gear.Meta!.RangeText);
        Assert.False(gear.Meta.HasRange);
        Assert.True(double.IsNaN(gear.Meta.Max));

        Assert.True(engine.FindField("Handle")!.Meta!.Hidden);
        FieldDef hours = engine.FindField("Hours")!;
        Assert.True(hours.Meta!.ReadOnly);
        Assert.Equal("Quote \"q\"", hours.Meta.Category);
        Assert.Null(engine.FindField("Running")!.Meta);  // no [Property], no hints

        ClassInfo gauge = TypeRegistry.GetClass("Gauge");
        Assert.Equal("0 to 1", gauge.FindProperty("level")!.Meta!.RangeText);
        Assert.Equal("up to 5", gauge.FindProperty("marks")!.Meta!.RangeText);
        Assert.Null(gauge.FindProperty("label")!.Meta);
    }

    [Fact]
    public void ALibraryIsAGlobalTableOfStaticMethods()
    {
        GeneratedCode.Build(Header + """
            namespace Fx.Libraries;

            /// <summary>Does sums.</summary>
            [ScriptLibrary("fx_math")]
            public static partial class MathLibrary
            {
                [Method] public static int Add(int a, int b) => a + b;
                [Method(Name = "scale")] public static Vector3 Scale(Vector3 v, float f = 2) => v * f;
            }
            """, "FxLibraries");

        LibraryInfo library = TypeRegistry.FindLibrary("fx_math")!;
        Assert.Equal("Does sums.", library.Doc);
        Assert.Equal(["add", "scale"], library.Methods.Select(m => m.ScriptName));

        var results = new Value[1];
        library.Methods[0].Invoke(null, [Value.Int(2), Value.Int(3)], results);
        Assert.Equal(5, results[0].ToInt32());
        library.Methods[1].Invoke(null, [Value.Vector(new Vector3(1, 2, 3))], results);
        Assert.Equal(new Vector3(2, 4, 6), results[0].ToVector3());
    }

    // ---- Diagnostics --------------------------------------------------------------------------------------------------------------------

    private static string[] DiagnosticIds(string source, string assembly) =>
        [.. GeneratedCode.Run(Header + source, assembly).Diagnostics.Select(d => d.Id)];

    [Fact]
    public void AClassThatIsNotPartialIsReported()
    {
        Assert.Equal(["APG001"], DiagnosticIds("[ApogeeClass] public class Plain : EngineObject { }", "FxDiag1"));
    }

    [Fact]
    public void AClassThatIsNotAnEngineObjectIsReported()
    {
        Assert.Equal(["APG001"], DiagnosticIds("[ApogeeClass] public partial class Other { }", "FxDiag2"));
    }

    [Fact]
    public void AnUnsupportedTypeIsReportedWithTheMember()
    {
        GeneratedCode code = GeneratedCode.Run(Header + "[ApogeeClass] public partial class Clock : EngineObject { [Property] public DateTime Now { get; set; } }", "FxDiag3");
        Diagnostic diagnostic = Assert.Single(code.Diagnostics);
        Assert.Equal("APG002", diagnostic.Id);
        Assert.Contains("Now", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("System.DateTime", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Empty(code.Generated); // nothing is written for a type with errors, so no cascade of compile errors
    }

    [Fact]
    public void TwoMembersWithOneScriptNameAreReported()
    {
        Assert.Equal(["APG003"], DiagnosticIds("""
            [ApogeeClass] public partial class Twin : EngineObject
            {
                [Property] public int Value { get; set; }
                [Property(Name = "value")] public int Other { get; set; }
            }
            """, "FxDiag4"));
    }

    [Fact]
    public void ANonUnmanagedComponentIsReported()
    {
        Assert.Equal(["APG004"], DiagnosticIds("[Component] public struct Named { public string Label; }", "FxDiag5"));
    }

    [Fact]
    public void AnUnsupportedComponentFieldIsReported()
    {
        Assert.Equal(["APG004"], DiagnosticIds("[Component] public struct Odd { public decimal Money; }", "FxDiag6"));
    }

    [Fact]
    public void ARefParameterIsReported()
    {
        Assert.Equal(["APG005"], DiagnosticIds("[ApogeeClass] public partial class Ref : EngineObject { [Method] public void Keep(ref int x) { } }", "FxDiag7"));
    }

    [Fact]
    public void ASignalOfTheWrongTypeIsReported()
    {
        Assert.Equal(["APG005"], DiagnosticIds("[ApogeeClass] public partial class Loud : EngineObject { [Signal] public int Noise { get; set; } }", "FxDiag8"));
    }

    [Fact]
    public void TheGeneratedCodeCompilesCleanlyInANestedClass()
    {
        GeneratedCode code = GeneratedCode.Run(Header + """
            namespace Fx.Nested;
            public static partial class Holder
            {
                [ApogeeClass] public partial class Inner : EngineObject { [Property] public int N { get; set; } }
            }
            """, "FxNested");
        Assert.Empty(code.Diagnostics);
        Assert.Empty(code.CompileErrors());
        code.Load();
        Assert.NotNull(TypeRegistry.FindClass("Inner"));
    }
}
