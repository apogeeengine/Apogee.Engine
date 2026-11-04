using System;
using System.IO;
using System.Linq;
using Apogee.Reflection;
using Apogee.Scripting;
using Xunit;

namespace Apogee.Scripting.Tests;

/// <summary>The Markdown pages of the Luau API (<see cref="ReferenceWriter"/>), on the test zoo that <see cref="DefinitionTests"/> also uses.</summary>
public class ReferenceTests
{
    private static System.Collections.Generic.IReadOnlyDictionary<string, string> Write()
    {
        using var vm = new VmFixture();
        LuauVm.RegisterEcsNatives();
        return ReferenceWriter.Write(
            [.. TypeRegistry.Classes.Where(c => c.Name is "EngineObject" or "Animal" or "Dog")],
            [.. TypeRegistry.Enums.Where(e => e.Name is "Mood" or "Traits")],
            [.. TypeRegistry.Libraries.Where(l => l.Name == "zoo")],
            new DefinitionOptions { Globals = [new HostGlobal("pet", "Animal", "The pet of this run.")] });
    }

    [Fact]
    public void AClassPageListsItsMembersAndItsBase()
    {
        var pages = Write();

        string animal = pages["classes/Animal.md"];
        Assert.StartsWith("# Animal\n\nExtends `EngineObject`.\n\nAn animal of the test zoo.\n", animal);
        Assert.Contains("## Properties\n", animal);
        Assert.Contains("- `name: string` What it is called.\n", animal);
        Assert.Contains("- `hurt: Signal<number, string>`", animal);
        Assert.Contains("### speak\n\n```luau\nfunction speak(self, word: string?): string\n```\n", animal);

        Assert.Contains("Extends [Animal](Animal.md).", pages["classes/Dog.md"]);
    }

    [Fact]
    public void TheEnumsLibrariesAndIndexAreWritten()
    {
        var pages = Write();

        Assert.Contains("## Mood\n", pages["enums.md"]);
        Assert.Contains("# zoo\n", pages["libraries/zoo.md"]);

        string index = pages["reference.md"];
        Assert.Contains("- `pet`: [`Animal`](classes/Animal.md). The pet of this run.\n", index);
        Assert.Contains("- [Dog](classes/Dog.md)", index);
        Assert.Contains("- [zoo](libraries/zoo.md)", index);
        Assert.All(pages.Values, text => Assert.DoesNotContain('\r', text));
    }

    [Fact]
    public void WritingReplacesTheGeneratedFoldersAndNothingElse()
    {
        using var vm = new VmFixture();
        string folder = Directory.CreateTempSubdirectory("apogee-luau-docs").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "classes"));
            File.WriteAllText(Path.Combine(folder, "classes", "Stale.md"), "old");
            File.WriteAllText(Path.Combine(folder, "index.md"), "kept");

            ReferenceWriter.WriteTo(folder);

            Assert.False(File.Exists(Path.Combine(folder, "classes", "Stale.md")));
            Assert.Equal("kept", File.ReadAllText(Path.Combine(folder, "index.md")));
            Assert.True(File.Exists(Path.Combine(folder, "classes", "World.md")));
            Assert.Contains("# Luau API reference", File.ReadAllText(Path.Combine(folder, "reference.md")));
            Assert.Equal(ReferenceWriter.Write()["reference.md"], File.ReadAllText(Path.Combine(folder, "reference.md")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
