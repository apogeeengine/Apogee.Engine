using System.Collections.Generic;
using System.Numerics;
using Apogee.Core.Numerics;

namespace Apogee.Rendering;

/// <summary>One mesh instance, ready to draw: which instance record to read, which indices of its model to draw, and where it is (an offset from the camera, to sort blended draws).</summary>
internal readonly record struct Draw(uint Instance, uint FirstIndex, uint IndexCount, DrawClass Class, bool DoubleSided, Vector3 Center, GpuModel Model);

/// <summary>One placement of a model in the world: where it is, in double precision, and how it is turned and scaled.</summary>
internal readonly record struct Placement(GpuModel Model, Double3 Position, Quaternion Rotation, Vector3 Scale)
{
    public bool IsUpright => Rotation == Quaternion.Identity && Scale == Vector3.One;
}

/// <summary>What a source of renderable things hands the renderer for one frame. Lights are already offsets from the camera. The lists are reused between frames.</summary>
internal sealed class SceneGather
{
    public List<Placement> Placements { get; } = [];

    public List<GpuLight> Lights { get; } = [];

    /// <summary>The direction toward the sun (unit length), and its color times illuminance.</summary>
    public Vector3 SunDirection { get; set; }

    public Vector3 SunColor { get; set; }

    public Vector3 AmbientSky { get; set; }

    public Vector3 AmbientGround { get; set; }

    public void Clear()
    {
        Placements.Clear();
        Lights.Clear();
    }
}

/// <summary>Something the renderer can draw: a <see cref="RenderScene"/> (one model) or a <see cref="RenderWorld"/> (the entities of an ECS world).</summary>
internal interface IRenderSource
{
    /// <summary>Fills <paramref name="into"/> with the frame's placements and lights, seen from <paramref name="camera"/>.</summary>
    void Gather(SceneGather into, Double3 camera);
}
