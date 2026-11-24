using System.Linq;
using System.Runtime.CompilerServices;
using Weft.Reflection;
using Xunit;

namespace Weft.Rendering.Tests;

/// <summary>What the registry says about the light component, for inspectors: its colour is a colour and its kind names an enum. No GPU.</summary>
public class ComponentHintTests
{
    private static ComponentInfo LightInfo()
    {
        RuntimeHelpers.RunModuleConstructor(typeof(Light).Module.ModuleHandle);
        return TypeRegistry.GetComponent<Light>();
    }

    [Fact]
    public void TheColorOfALightIsAColor()
    {
        FieldDef color = LightInfo().FindField("Color")!;
        Assert.Equal(FieldKind.Vector3, color.Kind);
        Assert.True(color.Meta!.Color);
        Assert.Equal("Light", color.Meta.Category);   // the other hints stay
        Assert.False(LightInfo().FindField("Intensity")!.Meta!.Color);
    }

    [Fact]
    public void TheKindOfALightNamesItsEnumAndStaysAByte()
    {
        FieldDef kind = LightInfo().FindField("Kind")!;
        Assert.Equal(FieldKind.UInt8, kind.Kind);
        Assert.Equal("LightKind", kind.Enum);

        EnumInfo info = TypeRegistry.GetEnum(kind.Enum!);
        Assert.False(info.IsFlags);
        Assert.Equal([("directional", 0L), ("point", 1L), ("spot", 2L)], info.Members.Select(m => (m.Name, m.Value)));
        Assert.Equal("point", info.GetName(Light.Point(default, 1).Kind));   // what the helpers store is a member
    }
}
