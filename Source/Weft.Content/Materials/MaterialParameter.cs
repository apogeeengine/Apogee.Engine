using System.Numerics;
using Weft.Core.Assets;

namespace Weft.Content.Materials;

#pragma warning disable CA1720 // names follow the elements of the format (Float, Int) and what they hold

/// <summary>The type of a material parameter: the element it is written as (<c>&lt;Float/&gt;</c>, <c>&lt;Color/&gt;</c>…) and what the shader receives.</summary>
public enum MaterialParameterType
{
    /// <summary>One number.</summary>
    Float,

    /// <summary>Two numbers.</summary>
    Vec2,

    /// <summary>Three numbers.</summary>
    Vec3,

    /// <summary>Four numbers.</summary>
    Vec4,

    /// <summary>A linear color: red, green, blue and alpha.</summary>
    Color,

    /// <summary>A whole number.</summary>
    Int,

    /// <summary>True or false.</summary>
    Bool,
}

/// <summary>
/// One parameter of a material: a name (what the shader calls it), a type and a value. The value of a <see cref="MaterialParameterType.Float"/> is in <see cref="Value"/>'s X, a vector's first
/// components are its own and the rest are 0, a color is all four (alpha 1 when the file gives three), an integer or a boolean is in <see cref="Integer"/> (0 or 1 for a boolean).
/// </summary>
/// <param name="Name">The name, unique among the parameters of a material.</param>
/// <param name="Type">The type.</param>
/// <param name="Value">The value of a float, a vector or a color.</param>
/// <param name="Integer">The value of an integer or a boolean.</param>
public readonly record struct MaterialParameter(string Name, MaterialParameterType Type, Vector4 Value, int Integer = 0)
{
    /// <summary>The number of a <see cref="MaterialParameterType.Float"/> parameter.</summary>
    public float AsFloat => Value.X;

    /// <summary>The value of a <see cref="MaterialParameterType.Bool"/> parameter.</summary>
    public bool AsBool => Integer != 0;

    /// <summary>A number parameter.</summary>
    public static MaterialParameter Float(string name, float value) => new(name, MaterialParameterType.Float, new Vector4(value, 0, 0, 0));

    /// <summary>A parameter of two numbers.</summary>
    public static MaterialParameter Vec2(string name, Vector2 value) => new(name, MaterialParameterType.Vec2, new Vector4(value, 0, 0));

    /// <summary>A parameter of three numbers.</summary>
    public static MaterialParameter Vec3(string name, Vector3 value) => new(name, MaterialParameterType.Vec3, new Vector4(value, 0));

    /// <summary>A parameter of four numbers.</summary>
    public static MaterialParameter Vec4(string name, Vector4 value) => new(name, MaterialParameterType.Vec4, value);

    /// <summary>A linear color.</summary>
    public static MaterialParameter Color(string name, Vector4 value) => new(name, MaterialParameterType.Color, value);

    /// <summary>A whole-number parameter.</summary>
    public static MaterialParameter Int(string name, int value) => new(name, MaterialParameterType.Int, Vector4.Zero, value);

    /// <summary>A true or false parameter.</summary>
    public static MaterialParameter Bool(string name, bool value) => new(name, MaterialParameterType.Bool, Vector4.Zero, value ? 1 : 0);
}

/// <summary>A texture a material gives its shader: the name the shader samples it by, and the image or texture asset.</summary>
/// <param name="Name">The name, unique among the textures of a material.</param>
/// <param name="Ref">The texture asset (an image, <c>.ktx2</c> or <c>.wtex</c>), by path, UID or both.</param>
public readonly record struct MaterialTexture(string Name, AssetRef Ref);
