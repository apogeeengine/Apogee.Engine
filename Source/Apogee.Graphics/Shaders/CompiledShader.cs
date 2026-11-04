using System;
using System.Collections.Generic;

namespace Apogee.Graphics.Shaders;

/// <summary>The result of compiling one Slang module for one backend: the code of each of its entry points (SPIR-V, or Metal shading language text), what the compiler learned about its parameters, and the files it read.</summary>
public sealed class CompiledShader
{
    private readonly Dictionary<string, byte[]> _spirv;

    public CompiledShader(string moduleName, Dictionary<string, byte[]> spirv, string reflectionJson, string warnings, IReadOnlyList<string> dependencies, bool fromCache, ShaderBackend backend = ShaderBackend.Spirv)
    {
        ModuleName = moduleName;
        Backend = backend;
        _spirv = spirv;
        EntryPoints = [.. spirv.Keys];
        ReflectionJson = reflectionJson;
        Warnings = warnings;
        Dependencies = dependencies;
        FromCache = fromCache;
    }

    internal Dictionary<string, byte[]> Entries => _spirv;

    public string ModuleName { get; }

    /// <summary>What the code is for.</summary>
    public ShaderBackend Backend { get; }

    public IReadOnlyList<string> EntryPoints { get; }

    /// <summary>Slang's reflection of the program as JSON: parameters and their bindings, thread group sizes.</summary>
    public string ReflectionJson { get; }

    /// <summary>The compiler's warnings (errors throw), empty when there are none.</summary>
    public string Warnings { get; }

    /// <summary>The files read to compile it, which a cache or a hot reload watches.</summary>
    public IReadOnlyList<string> Dependencies { get; }

    /// <summary>The result came from the on-disk cache, not from the compiler.</summary>
    public bool FromCache { get; }

    /// <summary>The code of an entry point as a pipeline takes it.</summary>
    public ShaderCode Code(string entryPoint) => new(Backend, Bytes(entryPoint), entryPoint, ThreadGroupSizeOf(entryPoint));

    /// <summary>The <c>[numthreads]</c> of a compute entry point, from the reflection; null for any other stage.</summary>
    private ThreadGroupSize? ThreadGroupSizeOf(string entryPoint)
    {
        if (ReflectionJson.Length == 0)
        {
            return null;
        }

        using var document = System.Text.Json.JsonDocument.Parse(ReflectionJson);
        if (document.RootElement.TryGetProperty("entryPoints", out System.Text.Json.JsonElement entries))
        {
            foreach (System.Text.Json.JsonElement entry in entries.EnumerateArray())
            {
                if (entry.TryGetProperty("name", out System.Text.Json.JsonElement name) && name.GetString() == entryPoint && entry.TryGetProperty("threadGroupSize", out System.Text.Json.JsonElement size) && size.GetArrayLength() == 3)
                {
                    return new ThreadGroupSize(size[0].GetUInt32(), size[1].GetUInt32(), size[2].GetUInt32());
                }
            }
        }

        return null;
    }

    /// <summary>The SPIR-V of an entry point (the shader must have been compiled for Vulkan).</summary>
    public byte[] Spirv(string entryPoint) =>
        Backend == ShaderBackend.Spirv ? Bytes(entryPoint) : throw new InvalidOperationException($"Shader '{ModuleName}' was compiled for {Backend}, not SPIR-V.");

    /// <summary>The Metal shading language source of an entry point (the shader must have been compiled for Metal). Every entry point of a module has the whole module's source.</summary>
    public string Msl(string entryPoint) =>
        Backend == ShaderBackend.Msl ? System.Text.Encoding.UTF8.GetString(Bytes(entryPoint)) : throw new InvalidOperationException($"Shader '{ModuleName}' was compiled for {Backend}, not Metal.");

    private byte[] Bytes(string entryPoint) =>
        _spirv.TryGetValue(entryPoint, out byte[]? code)
            ? code
            : throw new ArgumentException($"Shader '{ModuleName}' has no entry point '{entryPoint}'. It has: {string.Join(", ", EntryPoints)}.", nameof(entryPoint));
}
