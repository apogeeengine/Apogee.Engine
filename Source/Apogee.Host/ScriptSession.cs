using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
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

    /// <summary>A scene holds a model's path, not its number in this run's library; and a physics body only exists in the run that made it (scripts make them again).</summary>
    private static void RegisterSceneSupport(World world, ModelLibrary models)
    {
        world.RegisterSceneConverter("ModelRenderer", "Model", id => models.PathOf((uint)id), path => models.Load(path));
        world.IgnoreInScenes("Body");
    }

    /// <summary>Everything a session can import: models, images and shaders.</summary>
    public static IReadOnlyList<IAssetImporter> Importers { get; } = [.. AssetImporter.Defaults, new ShaderImporter()];

    private ScriptSession(World world, LuauVm vm, InputActionRouter input, PhysicsWorld physics, AudioSystem audio, SteamSystem steam, NetSystem net, ModelLibrary models, UiSystem ui, IDisposable? owned = null)
    {
        World = world;
        Ui = ui;
        Models = models;

        RegisterSceneSupport(world, models);
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

    /// <summary>Reloads what changed on disk (a game started with watching), or null.</summary>
    public HotReloader? Reloader { get; private init; }

    /// <summary>Applies the changes to files since the last frame, if the game is being watched.</summary>
    public void Reload() => Reloader?.Poll();

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
            vm.Execute(vm.BaseOwner, "@" + path, File.ReadAllText(path, Encoding.UTF8));
            return new ScriptSession(world, vm, input, physics, audio, steam, net, models, ui);
        }
        catch
        {
            vm.Dispose();
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
    public static ScriptSession StartGame(string folder, string? userFolder, bool watch = false, bool realAudio = false, bool realSteam = false, bool runScripts = true, string? scene = null)
    {
        RegisterLibraries();
        ScriptGui.Clear();
        GameLibrary.Reset();
        var files = new VirtualFileSystem();
        files.Mount("res://", new DirectorySource(folder, writable: watch)); // a watching run imports changed assets into res://imported
        ShaderLibrary.MountEngineShaders(files, sources: watch); // the engine's shaders: compiled next to the program, or when watching the source files, so that editing them rebuilds the pipelines
        var packs = new List<PackSource>();
        PackKey? key = PackKey.FromEnvironment();
        foreach (string pack in FindPacks(folder))
        {
            PackSource source = PackSource.Open(pack, key);
            packs.Add(source);
            files.Mount("res://", source);
            Log.Info("Packs", $"{Path.GetFileName(pack)}: {source.FileCount} files in {source.PartCount} part(s){(source.IsEncrypted ? ", encrypted" : "")}");
        }

        string user = userFolder ?? VirtualFileSystem.DefaultUserDirectory("Apogee", Path.GetFileName(Path.GetFullPath(folder).TrimEnd('/', '\\')));
        Directory.CreateDirectory(user);
        files.Mount("user://", new DirectorySource(user, writable: true));

        // Steam first: the cloud and the Workshop's mods are file sources, and the mods are found with the others. A game that Steam relaunches (SteamSystem.RestartRequested) stops here.
        var steamOptions = new SteamOptions { Enabled = realSteam, AppId = ReadSteamAppId(folder), RestartIfNecessary = !watch };
        var steamSystem = new SteamSystem(steamOptions);
        PackageSet packages;
        try
        {
            files.Mount("cloud://", steamSystem.CreateCloudSource(Path.Combine(user, "cloud")));
            var packageOptions = new PackageOptions();
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
            audio = InstallAudio(vm, world, physics, files, realAudio);
            steamSystem.Adopt();
            vm.SetGlobal("steam", steamSystem);
            net = InstallNet(vm);
            var content = new ContentLoader(files);
            models = InstallModels(vm, files, content);
            uiSystem = InstallUi(vm, files, content);
            RegisterSceneSupport(world, models);
            if (scene is not null)
            {
                world.LoadScene(scene);
            }

            if (runScripts)
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
                Log.Info("Runtime", "Watching for changes: scripts that were required, definitions and assets are reloaded when their files change.");
            }

            return new ScriptSession(world, vm, input, physics, audio, steamSystem, net, models, uiSystem, owned) { Reloader = reloader, Files = files };
        }
        catch
        {
            vm?.Dispose();
            uiSystem?.Dispose();
            models?.Dispose();
            net?.Dispose();
            steamSystem.Dispose();
            audio?.Dispose();
            physics?.Dispose();
            world?.Dispose();
            owned.Dispose();
            throw;
        }
    }

    /// <summary>The game's Steam app id: the number in <c>steam_appid.txt</c> in the game's folder, or 0.</summary>
    private static uint ReadSteamAppId(string folder)
    {
        string file = Path.Combine(folder, "steam_appid.txt");
        return File.Exists(file) && uint.TryParse(File.ReadAllText(file).Trim(), out uint id) ? id : 0;
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

    /// <summary>Runs the world's systems for <paramref name="deltaSeconds"/>.</summary>
    public void Progress(float deltaSeconds)
    {
        Steam.Update();                 // Steam's callbacks become signals before the scripts run
        Net.Update();                   // so do the network's: connections made, messages arrived
        Input.Update(InputDevices.Now); // holds that came due and taps that timed out fire before the systems and scripts read the actions
        Physics.Update(deltaSeconds);   // before the world, so that its systems see the poses it wrote and the transform system composes them
        World.Progress(deltaSeconds);
        Audio.Update(deltaSeconds);     // after the world: it reads the entities' world transforms
        Ui.Update(deltaSeconds);        // after the world and the scripts: it lays out what they changed
    }

    /// <summary>Runs what scripts deferred until the end of the frame.</summary>
    public void EndFrame()
    {
        DebugDraw.Clear();
        Input.EndFrame(); // the mouse's motion of the frame has been read
        _vm.ProcessPending();
    }

    public void Dispose()
    {
        if (ReferenceEquals(DebugDrawLibrary.Target, DebugDraw))
        {
            DebugDrawLibrary.Target = null;
        }

        _vm.Dispose();
        Input.Map?.Destroy();
        Input.Destroy();
        Net.Dispose();
        Steam.Dispose();
        Ui.Dispose();      // after the scripts (their elements are handles into it), before RmlUi's files go
        Models.Dispose();
        Audio.Dispose();   // before the world: it holds queries on it
        Physics.Dispose(); // before the world: it has an observer in it
        World.Dispose();
        _owned?.Dispose();
    }
}
