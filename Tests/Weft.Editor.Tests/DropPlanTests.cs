using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Weft.Editor.Assets;
using Xunit;

namespace Weft.Editor.Tests;

/// <summary>What a drop will do, decided before anything is copied: where files go, what is refused, what is never overwritten. Real files in a temp folder on the source side, a list of paths on the project's.</summary>
public sealed class DropPlanTests
{
    private static readonly string[] s_importable = [".glb", ".gltf", ".png", ".jpg", ".jpeg", ".slang"];

    private static DropPlan Plan(DropSource source, FakeTargets targets, string folder, ClashPolicy policy, params string[] relative) =>
        DropPlan.Make([.. relative.Select(source.FullPath)], folder, targets, s_importable, policy);

    [Fact]
    public void FilesLandInTheTargetFolderAndClashesAreNumbered()
    {
        using var source = new DropSource();
        source.Write("a.png", TestAssets.Png());
        source.Write("a.png.import.xml", "<Import uid=\"uid://abc\"/>");
        source.Write("b.glb", TestAssets.Glb());
        var targets = new FakeTargets(files: ["res://images/a.png", "res://images/a.png.import.xml", "res://images/a 2.png"], folders: ["res://images"]);

        DropPlan plan = Plan(source, targets, "res://images", ClashPolicy.KeepBoth, "a.png", "b.glb");

        Assert.True(plan.HasClashes);
        Assert.Equal(["res://images/a.png"], plan.Clashes);
        Assert.Equal(
            ["res://images/a 3.png", "res://images/a 3.png.import.xml", "res://images/b.glb"],
            plan.Files.Select(f => f.Target).Order(StringComparer.Ordinal));                      // "a 2.png" is taken: the next free number, and its settings follow the name
        Assert.All(plan.Files, f => Assert.False(f.Replaces));
        Assert.Equal(["res://images/a 3.png", "res://images/b.glb"], plan.Importable.Select(f => f.Target).Order(StringComparer.Ordinal));
        Assert.Empty(plan.Refused);
    }

    [Fact]
    public void ReplaceOverwritesOnlyWhenChosenAndKeepsTheProjectsOwnSettings()
    {
        using var source = new DropSource();
        source.Write("a.png", TestAssets.Png(10));
        source.Write("a.png.import.xml", "<Import uid=\"uid://incoming\"/>");
        var targets = new FakeTargets(files: ["res://images/a.png", "res://images/a.png.import.xml"], folders: ["res://images"]);

        DropPlan keep = Plan(source, targets, "res://images", ClashPolicy.KeepBoth, "a.png");
        DropPlan replace = Plan(source, targets, "res://images", ClashPolicy.Replace, "a.png");
        DropPlan skip = Plan(source, targets, "res://images", ClashPolicy.Skip, "a.png");

        Assert.All(keep.Files, f => Assert.False(f.Replaces));                                    // the default never overwrites
        PlannedFile only = Assert.Single(replace.Files);                                          // the incoming settings are dropped: the asset keeps the UID the project has for it
        Assert.Equal("res://images/a.png", only.Target);
        Assert.True(only.Replaces);
        Assert.Empty(skip.Files);
        Assert.Equal(["res://images/a.png"], skip.Skipped);
    }

    [Fact]
    public void ThingsThatAreNotFilesToReplaceAreNeverReplaced()
    {
        using var source = new DropSource();
        source.Write("a.png", TestAssets.Png());
        var targets = new FakeTargets(folders: ["res://images", "res://images/a.png"]);          // a folder has the name

        DropPlan plan = Plan(source, targets, "res://images", ClashPolicy.Replace, "a.png");

        Assert.Equal("res://images/a 2.png", Assert.Single(plan.Files).Target);
        Assert.False(plan.Files[0].Replaces);
    }

    [Fact]
    public void TwoDroppedFilesWithTheSameNameBothArrive()
    {
        using var source = new DropSource();
        source.Write("one/a.png", TestAssets.Png(1));
        source.Write("two/a.png", TestAssets.Png(2));

        DropPlan plan = Plan(source, new FakeTargets(), "res://", ClashPolicy.Replace, "one/a.png", "two/a.png");

        Assert.Equal(["res://a 2.png", "res://a.png"], plan.Files.Select(f => f.Target).Order(StringComparer.Ordinal));
        Assert.False(plan.HasClashes);                                                            // nothing in the project was met
    }

    [Fact]
    public void AGltfBringsItsBuffersAndImages()
    {
        using var source = new DropSource();
        source.Write("car/car.gltf", """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":36,"uri":"car%20data.bin"},{"byteLength":4,"uri":"data:application/octet-stream;base64,AAAAAA=="}],
             "images":[{"uri":"tex/body.png"},{"uri":"tex/body.png"}]}
            """);
        source.Write("car/car data.bin", TestAssets.TriangleBuffer());
        source.Write("car/tex/body.png", TestAssets.Png());
        source.Write("car/tex/body.png.import.xml", "<Import/>");
        source.Write("car/car.gltf.import.xml", "<Import uid=\"uid://car\"/>");

        DropPlan plan = Plan(source, new FakeTargets(folders: ["res://models"]), "res://models", ClashPolicy.KeepBoth, "car/car.gltf");

        Assert.Empty(plan.Refused);
        Assert.Equal(
            ["res://models/car data.bin", "res://models/car.gltf", "res://models/car.gltf.import.xml", "res://models/tex/body.png"],
            plan.Files.Select(f => f.Target).Order(StringComparer.Ordinal));                     // the texture's own settings are not used by the model: not copied
        Assert.Equal(["res://models/car.gltf"], plan.Importable.Select(f => f.Target));          // the buffer and the image come with the model; they are not imported on their own
        Assert.All(plan.Files.Where(f => !f.Target.EndsWith("car.gltf", StringComparison.Ordinal)), f => Assert.True(f.IsCompanion));
    }

    [Fact]
    public void AGltfWhoseFilesClashMovesIntoANewFolderSoItsNamesStayValid()
    {
        using var source = new DropSource();
        source.Write("car.gltf", TestAssets.GltfWithBuffer("car.bin"));
        source.Write("car.bin", TestAssets.TriangleBuffer());
        var targets = new FakeTargets(files: ["res://models/car.bin"], folders: ["res://models"]);  // another model's buffer has the name

        DropPlan plan = Plan(source, targets, "res://models", ClashPolicy.KeepBoth, "car.gltf");

        Assert.Equal(["res://models/car/car.bin", "res://models/car/car.gltf"], plan.Files.Select(f => f.Target).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("tex/../../x.bin")]
    [InlineData("%2e%2e/x.bin")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\x.bin")]
    [InlineData("C:/x.bin")]
    [InlineData("file:///x.bin")]
    [InlineData("a\\b.bin")]
    [InlineData("")]
    public void AGltfThatNamesAFileOutsideItsFolderIsRefusedWhole(string uri)
    {
        using var source = new DropSource();
        source.Write("m/m.gltf", TestAssets.GltfWithBuffer(uri.Replace("\\", "\\\\", StringComparison.Ordinal)));
        source.Write("outside.bin", TestAssets.TriangleBuffer());

        DropPlan plan = Plan(source, new FakeTargets(), "res://", ClashPolicy.KeepBoth, "m/m.gltf");

        Assert.Empty(plan.Files);
        RefusedFile refused = Assert.Single(plan.Refused);
        Assert.Contains("not a file beside it or below it", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AGltfThatNamesAMissingFileOrIsNotReadableIsRefused()
    {
        using var source = new DropSource();
        source.Write("missing.gltf", TestAssets.GltfWithBuffer("nothing.bin"));
        source.Write("broken.gltf", "this is not a model");

        DropPlan plan = Plan(source, new FakeTargets(), "res://", ClashPolicy.KeepBoth, "missing.gltf", "broken.gltf");

        Assert.Empty(plan.Files);
        Assert.Equal(2, plan.Refused.Count);
        Assert.Contains(plan.Refused, r => r.Reason.Contains("missing", StringComparison.Ordinal));
        Assert.Contains(plan.Refused, r => r.Reason.Contains("not a readable glTF", StringComparison.Ordinal));
    }

    [Fact]
    public void FoldersRecurseWithinLimits()
    {
        using var source = new DropSource();
        source.Write("kit/a.png", TestAssets.Png());
        source.Write("kit/sub/deeper/b.glb", TestAssets.Glb());
        source.Write("kit/.git/config", "hidden");
        source.Write("kit/.hidden.png", TestAssets.Png());
        source.Write("kit/readme.txt", "not an asset");

        DropPlan plan = DropPlan.Make([source.FullPath("kit")], "res://", new FakeTargets(), s_importable);

        Assert.Equal(["res://kit/a.png", "res://kit/sub/deeper/b.glb"], plan.Files.Select(f => f.Target).Order(StringComparer.Ordinal));
        Assert.Single(plan.Refused);                                                              // readme.txt; hidden files and folders are skipped without a word
        Assert.EndsWith("readme.txt", plan.Refused[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderWithTooManyFilesStopsAtTheLimitAndSaysSoOnce()
    {
        using var source = new DropSource();
        Directory.CreateDirectory(source.FullPath("many/a"));
        for (int i = 0; i < DropPlan.MaxFiles + 5; i++)
        {
            File.WriteAllBytes(source.FullPath($"many/a/f{i:D5}.luau"), [1]);
        }

        source.Write("many/z1.luau", "x");                                                        // after the limit, in the folder above: not taken, and not another message
        source.Write("many/z2.luau", "x");

        DropPlan plan = DropPlan.Make([source.FullPath("many")], "res://", new FakeTargets(), s_importable);

        Assert.Equal(DropPlan.MaxFiles, plan.Files.Count);
        RefusedFile refused = Assert.Single(plan.Refused);
        Assert.Contains("more than", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderNestedTooDeepIsNotFollowedFurther()
    {
        using var source = new DropSource();
        string deep = string.Join('/', Enumerable.Repeat("d", DropPlan.MaxDepth + 2));
        source.Write("start.png", TestAssets.Png());
        source.Write(deep + "/x.png", TestAssets.Png());

        DropPlan plan = DropPlan.Make([source.FullPath("start.png"), source.FullPath("d")], "res://", new FakeTargets(), s_importable);

        Assert.Equal("res://start.png", Assert.Single(plan.Files).Target);
        Assert.Contains(plan.Refused, r => r.Reason.Contains("deeper than", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadOnlyRootsAndNonFoldersAreRefused()
    {
        using var source = new DropSource();
        string file = source.Write("a.png", TestAssets.Png());
        var targets = new FakeTargets(files: ["res://notafolder"]);

        DropPlan inMod = DropPlan.Make([file], "mod://core/textures", targets, s_importable);
        DropPlan inPack = DropPlan.Make([file], "res://packed", new FakeTargets { ReadOnlyPrefix = "res://packed" }, s_importable);
        DropPlan inFile = DropPlan.Make([file], "res://notafolder", targets, s_importable);
        DropPlan inShaders = DropPlan.Make([file], "shaders://", targets, s_importable);

        foreach (DropPlan plan in new[] { inMod, inPack, inFile, inShaders })
        {
            Assert.Empty(plan.Files);
            Assert.Single(plan.Refused);
        }

        Assert.Contains("read-only", inPack.Refused[0].Reason, StringComparison.Ordinal);
        Assert.Contains("res://", inMod.Refused[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatTheGameDoesNotUseOrCannotNameIsRefusedWithAReason()
    {
        using var source = new DropSource();
        string exe = source.Write("tool.exe", "MZ");
        string empty = source.Write("empty.png", []);
        string orphan = source.Write("x.png.import.xml", "<Import/>");
        string fine = source.Write("fine.png", TestAssets.Png());
        string missing = source.FullPath("gone.png");

        DropPlan plan = DropPlan.Make([exe, empty, orphan, fine, missing], "res://", new FakeTargets(), s_importable);

        Assert.Equal("res://fine.png", Assert.Single(plan.Files).Target);
        Assert.Contains(plan.Refused, r => r.Path == exe && r.Reason.Contains("does not use this kind", StringComparison.Ordinal));
        Assert.Contains(plan.Refused, r => r.Path == empty && r.Reason.Contains("empty", StringComparison.Ordinal));
        Assert.Contains(plan.Refused, r => r.Path == orphan && r.Reason.Contains("import settings", StringComparison.Ordinal));
        Assert.Contains(plan.Refused, r => r.Path == missing && r.Reason.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void ANameWithAColonIsRefused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A colon cannot be in a file name on Windows (it names a drive or a stream), so there is no such file to drop.");
        using var source = new DropSource();
        string file = source.Write("a:b.png", TestAssets.Png());

        DropPlan plan = DropPlan.Make([file], "res://", new FakeTargets(), s_importable);

        Assert.Empty(plan.Files);
        Assert.Contains("not allowed in names", Assert.Single(plan.Refused).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void LinksAreNotFollowed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");
        using var source = new DropSource();
        string real = source.Write("real.png", TestAssets.Png());
        string link = source.FullPath("link.png");
        File.CreateSymbolicLink(link, real);
        Directory.CreateDirectory(source.FullPath("folder"));
        source.Write("folder/in.png", TestAssets.Png());
        string folderLink = source.FullPath("folderlink");
        Directory.CreateSymbolicLink(folderLink, source.FullPath("folder"));
        source.Write("kit/a.png", TestAssets.Png());
        File.CreateSymbolicLink(source.FullPath("kit/b.png"), real);

        DropPlan plan = DropPlan.Make([link, folderLink, source.FullPath("kit")], "res://", new FakeTargets(), s_importable);

        Assert.Equal("res://kit/a.png", Assert.Single(plan.Files).Target);                       // the link inside the folder is not taken either
        Assert.Equal(3, plan.Refused.Count);
        Assert.All(plan.Refused, r => Assert.Contains("link", r.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void AFileOverTheSizeLimitIsRefused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A file this large is not sparse on Windows file systems: writing it would fill the disk.");
        using var source = new DropSource();
        string big = source.Write("big.png", [1]);
        using (var stream = new FileStream(big, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(DropPlan.MaxFileBytes + 1);
        }

        DropPlan plan = DropPlan.Make([big], "res://", new FakeTargets(), s_importable);

        Assert.Empty(plan.Files);
        Assert.Contains("larger than", Assert.Single(plan.Refused).Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a.bin", "a.bin")]
    [InlineData("tex/a%20b.png", "tex/a b.png")]
    [InlineData("caf%C3%A9.png", "café.png")]
    public void ARelativeUriIsReadAsAPath(string uri, string expected) => Assert.Equal(expected, DropPlan.RelativeUri(uri));

    [Fact]
    public void TheImportersFolderTakesNoFiles()
    {
        using var source = new DropSource();
        string image = source.Write("a.png", TestAssets.Png());
        string index = source.Write("imported/index.xml", "<Imports/>");
        source.Write("imported/assets.xml", "<Assets/>");
        var targets = new FakeTargets(files: ["res://imported/index.xml"], folders: ["res://imported"]);

        DropPlan intoIt = DropPlan.Make([image], "res://imported", targets, s_importable);
        DropPlan intoBelow = DropPlan.Make([image], "res://imported/models", targets, s_importable);
        DropPlan replacing = DropPlan.Make([index], "res://imported", targets, s_importable, ClashPolicy.Replace);
        DropPlan aFolderOfThatName = DropPlan.Make([source.FullPath("imported")], "res://", targets, s_importable, ClashPolicy.Replace);

        foreach (DropPlan plan in new[] { intoIt, intoBelow, replacing, aFolderOfThatName })
        {
            Assert.Empty(plan.Files);
            Assert.NotEmpty(plan.Refused);
        }

        Assert.Single(aFolderOfThatName.Refused);                                                   // said once for the folder, not for each file in it
    }

    [Fact]
    public void TheGameFolderOrAFolderHoldingItIsRefused()
    {
        using var source = new DropSource();
        string game = source.FullPath("game");
        source.Write("game/game.xml", "<Game/>");
        string inside = source.Write("game/a.png", TestAssets.Png());
        source.Write("other/b.png", TestAssets.Png());

        DropPlan itself = DropPlan.Make([game], "res://", new FakeTargets(), s_importable, gameFolder: game + Path.DirectorySeparatorChar);
        DropPlan parent = DropPlan.Make([source.Root], "res://", new FakeTargets(), s_importable, gameFolder: game);
        DropPlan sibling = DropPlan.Make([source.FullPath("other")], "res://", new FakeTargets(), s_importable, gameFolder: game);
        DropPlan aFileOfIt = DropPlan.Make([inside], "res://", new FakeTargets(), s_importable, gameFolder: game);

        Assert.Empty(itself.Files);
        Assert.Contains("game folder", Assert.Single(itself.Refused).Reason, StringComparison.Ordinal);
        Assert.Empty(parent.Files);
        Assert.Single(parent.Refused);
        Assert.Equal("res://other/b.png", Assert.Single(sibling.Files).Target);
        Assert.Equal("res://a.png", Assert.Single(aFileOfIt.Files).Target);                         // a file of the project can still be copied within it
    }

    [Fact]
    public void AHiddenFolderDroppedOnItsOwnIsRefused()
    {
        using var source = new DropSource();
        source.Write(".secret/a.png", TestAssets.Png());

        DropPlan plan = DropPlan.Make([source.FullPath(".secret")], "res://", new FakeTargets(), s_importable);

        Assert.Empty(plan.Files);
        Assert.Contains("hidden", Assert.Single(plan.Refused).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceNeverWritesThroughALink()
    {
        using var source = new DropSource();
        string image = source.Write("a.png", TestAssets.Png());
        var live = new FakeTargets(files: ["res://a.png"]);
        live.Links.Add("res://a.png");                                                              // a link in the project, to a file anywhere
        var dangling = new FakeTargets();
        dangling.Links.Add("res://a.png");                                                          // a link whose target is gone

        DropPlan one = DropPlan.Make([image], "res://", live, s_importable, ClashPolicy.Replace);
        DropPlan two = DropPlan.Make([image], "res://", dangling, s_importable, ClashPolicy.Replace);

        foreach (DropPlan plan in new[] { one, two })
        {
            PlannedFile file = Assert.Single(plan.Files);
            Assert.Equal("res://a 2.png", file.Target);
            Assert.False(file.Replaces);
        }
    }

    [Fact]
    public void AGltfThatGoesThroughALinkedFolderIsRefused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");
        using var source = new DropSource();
        source.Write("real/x.bin", TestAssets.TriangleBuffer());
        source.Write("m/m.gltf", TestAssets.GltfWithBuffer("lnk/x.bin"));
        Directory.CreateSymbolicLink(source.FullPath("m/lnk"), source.FullPath("real"));            // a folder that is a link: its files are files from anywhere

        DropPlan plan = Plan(source, new FakeTargets(), "res://", ClashPolicy.KeepBoth, "m/m.gltf");

        Assert.Empty(plan.Files);
        Assert.Contains("link", Assert.Single(plan.Refused).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameFileNamedTwoWaysInAGltfIsOneFile()
    {
        using var source = new DropSource();
        source.Write("m.gltf", """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":36,"uri":"a.bin"},{"byteLength":36,"uri":"a%2Ebin"}]}
            """);
        source.Write("a.bin", TestAssets.TriangleBuffer());

        DropPlan plan = Plan(source, new FakeTargets(), "res://", ClashPolicy.KeepBoth, "m.gltf");

        Assert.Empty(plan.Refused);
        Assert.Equal(["res://a.bin", "res://m.gltf"], plan.Files.Select(f => f.Target).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ARefusedModelsBytesDoNotCountAgainstTheTotal()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Files this large are not sparse on Windows file systems: writing them would fill the disk.");
        using var source = new DropSource();
        string big = source.Write("big.gltf", [1]);                                                 // too large to be a .gltf: refused whole
        using (var stream = new FileStream(big, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(DropPlan.MaxFileBytes - 1);
        }

        List<string> images = [];
        for (int i = 0; i < 4; i++)
        {
            string path = source.Write($"i{i}.png", [1]);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
            stream.SetLength(DropPlan.MaxFileBytes);                                                // four of them are exactly the total
            images.Add(path);
        }

        DropPlan plan = DropPlan.Make([big, .. images], "res://", new FakeTargets(), s_importable);

        Assert.Equal(4, plan.Files.Count);
        Assert.Single(plan.Refused);
        Assert.EndsWith("big.gltf", plan.Refused[0].Path, StringComparison.Ordinal);
    }
}
