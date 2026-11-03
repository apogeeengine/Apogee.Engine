using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Apogee.Content.Xml;

/// <summary>What a simple type's values are, once its bases are laid under it.</summary>
internal enum BaseKind
{
    String,
    Number,
    Integer,
    Boolean,
    Vector3,
}

/// <summary>A simple type as the schema wrote it: the type it restricts (or the item type of a list) and the facets it adds.</summary>
internal sealed class SimpleTypeDef
{
    public string? BaseName { get; set; }

    public bool IsList { get; set; }

    public List<string> Enumeration { get; } = [];

    public double? Min { get; set; }

    public double? Max { get; set; }

    public bool MinExclusive { get; set; }

    public bool MaxExclusive { get; set; }

    public List<string> Patterns { get; } = [];

    public int MinLength { get; set; } = -1;

    public int MaxLength { get; set; } = -1;

    public int Length { get; set; } = -1;

    public string? Documentation { get; set; }

    public string Origin { get; set; } = "";

    public int Line { get; set; }

    /// <summary>Filled in the first time the type is used.</summary>
    public ResolvedSimple? Resolved { get; set; }
}

/// <summary>A simple type with all it inherits: converts the text of an attribute or an element to a <see cref="DefValue"/>, or says why it cannot.</summary>
internal sealed class ResolvedSimple
{
    private static readonly TimeSpan s_patternTimeout = TimeSpan.FromMilliseconds(200);

    public BaseKind Base { get; init; }

    /// <summary>The type of the items, for a list.</summary>
    public ResolvedSimple? Item { get; init; }

    public string[] Enumeration { get; init; } = [];

    public double? Min { get; init; }

    public double? Max { get; init; }

    public bool MinExclusive { get; init; }

    public bool MaxExclusive { get; init; }

    public Regex[] Patterns { get; init; } = [];

    public int MinLength { get; init; } = -1;

    public int MaxLength { get; init; } = -1;

    public int Length { get; init; } = -1;

    public string? Documentation { get; init; }

    public bool IsList => Item is not null;

    public static ResolvedSimple Of(BaseKind kind, double? min = null, double? max = null) => new() { Base = kind, Min = min, Max = max };

    /// <summary>Converts <paramref name="text"/>; on failure <paramref name="problem"/> says why and <paramref name="value"/> is the text as it was.</summary>
    public bool TryConvert(string text, out DefValue value, out string? problem)
    {
        problem = null;
        if (Item is not null)
        {
            string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var items = new List<DefValue>(parts.Length);
            foreach (string part in parts)
            {
                if (!Item.TryConvert(part, out DefValue item, out problem))
                {
                    value = new DefText(text);
                    return false;
                }

                items.Add(item);
            }

            value = new DefList(items);
            problem = CheckLengths(parts.Length, "items") is { } listProblem ? $"'{text}' {listProblem}" : null;
            return problem is null;
        }

        string trimmed = Base == BaseKind.String ? text : text.Trim();
        value = new DefText(text);
        switch (Base)
        {
            case BaseKind.Vector3:
                return TryParseVector(trimmed, out value, out problem);
            case BaseKind.Boolean:
                bool? flag = trimmed switch { "true" or "1" => true, "false" or "0" => false, _ => null };
                if (flag is null)
                {
                    problem = $"'{trimmed}' is not a boolean (true or false)";
                    return false;
                }

                value = new DefBool(flag.Value);
                break;
            case BaseKind.Integer:
                if (!IsInteger(trimmed) || !double.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double whole))
                {
                    problem = $"'{trimmed}' is not a whole number";
                    return false;
                }

                value = new DefNumber(whole);
                break;
            case BaseKind.Number:
                if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
                {
                    problem = $"'{trimmed}' is not a number";
                    return false;
                }

                value = new DefNumber(number);
                break;
        }

        problem = CheckFacets(trimmed, value);
        return problem is null;
    }

    private static bool TryParseVector(string text, out DefValue value, out string? problem)
    {
        string[] parts = text.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
        var numbers = new double[3];
        if (parts.Length != 3 || !TryNumber(parts[0], out numbers[0]) || !TryNumber(parts[1], out numbers[1]) || !TryNumber(parts[2], out numbers[2]))
        {
            value = new DefText(text);
            problem = $"'{text.Trim()}' is not a vector: three numbers, such as \"1 2.5 -3\"";
            return false;
        }

        value = new DefVector(numbers[0], numbers[1], numbers[2]);
        problem = null;
        return true;

        static bool TryNumber(string part, out double number) =>
            double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    }

    private static bool IsInteger(string text)
    {
        int start = text.Length > 0 && text[0] is '+' or '-' ? 1 : 0;
        if (text.Length <= start)
        {
            return false;
        }

        for (int i = start; i < text.Length; i++)
        {
            if (!char.IsAsciiDigit(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private string? CheckFacets(string text, DefValue value)
    {
        if (Enumeration.Length > 0 && !IsOneOf(text, value))
        {
            return $"'{text}' is not one of: {string.Join(", ", Enumeration)}";
        }

        if (value is DefNumber number)
        {
            if (Min is { } min && (MinExclusive ? number.Value <= min : number.Value < min))
            {
                return $"{text} is {(MinExclusive ? "not above" : "below")} the minimum {Format(min)}";
            }

            if (Max is { } max && (MaxExclusive ? number.Value >= max : number.Value > max))
            {
                return $"{text} is {(MaxExclusive ? "not below" : "above")} the maximum {Format(max)}";
            }
        }

        string? lengthProblem = CheckLengths(text.Length, "characters");
        if (lengthProblem is not null)
        {
            return $"'{text}' {lengthProblem}";
        }

        foreach (Regex pattern in Patterns)
        {
            try
            {
                if (!pattern.IsMatch(text))
                {
                    return $"'{text}' does not match the pattern '{pattern.ToString()[4..^2]}'";
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return $"'{text}' took too long to check against the pattern '{pattern.ToString()[4..^2]}'";
            }
        }

        return null;
    }

    private bool IsOneOf(string text, DefValue value)
    {
        foreach (string allowed in Enumeration)
        {
            bool same = value switch
            {
                DefNumber n => double.TryParse(allowed.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double a) && a == n.Value,
                DefBool b => (allowed.Trim() is "true" or "1") == b.Value,
                _ => allowed == text,
            };

            if (same)
            {
                return true;
            }
        }

        return false;
    }

    private string? CheckLengths(int length, string unit)
    {
        if (Length >= 0 && length != Length)
        {
            return $"has {length} {unit}, not {Length}";
        }

        if (MinLength >= 0 && length < MinLength)
        {
            return $"has {length} {unit}, fewer than {MinLength}";
        }

        return MaxLength >= 0 && length > MaxLength ? $"has {length} {unit}, more than {MaxLength}" : null;
    }

    private static string Format(double number) => number.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A regular expression that must match all of the text, as XSD patterns do.</summary>
    public static Regex AnchoredPattern(string pattern) => new("^(?:" + pattern + ")$", RegexOptions.CultureInvariant, s_patternTimeout);

    // ---- Built-in types -------------------------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, ResolvedSimple> s_builtins = new(StringComparer.Ordinal)
    {
        ["string"] = Of(BaseKind.String),
        ["normalizedString"] = Of(BaseKind.String),
        ["token"] = Of(BaseKind.String),
        ["anyURI"] = Of(BaseKind.String),
        ["ID"] = Of(BaseKind.String),
        ["IDREF"] = Of(BaseKind.String),
        ["NMTOKEN"] = Of(BaseKind.String),
        ["Name"] = Of(BaseKind.String),
        ["NCName"] = Of(BaseKind.String),
        ["language"] = Of(BaseKind.String),
        ["QName"] = Of(BaseKind.String),
        ["date"] = Of(BaseKind.String),
        ["dateTime"] = Of(BaseKind.String),
        ["time"] = Of(BaseKind.String),
        ["duration"] = Of(BaseKind.String),
        ["boolean"] = Of(BaseKind.Boolean),
        ["decimal"] = Of(BaseKind.Number),
        ["float"] = Of(BaseKind.Number),
        ["double"] = Of(BaseKind.Number),
        ["vector3"] = Of(BaseKind.Vector3),
        ["integer"] = Of(BaseKind.Integer),
        ["long"] = Of(BaseKind.Integer, long.MinValue, long.MaxValue),
        ["int"] = Of(BaseKind.Integer, int.MinValue, int.MaxValue),
        ["short"] = Of(BaseKind.Integer, short.MinValue, short.MaxValue),
        ["byte"] = Of(BaseKind.Integer, sbyte.MinValue, sbyte.MaxValue),
        ["nonNegativeInteger"] = Of(BaseKind.Integer, 0),
        ["positiveInteger"] = Of(BaseKind.Integer, 1),
        ["nonPositiveInteger"] = Of(BaseKind.Integer, null, 0),
        ["negativeInteger"] = Of(BaseKind.Integer, null, -1),
        ["unsignedLong"] = Of(BaseKind.Integer, 0, ulong.MaxValue),
        ["unsignedInt"] = Of(BaseKind.Integer, 0, uint.MaxValue),
        ["unsignedShort"] = Of(BaseKind.Integer, 0, ushort.MaxValue),
        ["unsignedByte"] = Of(BaseKind.Integer, 0, byte.MaxValue),
    };

    /// <summary>The built-in type called <paramref name="name"/> (<c>xs:int</c> or <c>int</c>).</summary>
    public static bool TryGetBuiltin(string name, out ResolvedSimple type)
    {
        int colon = name.IndexOf(':', StringComparison.Ordinal);
        return s_builtins.TryGetValue(colon < 0 ? name : name[(colon + 1)..], out type!);
    }

    /// <summary>A type with the facets of <paramref name="baseType"/> and then those of <paramref name="own"/> laid over them.</summary>
    public static ResolvedSimple Restrict(ResolvedSimple baseType, SimpleTypeDef own)
    {
        Regex[] ownPatterns = [.. own.Patterns.Select(AnchoredPattern)];
        return new ResolvedSimple
        {
            Base = baseType.Base,
            Item = baseType.Item,
            Enumeration = own.Enumeration.Count > 0 ? [.. own.Enumeration] : baseType.Enumeration,
            Min = own.Min ?? baseType.Min,
            MinExclusive = own.Min is not null ? own.MinExclusive : baseType.MinExclusive,
            Max = own.Max ?? baseType.Max,
            MaxExclusive = own.Max is not null ? own.MaxExclusive : baseType.MaxExclusive,
            Patterns = [.. baseType.Patterns, .. ownPatterns],
            MinLength = own.MinLength >= 0 ? own.MinLength : baseType.MinLength,
            MaxLength = own.MaxLength >= 0 ? own.MaxLength : baseType.MaxLength,
            Length = own.Length >= 0 ? own.Length : baseType.Length,
            Documentation = own.Documentation ?? baseType.Documentation,
        };
    }
}
