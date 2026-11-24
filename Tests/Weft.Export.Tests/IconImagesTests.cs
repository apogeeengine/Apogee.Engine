using System;
using System.Linq;
using Weft.Content;
using Xunit;

namespace Weft.Export.Tests;

public class IconImagesTests
{
    /// <summary>A square image whose pixels all have one color, or the color of its quadrant: red, green, blue, white.</summary>
    private static byte[] Quadrants(int size)
    {
        byte[] rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                (byte r, byte g, byte b) = (x < size / 2, y < size / 2) switch
                {
                    (true, true) => ((byte)255, (byte)0, (byte)0),
                    (false, true) => ((byte)0, (byte)255, (byte)0),
                    (true, false) => ((byte)0, (byte)0, (byte)255),
                    _ => ((byte)255, (byte)255, (byte)255),
                };
                int at = (y * size + x) * 4;
                rgba[at] = r;
                rgba[at + 1] = g;
                rgba[at + 2] = b;
                rgba[at + 3] = 255;
            }
        }

        return rgba;
    }

    [Fact]
    public void AnImageIsDownscaledByAveragingTheAreaItCovers()
    {
        // A checkerboard of black and white pixels is mid grey at half the size.
        byte[] rgba = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++)
        {
            byte value = (byte)(((i % 4) + (i / 4)) % 2 == 0 ? 255 : 0);
            rgba[i * 4] = rgba[(i * 4) + 1] = rgba[(i * 4) + 2] = value;
            rgba[(i * 4) + 3] = 255;
        }

        IconImages icon = IconImages.Load(Png.Encode(4, 4, rgba));
        byte[] half = icon.Rgba(2);

        Assert.Equal(2 * 2 * 4, half.Length);
        for (int pixel = 0; pixel < 4; pixel++)
        {
            Assert.InRange(half[pixel * 4], 127, 128);
            Assert.Equal(255, half[(pixel * 4) + 3]);
        }
    }

    [Fact]
    public void ItsOwnSizeIsTheImageAndQuadrantsStayWhereTheyAre()
    {
        byte[] source = Quadrants(512);
        IconImages icon = IconImages.Load(Png.Encode(512, 512, source));

        Assert.Equal(source, icon.Rgba(512));
        byte[] small = icon.Rgba(16);
        Assert.Equal([255, 0, 0, 255], small[..4]);                              // top left: red
        Assert.Equal([0, 255, 0, 255], small[(15 * 4)..(16 * 4)]);               // top right: green
        Assert.Equal([0, 0, 255, 255], small[(15 * 16 * 4)..((15 * 16 * 4) + 4)]); // bottom left: blue
        Assert.Empty(icon.Warnings);
    }

    [Fact]
    public void TransparentPixelsDoNotTintTheColorsAroundThem()
    {
        // The left half is opaque white, the right half fully transparent black: averaged with the black, white would go grey.
        byte[] rgba = new byte[2 * 2 * 4];
        for (int y = 0; y < 2; y++)
        {
            int at = y * 2 * 4;
            rgba[at] = rgba[at + 1] = rgba[at + 2] = rgba[at + 3] = 255;
        }

        byte[] one = IconImages.Load(Png.Encode(2, 2, rgba)).Rgba(1);

        Assert.Equal([255, 255, 255], one[..3]);
        Assert.InRange(one[3], 127, 128);
    }

    [Fact]
    public void ASmallIconIsWarnedAboutAndScalingUpIsToo()
    {
        IconImages icon = IconImages.Load(Png.Encode(128, 128, Quadrants(128)));
        Assert.Contains(icon.Warnings, w => w.Contains("128x128", StringComparison.Ordinal) && w.Contains("256x256", StringComparison.Ordinal));

        byte[] big = icon.Rgba(256);

        Assert.Equal(256 * 256 * 4, big.Length);
        Assert.Contains(icon.Warnings, w => w.Contains("scaled up", StringComparison.Ordinal));
        icon.Rgba(256);
        Assert.Single(icon.Warnings, w => w.Contains("scaled up", StringComparison.Ordinal));
        Assert.Equal([255, 0, 0, 255], big[..4]);
    }

    [Fact]
    public void ThePngOfASizeDecodesToThatSize()
    {
        IconImages icon = IconImages.Load(Png.Encode(256, 256, Quadrants(256)));

        TextureData decoded = ImageDecoder.Decode("check", icon.Png(64), srgb: false, SamplerData.Default, generateMips: false);

        Assert.Equal((64, 64), (decoded.Width, decoded.Height));
        Assert.Equal(icon.Rgba(64), decoded.Mips[0]);
    }

    [Fact]
    public void ANonSquareImageOrNonImageIsRefused()
    {
        Assert.Contains("square", Assert.Throws<ExportException>(() => IconImages.Load(Png.Encode(4, 2, new byte[4 * 2 * 4]))).Message, StringComparison.Ordinal);
        Assert.Contains("not an image", Assert.Throws<ExportException>(() => IconImages.Load(Enumerable.Repeat((byte)7, 100).ToArray())).Message, StringComparison.Ordinal);
    }
}
