using System.Collections.Generic;
using System.Numerics;
using Apogee.Content;

namespace Apogee.Rendering.Tests;

/// <summary>Models built in code, so rendering tests need no files.</summary>
internal sealed class TestScene
{
    private readonly List<MeshData> _meshes = [];
    private readonly List<MeshInstance> _instances = [];
    private readonly List<MaterialData> _materials = [];
    private readonly List<LightData> _lights = [];

    public int AddMaterial(MaterialData material)
    {
        _materials.Add(material);
        return _materials.Count - 1;
    }

    public TestScene AddBox(Vector3 center, Vector3 size, int material)
    {
        MeshData mesh = Box(size, material);
        _meshes.Add(mesh);
        _instances.Add(new MeshInstance(_meshes.Count - 1, Matrix4x4.CreateTranslation(center)));
        return this;
    }

    /// <summary>A horizontal square on y = 0, facing up.</summary>
    public TestScene AddGround(float half, int material)
    {
        Vertex[] vertices =
        [
            Vertex(new Vector3(-half, 0, half), Vector3.UnitY, new Vector2(0, 0)),
            Vertex(new Vector3(half, 0, half), Vector3.UnitY, new Vector2(1, 0)),
            Vertex(new Vector3(half, 0, -half), Vector3.UnitY, new Vector2(1, 1)),
            Vertex(new Vector3(-half, 0, -half), Vector3.UnitY, new Vector2(0, 1)),
        ];
        _meshes.Add(new MeshData("ground", vertices, [0, 1, 2, 0, 2, 3], material, new Vector3(-half, 0, -half), new Vector3(half, 0, half)));
        _instances.Add(new MeshInstance(_meshes.Count - 1, Matrix4x4.Identity));
        return this;
    }

    /// <summary>A vertical square facing +Z, centered at <paramref name="center"/>.</summary>
    public TestScene AddWall(Vector3 center, float halfWidth, float halfHeight, int material)
    {
        Vertex[] vertices =
        [
            Vertex(new Vector3(-halfWidth, -halfHeight, 0), Vector3.UnitZ, new Vector2(0, 1)),
            Vertex(new Vector3(halfWidth, -halfHeight, 0), Vector3.UnitZ, new Vector2(1, 1)),
            Vertex(new Vector3(halfWidth, halfHeight, 0), Vector3.UnitZ, new Vector2(1, 0)),
            Vertex(new Vector3(-halfWidth, halfHeight, 0), Vector3.UnitZ, new Vector2(0, 0)),
        ];
        _meshes.Add(new MeshData("wall", vertices, [0, 1, 2, 0, 2, 3], material, new Vector3(-halfWidth, -halfHeight, 0), new Vector3(halfWidth, halfHeight, 0)));
        _instances.Add(new MeshInstance(_meshes.Count - 1, Matrix4x4.CreateTranslation(center)));
        return this;
    }

    public TestScene AddLight(LightData light)
    {
        _lights.Add(light);
        return this;
    }

    public ModelData ToModel() => new()
    {
        Name = "test",
        Meshes = _meshes,
        Instances = _instances,
        Materials = _materials.Count > 0 ? _materials : [new MaterialData()],
        Textures = [],
        Lights = _lights,
        BoundsMin = new Vector3(-10, 0, -10),
        BoundsMax = new Vector3(10, 5, 10),
    };

    public static LightData Point(Vector3 position, Vector3 color, float intensity, float range = 0f) =>
        new("point", LightKind.Point, color, intensity, range, 0f, 0f, position, -Vector3.UnitY);

    public static LightData Spot(Vector3 position, Vector3 direction, Vector3 color, float intensity, float inner, float outer) =>
        new("spot", LightKind.Spot, color, intensity, 0f, inner, outer, position, Vector3.Normalize(direction));

    public static LightData Sun(Vector3 shinesAlong, Vector3 color, float illuminance) =>
        new("sun", LightKind.Directional, color, illuminance, 0f, 0f, 0f, Vector3.Zero, Vector3.Normalize(shinesAlong));

    private static Vertex Vertex(Vector3 position, Vector3 normal, Vector2 uv) => new() { Position = position, Normal = normal, Tangent = new Vector4(1, 0, 0, 1), Uv0 = uv };

    private static MeshData Box(Vector3 size, int material)
    {
        Vector3 h = size * 0.5f;
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        void Face(Vector3 normal, Vector3 right, Vector3 up)
        {
            uint first = (uint)vertices.Count;
            // The face is `half extent along the normal` out from the center, and spans the half extents along right and up.
            Vector3 center = normal * Vector3.Dot(Vector3.Abs(normal), h);
            Vector3 r = right * Vector3.Dot(Vector3.Abs(right), h);
            Vector3 u = up * Vector3.Dot(Vector3.Abs(up), h);
            vertices.Add(Vertex(center - r - u, normal, new Vector2(0, 1)));
            vertices.Add(Vertex(center + r - u, normal, new Vector2(1, 1)));
            vertices.Add(Vertex(center + r + u, normal, new Vector2(1, 0)));
            vertices.Add(Vertex(center - r + u, normal, new Vector2(0, 0)));
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        Face(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY);
        Face(-Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY);
        Face(Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY);
        Face(-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY);
        Face(Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ);
        Face(-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ);
        return new MeshData("box", [.. vertices], [.. indices], material, -h, h);
    }
}
