using System.Text;

namespace Apogee.Reflection;

/// <summary>The declared type of a property, parameter or result.</summary>
/// <param name="Name">The class or enum name for <see cref="ValueKind.Object"/> and <see cref="ValueKind.Enum"/>.</param>
/// <param name="Element">The element type of a <see cref="ValueKind.List"/>.</param>
public sealed record TypeRef(ValueKind Kind, bool Nullable = false, string? Name = null, TypeRef? Element = null)
{
    /// <summary>Any value (<see cref="Value"/> itself).</summary>
    public static readonly TypeRef Any = new(ValueKind.Nil, Nullable: true);

    public static readonly TypeRef Bool = new(ValueKind.Bool);
    public static readonly TypeRef Int = new(ValueKind.Int);
    public static readonly TypeRef Float = new(ValueKind.Float);
    public static readonly TypeRef String = new(ValueKind.String);
    public static readonly TypeRef Vector3 = new(ValueKind.Vector3);
    public static readonly TypeRef Quaternion = new(ValueKind.Quaternion);
    public static readonly TypeRef Entity = new(ValueKind.Entity);
    public static readonly TypeRef Function = new(ValueKind.Function);
    public static readonly TypeRef Table = new(ValueKind.Table);

    public bool IsAny => Kind == ValueKind.Nil;

    public static TypeRef Object(string className, bool nullable = false) => new(ValueKind.Object, nullable, className);

    public static TypeRef Enum(string enumName, bool nullable = false) => new(ValueKind.Enum, nullable, enumName);

    public static TypeRef List(TypeRef element, bool nullable = false) => new(ValueKind.List, nullable, null, element);

    public TypeRef AsNullable() => Nullable ? this : this with { Nullable = true };

    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append(Kind switch
        {
            ValueKind.Nil => "any",
            ValueKind.Object or ValueKind.Enum => Name,
            ValueKind.List => $"list<{Element}>",
            _ => Kind.ToString().ToLowerInvariant(),
        });
        if (Nullable && !IsAny)
        {
            text.Append('?');
        }

        return text.ToString();
    }
}
