using System;
using System.Linq;
using System.Reflection;
using Apogee.Reflection;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Apogee.Reflection.Tests;

/// <summary>Enum fields and the colour hint of components: what the generator reads, what it refuses, and what the registry then holds.</summary>
public sealed unsafe class InspectorHintTests
{
    private const string Header = "using System;\nusing System.Numerics;\nusing System.Collections.Generic;\nusing Apogee.Reflection;\nusing Apogee.Core.Numerics;\n";

    private static string[] Ids(string source, string assembly) =>
        [.. GeneratedCode.Run(Header + source, assembly).Diagnostics.Select(d => d.Id)];

    [Fact]
    public void AnEnumFieldIsItsUnderlyingKindAndNamesItsEnum()
    {
        GeneratedCode code = GeneratedCode.Run(Header + """
            namespace Fx.EnumFields;

            public enum Small : byte { A, B, C = 255 }
            public enum Signed : sbyte { Low = -128, Zero = 0, High = 127 }
            public enum Short : short { Neg = -1, Pos = 300 }
            public enum UShort : ushort { Big = 65535 }
            public enum Plain { Idle, Walk, Run }
            public enum Wide : uint { Top = uint.MaxValue }
            public enum Narrow : uint { One = 1 }
            public enum Huge : long { Min = long.MinValue, Max = long.MaxValue }
            public enum Giant : ulong { Max = ulong.MaxValue }

            /// <summary>Which parts.</summary>
            [Flags] public enum HintParts : byte { None = 0, Head = 1, Arm = 2, Leg = 4 }

            public enum Twins { First = 1, Second = 1 }
            public enum Empty { }
            public class Generic<T> { public enum Inner { X, Y } }
            public static class Holder { public enum Nested : byte { P, Q } }

            [Component]
            public struct Everything
            {
                public Small A;
                public Signed B;
                public Short C;
                public UShort D;
                public Plain E;
                public Wide F;
                public Huge G;
                public Giant H;
                public HintParts I;
                public Twins J;
                public Generic<int>.Inner K;
                public Holder.Nested L;
                public DayOfWeek M;   // an enum of another assembly

                /// <summary>A number that is a Plain.</summary>
                [Property(Enum = typeof(Plain), Category = "Raw")] public int Raw;
                [Property(Enum = typeof(Small))] public byte RawByte;
                [Property(Enum = typeof(DayOfWeek))] public int Day;
                [Property(Enum = typeof(Huge))] public long Wide64;
                [Property(Enum = typeof(Narrow))] public int SameSizeOtherSign;   // 4 bytes, and the one member fits: signedness is not compared
                public Empty Nothing;
            }

            // A class that uses one of the same enums is the same EnumInfo.
            [ApogeeClass]
            public partial class Walker : EngineObject
            {
                [Property] public Plain Gait { get; set; }
            }
            """, "FxEnumFields");
        Assert.Empty(code.Diagnostics);

        // Direct calls only: nothing reflects on the field or the enum.
        string text = code.GeneratedText;
        Assert.DoesNotContain("System.Reflection", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Enum.GetValues", text, StringComparison.Ordinal);
        Assert.Contains("Enum: \"Small\"", text, StringComparison.Ordinal);
        code.Load();

        ComponentInfo info = TypeRegistry.FindComponent("Everything")!;
        (string Name, FieldKind Kind, string? Enum)[] expected =
        [
            ("A", FieldKind.UInt8, "Small"),
            ("B", FieldKind.Int8, "Signed"),
            ("C", FieldKind.Int16, "Short"),
            ("D", FieldKind.UInt16, "UShort"),
            ("E", FieldKind.Int32, "Plain"),
            ("F", FieldKind.UInt32, "Wide"),
            ("G", FieldKind.Int64, "Huge"),
            ("H", FieldKind.UInt64, "Giant"),
            ("I", FieldKind.UInt8, "HintParts"),
            ("J", FieldKind.Int32, "Twins"),
            ("K", FieldKind.Int32, "Inner"),
            ("L", FieldKind.UInt8, "Nested"),
            ("M", FieldKind.Int32, "DayOfWeek"),
            ("Raw", FieldKind.Int32, "Plain"),
            ("RawByte", FieldKind.UInt8, "Small"),
            ("Day", FieldKind.Int32, "DayOfWeek"),
            ("Wide64", FieldKind.Int64, "Huge"),
            ("SameSizeOtherSign", FieldKind.Int32, "Narrow"),
            ("Nothing", FieldKind.Int32, "Empty"),
        ];
        Assert.Equal(expected, info.Fields.Select(f => (f.Name, f.Kind, f.Enum)));

        // The memory is what it was: offsets and the size are the struct's own, which the compiler laid out for the integer type.
        Type type = info.ClrType!;
        Assert.Equal(System.Runtime.InteropServices.Marshal.SizeOf(type), info.Size);
        Assert.Equal("Raw", info.FindField("Raw")!.Name);
        Assert.Equal("Raw", info.FindField("Raw")!.Meta!.Category);   // the other hints stay with it
        Assert.Equal("A number that is a Plain.", info.FindField("Raw")!.Doc);
        Assert.Null(info.FindField("Raw")!.AssetType);

        // Every enum is registered, once, with its members as script names.
        EnumInfo small = TypeRegistry.GetEnum("Small");
        Assert.Equal([("a", 0L), ("b", 1L), ("c", 255L)], small.Members.Select(m => (m.Name, m.Value)));
        Assert.Equal([("low", -128L), ("zero", 0L), ("high", 127L)], TypeRegistry.GetEnum("Signed").Members.Select(m => (m.Name, m.Value)));
        Assert.Equal([("neg", -1L), ("pos", 300L)], TypeRegistry.GetEnum("Short").Members.Select(m => (m.Name, m.Value)));
        Assert.Equal([("min", long.MinValue), ("max", long.MaxValue)], TypeRegistry.GetEnum("Huge").Members.Select(m => (m.Name, m.Value)));
        Assert.Equal([("max", -1L)], TypeRegistry.GetEnum("Giant").Members.Select(m => (m.Name, m.Value)));   // the bits of ulong.MaxValue, as Value.Enum holds them
        Assert.Equal([("top", (long)uint.MaxValue)], TypeRegistry.GetEnum("Wide").Members.Select(m => (m.Name, m.Value)));
        Assert.Equal([("first", 1L), ("second", 1L)], TypeRegistry.GetEnum("Twins").Members.Select(m => (m.Name, m.Value)));   // duplicate values stay members
        Assert.Equal("first", TypeRegistry.GetEnum("Twins").GetName(1));
        Assert.Empty(TypeRegistry.GetEnum("Empty").Members);
        Assert.Equal(["x", "y"], TypeRegistry.GetEnum("Inner").Members.Select(m => m.Name));
        Assert.True(TypeRegistry.GetEnum("HintParts").IsFlags);
        Assert.Equal("Which parts.", TypeRegistry.GetEnum("HintParts").Doc);
        Assert.False(TypeRegistry.GetEnum("Plain").IsFlags);
        Assert.Contains("sunday", TypeRegistry.GetEnum("DayOfWeek").Members.Select(m => m.Name));   // from another assembly: no doc, members read from metadata
        Assert.Null(TypeRegistry.GetEnum("DayOfWeek").Doc);
        Assert.Equal(1, TypeRegistry.Enums.Count(e => e.Name == "Plain"));   // the class property and the fields share it

        // Scripts, scenes and snapshots see the integer: the field codec does not know about enums.
        byte* data = stackalloc byte[info.Size];
        new Span<byte>(data, info.Size).Clear();
        FieldDef gait = info.FindField("E")!;
        FieldAccess.Write(data, gait, Value.Int(2));
        Assert.Equal(Value.Int(2), FieldAccess.Read(data, gait));
        FieldAccess.Write(data, info.FindField("B")!, Value.Int(-128));
        Assert.Equal(Value.Int(-128), FieldAccess.Read(data, info.FindField("B")!));
        new Span<byte>(data + info.FindField("H")!.Offset, 8).Fill(0xFF);   // Giant.Max: scripts read the bits of a ulong as a long
        Assert.Equal(Value.Int(-1), FieldAccess.Read(data, info.FindField("H")!));
        Assert.Throws<ValueException>(() => FieldAccess.Write(data, info.FindField("A")!, Value.Int(256)));   // the integer's own range check
        Assert.Equal("2", FieldText.Format(gait, data + gait.Offset));
        FieldText.Parse(gait, "1", data + gait.Offset);
        Assert.Equal(Value.Int(1), FieldAccess.Read(data, gait));
    }

    [Fact]
    public void AHintAtTheEdgesOfItsRangeIsAccepted()
    {
        GeneratedCode code = GeneratedCode.Run(Header + """
            namespace Fx.Edges;

            public enum EdgeBig : ulong { Zero = 0, Max = ulong.MaxValue }
            public enum EdgeNeg : sbyte { Min = -128, Neg = -1, Max = 127 }

            [Component]
            public struct Edges
            {
                [Property(Enum = typeof(EdgeBig))] public ulong Big;
                [Property(Enum = typeof(EdgeNeg))] public sbyte Neg;
                [Property(Enum = typeof(EdgeNeg))] public byte Unsigned;   // same size: the members -1 and -128 do not fit, so this is refused below
            }
            """, "FxEdges");
        Diagnostic refused = Assert.Single(code.Diagnostics);
        Assert.Equal("APG007", refused.Id);
        Assert.Contains("Edges.Unsigned", refused.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        GeneratedCode fine = GeneratedCode.Run(Header + """
            namespace Fx.EdgesOk;

            public enum EdgeBigOk : ulong { Zero = 0, Max = ulong.MaxValue }
            public enum EdgeNegOk : sbyte { Min = -128, Neg = -1, Max = 127 }

            [Component]
            public struct EdgesOk
            {
                [Property(Enum = typeof(EdgeBigOk))] public ulong Big;
                [Property(Enum = typeof(EdgeNegOk))] public sbyte Neg;
            }
            """, "FxEdgesOk");
        Assert.Empty(fine.Diagnostics);
        fine.Load();
        ComponentInfo info = TypeRegistry.FindComponent("EdgesOk")!;
        Assert.Equal([("Big", FieldKind.UInt64, "EdgeBigOk"), ("Neg", FieldKind.Int8, "EdgeNegOk")], info.Fields.Select(f => (f.Name, f.Kind, f.Enum)));
        Assert.Equal([("zero", 0L), ("max", -1L)], TypeRegistry.GetEnum("EdgeBigOk").Members.Select(m => (m.Name, m.Value)));
    }

    [Fact]
    public void AnUnboundGenericEnumHintIsADiagnosticOrHarmlessButNeverACrash()
    {
        GeneratedCode code = GeneratedCode.Run(Header + """
            public class Box<T> { public enum Slot : byte { A, B } }

            [Component]
            public struct Unbound { [Property(Enum = typeof(Box<>.Slot))] public byte V; }
            """, "FxUnbound");

        // Either the generator refuses it (APG007) or it describes the enum by name like any other: what it must not do is throw.
        Assert.All(code.Diagnostics, d => Assert.Equal("APG007", d.Id));
        if (code.Diagnostics.IsEmpty)
        {
            code.Load();
            Assert.Equal("Slot", TypeRegistry.FindComponent("Unbound")!.FindField("V")!.Enum);
        }
    }

    [Fact]
    public void AClassPropertyEnumAndAComponentFieldEnumWithOneNameAreADiagnostic()
    {
        GeneratedCode code = GeneratedCode.Run(Header + """
            namespace Fx.Clash2.One { public enum Mode2 : byte { Idle, Walk } }
            namespace Fx.Clash2.Two { public enum Mode2 : byte { Fast, Slow } }

            [ApogeeClass]
            public partial class Walks : EngineObject { [Property] public Fx.Clash2.One.Mode2 Gait { get; set; } }

            [Component]
            public struct Clashing2 { public Fx.Clash2.Two.Mode2 Second; }
            """, "FxClash2");
        Diagnostic diagnostic = Assert.Single(code.Diagnostics);
        Assert.Equal("APG003", diagnostic.Id);
        Assert.True(diagnostic.Location.IsInSource);   // it points at an enum, not nowhere
        Assert.Empty(code.Generated);
    }

    [Fact]
    public void ConstructionsOfOneGenericEnumShareAName()
    {
        GeneratedCode code = GeneratedCode.Run(Header + """
            namespace Fx.GenericEnums;

            public class Box<T> { public enum Slot : byte { Left, Right } }

            [Component]
            public struct TwoBoxes
            {
                public Box<int>.Slot First;
                public Box<string>.Slot Second;
            }
            """, "FxGenericEnums");
        Assert.Empty(code.Diagnostics);
        code.Load();
        Assert.Equal(["Slot", "Slot"], TypeRegistry.FindComponent("TwoBoxes")!.Fields.Select(f => f.Enum));
        Assert.Equal(1, TypeRegistry.Enums.Count(e => e.Name == "Slot"));
    }

    [Fact]
    public void TwoDifferentEnumsWithOneNameAreADiagnostic()
    {
        GeneratedCode code = GeneratedCode.Run(Header + """
            namespace Fx.Clash.One { public enum Mode : byte { Idle, Walk } }
            namespace Fx.Clash.Two { public enum Mode : byte { Fast, Slow } }

            [Component]
            public struct Clashing
            {
                public Fx.Clash.One.Mode First;
                public Fx.Clash.Two.Mode Second;
            }
            """, "FxClash");
        Diagnostic diagnostic = Assert.Single(code.Diagnostics);
        Assert.Equal("APG003", diagnostic.Id);
        Assert.Contains("Mode", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Empty(code.Generated);
    }

    [Fact]
    public void AnEnumThatIsNotPublicStillIsAComponentFieldWhenTheStructCanSeeIt()
    {
        // Internal enum and struct in one assembly: the generated registry class is in the same assembly.
        GeneratedCode code = GeneratedCode.Run(Header + """
            namespace Fx.Internal;

            internal enum Hidden : byte { A, B }

            [Component]
            internal struct Quiet { public Hidden State; }
            """, "FxInternalEnum");
        Assert.Empty(code.Diagnostics);
        code.Load();
        Assert.Equal("Hidden", TypeRegistry.FindComponent("Quiet")!.FindField("State")!.Enum);
    }

    [Theory]
    [InlineData("public enum E : int { A } [Component] public struct S { [Property(Enum = typeof(E))] public byte V; }")]   // enum wider than the field
    [InlineData("public enum E : byte { A } [Component] public struct S { [Property(Enum = typeof(E))] public int V; }")]   // enum narrower than the field
    [InlineData("public enum E : long { A } [Component] public struct S { [Property(Enum = typeof(E))] public uint V; }")]
    [InlineData("public enum E : ushort { A } [Component] public struct S { [Property(Enum = typeof(E))] public sbyte V; }")]
    [InlineData("[Component] public struct S { [Property(Enum = typeof(DayOfWeek))] public byte V; }")]   // an enum of another assembly, too wide
    [InlineData("public enum E : byte { A, Big = 200 } [Component] public struct S { [Property(Enum = typeof(E))] public sbyte V; }")]   // the member 200 is no sbyte
    [InlineData("public enum E : uint { A, Big = 3000000000 } [Component] public struct S { [Property(Enum = typeof(E))] public int V; }")]
    [InlineData("public enum E : long { A = -1 } [Component] public struct S { [Property(Enum = typeof(E))] public ulong V; }")]   // a negative member in an unsigned field
    [InlineData("public enum E : byte { A } [Component] public struct S { [Property(Enum = typeof(E))] public float V; }")]   // not an integer
    [InlineData("public enum E : byte { A } [Component] public struct S { [Property(Enum = typeof(E))] public bool V; }")]
    [InlineData("public enum E : byte { A } [Component] public struct S { [Property(Enum = typeof(E))] public Vector3 V; }")]
    [InlineData("public enum E : byte { A } [Component] public struct S { [Property(Enum = typeof(E))] public Entity V; }")]
    [InlineData("public enum E : uint { A } [Component] public struct S { [Property(Enum = typeof(E), Asset = \"model\")] public uint V; }")]   // an asset handle
    [InlineData("public enum E : byte { A } [Component] public struct S { [Property(Enum = typeof(E))] public E V; }")]   // declared with the enum already
    [InlineData("public enum E : byte { A } public enum F : byte { B } [Component] public struct S { [Property(Enum = typeof(F))] public E V; }")]
    [InlineData("[Component] public struct S { [Property(Enum = typeof(int))] public int V; }")]   // not an enum
    [InlineData("[Component] public struct S { [Property(Enum = typeof(System.Enum))] public int V; }")]   // the base class of enums is not one
    [InlineData("[Component] public struct S { [Property(Enum = typeof(List<int>))] public int V; }")]
    [InlineData("[Component] public struct S { [Property(Enum = null)] public int V; }")]
    [InlineData("public enum E : byte { A } [ApogeeClass] public partial class S : EngineObject { [Property(Enum = typeof(E))] public int V { get; set; } }")]   // not on a class
    public void EnumOnAMismatchedFieldIsADiagnostic(string source)
    {
        GeneratedCode code = GeneratedCode.Run(Header + source, "FxEnumDiag");
        Diagnostic diagnostic = Assert.Single(code.Diagnostics);
        Assert.Equal("APG007", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Empty(code.Generated);   // a type with an error writes nothing
    }

    [Fact]
    public void TheMismatchMessageNamesTheFieldAndTheFix()
    {
        GeneratedCode code = GeneratedCode.Run(Header + "public enum Kind : int { A } [Component] public struct Lamp { [Property(Enum = typeof(Kind))] public byte Mode; }", "FxEnumMessage");
        string message = Assert.Single(code.Diagnostics).GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains("Lamp.Mode", message, StringComparison.Ordinal);
        Assert.Contains("Kind", message, StringComparison.Ordinal);
        Assert.Contains("byte", message, StringComparison.Ordinal);
        Assert.Contains("enum Kind : byte", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnumFieldThatIsNotAnIntegerEnumIsRefusedAsBefore()
    {
        // A nullable enum is a Nullable<E>: not unmanaged.
        Assert.Equal(["APG004"], Ids("public enum E : byte { A } [Component] public struct S { public E? V; }", "FxEnumNullable"));
    }

    [Fact]
    public void ColorOnlyGoesOnVector3()
    {
        GeneratedCode code = GeneratedCode.Run(Header + """
            namespace Fx.Colors;

            [Component]
            public struct Tint
            {
                [Property(Color = true, Category = "Look")] public Vector3 Albedo;
                [Property(Color = false)] public Vector3 Offset;   // explicitly off is fine anywhere
                [Property(Color = false)] public float Strength;
                public Vector3 Plain;
                [Property(Min = 0)] public float Gloss;
            }

            [ApogeeClass]
            public partial class Painter : EngineObject
            {
                [Property(Color = true)] public Vector3 Ink { get; set; }
                [Property(Color = true)] public Vector3? Spare { get; set; }
            }
            """, "FxColors");
        Assert.Empty(code.Diagnostics);
        code.Load();

        ComponentInfo tint = TypeRegistry.FindComponent("Tint")!;
        Assert.True(tint.FindField("Albedo")!.Meta!.Color);
        Assert.Equal("Look", tint.FindField("Albedo")!.Meta!.Category);
        Assert.Null(tint.FindField("Offset")!.Meta);   // Color = false is the default: no hints at all
        Assert.Null(tint.FindField("Strength")!.Meta);
        Assert.Null(tint.FindField("Plain")!.Meta);
        Assert.False(tint.FindField("Gloss")!.Meta!.Color);
        Assert.Null(tint.FindField("Albedo")!.Enum);

        ClassInfo painter = TypeRegistry.GetClass("Painter");
        Assert.True(painter.FindProperty("ink")!.Meta!.Color);
        Assert.True(painter.FindProperty("spare")!.Meta!.Color);

        foreach (string field in new[] { "float", "double", "int", "bool", "Quaternion", "Double3", "Entity", "byte" })
        {
            Assert.Equal(["APG008"], Ids($"[Component] public struct S {{ [Property(Color = true)] public {field} V; }}", "FxColorDiag"));
        }

        Assert.Equal(["APG008"], Ids("[Component] public struct S { [Property(Color = true, Asset = \"model\")] public uint V; }", "FxColorDiag"));
        Assert.Equal(["APG008"], Ids("public enum E : byte { A } [Component] public struct S { [Property(Color = true)] public E V; }", "FxColorDiag"));
        Assert.Equal(["APG008"], Ids("[ApogeeClass] public partial class S : EngineObject { [Property(Color = true)] public float V { get; set; } }", "FxColorDiag"));
        Assert.Equal(["APG008"], Ids("[ApogeeClass] public partial class S : EngineObject { [Property(Color = true)] public Double3 V { get; set; } }", "FxColorDiag"));   // a position, not a colour
        Assert.Equal(["APG008"], Ids("[ApogeeClass] public partial class S : EngineObject { [Property(Color = true)] public string V { get; set; } = \"\"; }", "FxColorDiag"));

        GeneratedCode wrong = GeneratedCode.Run(Header + "[Component] public struct Glow { [Property(Color = true)] public float Level; }", "FxColorMessage");
        string message = Assert.Single(wrong.Diagnostics).GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains("Glow.Level", message, StringComparison.Ordinal);
        Assert.Contains("Vector3", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratedFieldKeepsTheOrderOfItsPositionalParameters()
    {
        GeneratedCode code = GeneratedCode.Run(Header + """
            namespace Fx.Order;

            public enum Mood : byte { Calm, Angry }

            [Component]
            public struct Everyone
            {
                /// <summary>The handle.</summary>
                [Property(Asset = "model", Category = "A", Color = false)] public uint Model;
                /// <summary>The mood.</summary>
                [Property(Min = 0, Max = 1, Step = 0.5, Category = "B", Hidden = true, ReadOnly = true, Enum = typeof(Mood))] public byte Feeling;
                [Property(Color = true, Hidden = true)] public Vector3 Hue;
            }
            """, "FxOrder");
        Assert.Empty(code.Diagnostics);
        code.Load();

        ComponentInfo info = TypeRegistry.FindComponent("Everyone")!;
        FieldDef model = info.FindField("Model")!;
        Assert.Equal(("model", "The handle.", "A"), (model.AssetType, model.Doc, model.Meta!.Category));
        Assert.Null(model.Enum);

        FieldDef feeling = info.FindField("Feeling")!;
        Assert.Equal("Mood", feeling.Enum);
        Assert.Equal("The mood.", feeling.Doc);
        Assert.Equal(new InspectorMeta(0, 1, 0.5, "B", true, true), feeling.Meta);   // the six positional parameters, in their old order, and Color false

        FieldDef hue = info.FindField("Hue")!;
        Assert.Equal(new InspectorMeta(Hidden: true, Color: true), hue.Meta);

        // A call written before the additions means what it meant.
        var meta = new InspectorMeta(1, 2, 3, "c", true, true);
        Assert.False(meta.Color);
        var field = new FieldDef("f", FieldKind.Int32, 4, "doc", meta, "model");
        Assert.Equal(("f", FieldKind.Int32, 4, "doc", "model"), (field.Name, field.Kind, field.Offset, field.Doc, field.AssetType));
        Assert.Same(meta, field.Meta);
        Assert.Null(field.Enum);
        Assert.Equal("Mood", (field with { Enum = "Mood" }).Enum);
    }
}
