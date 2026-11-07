using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Apogee.Content;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;
using Apogee.Core.Logging;
using Apogee.Core.Numerics;
using Apogee.Ecs;
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
        History.Changed += () => Dirty = true;
    }

    public string GameFolder { get; }

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

    /// <summary>The selected entity, or none. An entity that was destroyed by something else counts as none.</summary>
    public Entity? Selection
    {
        get => _selection is { } e && World.IsAlive(e) ? e : null;
        set => _selection = value;
    }

    private Entity? _selection;

    /// <summary>The virtual path of the scene file (<c>res://scenes/level.ascene</c>), or null for one that was never saved.</summary>
    public string? ScenePath { get; private set; }

    /// <summary>The scene has changes that are not in its file.</summary>
    public bool Dirty { get; private set; }

    /// <summary>The last problem Play had (a script that failed): shown by the editor, cleared by the next Play.</summary>
    public string? PlayError { get; private set; }

    /// <summary>Raised before the session is replaced, while it still exists: whatever was made from it (renderers, the models on the GPU) lets go first.</summary>
    public event Action? SessionReplacing;

    /// <summary>Raised after the session was replaced (Play, Stop): what was made from the old one (renderers, the world they read) has to be made again.</summary>
    public event Action? SessionReplaced;

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
        Selection = null;
        History.Clear();
        Dirty = false;
    }

    /// <summary>Opens the scene file at <paramref name="path"/>, replacing the entities in the world.</summary>
    public void OpenScene(string path)
    {
        StopIfPlaying();
        string text = Session.Files!.ReadAllText(path);
        foreach (Entity root in World.Roots())
        {
            World.Destroy(root);
        }

        World.LoadScene(text);
        ScenePath = path;
        Selection = null;
        History.Clear();
        Dirty = false;
        Log.Info("Editor", $"Opened {path}");
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
        Session.Files!.WriteAllText(path, World.SaveScene());
        ScenePath = path;
        Dirty = false;
        Log.Info("Editor", $"Saved {path}");
    }

    /// <summary>The scene files of the game (every <c>.ascene</c> under <c>res://</c>), as virtual paths.</summary>
    public List<string> SceneFiles() => [.. Session.Files!.EnumerateFiles("res://", "*.ascene", recursive: true).Order(StringComparer.Ordinal)];

    // ---- Play ----

    /// <summary>Runs the game's packages over a copy of the scene. Returns false (and says why in <see cref="PlayError"/>) when the game's scripts failed: the editor is back in editing, as it was.</summary>
    public bool Play()
    {
        if (IsPlaying)
        {
            return true;
        }

        PlayError = null;
        Entity? selected = _selection;      // kept as it is, alive or not: the entity can be gone in the play world and back after Stop
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
            _selection = selected;
            _beforePlay = null;
            SessionReplaced?.Invoke();
            return false;
        }

        IsPlaying = true;
        IsPaused = false;
        _selection = selected;
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

        Entity? selected = _selection;
        SessionReplacing?.Invoke();
        Session.Dispose();
        IsPlaying = false;
        IsPaused = false;
        Session = Start(runScripts: false, scene: null, realAudio: false);
        Session.World.Restore(_beforePlay!);
        _beforePlay = null;
        _selection = selected;
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
        Session.Reload();
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

    public Entity Duplicate(Entity entity)
    {
        var command = new CreateEntityCommand(_worldRef, "Duplicate " + (World.GetName(entity) ?? "entity"), () => World.Clone(entity));
        History.Execute(command);
        History.Seal();
        Selection = command.Entity;
        return command.Entity;
    }

    public void Delete(Entity entity)
    {
        if (Selection is { } selected && World.Subtree(entity).Contains(selected))
        {
            Selection = null;
        }

        History.Execute(new DestroyEntityCommand(_worldRef, entity));
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

    public bool Reparent(Entity entity, Entity parent)
    {
        if (!ReparentCommand.IsAllowed(World, entity, parent) || World.GetParent(entity) == parent)
        {
            return false;
        }

        // A name that a sibling under the new parent already has is made unique first, in the same step.
        string? name = World.GetName(entity);
        IEnumerable<Entity> siblings = parent.IsNull ? World.Roots() : World.Children(parent);
        var steps = new List<IEditCommand>();
        if (name is not null && siblings.Any(s => s != entity && World.GetName(s) == name))
        {
            steps.Add(new RenameCommand(_worldRef, entity, UniqueName(name, parent)));
        }

        steps.Add(new ReparentCommand(_worldRef, entity, parent));
        History.Execute(new CompositeCommand("Move " + (name ?? "entity"), steps));
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
        Session.Dispose();
    }
}
