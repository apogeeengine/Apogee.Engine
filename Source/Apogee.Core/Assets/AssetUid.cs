using System;
using System.Security.Cryptography;

namespace Apogee.Core.Assets;

/// <summary>
/// The identity of an asset that survives a rename or a move: 64 random bits. <see cref="None"/> (zero) means "no UID" and is never
/// minted. The text form is <c>uid://</c> followed by 13 characters of lowercase Crockford base32 (<c>0-9a-hjkmnp-tv-z</c>), most
/// significant digit first; the first digit holds the remaining 4 bits, so it is at most <c>f</c>.
/// </summary>
/// <param name="Value">The 64-bit value; zero is <see cref="None"/>.</param>
public readonly record struct AssetUid(ulong Value)
{
    /// <summary>The text that starts every UID: <c>uid://</c>.</summary>
    public const string Scheme = "uid://";

    /// <summary>The number of base32 characters after the scheme.</summary>
    public const int DigitCount = 13;

    /// <summary>Crockford's base32 alphabet in lowercase: no <c>i</c>, <c>l</c>, <c>o</c> or <c>u</c>.</summary>
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    /// <summary>No UID (value zero).</summary>
    public static AssetUid None => default;

    /// <summary>Whether this is <see cref="None"/>.</summary>
    public bool IsNone => Value == 0;

    /// <summary>Mints a UID: 64 random bits from the system's cryptographic generator, never zero.</summary>
    public static AssetUid New()
    {
        Span<byte> bytes = stackalloc byte[8];
        while (true)
        {
            RandomNumberGenerator.Fill(bytes);
            ulong value = BitConverter.ToUInt64(bytes);
            if (value != 0)
            {
                return new AssetUid(value);
            }
        }
    }

    /// <summary>The text form, <c>uid://</c> and 13 base32 characters; <see cref="None"/> gives the same form with all zeros, which <see cref="TryParse"/> refuses.</summary>
    public override string ToString() =>
        string.Create(Scheme.Length + DigitCount, Value, static (span, value) =>
        {
            Scheme.AsSpan().CopyTo(span);
            for (int i = 0; i < DigitCount; i++)
            {
                span[Scheme.Length + i] = Alphabet[(int)((value >> (5 * (DigitCount - 1 - i))) & 31)];
            }
        });

    /// <summary>
    /// Reads the text form. Case does not matter. Fails on a missing scheme, a length other than 13 digits, a character outside the
    /// alphabet (<c>i</c>, <c>l</c>, <c>o</c> and <c>u</c> included), a first digit above <c>f</c> (it would not fit in 64 bits) and the value zero.
    /// </summary>
    /// <param name="text">The text, with no surrounding spaces.</param>
    /// <param name="uid">The UID, or <see cref="None"/> on failure.</param>
    /// <returns>Whether the text was a valid UID.</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out AssetUid uid)
    {
        uid = default;
        if (text.Length != Scheme.Length + DigitCount || !text[..Scheme.Length].Equals(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ulong value = 0;
        for (int i = 0; i < DigitCount; i++)
        {
            int digit = DigitOf(text[Scheme.Length + i]);
            if (digit < 0 || (i == 0 && digit > 15))
            {
                return false;
            }

            value = (value << 5) | (uint)digit;
        }

        if (value == 0)
        {
            return false;
        }

        uid = new AssetUid(value);
        return true;
    }

    /// <summary>Reads the text form (see <see cref="TryParse"/>).</summary>
    /// <exception cref="FormatException">The text is not a valid UID.</exception>
    public static AssetUid Parse(ReadOnlySpan<char> text) =>
        TryParse(text, out AssetUid uid)
            ? uid
            : throw new FormatException($"'{text}' is not an asset UID: expected {Scheme} and {DigitCount} characters of 0-9 a-h j k m n p-t v-z (not zero).");

    private static int DigitOf(char c)
    {
        if (c is >= 'A' and <= 'Z')
        {
            c = (char)(c + ('a' - 'A'));
        }

        return c is (>= '0' and <= '9') or (>= 'a' and <= 'z') ? Alphabet.IndexOf(c) : -1;
    }
}
