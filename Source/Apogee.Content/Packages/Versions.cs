using System;
using System.Collections.Generic;
using System.Globalization;

namespace Apogee.Content.Packages;

/// <summary>A version as <c>major.minor.patch</c>: <c>1.2</c> is <c>1.2.0</c>, a leading <c>v</c> and any <c>-suffix</c> or <c>+build</c> are ignored.</summary>
public readonly record struct PackageVersion(int Major, int Minor, int Patch) : IComparable<PackageVersion>
{
    public static bool TryParse(string text, out PackageVersion version)
    {
        version = default;
        string trimmed = text.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        int cut = trimmed.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            trimmed = trimmed[..cut];
        }

        string[] parts = trimmed.Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        int[] numbers = new int[3];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new PackageVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public int CompareTo(PackageVersion other)
    {
        int major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        int minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(PackageVersion a, PackageVersion b) => a.CompareTo(b) < 0;

    public static bool operator >(PackageVersion a, PackageVersion b) => a.CompareTo(b) > 0;

    public static bool operator <=(PackageVersion a, PackageVersion b) => a.CompareTo(b) <= 0;

    public static bool operator >=(PackageVersion a, PackageVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>
/// Which versions a dependency or a game accepts: comparators separated by spaces or commas that must all hold. <c>&gt;=1.2</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&lt;</c>, <c>=1.2</c> (or just
/// <c>1.2</c>), <c>^1.2.3</c> (at least that, below the next version of its first non-zero part: <c>&lt;2.0.0</c>, and <c>^0.2.3</c> is <c>&lt;0.3.0</c>), <c>~1.2.3</c> (below the next minor:
/// <c>&lt;1.3.0</c>), and <c>*</c> (any). Empty means any.
/// </summary>
public sealed class VersionRange
{
    private readonly List<(PackageVersion? Min, PackageVersion? Max, bool MinExclusive, bool MaxExclusive)> _bounds = [];

    private VersionRange(string text) => Text = text;

    /// <summary>Any version.</summary>
    public static VersionRange Any { get; } = new("*");

    /// <summary>The range as it was written.</summary>
    public string Text { get; }

    public static VersionRange Parse(string text) =>
        TryParse(text, out VersionRange? range, out string? error) ? range! : throw new FormatException(error);

    public static bool TryParse(string text, out VersionRange? range, out string? error)
    {
        range = null;
        error = null;
        var result = new VersionRange(text.Trim().Length == 0 ? "*" : text.Trim());
        foreach (string token in text.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "*")
            {
                continue;
            }

            string op = token.StartsWith(">=", StringComparison.Ordinal) ? ">=" : token.StartsWith("<=", StringComparison.Ordinal) ? "<=" : token[0] is '>' or '<' or '=' or '^' or '~' ? token[..1] : "";
            if (!PackageVersion.TryParse(token[op.Length..], out PackageVersion version))
            {
                error = $"'{token}' is not a version comparator (>=1.2, <2, ^1.0, ~1.2.3, 1.4, *).";
                return false;
            }

            switch (op)
            {
                case ">=": result._bounds.Add((version, null, false, false)); break;
                case ">": result._bounds.Add((version, null, true, false)); break;
                case "<=": result._bounds.Add((null, version, false, false)); break;
                case "<": result._bounds.Add((null, version, false, true)); break;
                case "^":
                    PackageVersion caret = version.Major > 0 ? new(version.Major + 1, 0, 0) : version.Minor > 0 ? new(0, version.Minor + 1, 0) : new(0, 0, version.Patch + 1);
                    result._bounds.Add((version, caret, false, true));
                    break;
                case "~":
                    result._bounds.Add((version, new PackageVersion(version.Major, version.Minor + 1, 0), false, true));
                    break;
                default:
                    result._bounds.Add((version, version, false, false));
                    break;
            }
        }

        range = result;
        return true;
    }

    public bool Matches(PackageVersion version)
    {
        foreach ((PackageVersion? min, PackageVersion? max, bool minExclusive, bool maxExclusive) in _bounds)
        {
            if (min is { } low && (minExclusive ? version <= low : version < low))
            {
                return false;
            }

            if (max is { } high && (maxExclusive ? version >= high : version > high))
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString() => Text;
}
