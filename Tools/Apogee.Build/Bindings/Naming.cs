using System;
using System.Collections.Generic;
using System.Text;

namespace Apogee.Build.Bindings;

/// <summary>Turns C identifiers into C# ones.</summary>
public static class Naming
{
    private static readonly HashSet<string> s_keywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while",
    ];

    public static string StripPrefix(string name, string[] prefixes)
    {
        foreach (string prefix in prefixes)
        {
            if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return name[prefix.Length..];
            }
        }

        return name;
    }

    /// <summary>A type or function name: the stripped C name with a capital first letter.</summary>
    public static string Capitalize(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    /// <summary><c>WINDOW_FULL_SCREEN</c> → <c>WindowFullScreen</c>, <c>windowID</c> → <c>WindowID</c>.</summary>
    public static string Pascal(string name)
    {
        if (!name.Contains('_', StringComparison.Ordinal))
        {
            return IsAllUpper(name) && name.Length > 1 ? Capitalize(name.ToLowerInvariant()) : Capitalize(name);
        }

        var result = new StringBuilder(name.Length);
        foreach (string word in name.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            result.Append(IsAllUpper(word) ? Capitalize(word.ToLowerInvariant()) : Capitalize(word));
        }

        return result.ToString();
    }

    /// <summary>Makes <paramref name="name"/> a valid member name: a leading digit gets an underscore.</summary>
    public static string Member(string name) =>
        name.Length > 0 && char.IsDigit(name[0]) ? "_" + name : name;

    /// <summary>A parameter name, escaped when it is a C# keyword.</summary>
    public static string Parameter(string name) => s_keywords.Contains(name) ? "@" + name : name;

    private static bool IsAllUpper(string text)
    {
        bool letters = false;
        foreach (char c in text)
        {
            if (char.IsLower(c))
            {
                return false;
            }

            letters |= char.IsUpper(c);
        }

        return letters;
    }
}
