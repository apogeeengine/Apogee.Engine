using System;
using Apogee.Native.Metal;
using MtlSampler = Apogee.Native.Metal.Sampler;

namespace Apogee.Graphics.Metal;

public sealed unsafe class MetalSampler : GpuSampler
{
    private MtlSampler* _handle;

    internal MetalSampler(MetalDevice device, in SamplerDescription description)
    {
        var desc = new SamplerDesc
        {
            Filter = (int)description.Filter,
            MipFilter = (int)description.MipmapMode,
            AddressMode = (int)description.AddressMode,
            MaxAnisotropy = description.MaxAnisotropy > 1f ? (int)Math.Min(description.MaxAnisotropy, device.MaxSamplerAnisotropy) : 1,
            Compare = description.Compare ? 1 : 0,
        };
        _handle = Mtl.SamplerCreate(device.Handle, &desc);
        if (_handle is null)
        {
            throw new MetalException("The Metal device could not make a sampler.");
        }
    }

    internal ulong ResourceId => Mtl.SamplerResourceId(_handle);

    public override void Dispose()
    {
        if (_handle is not null)
        {
            Mtl.Release(_handle);
            _handle = null;
        }
    }
}
