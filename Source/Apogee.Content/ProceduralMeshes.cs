using System;
using System.Collections.Generic;
using System.Numerics;

namespace Apogee.Content;

/// <summary>Simple shapes built in code: for sample scenes, tools and tests. Every mesh has normals, UVs and tangents, and counter-clockwise front faces.</summary>
public static class ProceduralMeshes
{
    private static Vertex V(Vector3 position, Vector3 normal, Vector3 tangent, Vector2 uv) =>
        new() { Position = position, Normal = normal, Tangent = new Vector4(tangent, 1f), Uv0 = uv };

    /// <summary>A box centered on the origin. <paramref name="uvScale"/> repeats the texture along each face.</summary>
    public static MeshData Box(string name, Vector3 size, int material, float uvScale = 1f)
    {
        Vector3 h = size * 0.5f;
        var vertices = new List<Vertex>();
        var indices = new List<uint>();

        void Face(Vector3 normal, Vector3 right, Vector3 up)
        {
            uint first = (uint)vertices.Count;
            Vector3 center = normal * Vector3.Dot(Vector3.Abs(normal), h);
            Vector3 r = right * Vector3.Dot(Vector3.Abs(right), h);
            Vector3 u = up * Vector3.Dot(Vector3.Abs(up), h);
            float uRepeat = 2f * r.Length() * uvScale;
            float vRepeat = 2f * u.Length() * uvScale;
            vertices.Add(V(center - r - u, normal, right, new Vector2(0, vRepeat)));
            vertices.Add(V(center + r - u, normal, right, new Vector2(uRepeat, vRepeat)));
            vertices.Add(V(center + r + u, normal, right, new Vector2(uRepeat, 0)));
            vertices.Add(V(center - r + u, normal, right, new Vector2(0, 0)));
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        Face(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY);
        Face(-Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY);
        Face(Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY);
        Face(-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY);
        Face(Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ);
        Face(-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ);
        return new MeshData(name, [.. vertices], [.. indices], material, -h, h);
    }

    /// <summary>A horizontal rectangle on y = 0 facing up, centered on the origin, with the texture repeating <paramref name="uvScale"/> times per meter.</summary>
    public static MeshData Plane(string name, float width, float depth, int material, float uvScale = 1f)
    {
        float hx = width * 0.5f;
        float hz = depth * 0.5f;
        Vertex[] vertices =
        [
            V(new Vector3(-hx, 0, hz), Vector3.UnitY, Vector3.UnitX, new Vector2(0, depth * uvScale)),
            V(new Vector3(hx, 0, hz), Vector3.UnitY, Vector3.UnitX, new Vector2(width * uvScale, depth * uvScale)),
            V(new Vector3(hx, 0, -hz), Vector3.UnitY, Vector3.UnitX, new Vector2(width * uvScale, 0)),
            V(new Vector3(-hx, 0, -hz), Vector3.UnitY, Vector3.UnitX, new Vector2(0, 0)),
        ];
        return new MeshData(name, vertices, [0, 1, 2, 0, 2, 3], material, new Vector3(-hx, 0, -hz), new Vector3(hx, 0, hz));
    }

    /// <summary>An upright cylinder from y = 0 to <paramref name="height"/>, smooth shaded around its side, with a cap on top and bottom.</summary>
    public static MeshData Cylinder(string name, float radius, float height, int segments, int material)
    {
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        for (int i = 0; i <= segments; i++)
        {
            float angle = i / (float)segments * MathF.Tau;
            Vector3 normal = new(MathF.Cos(angle), 0, MathF.Sin(angle));
            Vector3 tangent = new(-MathF.Sin(angle), 0, MathF.Cos(angle));
            float u = i / (float)segments * MathF.Tau * radius;
            vertices.Add(V(normal * radius, normal, tangent, new Vector2(u, height)));
            vertices.Add(V(normal * radius + new Vector3(0, height, 0), normal, tangent, new Vector2(u, 0)));
        }

        for (int i = 0; i < segments; i++)
        {
            uint a = (uint)(i * 2);
            // Counter-clockwise seen from outside: bottom, top of the next column, bottom of the next, then bottom, top, top of the next.
            indices.AddRange([a, a + 3, a + 2, a, a + 1, a + 3]);
        }

        foreach ((float y, Vector3 normal, bool up) in new[] { (height, Vector3.UnitY, true), (0f, -Vector3.UnitY, false) })
        {
            uint center = (uint)vertices.Count;
            vertices.Add(V(new Vector3(0, y, 0), normal, Vector3.UnitX, new Vector2(0.5f, 0.5f)));
            for (int i = 0; i <= segments; i++)
            {
                float angle = i / (float)segments * MathF.Tau;
                vertices.Add(V(new Vector3(MathF.Cos(angle) * radius, y, MathF.Sin(angle) * radius), normal, Vector3.UnitX, new Vector2(0.5f + MathF.Cos(angle) * 0.5f, 0.5f + MathF.Sin(angle) * 0.5f)));
            }

            for (int i = 0; i < segments; i++)
            {
                uint a = center + 1 + (uint)i;
                indices.AddRange(up ? [center, a + 1, a] : [center, a, a + 1]);
            }
        }

        return new MeshData(name, [.. vertices], [.. indices], material, new Vector3(-radius, 0, -radius), new Vector3(radius, height, radius));
    }

    /// <summary>A UV sphere centered on the origin.</summary>
    public static MeshData Sphere(string name, float radius, int segments, int rings, int material)
    {
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        for (int ring = 0; ring <= rings; ring++)
        {
            float polar = ring / (float)rings * MathF.PI;
            for (int segment = 0; segment <= segments; segment++)
            {
                float azimuth = segment / (float)segments * MathF.Tau;
                Vector3 normal = new(MathF.Sin(polar) * MathF.Cos(azimuth), MathF.Cos(polar), MathF.Sin(polar) * MathF.Sin(azimuth));
                Vector3 tangent = new(-MathF.Sin(azimuth), 0, MathF.Cos(azimuth));
                vertices.Add(V(normal * radius, normal, tangent, new Vector2(segment / (float)segments, ring / (float)rings)));
            }
        }

        for (int ring = 0; ring < rings; ring++)
        {
            for (int segment = 0; segment < segments; segment++)
            {
                uint a = (uint)(ring * (segments + 1) + segment);
                uint b = a + (uint)segments + 1;
                indices.AddRange([a, a + 1, b, a + 1, b + 1, b]);
            }
        }

        return new MeshData(name, [.. vertices], [.. indices], material, new Vector3(-radius), new Vector3(radius));
    }
}
