using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace Apogee.Build.Bindings;

/// <summary>
/// Evaluates the object-like macros that are plain constants: integer literals (with C suffixes), <c>FOO_C(…)</c> literal
/// wrappers, parentheses, unary and binary operators, and references to other such macros. Anything else is not a constant.
/// </summary>
public sealed class MacroEvaluator
{
    private readonly Dictionary<string, List<string>> _macros;
    private readonly Dictionary<string, BigInteger?> _cache = [];
    private readonly HashSet<string> _active = [];

    public MacroEvaluator(Dictionary<string, List<string>> macros) => _macros = macros;

    /// <summary>The value of the macro <paramref name="name"/> as an integer, or null when it is not an integer constant.</summary>
    public BigInteger? EvaluateInteger(string name)
    {
        if (_cache.TryGetValue(name, out BigInteger? cached))
        {
            return cached;
        }

        if (!_macros.TryGetValue(name, out List<string>? tokens) || tokens.Count == 0 || !_active.Add(name))
        {
            return null;
        }

        BigInteger? value = null;
        try
        {
            var parser = new Parser(this, tokens);
            value = parser.ParseAll();
        }
        catch (FormatException)
        {
        }
        finally
        {
            _active.Remove(name);
        }

        _cache[name] = value;
        return value;
    }

    /// <summary>The unescaped text of the macro when its body is one string literal, otherwise null.</summary>
    public string? EvaluateString(string name)
    {
        if (!_macros.TryGetValue(name, out List<string>? tokens) || tokens.Count != 1)
        {
            return null;
        }

        string token = tokens[0];
        if (token.Length < 2 || token[0] != '"' || token[^1] != '"')
        {
            return null;
        }

        return token[1..^1];
    }

    private sealed class Parser(MacroEvaluator owner, List<string> tokens)
    {
        private int _position;

        public BigInteger ParseAll()
        {
            BigInteger value = ParseBinary(0);
            if (_position != tokens.Count)
            {
                throw new FormatException("trailing tokens");
            }

            return value;
        }

        private static readonly string[][] s_levels =
        [
            ["|"], ["^"], ["&"], ["<<", ">>"], ["+", "-"], ["*", "/"],
        ];

        private BigInteger ParseBinary(int level)
        {
            if (level == s_levels.Length)
            {
                return ParseUnary();
            }

            BigInteger left = ParseBinary(level + 1);
            while (_position < tokens.Count && Array.IndexOf(s_levels[level], tokens[_position]) >= 0)
            {
                string op = tokens[_position++];
                BigInteger right = ParseBinary(level + 1);
                left = op switch
                {
                    "|" => left | right,
                    "^" => left ^ right,
                    "&" => left & right,
                    "<<" => left << (int)right,
                    ">>" => left >> (int)right,
                    "+" => left + right,
                    "-" => left - right,
                    "*" => left * right,
                    _ => right.IsZero ? throw new FormatException("division by zero") : left / right,
                };
            }

            return left;
        }

        private BigInteger ParseUnary()
        {
            string token = Next();
            switch (token)
            {
                case "-": return -ParseUnary();
                case "+": return ParseUnary();
                case "~": return ~ParseUnary();
                case "(":
                {
                    BigInteger inner = ParseBinary(0);
                    Expect(")");
                    return inner;
                }
            }

            if (char.IsDigit(token[0]))
            {
                return ParseInteger(token);
            }

            if (!(char.IsLetter(token[0]) || token[0] == '_'))
            {
                throw new FormatException($"unexpected {token}");
            }

            // FOO_UINT64_C(0x1): a literal wrapper, the argument is the value.
            if (_position < tokens.Count && tokens[_position] == "(" && token.EndsWith("_C", StringComparison.Ordinal))
            {
                _position++;
                BigInteger inner = ParseBinary(0);
                Expect(")");
                return inner;
            }

            return owner.EvaluateInteger(token) ?? throw new FormatException($"{token} is not a constant");
        }

        private static BigInteger ParseInteger(string token)
        {
            string digits = token.TrimEnd('u', 'U', 'l', 'L');
            if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return BigInteger.Parse("0" + digits[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            if (digits.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                BigInteger value = 0;
                foreach (char c in digits[2..])
                {
                    value = (value << 1) | (c == '1' ? 1 : 0);
                }

                return value;
            }

            if (digits.Length > 1 && digits[0] == '0')
            {
                BigInteger value = 0;
                foreach (char c in digits[1..])
                {
                    value = (value * 8) + (c - '0');
                }

                return value;
            }

            return BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        private string Next() =>
            _position < tokens.Count ? tokens[_position++] : throw new FormatException("unexpected end");

        private void Expect(string expected)
        {
            if (Next() != expected)
            {
                throw new FormatException($"expected {expected}");
            }
        }
    }
}
