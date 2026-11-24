using System;
using Weft.Content.Xml;
using Weft.Ecs;
using Weft.Reflection;

namespace Weft.Host.Editing;

/// <summary>
/// One prefab opened on its own, to be edited as the prefab and not as an instance in a scene: its entities are made in a world of their own (<see cref="ScriptSession.CreateEditingWorld"/>), next to
/// the session's, and saved back to the file with <see cref="Save"/>. It has no interface: a tool shows <see cref="World"/> (a hierarchy, an inspector, a viewport over it), edits it with an
/// <see cref="EditHistory"/> of its own on a <see cref="WorldRef"/> over <see cref="World"/>, and calls <see cref="Save"/>.
/// </summary>
/// <remarks>
/// <para>
/// The stage's entities are the prefab's own: <see cref="Root"/> is not an instance, every entity of the file is an ordinary entity (it can be moved, renamed, deleted) that remembers its id in the file
/// (<see cref="PrefabMember"/>, so that saving keeps it), and a prefab the file holds is an instance in it, as it is in a scene, with the changes the file says. The world follows the session's prefab
/// library like any other: when something else changes a prefab the stage holds an instance of, <see cref="World.ApplyPrefabUpdates"/> (on <see cref="World"/>) makes its instances follow, and
/// <see cref="Save"/> does it for the prefab it writes. A stage belongs to its session: it ends when the session does (<see cref="Dispose"/> is then a no-op), so a tool that replaces its session
/// (Play) closes its stages first, and does not step the world of a stage but with <c>Progress(0)</c> to compose the transforms.
/// </para>
/// </remarks>
public sealed class PrefabStage : IDisposable
{
    private readonly ScriptSession _session;
    private XmlNode? _opened;   // the document of the prefab the entities were made from (or last saved as): another one means that the file changed since

    private PrefabStage(ScriptSession session, World world, uint prefab, string path, Entity root)
    {
        _session = session;
        World = world;
        Prefab = prefab;
        Path = path;
        Root = root;
        _opened = DocumentOf(world, prefab);
    }

    /// <summary>The world that holds the prefab's entities: not the session's, and not visible to its scripts.</summary>
    public World World { get; }

    /// <summary>The root entity of the prefab in <see cref="World"/>. It is not an instance: it is the prefab's own root.</summary>
    public Entity Root { get; }

    /// <summary>The path of the prefab's file (<c>res://prefabs/lamp.aprefab</c>), however it was asked for.</summary>
    public string Path { get; }

    /// <summary>The prefab's handle in the session's <see cref="PrefabLibrary"/> (the same in the stage's world and in the session's).</summary>
    public uint Prefab { get; }

    /// <summary>
    /// Whether <see cref="Save"/> can write the file: it is in a source that can be written (not a mod's or a pack's) and is not read-only on the disk. A prefab that cannot be saved still opens, and
    /// can be looked at and edited; <see cref="Save"/> says why it cannot.
    /// </summary>
    public bool CanSave => !World.IsDestroyed && World.CanWritePrefab(Prefab);

    /// <summary>Whether the stage has been disposed (or its session has).</summary>
    public bool IsDisposed => World.IsDestroyed;

    /// <summary>
    /// Opens the prefab at <paramref name="path"/> in a world of its own.
    /// </summary>
    /// <param name="session">The session whose files, assets, models and prefabs the stage uses.</param>
    /// <param name="path">A path ending in <c>.aprefab</c>, <c>uid://…</c>, or <c>path|uid://…</c> (the text of a <see cref="Weft.Core.Assets.AssetRef"/>).</param>
    /// <exception cref="System.IO.FileNotFoundException">There is no such file.</exception>
    /// <exception cref="Weft.Content.ContentException">The path is a <c>uid://</c> that no file has.</exception>
    /// <exception cref="FormatException">The text is not a reference, the file is not a prefab, its root is an instance of another prefab, or it would make more than <see cref="World.MaxLoadEntities"/> entities.</exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    public static PrefabStage Open(ScriptSession session, string path)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrEmpty(path);
        World world = session.CreateEditingWorld();
        try
        {
            PrefabLibrary library = world.Prefabs;
            uint prefab = library.Load(path);
            Entity root = world.LoadPrefabContents(prefab);
            return new PrefabStage(session, world, prefab, library.PathOf(prefab) ?? path, root);
        }
        catch
        {
            world.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes the stage's entities to the prefab's file, keeping the ids of the entities that were already in it (new ones take the file's <c>nextId</c>, and keep their id from then on). It is the write of
    /// <see cref="World.ApplyOverrides"/>: the file is checked in memory first and read back and checked after, and when anything fails it is as it was and nothing is announced. Then the session's
    /// world follows: every instance of this prefab, and of the prefabs that nest it, is brought up to date in place (unless the session's world holds updates, <see cref="World.HoldPrefabUpdates"/>:
    /// the tool applies them when it is ready).
    /// </summary>
    /// <param name="overwrite">
    /// Write even when the prefab's file changed since the stage was opened (or last saved), losing what changed. Without it a changed file is refused: the stage was made from the old one, and saving
    /// it would take back what someone else saved (an editor that applied a change to this prefab, a hot reload, the file edited by hand).
    /// </param>
    /// <returns>What bringing the session's world up to date did (see <see cref="PrefabUpdate"/>: an editor with an undo history clears it, or does not undo past it); null when nothing was written or the world holds its updates.</returns>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written (<see cref="CanSave"/> is false).</exception>
    /// <exception cref="InvalidOperationException">
    /// The prefab holds, directly or through another prefab, an instance of itself; the file changed since the stage was opened (<paramref name="overwrite"/>) or on the disk since the library read it;
    /// what would be written is not a prefab that loads; the file could not be read back (it was put back).
    /// </exception>
    /// <exception cref="ObjectDisposedException">The stage is disposed.</exception>
    public PrefabUpdate? Save(bool overwrite = false)
    {
        ObjectDisposedException.ThrowIf(World.IsDestroyed, this);
        if (!overwrite && !ReferenceEquals(DocumentOf(World, Prefab), _opened))
        {
            throw new InvalidOperationException($"{Path} changed since this stage was opened: saving it would take back what changed (open it again, or save with overwrite)");
        }

        World.SavePrefabContents(Prefab, Root);
        _opened = DocumentOf(World, Prefab);

        // Written: the library has told every world that follows it, and the session's world follows it now (its instances, and the ones of the prefabs that nest this one).
        World main = _session.World;
        return main.HoldPrefabUpdates ? null : main.ApplyPrefabUpdates();
    }

    /// <summary>Closes the stage: its world is disposed, and so are the entities in it. Does nothing when it is already closed.</summary>
    public void Dispose() => World.Dispose();

    // The document the library holds for the prefab now (a new object each time the file is read again with other content); null when there is none.
    private static XmlNode? DocumentOf(World world, uint prefab)
    {
        try
        {
            return world.Prefabs.Document(prefab);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
