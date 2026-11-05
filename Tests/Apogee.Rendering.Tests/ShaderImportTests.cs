using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Content;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;
using Apogee.Graphics.Shaders;
using Apogee.Native.Tests;
using Apogee.Rendering.Shaders;
using Xunit;

namespace Apogee.Rendering.Tests;

/// <summary>Shaders compiled ahead of time and loaded from their artifacts. Needs the Slang library (to import) but no GPU.</summary>
public sealed class ShaderImportTests : IDisposable
{
    private const string Library = "float scale() { return 2.0; }\n";

    private const string Shader = """
        import library;
        RWStructuredBuffer<float> output;
        [shader("compute")] [numthreads(8, 1, 1)]
        void run(uint3 id : SV_DispatchThreadID)
        {
        #ifdef FAST
            output[id.x] = scale() * 4.0;
        #else
            output[id.x] = scale();
        #endif
        }
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("apogee-shader-import").FullName;
    private readonly VirtualFileSystem _files = new();

    public ShaderImportTests()
    {
        NativeRequirements.Require(typeof(Apogee.Native.Slang.Slang).Assembly, "slang_c");
        _files.Mount("res://", new DirectorySource(_root, writable: true));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static readonly string[] s_rendererModules = ["depth", "forward", "cluster_build", "light_cull", "tonemap"];

    private static readonly IReadOnlyList<IAssetImporter> s_importers = [new ShaderImporter()];

    private void Write(string path, string text)
    {
        string full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private ImportReport Import(bool force = false) => new AssetImporter(_files, s_importers).Run(force);

    [Fact]
    public void AShaderBecomesABundleWithItsImportedModuleAsADependency()
    {
        Write("shaders/library.slang", Library);
        Write("shaders/run.slang", Shader);

        ImportReport report = Import();

        Assert.True(report.Success);
        Assert.Equal(["shaders/run.slang"], report.Imported); // the library has no entry point: it is not an asset
        byte[] bundle = _files.ReadAllBytes("res://imported/shaders/run.slang.ashader");
        CompiledShader shader = ShaderBundle.Read(bundle);
        Assert.Equal(["run"], shader.EntryPoints);
        Assert.Equal(0x07230203u, BitConverter.ToUInt32(shader.Spirv("run"), 0));
        Assert.Contains("<Dependency path=\"shaders/library.slang\"/>", _files.ReadAllText("res://imported/index.xml"));

        // The same file holds the Metal code of the module.
        CompiledShader metal = ShaderBundle.Read(bundle, backend: ShaderBackend.Msl);
        Assert.Equal(["run"], metal.EntryPoints);
        Assert.Contains("[[kernel]]", metal.Msl("run"));
    }

    [Fact]
    public void AProjectReachedThroughASymbolicLinkStillGetsItsDependencies()
    {
        // The compiler may report the real path of the files it read (macOS temporary folders are behind a link); the index names them relative to the project.
        string parent = Directory.CreateTempSubdirectory("apogee-shader-link").FullName;
        string link = Path.Combine(parent, "project");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, _root);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Assert.Skip("Symbolic links cannot be made here.");
            }

            Write("shaders/library.slang", Library);
            Write("shaders/run.slang", Shader);
            var linked = new VirtualFileSystem();
            linked.Mount("res://", new DirectorySource(link, writable: true));

            Assert.True(new AssetImporter(linked, s_importers).Run().Success);

            Assert.Contains("<Dependency path=\"shaders/library.slang\"/>", linked.ReadAllText("res://imported/index.xml"));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link); // the link, not the project behind it
            }

            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void EditingAnImportedModuleImportsTheShadersThatUseItAgainAndNothingElse()
    {
        Write("shaders/library.slang", Library);
        Write("shaders/run.slang", Shader);
        Write("shaders/other.slang", "RWStructuredBuffer<float> o;\n[shader(\"compute\")] [numthreads(1,1,1)] void other() { o[0] = 1.0; }\n");
        Import();
        Assert.Equal(["shaders/other.slang", "shaders/run.slang"], Import().UpToDate);

        Write("shaders/library.slang", Library.Replace("2.0", "3.0", StringComparison.Ordinal));
        ImportReport report = Import();

        Assert.Equal(["shaders/run.slang"], report.Imported);
        Assert.Equal(["shaders/other.slang"], report.UpToDate);
    }

    [Fact]
    public void TheSidecarListsVariantsThatAreCompiledWithTheirDefines()
    {
        Write("shaders/library.slang", Library);
        Write("shaders/run.slang", Shader);
        Write("shaders/run.slang.import.xml", "<Import><Variant><Define name=\"FAST\"/></Variant></Import>");

        Assert.True(Import().Success);

        byte[] bundle = _files.ReadAllBytes("res://imported/shaders/run.slang.ashader");
        Assert.Equal(["", "FAST=1"], ShaderBundle.Variants(bundle));
        Assert.NotEqual(ShaderBundle.Read(bundle).Spirv("run"), ShaderBundle.Read(bundle, "FAST=1").Spirv("run"));
    }

    [Fact]
    public void AShaderThatDoesNotCompileIsAFailedImportWithTheCompilersMessage()
    {
        Write("shaders/broken.slang", "[shader(\"compute\")] [numthreads(1,1,1)]\nvoid main() { undefinedThing(); }\n");

        ImportReport report = Import();

        Assert.False(report.Success);
        Assert.Contains("broken.slang", report.Failed.Single().Message);
    }

    [Fact]
    public void TheLibraryLoadsTheArtifactAndNeverStartsTheCompiler()
    {
        Write("shaders/library.slang", Library);
        Write("shaders/run.slang", Shader);
        Write("shaders/run.slang.import.xml", "<Import><Variant><Define name=\"FAST\"/></Variant></Import>");
        Import();
        var library = new ShaderLibrary(_files, createCompiler: _ => throw new InvalidOperationException("the compiler was started"));

        CompiledShader plain = library.Load("res://shaders/run.slang");
        CompiledShader fast = library.Load("res://shaders/run.slang", new Dictionary<string, string> { ["FAST"] = "1" });

        Assert.Equal(["run"], plain.EntryPoints);
        Assert.NotEqual(plain.Spirv("run"), fast.Spirv("run"));
    }

    [Fact]
    public void WithoutAnArtifactTheSourceIsCompiledAndAVariantMissingFromTheBundleIsToo()
    {
        Write("shaders/library.slang", Library);
        Write("shaders/run.slang", Shader);
        var library = new ShaderLibrary(_files);

        Assert.False(library.Load("res://shaders/run.slang").FromCache);

        Import();
        CompiledShader fast = library.Load("res://shaders/run.slang", new Dictionary<string, string> { ["FAST"] = "1" }); // not in the bundle
        Assert.Equal(["run"], fast.EntryPoints);
    }

    [Fact]
    public void WithoutAnArtifactOrACompilerTheErrorSaysWhatToDo()
    {
        Write("shaders/library.slang", Library);
        Write("shaders/run.slang", Shader);
        var library = new ShaderLibrary(_files, canCompile: false); // as in a game shipped without libslang

        var error = Assert.Throws<ShaderCompileException>(() => library.Load("res://shaders/run.slang"));

        Assert.Contains("does not have the shader compiler", error.Message);
        Assert.Contains("--import", error.Message);
        Assert.Throws<ShaderCompileException>(() => library.Load("res://shaders/nothing.slang"));
    }

    [Fact]
    public void AnOutdatedArtifactWithoutItsSourceIsRefusedAsAskedToRebuild()
    {
        Write("shaders/library.slang", Library);
        Write("shaders/run.slang", Shader);
        Import();
        string artifact = Path.Combine(_root, "imported", "shaders", "run.slang.ashader");
        byte[] bundle = File.ReadAllBytes(artifact);
        BitConverter.GetBytes(ShaderAbi.Version + 1).CopyTo(bundle, 8);
        File.WriteAllBytes(artifact, bundle);
        File.Delete(Path.Combine(_root, "shaders", "run.slang")); // a shipped game has no sources

        var error = Assert.Throws<ShaderCompileException>(() => new ShaderLibrary(_files).Load("res://shaders/run.slang"));

        Assert.Contains($"ABI {ShaderAbi.Version + 1}", error.Message);
    }

    [Fact]
    public void AModsOwnArtifactsAreUsedFromItsFolder()
    {
        Write("run.slang", Shader.Replace("import library;", Library, StringComparison.Ordinal));
        Assert.True(Import().Success);
        File.Delete(Path.Combine(_root, "run.slang"));
        var game = new VirtualFileSystem();
        game.Mount("mod://cool", new DirectorySource(_root));
        var library = new ShaderLibrary(game, createCompiler: _ => throw new InvalidOperationException("the compiler was started"));

        Assert.Equal(["run"], library.Load("mod://cool/run.slang").EntryPoints);
    }

    [Fact]
    public void TheEnginesOwnShadersImportAndEveryRendererModuleLoadsFromItsArtifact()
    {
        string shaders = Path.Combine(AppContext.BaseDirectory, "Shaders");
        foreach (string file in Directory.GetFiles(shaders, "*.slang"))
        {
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
        }

        ImportReport report = Import();

        Assert.True(report.Success, string.Join("\n", report.Failed.Select(f => f.Message)));
        Assert.DoesNotContain("common.slang", report.Imported);
        var library = new ShaderLibrary(_files, createCompiler: _ => throw new InvalidOperationException("the compiler was started"));
        foreach (string module in s_rendererModules.Where(m => File.Exists(Path.Combine(_root, m + ".slang"))))
        {
            Assert.NotEmpty(library.Load($"res://{module}.slang").EntryPoints);
        }

        string forward = _files.ReadAllText("res://imported/index.xml");
        Assert.Contains("common.slang", forward);
    }
}
