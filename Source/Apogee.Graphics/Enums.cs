using System;

namespace Apogee.Graphics;

// The engine's own names for the few GPU enumerations the renderer chooses between. The numbers are Vulkan's (so the Vulkan backend casts); another backend maps them.

/// <summary>What a buffer will be used for. Every buffer can also be copied from and to, and is reachable by address.</summary>
[Flags]
public enum BufferUsage : uint
{
    None = 0,
    TransferSource = 0x1,
    TransferDestination = 0x2,
    Uniform = 0x10,
    Storage = 0x20,
    Index = 0x40,
    Vertex = 0x80,
    Indirect = 0x100,
}

/// <summary>What an image will be used for. Every image can also be copied from and to.</summary>
[Flags]
public enum ImageUsage : uint
{
    None = 0,
    TransferSource = 0x1,
    TransferDestination = 0x2,
    Sampled = 0x4,
    Storage = 0x8,
    ColorAttachment = 0x10,
    DepthStencilAttachment = 0x20,
}

public enum SampleCount : uint
{
    Count1 = 1,
    Count2 = 2,
    Count4 = 4,
    Count8 = 8,
}

public enum CullMode
{
    None = 0,
    Front = 1,
    Back = 2,
    FrontAndBack = 3,
}

public enum FrontFace
{
    CounterClockwise = 0,
    Clockwise = 1,
}

public enum CompareOp
{
    Never = 0,
    Less = 1,
    Equal = 2,
    LessOrEqual = 3,
    Greater = 4,
    NotEqual = 5,
    GreaterOrEqual = 6,
    Always = 7,
}

public enum PrimitiveTopology
{
    PointList = 0,
    LineList = 1,
    LineStrip = 2,
    TriangleList = 3,
    TriangleStrip = 4,
}

public enum Filter
{
    Nearest = 0,
    Linear = 1,
}

public enum MipmapMode
{
    Nearest = 0,
    Linear = 1,
}

public enum AddressMode
{
    Repeat = 0,
    MirroredRepeat = 1,
    ClampToEdge = 2,
}

public enum IndexFormat
{
    Index16 = 0,
    Index32 = 1,
}
