using System;
using Apogee.Core.Graphics;
using Apogee.Native.Metal;
using Apogee.Platform;
using SDL = Apogee.Native.Sdl.SDL;

namespace Apogee.Graphics.Metal;

/// <summary>
/// The images of a window: a <c>CAMetalLayer</c> that SDL made for it (the window needs <see cref="WindowOptions.Metal"/>). The format is BGRA8 UNORM, like the Vulkan swapchain's: shaders write sRGB-encoded values themselves.
/// A frame takes the layer's next drawable (<see cref="Acquire"/>) and presents it with its command buffer.
/// </summary>
public sealed unsafe class MetalSwapchain : Swapchain
{
    private readonly MetalDevice _device;
    private readonly Window _window;
    private void* _view;
    private Layer* _layer;
    private uint _width;
    private uint _height;

    internal MetalSwapchain(MetalDevice device, Window window, bool vsync)
    {
        _device = device;
        _window = window;
        VSync = vsync;
        _view = SDL.MetalCreateView(window.NativeHandle);
        if (_view is null)
        {
            throw new MetalException("SDL could not make a Metal view for the window (was it created with WindowOptions.Metal?).");
        }

        PixelSize size = window.PixelSize;
        _width = (uint)Math.Max(size.Width, 1);
        _height = (uint)Math.Max(size.Height, 1);
        _layer = Mtl.LayerAttach(SDL.MetalGetLayer(_view), device.Handle, _width, _height, vsync ? 1 : 0);
        if (_layer is null)
        {
            SDL.MetalDestroyView(_view);
            _view = null;
            throw new MetalException("The window's Metal layer could not be set up.");
        }
    }

    public override bool VSync { get; }

    public override PixelFormat Format => PixelFormat.B8G8R8A8Unorm;

    public override uint Width => _width;

    public override uint Height => _height;

    public override int ImageCount => 3;

    public override bool IsStale
    {
        get
        {
            PixelSize size = _window.PixelSize;
            return !size.IsEmpty && (size.Width != _width || size.Height != _height);
        }
    }

    public override bool IsZeroSized => _window.PixelSize.IsEmpty;

    public override void Recreate()
    {
        PixelSize size = _window.PixelSize;
        _width = (uint)Math.Max(size.Width, 1);
        _height = (uint)Math.Max(size.Height, 1);
        Mtl.LayerResize(_layer, _width, _height);
    }

    /// <summary>
    /// Takes the next drawable and its texture as an image (give both back when the frame is committed: release the drawable, dispose the image); false when none is available.
    /// </summary>
    internal bool TryAcquire(out Drawable* drawable, out MetalImage? image)
    {
        drawable = Mtl.LayerNextDrawable(_layer);
        if (drawable is null)
        {
            image = null;
            return false;
        }

        Texture* texture = Mtl.DrawableTexture(drawable);
        var description = new ImageDescription(Format, _width, _height, ImageUsage.ColorAttachment | ImageUsage.TransferSource | ImageUsage.TransferDestination);
        image = new MetalImage(_device, texture, description);
        return true;
    }

    public override void Dispose()
    {
        if (_layer is not null)
        {
            Mtl.Release(_layer);
            _layer = null;
        }

        if (_view is not null)
        {
            SDL.MetalDestroyView(_view);
            _view = null;
        }
    }
}
