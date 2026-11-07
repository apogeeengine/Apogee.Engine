using System;
using System.IO;
using System.Runtime.InteropServices;
using Apogee.Interop;
using Apogee.Native.FreeType;
using Xunit;

namespace Apogee.Native.Tests;

public unsafe class FreeTypeSmokeTests
{
    private static void Require() => NativeRequirements.Require(typeof(FT).Assembly, "freetype");

    private static byte[] Font() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "ProggyClean.ttf"));

    /// <summary>A library and a face of the test font, made from memory and freed at the end of the scope.</summary>
    private sealed class Face : IDisposable
    {
        private readonly byte[] _data = Font(); // FreeType reads the font from this memory for as long as the face lives
        private readonly System.Runtime.InteropServices.GCHandle _pin;

        public Face()
        {
            _pin = System.Runtime.InteropServices.GCHandle.Alloc(_data, System.Runtime.InteropServices.GCHandleType.Pinned);
            LibraryRec* library;
            Assert.Equal(0, FT.InitFreeType(&library));
            Library = library;
            FtFaceRec* face;
            Assert.Equal(0, FT.NewMemoryFace(Library, (byte*)_pin.AddrOfPinnedObject(), new CLong(_data.Length), new CLong(0), &face));
            Handle = face;
            Assert.Equal(0, FT.SetPixelSizes(Handle, 0, 16));
        }

        public LibraryRec* Library { get; }

        public FtFaceRec* Handle { get; }

        public void Dispose()
        {
            Assert.Equal(0, FT.DoneFace(Handle));
            Assert.Equal(0, FT.DoneFreeType(Library));
            _pin.Free();
        }
    }

    private static (uint Width, uint Rows, int Lit) Draw(Face face, char c, int flags)
    {
        Assert.Equal(0, FT.LoadChar(face.Handle, new CULong(c), flags | FT.LoadRender));
        Bitmap bitmap = face.Handle->Glyph->Bitmap;
        int lit = 0;
        for (uint y = 0; y < bitmap.Rows; y++)
        {
            for (uint x = 0; x < bitmap.Width; x++)
            {
                lit += bitmap.Buffer[(y * bitmap.Pitch) + x] != 0 ? 1 : 0;
            }
        }

        return (bitmap.Width, bitmap.Rows, lit);
    }

    [Fact]
    public void TheLibraryIsTheVersionWeVendored()
    {
        Require();
        using var face = new Face();
        int major, minor, patch;

        FT.LibraryVersion(face.Library, &major, &minor, &patch);

        Assert.Equal((2, 15, 0), (major, minor, patch));
    }

    [Fact]
    public void AFontFromMemoryRendersAGlyphWithPixelsInIt()
    {
        Require();
        using var face = new Face();

        Assert.Equal("ProggyCleanTT", Utf8.FromPointer(face.Handle->FamilyName));
        (uint width, uint rows, int lit) = Draw(face, 'A', 0);

        Assert.InRange(width, 4u, 16u);
        Assert.InRange(rows, 6u, 20u);
        Assert.InRange(lit, 8, (int)(width * rows)); // some of the box is ink, not all of it
        Assert.True(face.Handle->Glyph->Advance.X.Value > 0);
    }

    [Fact]
    public void TheAutoHinterAndTheMonochromeAndSignedDistanceRenderersAreThere()
    {
        Require();
        using var face = new Face();

        Assert.True(Draw(face, 'g', FT.LoadForceAutohint).Lit > 0);                        // the auto-fitter module

        Assert.Equal(0, FT.LoadChar(face.Handle, new CULong('B'), 0));
        Assert.Equal(0, FT.RenderGlyph(face.Handle->Glyph, RenderMode.Mono));               // the raster module
        Assert.Equal(1, face.Handle->Glyph->Bitmap.PixelMode);                              // FT_PIXEL_MODE_MONO

        Assert.Equal(0, FT.LoadChar(face.Handle, new CULong('B'), 0));
        Assert.Equal(0, FT.RenderGlyph(face.Handle->Glyph, RenderMode.Sdf));                // the sdf module: a field around the shape, so more pixels than the shape has
        Bitmap field = face.Handle->Glyph->Bitmap;
        Assert.True(field.Width > 0 && field.Rows > 0);
        Assert.Equal(2, field.PixelMode);                                                   // FT_PIXEL_MODE_GRAY
    }

    [Fact]
    public void ABrokenFontIsAnErrorNotACrash()
    {
        Require();
        LibraryRec* library;
        Assert.Equal(0, FT.InitFreeType(&library));
        byte[] junk = new byte[256];
        FtFaceRec* face;

        fixed (byte* bytes = junk)
        {
            Assert.NotEqual(0, FT.NewMemoryFace(library, bytes, new CLong(junk.Length), new CLong(0), &face));
        }

        Assert.Equal(0, FT.DoneFreeType(library));
    }
}
