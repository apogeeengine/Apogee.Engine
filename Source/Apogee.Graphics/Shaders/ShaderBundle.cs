using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Apogee.Graphics.Shaders;

/// <summary>The shader language of a piece of compiled code in a bundle. SPIR-V (Vulkan) and Metal shading language source (Metal) are made; DXIL is reserved so that a bundle can hold every backend of a build.</summary>
public enum ShaderBackend : byte
{
    Spirv = 1,

    /// <summary>Metal Shading Language source text (the macOS backend), compiled by the Metal driver when the shader is loaded.</summary>
    Msl = 2,

    /// <summary>DXIL. Reserved: no backend uses it yet.</summary>
    Dxil = 3,
}

/// <summary>
/// What compiled shaders assume about the engine: the bindless descriptor layout and the push-constant size every pipeline shares (<see cref="BindlessHeap"/>). A bundle remembers the version it
/// was made for and is refused by an engine with another, so a mod's old shader fails with a message instead of a driver crash. Bump it when <c>BindlessHeap</c>'s layout changes, on any
/// backend: version 2 added the Metal code and the shaders' bindless parameter block (argument buffer 1; push constants are buffer 0).
/// </summary>
public static class ShaderAbi
{
    public const int Version = 2;
}

/// <summary>
/// The artifact of a shader module (<c>.ashader</c>): one or more <em>variants</em>, each the module compiled with one set of preprocessor defines (the default one has none), and for each
/// variant its reflection and the code of every entry point, per backend. A reader takes the code of the backend it runs on and skips the others, so one file serves Vulkan and Metal. The reflection is that of the first backend written (SPIR-V). Specialization constants need no variant: they stay in the SPIR-V and are listed in the reflection.
/// </summary>
public static class ShaderBundle
{
    public const string Extension = ".ashader";

    private const uint Magic = 0x42485341; // "ASHB"
    private const int FormatVersion = 1;

    /// <summary>The name of a variant: its defines sorted by name, as <c>A=1;B=2</c>. The default variant, with no defines, is the empty string.</summary>
    public static string VariantKey(IEnumerable<KeyValuePair<string, string>> defines) =>
        string.Join(';', defines.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => d.Key + "=" + d.Value));

    /// <summary>Writes variants that were compiled for one backend each.</summary>
    public static byte[] Write(string moduleName, IReadOnlyList<(string Variant, CompiledShader Shader)> variants) =>
        Write(moduleName, [.. variants.Select(v => (v.Variant, (IReadOnlyList<CompiledShader>)[v.Shader]))]);

    /// <summary>Writes variants, each compiled for every backend in its list (the first one's reflection and warnings are kept).</summary>
    public static byte[] Write(string moduleName, IReadOnlyList<(string Variant, IReadOnlyList<CompiledShader> Backends)> variants)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(ShaderAbi.Version);
            writer.Write(moduleName);
            writer.Write(variants.Count);
            foreach ((string variant, IReadOnlyList<CompiledShader> backends) in variants)
            {
                writer.Write(variant);
                writer.Write(backends[0].ReflectionJson);
                writer.Write(backends[0].Warnings);
                writer.Write(backends.Sum(b => b.EntryPoints.Count));
                foreach (CompiledShader shader in backends)
                {
                    foreach (string entryPoint in shader.EntryPoints)
                    {
                        byte[] code = shader.Code(entryPoint).Code;
                        writer.Write((byte)shader.Backend);
                        writer.Write(entryPoint);
                        writer.Write(code.Length);
                        writer.Write(code);
                    }
                }
            }
        }

        return stream.ToArray();
    }

    /// <summary>The names of the variants in a bundle.</summary>
    /// <exception cref="InvalidDataException">The data is not a bundle.</exception>
    /// <exception cref="ShaderCompileException">It was made for another shader ABI.</exception>
    public static IReadOnlyList<string> Variants(byte[] bundle) => [.. ReadAll(bundle, null, ShaderBackend.Spirv).Select(v => v.Variant)];

    /// <summary>The code of one variant of a bundle for one backend (SPIR-V by default).</summary>
    /// <exception cref="InvalidDataException">The data is not a bundle, or it has no such variant.</exception>
    /// <exception cref="ShaderCompileException">It was made for another shader ABI.</exception>
    public static CompiledShader Read(byte[] bundle, string variant = "", IReadOnlyList<string>? dependencies = null, bool fromCache = false, ShaderBackend backend = ShaderBackend.Spirv)
    {
        List<(string Variant, CompiledShader Shader)> all = ReadAll(bundle, variant, backend);
        foreach ((string name, CompiledShader shader) in all)
        {
            if (name == variant)
            {
                if (shader.EntryPoints.Count == 0)
                {
                    throw ShaderCompileException.Unavailable(shader.ModuleName, $"The shader bundle has no {backend} code: import it again with this engine's Apogee.Runtime --import.");
                }

                return new CompiledShader(shader.ModuleName, shader.Entries, shader.ReflectionJson, shader.Warnings, dependencies ?? [], fromCache, backend);
            }
        }

        throw new InvalidDataException($"The shader bundle has no variant '{variant}'. It has: {string.Join(", ", all.Select(v => v.Variant.Length == 0 ? "(default)" : v.Variant))}.");
    }

    private static List<(string Variant, CompiledShader Shader)> ReadAll(byte[] bundle, string? only, ShaderBackend wanted)
    {
        var result = new List<(string, CompiledShader)>();
        try
        {
            using var reader = new BinaryReader(new MemoryStream(bundle), Encoding.UTF8);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion)
            {
                throw new InvalidDataException("Not a shader bundle, or one of another format version.");
            }

            int abi = reader.ReadInt32();
            if (abi != ShaderAbi.Version)
            {
                throw ShaderCompileException.Unavailable("bundle", $"The shader was compiled for shader ABI {abi} and this engine is ABI {ShaderAbi.Version}: import it again with this engine's Apogee.Runtime --import.");
            }

            string moduleName = reader.ReadString();
            int variantCount = reader.ReadInt32();
            for (int v = 0; v < variantCount; v++)
            {
                string variant = reader.ReadString();
                string reflection = reader.ReadString();
                string warnings = reader.ReadString();
                int blobCount = reader.ReadInt32();
                var spirv = new Dictionary<string, byte[]>();
                for (int b = 0; b < blobCount; b++)
                {
                    var backend = (ShaderBackend)reader.ReadByte();
                    string entryPoint = reader.ReadString();
                    int length = reader.ReadInt32();
                    byte[] code = reader.ReadBytes(length);
                    if (code.Length != length)
                    {
                        throw new EndOfStreamException();
                    }

                    if (backend == wanted && (only is null || only == variant))
                    {
                        spirv[entryPoint] = code;
                    }
                }

                result.Add((variant, new CompiledShader(moduleName, spirv, reflection, warnings, [], fromCache: false, wanted)));
            }
        }
        catch (Exception e) when (e is EndOfStreamException or ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidDataException("The shader bundle is damaged.", e);
        }

        return result;
    }
}
