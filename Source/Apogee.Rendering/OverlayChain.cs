using Apogee.Graphics;
using Apogee.Rendering.Graph;

namespace Apogee.Rendering;

/// <summary>Several overlays as one, drawn in order over the finished image: the game's UI first, the dev tools on top of it.</summary>
public sealed class OverlayChain(params IRenderOverlay[] overlays) : IRenderOverlay
{
    public void AddPasses(RenderGraph graph, ImageHandle output, Frame frame)
    {
        foreach (IRenderOverlay overlay in overlays)
        {
            overlay.AddPasses(graph, output, frame);
        }
    }
}
