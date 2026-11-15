using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Apogee.Content.Packs;

namespace Apogee.Export;

/// <summary>
/// A reserved place in the engine's executable for the key of the game's packs, so that an exported game opens its packs without the environment variable. The executable holds
/// <see cref="Template"/> (68 bytes in its read-only data: a 16 byte magic, a format byte, a "used" byte, 2 reserved bytes, a 16 byte salt, and 32 bytes for the key); an export finds the magic in the
/// copy of the executable it makes, and writes the key there masked: <c>key XOR SHA-256(salt + magic)</c>. The running game reads it back with <see cref="TryRead"/>.
/// <para>
/// This is obfuscation, not protection. The key is in the file, one XOR from anyone who reads this code or finds the magic; and a running game has the key in memory. It keeps the key out of
/// <c>strings</c>, out of a quick look in a hex editor, and out of the packs' reach, nothing more. It stops nobody who wants the game's files.
/// </para>
/// </summary>
public static class PackKeySlot
{
    private const int MagicLength = 16;
    private const int UsedOffset = MagicLength + 1;
    private const int SaltOffset = MagicLength + 4;
    private const int KeyOffset = SaltOffset + 16;
    private const int Length = KeyOffset + 32;
    private const byte Format = 1;

    /// <summary>
    /// The slot as the engine's executable holds it, empty: <c>magic(16) | format(1) | used(1) | reserved(2) | salt(16) | maskedKey(32)</c>. The magic is the only copy of it in the executable, which
    /// is how an export finds it (and why it exists exactly once). The data is read-only: only an export changes it, in the copy of the file.
    /// </summary>
    internal static ReadOnlySpan<byte> Template =>
    [
        0xA7, 0x41, 0x50, 0x4F, 0x47, 0x45, 0x45, 0x4B, 0x45, 0x59, 0x53, 0x4C, 0x4F, 0x54, 0xD2, 0x6B, // magic
        Format,
        0, // used: 0 no key, 1 a key
        0, 0, // reserved
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // salt
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // masked key
    ];

    /// <summary>The key an export wrote into this executable, or null when it has none (the program was not exported with a key: a development run, or an export without one).</summary>
    /// <exception cref="InvalidDataException">The slot holds a key in a format this version does not know.</exception>
    public static unsafe PackKey? TryRead()
    {
        // The JIT and the AOT compiler know what a constant index into this read-only data holds, and would answer from the empty template. A pointer through a method that is not inlined is opaque to them.
        ReadOnlySpan<byte> template = Template;
        fixed (byte* start = template)
        {
            return Decode(Copy(start, template.Length));
        }
    }

    /// <summary>Writes <paramref name="key"/> into the slot of the executable at <paramref name="executable"/>.</summary>
    /// <exception cref="ExportException">The file has no slot, or more than one (so it is not this engine's executable), or cannot be written.</exception>
    public static void Write(string executable, PackKey key)
    {
        byte[] file;
        try
        {
            file = File.ReadAllBytes(executable);
        }
        catch (IOException e)
        {
            throw new ExportException($"{executable} could not be read: {e.Message}", e);
        }

        ReadOnlySpan<byte> magic = Template[..MagicLength];
        int offset = Find(file, magic, executable);
        byte[] slot = Encode(key.Bytes, RandomNumberGenerator.GetBytes(16));
        try
        {
            using FileStream stream = new(executable, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.Position = offset;
            stream.Write(slot);
        }
        catch (IOException e)
        {
            throw new ExportException($"{executable} could not be written: {e.Message}", e);
        }
    }

    /// <summary>Where the slot is in the file at <paramref name="executable"/>, and how long it is: for a platform step to check that what the export wrote lies where it expects.</summary>
    /// <exception cref="ExportException">The file has no slot, or more than one, or cannot be read.</exception>
    internal static (long Offset, int Length) Locate(string executable)
    {
        try
        {
            return (Find(File.ReadAllBytes(executable), Template[..MagicLength], executable), Length);
        }
        catch (IOException e)
        {
            throw new ExportException($"{executable} could not be read: {e.Message}", e);
        }
    }

    /// <summary>The slot that holds <paramref name="key"/>, masked with <paramref name="salt"/>.</summary>
    internal static byte[] Encode(ReadOnlySpan<byte> key, ReadOnlySpan<byte> salt)
    {
        byte[] slot = Template.ToArray();
        slot[UsedOffset] = 1;
        salt.CopyTo(slot.AsSpan(SaltOffset, 16));
        byte[] mask = Mask(salt, slot.AsSpan(0, MagicLength));
        for (int i = 0; i < 32; i++)
        {
            slot[KeyOffset + i] = (byte)(key[i] ^ mask[i]);
        }

        return slot;
    }

    /// <summary>The key a slot holds (as <see cref="TryRead"/> reads it from the executable's own data); null when the slot is empty.</summary>
    internal static PackKey? Decode(ReadOnlySpan<byte> slot)
    {
        if (slot.Length != Length)
        {
            throw new InvalidDataException($"The pack key slot is {slot.Length} bytes, not {Length}.");
        }

        if (slot[UsedOffset] == 0)
        {
            return null;
        }

        if (slot[MagicLength] != Format)
        {
            throw new InvalidDataException($"The pack key slot has format {slot[MagicLength]}, which this version of the engine does not know.");
        }

        byte[] mask = Mask(slot.Slice(SaltOffset, 16), slot[..MagicLength]);
        byte[] key = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            key[i] = (byte)(slot[KeyOffset + i] ^ mask[i]);
        }

        return new PackKey(key);
    }

    private static byte[] Mask(ReadOnlySpan<byte> salt, ReadOnlySpan<byte> magic)
    {
        byte[] input = new byte[salt.Length + magic.Length];
        salt.CopyTo(input);
        magic.CopyTo(input.AsSpan(salt.Length));
        return SHA256.HashData(input);
    }

    private static int Find(byte[] file, ReadOnlySpan<byte> magic, string executable)
    {
        int first = file.AsSpan().IndexOf(magic);
        if (first < 0)
        {
            throw new ExportException($"{executable} has no pack key slot: it is not an Apogee runtime of this version.");
        }

        int next = file.AsSpan(first + 1).IndexOf(magic);
        if (next >= 0)
        {
            throw new ExportException($"{executable} has more than one pack key slot: it is not an Apogee runtime of this version.");
        }

        if (first + Length > file.Length)
        {
            throw new ExportException($"{executable} ends inside its pack key slot.");
        }

        return first;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe byte[] Copy(byte* source, int length) => new ReadOnlySpan<byte>(source, length).ToArray();
}
