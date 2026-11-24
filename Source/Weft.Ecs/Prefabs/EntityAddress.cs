using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Weft.Ecs;

// The address of an entity inside what a prefab expands to (docs/manual/content/prefabs.md): entity ids separated by '/'.
//
//   4        entity 4 of this document (a scene, or the prefab being expanded)
//   4/2      entity 2 of the prefab that was instanced as entity 4
//   4/3/2    entity 2 of the prefab instanced as entity 3 of the prefab instanced as entity 4
//
// Ids repeat from one prefab to the next, so only the path is unique. Every segment is a whole number from 1 to 2^31-1 written without a leading zero (so a given address has exactly one spelling and
// equal text means equal address), and there are at most MaxSegments of them: one for the document and one for each level a prefab can nest.
internal static class EntityAddress
{
    /// <summary>The most segments an address has: the scene's entity and every level of nesting (<see cref="PrefabExpander.MaxDepth"/>).</summary>
    public const int MaxSegments = PrefabExpander.MaxDepth + 1;

    /// <summary>Whether <paramref name="text"/> is a well-formed address of at most <see cref="MaxSegments"/> segments.</summary>
    public static bool IsValid(string? text) => TryParse(text, out _);

    /// <summary>
    /// Reads an address. False for the empty text, a segment that is not a whole number from 1 up (a sign, a leading zero, a space, a number above 2^31-1, nothing between two slashes) and for more than
    /// <see cref="MaxSegments"/> segments; <paramref name="segments"/> is then empty.
    /// </summary>
    public static bool TryParse(string? text, out int[] segments)
    {
        segments = [];
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var found = new List<int>();
        int start = 0;
        while (true)
        {
            int slash = text.IndexOf('/', start);
            ReadOnlySpan<char> part = slash < 0 ? text.AsSpan(start) : text.AsSpan(start, slash - start);
            if (found.Count == MaxSegments || !TryParseSegment(part, out int id))
            {
                return false;
            }

            found.Add(id);
            if (slash < 0)
            {
                break;
            }

            start = slash + 1;
        }

        segments = [.. found];
        return true;
    }

    /// <summary>The address <paramref name="scope"/> followed by <paramref name="id"/>; just the id when the scope is empty (the entities of a scene).</summary>
    public static string Combine(string scope, int id)
    {
        string text = id.ToString(CultureInfo.InvariantCulture);
        return scope.Length == 0 ? text : $"{scope}/{text}";
    }

    /// <summary>The segments as an address.</summary>
    public static string Format(ReadOnlySpan<int> segments)
    {
        var text = new StringBuilder();
        foreach (int id in segments)
        {
            if (text.Length > 0)
            {
                text.Append('/');
            }

            text.Append(id.ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>
    /// Orders two addresses the way a writer lists them: segment by segment as numbers (so <c>2/10</c> comes after <c>2/9</c>), an address before the ones below it. The empty text (no address) comes first.
    /// Text that is not an address compares as the empty text.
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        int[] left = TryParse(a, out int[] first) ? first : [];
        int[] right = TryParse(b, out int[] second) ? second : [];
        for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            int order = left[i].CompareTo(right[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static bool TryParseSegment(ReadOnlySpan<char> part, out int id)
    {
        id = 0;
        if (part.Length == 0 || part[0] == '0')
        {
            return false;
        }

        return int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id >= 1;
    }
}
