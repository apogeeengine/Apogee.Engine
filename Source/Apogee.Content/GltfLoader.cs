using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apogee.Content.FileSystem;
using Apogee.Core.Logging;
using Apogee.Interop;
using CgltfApi = Apogee.Native.Cgltf.Cgltf;
using Gltf = Apogee.Native.Cgltf;

namespace Apogee.Content;

/// <summary>
/// Reads glTF 2.0 (.gltf with its files, or .glb) into a <see cref="ModelData"/>: the default scene's meshes placed in the world, metallic-roughness
/// materials with their textures decoded, and punctual lights. Supported extensions: KHR_texture_transform, KHR_texture_basisu,
/// KHR_materials_emissive_strength, KHR_materials_unlit, KHR_lights_punctual. Draco and meshopt compression are refused with a clear error.
/// </summary>
public static class GltfLoader
{
    public static ModelData Load(string path, ContentOptions? options = null)
    {
        options ??= new ContentOptions();
        byte[] bytes;
        try
        {
            bytes = (options.ReadFile ?? File.ReadAllBytes)(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ContentException($"Cannot read '{path}': {e.Message}", e);
        }

        string directory = VfsPath.IsVirtual(path) ? VfsPath.DirectoryOf(VfsPath.Normalize(path)) : Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        return Load(bytes, directory, options, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Reads a model from memory. Files it refers to are looked for in <paramref name="baseDirectory"/>, a folder or a virtual path such as <c>res://models</c> (read with <see cref="ContentOptions.ReadFile"/>).</summary>
    public static ModelData Load(ReadOnlySpan<byte> file, string baseDirectory, ContentOptions? options = null, string name = "model") =>
        new Builder(options ?? new ContentOptions(), baseDirectory, name).Build(file);

    /// <summary>What cgltf's file callbacks read with, and why the last read failed.</summary>
    private sealed class FileReader(Func<string, byte[]> read)
    {
        public byte[] Read(string path) => read(path);

        public string? LastError { get; set; }
    }

    /// <summary>cgltf's allocator and file reader in managed code. They are called from native code, so nothing may throw out of them.</summary>
    private static unsafe class CgltfMemory
    {
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static void* Alloc(void* user, nuint size) => NativeMemory.Alloc(Math.Max(size, 1));

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static void Free(void* user, void* pointer) => NativeMemory.Free(pointer);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static Gltf.Result Read(Gltf.MemoryOptions* memory, Gltf.FileOptions* file, byte* path, nuint* size, void** data)
        {
            var reader = (FileReader?)GCHandle.FromIntPtr((nint)file->UserData).Target;
            try
            {
                string name = Utf8.FromPointer(path) ?? "";
                byte[] bytes = reader!.Read(VfsPath.IsVirtual(name) ? VfsPath.Normalize(name) : name);
                void* buffer = memory->AllocFunc(memory->UserData, (nuint)Math.Max(bytes.Length, 1));
                if (buffer == null)
                {
                    return Gltf.Result.OutOfMemory;
                }

                bytes.CopyTo(new Span<byte>(buffer, bytes.Length));
                *size = (nuint)bytes.Length;
                *data = buffer;
                return Gltf.Result.Success;
            }
            catch (Exception e)
            {
                if (reader is not null)
                {
                    reader.LastError = e.Message;
                }

                return e is FileNotFoundException or DirectoryNotFoundException ? Gltf.Result.FileNotFound : Gltf.Result.IoError;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static void Release(Gltf.MemoryOptions* memory, Gltf.FileOptions* file, void* data) => memory->FreeFunc(memory->UserData, data);
    }

    private sealed unsafe class Builder
    {
        private const string LogCategory = "Content";

        private readonly ContentOptions _options;
        private readonly string _baseDirectory;
        private readonly string _name;
        private readonly List<MeshData> _meshes = [];
        private readonly Dictionary<nint, int> _meshByPrimitive = [];
        private readonly List<MaterialData> _materials = [];
        private readonly Dictionary<nint, int> _materialByPointer = [];
        private readonly List<TextureData> _textures = [];
        private readonly Dictionary<(nint Image, nint Sampler, bool Srgb), int> _textureByKey = [];
        private readonly List<MeshInstance> _instances = [];
        private readonly List<LightData> _lights = [];
        private Gltf.Data* _data;
        private int _defaultMaterial = -1;

        public Builder(ContentOptions options, string baseDirectory, string name)
        {
            _options = options;
            _baseDirectory = baseDirectory;
            _name = name;
        }

        public ModelData Build(ReadOnlySpan<byte> file)
        {
            // cgltf does not copy a .glb's binary chunk: its buffers point into the memory given to Parse, so that memory has to stay
            // put until Free. A managed array (even pinned only for the call) would be moved or collected under the data's feet.
            byte* memory = (byte*)NativeMemory.Alloc((nuint)Math.Max(file.Length, 1));
            file.CopyTo(new Span<byte>(memory, file.Length));

            // cgltf allocates with ours and reads the files it refers to (external .bin buffers) through ours, so that a model in a pack or a zip loads like one on disk.
            var reader = new FileReader(_options.ReadFile ?? File.ReadAllBytes);
            GCHandle readerHandle = GCHandle.Alloc(reader);
            var parseOptions = new Gltf.Options();
            parseOptions.Memory.AllocFunc = &CgltfMemory.Alloc;
            parseOptions.Memory.FreeFunc = &CgltfMemory.Free;
            parseOptions.File.Read = &CgltfMemory.Read;
            parseOptions.File.Release = &CgltfMemory.Release;
            parseOptions.File.UserData = (void*)GCHandle.ToIntPtr(readerHandle);

            Gltf.Data* data;
            Gltf.Result result = CgltfApi.Parse(&parseOptions, memory, (nuint)file.Length, &data);
            if (result != Gltf.Result.Success)
            {
                readerHandle.Free();
                NativeMemory.Free(memory);
                throw new ContentException($"'{_name}' is not a readable glTF file: {result}");
            }

            _data = data;
            try
            {
                // External .bin files are found relative to this path; embedded buffers need none.
                string reference = Resolve(_name + ".gltf");
                fixed (byte* path = Utf8.ToZeroTerminated(reference))
                {
                    result = CgltfApi.LoadBuffers(&parseOptions, data, path);
                }

                if (result != Gltf.Result.Success)
                {
                    throw new ContentException($"'{_name}' refers to data that cannot be loaded: {result}{(reader.LastError is { } why ? $" ({why})" : "")}");
                }

                result = CgltfApi.Validate(data);
                if (result != Gltf.Result.Success)
                {
                    throw new ContentException($"'{_name}' is not valid glTF: {result}");
                }

                RefuseCompression();
                VisitScene();
                return Finish();
            }
            finally
            {
                CgltfApi.Free(data);
                NativeMemory.Free(memory);
                readerHandle.Free();
            }
        }

        /// <summary>A file the model refers to, as a path inside the folder it is in: a folder, or a virtual path (where <c>..</c> stops at the root of its scheme).</summary>
        private string Resolve(string relative) =>
            VfsPath.IsVirtual(_baseDirectory) ? VfsPath.Normalize(VfsPath.Combine(_baseDirectory, relative)) : Path.Combine(_baseDirectory, relative);

        private void RefuseCompression()
        {
            for (nuint m = 0; m < _data->MeshesCount; m++)
            {
                for (nuint p = 0; p < _data->Meshes[m].PrimitivesCount; p++)
                {
                    if (_data->Meshes[m].Primitives[p].HasDracoMeshCompression != 0)
                    {
                        throw new ContentException($"'{_name}' uses Draco mesh compression, which is not supported. Re-export it uncompressed (or with meshopt-free quantization).");
                    }
                }
            }

            for (nuint v = 0; v < _data->BufferViewsCount; v++)
            {
                if (_data->BufferViews[v].HasMeshoptCompression != 0)
                {
                    throw new ContentException($"'{_name}' uses EXT_meshopt_compression, which is not supported.");
                }
            }
        }

        private void VisitScene()
        {
            Gltf.Scene* scene = _data->Scene != null ? _data->Scene : _data->ScenesCount > 0 ? _data->Scenes : null;
            if (scene != null)
            {
                for (nuint i = 0; i < scene->NodesCount; i++)
                {
                    VisitNode(scene->Nodes[i]);
                }

                return;
            }

            // No scene: every node without a parent is a root.
            for (nuint i = 0; i < _data->NodesCount; i++)
            {
                if (_data->Nodes[i].Parent == null)
                {
                    VisitNode(&_data->Nodes[i]);
                }
            }
        }

        private void VisitNode(Gltf.Node* node)
        {
            Matrix4x4 world = WorldMatrix(node);
            if (node->Mesh != null)
            {
                for (nuint p = 0; p < node->Mesh->PrimitivesCount; p++)
                {
                    int mesh = MeshFor(node->Mesh, &node->Mesh->Primitives[p], (int)p);
                    if (mesh >= 0)
                    {
                        _instances.Add(new MeshInstance(mesh, world));
                    }
                }
            }

            if (node->Light != null)
            {
                _lights.Add(Light(node->Light, world));
            }

            for (nuint c = 0; c < node->ChildrenCount; c++)
            {
                VisitNode(node->Children[c]);
            }
        }

        private static Matrix4x4 WorldMatrix(Gltf.Node* node)
        {
            // glTF stores matrices column-major for column vectors; read as rows that is exactly System.Numerics' row-vector convention.
            float* m = stackalloc float[16];
            CgltfApi.NodeTransformWorld(node, m);
            return new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
        }

        private static LightData Light(Gltf.Light* light, Matrix4x4 world)
        {
            LightKind kind = light->Type switch
            {
                Gltf.LightType.Directional => LightKind.Directional,
                Gltf.LightType.Spot => LightKind.Spot,
                _ => LightKind.Point,
            };
            Vector3 direction = Vector3.TransformNormal(-Vector3.UnitZ, world);
            return new LightData(
                Utf8.FromPointer(light->Name) ?? "",
                kind,
                new Vector3(light->Color[0], light->Color[1], light->Color[2]),
                light->Intensity,
                light->Range,
                light->SpotInnerConeAngle,
                light->SpotOuterConeAngle == 0f ? MathF.PI / 4f : light->SpotOuterConeAngle,
                world.Translation,
                direction.LengthSquared() > 0f ? Vector3.Normalize(direction) : -Vector3.UnitZ);
        }

        private int MeshFor(Gltf.Mesh* mesh, Gltf.Primitive* primitive, int primitiveIndex)
        {
            if (_meshByPrimitive.TryGetValue((nint)primitive, out int known))
            {
                return known;
            }

            int index = -1;
            if (primitive->Type is Gltf.PrimitiveType.Triangles or Gltf.PrimitiveType.TriangleStrip or Gltf.PrimitiveType.TriangleFan)
            {
                index = BuildMesh(mesh, primitive, primitiveIndex);
            }
            else
            {
                Log.Warn(LogCategory, $"'{_name}': skipping a {primitive->Type} primitive; only triangles are drawn.");
            }

            _meshByPrimitive[(nint)primitive] = index;
            return index;
        }

        private int BuildMesh(Gltf.Mesh* mesh, Gltf.Primitive* primitive, int primitiveIndex)
        {
            Gltf.Accessor* positions = null;
            Gltf.Accessor* normals = null;
            Gltf.Accessor* tangents = null;
            Gltf.Accessor* uvs = null;
            for (nuint a = 0; a < primitive->AttributesCount; a++)
            {
                Gltf.Attribute* attribute = &primitive->Attributes[a];
                switch (attribute->Type)
                {
                    case Gltf.AttributeType.Position when attribute->Index == 0:
                        positions = attribute->Data;
                        break;
                    case Gltf.AttributeType.Normal when attribute->Index == 0:
                        normals = attribute->Data;
                        break;
                    case Gltf.AttributeType.Tangent when attribute->Index == 0:
                        tangents = attribute->Data;
                        break;
                    case Gltf.AttributeType.Texcoord when attribute->Index == 0:
                        uvs = attribute->Data;
                        break;
                }
            }

            string meshName = (Utf8.FromPointer(mesh->Name) is { Length: > 0 } n ? n : "mesh") + (mesh->PrimitivesCount > 1 ? $"#{primitiveIndex}" : "");
            if (positions == null)
            {
                throw new ContentException($"'{_name}': mesh '{meshName}' has no POSITION attribute.");
            }

            int count = checked((int)positions->Count);
            var vertices = new Vertex[count];
            float[] floats = ReadFloats(positions, 3);
            for (int i = 0; i < count; i++)
            {
                vertices[i].Position = new Vector3(floats[i * 3], floats[i * 3 + 1], floats[i * 3 + 2]);
            }

            if (normals != null)
            {
                floats = ReadFloats(normals, 3);
                for (int i = 0; i < count; i++)
                {
                    vertices[i].Normal = new Vector3(floats[i * 3], floats[i * 3 + 1], floats[i * 3 + 2]);
                }
            }

            if (tangents != null)
            {
                floats = ReadFloats(tangents, 4);
                for (int i = 0; i < count; i++)
                {
                    vertices[i].Tangent = new Vector4(floats[i * 4], floats[i * 4 + 1], floats[i * 4 + 2], floats[i * 4 + 3]);
                }
            }

            if (uvs != null)
            {
                floats = ReadFloats(uvs, 2);
                for (int i = 0; i < count; i++)
                {
                    vertices[i].Uv0 = new Vector2(floats[i * 2], floats[i * 2 + 1]);
                }
            }

            uint[] indices = TriangleIndices(primitive, count);
            int material = primitive->Material != null ? MaterialFor(primitive->Material) : DefaultMaterial();

            if (normals == null)
            {
                (vertices, indices) = MeshProcessing.WithFlatNormals(vertices, indices);
            }

            if (tangents == null && _materials[material].NormalTexture is not null)
            {
                if (_options.OptimizeMeshes)
                {
                    (vertices, indices) = MeshProcessing.Optimize(vertices, indices);
                }

                MeshProcessing.GenerateTangents(vertices, indices);
            }
            else if (_options.OptimizeMeshes)
            {
                (vertices, indices) = MeshProcessing.Optimize(vertices, indices);
            }

            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);
            foreach (Vertex v in vertices)
            {
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
            }

            _meshes.Add(new MeshData(meshName, vertices, indices, material, min, max));
            return _meshes.Count - 1;
        }

        private static float[] ReadFloats(Gltf.Accessor* accessor, int components)
        {
            if ((int)CgltfApi.NumComponents(accessor->Type) != components)
            {
                throw new ContentException($"An attribute has {CgltfApi.NumComponents(accessor->Type)} components where {components} are expected.");
            }

            float[] result = new float[checked((int)accessor->Count * components)];
            fixed (float* data = result)
            {
                CgltfApi.AccessorUnpackFloats(accessor, data, (nuint)result.Length);
            }

            return result;
        }

        private static uint[] TriangleIndices(Gltf.Primitive* primitive, int vertexCount)
        {
            uint[] source;
            if (primitive->Indices != null)
            {
                source = new uint[checked((int)primitive->Indices->Count)];
                fixed (uint* data = source)
                {
                    CgltfApi.AccessorUnpackIndices(primitive->Indices, data, sizeof(uint), (nuint)source.Length);
                }
            }
            else
            {
                source = new uint[vertexCount];
                for (int i = 0; i < source.Length; i++)
                {
                    source[i] = (uint)i;
                }
            }

            foreach (uint index in source)
            {
                if (index >= (uint)vertexCount)
                {
                    throw new ContentException($"A mesh indexes vertex {index} but has only {vertexCount}.");
                }
            }

            switch (primitive->Type)
            {
                case Gltf.PrimitiveType.TriangleStrip:
                {
                    var list = new List<uint>();
                    for (int i = 2; i < source.Length; i++)
                    {
                        // Every second triangle of a strip is wound the other way.
                        if ((i & 1) == 0)
                        {
                            list.AddRange([source[i - 2], source[i - 1], source[i]]);
                        }
                        else
                        {
                            list.AddRange([source[i - 1], source[i - 2], source[i]]);
                        }
                    }

                    return [.. list];
                }

                case Gltf.PrimitiveType.TriangleFan:
                {
                    var list = new List<uint>();
                    for (int i = 2; i < source.Length; i++)
                    {
                        list.AddRange([source[0], source[i - 1], source[i]]);
                    }

                    return [.. list];
                }

                default:
                    return source.Length % 3 == 0 ? source : source[..(source.Length / 3 * 3)];
            }
        }

        private int DefaultMaterial()
        {
            if (_defaultMaterial < 0)
            {
                _materials.Add(new MaterialData { Name = "default" });
                _defaultMaterial = _materials.Count - 1;
            }

            return _defaultMaterial;
        }

        private int MaterialFor(Gltf.Material* material)
        {
            if (_materialByPointer.TryGetValue((nint)material, out int known))
            {
                return known;
            }

            Gltf.PbrMetallicRoughness* pbr = &material->PbrMetallicRoughness;
            bool metallicRoughness = material->HasPbrMetallicRoughness != 0;
            float strength = material->HasEmissiveStrength != 0 ? material->EmissiveStrength.EmissiveStrength_ : 1f;
            var data = new MaterialData
            {
                Name = Utf8.FromPointer(material->Name) ?? "",
                BaseColorFactor = metallicRoughness ? new Vector4(pbr->BaseColorFactor[0], pbr->BaseColorFactor[1], pbr->BaseColorFactor[2], pbr->BaseColorFactor[3]) : Vector4.One,
                BaseColorTexture = metallicRoughness ? TextureFor(&pbr->BaseColorTexture, srgb: true) : null,
                MetallicFactor = metallicRoughness ? pbr->MetallicFactor : 1f,
                RoughnessFactor = metallicRoughness ? pbr->RoughnessFactor : 1f,
                MetallicRoughnessTexture = metallicRoughness ? TextureFor(&pbr->MetallicRoughnessTexture, srgb: false) : null,
                NormalTexture = TextureFor(&material->NormalTexture, srgb: false),
                NormalScale = material->NormalTexture.Texture != null ? material->NormalTexture.Scale : 1f,
                OcclusionTexture = TextureFor(&material->OcclusionTexture, srgb: false),
                OcclusionStrength = material->OcclusionTexture.Texture != null ? material->OcclusionTexture.Scale : 1f,
                EmissiveFactor = new Vector3(material->EmissiveFactor[0], material->EmissiveFactor[1], material->EmissiveFactor[2]) * strength,
                EmissiveTexture = TextureFor(&material->EmissiveTexture, srgb: true),
                AlphaMode = material->AlphaMode switch { Gltf.AlphaMode.Mask => AlphaMode.Mask, Gltf.AlphaMode.Blend => AlphaMode.Blend, _ => AlphaMode.Opaque },
                AlphaCutoff = material->AlphaCutoff,
                DoubleSided = material->DoubleSided != 0,
                Unlit = material->Unlit != 0,
            };

            _materials.Add(data);
            _materialByPointer[(nint)material] = _materials.Count - 1;
            return _materials.Count - 1;
        }

        private TextureRef? TextureFor(Gltf.TextureView* view, bool srgb)
        {
            if (view->Texture == null)
            {
                return null;
            }

            Gltf.Texture* texture = view->Texture;
            Gltf.Image* image = texture->HasBasisu != 0 && texture->BasisuImage != null ? texture->BasisuImage : texture->Image;
            if (image == null)
            {
                Log.Warn(LogCategory, $"'{_name}': a texture has no image I can read (WebP is not supported).");
                return null;
            }

            var key = ((nint)image, (nint)texture->Sampler, srgb);
            if (!_textureByKey.TryGetValue(key, out int index))
            {
                _textures.Add(DecodeTexture(texture, image, srgb));
                index = _textures.Count - 1;
                _textureByKey[key] = index;
            }

            Vector2 offset = Vector2.Zero;
            Vector2 scale = Vector2.One;
            float rotation = 0f;
            int texCoord = view->Texcoord;
            if (view->HasTransform != 0)
            {
                offset = new Vector2(view->Transform.Offset[0], view->Transform.Offset[1]);
                scale = new Vector2(view->Transform.Scale[0], view->Transform.Scale[1]);
                rotation = view->Transform.Rotation;
                texCoord = view->Transform.HasTexcoord != 0 ? view->Transform.Texcoord : texCoord;
            }

            if (texCoord != 0)
            {
                Log.Warn(LogCategory, $"'{_name}': a texture uses TEXCOORD_{texCoord}; only TEXCOORD_0 is read.");
            }

            return new TextureRef(index, texCoord, offset, scale, rotation);
        }

        private TextureData DecodeTexture(Gltf.Texture* texture, Gltf.Image* image, bool srgb)
        {
            string name = Utf8.FromPointer(image->Name) is { Length: > 0 } n ? n : Utf8.FromPointer(image->Uri) is { Length: > 0 } uri && !uri.StartsWith("data:", StringComparison.Ordinal) ? Uri.UnescapeDataString(uri) : $"image{image - _data->Images}";
            SamplerData sampler = SamplerFor(texture->Sampler);
            byte[] file = ImageBytes(name, image);
            return Ktx2Loader.IsKtx2(file)
                ? Ktx2Loader.Load(name, file, srgb, _options.TextureCaps, sampler)
                : ImageDecoder.Decode(name, file, srgb, sampler, _options.GenerateMips);
        }

        private static SamplerData SamplerFor(Gltf.Sampler* sampler)
        {
            if (sampler == null)
            {
                return SamplerData.Default;
            }

            static TextureWrap Wrap(Gltf.WrapMode mode) => mode switch
            {
                Gltf.WrapMode.ClampToEdge => TextureWrap.ClampToEdge,
                Gltf.WrapMode.MirroredRepeat => TextureWrap.MirroredRepeat,
                _ => TextureWrap.Repeat,
            };

            bool nearest = sampler->MagFilter == Gltf.FilterType.Nearest;
            bool mipmaps = sampler->MinFilter is Gltf.FilterType.Undefined or Gltf.FilterType.NearestMipmapNearest or Gltf.FilterType.LinearMipmapNearest
                or Gltf.FilterType.NearestMipmapLinear or Gltf.FilterType.LinearMipmapLinear;
            return new SamplerData(nearest ? TextureFilter.Nearest : TextureFilter.Linear, mipmaps, Wrap(sampler->WrapS), Wrap(sampler->WrapT));
        }

        private byte[] ImageBytes(string name, Gltf.Image* image)
        {
            if (image->BufferView != null)
            {
                return new ReadOnlySpan<byte>(CgltfApi.BufferViewData(image->BufferView), checked((int)image->BufferView->Size)).ToArray();
            }

            string uri = Utf8.FromPointer(image->Uri) ?? throw new ContentException($"'{_name}': image '{name}' has neither a URI nor a buffer view.");
            if (uri.StartsWith("data:", StringComparison.Ordinal))
            {
                int comma = uri.IndexOf(',', StringComparison.Ordinal);
                if (comma < 0 || !uri[..comma].EndsWith(";base64", StringComparison.Ordinal))
                {
                    throw new ContentException($"'{_name}': image '{name}' has a data URI that is not base64.");
                }

                return Convert.FromBase64String(uri[(comma + 1)..]);
            }

            string path = Resolve(Uri.UnescapeDataString(uri));
            try
            {
                return (_options.ReadFile ?? File.ReadAllBytes)(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ContentException($"'{_name}': cannot read image '{path}': {e.Message}", e);
            }
        }

        private ModelData Finish()
        {
            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);
            foreach (MeshInstance instance in _instances)
            {
                MeshData mesh = _meshes[instance.Mesh];
                for (int corner = 0; corner < 8; corner++)
                {
                    var point = new Vector3((corner & 1) != 0 ? mesh.BoundsMax.X : mesh.BoundsMin.X, (corner & 2) != 0 ? mesh.BoundsMax.Y : mesh.BoundsMin.Y, (corner & 4) != 0 ? mesh.BoundsMax.Z : mesh.BoundsMin.Z);
                    Vector3 world = Vector3.Transform(point, instance.World);
                    min = Vector3.Min(min, world);
                    max = Vector3.Max(max, world);
                }
            }

            if (_instances.Count == 0)
            {
                min = max = Vector3.Zero;
            }

            return new ModelData
            {
                Name = _name,
                Meshes = _meshes,
                Instances = _instances,
                Materials = _materials,
                Textures = _textures,
                Lights = _lights,
                BoundsMin = min,
                BoundsMax = max,
            };
        }
    }
}
