using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Apogee.Content.FileSystem;
using Apogee.Core.Logging;
using Apogee.DevTools;
using Apogee.Editor.Editing;
using Apogee.Editor.Panels;
using Apogee.Graphics;
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
/// The editor program: a window with a dock space, the panels, the menu and the play controls, over an <see cref="EditorProject"/>. Run as <c>Apogee.Editor &lt;game folder&gt; [--user &lt;folder&gt;]</c>.
/// The layout is saved to <c>imgui.ini</c> in the user's editor folder; the first time (no file) a default one is built.
/// </summary>
public static unsafe class EditorApp
{
    private static readonly Vector4 Background = new(0.1f, 0.1f, 0.12f, 1f);

    public const string Usage = "Usage: Apogee.Editor <game folder> [--user <folder>]";

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

        if (folder is null || !Directory.Exists(folder))
        {
            Console.Error.WriteLine(folder is null ? Usage : $"{folder} is not a folder.");
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

    private static int RunWindow(string gameFolder, string? user)
    {
        Directory.CreateDirectory(EditorFolder);
        var log = new EditorLog { EchoToConsole = true };
        Log.AddSink(log);
        PlatformHost.Initialize();
        nint ini = Marshal.StringToCoTaskMemUTF8(Path.Combine(EditorFolder, "imgui.ini"));
        try
        {
            using var window = new Window("Apogee Editor: " + Path.GetFileName(Path.GetFullPath(gameFolder)), 1600, 900, WindowOptions.Resizable | WindowOptions.HighPixelDensity | GraphicsDevice.RequiredWindowOptions());
            using var device = GraphicsDevice.Create(new GraphicsDeviceOptions { ApplicationName = "Apogee editor", Window = window });
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
            using var project = new EditorProject(gameFolder, user);
            var ctx = new EditorContext(project, new EditorCamera(), log, host);

            void MakeViewport() => ctx.Viewport = new ViewportRenderer(device, shaders, project.Session, loop.FramesInFlight, project.IsPlaying, () => ctx.ViewportFocused, () => ctx.ViewportHovered, window.Id);
            project.SessionReplacing += () =>
            {
                ctx.Viewport?.Dispose();
                ctx.Viewport = null;
            };
            project.SessionReplaced += MakeViewport;
            MakeViewport();

            var app = new Frames(ctx, window, device, loop, pool, imgui, host, hasLayout);
            app.Loop();
            loop.WaitIdle();
            ctx.Viewport?.Dispose();
            ctx.Viewport = null;
            return device.ValidationErrorCount == 0 ? 0 : 3;
        }
        finally
        {
            Marshal.FreeCoTaskMem(ini);
            PlatformHost.Shutdown();
        }
    }

    /// <summary>The frame loop and the panels it draws.</summary>
    private sealed class Frames(EditorContext ctx, Window window, GraphicsDevice device, FrameLoop loop, TransientPool pool, ImGuiRenderer imgui, ImGuiHost host, bool hasLayout)
    {
        private readonly HierarchyPanel _hierarchy = new();
        private readonly InspectorPanel _inspector = new();
        private readonly AssetBrowserPanel _assets = new();
        private readonly ConsolePanel _console = new();
        private readonly RenderGraphPanel _graph = new();
        private readonly ViewportPanel _viewport = new();
        private bool _layoutBuilt = hasLayout;
        private string _savePath = "res://scenes/untitled";
        private bool _openSaveAs;
        private bool _openOpen;
        private bool _openQuit;
        private bool _quit;

        public void Loop()
        {
            EditorProject project = ctx.Project;
            var clock = Stopwatch.StartNew();
            double previous = 0;
            double cpuMilliseconds = 0;
            while (!PlatformHost.QuitRequested && !_quit)
            {
                double now = clock.Elapsed.TotalSeconds;
                float delta = Math.Min((float)(now - previous), 0.25f);
                previous = now;
                long cpuStart = Stopwatch.GetTimestamp();

                PlatformHost.PumpEvents();
                if (window.CloseRequested)
                {
                    window.ClearCloseRequest();
                    if (project.Dirty)
                    {
                        _openQuit = true;
                    }
                    else
                    {
                        break;
                    }
                }

                ctx.Viewport?.UpdateInput(window);
                project.Update(delta);
                Frame? frame = loop.BeginFrame();
                host.BeginFrame(Math.Max(delta, 1e-4f));
                Gizmo.BeginFrame();
                DrawUi(frame, window.PixelDensity, delta, now);
                ctx.ProfilerData = new DevOverlayData { GpuName = device.Name, CpuFrameMilliseconds = cpuMilliseconds, GpuTimings = loop.Timings, ShaderErrors = [] };
                ctx.Profiler.Draw(ctx.ProfilerData);
                host.EndFrame();

                if (frame is not null)
                {
                    Record(frame);
                    loop.EndFrame(frame);
                }

                project.Session.EndFrame();
                cpuMilliseconds = (Stopwatch.GetTimestamp() - cpuStart) * 1000.0 / Stopwatch.Frequency;
            }
        }

        private void Record(Frame frame)
        {
            EditorProject project = ctx.Project;
            if (_viewport.Wanted && ctx.Viewport is { } viewport)
            {
                if (ctx.Gizmo.ShowHelpers)
                {
                    EditorHelpers.Draw(project.World, project.Session.Models, project.Session.DebugDraw, project.Selection, ctx.Camera.Target);
                }

                if (project.IsPlaying && project.Session.Physics.DebugDraw)
                {
                    project.Session.Physics.DrawDebug(project.Session.DebugDraw, camera: viewport.Camera.Position);
                }

                viewport.Render(frame, ctx.Camera, project.IsPlaying, ctx.Gizmo.PreferGameCamera, window.PixelDensity);
            }

            var graph = new RenderGraph(device, pool);
            ImageHandle output = graph.ImportBackbuffer(frame.Backbuffer!.Value);
            graph.AddPass("Clear", PassType.Graphics, pass => pass.WriteColor(output, Background));
            imgui.AddPasses(graph, output, frame);
            graph.Execute(frame);
        }

        private void DrawUi(Frame? frame, float density, float delta, double now)
        {
            EditorProject project = ctx.Project;
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

        private void Shortcuts(EditorProject project)
        {
            if (host.WantsKeyboard)
            {
                return;
            }

            if (Gui.Ctrl && Gui.KeyPressed(ImGuiKey.ImGuiKeyZ) && !project.IsPlaying)
            {
                if (Gui.Shift)
                {
                    project.History.Redo();
                }
                else
                {
                    project.History.Undo();
                }
            }
            else if (Gui.Ctrl && Gui.KeyPressed(ImGuiKey.ImGuiKeyY) && !project.IsPlaying)
            {
                project.History.Redo();
            }
            else if (Gui.Ctrl && Gui.KeyPressed(ImGuiKey.ImGuiKeyS))
            {
                Save(project);
            }
            else if (Gui.KeyPressed(ImGuiKey.ImGuiKeyF5))
            {
                TogglePlay(project);
            }
        }

        private void Save(EditorProject project)
        {
            if (!project.SaveScene())
            {
                _openSaveAs = true;
            }
        }

        private void TogglePlay(EditorProject project)
        {
            if (project.IsPlaying)
            {
                project.Stop();
            }
            else if (!project.Play())
            {
                ctx.Message = "The game did not start:\n" + project.PlayError;
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
                if (Gui.MenuItem("New scene"))
                {
                    project.NewScene();
                }

                if (Gui.MenuItem("Open scene..."))
                {
                    _openOpen = true;
                }

                if (Gui.MenuItem("Save", "Ctrl+S"))
                {
                    Save(project);
                }

                if (Gui.MenuItem("Save as..."))
                {
                    _openSaveAs = true;
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
                if (Gui.MenuItem(project.History.UndoName is { } undo ? $"Undo {undo}" : "Undo", "Ctrl+Z", false, editing && project.History.CanUndo))
                {
                    project.History.Undo();
                }

                if (Gui.MenuItem(project.History.RedoName is { } redo ? $"Redo {redo}" : "Redo", "Ctrl+Y", false, editing && project.History.CanRedo))
                {
                    project.History.Redo();
                }

                Ui.Separator();
                if (Gui.MenuItem("Duplicate", "Ctrl+D", false, editing && project.Selection is not null))
                {
                    project.Duplicate(project.Selection!.Value);
                }

                if (Gui.MenuItem("Delete", "Del", false, editing && project.Selection is not null))
                {
                    project.Delete(project.Selection!.Value);
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

        private void DrawModals(EditorProject project)
        {
            if (_openSaveAs)
            {
                _openSaveAs = false;
                _savePath = project.ScenePath ?? "res://scenes/untitled";
                Gui.OpenPopup("Save scene as");
            }

            if (_openOpen)
            {
                _openOpen = false;
                Gui.OpenPopup("Open scene");
            }

            if (_openQuit)
            {
                _openQuit = false;
                Gui.OpenPopup("Unsaved changes");
            }

            if (ctx.Message is not null)
            {
                Gui.OpenPopup("Message");
            }

            if (Modal("Save scene as"))
            {
                Gui.InputText("Path", ref _savePath);
                if (Gui.Button("Save"))
                {
                    project.SaveSceneAs(_savePath);
                    Gui.ClosePopup();
                }

                Gui.SameLine();
                if (Gui.Button("Cancel"))
                {
                    Gui.ClosePopup();
                }

                Gui.EndPopup();
            }

            if (Modal("Open scene"))
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
                        project.OpenScene(scene);
                        Gui.ClosePopup();
                    }
                }

                if (Gui.Button("Cancel"))
                {
                    Gui.ClosePopup();
                }

                Gui.EndPopup();
            }

            if (Modal("Unsaved changes"))
            {
                Ui.Text("The scene has changes that are not saved.");
                if (Gui.Button("Save and quit"))
                {
                    Save(project);
                    _quit = !_openSaveAs;
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

            if (Modal("Message"))
            {
                Ui.Text(ctx.Message ?? "");
                if (Gui.Button("OK"))
                {
                    ctx.Message = null;
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
