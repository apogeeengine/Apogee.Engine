using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Apogee.Content;
using Apogee.Content.FileSystem;
using Apogee.Content.Xml;
using Apogee.Core.Logging;
using Apogee.Reflection;

namespace Apogee.Ecs;

/// <summary>What <see cref="World.BreakPrefabLink"/> did.</summary>
/// <param name="Unlinked">The roots of the instances that are plain entities now: the one that was asked for, and with <c>completely</c> the instances inside it.</param>
/// <param name="Dropped">
/// What the unlinked instances' elements held that could not be applied (changes to things their prefab no longer has, kept aside to apply again if they came back): they have nothing to apply to any
/// more and are dropped, here as the elements they were.
/// </param>
public sealed record PrefabLinkReport(IReadOnlyList<Entity> Unlinked, IReadOnlyList<XmlNode> Dropped);

/// <summary>What an update of one instance in place may be asked to change in the instance's saved form before it is planned again (a revert, an apply): see <c>World.PrefabRefresh.cs</c>.</summary>
internal sealed class CaptureEdit(Entity root, World.SavePlan saved, XmlNode document, Dictionary<string, Entity> living)
{
    /// <summary>The instance being updated (the outermost one: it holds every instance inside it).</summary>
    public Entity Root { get; } = root;

    /// <summary>What was saved: the address of every entity.</summary>
    public World.SavePlan Saved { get; } = saved;

    /// <summary>The scene document the instance was saved as.</summary>
    public XmlNode Document { get; } = document;

    /// <summary>The entities of the instance by their address in <see cref="Document"/>: an entity that will have another address is moved here.</summary>
    public Dictionary<string, Entity> Living { get; } = living;

    /// <summary>The element of the instance in <see cref="Document"/>.</summary>
    public XmlNode? InstanceElement => Document.Children.Find(static child => child.Name == "Entity");
}

// The editor-facing API for what an instance changed (docs/manual/content/prefabs.md, "From an editor or a tool"): list the overrides, revert them, apply them to the prefab file, break the link.
//
// The overrides are not recorded: they are read from the same form an instance is saved in (World.Scene.cs, PlanSave/WriteInstance), so what the list says is what a scene would hold. A revert changes that
// form (takes the override out) and updates the instance from it in place, as a prefab that changed does (World.PrefabRefresh.cs): the entities keep their ids. An apply writes the override into the
// prefab's file and then does the same, with the override taken out of the instance's form because the file says it now.
public sealed unsafe partial class World
{
    /// <summary>What listing an instance's overrides saw.</summary>
    private sealed record OverrideCapture(SavePlan Saved, InstanceSave Instance, XmlNode Element, List<FoundOverride> Found);

    // The instance as it would be saved, with its overrides read from the element. Nothing changes in the world.
    private OverrideCapture CaptureOverrides(Entity root)
    {
        RequireInstance(root);
        var outside = new Dictionary<string, Entity>(StringComparer.Ordinal);
        int number = _numbered; // numbers nothing else has; the world's counter is not advanced: nothing is kept of this
        SavePlan saved = PlanSave([root], _ => checked(++number), default, outside);
        if (!saved.Instances.TryGetValue(root.Id, out InstanceSave? instance))
        {
            throw new InvalidOperationException("the instance's prefab cannot be named by this world's library, so its changes cannot be told");
        }

        XmlNode element = WriteScene(saved, [root]).Children.Find(static child => child.Name == SceneEntity)!;
        Dictionary<string, Entity> byAddress = AddressesOf(saved);
        int kept = saved.KeptNodes.TryGetValue(root.Id, out List<XmlNode>? nodes) ? nodes.Count : 0;
        return new OverrideCapture(saved, instance, element, OverrideReader.Read(element, instance.Address, kept, address => byAddress.GetValueOrDefault(address)));
    }

    private static Dictionary<string, Entity> AddressesOf(SavePlan saved)
    {
        var byAddress = new Dictionary<string, Entity>(StringComparer.Ordinal);
        foreach ((ulong id, string address) in saved.AddressOf)
        {
            byAddress.TryAdd(address, new Entity(id));
        }

        return byAddress;
    }

    private void RequireInstance(Entity root)
    {
        if (!IsAlive(root) || !Has<PrefabInstance>(root))
        {
            throw new ArgumentException("the entity is not the root of a prefab instance", nameof(root));
        }
    }

    // ---- Listing

    /// <summary>
    /// What the instance whose root is <paramref name="root"/> has changed from its prefab: every field, name, component and entity that differs, and what its element keeps aside. It is worked out now from
    /// the entities (nothing is recorded while they are edited), the same way a scene is saved, so it is what a scene would hold. The root of an instance inside another instance is a root too: its
    /// changes are against its own prefab, including what the prefab it is in says about it.
    /// </summary>
    /// <exception cref="ArgumentException">The entity is not the root of an instance.</exception>
    public IReadOnlyList<PrefabOverride> PrefabOverrides(Entity root) => [.. CaptureOverrides(root).Found.Select(static found => found.Override)];

    /// <summary>
    /// Whether the component of the entity (or one of its fields) is changed from the entity's prefab: what an inspector marks. The instance is the nearest one above the entity (or the entity itself). It
    /// works the whole instance out each time: to ask about many fields, list <see cref="PrefabOverrides(Entity)"/> once and ask <see cref="PrefabOverride.Concerns"/>.
    /// </summary>
    public bool IsOverridden(Entity entity, string component, string? field = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(component);
        Entity root = InstanceOf(entity);
        return !root.IsNull && PrefabOverrides(root).Any(o => o.Concerns(entity, component, field));
    }

    // The overrides found now that the given (listed earlier) ones are.
    private static List<FoundOverride> Resolve(OverrideCapture capture, IEnumerable<PrefabOverride> wanted)
    {
        var chosen = new List<FoundOverride>();
        foreach (PrefabOverride o in wanted)
        {
            ArgumentNullException.ThrowIfNull(o);
            FoundOverride? found = capture.Found.Find(candidate => OverrideReader.Is(o, AbsoluteOf(capture.Instance.Address, o), candidate));
            if (found is null)
            {
                throw new ArgumentException($"{o.Kind} {o.Component}{(o.Field is null ? "" : "." + o.Field)} at '{o.Target}' is not a change of this instance (it was reverted or applied, or the entities changed since it was listed)", nameof(wanted));
            }

            if (!chosen.Contains(found))
            {
                chosen.Add(found);
            }
        }

        return chosen;
    }

    private static string AbsoluteOf(string instance, PrefabOverride o) => o.Target.Length == 0 ? instance : instance + "/" + o.Target;

    // ---- Reverting

    /// <summary>Puts one override back to what the prefab says. See <see cref="RevertOverrides"/>.</summary>
    public void RevertOverride(Entity root, PrefabOverride change) => RevertOverrides(root, [change]);

    /// <summary>
    /// Puts the given overrides of an instance back to what its prefab says: a field gets the prefab's value, a name the prefab's, an added component is removed and a removed one comes back, an added entity
    /// is destroyed and a removed one is made again, and an <see cref="PrefabOverrideKind.Unapplied"/> element is dropped. The entities that stay keep their ids (the instance is updated in place, as
    /// when its prefab changes); what is destroyed takes what is below it. Nothing changes when it fails.
    /// </summary>
    /// <param name="root">The root of an instance that is not inside another instance (revert those through the instance that holds them).</param>
    /// <param name="overrides">What <see cref="PrefabOverrides(Entity)"/> listed; one that is not a change of the instance any more is an <see cref="ArgumentException"/>.</param>
    /// <exception cref="InvalidOperationException">The root is part of another instance, or the instance cannot be updated (the prefab nests too deep, is too big…): it is left as it was.</exception>
    public void RevertOverrides(Entity root, IEnumerable<PrefabOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        RequireInstance(root);
        if (IsPrefabMember(root))
        {
            throw new InvalidOperationException("this instance is part of another: revert its changes through the instance that holds it");
        }

        OverrideCapture capture = CaptureOverrides(root);
        List<FoundOverride> chosen = Resolve(capture, overrides);
        List<PrefabOverride> live = [.. chosen.Select(static found => found.Override).Where(static o => o.Kind != PrefabOverrideKind.Unapplied)];
        HashSet<int> unapplied = [.. chosen.Select(static found => found.Override).Where(static o => o.Kind == PrefabOverrideKind.Unapplied).Select(static o => o.Index)];

        _kept.TryGetValue(root.Id, out KeptData? before);
        if (unapplied.Count > 0 && before is not null)
        {
            SetKept(root, With(before, [.. before.Unapplied.Where((_, at) => !unapplied.Contains(at))]));
        }

        try
        {
            if (live.Count > 0)
            {
                UpdateOne(root, edit => RemoveFromCapture(edit, root, live));
            }
        }
        catch
        {
            SetKept(root, before);
            throw;
        }
    }

    /// <summary>Reverts the overrides of one component of one entity of the instance (its changed fields, or the component added or removed).</summary>
    public void RevertComponentOverrides(Entity root, Entity entity, string component) =>
        RevertOverrides(root, PrefabOverrides(root).Where(o => o.Entity == entity && o.Component == component));

    /// <summary>Reverts every override on one entity of the instance: its name, and its components (not the entities added below it, which are entities of their own).</summary>
    public void RevertEntityOverrides(Entity root, Entity entity) =>
        RevertOverrides(root, PrefabOverrides(root).Where(o => o.Entity == entity && o.Kind is PrefabOverrideKind.Field or PrefabOverrideKind.Name or PrefabOverrideKind.AddedComponent or PrefabOverrideKind.RemovedComponent));

    /// <summary>
    /// Reverts everything the instance changed, the entities it added and removed included, so that it is its prefab again (the name of its root, which a scene gives it, stays). What the element keeps
    /// aside (<see cref="PrefabOverrideKind.Unapplied"/>) is not touched: see <see cref="DropUnappliedOverrides"/>.
    /// </summary>
    public void RevertAllOverrides(Entity root) => RevertOverrides(root, PrefabOverrides(root).Where(static o => o.Kind != PrefabOverrideKind.Unapplied));

    /// <summary>Drops what the instance's element keeps aside because its prefab no longer has what it names (it would apply again if the entity or component came back).</summary>
    public void DropUnappliedOverrides(Entity root)
    {
        RequireInstance(root);
        if (_kept.TryGetValue(root.Id, out KeptData? kept) && kept.Unapplied.Count > 0)
        {
            SetKept(root, With(kept, []));
        }
    }

    // What an entity keeps (see KeptData): nothing when there is nothing to keep.
    private void SetKept(Entity entity, KeptData? kept)
    {
        if (kept is null || kept.IsEmpty)
        {
            _kept.Remove(entity.Id);
        }
        else
        {
            _kept[entity.Id] = kept;
        }
    }

    private static KeptData With(KeptData kept, IReadOnlyList<XmlNode> unapplied) =>
        new() { Unapplied = unapplied, RawPrefab = kept.RawPrefab, Assets = kept.Assets, Refs = kept.Refs };

    // Updates one instance in place from its saved form changed by `edit`; throws (the instance is as it was) when it cannot.
    private void UpdateOne(Entity root, Action<CaptureEdit> edit)
    {
        if (_applying)
        {
            throw new InvalidOperationException("a prefab update is running: wait until it is done");
        }

        var update = new PrefabUpdate([]);
        var aftermath = new Aftermath();
        _aftermath = aftermath;
        _captureEdit = edit;
        try
        {
            UpdateInstance(root, null, update, aftermath);
            if (update.Failed.Count == 0)
            {
                FinishReferences(aftermath);
            }
        }
        finally
        {
            _aftermath = null;
            _captureEdit = null;
        }

        if (update.Failed.Count > 0)
        {
            throw new InvalidOperationException($"the instance could not be changed and is as it was: {update.Failed[0].Message}");
        }
    }

    // Takes the given overrides (of `applied`'s instance) out of the saved form of the instance that is being updated, which may be the one that holds it.
    private static void RemoveFromCapture(CaptureEdit edit, Entity applied, IReadOnlyList<PrefabOverride> overrides)
    {
        if (!edit.Saved.AddressOf.TryGetValue(applied.Id, out string? address) || edit.InstanceElement is not { } element || !edit.Saved.AddressOf.TryGetValue(edit.Root.Id, out string? top))
        {
            return; // another instance of the update
        }

        Dictionary<string, Entity> byAddress = AddressesOf(edit.Saved);
        int kept = edit.Saved.KeptNodes.TryGetValue(edit.Root.Id, out List<XmlNode>? nodes) ? nodes.Count : 0;
        List<FoundOverride> found = OverrideReader.Read(element, top, kept, name => byAddress.GetValueOrDefault(name));
        foreach (PrefabOverride o in overrides)
        {
            if (found.Find(candidate => candidate.Override.Kind != PrefabOverrideKind.Unapplied && OverrideReader.Is(o, AbsoluteOf(address, o), candidate)) is { } match)
            {
                OverrideReader.Remove(element, match);
            }
        }
    }

    // ---- Applying

    /// <summary>Writes one override into the prefab file. See <see cref="ApplyOverrides"/>.</summary>
    public PrefabUpdate? ApplyOverride(Entity root, PrefabOverride change, PrefabApplyTarget target = PrefabApplyTarget.InstancePrefab) => ApplyOverrides(root, [change], target);

    /// <summary>Applies every override of the instance (not what is kept aside). See <see cref="ApplyOverrides"/>.</summary>
    public PrefabUpdate? ApplyAllOverrides(Entity root, PrefabApplyTarget target = PrefabApplyTarget.InstancePrefab) =>
        ApplyOverrides(root, PrefabOverrides(root).Where(static o => o.Kind != PrefabOverrideKind.Unapplied), target);

    /// <summary>
    /// Makes what an instance changed part of its prefab: the overrides are written into the prefab's file (the ids of the file stay, an added entity takes the next id and a removed one is taken out), the
    /// file is read back and checked, and then every instance of the prefab follows it in place, this one included, which now has nothing to say about what was applied. It is all or nothing: when
    /// anything is refused, written wrongly or cannot be followed, the file is put back byte for byte and the instances are as they were.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A file is rewritten as the engine writes prefabs (comments and layout of a hand-written file are not kept), by a write that leaves the old file whole when it is interrupted, and only when it is
    /// writable, is what the library has read (it is refused when it changed on the disk since: reload it first), and is not read-only. <see cref="PrefabApplyTarget.InstancePrefab"/> writes the one
    /// file the instance names; <see cref="PrefabApplyTarget.SourcePrefab"/> writes the file that defines each entity, which may be several. A file that something between the instance and it
    /// pins (the instance's prefab holds its own override of the same field) keeps overriding it: the override is still listed afterwards.
    /// </para>
    /// <para>
    /// What is written must stand on its own in a prefab: an Entity field has to name an entity of the same instance (it is refused when it names one outside, or one the instance added), and an
    /// added entity brings what is below it, its instances included, with fields that name nothing outside the prefab. Removing an entity takes it from the prefab for everyone: the changes other
    /// prefabs and scenes hold for it are kept aside and apply again if it comes back.
    /// </para>
    /// <para>Not undoable by <c>Restore</c>: it writes a file. A tool that offers undo writes the file back (see the commands of <c>Apogee.Host</c>).</para>
    /// </remarks>
    /// <returns>What following the file did (every instance of every prefab written), or null when nothing was written.</returns>
    /// <exception cref="InvalidOperationException">Something is refused: pending updates, a missing or changed file, an override that cannot stand in a prefab, an instance that cannot follow.</exception>
    /// <exception cref="UnauthorizedAccessException">A file to write is read-only or in a source that cannot be written.</exception>
    public PrefabUpdate? ApplyOverrides(Entity root, IEnumerable<PrefabOverride> overrides, PrefabApplyTarget target = PrefabApplyTarget.InstancePrefab)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        RequireInstance(root);
        if (_applying)
        {
            throw new InvalidOperationException("a prefab update is running: wait until it is done");
        }

        if (HasPendingPrefabUpdates)
        {
            throw new InvalidOperationException("a prefab changed and its instances have not followed it yet: ApplyPrefabUpdates first, or the file would be written from entities made from an older one");
        }

        VirtualFileSystem files = Files ?? throw new InvalidOperationException("this world has no files to write a prefab to");
        PrefabLibrary library = Prefabs;
        OverrideCapture capture = CaptureOverrides(root);
        if (capture.Instance.Expansion.Root.IsMissing)
        {
            throw new InvalidOperationException($"the prefab of this instance cannot be read ({capture.Instance.Expansion.Root.MissingReason}): there is no file to write");
        }

        List<FoundOverride> chosen = Resolve(capture, overrides);
        if (chosen.Any(static found => found.Override.Kind == PrefabOverrideKind.Unapplied))
        {
            throw new ArgumentException("what an instance keeps aside has nothing to apply to: revert (drop) it instead", nameof(overrides));
        }

        if (chosen.Count == 0)
        {
            return null;
        }

        // 1. The files, as the disk has them now.
        var edits = new Dictionary<string, PrefabEditFile>(StringComparer.Ordinal);
        PrefabEditFile Open(string reference) => OpenForEdit(files, library, edits, reference);
        PrefabEditFile first = Open(capture.Instance.Reference);
        var edit = new PrefabFileEdit(Open);

        // 2. The changes, in memory: every refusal comes before anything is written.
        var placed = new List<(Entity Entity, string Hops, string Local)>();
        foreach (FoundOverride found in chosen)
        {
            EditOne(edit, first, capture, found.Override, target, placed);
        }

        List<PrefabEditFile> changed = [.. edits.Values.Where(static file => file.Changed)];
        if (changed.Count == 0)
        {
            return null;
        }

        foreach (PrefabEditFile file in changed)
        {
            file.Finish();
            if (library.Problem(file.Handle, file.Document, file.Path, MaxLoadEntities) is { } problem)
            {
                throw new InvalidOperationException($"{file.Path} would not be a prefab that loads, so nothing was written: {problem}");
            }

            if (!files.CanWrite(file.Path))
            {
                throw new UnauthorizedAccessException($"{file.Path} is not in a source that can be written");
            }

            if (files.TryGetPhysicalPath(file.Path) is { } physical && new FileInfo(physical).IsReadOnly)
            {
                throw new UnauthorizedAccessException($"{file.Path} is read-only");
            }
        }

        // 3. Written, read back and checked; the library takes the new documents. Anything that goes wrong puts the files back.
        var written = new List<PrefabEditFile>();
        PrefabUpdate? update;
        bool following = false;
        try
        {
            foreach (PrefabEditFile file in changed)
            {
                if (!files.ReadAllBytes(file.Path).AsSpan().SequenceEqual(file.Original))
                {
                    throw new InvalidOperationException($"{file.Path} changed on the disk while the change was being made: nothing was written");
                }
            }

            foreach (PrefabEditFile file in changed)
            {
                files.WriteAllText(file.Path, SceneText(file.Document));
                written.Add(file);
            }

            foreach (PrefabEditFile file in changed)
            {
                PrefabReload result = library.Reload(file.Path, MaxLoadEntities);
                if (result.Status == PrefabReloadStatus.Failed)
                {
                    throw new InvalidOperationException($"{file.Path} was written but cannot be read back as a prefab ({result.Error}): it was put back");
                }
            }

            // 4. The instances follow the files, this one with the overrides taken out of its saved form: the files say them now.
            var applied = new List<PrefabOverride>(chosen.Select(static found => found.Override));
            _captureEdit = capture2 => AfterApply(capture2, root, applied, placed);
            following = true;
            try
            {
                update = ApplyPrefabUpdates();
            }
            finally
            {
                _captureEdit = null;
            }
        }
        catch
        {
            PutFilesBack(files, library, written);
            if (!following)
            {
                // No instance was told to follow: the change the files announced is not one any more.
                DiscardPending(written);
            }

            throw;
        }

        if (update is not null)
        {
            // The instance that holds this one could not follow: the files go back, and the instances that did follow go back with them. That one is as it was, which is what the file says again.
            PrefabUpdateFailure[] stuck = [.. update.Failed.Where(failure => failure.Instance == root || HasAncestorIn(root, [failure.Instance.Id]))];
            if (stuck.Length > 0)
            {
                foreach (PrefabUpdateFailure failure in stuck)
                {
                    MarkUpToDate(failure.Instance);
                }

                PutFilesBack(files, library, written);
                ApplyPrefabUpdates();
                throw new InvalidOperationException($"the instance could not follow the prefab and the file was put back: {stuck[0].Message}");
            }
        }

        return update;
    }

    private void DiscardPending(List<PrefabEditFile> files)
    {
        lock (_updatesLock)
        {
            foreach (PrefabEditFile file in files)
            {
                if (_pendingBefore.Remove(file.Handle))
                {
                    _pendingOrder.Remove(file.Handle);
                }
            }
        }
    }

    // Reads a prefab file for editing: a copy of the document as the disk has it now, which must be the one the library holds (a file changed behind the library's back is not written over).
    private static PrefabEditFile OpenForEdit(VirtualFileSystem files, PrefabLibrary library, Dictionary<string, PrefabEditFile> edits, string reference)
    {
        uint handle = library.Load(reference);
        string path = library.PathOf(handle) ?? throw new InvalidOperationException($"the prefab {reference} is not a file this world can find: there is nothing to write");
        if (edits.TryGetValue(path, out PrefabEditFile? open))
        {
            return open;
        }

        byte[] bytes;
        XmlNode fresh;
        try
        {
            bytes = files.ReadAllBytes(path);
            fresh = XmlFile.Read(bytes, path);
            PrefabDocument.Check(fresh, path, warnNextId: false);
        }
        catch (Exception e) when (e is ContentException or FormatException or IOException)
        {
            throw new InvalidOperationException($"{path} cannot be read as a prefab, so it is not written over: {e.Message}", e);
        }

        XmlNode? known = library.Document(handle);
        if (known is null || known.ToXml() != fresh.ToXml())
        {
            throw new InvalidOperationException($"{path} changed on the disk since this world read it: reload it (PrefabLibrary.Reload) and apply again");
        }

        var file = new PrefabEditFile(handle, path, path, fresh, bytes);
        edits[path] = file;
        return file;
    }

    // Puts the files back as they were read, and the library with them; the instances were not told anything that has to be undone (a pending update that came of it is dropped).
    private void PutFilesBack(VirtualFileSystem files, PrefabLibrary library, List<PrefabEditFile> written)
    {
        foreach (PrefabEditFile file in written)
        {
            try
            {
                files.WriteAllBytes(file.Path, file.Original);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Error("Prefab", $"{file.Path} could not be put back as it was: {e.Message}");
            }
        }

        foreach (PrefabEditFile file in written)
        {
            library.Reload(file.Path, MaxLoadEntities);
        }
    }

    // One override into the files.
    private void EditOne(PrefabFileEdit edit, PrefabEditFile first, OverrideCapture capture, PrefabOverride o, PrefabApplyTarget target, List<(Entity Entity, string Hops, string Local)> placed)
    {
        string instance = capture.Instance.Address;
        if (!EntityAddress.TryParse(o.Target, out int[] segments) && o.Target.Length > 0)
        {
            throw new InvalidOperationException($"'{o.Target}' is not an address of the instance");
        }

        if (o.Target.Length == 0)
        {
            segments = [];
        }

        switch (o.Kind)
        {
            case PrefabOverrideKind.Field:
            {
                EditSite site = edit.Locate(first, instance, segments, target, intoRoot: true);
                string text = o.Element.Attribute(o.Field!) ?? "";
                if (IsEntityFieldOf(o.Component!, o.Field!))
                {
                    text = new FileScope(site.FileInstance, site.File.RootId).ToFile(text)
                        ?? throw new InvalidOperationException($"{o.Component}.{o.Field} names an entity that is not part of the prefab (outside the instance, or one the instance added): a prefab cannot name it");
                }

                PrefabFileEdit.SetField(site, o.Component!, o.Field!, text);
                break;
            }

            case PrefabOverrideKind.Name:
                PrefabFileEdit.SetName(edit.Locate(first, instance, segments, target, intoRoot: false), o.Element.Attribute("name") ?? "");
                break;
            case PrefabOverrideKind.AddedComponent:
            {
                EditSite site = edit.Locate(first, instance, segments, target, intoRoot: true);
                XmlNode component = ComponentForFile(o.Element, new FileScope(site.FileInstance, site.File.RootId), o.Entity);
                PrefabFileEdit.AddComponent(site, component, SchemaOf(o.Component!));
                break;
            }

            case PrefabOverrideKind.RemovedComponent:
                PrefabFileEdit.RemoveComponent(edit.Locate(first, instance, segments, target, intoRoot: true), o.Component!);
                break;
            case PrefabOverrideKind.RemovedEntity:
                PrefabFileEdit.RemoveEntity(edit.Locate(first, instance, segments, target, intoRoot: false));
                break;
            case PrefabOverrideKind.AddedEntity:
                AddEntityToFile(edit.Locate(first, instance, segments, target, intoRoot: true), capture, o, placed);
                break;
            default:
                throw new ArgumentException($"{o.Kind} cannot be applied", nameof(o));
        }
    }

    // The component element of an added component for a file: no `append` (the file's entity just has it), Entity fields in the file's numbering.
    private XmlNode ComponentForFile(XmlNode element, FileScope scope, Entity owner)
    {
        XmlNode component = element.Clone();
        component.RemoveAttribute("append");
        for (int i = 0; i < component.Attributes.Count; i++)
        {
            XmlProperty attribute = component.Attributes[i];
            if (!OverrideReader.IsReserved(attribute.Name) && IsEntityFieldOf(component.Name, attribute.Name))
            {
                string text = scope.ToFile(attribute.Value)
                    ?? throw new InvalidOperationException($"{component.Name}.{attribute.Name} names an entity that is not part of the prefab (outside the instance, or one the instance added): a prefab cannot name it");
                component.Attributes[i] = attribute with { Value = text };
            }
        }

        return component;
    }

    // The definition of a script-made component for a file; null for one the engine or the game defines.
    private XmlNode? SchemaOf(string component) =>
        InfoByName(component) is { ClrType: null } info ? SchemasOf(new SortedDictionary<string, ComponentInfo>(StringComparer.Ordinal) { [component] = info })?.Children[0].Clone() : null;

    // An added entity (and everything below it) as entities of the file, with ids from the file's own numbering.
    private void AddEntityToFile(EditSite site, OverrideCapture capture, PrefabOverride o, List<(Entity Entity, string Hops, string Local)> placed)
    {
        Entity added = o.Entity;
        if (added.IsNull || !IsAlive(added))
        {
            throw new InvalidOperationException("the entity to add to the prefab is gone");
        }

        PrefabEditFile file = site.File;
        var scope = new FileScope(site.FileInstance, file.RootId);
        SavePlan plan = PlanSave([added], _ => file.NewId(), default);

        // What the instance has of the prefab's own can be named by the new entities in the file's ids.
        foreach ((ulong id, string address) in capture.Saved.AddressOf)
        {
            if (!plan.AddressOf.ContainsKey(id) && scope.ToFile(address) is { Length: > 0 } local)
            {
                plan.AddressOf[id] = local;
            }
        }

        List<Entity> subtree = Subtree(added);
        foreach (Entity entity in subtree)
        {
            foreach ((Entity id, ComponentInfo info) in ComponentsOf(entity))
            {
                if (info.IsTag || IsSceneSkipped(info.Name))
                {
                    continue;
                }

                byte* data = GetPointer(entity, id);
                foreach (FieldDef field in info.Fields)
                {
                    if (field.Kind == FieldKind.Entity && *(ulong*)(data + field.Offset) is var target and not 0 && !plan.AddressOf.ContainsKey(target))
                    {
                        throw new InvalidOperationException($"{GetName(entity) ?? "an entity"} of what is added has {info.Name}.{field.Name} naming an entity that is not part of the prefab (outside the instance, or one the instance added): a prefab cannot name it");
                    }
                }
            }
        }

        (List<XmlNode> top, XmlNode? schemas) = WritePlanned(plan, [added]);
        PrefabFileEdit.AddEntity(site, top[0]);
        PrefabFileEdit.AddSchemas(file, schemas);

        var inside = new HashSet<ulong>(subtree.Select(static entity => entity.Id));
        string hops = site.FileInstance[capture.Instance.Address.Length..];
        foreach ((ulong id, string local) in plan.AddressOf)
        {
            if (inside.Contains(id))
            {
                placed.Add((new Entity(id), hops, local));
            }
        }
    }

    // What the update that follows an apply does to the saved form of the instance: the applied overrides are the file's now, and the entities that were added are the prefab's members now (at the
    // address the file gives them), which they keep being (same entities, same ids).
    private void AfterApply(CaptureEdit edit, Entity root, IReadOnlyList<PrefabOverride> applied, List<(Entity Entity, string Hops, string Local)> placed)
    {
        if (!edit.Saved.AddressOf.TryGetValue(root.Id, out string? address) || edit.InstanceElement is not { } element)
        {
            return;
        }

        // What the file says now is not said again, except what would not mean the same: a removal of what the file no longer has would be kept aside, and an added entity would be made twice. A changed
        // field, a name or an added component is left: against the new file it is the same value, and where a file between the instance and this one pins another, it keeps what was applied.
        RemoveFromCapture(edit, root, [.. applied.Where(static o => o.Kind is PrefabOverrideKind.RemovedComponent or PrefabOverrideKind.RemovedEntity or PrefabOverrideKind.AddedEntity)]);
        var moved = new Dictionary<string, string>(StringComparer.Ordinal);
        var arrived = new List<(string Address, Entity Entity)>();
        foreach ((Entity entity, string hops, string local) in placed)
        {
            if (edit.Saved.AddressOf.TryGetValue(entity.Id, out string? old))
            {
                string now = address + hops + "/" + local;
                edit.Living.Remove(old);
                moved[old] = now;
                arrived.Add((now, entity));
            }
        }

        foreach ((string now, Entity entity) in arrived)
        {
            edit.Living[now] = entity;
        }

        if (moved.Count > 0)
        {
            RewriteAddresses(element, moved);
        }
    }

    // The entity fields of the element that name an entity that moved, by what they were named and what follows.
    private void RewriteAddresses(XmlNode node, Dictionary<string, string> moved)
    {
        if (node.Name is SceneEntity or "Override")
        {
            foreach (XmlNode child in node.Children)
            {
                RewriteAddresses(child, moved);
            }

            return;
        }

        for (int i = 0; i < node.Attributes.Count; i++)
        {
            XmlProperty attribute = node.Attributes[i];
            if (attribute.Value.Length > 0 && EntityAddress.IsValid(attribute.Value) && IsEntityFieldOf(node.Name, attribute.Name))
            {
                string text = attribute.Value;
                for (int end = text.Length; end > 0; end = text.LastIndexOf('/', end - 1))
                {
                    if (moved.TryGetValue(text[..end], out string? now))
                    {
                        node.Attributes[i] = attribute with { Value = now + text[end..] };
                        break;
                    }
                }
            }
        }
    }

    // ---- Breaking the link

    /// <summary>
    /// Turns an instance into plain entities: <see cref="PrefabInstance"/> and <see cref="PrefabMember"/> come off, so a scene saves them as the entities they are and later changes of the prefab no longer
    /// reach them. Entities, ids, names and values stay as they are. An instance inside it stays an instance (of its own prefab, with its changes against that prefab) unless <paramref name="completely"/>.
    /// What the instance's element kept aside is dropped (it has nothing to apply to now) and given back in the report.
    /// </summary>
    /// <param name="root">The root of an instance that is not inside another instance.</param>
    /// <param name="completely">Also unlink the instances inside it, and so on.</param>
    /// <exception cref="InvalidOperationException">The instance is part of another one: unlink the one that holds it.</exception>
    public PrefabLinkReport BreakPrefabLink(Entity root, bool completely = false)
    {
        RequireInstance(root);
        if (IsPrefabMember(root))
        {
            throw new InvalidOperationException("this instance is part of another: break the link of the instance that holds it (or open its prefab)");
        }

        List<Entity> tree = Subtree(root);
        var roots = new List<Entity> { root };
        if (completely)
        {
            roots.AddRange(tree.Where(entity => entity != root && Has<PrefabInstance>(entity)));
        }

        var dropped = new List<XmlNode>();
        foreach (Entity unlink in roots)
        {
            if (_kept.TryGetValue(unlink.Id, out KeptData? kept))
            {
                dropped.AddRange(kept.Unapplied);
                SetKept(unlink, new KeptData { Assets = kept.Assets, Refs = kept.Refs });
            }

            Remove<PrefabInstance>(unlink);
        }

        // What named a place in an instance that is not one any more names nothing: a member is tagged only while the instance it belongs to (below the root) is there.
        foreach (Entity entity in tree)
        {
            if (TryGet(entity, out PrefabMember member) && !HasOwnerWithin(entity, member.Prefab, root))
            {
                Remove<PrefabMember>(entity);
            }
        }

        // An instance that stays one is the root of its prefab as a scene's instance is (it was a member of the prefab it was in).
        foreach (Entity entity in tree)
        {
            if (entity != root && TryGet(entity, out PrefabInstance link) && !Has<PrefabMember>(entity) && RootIdOf(link.Prefab) is { } id)
            {
                Set(entity, new PrefabMember { Id = (uint)id, Prefab = link.Prefab });
            }
        }

        if (dropped.Count > 0)
        {
            Log.Warn("Prefab", $"{dropped.Count} change(s) the instance kept aside for things its prefab no longer has were dropped when its link was broken");
        }

        return new PrefabLinkReport(roots, [.. dropped.Select(static node => node.Clone())]);
    }

    private bool HasOwnerWithin(Entity entity, uint prefab, Entity top)
    {
        for (Entity current = entity; !current.IsNull; current = GetParent(current))
        {
            if (TryGet(current, out PrefabInstance link) && link.Prefab == prefab)
            {
                return true;
            }

            if (current == top)
            {
                break;
            }
        }

        return false;
    }

    private int? RootIdOf(uint prefab)
    {
        try
        {
            return ExistingLibrary?.Document(prefab) is { } document && PrefabDocument.TryParseId(PrefabDocument.RootEntity(document).Attribute("id"), out int id) ? id : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ---- Making a prefab

    /// <summary>
    /// Writes <paramref name="root"/> and everything below it as a new prefab file (see <see cref="SavePrefabFile"/>) and makes the entities an instance of it: the same entities, with no overrides but
    /// what could not be written (an Entity field that names an entity outside the tree). The file is new: one that exists is not written over.
    /// </summary>
    /// <param name="root">The root of the new prefab: not an instance, not part of one (unpack that first).</param>
    /// <param name="path">A new path ending in <c>.aprefab</c>.</param>
    /// <returns><paramref name="root"/>, now the root of an instance.</returns>
    /// <exception cref="InvalidOperationException">The entity is an instance or part of one, the file exists, or the tree holds an instance of the prefab (a prefab that contains itself).</exception>
    /// <exception cref="UnauthorizedAccessException">The path is not in a writable source.</exception>
    public Entity CreatePrefab(Entity root, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        VirtualFileSystem files = Files ?? throw new InvalidOperationException("this world has no files to save a prefab to");
        if (!IsUserEntity(root))
        {
            throw new ArgumentException("the root of a prefab must be an entity of the world", nameof(root));
        }

        if (Has<PrefabInstance>(root))
        {
            throw new InvalidOperationException("this entity is already an instance of a prefab: break its link first, or the new prefab would hold another one's entities");
        }

        if (IsPrefabMember(root))
        {
            throw new InvalidOperationException("this entity is part of an instance: break the link of the instance first");
        }

        string resolved = ResolveScenePath(path);
        if (files.Exists(resolved))
        {
            throw new InvalidOperationException($"{resolved} already exists: a prefab is created at a new path (to change one, apply overrides to it)");
        }

        if (!files.CanWrite(resolved))
        {
            throw new UnauthorizedAccessException($"{resolved} is not in a source that can be written");
        }

        SavePrefabFile(resolved, root);
        uint handle = Prefabs.Load(resolved);
        Set(root, new PrefabInstance { Prefab = handle });
        MarkUpToDate(root); // made from the file it has just been written as
        ApplyPrefabUpdates(); // nothing else was made from it: this only lets go of the change the new file announced
        return root;
    }
}
