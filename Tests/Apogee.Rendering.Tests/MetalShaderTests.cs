using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Apogee.Graphics.Shaders;
using Apogee.Native.Tests;
using Xunit;

namespace Apogee.Rendering.Tests;

/// <summary>
/// The engine's shaders for Metal. The compile needs the Slang library but no GPU; Apple's compiler (the second test) needs a Mac with Xcode, which the macOS CI runner has.
/// The Metal backend relies on the conventions checked here: the push constants are the buffer at index 0 and the bindless block (an argument buffer) the buffer at index 1.
/// </summary>
public sealed partial class MetalShaderTests
{
    private static readonly string s_root = FindRoot();

    private static string FindRoot()
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "Apogee.slnx")))
            {
                return directory;
            }
        }

        throw new InvalidOperationException("Apogee.slnx not found above the test output.");
    }

    /// <summary>Every module of the engine that has entry points: the renderer's, and the dev overlay's.</summary>
    private static IEnumerable<string> EngineShaders() =>
    [
        .. Directory.GetFiles(Path.Combine(s_root, "Source", "Apogee.Rendering", "Shaders"), "*.slang"),
        .. Directory.GetFiles(Path.Combine(s_root, "Source", "Apogee.DevTools", "Shaders"), "*.slang"),
    ];

    private static IEnumerable<(string Module, string Entry, string Source)> Compile()
    {
        NativeRequirements.Require(typeof(Apogee.Native.Slang.Slang).Assembly, "slang_c");
        var compiler = new ShaderCompiler(new ShaderCompilerOptions { Backend = ShaderBackend.Msl, SearchPaths = [Path.Combine(s_root, "Source", "Apogee.Rendering", "Shaders")] });
        foreach (string path in EngineShaders().Where(p => File.ReadAllText(p).Contains("[shader(", StringComparison.Ordinal)))
        {
            CompiledShader shader = compiler.CompileFile(path);
            foreach (string entry in shader.EntryPoints)
            {
                yield return (Path.GetFileNameWithoutExtension(path), entry, shader.Msl(entry));
            }
        }
    }

    [GeneratedRegex(@"(\w+) constant\* (\w+) \[\[buffer\((\d+)\)\]\]")]
    private static partial Regex ConstantBuffers();

    [GeneratedRegex(@"\[\[buffer\((\d+)\)\]\]")]
    private static partial Regex AnyBuffer();

    [Fact]
    public void EveryEngineShaderCompilesToMetalWithTheBuffersWhereTheBackendExpectsThem()
    {
        var entries = Compile().ToList();

        Assert.True(entries.Count >= 10, $"only {entries.Count} entry points");
        int usingBindless = 0;
        foreach ((string module, string entry, string source) in entries)
        {
            string where = $"{module}.{entry}";
            // Nothing else is bound: no direct texture or sampler slots, no other buffers.
            Assert.All(AnyBuffer().Matches(source).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)), index => Assert.InRange(index, 0, 1));
            Assert.DoesNotContain("[[texture(", source);
            Assert.DoesNotContain("[[sampler(", source);
            foreach (Match match in ConstantBuffers().Matches(source))
            {
                string name = match.Groups[2].Value;
                int index = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(index == 0 ? name.StartsWith("push", StringComparison.Ordinal) : name.StartsWith("gBindless", StringComparison.Ordinal), $"{where}: {name} is buffer({index})");
                usingBindless += index == 1 ? 1 : 0;
            }
        }

        Assert.True(usingBindless >= 4, "the fragment shaders that sample textures read the bindless block");
    }

    [Fact]
    public void AppleCompilesEveryEngineShaderToMetal()
    {
        string? problem = null;
        try
        {
            using Process probe = Process.Start(new ProcessStartInfo("xcrun", "-sdk macosx --find metal") { RedirectStandardOutput = true, RedirectStandardError = true })!;
            probe.StandardOutput.ReadToEnd();
            probe.WaitForExit();
            problem = probe.ExitCode == 0 ? null : "xcrun cannot find the Metal compiler (Xcode's command line tools).";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            problem = "xcrun is not available (this is not a Mac).";
        }

        if (problem is not null)
        {
            if (Environment.GetEnvironmentVariable("APOGEE_REQUIRE_METAL_COMPILER") == "1")
            {
                Assert.Fail(problem);
            }

            Assert.Skip(problem);
        }

        string directory = Directory.CreateTempSubdirectory("apogee-msl").FullName;
        try
        {
            var failures = new List<string>();
            foreach ((string module, string entry, string source) in Compile())
            {
                string file = Path.Combine(directory, module + "." + entry + ".metal");
                File.WriteAllText(file, source);
                using Process compile = Process.Start(new ProcessStartInfo("xcrun", $"-sdk macosx metal -c \"{file}\" -o \"{file}.air\"") { RedirectStandardOutput = true, RedirectStandardError = true })!;
                string output = compile.StandardError.ReadToEnd() + compile.StandardOutput.ReadToEnd();
                compile.WaitForExit();
                if (compile.ExitCode != 0)
                {
                    failures.Add($"{module}.{entry}:\n{output}");
                }
            }

            Assert.True(failures.Count == 0, "Apple's Metal compiler rejects:\n" + string.Join("\n", failures));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
