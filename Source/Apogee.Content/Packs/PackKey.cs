using System;
using System.Globalization;

namespace Apogee.Content.Packs;

/// <summary>
/// The secret that encrypts a game's packs: 32 bytes, written as 64 hexadecimal digits. The build tool and the editor read it from the environment (<c>APOGEE_PACK_KEY</c>, which the
/// <c>.env</c> file of the repository sets), a shipped game has it baked in. Without a key packs are not encrypted. It is never written to a pack, a log, or an error message.
/// </summary>
public sealed class PackKey
{
    /// <summary>The environment variable the tools read the key from.</summary>
    public const string EnvironmentVariable = "APOGEE_PACK_KEY";

    private readonly byte[] _bytes;

    public PackKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32)
        {
            throw new ArgumentException("A pack key is 32 bytes.", nameof(bytes));
        }

        _bytes = bytes.ToArray();
    }

    internal ReadOnlySpan<byte> Bytes => _bytes;

    /// <exception cref="FormatException">The text is not 64 hexadecimal digits.</exception>
    public static PackKey Parse(string text)
    {
        string hex = text.Trim();
        if (hex.Length != 64)
        {
            throw new FormatException("A pack key is 64 hexadecimal digits (32 bytes).");
        }

        byte[] bytes = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[i]))
            {
                throw new FormatException("A pack key is 64 hexadecimal digits (32 bytes).");
            }
        }

        return new PackKey(bytes);
    }

    /// <summary>The key in <c>APOGEE_PACK_KEY</c>, or null when it is not set (packs are then not encrypted).</summary>
    /// <exception cref="FormatException">The variable is set to something that is not a key.</exception>
    public static PackKey? FromEnvironment(string variable = EnvironmentVariable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } text ? Parse(text) : null;

    public override string ToString() => "PackKey(…)";
}
