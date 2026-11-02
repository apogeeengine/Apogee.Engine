using System;
using System.Collections.Generic;

namespace Apogee.Reflection;

/// <param name="Name">The script name: snake_case.</param>
public sealed record EnumMemberInfo(string Name, long Value);

/// <summary>An enum scripts can use: its members are strings there (<c>"no_title_bar"</c>), a flags enum is a list of them.</summary>
public sealed class EnumInfo
{
    private readonly Dictionary<string, long> _byName = new(StringComparer.Ordinal);

    public EnumInfo(string name, bool isFlags, IReadOnlyList<EnumMemberInfo> members, string? doc = null)
    {
        Name = name;
        IsFlags = isFlags;
        Members = members;
        Doc = doc;
        foreach (EnumMemberInfo member in members)
        {
            _byName.TryAdd(member.Name, member.Value);
        }
    }

    public string Name { get; }

    public bool IsFlags { get; }

    public IReadOnlyList<EnumMemberInfo> Members { get; }

    public string? Doc { get; }

    public bool TryGetValue(string name, out long value) => _byName.TryGetValue(name, out value);

    /// <summary>The name of a member, or the number when the value is none (for a flags enum: the names joined with <c>|</c>).</summary>
    public string GetName(long value)
    {
        foreach (EnumMemberInfo member in Members)
        {
            if (member.Value == value)
            {
                return member.Name;
            }
        }

        if (IsFlags && value != 0)
        {
            var names = new List<string>();
            long rest = value;
            foreach (EnumMemberInfo member in Members)
            {
                if (member.Value != 0 && (rest & member.Value) == member.Value)
                {
                    names.Add(member.Name);
                    rest &= ~member.Value;
                }
            }

            if (rest == 0)
            {
                return string.Join('|', names);
            }
        }

        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
