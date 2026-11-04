using System;
using Apogee.Core.Graphics;

namespace Apogee.Graphics;

/// <summary>The images of a window, which the frame loop acquires, renders to and presents. Made by <see cref="GraphicsDevice.CreateSwapchain"/>.</summary>
public abstract class Swapchain : IDisposable
{
    public abstract bool VSync { get; }

    /// <summary>The format of the images: UNORM, because shaders write sRGB-encoded values themselves.</summary>
    public abstract PixelFormat Format { get; }

    public abstract uint Width { get; }

    public abstract uint Height { get; }

    public abstract int ImageCount { get; }

    /// <summary>The window's size is no longer the images' size.</summary>
    public abstract bool IsStale { get; }

    public abstract bool IsZeroSized { get; }

    /// <summary>Makes the images fit the window again.</summary>
    public abstract void Recreate();

    public abstract void Dispose();
}
