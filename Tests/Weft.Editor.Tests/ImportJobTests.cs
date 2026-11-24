using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Weft.Content.Assets;
using Weft.Content.Import;
using Weft.Core.Assets;
using Weft.Editor.Assets;
using Weft.Host;
using Weft.Native.Stb;
using Weft.Native.Tests;
using Xunit;
using CgltfApi = Weft.Native.Cgltf.Cgltf;
using KtxApi = Weft.Native.Ktx.Ktx;
using MeshoptApi = Weft.Native.Meshopt.Meshopt;

namespace Weft.Editor.Tests;

/// <summary>An importer that does what another does, and lets the test look at (or hold up) each import.</summary>
internal sealed class WatchedImporter(IAssetImporter inner, Action? before = null) : IAssetImporter
{
    public List<int> Threads { get; } = [];

    public string Name => inner.Name;

    public IReadOnlyList<string> Extensions => inner.Extensions;

    public string Fingerprint => inner.Fingerprint;

    public ImportResult? Import(ImportContext context)
    {
        lock (Threads)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
        }

        before?.Invoke();
        return inner.Import(context);
    }
}

/// <summary>
/// The worker that copies dropped files into the project and imports them: that it runs off the main thread, what it leaves in the project, that it can be stopped between files, never overwrites, and keeps the
/// importer to itself (the hot reloader and Play wait). Real files, a real project, the real importers; the drop itself (SDL) needs a display and is not here.
/// </summary>
[Collection("editor")]
public sealed class ImportJobTests
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(60);

    public ImportJobTests()
    {
        NativeRequirements.Require(typeof(CgltfApi).Assembly, "cgltf");
        NativeRequirements.Require(typeof(Stbi).Assembly, "stb_image");
        NativeRequirements.Require(typeof(MeshoptApi).Assembly, "meshoptimizer");
        NativeRequirements.Require(typeof(KtxApi).Assembly, "ktx");
    }

    private static string Game(ProjectRig rig) => Path.Combine(rig.Root, "game");

    private static ProjectDropTargets TargetsOf(EditorProject project) => new ProjectDropTargets(project.Session.Files!, project.GameFolder);

    private static DropPlan PlanFor(EditorProject project, string folder, ClashPolicy policy, params string[] paths) =>
        DropPlan.Make(paths, folder, TargetsOf(project), [.. ScriptSession.Importers.SelectMany(i => i.Extensions)], policy);

    /// <summary>A job over the project with the gate taken, as <see cref="EditorProject.TryStartImport"/> makes one but with importers the test controls.</summary>
    private static ImportJob JobFor(EditorProject project, DropPlan plan, params IAssetImporter[] importers)
    {
        Assert.True(project.ImportGate.TryEnter());
        return new ImportJob(project.Session.Files!, importers.Length > 0 ? importers : ScriptSession.Importers, TargetsOf(project), plan, project.ImportGate.Exit);
    }

    [Fact]
    public void ADroppedImageIsCopiedAndImportedOffTheMainThread()
    {
        using var rig = new ProjectRig();
        ImportedProject.Mark(rig.Project);
        using var source = new DropSource();
        EditorProject project = rig.Project;
        string image = source.Write("shade.png", TestAssets.Png(40));
        var watched = new WatchedImporter(new ImageImporter());
        DropPlan plan = PlanFor(project, "res://textures", ClashPolicy.KeepBoth, image);

        ImportJob job = JobFor(project, plan, watched);
        job.Start();
        Assert.True(job.Wait(s_wait));

        Assert.True(job.IsEnded);
        ImportJobReport report = job.Report!;
        Assert.True(report.Success);
        Assert.Equal(["res://textures/shade.png"], report.Copied);
        Assert.Equal(["res://textures/shade.png"], report.Imported);
        Assert.Equal(TestAssets.Png(40), File.ReadAllBytes(Path.Combine(Game(rig), "textures", "shade.png")));
        Assert.Equal(AssetStatus.UpToDate, new AssetImporter(project.Session.Files!, ScriptSession.Importers).StatusOf("textures/shade.png"));
        Assert.Equal(2, job.Total);
        Assert.Equal(2, job.Done);
        Assert.False(project.ImportGate.IsHeld);                                                    // given back by the worker
        int worker = Assert.Single(watched.Threads);
        Assert.NotEqual(Environment.CurrentManagedThreadId, worker);                                // the import ran on the worker
    }

    [Fact]
    public void TheReloaderCatchesUpAfterTheJobWithoutImportingAgain()
    {
        using var rig = new ProjectRig();
        ImportedProject.Mark(rig.Project);
        using var source = new DropSource();
        EditorProject project = rig.Project;
        DropPlan plan = PlanFor(project, "res://textures", ClashPolicy.KeepBoth, source.Write("shade.png", TestAssets.Png(40)));
        ImportJob job = JobFor(project, plan);
        var seen = new List<string>();
        project.Session.Reloader!.AssetsChanged += seen.AddRange;

        job.Start();
        Assert.True(job.Wait(s_wait));
        Assert.True(job.Report!.Success);
        string index = Path.Combine(Game(rig), "imported", "index.xml");
        string artifact = Path.Combine(Game(rig), "imported", "textures", "shade.png.ktx2");
        string indexBefore = File.ReadAllText(index);
        byte[] artifactBefore = File.ReadAllBytes(artifact);
        DateTime artifactTime = File.GetLastWriteTimeUtc(artifact);

        // The files changed on disk while the job ran: once it is over the reloader notices them, and finds the artifacts current.
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!seen.Contains("res://textures/shade.png") && DateTime.UtcNow < until)
        {
            Thread.Sleep(100);
            project.Update(0f);
        }

        Assert.Contains("res://textures/shade.png", seen);
        Assert.Equal(indexBefore, File.ReadAllText(index));                                         // same key: nothing was imported a second time
        Assert.Equal(artifactBefore, File.ReadAllBytes(artifact));
        Assert.Equal(artifactTime, File.GetLastWriteTimeUtc(artifact));
    }

    [Fact]
    public void TheReloaderDoesNotRunWhileTheImporterIsHeld()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        var seen = new List<string>();
        project.Session.Reloader!.AssetsChanged += seen.AddRange;
        Assert.True(project.ImportGate.TryEnter());                                                 // an import job holds it

        File.WriteAllBytes(Path.Combine(Game(rig), "late.png"), TestAssets.Png());
        Thread.Sleep(600);                                                                         // longer than the reloader's quiet time
        project.Update(0f);
        Assert.Empty(seen);                                                                         // it did not poll: nothing imported behind the job's back

        project.ImportGate.Exit();
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (seen.Count == 0 && DateTime.UtcNow < until)
        {
            Thread.Sleep(100);
            project.Update(0f);
        }

        Assert.Contains("res://late.png", seen);                                                    // what it missed is not lost
    }

    [Fact]
    public void ACancelledJobStopsBetweenFiles()
    {
        using var rig = new ProjectRig();
        ImportedProject.Mark(rig.Project);
        using var source = new DropSource();
        EditorProject project = rig.Project;
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        var watched = new WatchedImporter(new ImageImporter(), () =>
        {
            entered.Set();
            Assert.True(proceed.Wait(s_wait, CancellationToken.None));
        });
        DropPlan plan = PlanFor(project, "res://", ClashPolicy.KeepBoth, source.Write("a.png", TestAssets.Png(1)), source.Write("b.png", TestAssets.Png(2)), source.Write("c.png", TestAssets.Png(3)));

        ImportJob job = JobFor(project, plan, watched);
        job.Start();
        Assert.True(entered.Wait(s_wait, CancellationToken.None));                                                          // the first import is under way
        Assert.True(job.IsRunning);
        job.Cancel();
        proceed.Set();
        Assert.True(job.Wait(s_wait));

        ImportJobReport report = job.Report!;
        Assert.True(report.Cancelled);
        Assert.False(report.Success);
        Assert.Equal(3, report.Copied.Count);                                                       // the copying was done: what was copied stays
        Assert.Single(report.Imported);                                                             // the second and third were not imported
        Assert.Single(watched.Threads);
        Assert.True(File.Exists(Path.Combine(Game(rig), "c.png")));
        Assert.False(project.ImportGate.IsHeld);
    }

    [Fact]
    public void ACancelBeforeTheFirstFileCopiesNothing()
    {
        using var rig = new ProjectRig();
        using var source = new DropSource();
        DropPlan plan = PlanFor(rig.Project, "res://", ClashPolicy.KeepBoth, source.Write("a.png", TestAssets.Png()));
        ImportJob job = JobFor(rig.Project, plan);

        job.Cancel();
        job.Start();
        Assert.True(job.Wait(s_wait));

        Assert.True(job.Report!.Cancelled);
        Assert.Empty(job.Report.Copied);
        Assert.False(File.Exists(Path.Combine(Game(rig), "a.png")));
    }

    [Fact]
    public void PlayAndAnotherImportAreRefusedWhileOneRuns()
    {
        using var rig = new ProjectRig();
        ImportedProject.Mark(rig.Project);
        using var source = new DropSource();
        EditorProject project = rig.Project;
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        DropPlan plan = PlanFor(project, "res://", ClashPolicy.KeepBoth, source.Write("a.png", TestAssets.Png()));
        ImportJob job = JobFor(project, plan, new WatchedImporter(new ImageImporter(), () =>
        {
            entered.Set();
            Assert.True(proceed.Wait(s_wait, CancellationToken.None));
        }));
        job.Start();
        Assert.True(entered.Wait(s_wait, CancellationToken.None));

        Assert.False(project.Play());
        Assert.Contains("imported", project.PlayError, StringComparison.Ordinal);
        Assert.False(project.IsPlaying);
        DropPlan second = PlanFor(project, "res://", ClashPolicy.KeepBoth, source.Write("b.png", TestAssets.Png()));
        Assert.False(project.TryStartImport(second, out ImportJob? other, out string? problem));
        Assert.Null(other);
        Assert.Contains("already running", problem, StringComparison.Ordinal);

        proceed.Set();
        Assert.True(job.Wait(s_wait));
        Assert.True(project.TryStartImport(second, out ImportJob? next, out _));                    // free again
        Assert.True(next!.Wait(s_wait));
    }

    [Fact]
    public void ImportingIsRefusedWhilePlaying()
    {
        using var rig = new ProjectRig();
        using var source = new DropSource();
        EditorProject project = rig.Project;
        DropPlan plan = PlanFor(project, "res://", ClashPolicy.KeepBoth, source.Write("a.png", TestAssets.Png()));
        Assert.True(project.Play());

        Assert.False(project.TryStartImport(plan, out ImportJob? job, out string? problem));

        Assert.Null(job);
        Assert.Contains("playing", problem, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Game(rig), "a.png")));
    }

    [Fact]
    public void ANameThatAppearedAfterThePlanIsNotOverwritten()
    {
        using var rig = new ProjectRig();
        using var source = new DropSource();
        EditorProject project = rig.Project;
        DropPlan plan = PlanFor(project, "res://", ClashPolicy.KeepBoth, source.Write("a.png", TestAssets.Png(1)), source.Write("b.png", TestAssets.Png(2)));
        File.WriteAllBytes(Path.Combine(Game(rig), "a.png"), [1, 2, 3]);                           // another program wrote it after the plan was made

        ImportJob job = JobFor(project, plan);
        job.Start();
        Assert.True(job.Wait(s_wait));

        ImportJobReport report = job.Report!;
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(Game(rig), "a.png")));               // untouched
        (string path, string message) = Assert.Single(report.Failed);
        Assert.Equal("res://a.png", path);
        Assert.Contains("was not overwritten", message, StringComparison.Ordinal);
        Assert.Equal(["res://b.png"], report.Copied);                                              // the rest went on
    }

    [Fact]
    public void ReplaceOverwritesTheFileAndKeepsItsSettings()
    {
        using var rig = new ProjectRig();
        ImportedProject.Mark(rig.Project);
        using var source = new DropSource();
        EditorProject project = rig.Project;
        AssetUid uid = AssetUid.New();
        File.WriteAllBytes(Path.Combine(Game(rig), "a.png"), TestAssets.Png(1));
        File.WriteAllText(Path.Combine(Game(rig), "a.png.wimp"), ImportSidecar.SetUid("", uid));
        string dropped = source.Write("a.png", TestAssets.Png(9));
        source.Write("a.png.wimp", ImportSidecar.SetUid("", AssetUid.New()));
        DropPlan plan = PlanFor(project, "res://", ClashPolicy.Replace, dropped);

        ImportJob job = JobFor(project, plan);
        job.Start();
        Assert.True(job.Wait(s_wait));

        Assert.True(job.Report!.Success);
        Assert.Equal(TestAssets.Png(9), File.ReadAllBytes(Path.Combine(Game(rig), "a.png")));
        Assert.Equal(uid, ImportSidecar.ReadUid(File.ReadAllText(Path.Combine(Game(rig), "a.png.wimp"))));   // the asset is still the same asset
    }

    [Fact]
    public void ASidecarWithAUidAnotherAssetHasGetsANewOne()
    {
        using var rig = new ProjectRig();
        ImportedProject.Mark(rig.Project);
        using var source = new DropSource();
        EditorProject project = rig.Project;
        AssetUid shared = AssetUid.New();
        File.WriteAllBytes(Path.Combine(Game(rig), "orig.png"), TestAssets.Png(1));
        File.WriteAllText(Path.Combine(Game(rig), "orig.png.wimp"), ImportSidecar.SetUid("", shared));
        string copy = source.Write("copy.png", TestAssets.Png(2));
        source.Write("copy.png.wimp", ImportSidecar.SetUid("", shared));                      // a copy of the asset together with its settings
        DropPlan plan = PlanFor(project, "res://", ClashPolicy.KeepBoth, copy);

        ImportJob job = JobFor(project, plan);
        job.Start();
        Assert.True(job.Wait(s_wait));

        ImportJobReport report = job.Report!;
        Assert.True(report.Success);
        (string path, AssetUid old, AssetUid fresh) = Assert.Single(report.Reassigned);
        Assert.Equal("res://copy.png", path);
        Assert.Equal(shared, old);
        Assert.NotEqual(shared, fresh);
        Assert.Equal(fresh, ImportSidecar.ReadUid(File.ReadAllText(Path.Combine(Game(rig), "copy.png.wimp"))));
        Assert.Equal(shared, ImportSidecar.ReadUid(File.ReadAllText(Path.Combine(Game(rig), "orig.png.wimp"))));  // the original keeps its identity
    }

    [Fact]
    public void ASidecarWithAFreshUidKeepsIt()
    {
        using var rig = new ProjectRig();
        ImportedProject.Mark(rig.Project);
        using var source = new DropSource();
        AssetUid uid = AssetUid.New();
        string dropped = source.Write("a.png", TestAssets.Png());
        source.Write("a.png.wimp", ImportSidecar.SetUid("", uid));

        ImportJob job = JobFor(rig.Project, PlanFor(rig.Project, "res://", ClashPolicy.KeepBoth, dropped));
        job.Start();
        Assert.True(job.Wait(s_wait));

        Assert.Empty(job.Report!.Reassigned);
        Assert.Equal(uid, ImportSidecar.ReadUid(File.ReadAllText(Path.Combine(Game(rig), "a.png.wimp"))));
    }

    [Fact]
    public void AModelWithItsBufferIsCopiedAndImportedAsOne()
    {
        using var rig = new ProjectRig();
        ImportedProject.Mark(rig.Project);
        using var source = new DropSource();
        string model = source.Write("tri/tri.gltf", TestAssets.GltfWithBuffer("tri.bin"));
        source.Write("tri/tri.bin", TestAssets.TriangleBuffer());

        ImportJob job = JobFor(rig.Project, PlanFor(rig.Project, "res://models", ClashPolicy.KeepBoth, model));
        job.Start();
        Assert.True(job.Wait(s_wait));

        ImportJobReport report = job.Report!;
        Assert.True(report.Success, string.Join("; ", report.Failed.Select(f => f.Message)));
        Assert.Equal(["res://models/tri.gltf"], report.Imported);                                   // the buffer is part of the model, not an asset of its own
        Assert.True(File.Exists(Path.Combine(Game(rig), "models", "tri.bin")));
        Assert.Equal(AssetStatus.UpToDate, new AssetImporter(rig.Project.Session.Files!, ScriptSession.Importers).StatusOf("models/tri.gltf"));
    }

    [Fact]
    public void AFailedImportLeavesTheCopiedFileAndSaysSo()
    {
        using var rig = new ProjectRig();
        ImportedProject.Mark(rig.Project);
        using var source = new DropSource();
        string bad = source.Write("bad.glb", "this is not a model at all, but it has bytes");

        ImportJob job = JobFor(rig.Project, PlanFor(rig.Project, "res://", ClashPolicy.KeepBoth, bad));
        job.Start();
        Assert.True(job.Wait(s_wait));

        ImportJobReport report = job.Report!;
        Assert.False(report.Success);
        Assert.Equal(["res://bad.glb"], report.Copied);
        Assert.Empty(report.Imported);
        Assert.Equal("res://bad.glb", Assert.Single(report.Failed).Path);
        Assert.True(File.Exists(Path.Combine(Game(rig), "bad.glb")));                               // it stays: Re-import tries again
        Assert.False(rig.Project.ImportGate.IsHeld);
    }

    [Fact]
    public void ClosingTheProjectStopsAndWaitsForTheJob()
    {
        using var rig = new ProjectRig();
        ImportedProject.Mark(rig.Project);
        using var source = new DropSource();
        EditorProject project = rig.Project;
        DropPlan plan = PlanFor(project, "res://", ClashPolicy.KeepBoth, Enumerable.Range(0, 5).Select(i => source.Write($"f{i}.png", TestAssets.Png((byte)i))).ToArray());
        Assert.True(project.TryStartImport(plan, out ImportJob? job, out _));

        project.Dispose();

        Assert.True(job!.IsEnded);                                                                  // the worker was finished with the files before the session went
        Assert.False(project.ImportGate.IsHeld);
    }

    [Fact]
    public void AProjectThatWasNeverImportedGetsNoIndexAndNoWholeProjectImport()
    {
        using var rig = new ProjectRig();
        using var source = new DropSource();
        EditorProject project = rig.Project;
        File.WriteAllBytes(Path.Combine(Game(rig), "other.png"), TestAssets.Png(7));              // something else in the project: a whole-project import would make an artifact of it
        var seen = new List<string>();
        project.Session.Reloader!.AssetsChanged += seen.AddRange;

        ImportJob job = JobFor(project, PlanFor(project, "res://", ClashPolicy.KeepBoth, source.Write("a.png", TestAssets.Png())));
        job.Start();
        Assert.True(job.Wait(s_wait));

        ImportJobReport report = job.Report!;
        Assert.True(report.Success);
        Assert.True(report.NotImported);
        Assert.Equal(["res://a.png"], report.Copied);
        Assert.Empty(report.Imported);
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!seen.Contains("res://a.png") && DateTime.UtcNow < until)
        {
            Thread.Sleep(100);
            project.Update(0f);                                                                     // the reloader has noticed the files
        }

        Assert.Contains("res://a.png", seen);
        Assert.False(Directory.Exists(Path.Combine(Game(rig), "imported")));                       // and imported nothing: no index, no artifact of other.png
    }

    [Fact]
    public void AnImporterThatThrowsFailsItsFileAndTheNextOneStillImports()
    {
        using var rig = new ProjectRig();
        ImportedProject.Mark(rig.Project);
        using var source = new DropSource();
        int calls = 0;
        var watched = new WatchedImporter(new ImageImporter(), () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("an importer bug");
            }
        });
        DropPlan plan = PlanFor(rig.Project, "res://", ClashPolicy.KeepBoth, source.Write("a.png", TestAssets.Png(1)), source.Write("b.png", TestAssets.Png(2)));

        ImportJob job = JobFor(rig.Project, plan, watched);
        job.Start();
        Assert.True(job.Wait(s_wait));

        ImportJobReport report = job.Report!;
        Assert.Null(report.Error);                                                                  // a file's failure is the file's, not the job's
        Assert.False(report.Success);
        (string path, string message) = Assert.Single(report.Failed);
        Assert.Equal("res://a.png", path);
        Assert.Contains("an importer bug", message, StringComparison.Ordinal);
        Assert.Equal(["res://b.png"], report.Imported);
        Assert.False(rig.Project.ImportGate.IsHeld);
    }

    [Fact]
    public void AFileThatChangedSinceThePlanIsNotCopied()
    {
        using var rig = new ProjectRig();
        using var source = new DropSource();
        string grows = source.Write("grows.png", TestAssets.Png(1));
        string shrinks = source.Write("shrinks.png", TestAssets.Png(2));
        DropPlan plan = PlanFor(rig.Project, "res://", ClashPolicy.KeepBoth, grows, shrinks);
        File.AppendAllText(grows, "more bytes than the plan saw");                                  // a file that kept growing: never read past what the plan accepted
        File.WriteAllBytes(shrinks, [1, 2, 3]);

        ImportJob job = JobFor(rig.Project, plan);
        job.Start();
        Assert.True(job.Wait(s_wait));

        Assert.Equal(2, job.Report!.Failed.Count);
        Assert.All(job.Report.Failed, f => Assert.Contains("changed since it was dropped", f.Message, StringComparison.Ordinal));
        Assert.Empty(job.Report.Copied);
        Assert.False(File.Exists(Path.Combine(Game(rig), "grows.png")));
    }

    [Fact]
    public void AFileSwappedForAPipeAfterThePlanDoesNotHoldTheWorker()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "There are no named pipes in the file system on Windows.");
        using var rig = new ProjectRig();
        using var source = new DropSource();
        string path = source.Write("a.png", TestAssets.Png());
        DropPlan plan = PlanFor(rig.Project, "res://", ClashPolicy.KeepBoth, path);
        File.Delete(path);
        try
        {
            using var made = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mkfifo", [path]) { UseShellExecute = false })!;
            made.WaitForExit();
            Assert.SkipWhen(made.ExitCode != 0, "mkfifo failed here.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("There is no mkfifo here.");
        }

        ImportJob job = JobFor(rig.Project, plan);
        job.Start();

        Assert.True(job.Wait(TimeSpan.FromSeconds(20)));                                            // opening a pipe with no writer would wait for ever
        Assert.Contains("changed since it was dropped", Assert.Single(job.Report!.Failed).Message, StringComparison.Ordinal);
        Assert.False(rig.Project.ImportGate.IsHeld);
    }

    [Fact]
    public void PlayWaitsUntilTheEndedJobHasBeenTaken()
    {
        using var rig = new ProjectRig();
        using var source = new DropSource();
        EditorProject project = rig.Project;
        Assert.True(project.TryStartImport(PlanFor(project, "res://", ClashPolicy.KeepBoth, source.Write("a.png", TestAssets.Png())), out ImportJob? job, out _));
        Assert.True(job!.Wait(s_wait));

        Assert.False(project.ImportGate.IsHeld);                                                    // the worker is done and the gate free, but the report has not been taken
        Assert.True(project.ImportBusy);
        Assert.False(project.Play());
        Assert.Contains("imported", project.PlayError, StringComparison.Ordinal);

        project.ReleaseJob();
        Assert.False(project.ImportBusy);
        Assert.True(project.Play());
    }

    [Fact]
    public void ThereIsOneGatePerGameFolderForTheWholeProcess()
    {
        using var rig = new ProjectRig();
        string game = Game(rig);

        Assert.Same(rig.Project.ImportGate, ImportGate.ForFolder(game));
        Assert.Same(ImportGate.ForFolder(game), ImportGate.ForFolder(game + Path.DirectorySeparatorChar));
        Assert.Same(ImportGate.ForFolder(game), ImportGate.ForFolder(Path.Combine(game, "packages", "..")));
        Assert.NotSame(ImportGate.ForFolder(game), ImportGate.ForFolder(Path.Combine(rig.Root, "other")));

        // A project closed in the middle of a job and opened again is a new object over the same folder: it finds the gate as the worker left it.
        Assert.True(rig.Project.ImportGate.TryEnter());
        Assert.True(ImportGate.ForFolder(game).IsHeld);
        Assert.False(ImportGate.ForFolder(game).TryEnter());
        rig.Project.ImportGate.Exit();
        Assert.False(ImportGate.ForFolder(game).IsHeld);
    }
}
