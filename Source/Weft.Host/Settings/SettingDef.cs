using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Weft.Reflection;

namespace Weft.Host;

/// <summary>What a setting holds.</summary>
public enum SettingType
{
    /// <summary><c>true</c> or <c>false</c>.</summary>
    Bool,

    /// <summary>A whole number, between <see cref="SettingDef.Min"/> and <see cref="SettingDef.Max"/>.</summary>
    Whole,

    /// <summary>A number, between <see cref="SettingDef.Min"/> and <see cref="SettingDef.Max"/>.</summary>
    Number,

    /// <summary>Text, without spaces at its ends.</summary>
    Text,

    /// <summary>One of <see cref="SettingDef.Choices"/>.</summary>
    Choice,
}

/// <summary>
/// The schema of one setting: its key (dotted lower-case words, such as <c>audio.volume</c> or <c>mod.coolmod.show_hints</c>), its type, its default, and for a number its range or for a choice its
/// choices. The values themselves live in a <see cref="SettingsStore"/>.
/// </summary>
/// <param name="Key">Two or more words of <c>a-z</c>, <c>0-9</c> and <c>_</c>, joined by dots.</param>
/// <param name="Type">What the setting holds.</param>
/// <param name="Default">The value a player who never changed it has: a boolean, an integer, a float or a string, as <paramref name="Type"/> says.</param>
/// <param name="Min">The lowest a number may be (a smaller one is raised to it).</param>
/// <param name="Max">The highest a number may be.</param>
/// <param name="Choices">For <see cref="SettingType.Choice"/>: the values it may take.</param>
/// <param name="Restart">Changing it takes effect at the next start of the game, not at once (<see cref="SettingsStore.NeedsRestart"/>).</param>
/// <param name="Help">One line for the options menu, or null.</param>
/// <param name="Owner">Who defined it: the id of a script's owner (<c>base</c>, a mod's id), or null for the engine's own.</param>
public sealed record SettingDef(
    string Key,
    SettingType Type,
    Value Default,
    double Min = double.NegativeInfinity,
    double Max = double.PositiveInfinity,
    IReadOnlyList<string>? Choices = null,
    bool Restart = false,
    string? Help = null,
    string? Owner = null)
{
    private static readonly Regex KeyPattern = new(@"^[a-z0-9_]+(\.[a-z0-9_]+)+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Whether <paramref name="key"/> is a valid key.</summary>
    public static bool IsValidKey(string key) => KeyPattern.IsMatch(key);

    /// <summary>A boolean setting.</summary>
    public static SettingDef OfBool(string key, bool defaultValue, bool restart = false, string? help = null, string? owner = null) =>
        new(key, SettingType.Bool, Value.Bool(defaultValue), Restart: restart, Help: help, Owner: owner);

    /// <summary>A whole-number setting.</summary>
    public static SettingDef OfInt(string key, long defaultValue, double min = double.NegativeInfinity, double max = double.PositiveInfinity, bool restart = false, string? help = null, string? owner = null) =>
        new(key, SettingType.Whole, Value.Int(defaultValue), min, max, Restart: restart, Help: help, Owner: owner);

    /// <summary>A number setting.</summary>
    public static SettingDef OfNumber(string key, double defaultValue, double min = double.NegativeInfinity, double max = double.PositiveInfinity, bool restart = false, string? help = null, string? owner = null) =>
        new(key, SettingType.Number, Value.Float(defaultValue), min, max, Restart: restart, Help: help, Owner: owner);

    /// <summary>A text setting.</summary>
    public static SettingDef OfString(string key, string defaultValue, bool restart = false, string? help = null, string? owner = null) =>
        new(key, SettingType.Text, Value.String(defaultValue), Restart: restart, Help: help, Owner: owner);

    /// <summary>A setting that is one of a few words.</summary>
    public static SettingDef OfChoice(string key, string defaultValue, IReadOnlyList<string> choices, bool restart = false, string? help = null, string? owner = null) =>
        new(key, SettingType.Choice, Value.String(defaultValue), Choices: choices, Restart: restart, Help: help, Owner: owner);

    /// <summary>The name scripts use for a type: <c>bool</c>, <c>int</c>, <c>number</c>, <c>string</c>, <c>choice</c>.</summary>
    public static string TypeName(SettingType type) => type switch
    {
        SettingType.Bool => "bool",
        SettingType.Whole => "int",
        SettingType.Number => "number",
        SettingType.Text => "string",
        _ => "choice",
    };

    /// <summary>The type a script's name stands for.</summary>
    public static bool TryParseType(string name, out SettingType type)
    {
        (bool found, type) = name switch
        {
            "bool" => (true, SettingType.Bool),
            "int" => (true, SettingType.Whole),
            "number" => (true, SettingType.Number),
            "string" => (true, SettingType.Text),
            "choice" => (true, SettingType.Choice),
            _ => (false, default),
        };
        return found;
    }

    /// <summary>
    /// Checks the definition and returns it with its default made canonical (an integer setting holds integers, a number setting floats, a string is trimmed).
    /// </summary>
    /// <exception cref="ValueException">The key, the default, the range or the choices are wrong.</exception>
    internal SettingDef Validated()
    {
        if (!IsValidKey(Key))
        {
            throw new ValueException($"'{Key}' is not a setting key: use two or more words of a-z, 0-9 and _ joined by dots (audio.volume)");
        }

        if (double.IsNaN(Min) || double.IsNaN(Max) || Min > Max)
        {
            throw new ValueException($"setting '{Key}': min {Format(Min)} is above max {Format(Max)}");
        }

        if (Type == SettingType.Choice)
        {
            if (Choices is not { Count: > 0 } || Choices.Any(string.IsNullOrEmpty) || Choices.Distinct(StringComparer.Ordinal).Count() != Choices.Count)
            {
                throw new ValueException($"setting '{Key}': a choice needs a list of different, non-empty choices");
            }
        }
        else if (Choices is not null)
        {
            throw new ValueException($"setting '{Key}': only a choice has choices");
        }

        if (Type is not (SettingType.Whole or SettingType.Number) && (!double.IsNegativeInfinity(Min) || !double.IsPositiveInfinity(Max)))
        {
            throw new ValueException($"setting '{Key}': only an int or a number has a min and a max");
        }

        Value normalized = Coerce(Default, clamp: false);
        return this with { Default = normalized, Choices = Choices is null ? null : [.. Choices] };
    }

    /// <summary>
    /// The value as this setting stores it: checked against the type, and for a number against the range (clamped when <paramref name="clamp"/>, else an error).
    /// </summary>
    /// <exception cref="ValueException">The value is of another type, not an integer, not a choice, or (when not clamping) out of range.</exception>
    internal Value Coerce(Value value, bool clamp = true)
    {
        switch (Type)
        {
            case SettingType.Bool:
                return value.Kind == ValueKind.Bool ? value : throw Wrong(value, "a boolean");
            case SettingType.Whole:
                if (value.Kind is not (ValueKind.Int or ValueKind.Float))
                {
                    throw Wrong(value, "an integer");
                }

                return Value.Int((long)Limit(value.ToInt64(), clamp));
            case SettingType.Number:
                if (value.Kind is not (ValueKind.Int or ValueKind.Float) || double.IsNaN(value.ToDouble()) || double.IsInfinity(value.ToDouble()))
                {
                    throw Wrong(value, "a finite number");
                }

                return Value.Float(Limit(value.ToDouble(), clamp));
            case SettingType.Text:
                return value.Kind == ValueKind.String ? Value.String(value.ToStringValue().Trim()) : throw Wrong(value, "a string");
            default:
                string text = value.Kind == ValueKind.String ? value.ToStringValue() : throw Wrong(value, "a string");
                return Choices!.Contains(text, StringComparer.Ordinal) ? value : throw new ValueException($"setting '{Key}': '{text}' is not one of {string.Join(", ", Choices!)}");
        }
    }

    private double Limit(double number, bool clamp)
    {
        if (number >= Min && number <= Max)
        {
            return number;
        }

        return clamp ? Math.Clamp(number, Min, Max) : throw new ValueException($"setting '{Key}': {Format(number)} is outside {Format(Min)} to {Format(Max)}");
    }

    private ValueException Wrong(Value value, string expected) => new($"setting '{Key}' is {TypeName(Type)}: expected {expected}, got {value}");

    /// <summary>Reads the text of a value in the file. False when it does not read as this type (a number out of range is clamped, as <see cref="Coerce"/> does).</summary>
    public bool TryParse(string text, out Value value)
    {
        value = default;
        switch (Type)
        {
            case SettingType.Bool:
                if (text.Equals("true", StringComparison.Ordinal))
                {
                    value = Value.Bool(true);
                    return true;
                }

                if (text.Equals("false", StringComparison.Ordinal))
                {
                    value = Value.Bool(false);
                    return true;
                }

                return false;
            case SettingType.Whole:
                if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long whole))
                {
                    return false;
                }

                value = Value.Int((long)Limit(whole, clamp: true));
                return true;
            case SettingType.Number:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
                {
                    return false;
                }

                value = Value.Float(Limit(number, clamp: true));
                return true;
            case SettingType.Text:
                value = Value.String(text.Trim());
                return true;
            default:
                if (!Choices!.Contains(text, StringComparer.Ordinal))
                {
                    return false;
                }

                value = Value.String(text);
                return true;
        }
    }

    /// <summary>The text of a value as the file holds it, in the invariant culture.</summary>
    public static string Format(Value value) => value.Kind switch
    {
        ValueKind.Bool => value.ToBool() ? "true" : "false",
        ValueKind.Int => value.ToInt64().ToString(CultureInfo.InvariantCulture),
        ValueKind.Float => Format(value.ToDouble()),
        _ => value.ToStringValue(),
    };

    private static string Format(double number) => number.ToString("R", CultureInfo.InvariantCulture);
}
