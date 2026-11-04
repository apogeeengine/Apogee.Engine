using System;
using Apogee.Native.Metal;
using MtlComputePipeline = Apogee.Native.Metal.ComputePipeline;
using MtlDepthStencil = Apogee.Native.Metal.DepthStencil;
using MtlRenderPipeline = Apogee.Native.Metal.RenderPipeline;

namespace Apogee.Graphics.Metal;

/// <summary>A Metal compute pipeline, or a render pipeline with the fixed-function state Metal sets on the encoder (depth test, culling, winding).</summary>
public sealed unsafe class MetalPipeline : Pipeline
{
    private MtlComputePipeline* _compute;
    private MtlRenderPipeline* _render;
    private MtlDepthStencil* _depthStencil;

    internal MetalPipeline(MtlComputePipeline* compute, ThreadGroupSize group)
    {
        _compute = compute;
        Group = group;
    }

    internal MetalPipeline(MtlRenderPipeline* render, MtlDepthStencil* depthStencil, int cull, bool frontCounterClockwise, int primitive)
    {
        _render = render;
        _depthStencil = depthStencil;
        Cull = cull;
        FrontCounterClockwise = frontCounterClockwise;
        Primitive = primitive;
    }

    public bool IsCompute => _compute is not null;

    internal MtlComputePipeline* Compute => _compute;

    internal MtlRenderPipeline* Render => _render;

    internal MtlDepthStencil* DepthStencil => _depthStencil;

    internal ThreadGroupSize Group { get; }

    /// <summary>MTLCullMode: 0 none, 1 front, 2 back (the engine's <see cref="CullMode"/>).</summary>
    internal int Cull { get; }

    internal bool FrontCounterClockwise { get; }

    /// <summary>MTLPrimitiveType (the engine's <see cref="PrimitiveTopology"/>).</summary>
    internal int Primitive { get; }

    public override void Dispose()
    {
        if (_compute is not null)
        {
            Mtl.Release(_compute);
            _compute = null;
        }

        if (_render is not null)
        {
            Mtl.Release(_render);
            _render = null;
        }

        if (_depthStencil is not null)
        {
            Mtl.Release(_depthStencil);
            _depthStencil = null;
        }
    }
}
