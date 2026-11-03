using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Apogee.Content.Tests;

/// <summary>Writes small glTF documents with an embedded buffer, so the loader is tested on files whose every value is known.</summary>
internal sealed class GltfBuilder
{
    private readonly List<byte> _buffer = [];
    private readonly List<string> _views = [];
    private readonly List<string> _accessors = [];
    private readonly List<string> _meshes = [];

    public string Materials { get; set; } = "[]";

    public string Textures { get; set; } = "[]";

    public string Images { get; set; } = "[]";

    public string Samplers { get; set; } = "[]";

    public string Nodes { get; set; } = "[{\"mesh\":0}]";

    public string Scenes { get; set; } = "[{\"nodes\":[0]}]";

    public string Extensions { get; set; } = "";

    public string ExtensionsUsed { get; set; } = "[]";

    private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private int AddAccessor<T>(ReadOnlySpan<T> data, int componentType, string type, bool bounds = false)
        where T : unmanaged
    {
        while (_buffer.Count % 4 != 0)
        {
            _buffer.Add(0);
        }

        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
        _views.Add($"{{\"buffer\":0,\"byteOffset\":{_buffer.Count},\"byteLength\":{bytes.Length}}}");
        _buffer.AddRange(bytes.ToArray());

        int count = data.Length * Marshal.SizeOf<T>() / ComponentCount(type) / (componentType == 5125 ? 4 : 4);
        string minMax = "";
        if (bounds && typeof(T) == typeof(Vector3))
        {
            Vector3[] points = MemoryMarshal.Cast<T, Vector3>(data).ToArray();
            Vector3 min = points.Aggregate(Vector3.Min);
            Vector3 max = points.Aggregate(Vector3.Max);
            minMax = $",\"min\":[{F(min.X)},{F(min.Y)},{F(min.Z)}],\"max\":[{F(max.X)},{F(max.Y)},{F(max.Z)}]";
        }

        _accessors.Add($"{{\"bufferView\":{_views.Count - 1},\"componentType\":{componentType},\"count\":{count},\"type\":\"{type}\"{minMax}}}");
        return _accessors.Count - 1;
    }

    private static int ComponentCount(string type) => type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => throw new ArgumentException(type) };

    /// <summary>Adds a mesh with one primitive. <paramref name="mode"/> is glTF's primitive mode (4 triangles, 5 strip, 6 fan).</summary>
    public GltfBuilder AddMesh(Vector3[] positions, uint[]? indices = null, Vector3[]? normals = null, Vector2[]? uvs = null, Vector4[]? tangents = null, int? material = null, int mode = 4, string name = "mesh")
    {
        var attributes = new List<string> { $"\"POSITION\":{AddAccessor<Vector3>(positions, 5126, "VEC3", bounds: true)}" };
        if (normals is not null)
        {
            attributes.Add($"\"NORMAL\":{AddAccessor<Vector3>(normals, 5126, "VEC3")}");
        }

        if (tangents is not null)
        {
            attributes.Add($"\"TANGENT\":{AddAccessor<Vector4>(tangents, 5126, "VEC4")}");
        }

        if (uvs is not null)
        {
            attributes.Add($"\"TEXCOORD_0\":{AddAccessor<Vector2>(uvs, 5126, "VEC2")}");
        }

        string primitive = $"\"attributes\":{{{string.Join(',', attributes)}}},\"mode\":{mode}";
        if (indices is not null)
        {
            primitive += $",\"indices\":{AddAccessor<uint>(indices, 5125, "SCALAR")}";
        }

        if (material is not null)
        {
            primitive += $",\"material\":{material}";
        }

        _meshes.Add($"{{\"name\":\"{name}\",\"primitives\":[{{{primitive}}}]}}");
        return this;
    }

    public byte[] ToBytes()
    {
        string uri = "data:application/octet-stream;base64," + Convert.ToBase64String(_buffer.ToArray());
        string json = $$"""
            {"asset":{"version":"2.0"},
             "extensionsUsed":{{ExtensionsUsed}},
             "scene":0,"scenes":{{Scenes}},"nodes":{{Nodes}},
             "meshes":[{{string.Join(',', _meshes)}}],
             "materials":{{Materials}},"textures":{{Textures}},"images":{{Images}},"samplers":{{Samplers}},
             "accessors":[{{string.Join(',', _accessors)}}],
             "bufferViews":[{{string.Join(',', _views)}}],
             "buffers":[{"byteLength":{{_buffer.Count}},"uri":"{{uri}}"}]
             {{Extensions}}}
            """;
        return Encoding.UTF8.GetBytes(json);
    }

    /// <summary>A data URI for an image.</summary>
    public static string PngUri(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte r, byte g, byte b, byte a) = pixel(x, y);
                int i = (y * width + x) * 4;
                rgba[i] = r;
                rgba[i + 1] = g;
                rgba[i + 2] = b;
                rgba[i + 3] = a;
            }
        }

        return "data:image/png;base64," + Convert.ToBase64String(Png.Encode(width, height, rgba));
    }

    public static readonly Vector3[] Triangle = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];

    public static readonly Vector3[] Quad = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)];
}
