using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Apogee.Reflection;
using Apogee.Reflection.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Apogee.Reflection.Tests;

/// <summary>Compiles a snippet with the generator, as the build does, so a test can look at the generated text, the diagnostics and the running result.</summary>
internal sealed class GeneratedCode
{
    private static readonly MetadataReference[] s_references = BuildReferences();

    private GeneratedCode(Compilation compilation, ImmutableArray<Diagnostic> diagnostics, ImmutableArray<SyntaxTree> generated)
    {
        Compilation = compilation;
        Diagnostics = diagnostics;
        Generated = generated;
    }

    public Compilation Compilation { get; }

    /// <summary>The generator's diagnostics.</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public ImmutableArray<SyntaxTree> Generated { get; }

    public string GeneratedText => string.Join("\n", Generated.Select(t => t.ToString()));

    private static MetadataReference[] BuildReferences()
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).ToList();
        paths.Add(typeof(Value).Assembly.Location);
        paths.Add(typeof(Apogee.Core.Logging.Log).Assembly.Location);
        return [.. paths.Distinct().Select(p => MetadataReference.CreateFromFile(p))];
    }

    public static GeneratedCode Run(string source, string assemblyName)
    {
        CSharpParseOptions parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, parse)],
            s_references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create([new ReflectionGenerator().AsSourceGenerator()], parseOptions: parse);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation updated, out ImmutableArray<Diagnostic> diagnostics);
        return new GeneratedCode(updated, diagnostics, driver.GetRunResult().GeneratedTrees);
    }

    /// <summary>The errors of the compilation including the generated code.</summary>
    public string[] CompileErrors() =>
        [.. Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())];

    /// <summary>Compiles and loads the snippet; its module initializer has registered its types when this returns.</summary>
    public Assembly Load()
    {
        string[] errors = CompileErrors();
        Assert.True(errors.Length == 0, "The generated code does not compile:\n" + string.Join("\n", errors) + "\n\n" + GeneratedText);

        using var stream = new MemoryStream();
        Assert.True(Compilation.Emit(stream).Success);
        Assembly assembly = Assembly.Load(stream.ToArray());
        RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        return assembly;
    }

    /// <summary>Compiles, loads and returns the snippet's classes by their C# names.</summary>
    public static Assembly Build(string source, string assemblyName) => Run(source, assemblyName).Load();
}
