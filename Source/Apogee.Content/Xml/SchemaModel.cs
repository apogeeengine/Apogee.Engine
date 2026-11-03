using System.Collections.Generic;

namespace Apogee.Content.Xml;

/// <summary>A schema is invalid, or uses something the engine's XSD subset does not have. The message names the file and line.</summary>
public sealed class SchemaException : ContentException
{
    public SchemaException(string origin, int line, string message)
        : base($"{origin}:{line}: {message}")
    {
    }

    public SchemaException(string message)
        : base(message)
    {
    }
}

internal sealed class AttributeDecl
{
    public string Name { get; set; } = "";

    public string? TypeName { get; set; }

    public SimpleTypeDef? Inline { get; set; }

    public bool Required { get; set; }

    public bool Prohibited { get; set; }

    public string? Default { get; set; }

    public string? Documentation { get; set; }

    public string Origin { get; set; } = "";

    public int Line { get; set; }
}

/// <summary>An element declaration (a global one or a particle of a type), an <c>xs:any</c>, or a reference to a named group.</summary>
internal sealed class ParticleDecl
{
    public string Name { get; set; } = "";

    /// <summary>The global element this one stands for.</summary>
    public string? Ref { get; set; }

    public string? TypeName { get; set; }

    public ComplexTypeDef? InlineComplex { get; set; }

    public SimpleTypeDef? InlineSimple { get; set; }

    public bool IsAny { get; set; }

    public string? GroupRef { get; set; }

    public int Min { get; set; } = 1;

    /// <summary>-1 for unbounded.</summary>
    public int Max { get; set; } = 1;

    public string? Default { get; set; }

    public string? Documentation { get; set; }

    /// <summary>The <c>map</c> convention: a container of keyed entries is a record by the keys.</summary>
    public bool Map { get; set; }

    /// <summary>The <c>defaults</c> convention: an element that is left out is decoded as an empty one, so the defaults of its attributes apply.</summary>
    public bool Defaults { get; set; }

    public string Origin { get; set; } = "";

    public int Line { get; set; }

    public ParticleDecl Copy() => (ParticleDecl)MemberwiseClone();
}

internal sealed class ComplexTypeDef
{
    public string Name { get; set; } = "";

    /// <summary>The type this one extends.</summary>
    public string? BaseName { get; set; }

    public List<AttributeDecl> Attributes { get; } = [];

    public List<ParticleDecl> Particles { get; } = [];

    public List<string> AttributeGroupRefs { get; } = [];

    /// <summary>simpleContent: the element holds text of the type <see cref="TextTypeName"/> (or <see cref="TextInline"/>), beside its attributes.</summary>
    public bool SimpleContent { get; set; }

    public string? TextTypeName { get; set; }

    public bool AnyAttribute { get; set; }

    public string? Documentation { get; set; }

    public string Origin { get; set; } = "";

    public int Line { get; set; }

    /// <summary>Filled in the first time the type is used.</summary>
    public TypeShape? Shape { get; set; }
}

internal sealed class GroupDef
{
    public string Name { get; set; } = "";

    public List<ParticleDecl> Particles { get; } = [];

    public List<AttributeDecl> Attributes { get; } = [];

    public List<string> AttributeGroupRefs { get; } = [];

    public string Origin { get; set; } = "";

    public int Line { get; set; }
}

/// <summary>An element a type allows, with how often: the declarations of one name added together.</summary>
internal sealed class ElementSlot(string name, ParticleDecl declaration)
{
    public string Name { get; } = name;

    public ParticleDecl Declaration { get; } = declaration;

    public int Min { get; set; }

    public int Max { get; set; }

    public bool Repeats => Max is -1 or > 1;
}

/// <summary>A complex type with what it extends and the groups it uses laid out flat: the attributes and elements it allows.</summary>
internal sealed class TypeShape
{
    public List<AttributeDecl> Attributes { get; } = [];

    public List<ElementSlot> Slots { get; } = [];

    public bool SimpleContent { get; set; }

    public ResolvedSimple? Text { get; set; }

    /// <summary>The type has an <c>xs:any</c>: elements it does not declare are allowed.</summary>
    public bool Open { get; set; }

    public bool AnyAttribute { get; set; }
}
