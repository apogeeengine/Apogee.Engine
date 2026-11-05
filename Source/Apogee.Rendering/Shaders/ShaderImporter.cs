using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Content;
using Apogee.Content.Import;
using Apogee.Graphics.Shaders;

namespace Apogee.Rendering.Shaders;

/// <summary>
/// Slang modules into <see cref="ShaderBundle"/>s (<c>.ashader</c>): each module with entry points is compiled to SPIR-V (Vulkan) and to Metal shading language (Metal), with its reflection, once without defines and once for each
/// <c>&lt;Variant&gt;</c> its <c>.import.xml</c> lists. The files the compiler read (<c>import</c>, <c>#include</c>) are the artifact's dependencies, so editing a library re-imports every shader
/// that uses it, and the library is not imported on its own. A module with no <c>[shader(…)]</c> entry point is not an asset: it only serves the others.
/// </summary>
public sealed class ShaderImporter : IAssetImporter
{
    private readonly int _optimization;

    public ShaderImporter(int optimization = 1)
    {
        _optimization = optimization;
    }

    public string Name => "shader";

    public IReadOnlyList<string> Extensions { get; } = [".slang"];

    /// <summary>Slang's version, the optimization level and the ABI the code is for: a new compiler or a new engine layout imports again.</summary>
    public string Fingerprint => $"slang {ShaderCompiler.CompilerVersion}; abi {ShaderAbi.Version}; opt {_optimization}";

    public ImportResult? Import(ImportContext context)
    {
        string text = System.Text.Encoding.UTF8.GetString(context.Bytes);
        if (!text.Contains("[shader(", StringComparison.Ordinal))
        {
            return null;
        }

        if (context.PhysicalPath(context.SourcePath) is not { } physical || context.PhysicalRoot is not { } root)
        {
            throw new ContentException($"Shader '{context.Source}' is not a file in a folder: the compiler reads its imports from disk. Import the folder.");
        }

        var variants = new List<(string, IReadOnlyList<CompiledShader>)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<IReadOnlyDictionary<string, string>> defineSets =
            [new Dictionary<string, string>(), .. context.Settings.Variants.Select(v => v.Defines)];
        foreach (IReadOnlyDictionary<string, string> defines in defineSets)
        {
            string key = ShaderBundle.VariantKey(defines);
            if (!seen.Add(key))
            {
                continue;
            }

            // Every backend: a shader that does not compile for one of them is an error of the shader, found here and not on the machine of someone who has a Mac.
            var backends = new List<CompiledShader>();
            foreach (ShaderBackend backend in new[] { ShaderBackend.Spirv, ShaderBackend.Msl })
            {
                var compiler = new ShaderCompiler(new ShaderCompilerOptions { SearchPaths = [root], Defines = defines, Optimization = _optimization, Backend = backend });
                try
                {
                    backends.Add(compiler.CompileFile(physical));
                }
                catch (ShaderCompileException e)
                {
                    throw new ContentException(backend == ShaderBackend.Spirv ? e.Message : $"{e.Message} (compiling for {backend})");
                }
            }

            variants.Add((key, backends));
            foreach (string dependency in backends.SelectMany(b => b.Dependencies))
            {
                dependencies.Add(dependency);
            }
        }

        foreach (string dependency in dependencies)
        {
            if (RelativeToProject(root, dependency) is { } relative)
            {
                context.Depend("res://" + relative);
            }
        }

        return new ImportResult(ShaderBundle.Write(Path.GetFileNameWithoutExtension(context.Source), variants), ShaderBundle.Extension);
    }

    /// <summary>
    /// The path of a file the compiler read, relative to the project (the index names files that way), or null when it is not part of the project. The compiler may name a file through
    /// another route to the same place (macOS temporary folders are behind a symbolic link), so a path that is not under the root as written is looked for by its longest tail that
    /// exists in the project with the same contents.
    /// </summary>
    private static string? RelativeToProject(string root, string file)
    {
        string relative = Path.GetRelativePath(root, file);
        if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
        {
            return relative.Replace(Path.DirectorySeparatorChar, '/');
        }

        string[] segments = file.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            string tail = string.Join(Path.DirectorySeparatorChar, segments[i..]);
            string candidate = root + Path.DirectorySeparatorChar + tail;
            if (File.Exists(candidate) && File.Exists(file) && File.ReadAllBytes(candidate).AsSpan().SequenceEqual(File.ReadAllBytes(file)))
            {
                return string.Join('/', segments[i..]);
            }
        }

        return null;
    }
}
