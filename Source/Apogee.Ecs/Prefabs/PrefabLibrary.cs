using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Apogee.Content;
using Apogee.Content.Assets;
using Apogee.Content.FileSystem;
using Apogee.Content.Xml;
using Apogee.Core.Assets;

namespace Apogee.Ecs;

/// <summary>
/// The prefab files a session knows, as numbers: one handle per file (the same file asked for by its path, its <c>uid://</c> or both is the same handle), and each file's document, read once.
/// A handle is what <see cref="PrefabInstance"/> and <see cref="PrefabMember"/> hold; it only means something in the run that made it, which is why snapshots and scenes name the file instead
/// (<see cref="RefOf"/>). A reference that names no file still gets a handle that keeps its text, so an instance of a prefab that is missing keeps what it was made from. Safe to use from several threads.
/// </summary>
/// <param name="files">Where prefab files are read from and written to.</param>
/// <param name="assets">Finds the file a <c>uid://</c> names (and a prefab that moved); null: only paths are understood.</param>
public sealed class PrefabLibrary(VirtualFileSystem files, AssetDatabase? assets)
{
    private sealed class Entry(AssetRef named)
    {
        public AssetRef Named { get; } = named;

        public string? Path { get; set; }

        public XmlNode? Document { get; set; }

        public bool Checked { get; set; }
    }

    private readonly Lock _lock = new();
    private readonly List<Entry> _entries = [];                                     // handle - 1
    private readonly Dictionary<string, uint> _byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _byText = new(StringComparer.Ordinal); // references that named no file when they were asked for

    /// <summary>Raised after the file of a prefab was written by this library's world (see <see cref="World.SavePrefabFile"/>), with its handle: what an instance of it was made from has changed.</summary>
    public event Action<uint>? Changed;

    /// <summary>
    /// The handle of the prefab that <paramref name="pathOrRef"/> names: a path (<c>res://prefabs/lamp.aprefab</c>), a <c>uid://…</c>, or <c>path|uid://…</c>. The same file always gives the same
    /// handle. A reference that names no file gives a handle too (its <see cref="Document"/> is null), so that it can be saved and asked for again; the file is looked for again each time it is asked.
    /// </summary>
    /// <exception cref="FormatException">The text is not an asset reference.</exception>
    public uint Load(string pathOrRef)
    {
        ArgumentNullException.ThrowIfNull(pathOrRef);
        return Load(AssetRef.Parse(pathOrRef));
    }

    /// <summary>The handle of the prefab that <paramref name="reference"/> names, or 0 for the empty reference. See <see cref="Load(string)"/>.</summary>
    public uint Load(AssetRef reference)
    {
        if (reference.IsEmpty)
        {
            return 0;
        }

        lock (_lock)
        {
            string text = reference.ToString();
            string? path = Find(reference);
            if (path is not null)
            {
                if (_byPath.TryGetValue(path, out uint known))
                {
                    return known;
                }

                // The file is there now: the handle made for the text that did not name one becomes this file's.
                if (_byText.Remove(text, out uint waiting))
                {
                    _entries[(int)waiting - 1].Path = path;
                    _byPath[path] = waiting;
                    return waiting;
                }

                return Add(reference, path);
            }

            return _byText.TryGetValue(text, out uint pending) ? pending : AddPending(reference, text);
        }
    }

    /// <summary>The reference a handle names: the file's path and UID, or, when no file has been found for it, the reference it was asked for. Null for a handle this library did not give.</summary>
    public AssetRef? RefOf(uint handle)
    {
        lock (_lock)
        {
            if (Get(handle) is not { } entry)
            {
                return null;
            }

            string? path = Resolved(entry, handle);
            return path is null ? entry.Named : assets?.RefOf(path) ?? AssetRef.FromPath(path);
        }
    }

    /// <summary>The path of the file a handle names, or null when there is none (the reference names no file that exists) or the handle is not one of this library's.</summary>
    public string? PathOf(uint handle)
    {
        lock (_lock)
        {
            return Get(handle) is { } entry ? Resolved(entry, handle) : null;
        }
    }

    /// <summary>
    /// The document of the prefab, read once and kept: the root <c>Prefab</c> element. Null when the handle is not one of this library's or names no file.
    /// </summary>
    /// <exception cref="FormatException">The file exists but cannot be read as an XML document (text or cooked).</exception>
    public XmlNode? Document(uint handle)
    {
        lock (_lock)
        {
            if (Get(handle) is not { } entry || Resolved(entry, handle) is not { } path)
            {
                return null;
            }

            return entry.Document ??= Read(path);
        }
    }

    // The document, or the reason there is none, as the exception a script sees from world:instantiate.
    internal XmlNode Require(uint handle)
    {
        lock (_lock)
        {
            if (Get(handle) is not { } entry)
            {
                throw new ArgumentException($"{handle} is not a prefab handle of this library", nameof(handle));
            }

            if (Resolved(entry, handle) is not { } path)
            {
                AssetRef named = entry.Named;
                if (named.HasPath)
                {
                    throw new FileNotFoundException($"there is no prefab at {named.Path}" + (named.Uid.IsNone ? "" : $" and no file has {named.Uid}"), named.Path);
                }

                throw new ContentException(assets is null
                    ? $"{named.Uid} cannot be looked up: there is no asset database, only paths are understood"
                    : $"Unknown uid {named.Uid}: no prefab has it (the file may have been deleted, or never imported).");
            }

            XmlNode document = entry.Document ??= Read(path);
            if (!entry.Checked)
            {
                PrefabDocument.Check(document, path);
                entry.Checked = true;
            }

            return document;
        }
    }

    // After the file was written: what was read is out of date.
    internal void Written(uint handle)
    {
        lock (_lock)
        {
            if (Get(handle) is { } entry)
            {
                entry.Document = null;
                entry.Checked = false;
            }
        }

        Changed?.Invoke(handle);
    }

    private XmlNode Read(string path)
    {
        try
        {
            return XmlFile.Read(files, path);
        }
        catch (ContentException e)
        {
            throw new FormatException($"the prefab {path} cannot be read: {e.Message}", e);
        }
    }

    private Entry? Get(uint handle) => handle >= 1 && handle <= _entries.Count ? _entries[(int)handle - 1] : null;

    // The file of a handle, looked for again while there is none (it may have been written since).
    private string? Resolved(Entry entry, uint handle)
    {
        if (entry.Path is null && Find(entry.Named) is { } path)
        {
            if (_byPath.TryAdd(path, handle))
            {
                _byText.Remove(entry.Named.ToString());
                entry.Path = path;
            }
            else
            {
                return path; // another handle already has that file: this one names the same, but stays the same entry
            }
        }

        return entry.Path;
    }

    private string? Find(AssetRef reference)
    {
        if (assets is not null)
        {
            return assets.Resolve(reference);
        }

        return reference.HasPath && files.FileExists(reference.Path) ? reference.Path : null;
    }

    private uint Add(AssetRef reference, string path)
    {
        _entries.Add(new Entry(reference) { Path = path });
        uint handle = (uint)_entries.Count;
        _byPath[path] = handle;
        return handle;
    }

    private uint AddPending(AssetRef reference, string text)
    {
        _entries.Add(new Entry(reference));
        uint handle = (uint)_entries.Count;
        _byText[text] = handle;
        return handle;
    }
}
