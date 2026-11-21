using Apogee.Editor.Editing;
using Apogee.Reflection;
using Apogee.Rendering;
using Xunit;

namespace Apogee.Editor.Tests;

/// <summary>The field codec is unchanged by enum fields: the inspector's numbers for one are its integer's.</summary>
[Collection("editor")]
public sealed unsafe class EnumFieldCodecTests
{
    [Fact]
    public void AnEnumFieldIsEditedAsItsInteger()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(Light).Module.ModuleHandle);
        ComponentInfo light = TypeRegistry.GetComponent<Light>();
        FieldDef kind = light.FindField("Kind")!;
        Assert.NotNull(kind.Enum);

        // The widget of an enum field is the whole-number one until the inspector draws a combo for it.
        Assert.Equal(FieldWidget.WholeNumber, FieldCodec.WidgetOf(kind.Kind));
        Assert.Equal(1, FieldCodec.CountOf(kind.Kind));

        Light value = Light.Point(default, 1);
        byte* data = (byte*)&value;
        Assert.Equal([1.0], FieldCodec.Read(data + kind.Offset, kind.Kind));

        FieldCodec.Write(data + kind.Offset, kind.Kind, [2]);
        Assert.Equal(2, value.Kind);

        FieldCodec.Write(data + kind.Offset, kind.Kind, [300]);   // an integer is clamped to its type, not to the enum's members
        Assert.Equal(255, value.Kind);
        Assert.Equal("255", TypeRegistry.GetEnum(kind.Enum!).GetName(value.Kind));
    }
}
