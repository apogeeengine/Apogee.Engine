using System;
using System.Numerics;
using MeshoptApi = Apogee.Native.Meshopt.Meshopt;

namespace Apogee.Content;

/// <summary>Mesh clean-up and the vertex attributes glTF lets a file leave out.</summary>
public static unsafe class MeshProcessing
{
    /// <summary>Merges identical vertices and reorders both buffers for the GPU's vertex cache and memory fetch. Keeps exactly the same triangles.</summary>
    public static (Vertex[] Vertices, uint[] Indices) Optimize(Vertex[] vertices, uint[] indices)
    {
        if (indices.Length == 0)
        {
            return (vertices, indices);
        }

        nuint vertexSize = (nuint)sizeof(Vertex);
        uint[] remap = new uint[vertices.Length];
        uint[] remapped = new uint[indices.Length];
        uint[] optimized = new uint[indices.Length];
        nuint unique;
        Vertex[] merged;
        fixed (Vertex* source = vertices)
        fixed (uint* indexData = indices)
        fixed (uint* remapData = remap)
        fixed (uint* remappedData = remapped)
        fixed (uint* optimizedData = optimized)
        {
            unique = MeshoptApi.GenerateVertexRemap(remapData, indexData, (nuint)indices.Length, source, (nuint)vertices.Length, vertexSize);
            merged = new Vertex[unique];
            fixed (Vertex* mergedData = merged)
            {
                MeshoptApi.RemapVertexBuffer(mergedData, source, (nuint)vertices.Length, vertexSize, remapData);
            }

            MeshoptApi.RemapIndexBuffer(remappedData, indexData, (nuint)indices.Length, remapData);
            MeshoptApi.OptimizeVertexCache(optimizedData, remappedData, (nuint)indices.Length, unique);
        }

        var fetched = new Vertex[unique];
        fixed (Vertex* mergedData = merged)
        fixed (Vertex* fetchedData = fetched)
        fixed (uint* optimizedData = optimized)
        {
            MeshoptApi.OptimizeVertexFetch(fetchedData, optimizedData, (nuint)optimized.Length, mergedData, unique, vertexSize);
        }

        return (fetched, optimized);
    }

    /// <summary>
    /// Gives each triangle its own three vertices with the face normal: what glTF says a mesh without normals looks like. Positions, UVs and
    /// tangents are copied along.
    /// </summary>
    public static (Vertex[] Vertices, uint[] Indices) WithFlatNormals(Vertex[] vertices, uint[] indices)
    {
        var flat = new Vertex[indices.Length];
        var sequence = new uint[indices.Length];
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            Vertex a = vertices[indices[i]];
            Vertex b = vertices[indices[i + 1]];
            Vertex c = vertices[indices[i + 2]];
            Vector3 normal = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            normal = normal.LengthSquared() > 0f ? Vector3.Normalize(normal) : Vector3.UnitZ;
            a.Normal = b.Normal = c.Normal = normal;
            flat[i] = a;
            flat[i + 1] = b;
            flat[i + 2] = c;
            sequence[i] = (uint)i;
            sequence[i + 1] = (uint)(i + 1);
            sequence[i + 2] = (uint)(i + 2);
        }

        return (flat, sequence);
    }

    /// <summary>
    /// Computes tangents from the UVs, as a normal map needs when the file has none: per-triangle tangents are summed per vertex, made
    /// perpendicular to the normal, and given the handedness of the UV mapping.
    /// </summary>
    public static void GenerateTangents(Vertex[] vertices, uint[] indices)
    {
        var tangents = new Vector3[vertices.Length];
        var bitangents = new Vector3[vertices.Length];
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            uint i0 = indices[i];
            uint i1 = indices[i + 1];
            uint i2 = indices[i + 2];
            Vector3 edge1 = vertices[i1].Position - vertices[i0].Position;
            Vector3 edge2 = vertices[i2].Position - vertices[i0].Position;
            Vector2 uv1 = vertices[i1].Uv0 - vertices[i0].Uv0;
            Vector2 uv2 = vertices[i2].Uv0 - vertices[i0].Uv0;

            float determinant = uv1.X * uv2.Y - uv2.X * uv1.Y;
            if (MathF.Abs(determinant) < 1e-12f)
            {
                continue;
            }

            float scale = 1f / determinant;
            Vector3 tangent = (edge1 * uv2.Y - edge2 * uv1.Y) * scale;
            Vector3 bitangent = (edge2 * uv1.X - edge1 * uv2.X) * scale;
            foreach (uint index in new[] { i0, i1, i2 })
            {
                tangents[index] += tangent;
                bitangents[index] += bitangent;
            }
        }

        for (int v = 0; v < vertices.Length; v++)
        {
            Vector3 normal = vertices[v].Normal;
            Vector3 tangent = tangents[v] - normal * Vector3.Dot(normal, tangents[v]);
            if (tangent.LengthSquared() < 1e-20f)
            {
                // No usable UVs: any vector perpendicular to the normal will do.
                tangent = MathF.Abs(normal.X) < 0.9f ? Vector3.Cross(normal, Vector3.UnitX) : Vector3.Cross(normal, Vector3.UnitY);
            }

            tangent = Vector3.Normalize(tangent);
            float handedness = Vector3.Dot(Vector3.Cross(normal, tangent), bitangents[v]) < 0f ? -1f : 1f;
            vertices[v].Tangent = new Vector4(tangent, handedness);
        }
    }
}
