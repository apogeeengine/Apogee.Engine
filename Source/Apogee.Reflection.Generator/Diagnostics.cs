using Microsoft.CodeAnalysis;

namespace Apogee.Reflection.Generator;

internal static class Diagnostics
{
    private const string Category = "Apogee.Reflection";

    public static readonly DiagnosticDescriptor NotPartial = new(
        "APG001",
        "The type cannot be described",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedType = new(
        "APG002",
        "Unsupported type",
        "{0} cannot be described to scripts: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateName = new(
        "APG003",
        "Duplicate name",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor BadComponent = new(
        "APG004",
        "The component cannot be described",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor BadMember = new(
        "APG005",
        "The member cannot be described",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
