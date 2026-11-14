using System;

namespace Apogee.Graphics;

/// <summary>What a sampler does. <see cref="Reduction"/> other than the average needs <see cref="GraphicsDevice.SupportsSamplerFilterMinMax"/> and is not combined with <see cref="Compare"/>.</summary>
public readonly record struct SamplerDescription(
    Filter Filter = Filter.Linear,
    MipmapMode MipmapMode = MipmapMode.Linear,
    AddressMode AddressMode = AddressMode.Repeat,
    float MaxAnisotropy = 1f,
    bool Compare = false,
    SamplerReduction Reduction = SamplerReduction.WeightedAverage)
{
    public static SamplerDescription LinearRepeat { get; } = new(MaxAnisotropy: 8f);

    public static SamplerDescription LinearClamp { get; } = new(AddressMode: AddressMode.ClampToEdge);

    public static SamplerDescription NearestClamp { get; } = new(Filter.Nearest, MipmapMode.Nearest, AddressMode.ClampToEdge);

    /// <summary>Depth comparison (shadow maps): returns how much of a texel passes <c>reference &gt;= depth</c>.</summary>
    /// <summary>Clamped, nearest between mips, with the minimum or maximum of the filter footprint instead of its average (needs <see cref="GraphicsDevice.SupportsSamplerFilterMinMax"/>).</summary>
    public static SamplerDescription MinMax(SamplerReduction reduction) => new(MipmapMode: MipmapMode.Nearest, AddressMode: AddressMode.ClampToEdge, Reduction: reduction);

    public static SamplerDescription Shadow { get; } = new(AddressMode: AddressMode.ClampToEdge, Compare: true);
}

public abstract class GpuSampler : IDisposable
{
    public abstract void Dispose();
}
