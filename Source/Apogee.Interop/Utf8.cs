using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Apogee.Interop;

/// <summary>UTF-8 strings at the native boundary: C strings in, C strings out.</summary>
public static unsafe class Utf8
{
    /// <summary>The string a zero-terminated C string holds, or null for a null pointer.</summary>
    public static string? FromPointer(byte* text) => text is null ? null : Marshal.PtrToStringUTF8((IntPtr)text);

    /// <summary><paramref name="text"/> as zero-terminated UTF-8 bytes; pin the array (<c>fixed</c>) to pass it to C.</summary>
    public static byte[] ToZeroTerminated(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }
}
