using System;
using Apogee.Core.Logging;
using Apogee.Platform;

namespace Apogee.Editor.Editing;

/// <summary>
/// One dialog at a time, and a way out for a dialog that never answers. A request that starts is <see cref="Busy"/> until its result is delivered; when none arrives within <c>lostAfter</c> (a portal that
/// crashed and never sends its Response, a result that could not even be queued) the dialog counts as lost: the gate is free again, and if the result does turn up later it is dropped, not delivered to a
/// screen that moved on. No SDL in here, so the logic is tested with a fake clock and a <c>show</c> that throws.
/// </summary>
public sealed class DialogGate(TimeSpan lostAfter, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private int _token;
    private bool _open;
    private long _started;

    /// <summary>A dialog is open and not lost.</summary>
    public bool Busy
    {
        get
        {
            if (_open && _clock.GetElapsedTime(_started) > lostAfter)
            {
                Log.Warn("Editor", $"A file dialog did not answer within {lostAfter.TotalMinutes:0.#} minutes: treated as lost (a late answer is ignored).");
                _open = false;
                _token++; // the late result, if any, carries the old token
            }

            return _open;
        }
    }

    /// <summary>
    /// Starts a dialog: <paramref name="show"/> is given the continuation that SDL's result must reach (it calls <paramref name="done"/> once, unless the dialog was lost meanwhile). False when a dialog
    /// is already open. An exception from <paramref name="show"/> that means "SDL cannot show it" (<see cref="PlatformException"/>, a missing library or entry point, a bad filter) is delivered as a failed
    /// result, so the caller falls back; any other exception frees the gate and is rethrown.
    /// </summary>
    public bool Start(Action<FileDialogResult> done, Action<Action<FileDialogResult>> show)
    {
        ArgumentNullException.ThrowIfNull(done);
        ArgumentNullException.ThrowIfNull(show);
        if (Busy)
        {
            return false;
        }

        int token = ++_token;
        _open = true;
        _started = _clock.GetTimestamp();

        void Finished(FileDialogResult result)
        {
            if (token != _token)
            {
                Log.Info("Editor", "A file dialog that was treated as lost has answered; its result is dropped.");
                return;
            }

            _open = false;
            done(result);
        }

        try
        {
            show(Finished);
        }
        catch (Exception e) when (e is PlatformException or DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            Finished(FileDialogResult.Failed(e.Message));
        }
        catch
        {
            if (token == _token)
            {
                _open = false;
            }

            throw;
        }

        return true;
    }
}
