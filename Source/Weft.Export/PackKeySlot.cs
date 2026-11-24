using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Weft.Content.Packs;

namespace Weft.Export;

/// <summary>
/// A reserved place in the engine's executable for the key of the game's packs, so that an exported game opens its packs without the environment variable. The executable holds
/// <see cref="Template"/> (68 bytes in its read-only data: a 16 byte magic, a format byte, a "used" byte, 2 reserved bytes, a 16 byte salt, and 32 bytes for the key); an export finds every copy of the
/// template in the copy of the executable it makes, and writes the key into each, masked: <c>key XOR SHA-256(salt + magic)</c> (the same salt and bytes in all). The running game reads it back with <see cref="TryRead"/>.
/// <para>
/// There is normally one copy, but a compiler may emit the data twice (the NativeAOT compiler does in the arm64 macOS executable), and the game reads whichever copy its code refers to: so every copy is patched.
/// </para>
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

    /// <summary>More copies of the slot than this in one file is not a runtime of this engine (a compiler emits two or three at most): the file is refused, and no loop runs on what a hostile file holds.</summary>
    private const int MaxCopies = 16;

    /// <summary>
    /// The slot as the engine's executable holds it, empty: <c>magic(16) | format(1) | used(1) | reserved(2) | salt(16) | maskedKey(32)</c>. The magic is found only in the slot (and its copies), which
    /// is how an export finds them. The data is read-only: only an export changes it, in the copy of the file.
    /// </summary>
    internal static ReadOnlySpan<byte> Template =>
    [
        0xA7, 0x57, 0x45, 0x46, 0x54, 0x50, 0x41, 0x43, 0x4B, 0x4B, 0x45, 0x59, 0x7C, 0x62, 0x68, 0x58, // magic: 0xA7, "WEFTPACKKEY", 4 fixed random bytes
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

    /// <summary>
    /// Writes <paramref name="key"/> into every copy of the slot of the executable at <paramref name="executable"/>, the same masked bytes (and salt) in each: the running game reads whichever copy
    /// its code refers to.
    /// </summary>
    /// <returns>How many copies of the slot were written (at least one).</returns>
    /// <exception cref="ExportException">The file has no slot, or more than <see cref="MaxCopies"/> (so it is not this engine's executable), ends inside a slot, or cannot be written.</exception>
    public static int Write(string executable, PackKey key)
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

        int[] offsets = Find(file, executable);
        byte[] slot = Encode(key.Bytes, RandomNumberGenerator.GetBytes(16));
        try
        {
            using FileStream stream = new(executable, FileMode.Open, FileAccess.Write, FileShare.None);
            foreach (int offset in offsets)
            {
                stream.Position = offset;
                stream.Write(slot);
            }
        }
        catch (IOException e)
        {
            throw new ExportException($"{executable} could not be written: {e.Message}", e);
        }

        return offsets.Length;
    }

    /// <summary>Where each copy of the slot is in the file at <paramref name="executable"/>, and how long it is: for a platform step to check that what the export wrote lies where it expects.</summary>
    /// <exception cref="ExportException">The file has no slot, or too many, or ends inside one, or cannot be read.</exception>
    internal static (long Offset, int Length)[] Locate(string executable)
    {
        try
        {
            int[] offsets = Find(File.ReadAllBytes(executable), executable);
            var ranges = new (long Offset, int Length)[offsets.Length];
            for (int i = 0; i < offsets.Length; i++)
            {
                ranges[i] = (offsets[i], Length);
            }

            return ranges;
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

    /// <summary>
    /// The offset of every copy of the slot in <paramref name="file"/>: a place where the whole of <see cref="Template"/> is, empty or already written (so an export can be made again over a file), found by its
    /// magic. A magic that is not followed by the rest of a slot is some other data and is passed over; a magic with less than a slot after it is an error.
    /// </summary>
    private static int[] Find(byte[] file, string executable)
    {
        ReadOnlySpan<byte> magic = Template[..MagicLength];
        var offsets = new List<int>();
        long position = 0;
        while (position < file.Length)
        {
            int found = file.AsSpan((int)position).IndexOf(magic);
            if (found < 0)
            {
                break;
            }

            long at = position + found;
            if (at + Length > file.Length)
            {
                throw new ExportException($"{executable} ends inside its pack key slot.");
            }

            if (IsSlot(file.AsSpan((int)at, Length)))
            {
                if (offsets.Count == MaxCopies)
                {
                    throw new ExportException($"{executable} has more than {MaxCopies} pack key slots: it is not a Weft runtime of this version.");
                }

                offsets.Add((int)at);
                position = at + Length;
            }
            else
            {
                position = at + 1;
            }
        }

        if (offsets.Count == 0)
        {
            throw new ExportException($"{executable} has no pack key slot: it is not a Weft runtime of this version.");
        }

        return [.. offsets];
    }

    /// <summary>Whether <paramref name="bytes"/> (a slot's length) is the empty template, or the template with a key written into it: the magic, the format, a "used" byte of 0 or 1, and the reserved bytes 0 (an empty slot has an empty salt and key too).</summary>
    private static bool IsSlot(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> template = Template;
        if (!bytes[..UsedOffset].SequenceEqual(template[..UsedOffset]) || bytes[UsedOffset] > 1 || bytes[UsedOffset + 1] != 0 || bytes[UsedOffset + 2] != 0)
        {
            return false;
        }

        return bytes[UsedOffset] == 1 || bytes[SaltOffset..].SequenceEqual(template[SaltOffset..]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe byte[] Copy(byte* source, int length) => new ReadOnlySpan<byte>(source, length).ToArray();
}
