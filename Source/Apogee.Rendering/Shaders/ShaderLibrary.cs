using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Content;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;
using Apogee.Graphics.Shaders;

namespace Apogee.Rendering.Shaders;

/// <summary>
/// Where renderers get their shaders. <see cref="Load"/> takes the compiled artifact of a shader module when its project was imported (<see cref="ShaderImporter"/>), which is how a shipped game
/// and a mod that ships its shaders work: the Slang compiler is never loaded. A module without an artifact is compiled, as in development, if the compiler is there, and the error says what to
/// do when it is not. The engine's own shaders are mounted as <c>shaders://</c> (<see cref="Standard"/>); a mod's are at <c>mod://id/…</c>, with their artifacts in <c>mod://id/imported</c>.
/// The library remembers which files each module it loaded was made from, so that when files change (<see cref="NotifyChanged"/>, from <c>HotReloader.ShadersChanged</c> or a
/// <see cref="VfsWatcher"/>) it can say which modules are out of date (<see cref="ModulesChanged"/>): <see cref="ReloadablePipelines{T}"/> rebuilds on that. It is thread safe.
/// </summary>
public sealed class ShaderLibrary
{
    private readonly ContentLoader _content;
    private readonly ShaderCompilerOptions _options;
    private readonly Func<ShaderCompilerOptions, ShaderCompiler> _createCompiler;
    private readonly Dictionary<string, ShaderCompiler> _compilers = [];
    private readonly Dictionary<string, IReadOnlyList<string>> _loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <param name="files">The file system the shaders are read from.</param>
    /// <param name="options">The compiler's options, for shaders that have to be compiled.</param>
    /// <param name="createCompiler">Makes the compiler (tests replace it); the default is <see cref="ShaderCompiler"/>.</param>
    /// <param name="canCompile">Says whether shaders without an artifact can be compiled; by default whether the compiler library loads.</param>
    public ShaderLibrary(VirtualFileSystem files, ShaderCompilerOptions? options = null, Func<ShaderCompilerOptions, ShaderCompiler>? createCompiler = null, bool? canCompile = null)
    {
        Files = files;
        _content = new ContentLoader(files);
        _options = options ?? new ShaderCompilerOptions();
        _createCompiler = createCompiler ?? (o => new ShaderCompiler(o));
        CanCompile = canCompile ?? (createCompiler is not null || ShaderCompiler.IsAvailable);
    }

    public VirtualFileSystem Files { get; }

    /// <summary>True when shaders without an artifact can be compiled: the compiler library is there.</summary>
    public bool CanCompile { get; }

    /// <summary>Raised, on the thread that called <see cref="NotifyChanged"/>, with the modules that were loaded and depend on a file that changed.</summary>
    public event Action<IReadOnlyCollection<string>>? ModulesChanged;

    /// <summary>The compile errors of the shaders that are being rebuilt and could not be, one message per group of pipelines; empty when everything is as the files say. The dev overlay shows them.</summary>
    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_lock)
            {
                return [.. _errors.Values];
            }
        }
    }

    /// <summary>The engine's shaders: the <c>Shaders</c> folder next to the program, as <c>shaders://</c>.</summary>
    public static ShaderLibrary Standard(string? cacheDirectory = null, string? shaderDirectory = null)
    {
        var files = new VirtualFileSystem();
        MountEngineShaders(files, shaderDirectory is null ? null : [shaderDirectory]);
        return new ShaderLibrary(files, new ShaderCompilerOptions { CacheDirectory = cacheDirectory });
    }

    /// <summary>
    /// Mounts the engine's shaders as <c>shaders://</c>. With <paramref name="directories"/> null that is the <c>Shaders</c> folder next to the program, or, with <paramref name="sources"/>, the folders
    /// of the shaders' own sources when the program runs from the repository, so that editing them is what the running program sees.
    /// </summary>
    public static void MountEngineShaders(VirtualFileSystem files, IEnumerable<string>? directories = null, bool sources = false)
    {
        foreach (string directory in directories ?? (sources ? SourceFolders() : []).DefaultIfEmpty(Path.Combine(AppContext.BaseDirectory, "Shaders")))
        {
            files.Mount("shaders://", new DirectorySource(directory));
        }
    }

    /// <summary>The folders of the shaders' sources (<c>Source/Apogee.Rendering/Shaders</c>, <c>Source/Apogee.DevTools/Shaders</c>, <c>Source/Apogee.UI/Shaders</c>) above the program, or none when it does not run from the repository.</summary>
    private static IEnumerable<string> SourceFolders()
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            string rendering = Path.Combine(directory, "Source", "Apogee.Rendering", "Shaders");
            if (Directory.Exists(rendering))
            {
                yield return rendering;
                string devTools = Path.Combine(directory, "Source", "Apogee.DevTools", "Shaders");
                if (Directory.Exists(devTools))
                {
                    yield return devTools;
                }

                string ui = Path.Combine(directory, "Source", "Apogee.UI", "Shaders");
                if (Directory.Exists(ui))
                {
                    yield return ui;
                }

                yield break;
            }
        }
    }

    /// <summary>Reads the artifact indexes again (after an import).</summary>
    public void Refresh()
    {
        lock (_lock)
        {
            _content.Refresh();
        }
    }

    /// <summary>The module at <paramref name="path"/> (<c>shaders://forward.slang</c>), in the variant that <paramref name="defines"/> names, with the code for <paramref name="backend"/> (a device's <c>ShaderBackend</c>).</summary>
    /// <exception cref="ShaderCompileException">It has no artifact and cannot be compiled, or it does not compile.</exception>
    public CompiledShader Load(string path, IReadOnlyDictionary<string, string>? defines = null, ShaderBackend backend = ShaderBackend.Spirv)
    {
        string canonical = VfsPath.Normalize(path);
        string variant = ShaderBundle.VariantKey(defines ?? new Dictionary<string, string>());
        string moduleName = Path.GetFileNameWithoutExtension(canonical);

        string? artifact;
        IReadOnlyList<string> artifactDependencies = [];
        lock (_lock)
        {
            artifact = _content.ArtifactPathFor(canonical);
            if (artifact is not null)
            {
                artifactDependencies = _content.ArtifactDependenciesFor(canonical);
            }
        }

        Exception? artifactProblem = null;
        if (artifact is not null)
        {
            try
            {
                return Remember(canonical, ShaderBundle.Read(Files.ReadAllBytes(artifact), variant, artifactDependencies, backend: backend));
            }
            catch (Exception e) when (e is ShaderCompileException or InvalidDataException or IOException)
            {
                artifactProblem = e;
            }
        }

        if (!Files.FileExists(canonical))
        {
            throw artifactProblem as ShaderCompileException
                ?? ShaderCompileException.Unavailable(moduleName, $"Shader '{canonical}' has neither a compiled artifact nor a source{(artifactProblem is null ? "" : ": " + artifactProblem.Message)}.");
        }

        if (!CanCompile)
        {
            throw ShaderCompileException.Unavailable(moduleName, $"Shader '{canonical}' has no usable compiled artifact{(artifactProblem is null ? "" : " (" + artifactProblem.Message + ")")} and this build does not have the shader compiler. Import its folder with Apogee.Runtime --import.");
        }

        ShaderCompiler compiler = CompilerFor(variant, defines, backend);
        return Remember(canonical, Files.TryGetPhysicalPath(canonical) is { } physical
            ? compiler.CompileFile(physical)
            : compiler.CompileSource(moduleName, Files.ReadAllText(canonical), canonical));
    }

    /// <summary>
    /// Says that these files changed (virtual paths, as <c>HotReloader.ShadersChanged</c> reports them): the artifact indexes are read again, and <see cref="ModulesChanged"/> is raised with the
    /// loaded modules that are one of the files or were made from one.
    /// </summary>
    public void NotifyChanged(IEnumerable<string> paths)
    {
        List<string> changed = [.. paths.Select(VfsPath.Normalize)];
        if (changed.Count == 0)
        {
            return;
        }

        var spellings = new HashSet<string>(changed, StringComparer.Ordinal);
        foreach (string path in changed)
        {
            if (Files.TryGetPhysicalPath(path) is { } physical)
            {
                spellings.Add(Path.GetFullPath(physical));
            }
        }

        var affected = new List<string>();
        lock (_lock)
        {
            _content.Refresh();
            foreach ((string module, IReadOnlyList<string> dependencies) in _loaded)
            {
                if (spellings.Contains(module) || dependencies.Any(d => spellings.Contains(d) || (Path.IsPathRooted(d) && spellings.Contains(Path.GetFullPath(d)))))
                {
                    affected.Add(module);
                }
            }
        }

        if (affected.Count > 0)
        {
            ModulesChanged?.Invoke(affected);
        }
    }

    /// <summary>Records (or, with null, clears) the error of the group of pipelines called <paramref name="group"/>.</summary>
    internal void SetError(string group, string? message)
    {
        lock (_lock)
        {
            if (message is null)
            {
                _errors.Remove(group);
            }
            else
            {
                _errors[group] = message;
            }
        }
    }

    private CompiledShader Remember(string module, CompiledShader shader)
    {
        lock (_lock)
        {
            _loaded[module] = shader.Dependencies;
        }

        return shader;
    }

    private ShaderCompiler CompilerFor(string variant, IReadOnlyDictionary<string, string>? defines, ShaderBackend backend)
    {
        lock (_lock)
        {
            string key = $"{(int)backend}:{variant}";
            if (!_compilers.TryGetValue(key, out ShaderCompiler? compiler))
            {
                var merged = new Dictionary<string, string>(_options.Defines);
                foreach (KeyValuePair<string, string> define in defines ?? new Dictionary<string, string>())
                {
                    merged[define.Key] = define.Value;
                }

                _compilers[key] = compiler = _createCompiler(new ShaderCompilerOptions
                {
                    SearchPaths = _options.SearchPaths,
                    Defines = merged,
                    Optimization = _options.Optimization,
                    DebugInfo = _options.DebugInfo,
                    CacheDirectory = _options.CacheDirectory,
                    Backend = backend,
                });
            }

            return compiler;
        }
    }
}
