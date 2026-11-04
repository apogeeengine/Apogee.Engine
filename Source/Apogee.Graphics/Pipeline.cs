using System;
using Apogee.Core.Graphics;
using Apogee.Graphics.Shaders;

namespace Apogee.Graphics;

/// <summary>The size of a compute shader's thread group, <c>[numthreads(x, y, z)]</c>. Vulkan pipelines have it inside; a Metal dispatch has to be told.</summary>
public readonly record struct ThreadGroupSize(uint X, uint Y, uint Z);

/// <summary>
/// One shader stage: the compiled code for the device's <see cref="Backend"/> (SPIR-V for Vulkan, Metal shading language source for Metal) and the entry point in it to run. A compute shader
/// also says its <see cref="Group"/> size.
/// </summary>
public readonly record struct ShaderCode(ShaderBackend Backend, byte[] Code, string EntryPoint, ThreadGroupSize? Group = null);

public enum BlendMode
{
    Opaque,
    /// <summary>Straight alpha: source * alpha + destination * (1 - alpha).</summary>
    Alpha,
    /// <summary>Color already multiplied by alpha: source + destination * (1 - alpha).</summary>
    Premultiplied,
    Additive,
}

/// <summary>
/// Everything that makes a graphics pipeline (front faces are counter-clockwise by default, as in glTF). Geometry is pulled from buffers by the vertex shader (no vertex input state), depth is reverse-Z
/// (the near plane is 1, so <see cref="DepthCompare"/> is greater-or-equal), and the pipelines render with dynamic rendering into
/// attachments of these formats.
/// </summary>
public sealed record GraphicsPipelineDescription(
    ShaderCode Vertex,
    ShaderCode? Fragment,
    PixelFormat[] ColorFormats,
    PixelFormat DepthFormat = PixelFormat.Undefined,
    PrimitiveTopology Topology = PrimitiveTopology.TriangleList,
    CullMode CullMode = CullMode.Back,
    FrontFace FrontFace = FrontFace.CounterClockwise,
    bool DepthTest = false,
    bool DepthWrite = false,
    CompareOp DepthCompare = CompareOp.GreaterOrEqual,
    BlendMode Blend = BlendMode.Opaque);

/// <summary>A compiled pipeline; bind it with <see cref="CommandRecorder.BindPipeline"/>.</summary>
public abstract class Pipeline : IDisposable
{
    public abstract void Dispose();
}
