using Weft.Graphics;
using Weft.DevTools;
using Weft.Rendering;
using Weft.Rendering.Graph;
using Weft.Rendering.Shaders;
using Weft.UI;

namespace Weft.Host;

/// <summary>
/// What goes over the finished picture: the game's UI, then the dev tools on top of it. The UI renderer is made the first frame after a document was loaded, so a script that only has ImGui panels
/// never compiles the UI shader.
/// </summary>
public sealed class FrameOverlay(GraphicsDevice device, ShaderLibrary shaders, ScriptSession session, ImGuiRenderer imgui) : IRenderOverlay, System.IDisposable
{
    private UiRenderer? _ui;

    public void AddPasses(RenderGraph graph, ImageHandle output, Frame frame)
    {
        if (_ui is null && session.Ui.DocumentsLoaded > 0)
        {
            _ui = new UiRenderer(device, session.Ui, shaders);
        }

        _ui?.AddPasses(graph, output, frame);
        imgui.AddPasses(graph, output, frame);
    }

    public void Dispose() => _ui?.Dispose();
}
