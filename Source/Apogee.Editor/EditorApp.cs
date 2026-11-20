using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Apogee.Content.FileSystem;
using Apogee.Core.Diagnostics;
using Apogee.Core.Logging;
using Apogee.DevTools;
using Apogee.Editor.Editing;
using Apogee.Editor.Panels;
using Apogee.Graphics;
using Apogee.Host;
using Apogee.Graphics.Shaders;
using Apogee.Native.CImGui;
using Apogee.Native.ImGuizmo;
using Apogee.Platform;
using Apogee.Rendering.Graph;
using Apogee.Rendering.Shaders;
using Apogee.Scripting;
using Apogee.Content;

namespace Apogee.Editor;

/// <summary>
/// The editor program: a window with a dock space, the panels, the menu and the play controls, over an <see cref="EditorProject"/>. Run as <c>Apogee.Editor [game folder] [--user &lt;folder&gt;]</c>; without a
/// folder it shows a start screen, and <b>File &gt; Open project</b> switches to another game in the same process. The layout is saved to <c>imgui.ini</c> in the user's editor folder; the first time (no file) a default one is built.
/// </summary>
public static unsafe class EditorApp
{
    private static readonly Vector4 Background = new(0.1f, 0.1f, 0.12f, 1f);

    public const string Usage = "Usage: Apogee.Editor [game folder] [--user <folder>]";

    public static int Run(string[] args)
    {
        string? folder = null;
        string? user = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--user" && i + 1 < args.Length)
            {
                user = args[++i];
            }
            else if (args[i] is "-h" or "--help")
            {
                Console.WriteLine(Usage);
                return 0;
            }
            else if (!args[i].StartsWith('-') && folder is null)
            {
                folder = args[i];
            }
            else
            {
                Console.Error.WriteLine($"Unknown argument {args[i]}.\n{Usage}");
                return 1;
            }
        }

        if (folder is not null && !Directory.Exists(folder))
        {
            Console.Error.WriteLine($"{folder} is not a folder.");
            return 1;
        }

        try
        {
            return RunWindow(folder, user);
        }
        catch (Exception e) when (e is ScriptException or ContentException or PlatformException or GraphicsException or ShaderCompileException or IOException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }

    private static string EditorFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apogee", "Editor");

    private static int RunWindow(string? gameFolder, string? user)
    {
        Directory.CreateDirectory(EditorFolder);
        var log = new EditorLog { EchoToConsole = true };
        Log.AddSink(log);

        // The editor's own log and crash reports, in its folder (the game being edited has its own when it runs).
        var recent = new RecentLogSink();
        Log.AddSink(recent);
        CrashHandler.Install(Path.Combine(EditorFolder, "crashes"), recent);
        using FileLogSink logFile = RunDiagnostics.Start(EditorFolder, "Apogee Editor");
        PlatformHost.Initialize();
        nint ini = Marshal.StringToCoTaskMemUTF8(Path.Combine(EditorFolder, "imgui.ini"));
        try
        {
            using var window = new Window(WindowTitle(gameFolder), 1600, 900, WindowOptions.Resizable | WindowOptions.HighPixelDensity | GraphicsDevice.RequiredWindowOptions());
            using var device = GraphicsDevice.Create(new GraphicsDeviceOptions { ApplicationName = "Apogee editor", Window = window });
            CrashHandler.SetContext("GPU", device.Name);
            using var swapchain = device.CreateSwapchain();
            using var loop = device.CreateFrameLoop(swapchain);
            using var pool = new TransientPool(device);
            ShaderLibrary shaders = ShaderLibrary.Standard(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apogee", "ShaderCache"));
            using var host = new ImGuiHost();
            host.EnableDocking();
            host.Attach(window);
            host.IO->IniFilename = (byte*)ini;
            bool hasLayout = File.Exists(Path.Combine(EditorFolder, "imgui.ini"));
            using var imgui = new ImGuiRenderer(device, shaders);
            using var projects = new ProjectHost();
            var app = new Frames(log, window, device, loop, pool, imgui, host, shaders, projects, new SdlFileDialogs(window), hasLayout);
            if (gameFolder is not null && !projects.TryOpen(gameFolder, user, out string? error))
            {
                Console.Error.WriteLine(error);
                return 2;
            }

            app.Loop();
            loop.WaitIdle();
            app.Release();
            return device.ValidationErrorCount == 0 ? 0 : 3;
        }
        finally
        {
            Marshal.FreeCoTaskMem(ini);
            PlatformHost.Shutdown();
        }
    }

    /// <summary>The window title for the game folder being edited (none: the start screen).</summary>
    private static string WindowTitle(string? gameFolder) =>
        gameFolder is null ? "Apogee Editor" : "Apogee Editor: " + Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameFolder)));

    /// <summary>The frame loop and the panels it draws.</summary>
    private sealed class Frames
    {
        private readonly EditorLog _log;
        private readonly Window _window;
        private readonly GraphicsDevice _device;
        private readonly FrameLoop _loop;
        private readonly TransientPool _pool;
        private readonly ImGuiRenderer _imgui;
        private readonly ImGuiHost _host;
        private readonly ShaderLibrary _shaders;
        private readonly ProjectHost _projects;
        private readonly DialogFlow _dialogFlow;
        private readonly ConsolePanel _console = new();
        private readonly RenderGraphPanel _graph = new();
        private readonly ViewportPanel _viewport = new();
        private EditorContext? _context;
        private HierarchyPanel _hierarchy = new();
        private InspectorPanel _inspector = new();
        private AssetBrowserPanel _assets = new();
        private bool _layoutBuilt;
        private string _savePath = "res://scenes/untitled";
        private string _projectPath = "";
        private string? _startMessage;

        // The path entries that stand in for the system's dialogs when it has none.
        private bool _openSaveAs;
        private bool _openOpen;
        private bool _openProjectPath;

        private bool _openQuit;
        private bool _quit;

        /// <summary>The game folder the user chose and the editor will switch to at the start of the next frame (a switch in the middle of a frame would draw half of it for each project).</summary>
        private string? _switchTo;

        /// <summary>A game folder the user chose while the scene has unsaved changes: the prompt is open for it.</summary>
        private string? _confirmSwitch;
        private bool _openConfirmSwitch;

        public Frames(EditorLog log, Window window, GraphicsDevice device, FrameLoop loop, TransientPool pool, ImGuiRenderer imgui, ImGuiHost host, ShaderLibrary shaders, ProjectHost projects, IFileDialogs dialogs, bool hasLayout)
        {
            _log = log;
            _window = window;
            _device = device;
            _loop = loop;
            _pool = pool;
            _imgui = imgui;
            _host = host;
            _shaders = shaders;
            _projects = projects;
            _layoutBuilt = hasLayout;
            _dialogFlow = new DialogFlow(dialogs, () => _projects.Current, Say, RequestProject, UseBuiltIn);
            projects.Closing += OnClosing;
            projects.Opened += OnOpened;
        }

        /// <summary>The context of the open project; only used while one is open.</summary>
        private EditorContext ctx => _context!;

        private string? Message
        {
            get => _context?.Message ?? _startMessage;
            set
            {
                if (_context is not null)
                {
                    _context.Message = value;
                }

                _startMessage = value;
            }
        }

        private void Say(string text) => Message = text;

        /// <summary>The system has no file dialog (no backend): the editor's own entry for the same job opens.</summary>
        private void UseBuiltIn(DialogKind kind)
        {
            switch (kind)
            {
                case DialogKind.OpenScene:
                    _openOpen = true;
                    break;
                case DialogKind.SaveSceneAs:
                    _openSaveAs = true;
                    break;
                default:
                    _openProjectPath = true;
                    break;
            }
        }

        /// <summary>The user chose a game folder to switch to (checked by <see cref="DialogFlow.ChooseProject"/>, whether it came from a dialog or the path entry): after the question about unsaved changes, the switch happens at the start of the next frame.</summary>
        private void RequestProject(string folder)
        {
            if (_projects.Current is { Dirty: true })
            {
                _confirmSwitch = folder;
                _openConfirmSwitch = true;
            }
            else
            {
                _switchTo = folder;
            }
        }

        private void OnClosing(EditorProject project)
        {
            // The GPU may still be reading what the last frames drew from this project: wait before its models and renderer go.
            _ = project;
            _loop.WaitIdle();
            ctx.Viewport?.Dispose();
            ctx.Viewport = null;
        }

        private void OnOpened(EditorProject project)
        {
            if (_context is null)
            {
                _context = new EditorContext(project, new EditorCamera(), _log, _host);
            }
            else
            {
                _context.Project = project;
                _context.Camera.Reset();
                _context.Message = null;
            }

            _startMessage = null;

            // Panels keep what they looked at (a rename in progress, the folder shown): a new project starts them afresh.
            _hierarchy = new HierarchyPanel();
            _inspector = new InspectorPanel();
            _assets = new AssetBrowserPanel();

            project.SessionReplacing += () =>
            {
                ctx.Viewport?.Dispose();
                ctx.Viewport = null;
            };
            project.SessionReplaced += MakeViewport;
            _shortcuts = null; // the shortcuts act on the project that was open when they were built
            MakeViewport();
            string folder = Path.GetFullPath(project.GameFolder);
            _window.Title = WindowTitle(folder);
            CrashHandler.SetContext("Edited game", folder);
            Log.Info("Editor", $"Opened the project {folder}");
        }

        private void MakeViewport()
        {
            EditorProject project = _projects.Current!;
            ctx.Viewport = new ViewportRenderer(_device, _shaders, project.Session, _loop.FramesInFlight, project.IsPlaying, () => ctx.ViewportFocused, () => ctx.ViewportHovered, _window.Id);
        }

        /// <summary>Lets go of the open project's GPU objects; the loop has ended.</summary>
        public void Release() => _projects.Close();

        private void SwitchProject(string folder)
        {
            if (!_projects.TryOpen(folder, null, out string? error)) // --user belongs to the folder of the command line only
            {
                _window.Title = WindowTitle(_projects.Current?.GameFolder);
                Say($"The project could not be opened:\n{error}" + (_projects.Current is null ? "" : "\nThe previous project was opened again."));
            }
        }

        public void Loop()
        {
            var clock = Stopwatch.StartNew();
            double previous = 0;
            double cpuMilliseconds = 0;
            while (!PlatformHost.QuitRequested && !_quit)
            {
                double now = clock.Elapsed.TotalSeconds;
                float delta = Math.Min((float)(now - previous), 0.25f);
                previous = now;
                long cpuStart = Stopwatch.GetTimestamp();

                PlatformHost.PumpEvents(); // also runs the continuations of file dialogs that ended: a chosen scene opens or saves here, a chosen project is only noted for the switch below, all before the frame's UI
                if (_switchTo is { } folder)
                {
                    _switchTo = null;
                    SwitchProject(folder);
                }

                EditorProject? project = _projects.Current;
                if (_window.CloseRequested)
                {
                    _window.ClearCloseRequest();
                    if (project is { Dirty: true })
                    {
                        _openQuit = true;
                    }
                    else
                    {
                        break;
                    }
                }

                if (project is not null)
                {
                    ctx.Viewport?.UpdateInput(_window);
                    project.Update(delta);
                }

                Frame? frame = _loop.BeginFrame();
                _host.BeginFrame(Math.Max(delta, 1e-4f));
                Gizmo.BeginFrame();
                DrawUi(project, frame, _window.PixelDensity, delta, now);
                if (project is not null)
                {
                    ctx.ProfilerData = new DevOverlayData { GpuName = _device.Name, CpuFrameMilliseconds = cpuMilliseconds, GpuTimings = _loop.Timings, ShaderErrors = [] };
                    ctx.Profiler.Draw(ctx.ProfilerData);
                }

                _host.EndFrame();

                if (frame is not null)
                {
                    Record(project, frame);
                    _loop.EndFrame(frame);
                }

                project?.Session.EndFrame();
                cpuMilliseconds = (Stopwatch.GetTimestamp() - cpuStart) * 1000.0 / Stopwatch.Frequency;
            }
        }

        private void Record(EditorProject? project, Frame frame)
        {
            if (project is not null && _viewport.Wanted && ctx.Viewport is { } viewport)
            {
                if (ctx.Gizmo.ShowHelpers)
                {
                    EditorHelpers.Draw(project.World, project.Session.Models, project.Session.DebugDraw, project.Selected, ctx.Camera.Target);
                }

                if (project.IsPlaying && project.Session.Physics.DebugDraw)
                {
                    project.Session.Physics.DrawDebug(project.Session.DebugDraw, camera: viewport.Camera.Position);
                }

                viewport.Render(frame, ctx.Camera, project.IsPlaying, ctx.Gizmo.PreferGameCamera, _window.PixelDensity);
            }

            var graph = new RenderGraph(_device, _pool);
            ImageHandle output = graph.ImportBackbuffer(frame.Backbuffer!.Value);
            graph.AddPass("Clear", PassType.Graphics, pass => pass.WriteColor(output, Background));
            _imgui.AddPasses(graph, output, frame);
            graph.Execute(frame);
        }

        private void DrawUi(EditorProject? project, Frame? frame, float density, float delta, double now)
        {
            if (project is null)
            {
                DrawStart();
                DrawModals(null);
                return;
            }

            ImGuiViewport* main = ImGui.GetMainViewport();
            uint dock = ImGui.DockSpaceOverViewport(0, main, ImGuiDockNodeFlags.None, null);
            if (!_layoutBuilt)
            {
                BuildLayout(dock, main->Size);
                _layoutBuilt = true;
            }

            Shortcuts(project);
            DrawMenu(project);

            _hierarchy.Draw(ctx);
            _inspector.Draw(ctx);
            _viewport.Draw(ctx, frame, density, delta);
            _assets.Draw(ctx, now);
            _console.Draw(ctx);
            ProfilerPanel.Draw(ctx);
            _graph.Draw(ctx);

            DrawModals(project);
        }

        private static void BuildLayout(uint dock, ImVec2 size)
        {
            ImGui.DockBuilderRemoveNode(dock);
            _ = ImGui.DockBuilderAddNode(dock, (ImGuiDockNodeFlags)ImGuiDockNodeFlagsPrivate.DockSpace);
            ImGui.DockBuilderSetNodeSize(dock, size);
            uint left = 0;
            uint rest = 0;
            _ = ImGui.DockBuilderSplitNode(dock, ImGuiDir.Left, 0.2f, &left, &rest);
            uint right = 0;
            uint centre = 0;
            _ = ImGui.DockBuilderSplitNode(rest, ImGuiDir.Right, 0.27f, &right, &centre);
            uint bottom = 0;
            uint top = 0;
            _ = ImGui.DockBuilderSplitNode(centre, ImGuiDir.Down, 0.3f, &bottom, &top);
            Dock("Hierarchy", left);
            Dock("Inspector", right);
            Dock("Viewport", top);
            Dock("Assets", bottom);
            Dock("Console", bottom);
            Dock("Profiler", bottom);
            Dock("Render graph", bottom);
            ImGui.DockBuilderFinish(dock);
        }

        private static void Dock(string window, uint node)
        {
            fixed (byte* name = Apogee.Interop.Utf8.ToZeroTerminated(window))
            {
                ImGui.DockBuilderDockWindow(name, node);
            }
        }

        /// <summary>The global shortcuts: what each action of the keymap does and when it is allowed. Only one fires per frame.</summary>
        private List<(string Id, Func<bool> Enabled, Action Run)>? _shortcuts;

        /// <summary>The start screen: no project is open. It offers the system's folder dialog, and the menu has what works without a project.</summary>
        private void DrawStart()
        {
            if (Gui.BeginMainMenuBar())
            {
                if (Gui.BeginMenu("File"))
                {
                    if (Gui.MenuItem("Open project...", null, false, !_dialogFlow.Busy))
                    {
                        _dialogFlow.OpenProject();
                    }

                    Ui.Separator();
                    if (Gui.MenuItem("Quit"))
                    {
                        _quit = true;
                    }

                    Gui.EndMenu();
                }

                Gui.EndMainMenuBar();
            }

            ImGuiViewport* main = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(new ImVec2 { X = main->Pos.X + main->Size.X * 0.5f, Y = main->Pos.Y + main->Size.Y * 0.4f }, ImGuiCond.Always, new ImVec2 { X = 0.5f, Y = 0.5f });
            if (Ui.Begin("Apogee Editor##start", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
            {
                Ui.Text("Open a game folder to edit it.");
                Ui.Text("A game folder holds game.xml, its packages and its assets.");
                Gui.Disabled(_dialogFlow.Busy, () =>
                {
                    if (Gui.Button("Open project..."))
                    {
                        _dialogFlow.OpenProject();
                    }
                });
            }

            Ui.End();
        }

        private List<(string Id, Func<bool> Enabled, Action Run)> BuildShortcuts()
        {
            EditorProject project = ctx.Project;
            bool Editing() => !project.IsPlaying;
            return
            [
                ("edit.undo", Editing, () => project.History.Undo()),
                ("edit.redo", Editing, () => project.History.Redo()),
                ("edit.copy", () => true, () => project.Copy(ctx.Clipboard)),
                ("edit.cut", Editing, () => project.Cut(ctx.Clipboard)),
                ("edit.paste", Editing, () => project.Paste(ctx.Clipboard)),
                ("edit.paste_child", Editing, () => project.Paste(ctx.Clipboard, project.Selected.Active)),
                ("edit.duplicate", Editing, () => project.Duplicate(project.Selected.TopLevel())),
                ("file.save", () => true, () => _ = Save(project)),
                ("file.save_as", () => true, _dialogFlow.SaveSceneAs),
                ("play.toggle", () => true, () => TogglePlay(project)),
            ];
        }

        private void Shortcuts(EditorProject project)
        {
            _ = project;
            _shortcuts ??= BuildShortcuts();
            foreach ((string id, Func<bool> enabled, Action run) in _shortcuts)
            {
                if (ctx.Pressed(id))
                {
                    if (enabled())
                    {
                        run();
                    }

                    break;
                }
            }
        }

        /// <summary>Saves the scene to its file; a scene that has none yet asks where (the Save as dialog) and is not saved yet when this returns.</summary>
        private bool Save(EditorProject project)
        {
            if (project.TrySaveScene(out string? error))
            {
                return true;
            }

            if (error is not null)
            {
                Say(error); // a file that cannot be written: the scene stays modified, and a quit or a project switch that waited for this does not happen
            }
            else
            {
                _dialogFlow.SaveSceneAs();
            }

            return false;
        }

        private void TogglePlay(EditorProject project)
        {
            if (project.IsPlaying)
            {
                project.Stop();
            }
            else if (!project.Play())
            {
                Say("The game did not start:\n" + project.PlayError);
            }
        }

        private void DrawMenu(EditorProject project)
        {
            if (!Gui.BeginMainMenuBar())
            {
                return;
            }

            if (Gui.BeginMenu("File"))
            {
                // The system's dialog may stay open while the editor runs on (it is not modal on every platform): the entries that would open another one are greyed meanwhile.
                bool dialogFree = !_dialogFlow.Busy;
                if (Gui.MenuItem("New scene", null, false, dialogFree)) // a Save as that is answered after a New scene would write the empty scene
                {
                    project.NewScene();
                }

                if (Gui.MenuItem("Open scene...", null, false, dialogFree))
                {
                    _dialogFlow.OpenScene();
                }

                if (Gui.MenuItem("Save", ctx.Shortcut("file.save"), false, dialogFree || project.ScenePath is not null))
                {
                    _ = Save(project);
                }

                if (Gui.MenuItem("Save as...", ctx.Shortcut("file.save_as"), false, dialogFree))
                {
                    _dialogFlow.SaveSceneAs();
                }

                Ui.Separator();
                if (Gui.MenuItem("Open project...", null, false, dialogFree))
                {
                    _dialogFlow.OpenProject();
                }

                Ui.Separator();
                if (Gui.MenuItem("Quit"))
                {
                    if (project.Dirty)
                    {
                        _openQuit = true;
                    }
                    else
                    {
                        _quit = true;
                    }
                }

                Gui.EndMenu();
            }

            if (Gui.BeginMenu("Edit"))
            {
                bool editing = !project.IsPlaying;
                if (Gui.MenuItem(project.History.UndoName is { } undo ? $"Undo {undo}" : "Undo", ctx.Shortcut("edit.undo"), false, editing && project.History.CanUndo))
                {
                    project.History.Undo();
                }

                if (Gui.MenuItem(project.History.RedoName is { } redo ? $"Redo {redo}" : "Redo", ctx.Shortcut("edit.redo"), false, editing && project.History.CanRedo))
                {
                    project.History.Redo();
                }

                Ui.Separator();
                bool selected = project.Selection is not null;
                bool canPaste = editing && ClipboardScene.LooksLikeScene(ctx.Clipboard.GetText());
                if (Gui.MenuItem("Cut", ctx.Shortcut("edit.cut"), false, editing && selected))
                {
                    project.Cut(ctx.Clipboard);
                }

                if (Gui.MenuItem("Copy", ctx.Shortcut("edit.copy"), false, selected))
                {
                    project.Copy(ctx.Clipboard);
                }

                if (Gui.MenuItem("Paste", ctx.Shortcut("edit.paste"), false, canPaste))
                {
                    project.Paste(ctx.Clipboard);
                }

                if (Gui.MenuItem("Paste as child", ctx.Shortcut("edit.paste_child"), false, canPaste && selected))
                {
                    project.Paste(ctx.Clipboard, project.Selected.Active);
                }

                if (Gui.MenuItem("Duplicate", ctx.Shortcut("edit.duplicate"), false, editing && selected))
                {
                    project.Duplicate(project.Selected.TopLevel());
                }

                if (Gui.MenuItem("Delete", ctx.Shortcut("edit.delete"), false, editing && project.Selection is not null))
                {
                    project.Delete(project.Selected.TopLevel());
                }

                Gui.EndMenu();
            }

            if (Gui.BeginMenu("Entity", !project.IsPlaying))
            {
                if (Gui.MenuItem("Empty"))
                {
                    project.CreateEmpty();
                }

                if (Gui.MenuItem("Point light"))
                {
                    project.CreatePointLight();
                }

                if (Gui.MenuItem("Sun"))
                {
                    project.CreateSun();
                }

                if (Gui.MenuItem("Camera"))
                {
                    project.CreateCamera();
                }

                Gui.EndMenu();
            }

            if (Gui.BeginMenu("Debug"))
            {
                bool physics = project.Session.Physics.DebugDraw;
                if (Gui.MenuItem("Physics shapes", null, physics, project.IsPlaying))
                {
                    project.Session.Physics.DebugDraw = !physics;
                }

                bool rml = project.IsPlaying && project.Session.Ui.Debugger;
                if (Gui.MenuItem("RmlUi debugger", null, rml, project.IsPlaying))
                {
                    project.Session.Ui.Debugger = !rml;
                }

                Gui.EndMenu();
            }

            // The play controls, in the middle of the bar.
            Gui.SameLine(30);
            if (Gui.SmallButton(project.IsPlaying ? "Stop (F5)" : "Play (F5)"))
            {
                TogglePlay(project);
            }

            if (project.IsPlaying)
            {
                if (Gui.SmallButton(project.IsPaused ? "Resume" : "Pause"))
                {
                    project.IsPaused = !project.IsPaused;
                }

                if (project.IsPaused && Gui.SmallButton("Step"))
                {
                    project.Step(1f / 60f);
                }
            }

            string scene = (project.ScenePath ?? "(unsaved scene)") + (project.Dirty ? " *" : "");
            Gui.SameLine(30);
            Ui.Text(scene);
            Gui.EndMainMenuBar();
        }

        private void DrawModals(EditorProject? project)
        {
            if (_openSaveAs && project is not null)
            {
                _openSaveAs = false;
                _savePath = project.ScenePath ?? "res://scenes/untitled";
                Gui.OpenPopup("Save scene as");
            }

            if (_openOpen && project is not null)
            {
                _openOpen = false;
                Gui.OpenPopup("Open scene");
            }

            if (_openProjectPath)
            {
                _openProjectPath = false;
                _projectPath = project?.GameFolder ?? "";
                Gui.OpenPopup("Open project");
            }

            if (_openQuit)
            {
                _openQuit = false;
                Gui.OpenPopup("Unsaved changes");
            }

            if (_openConfirmSwitch)
            {
                _openConfirmSwitch = false;
                Gui.OpenPopup("Unsaved changes in this project");
            }

            if (Message is not null)
            {
                Gui.OpenPopup("Message");
            }

            // The path entries below are what the editor falls back on when the system has no file dialog (see UseBuiltIn).
            if (project is not null && Modal("Save scene as"))
            {
                Gui.InputText("Path", ref _savePath);
                if (Gui.Button("Save"))
                {
                    if (!project.TrySaveSceneAs(_savePath, out string? error))
                    {
                        Say(error!);
                    }

                    Gui.ClosePopup();
                }

                Gui.SameLine();
                if (Gui.Button("Cancel"))
                {
                    Gui.ClosePopup();
                }

                Gui.EndPopup();
            }

            if (project is not null && Modal("Open scene"))
            {
                var scenes = project.SceneFiles();
                if (scenes.Count == 0)
                {
                    Ui.Text("There are no .ascene files in the game folder.");
                }

                foreach (string scene in scenes)
                {
                    if (Gui.Selectable(scene, project.ScenePath == scene))
                    {
                        if (!project.TryOpenScene(scene, out string? error))
                        {
                            Say(error!);
                        }

                        Gui.ClosePopup();
                    }
                }

                if (Gui.Button("Cancel"))
                {
                    Gui.ClosePopup();
                }

                Gui.EndPopup();
            }

            if (Modal("Open project"))
            {
                Ui.Text("The system's file dialog is not available now (the log says why). Type the path of the game folder.");
                Gui.InputText("Folder", ref _projectPath);
                if (Gui.Button("Open"))
                {
                    _dialogFlow.ChooseProject(_projectPath.Trim());
                    Gui.ClosePopup();
                }

                Gui.SameLine();
                if (Gui.Button("Cancel"))
                {
                    Gui.ClosePopup();
                }

                Gui.EndPopup();
            }

            if (project is not null && Modal("Unsaved changes"))
            {
                Ui.Text("The scene has changes that are not saved.");
                if (Gui.Button("Save and quit"))
                {
                    _quit = Save(project);
                    Gui.ClosePopup();
                }

                Gui.SameLine();
                if (Gui.Button("Quit without saving"))
                {
                    _quit = true;
                    Gui.ClosePopup();
                }

                Gui.SameLine();
                if (Gui.Button("Cancel"))
                {
                    Gui.ClosePopup();
                }

                Gui.EndPopup();
            }

            if (Modal("Unsaved changes in this project"))
            {
                Ui.Text("The scene has changes that are not saved. Opening another project closes this one.");
                if (project?.ScenePath is not null && Gui.Button("Save and open"))
                {
                    if (Save(project))
                    {
                        _switchTo = _confirmSwitch;
                    }

                    _confirmSwitch = null;
                    Gui.ClosePopup();
                }

                Gui.SameLine();
                if (Gui.Button("Open without saving"))
                {
                    _switchTo = _confirmSwitch;
                    _confirmSwitch = null;
                    Gui.ClosePopup();
                }

                Gui.SameLine();
                if (Gui.Button("Cancel"))
                {
                    _confirmSwitch = null;
                    Gui.ClosePopup();
                }

                Gui.EndPopup();
            }

            if (Modal("Message"))
            {
                Ui.Text(Message ?? "");
                if (Gui.Button("OK"))
                {
                    Message = null;
                    Gui.ClosePopup();
                }

                Gui.EndPopup();
            }
        }

        private static bool Modal(string title)
        {
            fixed (byte* name = Apogee.Interop.Utf8.ToZeroTerminated(title))
            {
                return ImGui.BeginPopupModal(name, null, ImGuiWindowFlags.AlwaysAutoResize);
            }
        }
    }
}
