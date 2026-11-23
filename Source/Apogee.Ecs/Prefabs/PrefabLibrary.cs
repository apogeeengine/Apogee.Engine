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

        /// <summary>The file's UID as far as is known: the reference's, or the database's when the handle was made (the file was found then).</summary>
        public AssetUid Uid { get; set; } = named.Uid;

        public string? Path { get; set; }

        public XmlNode? Document { get; set; }

        /// <summary>The document this one replaced, for as long as <see cref="PrefabLibrary.Changed"/> is being raised (see <see cref="PrefabLibrary.PreviousDocument"/>).</summary>
        public XmlNode? Previous { get; set; }

        public bool Checked { get; set; }
    }

    private readonly Lock _lock = new();
    private readonly List<Entry> _entries = [];                                     // handle - 1
    private readonly Dictionary<string, uint> _byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _byText = new(StringComparer.Ordinal); // references that named no file when they were asked for

    /// <summary>
    /// Raised with the handle of a prefab whose document is not the one the library had: after its file was written by this library's world (see <see cref="World.SavePrefabFile"/>) or read again
    /// by <see cref="Reload(string)"/>. What an instance of it was made from has changed; <see cref="PreviousDocument"/> still gives what it was made from until the handlers return. Raised on the
    /// thread that changed it, outside the library's lock.
    /// </summary>
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

                // A file that was moved by hand (and the database looked again) is found at a path no handle has, by a UID that a handle has: it is that handle's file, which is how an instance that was made
                // before the move is still an instance of the same prefab after it.
                AssetUid uid = reference.Uid.IsNone && assets is not null ? assets.UidOf(path) : reference.Uid;
                if (!uid.IsNone)
                {
                    for (int i = 0; i < _entries.Count; i++)
                    {
                        Entry entry = _entries[i];
                        if (entry.Path is { } old && entry.Uid == uid && old != path && Find(AssetRef.FromPath(old)) is null)
                        {
                            _byPath.Remove(old);
                            entry.Path = path;
                            _byPath[path] = (uint)(i + 1);
                            return (uint)(i + 1);
                        }
                    }
                }

                // The file is there now: the handle made for the text that did not name one becomes this file's.
                if (_byText.Remove(text, out uint waiting))
                {
                    _entries[(int)waiting - 1].Path = path;
                    if (_entries[(int)waiting - 1].Uid.IsNone && assets is not null)
                    {
                        _entries[(int)waiting - 1].Uid = assets.UidOf(path);
                    }

                    _byPath[path] = waiting;
                    return waiting;
                }

                return Add(reference, path);
            }

            // A prefab whose file is gone (deleted, a branch switched) is still the handle it was while the library holds what it read: its instances keep working from it, and the file coming back is a reload.
            if (reference.HasPath && _byPath.TryGetValue(reference.Path, out uint remembered) && _entries[(int)remembered - 1].Document is not null)
            {
                return remembered;
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
            if (path is null)
            {
                return entry.Named;
            }

            AssetRef known = assets?.RefOf(path) ?? AssetRef.FromPath(path);

            // The UID is the file's identity: when the database cannot say it (the file was moved by hand and the database looked again, so it has nothing at the path this handle remembers), what the
            // reference this handle was made from said, or the file's own root, still does. Without it an instance would be saved as a path that leads nowhere.
            AssetUid uid = known.Uid;
            if (uid.IsNone)
            {
                uid = entry.Uid;
            }

            if (uid.IsNone && entry.Document is { } document && AssetUid.TryParse(document.Attribute("uid") ?? "", out AssetUid inFile))
            {
                uid = inFile;
            }

            string where = known.Path;
            if (assets?.PathOf(uid) is { } moved && !uid.IsNone)
            {
                where = moved; // the file is where its UID says it is now
            }

            return new AssetRef(where, uid);
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

    /// <summary>
    /// The document the prefab had before the change <see cref="Changed"/> is announcing, which is what the instances that are about to be updated were made from; null outside the event, and when
    /// the library had not read the file before. Only meaningful while the handlers of <see cref="Changed"/> run: a handler that needs it later keeps it.
    /// </summary>
    public XmlNode? PreviousDocument(uint handle)
    {
        lock (_lock)
        {
            return Get(handle)?.Previous;
        }
    }

    /// <summary>
    /// Reads the file of a prefab again, because it changed on disk (edited by hand, saved by an editor, a hot reload). The new document replaces the old one only when it is a prefab that
    /// loads and does not contain itself (directly or through the prefabs it holds); then <see cref="Changed"/> is raised. Otherwise the library keeps what it had and says why: a prefab that
    /// stops working never takes the instances made from the one that worked with it, and a file that is gone (an editor saves by replacing it, a branch is switched) is the same as one that does
    /// not parse. A file nothing has asked for yet is looked at too, so that an instance that was missing becomes whole when its prefab appears. Call it from the thread that owns the worlds.
    /// </summary>
    /// <param name="pathOrRef">The prefab: a path, <c>uid://…</c>, or <c>path|uid://…</c>.</param>
    /// <param name="maxEntities">The most entities the prefab may expand to (what a world's <see cref="World.MaxLoadEntities"/> allows), checked before the new file is adopted.</param>
    /// <exception cref="FormatException">The text is not an asset reference.</exception>
    public PrefabReload Reload(string pathOrRef, int maxEntities = ExpansionBudget.DefaultLimit)
    {
        ArgumentException.ThrowIfNullOrEmpty(pathOrRef);
        AssetRef reference = AssetRef.Parse(pathOrRef);
        if (reference.IsEmpty)
        {
            throw new FormatException("an empty text names no prefab");
        }

        PrefabReload result;
        lock (_lock)
        {
            result = ReloadLocked(Load(reference), maxEntities);
        }

        if (result.Status == PrefabReloadStatus.Reloaded)
        {
            Announce(result.Handle);
        }

        return result;
    }

    // Reads the file of a handle and, when it is usable and different, makes it the handle's document (the old one stays in Previous until Announce is done).
    private PrefabReload ReloadLocked(uint handle, int maxEntities)
    {
        Entry entry = Get(handle)!;
        if (Resolved(entry, handle) is not { } path)
        {
            return new PrefabReload(handle, null, PrefabReloadStatus.Failed, $"there is no prefab at {entry.Named}: its instances keep the version they were made from");
        }

        XmlNode fresh;
        try
        {
            fresh = Read(path);
            PrefabDocument.Check(fresh, path);
            if (NestingProblem(handle, fresh, maxEntities) is { } problem)
            {
                return new PrefabReload(handle, path, PrefabReloadStatus.Failed, problem);
            }
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        {
            return new PrefabReload(handle, path, PrefabReloadStatus.Failed, e.Message);
        }

        if (entry.Document is { } current && current.ToXml() == fresh.ToXml())
        {
            return new PrefabReload(handle, path, PrefabReloadStatus.Unchanged);
        }

        entry.Previous = entry.Document;
        entry.Document = fresh;
        entry.Checked = true;
        return new PrefabReload(handle, path, PrefabReloadStatus.Reloaded);
    }

    // Why a new document of a prefab cannot replace the old one even though it parses, or null: expanded on its own (with what the library holds of every other prefab) it holds an instance of itself, is
    // nested too deep, or is too big. A prefab that only names a prefab that does not exist is fine: that instance is empty, as it is wherever it is used.
    private string? NestingProblem(uint handle, XmlNode fresh, int maxEntities)
    {
        ResolvedPrefab Resolve(string reference)
        {
            uint other = Load(reference);
            string key = PathOf(other) ?? RefOf(other)?.ToString() ?? reference;
            if (other == handle)
            {
                return new ResolvedPrefab(key, fresh);
            }

            try
            {
                return new ResolvedPrefab(key, Require(other));
            }
            catch (Exception e) when (e is FileNotFoundException or ContentException or FormatException)
            {
                return new ResolvedPrefab(key, null, e.Message);
            }
        }

        var element = new XmlNode("Entity");
        element.SetAttribute("prefab", RefOf(handle)!.Value.ToString());
        try
        {
            Expansion expansion = new PrefabExpander(Resolve, static (_, _) => false, new ExpansionBudget(maxEntities)).Expand(element, "1");
            foreach (ExpandedEntity entity in expansion.Root.Subtree())
            {
                if (entity.IsCut)
                {
                    return entity.MissingReason;
                }
            }
        }
        catch (FormatException e)
        {
            return e.Message;
        }

        return null;
    }

    /// <summary>Why a document could replace the one the library has for this prefab (it parses as one, holds no instance of itself, is not nested too deep or too big), or null. Nothing changes.</summary>
    internal string? Problem(uint handle, XmlNode document, string origin, int maxEntities)
    {
        try
        {
            PrefabDocument.Check(document, origin, warnNextId: false);
        }
        catch (FormatException e)
        {
            return e.Message;
        }

        lock (_lock)
        {
            return NestingProblem(handle, document, maxEntities);
        }
    }

    // Tells the handlers (outside the lock), and lets go of the old document once they have all seen it.
    private void Announce(uint handle)
    {
        try
        {
            Changed?.Invoke(handle);
        }
        finally
        {
            lock (_lock)
            {
                if (Get(handle) is { } entry)
                {
                    entry.Previous = null;
                }
            }
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

    // After the file was written by this library's world: the file is read now (one that cannot be read is no document, and the next Require says why), and whatever holds an instance of it is told.
    // Not checked for nesting: SavePrefabFile has refused a prefab that holds itself.
    internal void Written(uint handle)
    {
        lock (_lock)
        {
            if (Get(handle) is { } entry)
            {
                entry.Previous = entry.Document;
                entry.Checked = false;
                try
                {
                    entry.Document = Resolved(entry, handle) is { } path ? Read(path) : null;
                }
                catch (FormatException)
                {
                    entry.Document = null;
                }
            }
        }

        Announce(handle);
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
        _entries.Add(new Entry(reference) { Path = path, Uid = reference.Uid.IsNone && assets is not null ? assets.UidOf(path) : reference.Uid });
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
