using System;
using System.Numerics;
using System.Runtime.Intrinsics;
using Apogee.Core.Numerics;
using BenchmarkDotNet.Attributes;

namespace Apogee.Benchmarks;

/// <summary>Boxes against a frustum: the scalar loop, 128-bit vectors (four boxes at a time) and 256-bit vectors (eight).</summary>
public class CullingBenchmarks
{
    private BoxSet _boxes = new(0);
    private byte[] _result = [];
    private Frustum _frustum;

    [Params(1_000, 10_000, 100_000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(1);
        _boxes = new BoxSet(Count);
        for (int i = 0; i < Count; i++)
        {
            var center = new Vector3((float)((random.NextDouble() - 0.5) * 400), (float)((random.NextDouble() - 0.5) * 400), (float)((random.NextDouble() - 0.5) * 400));
            _boxes.Set(i, center, new Vector3((float)(random.NextDouble() * 3), (float)(random.NextDouble() * 3), (float)(random.NextDouble() * 3)));
        }

        _result = new byte[_boxes.ResultLength];
        _frustum = Frustum.FromCamera(Vector3.UnitX, Vector3.UnitY, -Vector3.UnitZ, MathF.Tan(0.5236f), 16f / 9f, 0.1f);
    }

    [Benchmark(Baseline = true)]
    public byte[] Scalar()
    {
        FrustumCulling.CullScalar(_frustum, _boxes, _result);
        return _result;
    }

    [Benchmark]
    public byte[] Vector128Kernel()
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return _result;
        }

        FrustumCulling.CullVector128(_frustum, _boxes, _result);
        return _result;
    }

    [Benchmark]
    public byte[] Vector256Kernel()
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            return _result; // not accelerated here (NativeAOT for x86-64-v2): the row measures nothing
        }

        FrustumCulling.CullVector256(_frustum, _boxes, _result);
        return _result;
    }

    [Benchmark]
    public byte[] Chosen()
    {
        FrustumCulling.Cull(_frustum, _boxes, _result);
        return _result;
    }
}

/// <summary>Turning world positions into offsets from the camera (what every instance costs each frame).</summary>
public class RelativePositionBenchmarks
{
    private Double3[] _positions = [];
    private Vector3[] _offsets = [];
    private Double3 _camera;

    [Params(10_000, 100_000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(2);
        _positions = new Double3[Count];
        _offsets = new Vector3[Count];
        for (int i = 0; i < Count; i++)
        {
            _positions[i] = new Double3(5_000_000 + (random.NextDouble() * 4000), random.NextDouble() * 50, 2_000_000 + (random.NextDouble() * 4000));
        }

        _camera = new Double3(5_002_000, 3, 2_002_000);
    }

    [Benchmark]
    public Vector3[] RelativeTo()
    {
        for (int i = 0; i < _positions.Length; i++)
        {
            _offsets[i] = _positions[i].RelativeTo(_camera);
        }

        return _offsets;
    }
}
