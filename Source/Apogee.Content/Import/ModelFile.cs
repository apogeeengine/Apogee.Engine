using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using MeshoptApi = Apogee.Native.Meshopt.Meshopt;

namespace Apogee.Content.Import;

/// <summary>
/// The engine's own model file (<c>.amodel</c>): a <see cref="ModelData"/> as it is after import, ready to load without a glTF parser, its files, or any decoding of images. Vertex and index
/// buffers are compressed with meshoptimizer's codecs (lossless: the model comes back bit for bit), textures are embedded KTX 2 files (<see cref="Ktx2Writer"/>), the rest is plain
/// little-endian fields. Loading is far faster than reading the glTF, and the file is smaller than the sources it replaces.
/// </summary>
public static unsafe class ModelFile
{
    public const uint Magic = 0x4C444D41; // "AMDL"
    public const uint Version = 1;

    public static bool IsModelFile(ReadOnlySpan<byte> file) => file.Length >= 8 && BitConverter.ToUInt32(file) == Magic;

    /// <param name="model">The model; its textures are RGBA8.</param>
    /// <param name="compression">How the embedded textures are stored. With <see cref="TextureCompression.Uastc"/> those that a material uses as a normal map are encoded for accuracy.</param>
    public static byte[] Write(ModelData model, TextureCompression compression = TextureCompression.None)
    {
        var normalMaps = new HashSet<int>();
        foreach (MaterialData material in model.Materials)
        {
            if (material.NormalTexture is { } normal)
            {
                normalMaps.Add(normal.Texture);
            }
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(model.Name);
        Write(writer, model.BoundsMin);
        Write(writer, model.BoundsMax);

        writer.Write(model.Textures.Count);
        for (int index = 0; index < model.Textures.Count; index++)
        {
            TextureData texture = model.Textures[index];
            writer.Write(texture.Name);
            writer.Write(texture.Srgb);
            writer.Write((byte)texture.Sampler.Filter);
            writer.Write(texture.Sampler.Mipmaps);
            writer.Write((byte)texture.Sampler.WrapU);
            writer.Write((byte)texture.Sampler.WrapV);
            bool compress = compression == TextureCompression.Uastc && Math.Min(texture.Width, texture.Height) >= BasisEncoder.MinimumSize;
            WriteBlob(writer, compress ? BasisEncoder.Encode(texture, normalMaps.Contains(index)) : Ktx2Writer.Write(texture, Ktx2Writer.ImportLevel));
        }

        writer.Write(model.Materials.Count);
        foreach (MaterialData material in model.Materials)
        {
            WriteMaterial(writer, material);
        }

        writer.Write(model.Meshes.Count);
        foreach (MeshData mesh in model.Meshes)
        {
            WriteMesh(writer, mesh);
        }

        writer.Write(model.Instances.Count);
        foreach (MeshInstance instance in model.Instances)
        {
            writer.Write(instance.Mesh);
            Write(writer, instance.World);
        }

        writer.Write(model.Lights.Count);
        foreach (LightData light in model.Lights)
        {
            writer.Write(light.Name);
            writer.Write((byte)light.Kind);
            Write(writer, light.Color);
            writer.Write(light.Intensity);
            writer.Write(light.Range);
            writer.Write(light.InnerConeAngle);
            writer.Write(light.OuterConeAngle);
            Write(writer, light.Position);
            Write(writer, light.Direction);
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <exception cref="ContentException">The file is not a model file of this version, or it is damaged.</exception>
    public static ModelData Read(ReadOnlySpan<byte> file, string name = "model", TextureCaps caps = default)
    {
        if (!IsModelFile(file))
        {
            throw new ContentException($"'{name}' is not a model file.");
        }

        try
        {
            using var reader = new BinaryReader(new MemoryStream(file.ToArray()), Encoding.UTF8);
            reader.ReadUInt32();
            uint version = reader.ReadUInt32();
            if (version != Version)
            {
                throw new ContentException($"'{name}' is version {version} of the model format; this engine reads version {Version}. Import it again.");
            }

            string modelName = reader.ReadString();
            Vector3 boundsMin = ReadVector3(reader);
            Vector3 boundsMax = ReadVector3(reader);

            var textures = new List<TextureData>();
            int textureCount = Count(reader, file.Length);
            for (int i = 0; i < textureCount; i++)
            {
                string textureName = reader.ReadString();
                bool srgb = reader.ReadBoolean();
                var sampler = new SamplerData((TextureFilter)reader.ReadByte(), reader.ReadBoolean(), (TextureWrap)reader.ReadByte(), (TextureWrap)reader.ReadByte());
                textures.Add(Ktx2Loader.Load(textureName, ReadBlob(reader), srgb, caps, sampler));
            }

            var materials = new List<MaterialData>();
            int materialCount = Count(reader, file.Length);
            for (int i = 0; i < materialCount; i++)
            {
                materials.Add(ReadMaterial(reader));
            }

            var meshes = new List<MeshData>();
            int meshCount = Count(reader, file.Length);
            for (int i = 0; i < meshCount; i++)
            {
                meshes.Add(ReadMesh(reader, file.Length));
            }

            var instances = new List<MeshInstance>();
            int instanceCount = Count(reader, file.Length);
            for (int i = 0; i < instanceCount; i++)
            {
                int mesh = reader.ReadInt32();
                instances.Add(new MeshInstance(mesh, ReadMatrix(reader)));
            }

            var lights = new List<LightData>();
            int lightCount = Count(reader, file.Length);
            for (int i = 0; i < lightCount; i++)
            {
                lights.Add(new LightData(reader.ReadString(), (LightKind)reader.ReadByte(), ReadVector3(reader), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), ReadVector3(reader), ReadVector3(reader)));
            }

            return new ModelData
            {
                Name = modelName,
                Meshes = meshes,
                Instances = instances,
                Materials = materials,
                Textures = textures,
                Lights = lights,
                BoundsMin = boundsMin,
                BoundsMax = boundsMax,
            };
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or ArgumentException or IOException)
        {
            throw new ContentException($"'{name}' is damaged: {e.Message}", e);
        }
    }

    // ---- Meshes ---------------------------------------------------------------------------------------------------------------------

    private static void WriteMesh(BinaryWriter writer, MeshData mesh)
    {
        writer.Write(mesh.Name);
        writer.Write(mesh.MaterialIndex);
        Write(writer, mesh.BoundsMin);
        Write(writer, mesh.BoundsMax);
        writer.Write(mesh.Vertices.Length);
        writer.Write(mesh.Indices.Length);

        byte[] vertices = new byte[(int)MeshoptApi.EncodeVertexBufferBound((nuint)mesh.Vertices.Length, (nuint)sizeof(Vertex))];
        nuint vertexBytes;
        fixed (byte* buffer = vertices)
        fixed (Vertex* source = mesh.Vertices)
        {
            vertexBytes = MeshoptApi.EncodeVertexBuffer(buffer, (nuint)vertices.Length, source, (nuint)mesh.Vertices.Length, (nuint)sizeof(Vertex));
        }

        WriteBlob(writer, vertices.AsSpan(0, (int)vertexBytes));

        bool triangles = mesh.Indices.Length > 0 && mesh.Indices.Length % 3 == 0;
        writer.Write(triangles);
        if (triangles)
        {
            byte[] indices = new byte[(int)MeshoptApi.EncodeIndexBufferBound((nuint)mesh.Indices.Length, (nuint)mesh.Vertices.Length)];
            nuint indexBytes;
            fixed (byte* buffer = indices)
            fixed (uint* source = mesh.Indices)
            {
                indexBytes = MeshoptApi.EncodeIndexBuffer(buffer, (nuint)indices.Length, source, (nuint)mesh.Indices.Length);
            }

            WriteBlob(writer, indices.AsSpan(0, (int)indexBytes));
        }
        else
        {
            WriteBlob(writer, MemoryMarshal.AsBytes(mesh.Indices.AsSpan()));
        }
    }

    private static MeshData ReadMesh(BinaryReader reader, int limit)
    {
        string name = reader.ReadString();
        int materialIndex = reader.ReadInt32();
        Vector3 boundsMin = ReadVector3(reader);
        Vector3 boundsMax = ReadVector3(reader);
        int vertexCount = Count(reader, limit * 8);
        int indexCount = Count(reader, limit * 8);

        Vertex[] vertices = new Vertex[vertexCount];
        byte[] vertexData = ReadBlob(reader);
        fixed (byte* buffer = vertexData)
        fixed (Vertex* destination = vertices)
        {
            if (MeshoptApi.DecodeVertexBuffer(destination, (nuint)vertexCount, (nuint)sizeof(Vertex), buffer, (nuint)vertexData.Length) != 0)
            {
                throw new InvalidDataException($"the vertices of mesh '{name}' cannot be decoded");
            }
        }

        uint[] indices = new uint[indexCount];
        bool triangles = reader.ReadBoolean();
        byte[] indexData = ReadBlob(reader);
        if (triangles)
        {
            fixed (byte* buffer = indexData)
            fixed (uint* destination = indices)
            {
                if (MeshoptApi.DecodeIndexBuffer(destination, (nuint)indexCount, 4, buffer, (nuint)indexData.Length) != 0)
                {
                    throw new InvalidDataException($"the indices of mesh '{name}' cannot be decoded");
                }
            }
        }
        else
        {
            if (indexData.Length != indexCount * 4)
            {
                throw new InvalidDataException($"the indices of mesh '{name}' are the wrong size");
            }

            MemoryMarshal.Cast<byte, uint>(indexData).CopyTo(indices);
        }

        return new MeshData(name, vertices, indices, materialIndex, boundsMin, boundsMax);
    }

    // ---- Materials ------------------------------------------------------------------------------------------------------------------

    private static void WriteMaterial(BinaryWriter writer, MaterialData material)
    {
        writer.Write(material.Name);
        Write(writer, material.BaseColorFactor);
        WriteTexture(writer, material.BaseColorTexture);
        writer.Write(material.MetallicFactor);
        writer.Write(material.RoughnessFactor);
        WriteTexture(writer, material.MetallicRoughnessTexture);
        WriteTexture(writer, material.NormalTexture);
        writer.Write(material.NormalScale);
        WriteTexture(writer, material.OcclusionTexture);
        writer.Write(material.OcclusionStrength);
        Write(writer, material.EmissiveFactor);
        WriteTexture(writer, material.EmissiveTexture);
        writer.Write((byte)material.AlphaMode);
        writer.Write(material.AlphaCutoff);
        writer.Write(material.DoubleSided);
        writer.Write(material.Unlit);
    }

    private static MaterialData ReadMaterial(BinaryReader reader) => new()
    {
        Name = reader.ReadString(),
        BaseColorFactor = ReadVector4(reader),
        BaseColorTexture = ReadTexture(reader),
        MetallicFactor = reader.ReadSingle(),
        RoughnessFactor = reader.ReadSingle(),
        MetallicRoughnessTexture = ReadTexture(reader),
        NormalTexture = ReadTexture(reader),
        NormalScale = reader.ReadSingle(),
        OcclusionTexture = ReadTexture(reader),
        OcclusionStrength = reader.ReadSingle(),
        EmissiveFactor = ReadVector3(reader),
        EmissiveTexture = ReadTexture(reader),
        AlphaMode = (AlphaMode)reader.ReadByte(),
        AlphaCutoff = reader.ReadSingle(),
        DoubleSided = reader.ReadBoolean(),
        Unlit = reader.ReadBoolean(),
    };

    private static void WriteTexture(BinaryWriter writer, TextureRef? texture)
    {
        writer.Write(texture is not null);
        if (texture is { } value)
        {
            writer.Write(value.Texture);
            writer.Write(value.TexCoord);
            Write(writer, value.Offset);
            Write(writer, value.Scale);
            writer.Write(value.Rotation);
        }
    }

    private static TextureRef? ReadTexture(BinaryReader reader) =>
        reader.ReadBoolean() ? new TextureRef(reader.ReadInt32(), reader.ReadInt32(), ReadVector2(reader), ReadVector2(reader), reader.ReadSingle()) : null;

    // ---- Primitives -----------------------------------------------------------------------------------------------------------------

    private static void WriteBlob(BinaryWriter writer, ReadOnlySpan<byte> data)
    {
        writer.Write(data.Length);
        writer.Write(data);
    }

    private static byte[] ReadBlob(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("a block is longer than the file");
        }

        return reader.ReadBytes(length);
    }

    /// <summary>A count that cannot be more than the file could hold.</summary>
    private static int Count(BinaryReader reader, int limit)
    {
        int count = reader.ReadInt32();
        return count < 0 || count > limit ? throw new InvalidDataException($"a count ({count}) is larger than the file") : count;
    }

    private static void Write(BinaryWriter writer, Vector2 v)
    {
        writer.Write(v.X);
        writer.Write(v.Y);
    }

    private static void Write(BinaryWriter writer, Vector3 v)
    {
        writer.Write(v.X);
        writer.Write(v.Y);
        writer.Write(v.Z);
    }

    private static void Write(BinaryWriter writer, Vector4 v)
    {
        writer.Write(v.X);
        writer.Write(v.Y);
        writer.Write(v.Z);
        writer.Write(v.W);
    }

    private static void Write(BinaryWriter writer, Matrix4x4 m)
    {
        foreach (float value in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 })
        {
            writer.Write(value);
        }
    }

    private static Vector2 ReadVector2(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle());

    private static Vector3 ReadVector3(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static Vector4 ReadVector4(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static Matrix4x4 ReadMatrix(BinaryReader reader) => new(
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
