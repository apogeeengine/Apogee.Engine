using System;
using Apogee.Native.CImGui;
using Apogee.Native.ImNodes;
using Apogee.Native.ImPlot;
using Apogee.Platform;

namespace Apogee.DevTools;

/// <summary>
/// A Dear ImGui context wired to the engine: SDL input, display size and the dark style. Draw widgets between <see cref="BeginFrame(float)"/> and
/// <see cref="EndFrame"/>; <see cref="ImGuiRenderer"/> then puts the result on the screen.
/// </summary>
public sealed unsafe class ImGuiHost : IDisposable
{
    private ImGuiContext* _context;
    private AplotContext* _plot;
    private AnodesContext* _nodes;
    private ImGuiSdlInput? _input;
    private Window? _window;

    public ImGuiHost()
    {
        _context = ImGui.CreateContext(null);
        ImGui.SetCurrentContext(_context);

        ImGuiIO* io = IO;
        io->IniFilename = null;                       // no imgui.ini next to the program
        io->ConfigErrorRecovery = true;               // a script that forgets an end call (or fails between a begin and its end) is put right at the end of the frame
        io->ConfigErrorRecoveryEnableAssert = false;
        io->ConfigErrorRecoveryEnableTooltip = false;
        io->ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io->BackendFlags |= ImGuiBackendFlags.HasMouseCursors | ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.RendererHasTextures;
        ImGui.StyleColorsDark(null);

        // ImPlot and imnodes keep a context of their own over ImGui's: the dev tools' graphs and the editor's node views use them.
        _plot = Plot.CreateContext();
        _nodes = Nodes.CreateContext();

        // The ImGui built-in fonts and the font atlas stay valid for the life of the context; the renderer uploads the atlas as ImGui asks.
    }

    public ImGuiContext* Context => _context != null ? _context : throw new ObjectDisposedException(nameof(ImGuiHost));

    public ImGuiIO* IO => ImGui.GetIOContextPtr(Context);

    /// <summary>True while ImGui wants the mouse (the pointer is over a widget): the game should not also react to clicks.</summary>
    public bool WantsMouse => IO->WantCaptureMouse;

    /// <summary>True while ImGui wants the keyboard (a text field is active).</summary>
    public bool WantsKeyboard => IO->WantCaptureKeyboard;

    /// <summary>
    /// Lets windows dock into each other and into a dockspace (<see cref="ImGui.DockSpaceOverViewport"/>). It is off by default: the dev overlay's windows float.
    /// Windows stay inside the one OS window; ImGui's multi-viewport is not enabled.
    /// </summary>
    public void EnableDocking() => IO->ConfigFlags |= ImGuiConfigFlags.DockingEnable;

    /// <summary>Sends this window's input to ImGui and sizes the display from it.</summary>
    public void Attach(Window window)
    {
        _input?.Dispose();
        _window = window;
        _input = new ImGuiSdlInput(IO, ImGui.GetPlatformIOContextPtr(Context), window.Id);
    }

    /// <summary>Takes input from every window instead of one: for tools and tests that have no window of their own.</summary>
    public void AttachToAllWindows()
    {
        _input?.Dispose();
        _window = null;
        _input = new ImGuiSdlInput(IO, ImGui.GetPlatformIOContextPtr(Context), 0);
    }

    /// <summary>Starts a frame sized to the attached window.</summary>
    public void BeginFrame(float deltaSeconds)
    {
        Window window = _window ?? throw new InvalidOperationException("Attach a window first, or give BeginFrame the display size.");
        (int width, int height) = window.LogicalSize;
        BeginFrame(width, height, window.PixelDensity, deltaSeconds);
        _input!.UpdateWindowState(window);
    }

    /// <summary>Starts a frame with an explicit display size in points and the pixels-per-point scale (headless use, tests).</summary>
    public void BeginFrame(float width, float height, float scale, float deltaSeconds)
    {
        ImGui.SetCurrentContext(Context);
        Plot.SetCurrentContext(_plot);
        Nodes.SetCurrentContext(_nodes);
        ImGuiIO* io = IO;
        io->DisplaySize = new ImVec2 { X = width, Y = height };
        io->DisplayFramebufferScale = new ImVec2 { X = scale, Y = scale };
        io->DeltaTime = MathF.Max(deltaSeconds, 1e-5f);
        ImGui.NewFrame();
        ImGuiState.FrameOpen = true;
    }

    /// <summary>Finishes the frame's widgets and produces the draw data the renderer draws.</summary>
    public void EndFrame()
    {
        ImGuiState.FrameOpen = false;
        ImGui.SetCurrentContext(Context);
        ImGui.Render();
    }

    public void Dispose()
    {
        _input?.Dispose();
        _input = null;
        if (_context != null)
        {
            ImGui.SetCurrentContext(_context);
            Nodes.DestroyContext(_nodes);
            Plot.DestroyContext(_plot);
            _nodes = null;
            _plot = null;
            ImGui.DestroyContext(_context);
            _context = null;
        }
    }
}
