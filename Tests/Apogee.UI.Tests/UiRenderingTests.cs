using Apogee.Core.Graphics;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Apogee.Content;
using Apogee.Graphics;
using Apogee.Graphics.Tests;
using Apogee.Rendering.Graph;
using Xunit;

namespace Apogee.UI.Tests;

/// <summary>Documents drawn by <see cref="UiRenderer"/> into an offscreen image and read back.</summary>
public sealed class UiRenderingTests(GpuFixture gpu) : GpuTest(gpu)
{
    private const int Width = 200;
    private const int Height = 100;

    private static readonly string s_shaderCache = Path.Combine(Path.GetTempPath(), "apogee-test-shader-cache");

    /// <summary>A UI system with a renderer, and an image to draw into: black, as the frame would be before the UI goes over it.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly GraphicsDevice _device;
        private readonly GpuImage _target;
        private readonly FrameLoop _loop;
        private readonly TransientPool _pool;

        public Rig(GraphicsDevice device)
        {
            _device = device;
            Files = new UiRig(Width, Height);
            Renderer = new UiRenderer(device, Files.Ui, cacheDirectory: s_shaderCache);
            _target = device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, Width, Height, ImageUsage.ColorAttachment), "UI target");
            _loop = device.CreateFrameLoop();
            _pool = new TransientPool(device);
        }

        public UiRig Files { get; }

        public UiSystem Ui => Files.Ui;

        public UiRenderer Renderer { get; }

        /// <summary>One frame: clear to <paramref name="background"/>, then the UI. The image is RGBA bytes, row by row.</summary>
        public byte[] Frame(Vector4? background = null)
        {
            Frame frame = _loop.BeginFrame()!;
            var graph = new RenderGraph(_device, _pool);
            ImageHandle image = graph.ImportImage("Output", _target, ImageState.Undefined, ImageState.TransferSource, output: true);
            graph.AddPass("Clear", PassType.Graphics, pass => pass.WriteColor(image, background ?? new Vector4(0, 0, 0, 1)));
            Renderer.AddPasses(graph, image, frame);
            graph.Execute(frame);
            _loop.EndFrame(frame);
            _loop.WaitIdle();
            return _device.ReadImage(_target, ImageState.TransferSource);
        }

        public void Dispose()
        {
            _loop.WaitIdle();
            Renderer.Dispose();
            _pool.Dispose();
            _loop.Dispose();
            _target.Dispose();
            Files.Dispose();
        }
    }

    private static (byte R, byte G, byte B, byte A) At(byte[] image, int x, int y) => (image[((y * Width) + x) * 4], image[((y * Width) + x) * 4 + 1], image[((y * Width) + x) * 4 + 2], image[((y * Width) + x) * 4 + 3]);

    private static int Count(byte[] image, Func<(byte R, byte G, byte B, byte A), bool> test, int firstRow = 0)
    {
        int count = 0;
        for (int y = firstRow; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                count += test(At(image, x, y)) ? 1 : 0;
            }
        }

        return count;
    }

    private static void Near((byte R, byte G, byte B, byte A) actual, int r, int g, int b, int tolerance = 2) =>
        Assert.True(Math.Abs(actual.R - r) <= tolerance && Math.Abs(actual.G - g) <= tolerance && Math.Abs(actual.B - b) <= tolerance, $"expected about ({r}, {g}, {b}), got ({actual.R}, {actual.G}, {actual.B})");

    [Fact]
    public void ABoxIsDrawnWhereTheLayoutPutsItInItsColor()
    {
        using var rig = new Rig(Device);
        rig.Files.Show("<rml><body><div style=\"margin-left: 20px; margin-top: 10px; width: 50px; height: 30px; background-color: #ff0000\"></div></body></rml>");

        byte[] image = rig.Frame();

        Near(At(image, 30, 20), 255, 0, 0);   // inside
        Near(At(image, 69, 39), 255, 0, 0);   // the last pixel
        Near(At(image, 15, 20), 0, 0, 0);     // left of it
        Near(At(image, 30, 45), 0, 0, 0);     // below it
        Assert.InRange(Count(image, p => p.R > 200 && p.G < 50), 50 * 30 - 60, 50 * 30 + 60);
        Assert.InRange(rig.Renderer.LastFrame.Draws, 1, 4);
    }

    [Fact]
    public void ColorsBlendOverWhatIsBeneathAsPremultipliedAlpha()
    {
        using var rig = new Rig(Device);
        rig.Files.Show("<rml><body><div style=\"width: 100px; height: 50px; background-color: rgba(255, 0, 0, 128)\"></div></body></rml>");

        // Half-transparent red over black is half red; over white it is pink: color * alpha + background * (1 - alpha).
        Near(At(rig.Frame(), 50, 25), 128, 0, 0, 3);
        Near(At(rig.Frame(new Vector4(1, 1, 1, 1)), 50, 25), 255, 127, 127, 3);
    }

    [Fact]
    public void OverflowHiddenClipsWithTheScissorAndChildrenDrawInOrder()
    {
        using var rig = new Rig(Device);
        rig.Files.Show("""
            <rml><body>
              <div style="width: 40px; height: 40px; overflow: hidden; background-color: #0000ff">
                <div style="width: 200px; height: 200px; background-color: #00ff00"></div>
              </div>
            </body></rml>
            """);

        byte[] image = rig.Frame();

        Near(At(image, 20, 20), 0, 255, 0);   // the child, inside the clip
        Near(At(image, 39, 39), 0, 255, 0);
        Near(At(image, 41, 20), 0, 0, 0);     // outside the container: clipped, though the child is 200 wide
        Near(At(image, 20, 41), 0, 0, 0);
        Assert.InRange(Count(image, p => p.G > 200), 40 * 40 - 80, 40 * 40 + 80);
    }

    [Fact]
    public void ACssTransformMovesTheGeometry()
    {
        using var rig = new Rig(Device);
        rig.Files.Show("<rml><body><div style=\"width: 20px; height: 20px; background-color: #ff0000; transform: translateX(60px)\"></div></body></rml>");

        byte[] image = rig.Frame();

        Near(At(image, 70, 10), 255, 0, 0);   // where the transform put it
        Near(At(image, 10, 10), 0, 0, 0);     // and not where it was laid out
    }

    [Fact]
    public void ARotatedBoxIsAParallelogramOfTheSameArea()
    {
        using var rig = new Rig(Device);
        rig.Files.Show("<rml><body><div style=\"margin: 40px 0 0 90px; width: 30px; height: 30px; background-color: #ff0000; transform: rotate(45deg)\"></div></body></rml>");

        byte[] image = rig.Frame();

        Near(At(image, 105, 55), 255, 0, 0);                                   // the center stays
        Near(At(image, 91, 41), 0, 0, 0);                                      // a corner of the unrotated box is now outside
        Assert.InRange(Count(image, p => p.R > 200 && p.G < 50), 800, 1000);   // 30 x 30 = 900, turned
    }

    [Fact]
    public void TextIsDrawnWithGlyphsFromTheDefaultFont()
    {
        using var rig = new Rig(Device);
        rig.Files.Show("<rml><body><div style=\"font-size: 24px; color: #ffffff; margin: 10px\">Hello UI</div></body></rml>");

        byte[] image = rig.Frame();

        int lit = Count(image, p => p.R > 128);
        Assert.InRange(lit, 100, 2500);                                         // glyphs: some, and far from a filled box
        Assert.True(rig.Renderer.TextureCount >= 1, "the glyph atlas is a texture");
        // Nothing below the line: the text is at the top left, some 30 pixels tall.
        Assert.Equal(0, Count(image, p => p.R > 128, firstRow: 60));
    }

    [Fact]
    public void AnImageIsLoadedThroughTheFileSystemAndBlendsItsAlpha()
    {
        using var rig = new Rig(Device);
        // A 2 x 2 image: opaque green on the left, half-transparent (straight alpha) red on the right.
        byte[] pixels =
        [
            0, 255, 0, 255, 255, 0, 0, 128,
            0, 255, 0, 255, 255, 0, 0, 128,
        ];
        rig.Files.Write("pics/two.png", Png.Encode(2, 2, pixels));
        rig.Files.Show("<rml><head><style>img { width: 100px; height: 100px; image-rendering: pixelated; }</style></head><body><img src=\"res://pics/two.png\"/></body></rml>");

        byte[] image = rig.Frame();

        Near(At(image, 20, 50), 0, 255, 0);       // the opaque half
        Near(At(image, 80, 50), 128, 0, 0, 4);    // the red half over black: 255 * 0.5, from a texture premultiplied on loading
        Assert.Equal(1, rig.Renderer.TextureCount); // the image; no text, so no glyph atlas
    }

    [Fact]
    public void AGeometryIsPackedOnceNoMatterHowOftenItIsDrawnAndReleasedWhenTheDocumentCloses()
    {
        using var rig = new Rig(Device);
        UiElement document = rig.Files.Show("<rml><body><div id=\"row\"><div style=\"height: 6px; background-color: #ff0000\"></div><div style=\"height: 6px; background-color: #ff0000\"></div><div style=\"height: 6px; background-color: #ff0000\"></div></div></body></rml>");
        rig.Frame();
        int draws = rig.Renderer.LastFrame.Draws;
        Assert.True(draws >= 3);
        int geometry = rig.Renderer.GeometryCount;
        Assert.True(geometry >= 3);

        // A second frame draws the same compiled geometry again: nothing is compiled twice.
        rig.Frame();
        Assert.Equal(geometry, rig.Renderer.GeometryCount);

        document.Close();
        rig.Ui.Update(0.016f);
        byte[] after = rig.Frame();
        Assert.Equal(0, rig.Renderer.LastFrame.Draws);
        Assert.Equal(0, Count(after, p => p.R > 0));
        Assert.Equal(0, rig.Renderer.GeometryCount);
    }

    [Fact]
    public void ChangingTheTreeChangesThePictureOnTheNextFrame()
    {
        using var rig = new Rig(Device);
        UiElement document = rig.Files.Show("<rml><body><div id=\"box\" style=\"width: 50px; height: 50px; background-color: #ff0000\"></div></body></rml>");
        UiElement box = document.ById("box")!;
        Near(At(rig.Frame(), 25, 25), 255, 0, 0);

        box.SetProperty("background-color", "#0000ff");
        rig.Ui.Update(0.016f);
        Near(At(rig.Frame(), 25, 25), 0, 0, 255);

        box.SetProperty("display", "none");
        rig.Ui.Update(0.016f);
        Near(At(rig.Frame(), 25, 25), 0, 0, 0);

        // A new element appended in code is drawn too.
        UiElement made = document.CreateElement("div")!;
        made.SetProperty("width", "30px");
        made.SetProperty("height", "30px");
        made.SetProperty("background-color", "#ffff00");
        document.Append(made);
        rig.Ui.Update(0.016f);
        Near(At(rig.Frame(), 10, 10), 255, 255, 0);
    }

    [Fact]
    public void ResizingTheScreenResizesWhatIsDrawn()
    {
        using var rig = new Rig(Device);
        rig.Files.Show("<rml><body><div style=\"width: 50%; height: 100%; background-color: #ff0000\"></div></body></rml>");
        byte[] image = rig.Frame();
        Near(At(image, 99, 50), 255, 0, 0);
        Near(At(image, 101, 50), 0, 0, 0);

        // The renderer projects at the context's size, which a host keeps equal to the window's. Here the target stays 200 wide while the context becomes 100: what is 50 pixels
        // wide in the document now covers 100 of the target's.
        rig.Ui.SetSize(100, 100);
        rig.Ui.Update(0.016f);
        byte[] smaller = rig.Frame();
        Near(At(smaller, 99, 50), 255, 0, 0);
        Near(At(smaller, 101, 50), 0, 0, 0);
    }

    [Fact]
    public void ACustomPropertySetFromCodeRestylesEverythingThatUsesIt()
    {
        using var rig = new Rig(Device);
        UiElement document = rig.Files.Show("""
            <rml><head><style>
              body { --accent: #ff0000; }
              .a { width: 40px; height: 40px; background-color: var(--accent); }
            </style></head><body><div class="a"></div><div id="inner"><div class="a"></div></div></body></rml>
            """);
        Near(At(rig.Frame(), 20, 20), 255, 0, 0);

        // The variable is defined on body and inherited: changing it there changes every element that reads it, wherever it is in the tree.
        UiElement body = document;
        Assert.True(body.SetProperty("--accent", "#00ff00"));
        rig.Ui.Update(0.016f);
        byte[] image = rig.Frame();
        Near(At(image, 20, 20), 0, 255, 0);
        Near(At(image, 20, 60), 0, 255, 0);

        // A descendant that defines it itself overrides it for its own subtree: how a part of the UI wears another theme.
        document.ById("inner")!.SetProperty("--accent", "#0000ff");
        rig.Ui.Update(0.016f);
        image = rig.Frame();
        Near(At(image, 20, 20), 0, 255, 0);
        Near(At(image, 20, 60), 0, 0, 255);
    }

    [Fact]
    public void ARoundedContainerClipsItsChildrenWithAMask()
    {
        using var rig = new Rig(Device);
        rig.Files.Show("""
            <rml><body>
              <div style="width: 80px; height: 80px; overflow: hidden; border-radius: 40px; background-color: #0000ff">
                <div style="width: 200px; height: 200px; background-color: #00ff00"></div>
              </div>
            </body></rml>
            """);

        byte[] image = rig.Frame();

        Near(At(image, 40, 40), 0, 255, 0);   // the middle of the circle
        Near(At(image, 2, 2), 0, 0, 0);       // the corner of the box is outside the circle, though inside the box
        Near(At(image, 77, 77), 0, 0, 0);
        Assert.InRange(Count(image, p => p.G > 200), 4850, 5150);   // pi * 40 * 40 = 5027: far from the 6400 of a square
        Assert.True(rig.Renderer.LastEffects.Masks >= 1);
    }

    [Fact]
    public void TheOpacityFilterDrawsAGroupIntoALayerAndCompositesItOnce()
    {
        using var rig = new Rig(Device);
        // Two overlapping boxes at half opacity as a group: where they overlap, the one on top hides the other. (The opacity property is baked into each element's colors by RmlUi, which would show the
        // red through the green: only the filter makes a group.)
        rig.Files.Show("""
            <rml><body>
              <div style="filter: opacity(0.5); width: 100px; height: 60px">
                <div style="width: 60px; height: 60px; background-color: #ff0000"></div>
                <div style="margin-top: -60px; margin-left: 30px; width: 60px; height: 60px; background-color: #00ff00"></div>
              </div>
            </body></rml>
            """);

        byte[] image = rig.Frame();

        Near(At(image, 10, 30), 128, 0, 0, 4);    // red alone
        Near(At(image, 80, 30), 0, 128, 0, 4);    // green alone
        Near(At(image, 45, 30), 0, 128, 0, 4);    // overlap: green on top, then the whole group at half
        Assert.True(rig.Renderer.LastEffects.Layers >= 1);
        Assert.True(rig.Renderer.LastEffects.Composites >= 1);
    }

    [Fact]
    public void ABlurFilterSpreadsAnEdge()
    {
        using var rig = new Rig(Device);
        rig.Files.Show("<rml><body><div style=\"margin: 20px 0 0 50px; width: 100px; height: 60px; background-color: #ffffff; filter: blur(6px)\"></div></body></rml>");

        byte[] image = rig.Frame();

        Near(At(image, 100, 50), 255, 255, 255, 4);                     // the middle stays
        (byte R, byte G, byte B, byte A) edge = At(image, 50, 50);     // the left edge is half
        Assert.InRange((int)edge.R, 60, 200);
        Assert.True(At(image, 45, 50).R > 0, "the blur reaches outside the box");
        Assert.True(At(image, 45, 50).R < At(image, 55, 50).R);
    }

    [Fact]
    public void AGrayscaleFilterRemovesTheColor()
    {
        using var rig = new Rig(Device);
        rig.Files.Show("<rml><body><div style=\"width: 60px; height: 60px; background-color: #ff0000; filter: grayscale(1)\"></div></body></rml>");

        (byte R, byte G, byte B, byte A) p = At(rig.Frame(), 30, 30);

        Assert.True(Math.Abs(p.R - p.G) <= 3 && Math.Abs(p.G - p.B) <= 3, $"expected gray, got ({p.R}, {p.G}, {p.B})");
        Assert.InRange((int)p.R, 40, 120);
    }

    [Fact]
    public void ALinearGradientRunsBetweenItsStops()
    {
        using var rig = new Rig(Device);
        rig.Files.Show("""
            <rml><head><style>div { width: 100px; height: 40px; decorator: linear-gradient(to right, #ff0000, #0000ff); }</style></head><body><div></div></body></rml>
            """);

        byte[] image = rig.Frame();

        Near(At(image, 1, 20), 255, 0, 0, 12);
        Near(At(image, 98, 20), 0, 0, 255, 12);
        (byte R, byte G, byte B, byte A) middle = At(image, 50, 20);
        Assert.InRange((int)middle.R, 100, 155);
        Assert.InRange((int)middle.B, 100, 155);
    }

    [Fact]
    public void ABoxShadowIsDrawnBehindTheBox()
    {
        using var rig = new Rig(Device);
        rig.Files.Show("<rml><body><div style=\"margin: 20px; width: 60px; height: 40px; background-color: #ff0000; box-shadow: #00ff00 30px 0px 0px 0px\"></div></body></rml>");

        byte[] image = rig.Frame();

        Near(At(image, 40, 40), 255, 0, 0);    // the box on top
        Near(At(image, 100, 40), 0, 255, 0);   // the shadow, shifted right, shows where the box is not
    }

    [Fact]
    public void AMaskImageMultipliesByItsAlpha()
    {
        using var rig = new Rig(Device);
        rig.Files.Show("<rml><body><div style=\"width: 100px; height: 40px; background-color: #ffffff; mask-image: linear-gradient(to right, transparent, white)\"></div></body></rml>");

        byte[] image = rig.Frame();

        Assert.True(At(image, 5, 20).R < 50, "transparent on the left");
        Assert.True(At(image, 95, 20).R > 200, "opaque on the right");
        Assert.True(At(image, 50, 20).R is > 80 and < 180);
    }

    [Fact]
    public void ADropShadowFilterDrawsTheShadowBesideTheBox()
    {
        using var rig = new Rig(Device);
        rig.Files.Show("<rml><body><div style=\"margin: 20px; width: 40px; height: 40px; background-color: #ff0000; filter: drop-shadow(#00ff00 40px 0px 0px)\"></div></body></rml>");

        byte[] image = rig.Frame();

        Near(At(image, 40, 40), 255, 0, 0);
        Near(At(image, 80, 40), 0, 255, 0, 6);
    }
}
