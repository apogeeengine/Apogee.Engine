using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Apogee.Content;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;
using Apogee.Content.Xml;
using Apogee.Core.Logging;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Editor.Assets;
using Apogee.Editor.Editing;
using Apogee.Host;
using Apogee.Host.Editing;
using Apogee.Reflection;
using Apogee.Rendering;
using Apogee.Scripting;

namespace Apogee.Editor;

/// <summary>
/// What the editor has open: a game folder, the session that holds its world, the scene being edited, the selection and the undo history. The session is a game's (the same <see cref="ScriptSession"/> the
/// runtime makes) that runs no scripts while editing. <b>Play</b> keeps a snapshot of the world, makes a new session that runs the game's packages over the scene, and <b>Stop</b> makes an editing
/// session again and puts the snapshot back under the same entity ids, so the selection and the history still point at the same entities. Only one session exists at a time (the UI library and others
/// have one state per process), which is why Play and Stop replace it.
/// </summary>
public sealed class EditorProject : IDisposable
{
    private WorldSnapshot? _beforePlay;
    private readonly WorldRef _worldRef;
    private bool _disposed;

    public EditorProject(string gameFolder, string? userFolder = null)
    {
        GameFolder = Path.GetFullPath(gameFolder);
        UserFolder = userFolder;
        _worldRef = new WorldRef(() => Session?.World ?? throw new InvalidOperationException("The project has no session."));
        Session = Start(runScripts: false, scene: null, realAudio: false);
        Selected = new Selection(() => World);
    }

    public string GameFolder { get; }

    /// <summary>The project was disposed: its session is gone (see <see cref="Dispose"/>), and the editor no longer shows it.</summary>
    public bool IsDisposed => _disposed;

    public string? UserFolder { get; }

    /// <summary>The current session: an editing one, or while playing the game's.</summary>
    public ScriptSession Session { get; private set; }

    /// <summary>The world as commands look it up: it follows the session when Play and Stop replace it.</summary>
    public WorldRef WorldRef => _worldRef;

    public World World => Session.World;

    public bool IsPlaying { get; private set; }

    /// <summary>While playing: the world is not stepped (but <see cref="Step"/> advances it one frame).</summary>
    public bool IsPaused { get; set; }

    public EditHistory History { get; } = new();

    /// <summary>The selected entities (several can be), kept across Play and Stop. See <see cref="Editing.Selection"/>.</summary>
    public Selection Selected { get; }

    /// <summary>The active entity of <see cref="Selected"/> (the last one picked), or none. Setting it selects only that entity, or clears the selection. An entity that was destroyed counts as none.</summary>
    public Entity? Selection
    {
        get => Selected.Active;
        set
        {
            if (value is { } entity)
            {
                Selected.Set(entity);
            }
            else
            {
                Selected.Clear();
            }
        }
    }

    /// <summary>The virtual path of the scene file (<c>res://scenes/level.ascene</c>), or null for one that was never saved.</summary>
    public string? ScenePath { get; private set; }

    /// <summary>The scene has changes that are not in its file. Undoing back to the saved state clears it (see <see cref="EditHistory.IsAtSavePoint"/>).</summary>
    public bool Dirty => !History.IsAtSavePoint;

    /// <summary>The last problem Play had (a script that failed): shown by the editor, cleared by the next Play.</summary>
    public string? PlayError { get; private set; }

    /// <summary>Raised before the session is replaced, while it still exists: whatever was made from it (renderers, the models on the GPU) lets go first.</summary>
    public event Action? SessionReplacing;

    /// <summary>Raised after the session was replaced (Play, Stop): what was made from the old one (renderers, the world they read) has to be made again.</summary>
    public event Action? SessionReplaced;

    /// <summary>
    /// "An importer is running" (see <see cref="Assets.ImportGate"/>): the hot reloader's import in <see cref="Update"/>, an import job and the asset browser's re-import take it one at a time, because they all
    /// write <c>imported/index.xml</c>.
    /// </summary>
    public ImportGate ImportGate { get; } = new();

    /// <summary>The import of dropped files that is running or has just ended (its report not taken yet), or null. See <see cref="TryStartImport"/>.</summary>
    public ImportJob? Job { get; private set; }

    private ScriptSession Start(bool runScripts, string? scene, bool realAudio) =>
        ScriptSession.StartGame(GameFolder, UserFolder, watch: true, realAudio: realAudio, realSteam: false, runScripts: runScripts, scene: scene);

    // ---- Scenes ----

    /// <summary>Starts an empty scene.</summary>
    public void NewScene()
    {
        StopIfPlaying();
        foreach (Entity root in World.Roots())
        {
            World.Destroy(root);
        }

        ScenePath = null;
        Selected.Clear();
        History.Clear();
        History.MarkSaved();
    }

    /// <summary>
    /// Opens the scene file at <paramref name="path"/>, replacing the entities in the world. When the file cannot be loaded (it does not parse, it is from a newer engine) the exception is thrown and the world, the
    /// scene's path, the history and the selection are exactly as they were: the entities that were taken out come back under the same ids.
    /// </summary>
    public void OpenScene(string path)
    {
        StopIfPlaying();
        if (!Session.Files!.FileExists(World.Assets?.ResolvePath(path) ?? path))
        {
            throw new FileNotFoundException($"The scene {path} does not exist.", path); // before the scene being edited is cleared
        }

        // The file can still fail halfway (a version check, a component): the world is put back from this, so nothing is lost and the next Save cannot write an empty scene over the old one.
        WorldSnapshot before = World.Snapshot();
        foreach (Entity root in World.Roots())
        {
            World.Destroy(root);
        }

        try
        {
            World.LoadSceneFile(path);
        }
        catch
        {
            World.Restore(before);
            throw;
        }

        ScenePath = path;
        Selected.Clear();
        History.Clear();
        History.MarkSaved();
        Log.Info("Editor", $"Opened {path}");
    }

    /// <summary>The problems a scene file's reading or writing can end in: what the editor reports in a message instead of failing.</summary>
    internal static bool IsFileProblem(Exception e) => e is IOException or ContentException or FormatException or UnauthorizedAccessException;

    /// <summary><see cref="OpenScene"/> that reports a file problem in <paramref name="error"/> (the world untouched) instead of throwing it.</summary>
    public bool TryOpenScene(string path, out string? error)
    {
        try
        {
            OpenScene(path);
            error = null;
            return true;
        }
        catch (Exception e) when (IsFileProblem(e))
        {
            Log.Error("Editor", $"Could not open {path}: {e.Message}");
            error = $"The scene {path} could not be opened:\n{e.Message}";
            return false;
        }
    }

    /// <summary>
    /// Saves the scene to its file, or says why not: false with no <paramref name="error"/> when the scene has no file yet (use <see cref="TrySaveSceneAs"/>), false with one when writing failed (a read-only
    /// file or part of the game); the scene is then still modified.
    /// </summary>
    public bool TrySaveScene(out string? error)
    {
        error = null;
        return ScenePath is { } path && TrySaveSceneAs(path, out error);
    }

    /// <summary><see cref="SaveSceneAs"/> that reports a file problem in <paramref name="error"/> instead of throwing it.</summary>
    public bool TrySaveSceneAs(string path, out string? error)
    {
        try
        {
            SaveSceneAs(path);
            error = null;
            return true;
        }
        catch (Exception e) when (IsFileProblem(e))
        {
            Log.Error("Editor", $"Could not save {path}: {e.Message}");
            error = $"The scene could not be saved to {path}:\n{e.Message}";
            return false;
        }
    }

    /// <summary>Saves the scene to its file; false when it has none yet (use <see cref="SaveSceneAs"/>).</summary>
    public bool SaveScene()
    {
        if (ScenePath is null)
        {
            return false;
        }

        SaveSceneAs(ScenePath);
        return true;
    }

    public void SaveSceneAs(string path)
    {
        if (!path.EndsWith(".ascene", StringComparison.OrdinalIgnoreCase))
        {
            path += ".ascene";
        }

        StopIfPlaying();
        World.SaveSceneFile(path);
        ScenePath = path;
        History.MarkSaved();
        Log.Info("Editor", $"Saved {path}");
    }

    /// <summary>The scene files of the game (every <c>.ascene</c> under <c>res://</c>), as virtual paths.</summary>
    public List<string> SceneFiles() => [.. Session.Files!.EnumerateFiles("res://", "*.ascene", recursive: true).Order(StringComparer.Ordinal)];

    // ---- Importing dropped files ----

    /// <summary>
    /// Starts <paramref name="plan"/> on a worker thread: it copies the files into the project and imports them (see <see cref="ImportJob"/>). Refused, with the reason, while playing (the game has the files
    /// open and the session is about to be replaced), while another import is running, or when the project has no writable file system. While the job runs the hot reloader is not polled, so two importers
    /// never write the import index at once; when it ends, the next <see cref="Update"/> lets the reloader catch up (the artifacts are current, so it only reloads what the files changed).
    /// </summary>
    public bool TryStartImport(DropPlan plan, out ImportJob? job, out string? problem)
    {
        job = null;
        if (IsPlaying)
        {
            problem = "Files cannot be added while the game is playing. Stop it first.";
        }
        else if (Session.Files is not { } files)
        {
            problem = "The game has no file system to add files to.";
        }
        else if (!ImportGate.TryEnter())
        {
            problem = "An import is already running. Wait for it to end.";
        }
        else
        {
            problem = null;
            job = Job = new ImportJob(files, ScriptSession.Importers, new ProjectDropTargets(files, GameFolder), plan, ImportGate.Exit);
            job.Start();
        }

        return problem is null;
    }

    /// <summary>Forgets the ended job (its report was taken).</summary>
    public void ReleaseJob()
    {
        if (Job is { IsEnded: true })
        {
            Job = null;
        }
    }

    // ---- Play ----

    /// <summary>Runs the game's packages over a copy of the scene. Returns false (and says why in <see cref="PlayError"/>) when the game's scripts failed: the editor is back in editing, as it was.</summary>
    public bool Play()
    {
        if (IsPlaying)
        {
            return true;
        }

        PlayError = null;
        if (ImportGate.IsHeld)
        {
            PlayError = "Files are being imported. Play when the import has ended.";
            return false;
        }

        // The selection is kept as ids, alive or not: an entity can be gone in the play world and back after Stop.
        _beforePlay = World.Snapshot();
        string scene = World.SaveScene();
        SessionReplacing?.Invoke();
        Session.Dispose();
        try
        {
            Session = Start(runScripts: true, scene: scene, realAudio: true);
        }
        catch (Exception e) when (e is ScriptException or ContentException or IOException)
        {
            PlayError = e.Message;
            Log.Error("Editor", $"Play failed: {e.Message}");
            Session = Start(runScripts: false, scene: null, realAudio: false);
            Session.World.Restore(_beforePlay);
            _beforePlay = null;
            SessionReplaced?.Invoke();
            return false;
        }

        IsPlaying = true;
        IsPaused = false;
        Log.Info("Editor", "Playing");
        SessionReplaced?.Invoke();
        return true;
    }

    /// <summary>Ends the run: the editing session is made again and the world is put back as it was when Play was pressed.</summary>
    public void Stop()
    {
        if (!IsPlaying)
        {
            return;
        }

        SessionReplacing?.Invoke();
        Session.Dispose();
        IsPlaying = false;
        IsPaused = false;
        Session = Start(runScripts: false, scene: null, realAudio: false);
        Session.World.Restore(_beforePlay!);
        _beforePlay = null;
        Log.Info("Editor", "Stopped");
        SessionReplaced?.Invoke();
    }

    private void StopIfPlaying()
    {
        if (IsPlaying)
        {
            Stop();
        }
    }

    /// <summary>One frame of a paused run.</summary>
    public void Step(float deltaSeconds)
    {
        if (IsPlaying)
        {
            Session.Reload();
            Session.Progress(deltaSeconds);
        }
    }

    /// <summary>Called every frame: a run that is not paused advances; an editing session lets assets that changed on disk reload.</summary>
    public void Update(float deltaSeconds)
    {
        // The reloader imports what changed, on this thread; an import job imports on its own. Never both: while the job holds the gate the reloader waits (what it noticed is kept for later).
        if (ImportGate.TryEnter())
        {
            try
            {
                Session.Reload();
            }
            finally
            {
                ImportGate.Exit();
            }
        }

        if (IsPlaying && !IsPaused)
        {
            Session.Progress(deltaSeconds);
        }
        else if (!IsPlaying)
        {
            // Editing: the transform system keeps WorldTransform up to date for picking and the gizmo, and nothing else runs.
            World.Progress(0f);
        }
    }

    // ---- Edits: each is a command, so each can be undone ----

    /// <summary>A name that no sibling under <paramref name="parent"/> has: <paramref name="baseName"/>, then "baseName 2", "baseName 3".</summary>
    public string UniqueName(string baseName, Entity parent = default)
    {
        IEnumerable<Entity> siblings = parent.IsNull ? World.Roots() : World.Children(parent);
        HashSet<string?> taken = [.. siblings.Select(World.GetName)];
        if (!taken.Contains(baseName))
        {
            return baseName;
        }

        for (int n = 2; ; n++)
        {
            string candidate = $"{baseName} {n}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private Entity Make(string name, Entity parent, Double3 at, Action<Entity>? configure = null)
    {
        Entity entity = World.CreateEntity(name: null);
        if (!parent.IsNull)
        {
            World.SetParent(entity, parent);
        }

        World.SetName(entity, name);
        World.Set(entity, Transform.At(at));
        configure?.Invoke(entity);
        return entity;
    }

    private Entity Create(string commandName, string name, Entity parent, Double3 at, Action<Entity>? configure = null)
    {
        string unique = UniqueName(name, parent);
        var command = new CreateEntityCommand(_worldRef, commandName, () => Make(unique, parent, at, configure));
        History.Execute(command);
        History.Seal();
        Selection = command.Entity;
        return command.Entity;
    }

    public Entity CreateEmpty(Entity parent = default, Double3 at = default) => Create("Create Empty", "Empty", parent, at);

    public Entity CreatePointLight(Entity parent = default, Double3 at = default) =>
        Create("Create Point Light", "Point Light", parent, at, e => World.Set(e, Light.Point(Vector3.One, 80f, 12f)));

    public Entity CreateSun(Entity parent = default) =>
        Create("Create Sun", "Sun", parent, default, e =>
        {
            World.Set(e, new Transform(new Double3(0, 10, 0), Quaternion.CreateFromYawPitchRoll(0.6f, -0.9f, 0f), Vector3.One));
            World.Set(e, Light.Sun(Vector3.One, 3f));
        });

    public Entity CreateCamera(Entity parent = default, Double3 at = default) =>
        Create("Create Camera", "Camera", parent, at == default ? new Double3(0, 2, 6) : at, e =>
        {
            World.Set(e, CameraComponent.Default);
            World.Add<MainCamera>(e);
        });

    /// <summary>An entity that draws the model file at <paramref name="path"/> (a virtual path), at <paramref name="at"/>.</summary>
    public Entity CreateModel(string path, Double3 at, Entity parent = default)
    {
        uint id = Session.Models.Load(path);
        string name = UniqueName(VfsPath.FileName(path) is { } file ? Path.GetFileNameWithoutExtension(file) : "Model", parent);
        return Create("Add " + name, name, parent, at, e => World.Set(e, new ModelRenderer { Model = id }));
    }

    /// <summary>
    /// Duplicates <paramref name="entity"/> with its branch next to it (see <see cref="Duplicate(IReadOnlyList{Entity})"/>), and selects the copy. Returns the copy, or none when nothing was duplicated
    /// (the entity is gone, or the editor is playing).
    /// </summary>
    public Entity Duplicate(Entity entity) => Duplicate([entity]) is [var copy, ..] ? copy : default;

    /// <summary>
    /// Duplicates <paramref name="entities"/> as one step ("Duplicate Cube", "Duplicate 3 entities"): each top-level one is cloned with its branch under the same parent, and the copies become the selection.
    /// An entity under another one of the list is carried by that one. A copy keeps the references its components hold to other entities, the copy's own included (they point at the originals), unlike
    /// a paste, which connects the copies among themselves and drops what points outside. Nothing happens while playing.
    /// </summary>
    public IReadOnlyList<Entity> Duplicate(IReadOnlyList<Entity> entities)
    {
        IReadOnlyList<Entity> top = Editing.Selection.TopLevelOf(World, entities);
        if (top.Count == 0 || IsPlaying)
        {
            return [];
        }

        string name = top.Count == 1 ? "Duplicate " + (World.GetName(top[0]) ?? "entity") : $"Duplicate {top.Count} entities";
        var command = new CreateEntitiesCommand(_worldRef, name, () => [.. top.Select(root => World.Clone(root))]);
        History.Execute(command);
        History.Seal();
        Selected.Set(command.Entities);
        return command.Entities;
    }

    // ---- The clipboard: scene text, so a paste works across projects and editor instances ----

    /// <summary>
    /// Puts the selected entities (the top-level ones, each with its branch) on <paramref name="clipboard"/> as scene text. Works while playing (it copies the running state). Returns how many trees were
    /// copied: 0 when nothing is selected, or the text is too long to paste back (said on the console).
    /// </summary>
    public int Copy(IClipboard clipboard)
    {
        IReadOnlyList<Entity> top = Selected.TopLevel();
        if (top.Count == 0)
        {
            return 0;
        }

        string text = ClipboardScene.Copy(World, top);
        if (text.Length > ClipboardScene.MaxLength)
        {
            Log.Warn("Editor", $"Nothing copied: the scene text of the selection is longer than {ClipboardScene.MaxLength / (1024 * 1024)} MB");
            return 0;
        }

        clipboard.SetText(text);
        return top.Count;
    }

    /// <summary>Copies the selection (<see cref="Copy"/>) and deletes it as one step. Nothing happens while playing, or when nothing was copied. Returns how many trees were cut.</summary>
    public int Cut(IClipboard clipboard)
    {
        if (IsPlaying)
        {
            return 0;
        }

        int copied = Copy(clipboard);
        if (copied > 0)
        {
            Delete(Selected.TopLevel());
        }

        return copied;
    }

    /// <summary>
    /// Makes the entities of the scene text on <paramref name="clipboard"/> as one step ("Paste Cube", "Paste 3 entities") and selects them. They go next to the active entity (under its parent; the
    /// root with no selection), or under <paramref name="parent"/> when one is given (<c>default</c> is the root). Names that a sibling has are made unique ("Cube 2"); entity references among the pasted
    /// entities are kept and the ones to anything else are empty; assets are found again by their path and UID. Text that is not a scene changes nothing and says why on the console. Nothing happens
    /// while playing. Returns the new top-level entities.
    /// </summary>
    public IReadOnlyList<Entity> Paste(IClipboard clipboard, Entity? parent = null)
    {
        if (IsPlaying)
        {
            return [];
        }

        if (!ClipboardScene.TryRead(clipboard.GetText(), out XmlNode scene, out string problem))
        {
            Log.Warn("Editor", $"Nothing pasted: {problem}");
            return [];
        }

        List<XmlNode> tops = [.. scene.Children.Where(node => node.Name == "Entity" && !node.HasAttribute("prefab"))];
        if (tops.Count == 0)
        {
            Log.Warn("Editor", "Nothing pasted: the scene on the clipboard has no entities");
            return [];
        }

        Entity under = parent ?? (Selected.Active is { } active ? World.GetParent(active) : default);
        if (!under.IsNull && !World.IsAlive(under))
        {
            under = default;
        }

        string name = tops.Count == 1 ? "Paste " + (tops[0].Attribute("name") ?? "entity") : $"Paste {tops.Count} entities";
        var command = new CreateEntitiesCommand(_worldRef, name, () =>
        {
            List<Entity> roots = ClipboardScene.RootsOf(World, World.LoadScene(scene, under), under);
            NameUniquely(roots, [.. tops.Select(node => node.Attribute("name"))], under);
            return roots;
        });
        try
        {
            History.Execute(command);
        }
        catch (FormatException e)
        {
            Log.Warn("Editor", $"Nothing pasted: {e.Message}");
            return [];
        }

        History.Seal();
        Selected.Set(command.Entities);
        return command.Entities;
    }

    // Gives pasted roots the names the scene text had (`names`, in the same order), made unique among the siblings that were there and the roots before them. The scene loader checks a name with a lookup
    // that also finds the engine's own entities (there is one called "Empty"), so it can turn a free name into "Empty 2"; here the siblings' names decide.
    private void NameUniquely(List<Entity> roots, IReadOnlyList<string?> names, Entity under)
    {
        if (roots.Count != names.Count)
        {
            return;
        }

        IEnumerable<Entity> siblings = under.IsNull ? World.Roots() : World.Children(under);
        HashSet<string?> taken = [.. siblings.Where(s => !roots.Contains(s)).Select(World.GetName)];
        for (int i = 0; i < roots.Count; i++)
        {
            if (names[i] is not { Length: > 0 } baseName)
            {
                continue;
            }

            string chosen = baseName;
            for (int n = 2; taken.Contains(chosen); n++)
            {
                chosen = $"{baseName} {n}";
            }

            taken.Add(chosen);
            if (World.GetName(roots[i]) != chosen)
            {
                World.SetName(roots[i], chosen);
            }
        }
    }

    /// <summary>
    /// Makes one entity per model file, each at its position, as one step ("Add car", "Add 3 models"), and selects them. A model that does not load is left out with a message on the console. Nothing
    /// happens while playing. Returns the new entities.
    /// </summary>
    public IReadOnlyList<Entity> CreateModels(IReadOnlyList<(string Path, Double3 At)> models)
    {
        if (IsPlaying)
        {
            return [];
        }

        var loaded = new List<(uint Id, string Name, Double3 At)>();
        foreach ((string path, Double3 at) in models)
        {
            try
            {
                loaded.Add((Session.Models.Load(path), VfsPath.FileName(path) is { } file ? Path.GetFileNameWithoutExtension(file) : "Model", at));
            }
            catch (Exception e) when (e is ContentException or IOException)
            {
                Log.Error("Editor", $"{path} could not be loaded: {e.Message}");
            }
        }

        if (loaded.Count == 0)
        {
            return [];
        }

        string name = loaded.Count == 1 ? "Add " + loaded[0].Name : $"Add {loaded.Count} models";
        var command = new CreateEntitiesCommand(_worldRef, name, () => [.. loaded.Select(m => Make(UniqueName(m.Name), default, m.At, e => World.Set(e, new ModelRenderer { Model = m.Id })))]);
        History.Execute(command);
        History.Seal();
        Selected.Set(command.Entities);
        return command.Entities;
    }

    public void Delete(Entity entity) => Delete([entity]);

    /// <summary>
    /// Deletes <paramref name="entities"/> with their branches as one step ("Delete 3 entities"; a single one is "Delete Cube"). An entity whose ancestor is in the list goes with that ancestor, once. What
    /// was deleted leaves the selection.
    /// </summary>
    public void Delete(IReadOnlyList<Entity> entities)
    {
        IReadOnlyList<Entity> top = Editing.Selection.TopLevelOf(World, entities);
        if (top.Count == 0)
        {
            return;
        }

        foreach (Entity root in top)
        {
            foreach (Entity gone in World.Subtree(root))
            {
                Selected.Remove(gone);
            }
        }

        if (top.Count == 1)
        {
            History.Execute(new DestroyEntityCommand(_worldRef, top[0]));
        }
        else
        {
            History.Execute(new CompositeCommand($"Delete {top.Count} entities", [.. top.Select(e => new DestroyEntityCommand(_worldRef, e))]));
        }

        History.Seal();
    }

    public bool Rename(Entity entity, string name)
    {
        if (name.Length == 0 || name == World.GetName(entity) || !RenameCommand.IsFree(World, entity, name))
        {
            return false;
        }

        History.Execute(new RenameCommand(_worldRef, entity, name));
        History.Seal();
        return true;
    }

    public bool Reparent(Entity entity, Entity parent) => Reparent([entity], parent);

    /// <summary>
    /// Makes <paramref name="entities"/> children of <paramref name="parent"/> (none: roots) as one step. An entity under another one of the list moves with it, an entity that cannot go there (under
    /// itself) or is there already is left out; false when none moves. Names that a sibling under the new parent has (or that another moved entity just took) are made unique in the same step.
    /// </summary>
    public bool Reparent(IReadOnlyList<Entity> entities, Entity parent)
    {
        IEnumerable<Entity> siblings = parent.IsNull ? World.Roots() : World.Children(parent);
        HashSet<string?> taken = [.. siblings.Select(World.GetName)];
        var steps = new List<IEditCommand>();
        int moved = 0;
        string? firstName = null;
        foreach (Entity entity in Editing.Selection.TopLevelOf(World, entities))
        {
            if (!ReparentCommand.IsAllowed(World, entity, parent) || World.GetParent(entity) == parent)
            {
                continue;
            }

            // A name that a sibling under the new parent already has is made unique first.
            string? name = World.GetName(entity);
            if (name is not null && taken.Contains(name))
            {
                int n = 2;
                while (taken.Contains($"{name} {n}"))
                {
                    n++;
                }

                steps.Add(new RenameCommand(_worldRef, entity, $"{name} {n}"));
                name = $"{name} {n}";
            }

            taken.Add(name);
            steps.Add(new ReparentCommand(_worldRef, entity, parent));
            if (moved++ == 0)
            {
                firstName = World.GetName(entity);
            }
        }

        if (moved == 0)
        {
            return false;
        }

        History.Execute(new CompositeCommand(moved == 1 ? "Move " + (firstName ?? "entity") : $"Move {moved} entities", steps));
        History.Seal();
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Job is { } job)
        {
            // The worker uses the project's files: it is stopped (at its next file) and waited for before the session goes.
            job.Cancel();
            if (!job.Wait(TimeSpan.FromMinutes(5)))
            {
                Log.Error("Editor", "The import did not end within five minutes; the project is closed anyway.");
            }
        }

        Session.Dispose();
    }
}
