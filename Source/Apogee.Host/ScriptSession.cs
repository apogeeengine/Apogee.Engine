using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using Apogee.Content;
using Apogee.Content.Assets;
using Apogee.Content.FileSystem;
using Apogee.Core.Graphics;
using Apogee.Content.Import;
using Apogee.Content.Packages;
using Apogee.Content.Packs;
using Apogee.Core.Logging;
using Apogee.DevTools;
using Apogee.Rendering.Shaders;
using Apogee.Ecs;
using Apogee.Audio;
using Apogee.Network;
using Apogee.Input;
using Apogee.Physics;
using Apogee.Rendering;
using Apogee.UI;
using Apogee.Steam;
using Apogee.Graphics.Shaders;
using Apogee.Scripting;

namespace Apogee.Host;

/// <summary>A script running in a world: the entry script has been executed and its callbacks are registered; the loops step it frame by frame.</summary>
public sealed class ScriptSession : IDisposable
{
    private readonly LuauVm _vm;
    private readonly IDisposable? _owned;

    /// <summary>Makes sure the libraries scripts see are registered: they register from their assemblies' module initializers, at startup under NativeAOT and on first use otherwise.</summary>
    public static void RegisterLibraries()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(ImGuiLibrary).Module.ModuleHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(Apogee.Input.InputActionMap).Module.ModuleHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(PhysicsWorld).Module.ModuleHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(AudioSystem).Module.ModuleHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(SteamSystem).Module.ModuleHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(NetSystem).Module.ModuleHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(ModelLibrary).Module.ModuleHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(UiSystem).Module.ModuleHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(GameLibrary).Module.ModuleHandle);
    }

    /// <summary>A scene holds a model's path and UID, not its number in this run's library; and a physics body only exists in the run that made it (scripts make them again).</summary>
    private static void RegisterSceneSupport(World world, VirtualFileSystem files, ModelLibrary models, AssetDatabase assets)
    {
        world.Files = files;
        world.Assets = assets;
        world.RegisterAssetType("model", id => models.PathOf(id) is { } path ? assets.RefOf(path) : null, reference => assets.Resolve(reference) is { } path ? models.Load(path) : 0);
        world.IgnoreInScenes("Body");
    }

    private readonly List<World> _editingWorlds = [];
    private bool _disposed;

    /// <summary>
    /// Makes another world for a tool to edit in, next to <see cref="World"/>: the same files, asset database, models and prefab library (so that a model or a prefab has one number in both, and a prefab
    /// saved in one is read by the other), and nothing else of the session. Scripts do not see it (it is not the <c>world</c> global, and no scripts, physics or audio run on it), <see cref="Progress"/>
    /// does not step it (a tool steps it, with <c>Progress(0)</c> to compose transforms), and it is disposed with the session. A tool that is done with it disposes it sooner.
    /// </summary>
    /// <returns>An empty world: <see cref="Editing.PrefabStage.Open"/> fills one with a prefab.</returns>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    public World CreateEditingWorld()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _editingWorlds.RemoveAll(static world => world.IsDestroyed); // the ones a tool has finished with
        var editing = new World();
        try
        {
            RegisterSceneSupport(editing, World.Files!, Models, World.Assets!);
            editing.Prefabs = World.Prefabs;
        }
        catch
        {
            editing.Dispose();
            throw;
        }

        _editingWorlds.Add(editing);
        return editing;
    }

    /// <summary>Makes the <c>assets</c> global (the engine owns it). The packages that load (in load order, after <c>res://</c>) are where a UID is looked for.</summary>
    private static AssetLibrary InstallAssets(LuauVm vm, ContentLoader content, PackageSet? packages = null)
    {
        foreach (SolvedPackage package in packages?.LoadOrder ?? [])
        {
            content.Assets.Roots.Add($"mod://{package.Id}/");
        }

        var assets = new AssetLibrary(content.Assets);
        assets.Adopt();
        vm.SetGlobal("assets", assets);
        return assets;
    }

    /// <summary>Everything a session can import: models, images and shaders.</summary>
    public static IReadOnlyList<IAssetImporter> Importers { get; } = [.. AssetImporter.Defaults, new ShaderImporter()];

    private ScriptSession(World world, LuauVm vm, InputActionRouter input, PhysicsWorld physics, AudioSystem audio, SteamSystem steam, NetSystem net, ModelLibrary models, UiSystem ui, AssetLibrary assets, SettingsStore settings, IDisposable? owned = null)
    {
        World = world;
        Settings = settings;
        Ui = ui;
        Models = models;
        Assets = assets;

        DebugDrawLibrary.Target = DebugDraw;
        _vm = vm;
        Input = input;
        Physics = physics;
        Audio = audio;
        Steam = steam;
        Net = net;
        _owned = owned;
    }

    /// <summary>
    /// The debug lines and shapes of this frame: scripts add to it (<c>debug_draw</c>), and so does the physics world when its <see cref="PhysicsWorld.DebugDraw"/> is on. The loops draw it with the world and
    /// <see cref="EndFrame"/> clears it.
    /// </summary>
    public DebugDraw DebugDraw { get; } = new();

    /// <summary>
    /// The player's settings, the scripts' <c>settings</c> global: <c>user://settings.xml</c> of a game, in memory for a single script. The engine's keys (window, graphics, audio) are defined, and the
    /// volumes and the input bindings follow them. It is saved a second after a change, and when the session ends.
    /// </summary>
    public SettingsStore Settings { get; }

    /// <summary>
    /// Makes the settings scripts see as <c>settings</c> (the engine owns them: they live until the session ends): the engine's keys defined with the window <paramref name="defaults"/>, the input map's
    /// rebindings kept in them, and the audio volumes following them.
    /// </summary>
    private static SettingsStore InstallSettings(LuauVm vm, SettingsStore settings, InputActionRouter input, AudioSystem audio, EngineSettingDefaults defaults)
    {
        settings.Adopt();
        EngineSettings.Register(settings, defaults);
        settings.Bindings = input.Map;
        EngineSettings.ConnectAudio(settings, audio);
        vm.SetGlobal("settings", settings);
        return settings;
    }

    /// <summary>The game's user interface, the scripts' <c>ui</c> global. The loops size it to the screen, feed it input, step it with the world and draw it over the picture.</summary>
    public UiSystem Ui { get; }

    /// <summary>Makes the UI system scripts see as <c>ui</c> (the engine owns it: it lives until the session ends). RmlUi has one state per process, so there is one session at a time.</summary>
    private static UiSystem InstallUi(LuauVm vm, VirtualFileSystem files, ContentLoader content)
    {
        var ui = new UiSystem(files, content);
        ui.Adopt();
        ui.ScriptEnvironments = vm; // <script> and onclick in documents run as the script that loaded them
        vm.SetGlobal("ui", ui);
        return ui;
    }

    /// <summary>The models entities draw, the scripts' <c>models</c> global. The loops attach a renderer to it when there is a GPU.</summary>
    public ModelLibrary Models { get; }

    /// <summary>Asset paths and UIDs, the scripts' <c>assets</c> global.</summary>
    public AssetLibrary Assets { get; }

    /// <summary>Makes the model library scripts see as <c>models</c> (the engine owns it: it lives until the session ends). Its ids exist before there is a GPU.</summary>
    private static ModelLibrary InstallModels(LuauVm vm, VirtualFileSystem files, ContentLoader content)
    {
        var models = new ModelLibrary(files, content);
        models.Adopt();
        vm.SetGlobal("models", models);
        return models;
    }

    /// <summary>The game's Steam connection, the scripts' <c>steam</c> global; inactive when Steam is not running or this run does not use it.</summary>
    public SteamSystem Steam { get; }

    /// <summary>
    /// Makes the Steam system scripts see as <c>steam</c>. Only a game that really runs (<paramref name="options"/> enabled) connects to Steam; a script or a test gets an inactive one, whose
    /// calls do nothing.
    /// </summary>
    private static SteamSystem InstallSteam(LuauVm vm, SteamOptions options)
    {
        var steam = new SteamSystem(options);
        steam.Adopt();
        vm.SetGlobal("steam", steam);
        return steam;
    }

    /// <summary>The game's network, the scripts' <c>net</c> global; inactive when GameNetworkingSockets is not installed.</summary>
    public NetSystem Net { get; }

    /// <summary>Makes the network system scripts see as <c>net</c> (the engine owns it: it lives until the session ends). It opens no socket until a script listens or connects.</summary>
    private static NetSystem InstallNet(LuauVm vm)
    {
        var net = new NetSystem();
        net.Adopt();
        vm.SetGlobal("net", net);
        return net;
    }

    /// <summary>The game's sound, the scripts' <c>audio</c> global; inactive when OpenAL Soft is not installed.</summary>
    public AudioSystem Audio { get; }

    /// <summary>
    /// Makes the audio system scripts see as <c>audio</c>. It plays on the sound device only when a game is really running (<paramref name="realAudio"/>): headless runs and tests mix into nowhere.
    /// </summary>
    private static AudioSystem InstallAudio(LuauVm vm, World world, PhysicsWorld physics, VirtualFileSystem? files, bool realAudio)
    {
        var audio = new AudioSystem(world, new AudioOptions { Output = realAudio ? AudioOutput.Default : AudioOutput.None }, files) { Physics = physics };
        audio.Adopt();
        vm.SetGlobal("audio", audio);
        return audio;
    }

    public World World { get; }

    /// <summary>The physics of <see cref="World"/>, the scripts' <c>physics</c> global.</summary>
    public PhysicsWorld Physics { get; }

    /// <summary>Makes the physics world scripts see as <c>physics</c> (the engine owns it: it lives until the session ends).</summary>
    private static PhysicsWorld InstallPhysics(LuauVm vm, World world)
    {
        var physics = new PhysicsWorld(world);
        physics.Adopt();
        vm.SetGlobal("physics", physics);
        return physics;
    }

    /// <summary>The router behind the scripts' <c>input</c> global: <see cref="InputDevices"/> feeds it, the scripts define the actions on its map.</summary>
    public InputActionRouter Input { get; }

    /// <summary>Makes the router and map that scripts see as <c>input</c> (the engine owns them: they live until the session ends).</summary>
    private static InputActionRouter InstallInput(LuauVm vm)
    {
        var router = new InputActionRouter { Map = new InputActionMap() };
        router.Adopt();
        vm.SetGlobal("input", router);
        return router;
    }

    /// <summary>The game's files (<c>res://</c>, <c>mod://</c>, the engine's shaders as <c>shaders://</c>), or null for a single script.</summary>
    public VirtualFileSystem? Files { get; private init; }

    /// <summary>
    /// The shaders for this session's renderers. A game's come from its own file system, so that its mods' compiled shaders are found, and when it is watched a change to a shader
    /// file rebuilds the pipelines made from it; a single script gets the engine's shaders next to the program.
    /// </summary>
    public ShaderLibrary CreateShaderLibrary(string cacheDirectory)
    {
        if (Files is null)
        {
            return ShaderLibrary.Standard(cacheDirectory);
        }

        var library = new ShaderLibrary(Files, new ShaderCompilerOptions { CacheDirectory = cacheDirectory });
        if (Reloader is not null)
        {
            Reloader.ShadersChanged += library.NotifyChanged;
        }

        return library;
    }

    /// <summary>
    /// Reads the prefab files among <paramref name="paths"/> again and makes the instances in the world follow the ones that changed (<see cref="World.ApplyPrefabUpdates"/>), unless the host
    /// holds updates (<see cref="World.HoldPrefabUpdates"/>: it applies them itself). A file that is not usable keeps the version the world has, and says why in the log.
    /// </summary>
    private static void ReloadPrefabs(World world, IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            if (!path.EndsWith(".aprefab", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                PrefabReload result = world.Prefabs.Reload(path, world.MaxLoadEntities);
                if (result.Status == PrefabReloadStatus.Failed)
                {
                    Log.Error("HotReload", $"prefab {path} not reloaded, the old version stays: {result.Error}");
                }
                else if (result.Status == PrefabReloadStatus.Reloaded)
                {
                    Log.Info("HotReload", $"prefab {path} reloaded");
                }
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException)
            {
                Log.Error("HotReload", $"prefab {path} not reloaded: {e.Message}");
            }
        }

        if (!world.HoldPrefabUpdates)
        {
            world.ApplyPrefabUpdates();
        }
    }

    /// <summary>Reloads what changed on disk (a game started with watching), or null.</summary>
    public HotReloader? Reloader { get; private init; }

    /// <summary>Applies the changes to files since the last frame, if the game is being watched. What it reloaded, or null when the game is not watched.</summary>
    public ReloadReport? Reload() => Reloader?.Poll();

    /// <summary>Runs the script at <paramref name="path"/> (a full path with forward slashes) in a new world, which it sees as the <c>world</c> global.</summary>
    public static ScriptSession Start(string path, bool realAudio = false)
    {
        ScriptGui.Clear();
        GameLibrary.Reset();
        var world = new World();
        var vm = new LuauVm(new LuauVmOptions { TimeBudgetMilliseconds = 5000 });
        PhysicsWorld? physics = null;
        AudioSystem? audio = null;
        SteamSystem? steam = null;
        NetSystem? net = null;
        ModelLibrary? models = null;
        UiSystem? ui = null;
        SettingsStore? settings = null;
        try
        {
            vm.AddRoot(Path.GetDirectoryName(path)!.Replace('\\', '/'));
            vm.SetGlobal("world", world);
            InputActionRouter input = InstallInput(vm);
            physics = InstallPhysics(vm, world);
            audio = InstallAudio(vm, world, physics, null, realAudio);
            steam = InstallSteam(vm, new SteamOptions { Enabled = false });
            net = InstallNet(vm);
            var nothing = new VirtualFileSystem(); // a single script has no game folder, so there are no model files for it to name
            var nothingContent = new ContentLoader(nothing);
            models = InstallModels(vm, nothing, nothingContent);
            ui = InstallUi(vm, nothing, nothingContent);
            RegisterSceneSupport(world, nothing, models, nothingContent.Assets);
            AssetLibrary assets = InstallAssets(vm, nothingContent);
            settings = InstallSettings(vm, new SettingsStore(), input, audio, new EngineSettingDefaults()); // a single script keeps no settings between runs
            vm.Execute(vm.BaseOwner, "@" + path, File.ReadAllText(path, Encoding.UTF8));
            return new ScriptSession(world, vm, input, physics, audio, steam, net, models, ui, assets, settings);
        }
        catch
        {
            vm.Dispose();
            settings?.Destroy();
            ui?.Dispose();
            models?.Dispose();
            net?.Dispose();
            steam?.Dispose();
            audio?.Dispose();
            physics?.Dispose();
            world.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Starts a game: <paramref name="folder"/> is <c>res://</c>, <paramref name="userFolder"/> (or the platform's folder for the game's name) is <c>user://</c>. The packages of the game and
    /// the player's mods are found and solved, their definitions loaded, and their scripts run in a world, as <see cref="PackageLoader"/> describes. A mod that fails is logged and left out; a
    /// base package that fails is an error of the game (<see cref="ScriptException"/>).
    /// </summary>
    /// <param name="runScripts">False makes everything a game has (the world, the file system, the models, physics, audio, the UI) without running the packages' scripts: what an editor edits before Play.</param>
    /// <param name="scene">Scene text (<see cref="World.SaveScene()"/>) loaded into the world before the scripts run: what Play starts from.</param>
    public static ScriptSession StartGame(string folder, string? userFolder, bool watch = false, bool realAudio = false, bool realSteam = false, bool runScripts = true, string? scene = null) =>
        StartGame(new GameStart { Folder = folder, UserFolder = userFolder, Watch = watch, RealAudio = realAudio, RealSteam = realSteam, RunScripts = runScripts, Scene = scene });

    /// <summary>
    /// The folder of the player's files (<c>user://</c>) of the game in <paramref name="folder"/>: <paramref name="userFolder"/> when one is given, else the one <paramref name="manifest"/> names
    /// (<see cref="GameManifest.UserDirectory"/>), else one named after the game folder.
    /// </summary>
    /// <param name="manifest">The game's <c>game.xml</c>; null when it has none.</param>
    public static string UserDirectoryFor(string folder, string? userFolder, GameManifest? manifest)
    {
        if (userFolder is not null)
        {
            return userFolder;
        }

        return manifest is not null
            ? manifest.UserDirectory()
            : VirtualFileSystem.DefaultUserDirectory("Apogee", Path.GetFileName(Path.GetFullPath(folder).TrimEnd('/', '\\')));
    }

    /// <summary>
    /// Starts a game as <see cref="StartGame(string, string?, bool, bool, bool, bool, string?)"/> does, and reads its <c>game.xml</c> if it has one: the user folder (unless one is given),
    /// the Steam app id and the version that the packages' <c>gameVersion</c> are checked against come from it, and so does the entry scene.
    /// </summary>
    /// <exception cref="ContentException">The game's <c>game.xml</c> is wrong, or its entry scene is missing.</exception>
    public static ScriptSession StartGame(GameStart start)
    {
        string folder = start.Folder;
        bool watch = start.Watch;
        if (start.PacksOnly && watch)
        {
            throw new ArgumentException("A game that is only its packs cannot be watched: there are no loose files to watch.", nameof(start));
        }

        GameManifest? manifest = GameManifest.Load(folder, out IReadOnlyList<string> manifestProblems);
        if (manifestProblems.Count > 0)
        {
            throw new ContentException($"{GameManifest.FileName} of {folder} is wrong:{Environment.NewLine}{string.Join(Environment.NewLine, manifestProblems)}");
        }

        RegisterLibraries();
        ScriptGui.Clear();
        GameLibrary.Reset();
        var files = new VirtualFileSystem();
        if (!start.PacksOnly)
        {
            files.Mount("res://", new DirectorySource(folder, writable: watch)); // a watching run imports changed assets into res://imported
        }

        ShaderLibrary.MountEngineShaders(files, sources: watch); // the engine's shaders: compiled next to the program, or when watching the source files, so that editing them rebuilds the pipelines
        var packs = new List<PackSource>();
        PackKey? key = start.Key ?? PackKey.FromEnvironment();
        foreach (string pack in FindPacks(folder))
        {
            PackSource source = PackSource.Open(pack, key);
            packs.Add(source);
            files.Mount("res://", source);
            Log.Info("Packs", $"{Path.GetFileName(pack)}: {source.FileCount} files in {source.PartCount} part(s){(source.IsEncrypted ? ", encrypted" : "")}");
        }

        string user = UserDirectoryFor(folder, start.UserFolder, manifest);
        if (start.UserFolder is null && manifest is null)
        {
            Log.Info("Runtime", $"No {GameManifest.FileName} in {folder}: the player's files go in {user}, named after the folder.");
        }

        Directory.CreateDirectory(user);
        files.Mount("user://", new DirectorySource(user, writable: true));

        // Steam first: the cloud and the Workshop's mods are file sources, and the mods are found with the others. A game that Steam relaunches (SteamSystem.RestartRequested) stops here.
        uint appId = SteamAppIdFor(manifest, folder, out string? appIdWarning);
        if (appIdWarning is not null)
        {
            Log.Warn("Steam", appIdWarning);
        }

        var steamOptions = new SteamOptions { Enabled = start.RealSteam, AppId = appId, RestartIfNecessary = !watch };
        var steamSystem = new SteamSystem(steamOptions);
        PackageSet packages;
        try
        {
            files.Mount("cloud://", steamSystem.CreateCloudSource(Path.Combine(user, "cloud")));
            var packageOptions = new PackageOptions { GameVersion = manifest?.Version };
            if (steamSystem.IsActive)
            {
                files.Mount("workshop://", new WorkshopSource(steamSystem.InstalledWorkshopItems()));
                packageOptions = packageOptions with { ExtraUserModsPaths = ["workshop://"] };
            }

            packages = PackageSet.Load(files, packageOptions);
        }
        catch
        {
            steamSystem.Dispose();
            throw;
        }

        var owned = new Owned(packages, packs);
        LuauVm? vm = null;
        World? world = null;
        PhysicsWorld? physics = null;
        AudioSystem? audio = null;
        NetSystem? net = null;
        ModelLibrary? models = null;
        UiSystem? uiSystem = null;
        AssetLibrary? assets = null;
        SettingsStore? settings = null;
        HotReloader? reloader = null;
        try
        {
            foreach (SolvedPackage package in packages.Packages)
            {
                Log.Info("Packages", package.Enabled ? $"{package.Id} {package.Manifest.Version} ({package.Kind.ToString().ToLowerInvariant()}, {package.Location})" : $"{package.Id} {package.Manifest.Version} does not load: {package.Reason}");
            }

            foreach (string problem in packages.Problems)
            {
                Log.Warn("Packages", problem);
            }

            Apogee.Content.Xml.DefDatabase defs = packages.LoadDefs();
            foreach (Apogee.Content.Xml.SchemaProblem problem in defs.Problems)
            {
                if (problem.Severity == Apogee.Content.Xml.ProblemSeverity.Error)
                {
                    Log.Error("Content", problem.ToString());
                }
                else
                {
                    Log.Warn("Content", problem.ToString());
                }
            }

            var loader = new PackageLoader(files, packages);
            world = new World();
            vm = new LuauVm(new LuauVmOptions
            {
                TimeBudgetMilliseconds = 5000,
                Files = new VfsScriptFiles(files),
                FileSystem = files,
                OwnerOfModule = loader.OwnerOfModule,
                CanLoadModule = loader.CanLoadModule,
                CanAccessFile = loader.CanAccessFile,
            });
            vm.Defs = defs;
            vm.SetGlobal("world", world);
            InputActionRouter input = InstallInput(vm);
            physics = InstallPhysics(vm, world);
            audio = InstallAudio(vm, world, physics, files, start.RealAudio);
            steamSystem.Adopt();
            vm.SetGlobal("steam", steamSystem);
            net = InstallNet(vm);
            var content = new ContentLoader(files);
            models = InstallModels(vm, files, content);
            uiSystem = InstallUi(vm, files, content);
            RegisterSceneSupport(world, files, models, content.Assets);
            assets = InstallAssets(vm, content, packages);

            // The player's settings before any script runs: scripts define their keys at load and find the saved values waiting. The window of game.xml is what the engine's window keys default to.
            GameWindow window = manifest?.Window ?? GameWindow.Default;
            settings = InstallSettings(vm, SettingsStore.Open(files), input, audio, new EngineSettingDefaults(window.Width, window.Height, window.Fullscreen));
            if (start.Scene is not null)
            {
                world.LoadScene(start.Scene);
            }
            else if (start.RunScripts && manifest?.EntryScene is { } entryScene)
            {
                string entryPath = "res://" + entryScene;
                if (!files.FileExists(entryPath))
                {
                    throw new ContentException($"The entry scene {entryPath} of {GameManifest.FileName} does not exist.");
                }

                world.LoadSceneFile(entryPath);
            }

            if (start.RunScripts)
            {
                foreach (PackageRunResult result in loader.Load(vm))
                {
                    if (result.Package.Kind == PackageKind.Base && !result.Ran)
                    {
                        throw new ScriptException(ScriptErrorKind.Runtime, $"the base package '{result.Package.Id}' {result.Error}");
                    }
                }
            }

            if (watch)
            {
                reloader = new HotReloader(files, vm, packages, importers: Importers);
                owned.Add(reloader);

                // A model whose file changed is read again by the renderer (the importer ran first: the loader looks at its index again).
                ModelLibrary library = models;
                reloader.AssetsChanged += paths =>
                {
                    content.Refresh();
                    foreach (string changed in paths)
                    {
                        try
                        {
                            if (library.Reload(changed))
                            {
                                Log.Info("HotReload", $"model {changed} reloaded");
                            }
                        }
                        catch (Exception e) when (e is not OutOfMemoryException)
                        {
                            Log.Error("HotReload", $"model {changed} not reloaded, the old one stays: {e.Message}");
                        }
                    }
                };
                // A prefab file that changed is read again, and its instances in the world follow it (in place, with what they changed kept): this runs where the reload is polled, a frame boundary.
                World followed = world;
                reloader.DocumentsChanged += paths =>
                {
                    content.Refresh();
                    ReloadPrefabs(followed, paths);
                };
                Log.Info("Runtime", "Watching for changes: scripts that were required, definitions and assets are reloaded when their files change.");
            }

            return new ScriptSession(world, vm, input, physics, audio, steamSystem, net, models, uiSystem, assets, settings, owned) { Reloader = reloader, Files = files };
        }
        catch
        {
            vm?.Dispose();
            settings?.Destroy();
            uiSystem?.Dispose();
            models?.Dispose();
            assets?.Destroy();
            net?.Dispose();
            steamSystem.Dispose();
            audio?.Dispose();
            physics?.Dispose();
            world?.Dispose();
            owned.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The game's Steam app id: the one in its <c>game.xml</c> (when it names one); else the number in <c>steam_appid.txt</c> in the game's folder, with a warning that the file is deprecated;
    /// else 0.
    /// </summary>
    /// <param name="manifest">The game's <c>game.xml</c>, or null.</param>
    /// <param name="folder">The game's folder.</param>
    /// <param name="warning">What to tell the developer, or null.</param>
    public static uint SteamAppIdFor(GameManifest? manifest, string folder, out string? warning)
    {
        warning = null;
        if (manifest is { SteamAppId: not 0 })
        {
            return manifest.SteamAppId;
        }

        string file = Path.Combine(folder, "steam_appid.txt");
        if (!File.Exists(file))
        {
            return 0;
        }

        warning = "steam_appid.txt in the game folder is deprecated: put steamAppId in game.xml.";
        return uint.TryParse(File.ReadAllText(file).Trim(), out uint id) ? id : 0;
    }

    /// <summary>
    /// The packs of a game: the <c>.pack</c> files in its <c>data</c> folder, by name, so that a later one (a patch) is mounted over an earlier one. The parts of a split pack
    /// (<c>game_001.pack</c>) are found by their first part and are not packs of their own.
    /// </summary>
    private static List<string> FindPacks(string folder)
    {
        string data = Path.Combine(folder, "data");
        if (!Directory.Exists(data))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(data, "*.pack")
            .Where(f => !(Regex.Match(Path.GetFileName(f), @"^(.*)_\d{3}\.pack$") is { Success: true } part && File.Exists(Path.Combine(data, part.Groups[1].Value + ".pack"))))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>What a game session has to close: its packages (and their mounts) and its packs.</summary>
    private sealed class Owned(PackageSet packages, List<PackSource> packs) : IDisposable
    {
        private readonly List<IDisposable> _more = [];

        public void Add(IDisposable disposable) => _more.Add(disposable);

        public void Dispose()
        {
            foreach (IDisposable disposable in _more)
            {
                disposable.Dispose();
            }

            packages.Dispose();
            foreach (PackSource pack in packs)
            {
                pack.Dispose();
            }
        }
    }

    /// <summary>The tasks scripts started with <c>task</c>, for tests and tools to look at.</summary>
    public ScriptScheduler Tasks => _vm.Scheduler;

    /// <summary>Runs the world's systems for <paramref name="deltaSeconds"/> of real time (the game's time scale applies to the systems, the physics and the tasks).</summary>
    public void Progress(float deltaSeconds)
    {
        float scaled = deltaSeconds * (float)GameLibrary.TimeScale(); // game time: what game.set_time_scale changes. Audio, the UI and input go by real time
        Steam.Update();                 // Steam's callbacks become signals before the scripts run
        Net.Update();                   // so do the network's: connections made, messages arrived
        Input.Update(InputDevices.Now); // holds that came due and taps that timed out fire before the systems and scripts read the actions
        Physics.Update(scaled);         // before the world, so that its systems see the poses it wrote and the transform system composes them
        World.Progress(scaled);
        _vm.AdvanceTime(scaled, deltaSeconds); // tasks after the world: they see this frame's physics and transforms, and what they change is heard and laid out below
        Audio.Update(deltaSeconds);     // after the world: it reads the entities' world transforms
        Ui.Update(deltaSeconds);        // after the world and the scripts: it lays out what they changed
    }

    /// <summary>Runs what scripts deferred until the end of the frame.</summary>
    public void EndFrame()
    {
        DebugDraw.Clear();
        Input.EndFrame(); // the mouse's motion of the frame has been read
        _vm.RunDeferred(); // what GUI functions, UI handlers and signals deferred after the step
        _vm.ProcessPending();
        Settings.SaveIfDirty(SettingsSaveInterval); // after the scripts: what they changed this frame is written soon, and a slider that is dragged is written once a second
    }

    /// <summary>The least time between two writes of <c>user://settings.xml</c> while the game runs.</summary>
    private static readonly TimeSpan SettingsSaveInterval = TimeSpan.FromSeconds(1);

    public void Dispose()
    {
        _disposed = true;
        foreach (World editing in _editingWorlds)
        {
            editing.Dispose(); // the worlds tools made: they follow the prefab library and hold models, so they go first
        }

        _editingWorlds.Clear();
        if (ReferenceEquals(DebugDrawLibrary.Target, DebugDraw))
        {
            DebugDrawLibrary.Target = null;
        }

        Settings.Flush(); // what changed in the last second is not lost
        _vm.Dispose();
        Settings.Destroy();
        Input.Map?.Destroy();
        Input.Destroy();
        Net.Dispose();
        Steam.Dispose();
        Ui.Dispose();      // after the scripts (their elements are handles into it), before RmlUi's files go
        Models.Dispose();
        Assets.Destroy();
        Audio.Dispose();   // before the world: it holds queries on it
        Physics.Dispose(); // before the world: it has an observer in it
        World.Dispose();
        _owned?.Dispose();
    }
}
