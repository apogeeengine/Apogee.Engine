using System;
using Apogee.Native.CImGui;
using Apogee.Native.Tests;
using Xunit;

namespace Apogee.DevTools.Tests;

[Collection("imgui")]
public sealed unsafe class ImGuiDockingTests : IDisposable
{
    private readonly ImGuiHost _host;

    public ImGuiDockingTests()
    {
        NativeRequirements.Require(typeof(ImGui).Assembly, "cimgui");
        _host = new ImGuiHost();
    }

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void DockingIsOffUntilEnabled()
    {
        Assert.Equal(ImGuiConfigFlags.None, _host.IO->ConfigFlags & ImGuiConfigFlags.DockingEnable);
        _host.EnableDocking();
        Assert.Equal(ImGuiConfigFlags.DockingEnable, _host.IO->ConfigFlags & ImGuiConfigFlags.DockingEnable);
    }

    [Fact]
    public void AWindowDocksIntoTheDockspaceOverTheViewport()
    {
        _host.EnableDocking();
        uint dockspace = 0;
        bool docked = false;

        // Frame 1 builds the layout; ImGui applies a dock request at the start of the next frame, so a third frame sees the result.
        for (int frame = 0; frame < 3; frame++)
        {
            _host.BeginFrame(1280, 720, 1f, 1f / 60f);
            dockspace = ImGui.DockSpaceOverViewport(0, ImGui.GetMainViewport(), ImGuiDockNodeFlags.PassthruCentralNode, null);
            if (frame == 0)
            {
                ImGui.SetNextWindowDockID(dockspace, ImGuiCond.Always);
            }

            if (Ui.Begin("Panel"))
            {
                docked = ImGui.IsWindowDocked();
            }

            Ui.End();
            _host.EndFrame();
        }

        Assert.NotEqual(0u, dockspace);
        Assert.True(docked);
        Assert.True(ImGui.DockBuilderGetNode(dockspace) != null);
    }
}
