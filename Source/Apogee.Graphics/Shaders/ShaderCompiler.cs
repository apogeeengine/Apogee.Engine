using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Core.Logging;
using Apogee.Interop;
using Apogee.Native.Slang;
using SlangApi = Apogee.Native.Slang.Slang;
using SlangResult = Apogee.Native.Slang.Result;
using SlangSession = Apogee.Native.Slang.Session;

namespace Apogee.Graphics.Shaders;

public sealed class ShaderCompilerOptions
{
    /// <summary>Directories searched for <c>import</c> and <c>#include</c>.</summary>
    public IReadOnlyList<string> SearchPaths { get; set; } = [];

    public IReadOnlyDictionary<string, string> Defines { get; set; } = new Dictionary<string, string>();

    /// <summary>0 none, 1 default, 2 high, 3 maximal.</summary>
    public int Optimization { get; set; } = 1;

    /// <summary>0 none, 1 minimal, 2 standard (line tables for RenderDoc), 3 maximal.</summary>
    public int DebugInfo { get; set; }

    /// <summary>Where compiled code is kept between runs; null for no cache.</summary>
    public string? CacheDirectory { get; set; }

    /// <summary>What to compile to: SPIR-V for Vulkan (the default), or Metal shading language source text for Metal.</summary>
    public ShaderBackend Backend { get; set; } = ShaderBackend.Spirv;
}

/// <summary>Compiles Slang to SPIR-V (Vulkan 1.3, SPIR-V 1.6) or to Metal shading language, with row-major matrices and entry points that keep their names. Thread safe: compiles are serialized, across every compiler in the process.</summary>
public sealed unsafe class ShaderCompiler
{
    private const string LogCategory = "Shaders";

    private readonly ShaderCompilerOptions _options;
    private readonly ShaderCache? _cache;
    // Slang's global session is shared by every compiler of the process and is not safe to use from two threads at once.
    private static readonly object s_slangLock = new();

    private readonly string _optionsKey;

    public ShaderCompiler(ShaderCompilerOptions? options = null)
    {
        _options = options ?? new ShaderCompilerOptions();
        _cache = _options.CacheDirectory is { } directory && _options.Backend == ShaderBackend.Spirv ? new ShaderCache(directory) : null;
        _optionsKey = string.Join(
            ';',
            $"t{(int)_options.Backend}",
            $"o{_options.Optimization}",
            $"g{_options.DebugInfo}",
            string.Join(',', _options.SearchPaths.Select(Path.GetFullPath)),
            string.Join(',', _options.Defines.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => d.Key + "=" + d.Value)));
    }

    /// <summary>True when the Slang compiler library can be loaded. Games that ship without it (everything is compiled ahead of time) see false.</summary>
    public static bool IsAvailable => NativeLibraries.TryLoad(typeof(SlangApi).Assembly, "slang_c", out _);

    /// <summary>The Slang release the engine ships, such as "2026.19".</summary>
    public static string CompilerVersion => Utf8.FromPointer(SlangApi.Version())!;

    /// <summary>Compiles a module given as text. <paramref name="path"/> names it in diagnostics and, when it is a real file, in the cache's dependency list.</summary>
    public CompiledShader CompileSource(string moduleName, string source, string? path = null)
    {
        string? key = _cache is null ? null : ShaderCache.Key(CompilerVersion, _optionsKey, moduleName, source);
        if (_cache is not null && _cache.TryLoad(key!, moduleName) is { } cached)
        {
            return cached;
        }

        CompiledShader compiled = Compile(moduleName, source, path ?? moduleName + ".slang");
        if (_cache is not null)
        {
            _cache.Store(key!, compiled);
        }

        return compiled;
    }

    /// <summary>Compiles a shader file; its directory is searched for imports too.</summary>
    public CompiledShader CompileFile(string path)
    {
        string full = Path.GetFullPath(path);
        return CompileSource(Path.GetFileNameWithoutExtension(full), File.ReadAllText(full), full);
    }

    private CompiledShader Compile(string moduleName, string source, string path)
    {
        lock (s_slangLock)
        {
            // A fresh session per compile: modules are not kept between calls, so an edited import is always read again.
            var searchPaths = _options.SearchPaths.Select(Path.GetFullPath).ToList();
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory && File.Exists(Path.GetFullPath(path)) && !searchPaths.Contains(directory))
            {
                searchPaths.Add(directory);
            }

            using var searchPathStrings = new Utf8Array(searchPaths);
            // The Metal build sees APOGEE_METAL, which the engine's shaders use to pin their buffer indices (Vulkan's bindings need no such thing).
            Dictionary<string, string> defines = new(_options.Defines);
            if (_options.Backend == ShaderBackend.Msl)
            {
                defines["APOGEE_METAL"] = "1";
            }

            string[] defineNames = [.. defines.Keys];
            using var defineNameStrings = new Utf8Array(defineNames);
            using var defineValueStrings = new Utf8Array([.. defineNames.Select(n => defines[n])]);
            var macros = new Macro[defineNames.Length];
            for (int i = 0; i < macros.Length; i++)
            {
                macros[i] = new Macro { Name = defineNameStrings.Items[i], Value = defineValueStrings.Items[i] };
            }

            SlangSession* session;
            fixed (Macro* macroData = macros)
            {
                var desc = new SessionDesc
                {
                    SearchPaths = searchPathStrings.Items,
                    SearchPathCount = searchPathStrings.Count,
                    Macros = macroData,
                    MacroCount = macros.Length,
                    Optimization = _options.Optimization,
                    DebugInfo = _options.DebugInfo,
                    Target = _options.Backend == ShaderBackend.Msl ? 1 : 0,
                };
                int status = SlangApi.SessionCreate(&desc, &session);
                if (status < 0)
                {
                    throw new ShaderCompileException(moduleName, $"The Slang compiler could not create a session (status {status:X8}).");
                }
            }

            try
            {
                SlangResult* result;
                fixed (byte* module = Utf8.ToZeroTerminated(moduleName))
                fixed (byte* pathText = Utf8.ToZeroTerminated(path))
                fixed (byte* sourceText = Utf8.ToZeroTerminated(source))
                {
                    _ = SlangApi.Compile(session, module, pathText, sourceText, null, 0, &result);
                }

                try
                {
                    string diagnostics = Utf8.FromPointer(SlangApi.ResultDiagnostics(result)) ?? "";
                    if (SlangApi.ResultStatus(result) < 0)
                    {
                        throw new ShaderCompileException(moduleName, diagnostics);
                    }

                    var spirv = new Dictionary<string, byte[]>();
                    for (int i = 0; i < SlangApi.ResultEntryPointCount(result); i++)
                    {
                        nuint size;
                        byte* code = (byte*)SlangApi.ResultCode(result, i, &size);
                        spirv[Utf8.FromPointer(SlangApi.ResultEntryPointName(result, i))!] = new ReadOnlySpan<byte>(code, checked((int)size)).ToArray();
                    }

                    var dependencies = new List<string>();
                    for (int i = 0; i < SlangApi.ResultDependencyCount(result); i++)
                    {
                        dependencies.Add(Utf8.FromPointer(SlangApi.ResultDependency(result, i))!);
                    }

                    if (diagnostics.Length > 0)
                    {
                        Log.Warn(LogCategory, $"{moduleName}: {diagnostics.TrimEnd()}");
                    }

                    return new CompiledShader(moduleName, spirv, Utf8.FromPointer(SlangApi.ResultReflectionJson(result)) ?? "", diagnostics, dependencies, fromCache: false, _options.Backend);
                }
                finally
                {
                    SlangApi.ResultDestroy(result);
                }
            }
            finally
            {
                SlangApi.SessionDestroy(session);
            }
        }
    }
}
