using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Apogee.Content.FileSystem;
using Apogee.Scripting;
using Xunit;

namespace Apogee.Scripting.Tests;

/// <summary>Scripts that live in the content file system: <c>res://</c> folders, mods, zips.</summary>
public sealed class ScriptsInTheFileSystem : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apogee-vfs-scripts-" + Guid.NewGuid().ToString("N"));

    public ScriptsInTheFileSystem(Func<string, ScriptOwner?>? ownerOfModule = null)
    {
        Directory.CreateDirectory(_root);
        Files = new VirtualFileSystem();
        Fixture = new VmFixture(ownerOfModule: ownerOfModule, files: new VfsScriptFiles(Files));
    }

    public VirtualFileSystem Files { get; }

    public VmFixture Fixture { get; }

    /// <summary>Writes the files into a folder and mounts it at <paramref name="prefix"/>; the VM may load scripts from the prefix.</summary>
    public void Mount(string prefix, params (string Path, string Source)[] files)
    {
        string folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        foreach ((string path, string source) in files)
        {
            string full = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, source);
        }

        Directory.CreateDirectory(folder);
        Files.Mount(prefix, new DirectorySource(folder));
        Fixture.Vm.AddRoot(prefix);
    }

    public IReadOnlyList<Apogee.Reflection.Value> Run(string path, ScriptOwner? owner = null) =>
        Fixture.Vm.Execute(owner ?? Fixture.Vm.BaseOwner, "@" + path, Files.ReadAllText(path));

    public void Dispose()
    {
        Fixture.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not a failure.
        }
    }
}

public class VfsModuleTests
{
    [Fact]
    public void RequiresResolveAgainstVirtualPaths()
    {
        using var scripts = new ScriptsInTheFileSystem();
        scripts.Mount(
            "res://",
            ("main.luau", "return require('./a/one') + require('../shared')"),
            ("a/one.luau", "return require('./two') + 1"),
            ("a/two.luau", "return 10"),
            ("shared.luau", "return 5"));

        Assert.Equal(16.0, scripts.Run("res://main.luau")[0].ToDouble());
    }

    [Fact]
    public void ADirectoryWithAnInitFileIsAModuleAndAmbiguityIsStillAnError()
    {
        using var scripts = new ScriptsInTheFileSystem();
        scripts.Mount(
            "res://",
            ("main.luau", "return require('./pkg')"),
            ("pkg/init.luau", "return 'from init'"),
            ("both.luau", "return require('./twice')"),
            ("twice.luau", "return 1"),
            ("twice/init.luau", "return 2"));

        Assert.Equal("from init", scripts.Run("res://main.luau")[0].ToStringValue());
        Assert.Contains("ambiguous", Assert.Throws<ScriptException>(() => scripts.Run("res://both.luau")).Message);
    }

    [Fact]
    public void AliasesComeFromALuaurcInTheFileSystem()
    {
        using var scripts = new ScriptsInTheFileSystem();
        scripts.Mount(
            "res://",
            (".luaurc", "{ // comments are fine\n \"aliases\": { \"lib\": \"./library\" } }"),
            ("main.luau", "return require('@lib/tools')"),
            ("library/tools.luau", "return 'tools'"));

        Assert.Equal("tools", scripts.Run("res://main.luau")[0].ToStringValue());
    }

    [Fact]
    public void ADotDotThatClimbsHighStopsAtTheRootOfTheScheme()
    {
        using var scripts = new ScriptsInTheFileSystem();
        scripts.Mount("res://", ("main.luau", "return require('../../../../outside')"), ("outside.luau", "return 'inside the root'"));

        // Four levels up from res:// is still res://: it never reaches the real file system around the mount.
        Assert.Equal("inside the root", scripts.Run("res://main.luau")[0].ToStringValue());
    }

    [Fact]
    public void ARequireCannotLeaveTheFoldersTheVmWasGiven()
    {
        using var scripts = new ScriptsInTheFileSystem();
        scripts.Mount("res://game", ("main.luau", "return require('../mods/thing')"));
        scripts.Files.Mount("res://mods", new DirectorySource(Path.GetTempPath()));

        // res://mods is mounted but is not a root of the VM.
        Assert.Contains("outside the folders", Assert.Throws<ScriptException>(() => scripts.Run("res://game/main.luau")).Message);
    }

    [Fact]
    public void AMissingModuleIsAScriptErrorThatNamesTheVirtualPath()
    {
        using var scripts = new ScriptsInTheFileSystem();
        scripts.Mount("res://", ("main.luau", "return require('./nothing')"));

        string message = Assert.Throws<ScriptException>(() => scripts.Run("res://main.luau")).Message;

        Assert.Contains("module not found", message);
        Assert.Contains("res://nothing.luau", message);
    }

    [Fact]
    public void ARuntimeErrorNamesTheVirtualFile()
    {
        using var scripts = new ScriptsInTheFileSystem();
        scripts.Mount("res://", ("main.luau", "local x = 1\nerror('broken')"));

        string message = Assert.Throws<ScriptException>(() => scripts.Run("res://main.luau")).Message;

        Assert.Contains("res://main.luau:2", message);
    }

    [Fact]
    public void ModulesInAModGetTheOwnerTheHookPicksFromTheirPath()
    {
        var seen = new List<string>();
        ScriptOwner? picked = null;
        using var scripts = new ScriptsInTheFileSystem(path =>
        {
            seen.Add(path);
            return path.StartsWith("mod://cool/", StringComparison.Ordinal) ? picked : null;
        });
        picked = scripts.Fixture.Vm.CreateOwner("cool", isMod: true);
        scripts.Mount("mod://cool", ("main.luau", "return require('./helper')"), ("helper.luau", "return 7"));

        Assert.Equal(7.0, scripts.Run("mod://cool/main.luau", picked)[0].ToDouble());
        Assert.Contains("mod://cool/helper.luau", seen);
    }

    [Fact]
    public void AZipOfScriptsIsAModRoot()
    {
        using var scripts = new ScriptsInTheFileSystem();
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string source) in new[] { ("main.luau", "return require('./util') * 2"), ("util.luau", "return 21") })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(source);
            }
        }

        stream.Position = 0;
        using var zip = new ZipSource(stream, "cool.zip");
        scripts.Files.Mount("mod://cool", zip);
        scripts.Fixture.Vm.AddRoot("mod://cool");

        Assert.Equal(42.0, scripts.Run("mod://cool/main.luau")[0].ToDouble());
    }

    [Fact]
    public void ThePlainFileSystemStillWorksWhereTheVmWasGivenRealPaths()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("main.luau", "return require('./lib/../sub/x')");
        folder.Write("sub/x.luau", "return 3");

        Assert.Equal(3.0, folder.RunMain()[0].ToDouble());
    }
}
