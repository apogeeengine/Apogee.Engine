using System;
using Apogee.Interop;
using Apogee.Native.CImGui;
using Apogee.Native.ImGuizmo;
using Apogee.Native.ImNodes;
using Apogee.Native.ImPlot;
using Apogee.Native.Tests;
using Xunit;

namespace Apogee.DevTools.Tests;

/// <summary>ImGuizmo, ImPlot and imnodes, which live in the same library as Dear ImGui: one headless frame each, calling what our shims bind and reading what they drew.</summary>
[Collection("imgui")]
public sealed unsafe class ImGuiExtensionTests : IDisposable
{
    private readonly ImGuiHost _host;

    public ImGuiExtensionTests()
    {
        NativeRequirements.Require(typeof(ImGui).Assembly, "cimgui");
        _host = new ImGuiHost();
    }

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The vertices the current window has drawn so far.</summary>
    private static int Vertices() => ImGui.GetWindowDrawList()->VtxBuffer.Size;

    /// <summary>The vertices of the whole frame that was just rendered (ImGuizmo draws into a window of its own, imnodes into a child).</summary>
    private static int FrameVertices() => ImGui.GetDrawData()->TotalVtxCount;

    private int EmptyFrameVertices()
    {
        _host.BeginFrame(800, 600, 1f, 1f / 60f);
        Assert.True(Ui.Begin("Empty"));
        Ui.End();
        _host.EndFrame();
        return FrameVertices();
    }

    // Column-major 4x4: a camera 5 meters back from the origin looking down -z, and a perspective projection.
    private static float[] View() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, -5, 1];

    private static float[] Projection()
    {
        const float f = 1.7320508f; // 1 / tan(30 degrees)
        const float near = 0.1f;
        const float far = 100f;
        float aspect = 800f / 600f;
        return [f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, (far + near) / (near - far), -1, 0, 0, 2 * far * near / (near - far), 0];
    }

    [Fact]
    public void AMatrixDecomposesAndRecomposesAndAQuietGizmoChangesNothing()
    {
        float[] matrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 1, 2, 3, 1];
        float[] translation = new float[3];
        float[] rotation = new float[3];
        float[] scale = new float[3];
        fixed (float* m = matrix)
        fixed (float* t = translation)
        fixed (float* r = rotation)
        fixed (float* s = scale)
        {
            Gizmo.DecomposeMatrix(m, t, r, s);
            Assert.Equal([1f, 2f, 3f], translation);
            Assert.Equal([1f, 1f, 1f], scale);
            Gizmo.RecomposeMatrix(t, r, s, m);
        }

        Assert.Equal(1f, matrix[12]);
        Assert.Equal(3f, matrix[14]);

        float[] view = View();
        float[] projection = Projection();
        float[] before = (float[])matrix.Clone();
        float[] viewBefore = (float[])view.Clone();
        bool changed;
        int baseline = EmptyFrameVertices();
        _host.BeginFrame(800, 600, 1f, 1f / 60f);
        Assert.True(Ui.Begin("Gizmo"));
        Gizmo.BeginFrame();
        Gizmo.SetRect(0, 0, 800, 600);
        Gizmo.SetOrthographic(false);
        fixed (float* v = view)
        fixed (float* p = projection)
        fixed (float* m = matrix)
        {
            changed = Gizmo.Manipulate(v, p, (int)AgzOperation.Translate, (int)AgzMode.World, m, null, null, null, null);
            Gizmo.ViewManipulate(v, 8f, 700, 10, 90, 90, 0x10101010);
        }

        Ui.End();
        _host.EndFrame();
        int drawn = FrameVertices() - baseline;

        Assert.False(changed);          // nobody dragged it
        Assert.False(Gizmo.IsUsing());
        Assert.Equal(before, matrix);
        Assert.Equal(viewBefore, view);
        Assert.True(drawn > 50, $"the gizmo and the view cube drew {drawn} vertices");
    }

    [Fact]
    public void APlotDrawsItsItemsInFloatAndDouble()
    {
        float[] values = [1, 3, 2, 5, 4, 6, 3, 2];
        double[] doubles = [1, 3, 2, 5, 4, 6, 3, 2];
        bool began;
        int drawn;
        _host.BeginFrame(800, 600, 1f, 1f / 60f);
        Assert.True(Ui.Begin("Plot"));
        int before = Vertices();
        fixed (byte* title = Utf8.ToZeroTerminated("frame times"))
        fixed (byte* x = Utf8.ToZeroTerminated("frame"))
        fixed (byte* y = Utf8.ToZeroTerminated("ms"))
        fixed (byte* line = Utf8.ToZeroTerminated("line"))
        fixed (byte* shaded = Utf8.ToZeroTerminated("shaded"))
        fixed (byte* bars = Utf8.ToZeroTerminated("bars"))
        fixed (byte* scatter = Utf8.ToZeroTerminated("scatter"))
        fixed (float* f = values)
        fixed (double* d = doubles)
        {
            began = Plot.BeginPlot(title, 300, 200, 0);
            if (began)
            {
                Plot.SetupAxes(x, y, 0, 0);
                Plot.SetupAxesLimits(0, 8, 0, 8, (int)AplotCond.Always);
                Plot.SetupLegend((int)AplotLocation.NorthWest, 0);
                AplotSpec spec = default;
                Plot.DefaultSpec(&spec);
                Plot.PlotLineF(line, f, values.Length, 1, 0, &spec);
                Plot.PlotShadedF(shaded, f, values.Length, 0, 1, 0, null);
                Plot.PlotBarsD(bars, d, doubles.Length, 0.5, 0, null);
                Plot.PlotScatterD(scatter, d, doubles.Length, 1, 0, null);
                Plot.EndPlot();
            }
        }

        drawn = Vertices() - before;
        Ui.End();
        _host.EndFrame();

        Assert.True(began);
        Assert.True(drawn > 200, $"the plot drew {drawn} vertices");
    }

    [Fact]
    public void ANodeEditorDrawsNodesAndALinkAndKeepsTheirPositions()
    {
        float x = 0;
        float y = 0;
        bool created = false;
        int drawn = 0;

        // A window is hidden for its first frame (ImGui sizes it then), so the editor is drawn twice and the second frame is the one that counts.
        _ = EmptyFrameVertices();
        int baseline = EmptyFrameVertices();
        for (int frame = 0; frame < 2; frame++)
        {
            _host.BeginFrame(800, 600, 1f, 1f / 60f);
            Assert.True(Ui.Begin("Graph"));
            Nodes.BeginNodeEditor();
            Nodes.BeginNode(1);
            Nodes.BeginNodeTitleBar();
            Ui.Text("Depth prepass");
            Nodes.EndNodeTitleBar();
            Nodes.BeginOutputAttribute(2, (int)AnodesPinShape.CircleFilled);
            Ui.Text("depth");
            Nodes.EndOutputAttribute();
            Nodes.EndNode();
            Nodes.SetNodeGridSpacePos(1, 10, 20);
            Nodes.BeginNode(3);
            Nodes.BeginNodeTitleBar();
            Ui.Text("Forward");
            Nodes.EndNodeTitleBar();
            Nodes.BeginInputAttribute(4, (int)AnodesPinShape.CircleFilled);
            Ui.Text("depth");
            Nodes.EndInputAttribute();
            Nodes.EndNode();
            Nodes.SetNodeGridSpacePos(3, 200, 20);
            Nodes.Link(5, 2, 4);
            Nodes.Minimap(0.2f, (int)AnodesMiniMapLocation.BottomRight);
            Nodes.EndNodeEditor();

            Nodes.GetNodeGridSpacePos(1, &x, &y);
            int start = 0;
            int end = 0;
            created = Nodes.IsLinkCreated(&start, &end);
            Ui.End();
            _host.EndFrame();
            drawn = FrameVertices() - baseline;
        }

        Assert.Equal(10f, x);
        Assert.Equal(20f, y);
        Assert.False(created); // nobody dragged a link
        Assert.Equal(0, Nodes.NumSelectedNodes());
        Assert.True(drawn > 100, $"the editor drew {drawn} vertices");
    }
}
