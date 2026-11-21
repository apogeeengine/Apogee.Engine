using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apogee.Content;
using Apogee.Content.Assets;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;
using Apogee.Core.Assets;
using Apogee.Core.Logging;

namespace Apogee.Editor.Assets;

/// <summary>What an <see cref="ImportJob"/> did. Built by the worker before the job is marked ended, so the main thread reads it whole.</summary>
/// <param name="Copied">The files now in the project (virtual paths).</param>
/// <param name="Imported">The assets that were imported, as virtual paths.</param>
/// <param name="Failed">The files that were not copied or not imported, with the reason; the ones that were copied stay in place (the asset browser's <i>Re-import</i> tries again).</param>
/// <param name="Reassigned">The assets whose <c>.import.xml</c> came with a UID that another asset of the project has, and got a new one (the path, the old UID, the new).</param>
/// <param name="Cancelled">The job was cancelled before it was done: what was copied stays, nothing was undone.</param>
/// <param name="Error">An unexpected failure that ended the job.</param>
public sealed record ImportJobReport(
    IReadOnlyList<string> Copied,
    IReadOnlyList<string> Imported,
    IReadOnlyList<(string Path, string Message)> Failed,
    IReadOnlyList<(string Path, AssetUid Old, AssetUid New)> Reassigned,
    bool Cancelled,
    string? Error)
{
    /// <summary>Everything was copied and imported.</summary>
    public bool Success => !Cancelled && Error is null && Failed.Count == 0;
}

/// <summary>
/// Copies the files of a <see cref="DropPlan"/> into the project and imports them, on a worker thread, so that a big model or a Basis encode does not stop the editor.
/// <para>
/// <b>Threads.</b> The job is started on the main thread and runs on one worker. It shares with the main thread only: (1) the project's <see cref="VirtualFileSystem"/> (safe: its mount list is guarded, the
/// folders it writes to are not touched by the editor meanwhile except to read), (2) the progress fields, written by the worker and read by the main thread through <see cref="Volatile"/>, (3) the
/// cancel flag, the other way, and (4) the end: the worker builds the <see cref="Report"/>, then publishes it with a volatile write of the state, and the main thread reads the report only after it saw the
/// state ended. The worker touches nothing else of the editor (no world, no session, no GPU, no ImGui).
/// </para>
/// <para>
/// <b>The importer.</b> The caller holds the <see cref="ImportGate"/> for the job: the hot reloader and the asset browser import through the same index, so none of them runs while the job does. The job gives
/// the gate back, from the worker, as its last act (<c>release</c>).
/// </para>
/// </summary>
public sealed class ImportJob
{
    private const string LogCategory = "Import";

    private readonly VirtualFileSystem _files;
    private readonly IReadOnlyList<IAssetImporter> _importers;
    private readonly IDropTargets _targets;
    private readonly DropPlan _plan;
    private readonly Action _release;
    private int _total;
    private int _done;
    private string _current = "";
    private int _cancel;
    private bool _stopped;               // the worker saw the cancel and left the work half done (worker only)
    private int _state;                  // 0 not started, 1 running, 2 ended
    private ImportJobReport? _report;
    private Task _task = Task.CompletedTask;

    /// <summary>The job for <paramref name="plan"/>. <paramref name="release"/> gives the importer gate back once the work is over, whatever happened.</summary>
    public ImportJob(VirtualFileSystem files, IReadOnlyList<IAssetImporter> importers, IDropTargets targets, DropPlan plan, Action release)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _importers = importers ?? throw new ArgumentNullException(nameof(importers));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _release = release ?? throw new ArgumentNullException(nameof(release));
        _total = plan.Files.Count + plan.Importable.Count();
    }

    /// <summary>The job is on its way: started and not ended.</summary>
    public bool IsRunning => Volatile.Read(ref _state) == 1;

    /// <summary>The job ended (done, cancelled or failed): <see cref="Report"/> is there.</summary>
    public bool IsEnded => Volatile.Read(ref _state) == 2;

    /// <summary>The steps the job has (one to copy each file, one to import each importable one).</summary>
    public int Total => Volatile.Read(ref _total);

    /// <summary>The steps done.</summary>
    public int Done => Volatile.Read(ref _done);

    /// <summary>What the job is doing now (<c>copying res://models/car.glb</c>).</summary>
    public string Current => Volatile.Read(ref _current);

    /// <summary>The plan the job runs.</summary>
    public DropPlan Plan => _plan;

    /// <summary>What happened; null until <see cref="IsEnded"/>.</summary>
    public ImportJobReport? Report => IsEnded ? Volatile.Read(ref _report) : null;

    /// <summary>The worker's task, for tests and for waiting at shutdown.</summary>
    public Task Completion => _task;

    /// <summary>Starts the worker. Once.</summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            throw new InvalidOperationException("The job was started already.");
        }

        _task = Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>Asks the job to stop at the next file. What was copied stays.</summary>
    public void Cancel() => Volatile.Write(ref _cancel, 1);

    /// <summary>Waits for the job to end, up to <paramref name="timeout"/>; true when it did.</summary>
    public bool Wait(TimeSpan timeout) => _task.Wait(timeout);

    private bool Cancelled => Volatile.Read(ref _cancel) != 0;

    private void Step(string what)
    {
        Volatile.Write(ref _current, what);
        Log.Info(LogCategory, what);
    }

    private void Advance() => Interlocked.Increment(ref _done);

    /// <summary>The worker's body. Nothing may escape it: a worker that throws would end the process.</summary>
    private void Work()
    {
        var copied = new List<string>();
        var imported = new List<string>();
        var failed = new List<(string, string)>();
        var reassigned = new List<(string, AssetUid, AssetUid)>();
        string? error = null;
        try
        {
            CopyAll(copied, failed, reassigned);
            if (!_stopped)
            {
                ImportAll(copied, imported, failed);
            }
        }
#pragma warning disable CA1031 // The worker's last line of defence: whatever went wrong is the report's error, and the editor goes on.
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            error = e.Message;
            Log.Error(LogCategory, $"The import stopped: {e}");
        }
#pragma warning restore CA1031
        finally
        {
            Volatile.Write(ref _report, new ImportJobReport(copied, imported, failed, reassigned, _stopped && error is null, error));
            Volatile.Write(ref _current, "");
            Volatile.Write(ref _state, 2);   // published after the report: the main thread reads the report only once it sees this
            _release();                      // the importer gate: given back last, so nothing that sees it free can still be running
        }
    }

    private void CopyAll(List<string> copied, List<(string, string)> failed, List<(string, AssetUid, AssetUid)> reassigned)
    {
        HashSet<AssetUid>? used = null; // the UIDs the project's sidecars have; read once, and only when a sidecar comes along
        foreach (PlannedFile file in _plan.Files)
        {
            if (Cancelled)
            {
                _stopped = true;
                return;
            }

            Step($"Copying {file.Target}");
            try
            {
                byte[] bytes = ReadSource(file);
                if (file.IsCompanion && file.Target.EndsWith(ImportSidecar.Suffix, StringComparison.OrdinalIgnoreCase))
                {
                    bytes = FreshUid(file, bytes, ref used, reassigned);
                }

                Write(file, bytes);
                copied.Add(file.Target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ContentException or VfsException)
            {
                failed.Add((file.Target, e.Message));
                Log.Error(LogCategory, $"{file.Target}: not copied: {e.Message}");
            }

            Advance();
        }
    }

    /// <summary>Reads the dropped file again, checking that it is still what the plan accepted (a link or a file that grew since).</summary>
    private static byte[] ReadSource(PlannedFile file)
    {
        if (DropPlan.IsLink(file.Source))
        {
            throw new IOException($"{file.Source} became a link since it was dropped.");
        }

        byte[] bytes = File.ReadAllBytes(file.Source);
        return bytes.LongLength is 0 or > DropPlan.MaxFileBytes ? throw new IOException($"{file.Source} changed since it was dropped (now {bytes.LongLength} bytes).") : bytes;
    }

    /// <summary>
    /// Writes the file, never over one the plan did not mean to replace: something that is at the target now, and was not when the plan was made (another program, a second drop), is left alone and
    /// reported. The write itself is the file system's: whole or not at all.
    /// </summary>
    private void Write(PlannedFile file, byte[] bytes)
    {
        if (!file.Replaces && _targets.Exists(file.Target))
        {
            throw new IOException($"{file.Target} appeared in the project while the files were being copied, so it was not overwritten.");
        }

        _files.WriteAllBytes(file.Target, bytes);
    }

    /// <summary>
    /// The sidecar's text with a new UID when its UID is one the project's assets already have (a copy of an asset and its settings, or the same drop twice), so that two assets never share one; the
    /// importer would tell them apart later, but not before the copy has been listed under the original's UID. A sidecar without a UID, or with a UID nobody has, is copied as it is.
    /// </summary>
    private byte[] FreshUid(PlannedFile file, byte[] bytes, ref HashSet<AssetUid>? used, List<(string, AssetUid, AssetUid)> reassigned)
    {
        string text = new System.Text.UTF8Encoding(false).GetString(bytes);
        AssetUid uid = ImportSidecar.ReadUid(text);
        if (uid.IsNone)
        {
            return bytes;
        }

        used ??= UidsInUse();
        if (used.Add(uid))
        {
            return bytes;
        }

        AssetUid fresh = AssetUid.New();
        used.Add(fresh);
        string asset = file.Target[..^ImportSidecar.Suffix.Length];
        reassigned.Add((asset, uid, fresh));
        Log.Info(LogCategory, $"{asset} came with the UID {uid}, which another asset has: it has the new UID {fresh}.");
        return new System.Text.UTF8Encoding(false).GetBytes(ImportSidecar.SetUid(text, fresh));
    }

    private HashSet<AssetUid> UidsInUse()
    {
        var used = new HashSet<AssetUid>();
        foreach (string sidecar in _files.EnumerateFiles("res://", "*" + ImportSidecar.Suffix, recursive: true))
        {
            if (VfsPath.IsUnder(sidecar, "res://" + AssetImporter.CacheFolder))
            {
                continue;
            }

            try
            {
                if (ImportSidecar.ReadUid(new System.Text.UTF8Encoding(false).GetString(_files.ReadAllBytes(sidecar))) is { IsNone: false } uid)
                {
                    used.Add(uid);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A sidecar that cannot be read has a UID we cannot know; the importer's own pass tells copies apart at the next full import.
            }
        }

        return used;
    }

    private void ImportAll(List<string> copied, List<string> imported, List<(string, string)> failed)
    {
        var importer = new AssetImporter(_files, _importers);
        HashSet<string> there = [.. copied];
        foreach (PlannedFile file in _plan.Importable.Where(f => there.Contains(f.Target)))
        {
            if (Cancelled)
            {
                _stopped = true;
                return;
            }

            Step($"Importing {file.Target}");
            try
            {
                ImportReport report = importer.Reimport(file.Target["res://".Length..]);
                foreach ((string source, string message) in report.Failed)
                {
                    failed.Add(("res://" + source, message));
                    Log.Error(LogCategory, $"res://{source}: not imported: {message}");
                }

                imported.AddRange(report.Imported.Select(s => "res://" + s));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ContentException)
            {
                failed.Add((file.Target, e.Message));
                Log.Error(LogCategory, $"{file.Target}: not imported: {e.Message}");
            }

            Advance();
        }
    }
}
