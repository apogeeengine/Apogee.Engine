using Microsoft.CodeAnalysis;

namespace Weft.Reflection.Generator;

internal static class Diagnostics
{
    private const string Category = "Weft.Reflection";

    public static readonly DiagnosticDescriptor NotPartial = new(
        "WFT001",
        "The type cannot be described",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedType = new(
        "WFT002",
        "Unsupported type",
        "{0} cannot be described to scripts: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateName = new(
        "WFT003",
        "Duplicate name",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor BadComponent = new(
        "WFT004",
        "The component cannot be described",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor BadMember = new(
        "WFT005",
        "The member cannot be described",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor BadAsset = new(
        "WFT006",
        "The asset type cannot be used here",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor BadEnum = new(
        "WFT007",
        "The enum cannot be used here",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor BadColor = new(
        "WFT008",
        "The colour hint cannot be used here",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
