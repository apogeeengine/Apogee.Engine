using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Apogee.Content.Xml;

public enum DefKind
{
    Text,
    Number,
    Boolean,
    List,
    Record,
    Vector,
}

/// <summary>
/// What a definition decodes into (<see cref="SchemaSet.Decode"/>): text, a number, a boolean, a list, or a record of named members, nested as deep as the XML was. A plain tree
/// that scripts, tools and C# read alike; <see cref="ToString"/> writes it compactly (<c>{id: "crate", weight: 12.5, tags: ["a", "b"]}</c>), which is what tests compare.
/// </summary>
public abstract class DefValue
{
    public abstract DefKind Kind { get; }

    public static DefValue From(string text) => new DefText(text);

    public static DefValue From(double number) => new DefNumber(number);

    public static DefValue From(bool value) => new DefBool(value);

    public override string ToString()
    {
        var text = new StringBuilder();
        Write(text);
        return text.ToString();
    }

    internal abstract void Write(StringBuilder text);
}

public sealed class DefText(string value) : DefValue
{
    public string Value { get; } = value;

    public override DefKind Kind => DefKind.Text;

    internal override void Write(StringBuilder text) => text.Append('"').Append(Value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
}

public sealed class DefNumber(double value) : DefValue
{
    public double Value { get; } = value;

    public override DefKind Kind => DefKind.Number;

    internal override void Write(StringBuilder text) => text.Append(Value.ToString("R", CultureInfo.InvariantCulture));
}

public sealed class DefBool(bool value) : DefValue
{
    public bool Value { get; } = value;

    public override DefKind Kind => DefKind.Boolean;

    internal override void Write(StringBuilder text) => text.Append(Value ? "true" : "false");
}

/// <summary>Three numbers written <c>"x y z"</c> (or <c>"x, y, z"</c>) in a <c>vector3</c> attribute: a <c>vector</c> in scripts.</summary>
public sealed class DefVector(double x, double y, double z) : DefValue
{
    public double X { get; } = x;

    public double Y { get; } = y;

    public double Z { get; } = z;

    public override DefKind Kind => DefKind.Vector;

    internal override void Write(StringBuilder text) =>
        text.Append("vec(").Append(X.ToString("R", CultureInfo.InvariantCulture)).Append(", ").Append(Y.ToString("R", CultureInfo.InvariantCulture)).Append(", ").Append(Z.ToString("R", CultureInfo.InvariantCulture)).Append(')');
}

public sealed class DefList(List<DefValue> items) : DefValue
{
    public IReadOnlyList<DefValue> Items { get; } = items;

    public override DefKind Kind => DefKind.List;

    internal override void Write(StringBuilder text)
    {
        text.Append('[');
        for (int i = 0; i < Items.Count; i++)
        {
            if (i > 0)
            {
                text.Append(", ");
            }

            Items[i].Write(text);
        }

        text.Append(']');
    }
}

/// <summary>A record: members by name, in the order they were decoded (attributes first, then child elements as the schema declares them).</summary>
public sealed class DefRecord : DefValue
{
    private readonly OrderedDictionary<string, DefValue> _members = [];

    public override DefKind Kind => DefKind.Record;

    public IReadOnlyList<KeyValuePair<string, DefValue>> Members => [.. _members];

    public int Count => _members.Count;

    public DefValue? this[string name] => _members.TryGetValue(name, out DefValue? value) ? value : null;

    public bool Has(string name) => _members.ContainsKey(name);

    public void Set(string name, DefValue value) => _members[name] = value;

    public bool Remove(string name) => _members.Remove(name);

    internal override void Write(StringBuilder text)
    {
        text.Append('{');
        bool first = true;
        foreach ((string name, DefValue value) in _members)
        {
            if (!first)
            {
                text.Append(", ");
            }

            first = false;
            text.Append(name).Append(": ");
            value.Write(text);
        }

        text.Append('}');
    }
}
