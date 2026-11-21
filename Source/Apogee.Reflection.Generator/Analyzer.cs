using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Apogee.Reflection.Generator;

/// <summary>Reads the symbols of a described type into plain data (<see cref="Described"/>), so the emitter never touches Roslyn.</summary>
internal static class Analyzer
{
    private const string Ns = "Apogee.Reflection.";

    public static Described Class(GeneratorAttributeSyntaxContext context, CancellationToken cancellation) =>
        Analyze(DescribedKind.Class, context, cancellation);

    public static Described Component(GeneratorAttributeSyntaxContext context, CancellationToken cancellation) =>
        Analyze(DescribedKind.Component, context, cancellation);

    public static Described Library(GeneratorAttributeSyntaxContext context, CancellationToken cancellation) =>
        Analyze(DescribedKind.Library, context, cancellation);

    private static Described Analyze(DescribedKind kind, GeneratorAttributeSyntaxContext context, CancellationToken cancellation)
    {
        var symbol = (INamedTypeSymbol)context.TargetSymbol;
        AttributeData attribute = context.Attributes[0];
        var described = new Described
        {
            Kind = kind,
            Namespace = symbol.ContainingNamespace.IsGlobalNamespace ? "" : symbol.ContainingNamespace.ToDisplayString(),
            TypeName = symbol.Name,
            FullName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Doc = Summary(symbol),
        };

        string keyword = symbol.TypeKind == TypeKind.Struct ? "struct" : "class";
        described.Declaration = $"{keyword} {symbol.Name}";
        for (INamedTypeSymbol? container = symbol.ContainingType; container is not null; container = container.ContainingType)
        {
            described.Containers.Insert(0, $"{(container.TypeKind == TypeKind.Struct ? "struct" : "class")} {container.Name}");
            if (!IsPartial(container))
            {
                Error(described, Diagnostics.NotPartial, symbol, $"{container.Name} contains {symbol.Name} and must be partial.");
            }
        }

        if (symbol.IsGenericType)
        {
            Error(described, Diagnostics.NotPartial, symbol, $"{symbol.Name} is generic; only closed types are described.");
        }

        string? explicitName = kind == DescribedKind.Library
            ? attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string : null
            : Named(attribute, "Name") as string;
        described.RegistryName = explicitName ?? symbol.Name;

        switch (kind)
        {
            case DescribedKind.Class:
                AnalyzeClass(described, symbol, attribute, cancellation);
                break;
            case DescribedKind.Library:
                AnalyzeLibrary(described, symbol, cancellation);
                break;
            default:
                AnalyzeComponent(described, symbol, cancellation);
                break;
        }

        return described;
    }

    // ---- Classes -----------------------------------------------------------------------------------------------

    private static void AnalyzeClass(Described described, INamedTypeSymbol symbol, AttributeData attribute, CancellationToken cancellation)
    {
        if (!IsPartial(symbol))
        {
            Error(described, Diagnostics.NotPartial, symbol, $"{symbol.Name} is marked [ApogeeClass] and must be partial.");
        }

        described.Scriptable = Named(attribute, "Scriptable") as bool? ?? true;
        described.ModsBlocked = Named(attribute, "ModsBlocked") as bool? ?? false;
        described.Creatable = Named(attribute, "Creatable") as bool? ?? false;

        bool derives = false;
        for (INamedTypeSymbol? baseType = symbol.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.ToDisplayString() == Ns + "EngineObject")
            {
                derives = true;
                break;
            }

            if (described.BaseName == "EngineObject" && HasAttribute(baseType, "ApogeeClassAttribute"))
            {
                described.BaseName = RegistryNameOf(baseType);
            }
        }

        if (!derives)
        {
            Error(described, Diagnostics.NotPartial, symbol, $"{symbol.Name} must derive from EngineObject.");
        }

        if (described.Creatable && (symbol.IsAbstract || !symbol.Constructors.Any(c => !c.IsStatic && c.Parameters.Length == 0)))
        {
            Error(described, Diagnostics.NotPartial, symbol, $"{symbol.Name} is Creatable and needs to be concrete and have a parameterless constructor.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (ISymbol member in symbol.GetMembers())
        {
            cancellation.ThrowIfCancellationRequested();
            switch (member)
            {
                case IPropertySymbol or IFieldSymbol:
                    if (HasAttribute(member, "SignalAttribute"))
                    {
                        AddSignal(described, member, names);
                    }
                    else if (HasAttribute(member, "PropertyAttribute"))
                    {
                        AddProperty(described, member, names);
                    }

                    break;
                case IMethodSymbol method when HasAttribute(method, "MethodAttribute"):
                    AddMethod(described, method, names, allowInstance: true);
                    break;
            }
        }
    }

    private static void AnalyzeLibrary(Described described, INamedTypeSymbol symbol, CancellationToken cancellation)
    {
        if (!IsPartial(symbol) || !symbol.IsStatic)
        {
            Error(described, Diagnostics.NotPartial, symbol, $"{symbol.Name} is marked [ScriptLibrary] and must be a static partial class.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (IMethodSymbol method in symbol.GetMembers().OfType<IMethodSymbol>().Where(m => HasAttribute(m, "MethodAttribute")))
        {
            cancellation.ThrowIfCancellationRequested();
            AddMethod(described, method, names, allowInstance: false);
        }
    }

    private static void AddProperty(Described described, ISymbol member, HashSet<string> names)
    {
        ITypeSymbol type = member is IPropertySymbol p ? p.Type : ((IFieldSymbol)member).Type;
        if (member.IsStatic)
        {
            Error(described, Diagnostics.BadMember, member, $"{member.Name} is static; only instance properties are described.");
            return;
        }

        if (member is IPropertySymbol { IsIndexer: true })
        {
            Error(described, Diagnostics.BadMember, member, "Indexers are not described.");
            return;
        }

        // Asset names the type behind a uint handle of a component field. A class property that names an asset is an AssetRef instead (a string to scripts).
        if (AssetOf(member, out _))
        {
            Error(described, Diagnostics.BadAsset, member, $"{described.TypeName}.{member.Name}: Asset is only for uint fields of a [Component] struct. A property of a class that names an asset has the type Apogee.Core.Assets.AssetRef.");
            return;
        }

        // Enum names the enum behind an integer field of a component. A class property of an enum type already is one, and any other property has nothing to name.
        if (HintOf(member, "Enum", out _))
        {
            Error(described, Diagnostics.BadEnum, member, $"{described.TypeName}.{member.Name}: Enum is only for integer fields of a [Component] struct. A property of a class that has an enum type is described by that type.");
            return;
        }

        TypeModel? model = TypeMapper.Map(type, described, member, member.Name);
        if (model is null)
        {
            return;
        }

        if (HintOf(member, "Color", out object? color) && color is true && model is not { Shape: Shape.Vector3, IsDouble3: false })
        {
            Error(described, Diagnostics.BadColor, member, $"{described.TypeName}.{member.Name} is a {type.ToDisplayString()}: Color is only for Vector3 (a linear RGB colour).");
            return;
        }

        bool canWrite = member switch
        {
            IPropertySymbol { SetMethod: { } setter } => !setter.IsInitOnly && setter.DeclaredAccessibility == Accessibility.Public,
            IFieldSymbol field => !field.IsReadOnly && !field.IsConst,
            _ => false,
        };
        if (member is IPropertySymbol { GetMethod: null })
        {
            Error(described, Diagnostics.BadMember, member, $"{member.Name} has no getter.");
            return;
        }

        string scriptName = ScriptNameOf(member, "PropertyAttribute");
        if (!names.Add(scriptName))
        {
            Error(described, Diagnostics.DuplicateName, member, $"{described.TypeName} has two members named {scriptName} for scripts.");
            return;
        }

        described.Properties.Add(new PropertyModel { Name = member.Name, ScriptName = scriptName, Type = model, CanWrite = canWrite, Doc = Summary(member), Meta = MetaOf(member) });
    }

    private static void AddSignal(Described described, ISymbol member, HashSet<string> names)
    {
        ITypeSymbol type = member is IPropertySymbol p ? p.Type : ((IFieldSymbol)member).Type;
        var signal = new SignalModel { Name = member.Name, ScriptName = ScriptNameOf(member, "SignalAttribute"), Doc = Summary(member) };

        if (member.IsStatic || type is not INamedTypeSymbol named || named.ContainingNamespace.ToDisplayString() != "Apogee.Reflection" || named.Name != "Signal")
        {
            Error(described, Diagnostics.BadMember, member, $"{member.Name} must be an instance Signal or Signal<…> property or field.");
            return;
        }

        foreach (ITypeSymbol argument in named.TypeArguments)
        {
            TypeModel? model = TypeMapper.Map(argument, described, member, member.Name);
            if (model is null)
            {
                return;
            }

            signal.Parameters.Add(model);
        }

        if (!names.Add(signal.ScriptName))
        {
            Error(described, Diagnostics.DuplicateName, member, $"{described.TypeName} has two members named {signal.ScriptName} for scripts.");
            return;
        }

        described.Signals.Add(signal);
    }

    private static void AddMethod(Described described, IMethodSymbol method, HashSet<string> names, bool allowInstance)
    {
        if (method.MethodKind != MethodKind.Ordinary || method.IsGenericMethod)
        {
            Error(described, Diagnostics.BadMember, method, $"{method.Name} must be an ordinary, non-generic method.");
            return;
        }

        if (!method.IsStatic && !allowInstance)
        {
            Error(described, Diagnostics.BadMember, method, $"{method.Name} must be static: a script library has no instance.");
            return;
        }

        var model = new MethodModel { Name = method.Name, ScriptName = ScriptNameOf(method, "MethodAttribute"), IsStatic = method.IsStatic, Doc = Summary(method) };

        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None || parameter.IsParams)
            {
                Error(described, Diagnostics.BadMember, method, $"{method.Name}: parameter {parameter.Name} is ref, out, in or params.");
                return;
            }

            TypeModel? type = TypeMapper.Map(parameter.Type, described, method, $"{method.Name}({parameter.Name})");
            if (type is null)
            {
                return;
            }

            string? defaultText = null;
            if (parameter.HasExplicitDefaultValue)
            {
                var syntax = parameter.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as ParameterSyntax;
                defaultText = syntax?.Default?.Value.ToString() ?? "default";
            }

            model.Parameters.Add(new ParameterModel { Name = parameter.Name, Type = type, Default = defaultText });
        }

        if (method.ReturnType.SpecialType != SpecialType.System_Void)
        {
            if (method.ReturnType is INamedTypeSymbol { IsTupleType: true } tuple)
            {
                model.ReturnsTuple = true;
                foreach (IFieldSymbol element in tuple.TupleElements)
                {
                    TypeModel? item = TypeMapper.Map(element.Type, described, method, $"{method.Name} (result)");
                    if (item is null)
                    {
                        return;
                    }

                    model.Results.Add(item);
                }
            }
            else
            {
                TypeModel? result = TypeMapper.Map(method.ReturnType, described, method, $"{method.Name} (result)");
                if (result is null)
                {
                    return;
                }

                model.Results.Add(result);
            }
        }

        if (!names.Add(model.ScriptName))
        {
            Error(described, Diagnostics.DuplicateName, method, $"{described.TypeName} has two members named {model.ScriptName} for scripts.");
            return;
        }

        described.Methods.Add(model);
    }

    // ---- Components -----------------------------------------------------------------------------------------------

    private static void AnalyzeComponent(Described described, INamedTypeSymbol symbol, CancellationToken cancellation)
    {
        if (!symbol.IsUnmanagedType || symbol.TypeKind != TypeKind.Struct)
        {
            Error(described, Diagnostics.BadComponent, symbol, $"{symbol.Name} must be an unmanaged struct to be a component.");
            return;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (IFieldSymbol field in symbol.GetMembers().OfType<IFieldSymbol>())
        {
            cancellation.ThrowIfCancellationRequested();
            if (field.IsStatic || field.IsConst || field.DeclaredAccessibility != Accessibility.Public || field.IsImplicitlyDeclared)
            {
                continue;
            }

            (string Kind, int Alignment)? kind = FieldKindOf(field.Type);

            // A field declared with an enum type is stored as the enum's integer type and names that enum for inspectors.
            string? enumName = null;
            if (kind is not null && field.Type is INamedTypeSymbol { TypeKind: TypeKind.Enum } declared)
            {
                enumName = TypeMapper.Describe(declared, described).Name;
            }

            // [Property(Asset = "model")] on a uint turns the number into an asset handle. It is the only way to get the AssetRef kind, so a plain uint stays a number.
            string? assetType = null;
            if (AssetOf(field, out string? asset))
            {
                if (string.IsNullOrWhiteSpace(asset))
                {
                    Error(described, Diagnostics.BadAsset, field, $"{symbol.Name}.{field.Name}: Asset must name an asset type, such as \"model\".");
                    continue;
                }

                if (field.Type.SpecialType != SpecialType.System_UInt32)
                {
                    Error(described, Diagnostics.BadAsset, field, $"{symbol.Name}.{field.Name} is a {field.Type.ToDisplayString()}: Asset is only for uint fields (the handle of a loaded asset).");
                    continue;
                }

                kind = ("AssetRef", 4);
                assetType = asset;
            }

            if (kind is null)
            {
                Error(described, Diagnostics.BadComponent, field, $"{symbol.Name}.{field.Name} is a {field.Type.ToDisplayString()}: component fields are bool, integers, enums, float, double, Vector3, Double3, Quaternion or Entity.");
                continue;
            }

            // [Property(Enum = typeof(X))] on an integer field says that its numbers are the values of X. The field keeps its own kind: only the size and the range are checked.
            if (HintOf(field, "Enum", out object? hint))
            {
                string? problem = EnumHintProblem(field.Type, kind.Value.Kind, hint, out INamedTypeSymbol? hinted);
                if (problem is not null)
                {
                    Error(described, Diagnostics.BadEnum, field, $"{symbol.Name}.{field.Name}: {problem}");
                    continue;
                }

                enumName = TypeMapper.Describe(hinted!, described).Name;
            }

            if (HintOf(field, "Color", out object? color) && color is true && kind.Value.Kind != "Vector3")
            {
                Error(described, Diagnostics.BadColor, field, $"{symbol.Name}.{field.Name} is a {field.Type.ToDisplayString()}: Color is only for Vector3 (a linear RGB colour).");
                continue;
            }

            names.Add(field.Name);
            described.Fields.Add(new FieldModel
            {
                Name = field.Name,
                Kind = kind.Value.Kind,
                Alignment = kind.Value.Alignment,
                CsType = field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                Doc = Summary(field),
                Meta = MetaOf(field),
                AssetType = assetType,
                Enum = enumName,
            });
        }
    }

    /// <summary>Why <c>[Property(Enum = …)]</c> cannot go on this field, or null when it can; <paramref name="hinted"/> is the enum then.</summary>
    private static string? EnumHintProblem(ITypeSymbol fieldType, string fieldKind, object? hint, out INamedTypeSymbol? hinted)
    {
        hinted = null;
        if (fieldType.TypeKind == TypeKind.Enum)
        {
            return $"the field has the enum type {fieldType.ToDisplayString()}, which names its enum already: Enum is for the integer fields that are not declared with one.";
        }

        if (fieldKind == "AssetRef")
        {
            return "a field that names an asset (Asset) is a handle and not an enum value.";
        }

        if (IntegerSize(fieldKind) == 0)
        {
            return $"Enum is only for integer fields, and this is a {fieldType.ToDisplayString()}.";
        }

        if (hint is not INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying } named)
        {
            return "Enum must be typeof(an enum).";
        }

        // The size must match, so that the enum is what the bytes of the field can hold. Signedness is not compared (an enum of byte on an sbyte field is fine while its members fit).
        string? underlyingKind = FieldKindOf(underlying)?.Kind;
        if (underlyingKind is null || IntegerSize(underlyingKind) != IntegerSize(fieldKind))
        {
            return $"{named.ToDisplayString()} is stored as {underlying.ToDisplayString()} ({IntegerSize(underlyingKind ?? "")} bytes) and the field is a {fieldType.ToDisplayString()} ({IntegerSize(fieldKind)} bytes): declare the enum with the field's type (enum {named.Name} : {fieldType.ToDisplayString()}).";
        }

        (decimal least, decimal most) = RangeOf(fieldKind);
        foreach (IFieldSymbol member in named.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsConst && f.ConstantValue is not null))
        {
            decimal value = Convert.ToDecimal(member.ConstantValue, CultureInfo.InvariantCulture);
            if (value < least || value > most)
            {
                return $"{named.Name}.{member.Name} = {value.ToString(CultureInfo.InvariantCulture)} does not fit a {fieldType.ToDisplayString()}.";
            }
        }

        hinted = named;
        return null;
    }

    /// <summary>The size in bytes of the integer kinds (not <c>Bool</c>, which is no enum's type); 0 for every other kind.</summary>
    private static int IntegerSize(string kind) => kind switch
    {
        "Int8" or "UInt8" => 1,
        "Int16" or "UInt16" => 2,
        "Int32" or "UInt32" => 4,
        "Int64" or "UInt64" => 8,
        _ => 0,
    };

    private static (decimal Least, decimal Most) RangeOf(string integerKind) => integerKind switch
    {
        "Int8" => (sbyte.MinValue, sbyte.MaxValue),
        "UInt8" => (byte.MinValue, byte.MaxValue),
        "Int16" => (short.MinValue, short.MaxValue),
        "UInt16" => (ushort.MinValue, ushort.MaxValue),
        "Int32" => (int.MinValue, int.MaxValue),
        "UInt32" => (uint.MinValue, uint.MaxValue),
        "Int64" => (long.MinValue, long.MaxValue),
        _ => (ulong.MinValue, ulong.MaxValue),
    };

    private static (string Kind, int Alignment)? FieldKindOf(ITypeSymbol type)
    {
        // An enum is stored as its underlying integer type (C# allows no other kind).
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying })
        {
            return FieldKindOf(underlying);
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return ("Bool", 1);
            case SpecialType.System_SByte: return ("Int8", 1);
            case SpecialType.System_Byte: return ("UInt8", 1);
            case SpecialType.System_Int16: return ("Int16", 2);
            case SpecialType.System_UInt16: return ("UInt16", 2);
            case SpecialType.System_Int32: return ("Int32", 4);
            case SpecialType.System_UInt32: return ("UInt32", 4);
            case SpecialType.System_Int64: return ("Int64", 8);
            case SpecialType.System_UInt64: return ("UInt64", 8);
            case SpecialType.System_Single: return ("Float32", 4);
            case SpecialType.System_Double: return ("Float64", 8);
        }

        return type.ToDisplayString() switch
        {
            "System.Numerics.Vector3" => ("Vector3", 4),
            "Apogee.Core.Numerics.Double3" => ("Double3", 8),
            "System.Numerics.Quaternion" => ("Quaternion", 4),
            Ns + "Entity" => ("Entity", 8),
            _ => null,
        };
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    internal static void Error(Described described, DiagnosticDescriptor descriptor, ISymbol where, params object[] arguments)
    {
        described.HasErrors = true;
        described.Diagnostics.Add(Diagnostic.Create(descriptor, where.Locations.FirstOrDefault(), arguments));
    }

    private static bool IsPartial(INamedTypeSymbol symbol) =>
        symbol.DeclaringSyntaxReferences.All(r => r.GetSyntax() is TypeDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword));

    private static bool HasAttribute(ISymbol symbol, string name) =>
        symbol.GetAttributes().Any(a => a.AttributeClass is { } c && c.Name == name && c.ContainingNamespace.ToDisplayString() == "Apogee.Reflection");

    private static object? Named(AttributeData attribute, string name) =>
        attribute.NamedArguments.Where(a => a.Key == name).Select(a => (object?)a.Value.Value).FirstOrDefault();

    internal static string RegistryNameOf(INamedTypeSymbol type)
    {
        AttributeData? attribute = type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "ApogeeClassAttribute");
        return (attribute is null ? null : Named(attribute, "Name") as string) ?? type.Name;
    }

    /// <summary>The <c>new InspectorMeta(...)</c> expression for the hints a <c>[Property]</c> carries, or null when it carries none.</summary>
    private static string? MetaOf(ISymbol member)
    {
        AttributeData? attribute = member.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "PropertyAttribute");
        if (attribute is null)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (string name in new[] { "Min", "Max", "Step" })
        {
            if (Named(attribute, name) is double value && !double.IsNaN(value))
            {
                parts.Add($"{name}: {value.ToString("R", CultureInfo.InvariantCulture)}d");
            }
        }

        if (Named(attribute, "Category") is string category)
        {
            parts.Add($"Category: \"{category.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
        }

        foreach (string name in new[] { "Hidden", "ReadOnly", "Color" })
        {
            if (Named(attribute, name) is true)
            {
                parts.Add($"{name}: true");
            }
        }

        return parts.Count == 0 ? null : $"new global::Apogee.Reflection.InspectorMeta({string.Join(", ", parts)})";
    }

    /// <summary>Whether <c>[Property]</c> on the member sets <c>Asset</c> (even to null or empty, which is an error to report), and its value.</summary>
    private static bool AssetOf(ISymbol member, out string? asset)
    {
        bool set = HintOf(member, "Asset", out object? value);
        asset = value as string;
        return set;
    }

    /// <summary>Whether <c>[Property]</c> on the member sets the named argument (even to null), and its value: a bool, a string, or the <c>ITypeSymbol</c> of a <c>typeof</c>.</summary>
    private static bool HintOf(ISymbol member, string name, out object? value)
    {
        value = null;
        AttributeData? attribute = member.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "PropertyAttribute" && a.AttributeClass.ContainingNamespace.ToDisplayString() == "Apogee.Reflection");
        if (attribute is null || !attribute.NamedArguments.Any(a => a.Key == name))
        {
            return false;
        }

        value = Named(attribute, name);
        return true;
    }

    private static string ScriptNameOf(ISymbol member, string attributeName)
    {
        AttributeData? attribute = member.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == attributeName);
        return (attribute is null ? null : Named(attribute, "Name") as string) ?? SnakeCase(member.Name);
    }

    /// <summary><c>GetHTTPResponse</c> → <c>get_http_response</c>, <c>Vector3Length</c> → <c>vector3_length</c>.</summary>
    internal static string SnakeCase(string name)
    {
        var text = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                bool afterLowerOrDigit = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                bool endOfAcronym = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (afterLowerOrDigit || endOfAcronym)
                {
                    text.Append('_');
                }

                text.Append(char.ToLowerInvariant(c));
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    /// <summary>The text of the <c>&lt;summary&gt;</c> in the comment above the declaration, on one line; null without one.</summary>
    internal static string? Summary(ISymbol symbol)
    {
        SyntaxReference? reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null)
        {
            return null;
        }

        SyntaxNode node = reference.GetSyntax();
        if (node is VariableDeclaratorSyntax { Parent.Parent: { } field })
        {
            node = field;
        }

        // Without GenerateDocumentationFile the compiler leaves /// as plain comments; with it they are structured trivia.
        string comment = string.Concat(node.GetLeadingTrivia()
            .Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
            .Select(t => t.ToFullString()));
        Match match = Regex.Match(comment, @"<summary>(?<text>.*?)</summary>", RegexOptions.Singleline);
        if (!match.Success)
        {
            return null;
        }

        string text = match.Groups["text"].Value;
        text = text.Replace("///", " ");
        text = Regex.Replace(text, @"<(?:see|paramref)\s+(?:cref|name)=""(?:[A-Z]:)?([^""]*)""\s*/>", "$1");
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length == 0 ? null : text;
    }
}
