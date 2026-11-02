using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Apogee.Reflection.Generator;

/// <summary>
/// Writes the registry of an assembly from its <c>[ApogeeClass]</c> classes, <c>[Component]</c> structs and <c>[ScriptLibrary]</c>
/// classes: each class gets a <c>partial</c> half with its <c>ClassInfo</c>, and a module initializer registers everything.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ReflectionGenerator : IIncrementalGenerator
{
    private const string Ns = "Apogee.Reflection.";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<Described>> classes = context.SyntaxProvider
            .ForAttributeWithMetadataName(Ns + "ApogeeClassAttribute", static (node, _) => node is ClassDeclarationSyntax, static (ctx, ct) => Analyzer.Class(ctx, ct))
            .Collect();
        IncrementalValueProvider<ImmutableArray<Described>> components = context.SyntaxProvider
            .ForAttributeWithMetadataName(Ns + "ComponentAttribute", static (node, _) => node is StructDeclarationSyntax, static (ctx, ct) => Analyzer.Component(ctx, ct))
            .Collect();
        IncrementalValueProvider<ImmutableArray<Described>> libraries = context.SyntaxProvider
            .ForAttributeWithMetadataName(Ns + "ScriptLibraryAttribute", static (node, _) => node is ClassDeclarationSyntax, static (ctx, ct) => Analyzer.Library(ctx, ct))
            .Collect();

        var input = classes.Combine(components).Combine(libraries).Combine(context.CompilationProvider);
        context.RegisterSourceOutput(input, static (production, data) =>
        {
            var all = new System.Collections.Generic.List<Described>();
            all.AddRange(data.Left.Left.Left);
            all.AddRange(data.Left.Left.Right);
            all.AddRange(data.Left.Right);
            Emitter.Emit(production, all, data.Right.AssemblyName ?? "Assembly");
        });
    }
}
