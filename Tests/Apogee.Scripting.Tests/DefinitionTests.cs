using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Apogee.Reflection;
using Apogee.Scripting;
using Xunit;

namespace Apogee.Scripting.Tests;

public class DefinitionTests
{
    /// <summary>Makes a VM once, which registers the engine assemblies, so the registry and the natives are complete.</summary>
    private static void Prepare()
    {
        using var vm = new VmFixture();
        LuauVm.RegisterEcsNatives();
    }

    private static string Write(string[] classes, string[] enums, string[] libraries)
    {
        Prepare();
        return DefinitionWriter.Write(
            [.. TypeRegistry.Classes.Where(c => classes.Contains(c.Name))],
            [.. TypeRegistry.Enums.Where(e => enums.Contains(e.Name))],
            [.. TypeRegistry.Libraries.Where(l => libraries.Contains(l.Name))]);
    }

    [Fact]
    public void AClassIsDeclaredWithItsMembers()
    {
        string text = Write(["EngineObject", "Animal", "Dog"], ["Mood", "Traits"], ["zoo"]);

        const string Animal = """
            --- An animal of the test zoo.
            declare extern type Animal extends EngineObject with
                --- What it is called.
                name: string
                speed: number
                legs: number
                position: Vector3
                mood: Mood
                traits: { Traits }
                friend: Animal?
                tags: { string }
                id: number
                hurt: Signal<number, string>
                died: Signal<()>
                function speak(self, word: string?): string
                function pair(self): (number, string)
                function move(self, by: Vector3): Vector3
                function sum(self, numbers: { number }): number
                function describe(self, mood: Mood, traits: { Traits }): string
                function wound(self, amount: number): ()
                function kill(self): ()
                function keep(self): ()
                function call(self, callback: (...any) -> ...any): ()
                function apply(self, callback: (...any) -> ...any): number
            end
            """;
        Assert.Contains(Animal.ReplaceLineEndings("\n"), text.ReplaceLineEndings("\n"));
        Assert.Contains("declare extern type Dog extends Animal with\n    barks: boolean\nend", text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void EnumsAreUnionsOfStrings()
    {
        string text = Write(["EngineObject"], ["Mood", "Traits"], []).ReplaceLineEndings("\n");

        Assert.Contains("type Mood = \"calm\" | \"angry\" | \"very_happy\"\n", text);
        Assert.Contains("type Traits = \"none\" | \"fast\" | \"strong\" | \"tough_skin\"\n", text);
    }

    [Fact]
    public void GlobalsAreDeclaredForStaticMethodsCreatableClassesAndLibraries()
    {
        string text = Write(["EngineObject", "Animal", "Dog"], ["Traits"], ["zoo"]).ReplaceLineEndings("\n");

        Assert.Contains("declare Animal: { new: () -> Animal, make: (name: string) -> Animal }\n", text);
        Assert.DoesNotContain("declare Dog:", text); // not creatable, no static methods
        Assert.Contains("declare zoo: {\n    count: () -> number,\n    find: (name: string) -> Animal?,\n    groom: (traits: ({ Traits } | {})?) -> number,\n}\n", text);
        Assert.Contains("declare function Vector3(x: number?, y: number?, z: number?): vector\n", text);
    }

    [Fact]
    public void AnOptionalListOfFlagsTakesATableLiteralInBothTypeSolvers()
    {
        // luau-lsp's default solver refuses { "fast" } against { Traits }? (it infers { string }), so the optional form is a union with an empty table.
        string text = Write(["EngineObject", "Animal"], ["Mood", "Traits"], ["zoo"]);

        Assert.Contains("groom: (traits: ({ Traits } | {})?) -> number", text);
        Assert.Contains("describe(self, mood: Mood, traits: { Traits }): string", text); // required: checked as it is
    }

    [Fact]
    public void TheHostGlobalsAreDeclaredAndHiddenClassesAreLeftOut()
    {
        Prepare();
        string text = DefinitionWriter.Write(
            [.. TypeRegistry.Classes.Where(c => c.Name is "EngineObject" or "Animal" or "Dog")],
            [],
            [],
            new DefinitionOptions { Globals = [new HostGlobal("pet", "Animal", "The pet of this run.")], HiddenClasses = ["Dog"] }).ReplaceLineEndings("\n");

        Assert.Contains("--- The pet of this run.\ndeclare pet: Animal\n", text);
        Assert.DoesNotContain("type Dog", text);
        Assert.Contains("declare extern type Animal extends EngineObject with", text);
    }

    [Fact]
    public void ClassesNotForScriptsAreLeftOut()
    {
        string text = Write(["EngineObject", "Internal", "Holder"], [], []);

        Assert.DoesNotContain("type Internal", text);
        Assert.Contains("hidden: EngineObject?", text); // a property of a class scripts cannot see is a plain object
    }

    [Fact]
    public void TheNativeMethodsOfWorldComeWithTheirDeclarations()
    {
        string text = Write(["EngineObject", "World"], [], []);

        Assert.Contains("declare extern type World extends EngineObject with", text);
        // A union with an empty table, because luau-lsp's default solver refuses the literal { angle = "f32" } against { [string]: FieldType }?.
        Assert.Contains("function component(self, name: string, fields: ({ [string]: FieldType } | {})?): number", text);
        Assert.Contains("function each(self, components: { Term }, callback: (entity: number, ...ComponentView) -> ()): ()", text);
        Assert.Contains("function create_entity(self, name: string?, parent: Entity?): Entity", text);
    }

    [Fact]
    public void EveryNativeMethodHasAMatchingDeclarationAndBody()
    {
        Prepare();

        IReadOnlyList<ScriptNatives.NativeMethod> natives = ScriptNatives.Of("World");
        Assert.NotEmpty(natives);
        foreach (ScriptNatives.NativeMethod native in natives)
        {
            Assert.NotEqual(0, native.Body);
            Assert.Matches($@"^function {Regex.Escape(native.Name)}\(self[,)]", native.Declaration);
        }

        // And every declaration in the output belongs to a native or to a described member: the writer adds nothing of its own to a class.
        string text = DefinitionWriter.Write();
        foreach (ScriptNatives.NativeMethod native in natives)
        {
            Assert.Contains("    " + native.Declaration, text);
        }
    }

    [Fact]
    public void TheOutputIsTheSameEveryTime()
    {
        Prepare();
        Assert.Equal(DefinitionWriter.Write(), DefinitionWriter.Write());
    }
}
