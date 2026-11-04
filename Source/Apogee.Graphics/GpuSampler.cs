using System;

namespace Apogee.Graphics;

public readonly record struct SamplerDescription(
    Filter Filter = Filter.Linear,
    MipmapMode MipmapMode = MipmapMode.Linear,
    AddressMode AddressMode = AddressMode.Repeat,
    float MaxAnisotropy = 1f,
    bool Compare = false)
{
    public static SamplerDescription LinearRepeat { get; } = new(MaxAnisotropy: 8f);

    public static SamplerDescription LinearClamp { get; } = new(AddressMode: AddressMode.ClampToEdge);

    public static SamplerDescription NearestClamp { get; } = new(Filter.Nearest, MipmapMode.Nearest, AddressMode.ClampToEdge);

    /// <summary>Depth comparison (shadow maps): returns how much of a texel passes <c>reference &gt;= depth</c>.</summary>
    public static SamplerDescription Shadow { get; } = new(AddressMode: AddressMode.ClampToEdge, Compare: true);
}

public abstract class GpuSampler : IDisposable
{
    public abstract void Dispose();
}
