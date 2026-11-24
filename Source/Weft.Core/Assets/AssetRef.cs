using System;

namespace Weft.Core.Assets;

/// <summary>
/// A reference to an asset by its path and its <see cref="AssetUid"/>. The path is what people read and is tried first; when the
/// file is no longer there, the UID finds it where it was moved. Either part may be missing. The path is kept exactly as given:
/// this type does not normalise it (the asset database does).
/// <para>
/// The text form is <c>res://models/car.glb|uid://4k2m9x7q1w3ze</c>; a path alone and a UID alone (<c>uid://4k2m9x7q1w3ze</c>) are
/// also valid, and the empty text is the empty reference. <c>|</c> cannot be part of a path.
/// </para>
/// </summary>
/// <param name="Path">The path, such as <c>res://models/car.glb</c> or <c>mod://id/a.png</c>; empty (or null in <c>default</c>) when there is none.</param>
/// <param name="Uid">The UID, or <see cref="AssetUid.None"/>.</param>
public readonly record struct AssetRef(string Path, AssetUid Uid)
{
    /// <summary>The character between the path and the UID in the text form.</summary>
    public const char Separator = '|';

    /// <summary>Whether there is neither a path nor a UID.</summary>
    public bool IsEmpty => !HasPath && Uid.IsNone;

    /// <summary>Whether there is a path.</summary>
    public bool HasPath => !string.IsNullOrEmpty(Path);

    /// <summary>A reference by path alone.</summary>
    /// <exception cref="ArgumentException">The path is empty, contains <c>|</c> or starts with <c>uid://</c> (it would read back as a UID).</exception>
    public static AssetRef FromPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path.Contains(Separator))
        {
            throw new ArgumentException($"A path cannot contain '{Separator}': '{path}'.", nameof(path));
        }

        if (path.StartsWith(AssetUid.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"A path cannot start with '{AssetUid.Scheme}': '{path}'.", nameof(path));
        }

        return new AssetRef(path, AssetUid.None);
    }

    /// <summary>A reference by UID alone.</summary>
    public static AssetRef FromUid(AssetUid uid) => new(string.Empty, uid);

    /// <summary>The text form: the path, <c>path|uid://…</c>, <c>uid://…</c> alone, or an empty string for the empty reference.</summary>
    public override string ToString()
    {
        if (!HasPath)
        {
            return Uid.IsNone ? string.Empty : Uid.ToString();
        }

        return Uid.IsNone ? Path : string.Concat(Path, "|", Uid.ToString());
    }

    /// <summary>
    /// Reads the text form. Fails on a <c>|</c> with an empty path or an empty or invalid UID after it, on a second <c>|</c>, and on
    /// text that starts with <c>uid://</c> but is not a valid UID. The empty text is the empty reference.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="value">The reference, or the empty reference on failure.</param>
    /// <returns>Whether the text was valid.</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out AssetRef value)
    {
        value = default;
        if (text.IsEmpty)
        {
            return true;
        }

        int bar = text.IndexOf(Separator);
        if (bar < 0)
        {
            if (text.StartsWith(AssetUid.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                if (!AssetUid.TryParse(text, out AssetUid only))
                {
                    return false;
                }

                value = FromUid(only);
                return true;
            }

            value = new AssetRef(text.ToString(), AssetUid.None);
            return true;
        }

        ReadOnlySpan<char> path = text[..bar];
        if (path.IsEmpty || path.StartsWith(AssetUid.Scheme, StringComparison.OrdinalIgnoreCase) || !AssetUid.TryParse(text[(bar + 1)..], out AssetUid uid))
        {
            return false;
        }

        value = new AssetRef(path.ToString(), uid);
        return true;
    }

    /// <summary>Reads the text form (see <see cref="TryParse"/>).</summary>
    /// <exception cref="FormatException">The text is not a valid asset reference.</exception>
    public static AssetRef Parse(ReadOnlySpan<char> text) =>
        TryParse(text, out AssetRef value)
            ? value
            : throw new FormatException($"'{text}' is not an asset reference: expected a path, a path|uid://… or a uid://… (a path cannot be empty or contain '|').");
}
