using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Apogee.Content.FileSystem;
using Xunit;

namespace Apogee.Content.Tests;

public class WatcherTests
{
    /// <summary>Changes, collected until <paramref name="done"/> holds or ten seconds pass.</summary>
    private static List<string> Collect(VfsWatcher watcher, Func<List<string>, bool> done)
    {
        var all = new List<string>();
        DateTime end = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < end && !done(all))
        {
            all.AddRange(watcher.TakeChanges());
            Thread.Sleep(25);
        }

        return all;
    }

    [Fact]
    public void AChangeIsReportedOnceAsAVirtualPathAfterItWentQuiet()
    {
        using var folder = new TempFolder();
        folder.Write("scripts/a.luau", "1");
        var files = new VirtualFileSystem();
        files.Mount("res://", new DirectorySource(folder.Root));
        using var watcher = new VfsWatcher(files, TimeSpan.FromMilliseconds(100));

        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(folder.Root, "scripts", "a.luau"), $"version {i}");
        }

        List<string> changes = Collect(watcher, all => all.Contains("res://scripts/a.luau"));
        Thread.Sleep(300);
        changes.AddRange(watcher.TakeChanges());

        Assert.Equal(1, changes.Count(c => c == "res://scripts/a.luau")); // five writes, one report
        Assert.Empty(watcher.TakeChanges());
    }

    [Fact]
    public void NewAndDeletedFilesAreChangesToo()
    {
        using var folder = new TempFolder();
        folder.Write("old.txt", "x");
        var files = new VirtualFileSystem();
        files.Mount("res://", new DirectorySource(folder.Root));
        using var watcher = new VfsWatcher(files, TimeSpan.FromMilliseconds(50));

        folder.Write("deep/new.txt", "y");
        File.Delete(Path.Combine(folder.Root, "old.txt"));
        List<string> changes = Collect(watcher, all => all.Contains("res://deep/new.txt") && all.Contains("res://old.txt"));

        Assert.Contains("res://deep/new.txt", changes);
        Assert.Contains("res://old.txt", changes);
    }

    [Fact]
    public void HiddenFilesAndTemporaryFilesAreIgnored()
    {
        using var folder = new TempFolder();
        var files = new VirtualFileSystem();
        files.Mount("res://", new DirectorySource(folder.Root));
        using var watcher = new VfsWatcher(files, TimeSpan.FromMilliseconds(50));

        folder.Write(".hidden/x.txt", "1");
        folder.Write("a.txt.tmp", "2");
        folder.Write("a.txt~", "3");
        folder.Write(".swap", "4");
        folder.Write("visible.txt", "5");
        List<string> changes = Collect(watcher, all => all.Contains("res://visible.txt"));
        Thread.Sleep(200);
        changes.AddRange(watcher.TakeChanges());

        Assert.Contains("res://visible.txt", changes);
        Assert.DoesNotContain(changes, c => c.Contains(".hidden", StringComparison.Ordinal) || c.EndsWith(".tmp", StringComparison.Ordinal) || c.EndsWith('~') || c.EndsWith(".swap", StringComparison.Ordinal));
    }

    [Fact]
    public void AFileSeenThroughAPackageViewIsReportedUnderBothAddresses()
    {
        using var folder = new TempFolder();
        folder.Write("packages/core/main.luau", "1");
        var files = new VirtualFileSystem();
        files.Mount("res://", new DirectorySource(folder.Root));
        files.Mount("mod://core", new VfsSubtreeSource(files, "res://packages/core"));
        using var watcher = new VfsWatcher(files, TimeSpan.FromMilliseconds(50));

        File.WriteAllText(Path.Combine(folder.Root, "packages", "core", "main.luau"), "2");
        List<string> changes = Collect(watcher, all => all.Contains("res://packages/core/main.luau") && all.Contains("mod://core/main.luau"));

        Assert.Contains("res://packages/core/main.luau", changes);
        Assert.Contains("mod://core/main.luau", changes);
    }

    [Fact]
    public void ZipsAndPacksAreNotWatchedAndAMissingFolderIsFine()
    {
        var files = new VirtualFileSystem();
        files.Mount("res://", new DirectorySource(Path.Combine(Path.GetTempPath(), "apogee-no-such-folder-" + Guid.NewGuid().ToString("N"))));
        using var watcher = new VfsWatcher(files);

        Assert.Empty(watcher.TakeChanges());
    }
}
