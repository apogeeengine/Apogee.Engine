using Apogee.Core.Graphics;
using System.Linq;
using System.Numerics;
using Apogee.Graphics;
using Apogee.Rendering.Graph;
using Xunit;

namespace Apogee.Rendering.Tests;

/// <summary>The graph compiler needs no GPU: handles are just numbers here.</summary>
public class PlannerTests
{
    private static ImageDescription Color(uint size = 64) =>
        new(PixelFormat.R8G8B8A8Unorm, size, size, ImageUsage.ColorAttachment | ImageUsage.Sampled);

    private static ImageHandle Backbuffer(RenderGraph graph, uint size = 64) =>
        graph.ImportBackbuffer(new Backbuffer(new FakeImage(PixelFormat.B8G8R8A8Unorm, size), new FakeView(), PixelFormat.B8G8R8A8Unorm, size, size));

    private static PassPlan Pass(GraphPlan plan, string name) => plan.Passes.Single(p => p.Name == name);

    [Fact]
    public void PassesNothingDependsOnAreCulled()
    {
        var graph = new RenderGraph();
        ImageHandle back = Backbuffer(graph);
        ImageHandle unused = graph.CreateImage("unused", Color());
        ImageHandle used = graph.CreateImage("used", Color());
        graph.AddPass("Wasted", PassType.Graphics, p => p.WriteColor(unused, Vector4.Zero));
        graph.AddPass("Produce", PassType.Graphics, p => p.WriteColor(used, Vector4.Zero));
        graph.AddPass("Compose", PassType.Graphics, p => { p.ReadSampled(used); p.WriteColor(back, Vector4.Zero); });

        GraphPlan plan = graph.Compile();

        Assert.True(Pass(plan, "Wasted").Culled);
        Assert.False(Pass(plan, "Produce").Culled);
        Assert.False(Pass(plan, "Compose").Culled);
        Assert.Equal(2, plan.LivePassCount);
        Assert.Equal(1, plan.CulledPassCount);
        Assert.Contains("(culled) Wasted", plan.ToString());
    }

    [Fact]
    public void APassWithSideEffectsIsNeverCulled()
    {
        var graph = new RenderGraph();
        ImageHandle scratch = graph.CreateImage("scratch", Color());
        graph.AddPass("Debug print", PassType.Graphics, p => { p.WriteColor(scratch, Vector4.Zero); p.NoCull(); });

        Assert.False(Pass(graph.Compile(), "Debug print").Culled);
    }

    [Fact]
    public void ACompleteOverwriteMakesTheEarlierWriterUnneeded()
    {
        var graph = new RenderGraph();
        ImageHandle back = Backbuffer(graph);
        ImageHandle t = graph.CreateImage("t", Color());
        graph.AddPass("First", PassType.Graphics, p => p.WriteColor(t, Vector4.Zero));
        graph.AddPass("Second", PassType.Graphics, p => p.WriteColor(t, Vector4.One));
        graph.AddPass("Use", PassType.Graphics, p => { p.ReadSampled(t); p.WriteColor(back, Vector4.Zero); });

        GraphPlan plan = graph.Compile();

        Assert.True(Pass(plan, "First").Culled);
        Assert.False(Pass(plan, "Second").Culled);
    }

    [Fact]
    public void LoadingAttachmentsKeepsTheEarlierWriter()
    {
        var graph = new RenderGraph();
        ImageHandle back = Backbuffer(graph);
        ImageHandle t = graph.CreateImage("t", Color());
        graph.AddPass("First", PassType.Graphics, p => p.WriteColor(t, Vector4.Zero));
        graph.AddPass("Blend on top", PassType.Graphics, p => p.WriteColor(t));      // loads
        graph.AddPass("Use", PassType.Graphics, p => { p.ReadSampled(t); p.WriteColor(back, Vector4.Zero); });

        GraphPlan plan = graph.Compile();

        Assert.False(Pass(plan, "First").Culled);
        Assert.False(Pass(plan, "Blend on top").Culled);
    }

    [Fact]
    public void BarriersFollowTheStatesAndTheBackbufferEndsInPresent()
    {
        var graph = new RenderGraph();
        ImageHandle back = Backbuffer(graph);
        ImageHandle t = graph.CreateImage("t", Color());
        graph.AddPass("Produce", PassType.Graphics, p => p.WriteColor(t, Vector4.Zero));
        graph.AddPass("Compose", PassType.Graphics, p => { p.ReadSampled(t); p.WriteColor(back, Vector4.Zero); });

        GraphPlan plan = graph.Compile();

        Assert.Equal(1, Pass(plan, "Produce").Barriers);     // t: Undefined -> ColorAttachment
        Assert.Equal(2, Pass(plan, "Compose").Barriers);     // t: ColorAttachment -> ShaderRead, back: Undefined -> ColorAttachment
        Assert.Equal(4, plan.BarrierCount);                  // and back: ColorAttachment -> Present at the end
    }

    [Fact]
    public void ReadingAfterReadingNeedsNoBarrier()
    {
        var graph = new RenderGraph();
        ImageHandle back = Backbuffer(graph);
        ImageHandle t = graph.CreateImage("t", Color());
        graph.AddPass("Produce", PassType.Graphics, p => p.WriteColor(t, Vector4.Zero));
        graph.AddPass("Read A", PassType.Compute, p => { p.ReadSampled(t); p.NoCull(); });
        graph.AddPass("Read B", PassType.Graphics, p => { p.ReadSampled(t); p.WriteColor(back, Vector4.Zero); });

        GraphPlan plan = graph.Compile();

        Assert.Equal(1, Pass(plan, "Read A").Barriers);      // the first read after the write
        Assert.Equal(1, Pass(plan, "Read B").Barriers);      // only the backbuffer
    }

    [Fact]
    public void WritingAfterWritingNeedsABarrierEvenInTheSameState()
    {
        var graph = new RenderGraph();
        ImageHandle storage = graph.CreateImage("storage", new ImageDescription(PixelFormat.R8G8B8A8Unorm, 8, 8, ImageUsage.Storage));
        graph.AddPass("Write 1", PassType.Compute, p => { p.WriteStorage(storage); p.NoCull(); });
        graph.AddPass("Write 2", PassType.Compute, p => { p.ReadWriteStorage(storage); p.NoCull(); });

        GraphPlan plan = graph.Compile();

        Assert.Equal(1, Pass(plan, "Write 1").Barriers);     // Undefined -> General
        Assert.Equal(1, Pass(plan, "Write 2").Barriers);     // General -> General: the second write must wait for the first
    }

    [Fact]
    public void BufferStatesProduceBarriersToo()
    {
        var graph = new RenderGraph();
        BufferHandle buffer = graph.CreateBuffer("list", 1024, BufferUsage.Storage);
        graph.AddPass("Fill", PassType.Compute, p => { p.Write(buffer); p.NoCull(); });
        graph.AddPass("Use", PassType.Compute, p => { p.Read(buffer); p.NoCull(); });
        graph.AddPass("Use again", PassType.Compute, p => { p.Read(buffer); p.NoCull(); });

        GraphPlan plan = graph.Compile();

        Assert.Equal(1, Pass(plan, "Fill").Barriers);        // Undefined -> ShaderWrite
        Assert.Equal(1, Pass(plan, "Use").Barriers);         // ShaderWrite -> ShaderRead
        Assert.Equal(0, Pass(plan, "Use again").Barriers);
    }

    [Fact]
    public void TransientsWithDisjointLifetimesAndEqualDescriptionsShareAnImage()
    {
        var graph = new RenderGraph();
        ImageHandle back = Backbuffer(graph);
        ImageHandle a = graph.CreateImage("a", Color());
        ImageHandle b = graph.CreateImage("b", Color());
        graph.AddPass("Make a", PassType.Graphics, p => p.WriteColor(a, Vector4.Zero));
        graph.AddPass("Use a", PassType.Graphics, p => { p.ReadSampled(a); p.WriteColor(back, Vector4.Zero); });
        graph.AddPass("Make b", PassType.Graphics, p => p.WriteColor(b, Vector4.Zero));
        graph.AddPass("Use b", PassType.Graphics, p => { p.ReadSampled(b); p.WriteColor(back); });

        GraphPlan plan = graph.Compile();

        Assert.Equal(2, plan.TransientImageCount);
        Assert.Equal(1, plan.PhysicalImageCount);
    }

    [Fact]
    public void OverlappingOrDifferentTransientsDoNotShare()
    {
        var overlapping = new RenderGraph();
        ImageHandle back = Backbuffer(overlapping);
        ImageHandle a = overlapping.CreateImage("a", Color());
        ImageHandle b = overlapping.CreateImage("b", Color());
        overlapping.AddPass("Make a", PassType.Graphics, p => p.WriteColor(a, Vector4.Zero));
        overlapping.AddPass("Make b", PassType.Graphics, p => p.WriteColor(b, Vector4.Zero));
        overlapping.AddPass("Use both", PassType.Graphics, p => { p.ReadSampled(a); p.ReadSampled(b); p.WriteColor(back, Vector4.Zero); });
        Assert.Equal(2, overlapping.Compile().PhysicalImageCount);

        var different = new RenderGraph();
        ImageHandle back2 = Backbuffer(different);
        ImageHandle c = different.CreateImage("c", Color(64));
        ImageHandle d = different.CreateImage("d", Color(32));
        different.AddPass("Make c", PassType.Graphics, p => p.WriteColor(c, Vector4.Zero));
        different.AddPass("Use c", PassType.Graphics, p => { p.ReadSampled(c); p.WriteColor(back2, Vector4.Zero); });
        different.AddPass("Make d", PassType.Graphics, p => p.WriteColor(d, Vector4.Zero));
        different.AddPass("Use d", PassType.Graphics, p => { p.ReadSampled(d); p.WriteColor(back2); });
        Assert.Equal(2, different.Compile().PhysicalImageCount);
    }

    [Fact]
    public void TransientBuffersOfTheSameSizeShareToo()
    {
        var graph = new RenderGraph();
        BufferHandle a = graph.CreateBuffer("a", 4096, BufferUsage.Storage);
        BufferHandle b = graph.CreateBuffer("b", 4096, BufferUsage.Storage);
        graph.AddPass("Fill a", PassType.Compute, p => { p.Write(a); p.NoCull(); });
        graph.AddPass("Use a", PassType.Compute, p => { p.Read(a); p.NoCull(); });
        graph.AddPass("Fill b", PassType.Compute, p => { p.Write(b); p.NoCull(); });
        graph.AddPass("Use b", PassType.Compute, p => { p.Read(b); p.NoCull(); });

        Assert.Equal(1, graph.Compile().PhysicalBufferCount);
    }

    [Fact]
    public void AReadWriteNeedsContentsButAWriteDoesNot()
    {
        var fresh = new RenderGraph();
        BufferHandle counter = fresh.CreateBuffer("counter", 16, BufferUsage.Storage);
        fresh.AddPass("Increment", PassType.Compute, p => { p.ReadWrite(counter); p.NoCull(); });
        Assert.Contains("counter", Assert.Throws<RenderGraphException>(() => fresh.Compile()).Message);

        var initialized = new RenderGraph();
        BufferHandle list = initialized.CreateBuffer("list", 16, BufferUsage.Storage);
        initialized.AddPass("Reset", PassType.Compute, p => { p.Write(list); p.NoCull(); });
        initialized.AddPass("Append", PassType.Compute, p => { p.ReadWrite(list); p.NoCull(); });
        Assert.Equal(2, initialized.Compile().LivePassCount);
    }

    [Fact]
    public void ReadingSomethingNobodyWroteIsAnError()
    {
        var graph = new RenderGraph();
        ImageHandle back = Backbuffer(graph);
        ImageHandle never = graph.CreateImage("never written", Color());
        graph.AddPass("Compose", PassType.Graphics, p => { p.ReadSampled(never); p.WriteColor(back, Vector4.Zero); });

        var error = Assert.Throws<RenderGraphException>(() => graph.Compile());
        Assert.Contains("never written", error.Message);
        Assert.Contains("Compose", error.Message);
    }

    [Fact]
    public void LoadingAnImageThatHasNoContentsIsAnError()
    {
        var graph = new RenderGraph();
        ImageHandle back = Backbuffer(graph);
        graph.AddPass("Draw", PassType.Graphics, p => p.WriteColor(back));        // loads the undefined backbuffer

        Assert.Throws<RenderGraphException>(() => graph.Compile());
    }

    [Fact]
    public void UsingOneResourceTwoWaysInOnePassIsAnError()
    {
        var graph = new RenderGraph();
        ImageHandle t = graph.CreateImage("t", Color());

        var error = Assert.Throws<RenderGraphException>(() => graph.AddPass("Both", PassType.Graphics, p => { p.ReadSampled(t); p.WriteColor(t, Vector4.Zero); }));
        Assert.Contains("two different ways", error.Message);
    }

    [Fact]
    public void AttachmentsMustBeTheSameSizeAndInAGraphicsPass()
    {
        var sizes = new RenderGraph();
        ImageHandle a = sizes.CreateImage("a", Color(64));
        ImageHandle b = sizes.CreateImage("b", Color(32));
        sizes.AddPass("Mismatch", PassType.Graphics, p => { p.WriteColor(a, Vector4.Zero); p.WriteColor(b, Vector4.Zero); p.NoCull(); });
        Assert.Contains("different sizes", Assert.Throws<RenderGraphException>(() => sizes.Compile()).Message);

        var compute = new RenderGraph();
        ImageHandle c = compute.CreateImage("c", Color());
        compute.AddPass("Wrong kind", PassType.Compute, p => { p.WriteColor(c, Vector4.Zero); p.NoCull(); });
        Assert.Contains("not a graphics pass", Assert.Throws<RenderGraphException>(() => compute.Compile()).Message);
    }

    [Fact]
    public void HandlesOfAnotherGraphAreRejected()
    {
        var one = new RenderGraph();
        var two = new RenderGraph();
        ImageHandle foreign = one.CreateImage("t", Color());

        Assert.Throws<RenderGraphException>(() => two.AddPass("Mixed", PassType.Graphics, p => p.ReadSampled(foreign)));
    }

    [Fact]
    public void TransientsHaveOneMipAndNoMultisampling()
    {
        var graph = new RenderGraph();

        Assert.Throws<RenderGraphException>(() => graph.CreateImage("mips", Color() with { MipLevels = 3 }));
        Assert.Throws<RenderGraphException>(() => graph.CreateImage("msaa", Color() with { Samples = SampleCount.Count4 }));
    }

    [Fact]
    public void ADepthPrepassFeedsLaterPassesThroughDepthRead()
    {
        var graph = new RenderGraph();
        ImageHandle back = Backbuffer(graph);
        ImageHandle depth = graph.CreateImage("depth", new ImageDescription(PixelFormat.D32Sfloat, 64, 64, ImageUsage.DepthStencilAttachment | ImageUsage.Sampled));
        graph.AddPass("Depth prepass", PassType.Graphics, p => p.WriteDepth(depth));
        graph.AddPass("Opaque", PassType.Graphics, p => { p.ReadDepth(depth); p.WriteColor(back, Vector4.Zero); });

        GraphPlan plan = graph.Compile();

        Assert.Equal(1, Pass(plan, "Depth prepass").Barriers);
        Assert.Equal(2, Pass(plan, "Opaque").Barriers);      // depth: DepthAttachment -> DepthRead, back: Undefined -> ColorAttachment
    }
}

/// <summary>An image that is not on any GPU, for planning a graph (the planner never touches the GPU).</summary>
internal sealed class FakeImage(PixelFormat format, uint size) : GpuImage(new ImageDescription(format, size, size, ImageUsage.ColorAttachment))
{
    public override uint Layers => 1;

    public override ImageView View { get; } = new FakeView();

    public override ImageView CreateView(uint baseMip, uint mipCount, uint baseLayer, uint layerCount) => new FakeView();

    public override void DestroyView(ImageView view)
    {
    }

    public override void Dispose()
    {
    }
}

internal sealed class FakeView : ImageView
{
}
