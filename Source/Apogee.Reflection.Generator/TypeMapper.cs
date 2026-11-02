using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Apogee.Reflection.Generator;

/// <summary>Maps the C# types a reflected member may have to the registry's shapes; anything else is an APG002 error.</summary>
internal static class TypeMapper
{
    private const string Ns = "Apogee.Reflection.";

    public static TypeModel? Map(ITypeSymbol type, Described described, ISymbol where, string what)
    {
        TypeModel? model = TryMap(type, described, out string reason);
        if (model is null)
        {
            Analyzer.Error(described, Diagnostics.UnsupportedType, where, what, reason);
        }

        return model;
    }

    private static TypeModel? TryMap(ITypeSymbol type, Described described, out string reason)
    {
        reason = "";
        bool nullable = type.NullableAnnotation == NullableAnnotation.Annotated;

        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableValue)
        {
            TypeModel? inner = TryMap(nullableValue.TypeArguments[0], described, out reason);
            if (inner is not null)
            {
                inner.Nullable = true;
            }

            return inner;
        }

        string display = type.ToDisplayString();
        string qualified = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).TrimEnd('?');

        TypeModel Make(Shape shape) => new() { Shape = shape, CsType = qualified, Nullable = nullable, IsValueType = type.IsValueType };

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return Make(Shape.Bool);
            case SpecialType.System_SByte: return Integer(Make(Shape.Int), "SByte");
            case SpecialType.System_Byte: return Integer(Make(Shape.Int), "Byte");
            case SpecialType.System_Int16: return Integer(Make(Shape.Int), "Int16");
            case SpecialType.System_UInt16: return Integer(Make(Shape.Int), "UInt16");
            case SpecialType.System_Int32: return Integer(Make(Shape.Int), "Int32");
            case SpecialType.System_UInt32: return Integer(Make(Shape.Int), "UInt32");
            case SpecialType.System_Int64: return Integer(Make(Shape.Int), "Int64");
            case SpecialType.System_UInt64:
                TypeModel unsigned = Integer(Make(Shape.Int), "UInt64");
                unsigned.IsUnsigned64 = true;
                return unsigned;
            case SpecialType.System_Single: return Make(Shape.Float);
            case SpecialType.System_Double:
                TypeModel number = Make(Shape.Float);
                number.IsDouble = true;
                return number;
            case SpecialType.System_String: return Make(Shape.String);
        }

        switch (display)
        {
            case "System.Numerics.Vector3": return Make(Shape.Vector3);
            case "Apogee.Core.Numerics.Double3":
                TypeModel position = Make(Shape.Vector3);
                position.IsDouble3 = true;
                return position;
            case "System.Numerics.Quaternion": return Make(Shape.Quaternion);
            case Ns + "Entity": return Make(Shape.Entity);
            case Ns + "Value": return Make(Shape.Any);
            case Ns + "ScriptFunction": return Make(Shape.Function);
            case Ns + "ScriptTable": return Make(Shape.Table);
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return MapEnum((INamedTypeSymbol)type, described, Make(Shape.Enum));
        }

        if (type is IArrayTypeSymbol { Rank: 1 } array)
        {
            TypeModel? element = TryMap(array.ElementType, described, out reason);
            return element is null ? null : List(Make(Shape.List), element, ListKind.Array);
        }

        if (type is INamedTypeSymbol { IsGenericType: true } generic && generic.TypeArguments.Length == 1)
        {
            string definition = generic.OriginalDefinition.ToDisplayString();
            ListKind? kind = definition switch
            {
                "System.Collections.Generic.List<T>" => ListKind.List,
                "System.Collections.Generic.IReadOnlyList<T>" => ListKind.ReadOnlyList,
                _ => null,
            };
            if (kind is not null)
            {
                TypeModel? element = TryMap(generic.TypeArguments[0], described, out reason);
                return element is null ? null : List(Make(Shape.List), element, kind.Value);
            }
        }

        if (type is INamedTypeSymbol { TypeKind: TypeKind.Class } named && DerivesFromEngineObject(named))
        {
            TypeModel model = Make(Shape.Object);
            model.RegistryName = RegistryName(named);
            return model;
        }

        reason = $"{display} is not a supported type (bool, integers, float, double, string, Vector3, Double3, Quaternion, enums, Entity, Value, ScriptFunction, ScriptTable, EngineObject classes, and lists of them).";
        return null;
    }

    private static TypeModel Integer(TypeModel model, string name)
    {
        model.IntegerName = name;
        return model;
    }

    private static TypeModel List(TypeModel model, TypeModel element, ListKind kind)
    {
        model.Element = element;
        model.ListKind = kind;
        return model;
    }

    private static bool DerivesFromEngineObject(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == Ns + "EngineObject")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The registry name of the class, or of its closest described base class; EngineObject for a class that is none.</summary>
    private static string RegistryName(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (current.GetAttributes().Any(a => a.AttributeClass?.Name == "ApogeeClassAttribute" && a.AttributeClass.ContainingNamespace.ToDisplayString() == "Apogee.Reflection"))
            {
                return Analyzer.RegistryNameOf(current);
            }
        }

        return "EngineObject";
    }

    private static TypeModel MapEnum(INamedTypeSymbol type, Described described, TypeModel model)
    {
        string key = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        model.EnumKey = key;
        model.RegistryName = type.Name;
        if (!described.Enums.ContainsKey(key))
        {
            var info = new EnumModel
            {
                Key = key,
                Name = type.Name,
                IsFlags = type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute"),
                Doc = Analyzer.Summary(type),
            };
            foreach (IFieldSymbol member in type.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsConst && f.ConstantValue is not null))
            {
                info.Members.Add(new System.Collections.Generic.KeyValuePair<string, long>(
                    Analyzer.SnakeCase(member.Name),
                    member.ConstantValue is ulong unsignedValue ? unchecked((long)unsignedValue) : Convert.ToInt64(member.ConstantValue, System.Globalization.CultureInfo.InvariantCulture)));
            }

            described.Enums[key] = info;
        }

        return model;
    }
}
