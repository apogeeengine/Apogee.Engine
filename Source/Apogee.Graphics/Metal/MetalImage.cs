using System;
using Apogee.Core.Graphics;
using Apogee.Native.Metal;
using MtlTexture = Apogee.Native.Metal.Texture;

namespace Apogee.Graphics.Metal;

/// <summary>A view of a Metal texture. The image's own view borrows the texture; views made with <see cref="MetalImage.CreateView"/> own theirs.</summary>
public sealed unsafe class MetalImageView(MtlTexture* handle, bool owned) : ImageView
{
    public MtlTexture* Handle { get; } = handle;

    internal bool Owned { get; } = owned;
}

/// <summary>A Metal texture, or a window's drawable texture that is borrowed for a frame (<see cref="MetalImage(MetalDevice, MtlTexture*, in ImageDescription)"/>).</summary>
public sealed unsafe class MetalImage : GpuImage
{
    private readonly MetalDevice _device;
    private MtlTexture* _texture;
    private readonly MetalImageView _view;

    internal MetalImage(MetalDevice device, in ImageDescription description, string? name)
        : base(description)
    {
        _device = device;
        uint format = Mtl.PixelFormat((int)description.Format);
        if (format == 0)
        {
            throw new MetalException($"Metal has no pixel format for {description.Format}.");
        }

        uint usage = 0;
        usage |= (description.Usage & (ImageUsage.Sampled | ImageUsage.Storage)) != 0 ? (uint)Mtl.UsageShaderRead : 0;
        usage |= (description.Usage & ImageUsage.Storage) != 0 ? (uint)Mtl.UsageShaderWrite : 0;
        usage |= (description.Usage & (ImageUsage.ColorAttachment | ImageUsage.DepthStencilAttachment)) != 0 ? (uint)Mtl.UsageRenderTarget : 0;
        var desc = new TextureDesc
        {
            Type = TypeOf(description),
            PixelFormat = format,
            Width = description.Width,
            Height = description.Height,
            Depth = description.Depth,
            MipLevels = description.MipLevels,
            ArrayLength = description.ArrayLayers,
            SampleCount = (uint)description.Samples,
            Usage = usage,
        };
        _texture = Mtl.TextureCreate(device.Handle, &desc);
        if (_texture is null)
        {
            throw new MetalException($"The Metal device could not make a {description.Width}x{description.Height} {description.Format} texture.");
        }

        Layers = description.Cube ? 6 * description.ArrayLayers : description.ArrayLayers;
        _view = new MetalImageView(_texture, owned: false);
        device.Track(_texture);
        if (name is not null)
        {
            fixed (byte* label = Apogee.Interop.Utf8.ToZeroTerminated(name))
            {
                Mtl.TextureSetLabel(_texture, label);
            }
        }
    }

    /// <summary>A texture someone else owns (a drawable's, whose reference this takes over): disposing the image gives the reference back.</summary>
    internal MetalImage(MetalDevice device, MtlTexture* texture, in ImageDescription description)
        : base(description)
    {
        _device = device;
        _texture = texture;
        Layers = 1;
        _view = new MetalImageView(texture, owned: false);
    }

    public MtlTexture* Handle => _texture;

    public override uint Layers { get; }

    public override ImageView View => _view;

    /// <summary>The index a bindless heap's argument buffer holds for this texture.</summary>
    internal ulong ResourceId => Mtl.TextureResourceId(_texture);

    private static int TypeOf(in ImageDescription d) =>
        d.Cube ? (d.ArrayLayers > 1 ? Mtl.TextureCubeArray : Mtl.TextureCube)
        : d.Depth > 1 ? Mtl.Texture3d
        : d.ArrayLayers > 1 ? Mtl.Texture2dArray
        : Mtl.Texture2d;

    public override ImageView CreateView(uint baseMip, uint mipCount, uint baseLayer, uint layerCount)
    {
        int type = Description.Depth > 1 ? Mtl.Texture3d
            : Description.Cube ? (layerCount == 6 ? Mtl.TextureCube : layerCount > 6 ? Mtl.TextureCubeArray : Mtl.Texture2dArray)
            : layerCount > 1 ? Mtl.Texture2dArray
            : Mtl.Texture2d;
        MtlTexture* view = Mtl.TextureView(_texture, 0, type, baseMip, mipCount, baseLayer, layerCount);
        return view is null ? throw new MetalException("The Metal texture could not make that view.") : new MetalImageView(view, owned: true);
    }

    public override void DestroyView(ImageView view)
    {
        var metal = (MetalImageView)view;
        if (metal.Owned)
        {
            Mtl.Release(metal.Handle);
        }
    }

    public override void Dispose()
    {
        if (_texture is null)
        {
            return;
        }

        _device.Untrack(_texture);
        Mtl.Release(_texture);
        _texture = null;
    }
}
