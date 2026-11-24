using System;
using System.Collections.Generic;
using Weft.Content;
using Weft.Content.Xml;
using Weft.Ecs;
using Weft.Interop;
using Weft.Reflection;
using Sdl = Weft.Native.Sdl;

namespace Weft.Editor.Editing;

/// <summary>The text on the system clipboard. The editor uses <see cref="SdlClipboard"/>; tests use <see cref="MemoryClipboard"/>.</summary>
public interface IClipboard
{
    /// <summary>The text on the clipboard, or null when it holds none (or something that is not text).</summary>
    string? GetText();

    /// <summary>Replaces the clipboard's content with <paramref name="text"/>.</summary>
    void SetText(string text);
}

/// <summary>The operating system's clipboard, through SDL. It needs SDL's video system, which the editor's window has started.</summary>
public sealed unsafe class SdlClipboard : IClipboard
{
    public string? GetText()
    {
        byte* text = Sdl.SDL.GetClipboardText();
        if (text is null)
        {
            return null;
        }

        try
        {
            string result = Utf8.FromPointer(text) ?? "";
            return result.Length == 0 ? null : result;   // SDL answers an empty string for "no text"
        }
        finally
        {
            Sdl.SDL.Free(text);
        }
    }

    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        fixed (byte* bytes = Utf8.ToZeroTerminated(text))
        {
            _ = Sdl.SDL.SetClipboardText(bytes);
        }
    }
}

/// <summary>A clipboard that lives in memory: what tests use in place of the system's.</summary>
public sealed class MemoryClipboard : IClipboard
{
    /// <summary>The text on the clipboard; settable so a test can put anything there.</summary>
    public string? Text { get; set; }

    public string? GetText() => Text;

    public void SetText(string text) => Text = text;
}

/// <summary>
/// Entities as scene text on the clipboard: what Copy writes (the same XML a scene file holds, with no file identity) and what Paste checks before it reads. The text is untrusted, since anything can be
/// on the clipboard: it is read as an <see cref="XmlNode"/> (which refuses a DTD and deep nesting) and has to be a <c>Scene</c> of at most <see cref="MaxLength"/> characters.
/// </summary>
public static class ClipboardScene
{
    /// <summary>The longest text that is copied or pasted, 16 MB of characters: more than any hand-made selection, and a bound on what a stray clipboard can make the editor parse.</summary>
    public const int MaxLength = 16 * 1024 * 1024;

    /// <summary>
    /// The scene text of <paramref name="roots"/> and everything below them, as one document. An entity under another one of the list is not written twice. An entity field that points to something
    /// outside the copied entities is left out (the writer omits it); one that points inside is written as its number in the document, which is how a paste connects the copies. Asset fields are
    /// written as <c>path|uid</c>.
    /// </summary>
    public static string Copy(World world, IReadOnlyList<Entity> roots)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(roots);
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + world.SaveSceneDocument(Selection.TopLevelOf(world, roots)).ToXml();
    }

    /// <summary>
    /// A cheap check of whether <paramref name="text"/> can be a scene: not too long, and its first element is <c>Scene</c> (after a byte order mark, white space, the XML declaration and comments).
    /// Nothing is parsed, so a menu can ask it every frame; <see cref="TryRead"/> is the real test.
    /// </summary>
    public static bool LooksLikeScene(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            return false;
        }

        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\uFEFF' || char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            ReadOnlySpan<char> rest = text.AsSpan(i);
            if (rest.StartsWith("<?", StringComparison.Ordinal) || rest.StartsWith("<!--", StringComparison.Ordinal))
            {
                string end = rest[1] == '?' ? "?>" : "-->";
                int close = text.IndexOf(end, i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    return false;
                }

                i = close + end.Length;
                continue;
            }

            return rest.StartsWith("<Scene", StringComparison.Ordinal) && (rest.Length == 6 || rest[6] is ' ' or '>' or '/' or '\t' or '\r' or '\n');
        }

        return false;
    }

    /// <summary>
    /// Reads <paramref name="text"/> as a scene. False, with what is wrong in <paramref name="problem"/> (a sentence for the console), when there is no text, it is too long, it is not XML, or its
    /// root is not <c>Scene</c>.
    /// </summary>
    public static bool TryRead(string? text, out XmlNode scene, out string problem)
    {
        scene = null!;
        if (string.IsNullOrEmpty(text))
        {
            problem = "the clipboard has no text";
            return false;
        }

        if (text.Length > MaxLength)
        {
            problem = $"the clipboard text is longer than {MaxLength / (1024 * 1024)} MB";
            return false;
        }

        if (!LooksLikeScene(text))
        {
            problem = "the clipboard text is not a scene";
            return false;
        }

        try
        {
            scene = XmlNode.Parse(text.TrimStart('\uFEFF'), "clipboard");
        }
        catch (ContentException e)
        {
            problem = $"the clipboard text is not a scene: {e.Message}";
            return false;
        }

        problem = "";
        return true;
    }

    /// <summary>The entities a paste made that sit directly under <paramref name="under"/> (none: roots): the tops of the pasted trees, in the order they were made.</summary>
    public static List<Entity> RootsOf(World world, IReadOnlyList<Entity> made, Entity under)
    {
        var roots = new List<Entity>();
        foreach (Entity entity in made)
        {
            if (world.GetParent(entity) == under)
            {
                roots.Add(entity);
            }
        }

        return roots;
    }
}
