# Logs and crash reports

Every run of a game writes its log to a file of its own, and a program that dies leaves a crash report. Both stay on the player's machine: nothing is sent anywhere. A player who has a problem
sends the files, or you ask for them.

## The log file

A game started by `Weft.Runtime` (`--game <folder>`, or a shipped game starting itself) and the editor open a `FileLogSink` when they start. It writes the same lines as the console
(`HH:mm:ss.fff LVL [Category] message`), one `Log` entry per line, and flushes after every line, so that the end of the file survives a crash. The first lines say which engine version, which OS and
which command line the run had. `Log.MinimumLevel` filters what reaches it, as it filters the console.

- The file is `<yyyyMMdd-HHmmss>-<pid>.log` (UTC) in `<user>/logs`: one per run, so that two instances of a game do not mix their lines. The newest 10 are kept; older ones are deleted when a run starts.
- A file that passes 64 MiB gets the line `Log truncated` and nothing more. A normal run is a few hundred kilobytes.
- A clean end writes `End of run.` as the last line. A file without it is a run that died.
- A file that cannot be written (a read-only disk, no space) switches the sink off with one line on stderr. The game goes on.
- A single script run with `--run` is a development tool: it has the crash handler but no log file.

## Where the files are

`<user>` is the player's folder of the game (`user://`): `--user <dir>`, else `<local application data>/<organization>/<userFolder>` from `game.wgam` (see [The game manifest](../content/game-manifest.md#what-it-drives)).

| OS | `<local application data>` | Example |
| --- | --- | --- |
| Linux | `$XDG_DATA_HOME`, else `~/.local/share` | `~/.local/share/Acme/Truck/logs/20261115-120000-4242.log` |
| Windows | `%LOCALAPPDATA%` | `C:\Users\you\AppData\Local\Acme\Truck\crashes\` |
| macOS | `~/Library/Application Support` | `~/Library/Application Support/Acme/Truck/crashes/` |

The editor keeps its own `logs` and `crashes` in `<local application data>/Weft/Editor`. Before a game's folder is known (while the program parses its arguments), and for commands that are not a
game (`--pack`, `--import`, ...), reports go to `<local application data>/Weft/Runtime/crashes`.

## Crash reports

A crash report is a plain text file `crash-<yyyyMMdd-HHmmss-fff>-<number>-<kind>.txt` (the number orders reports made in the same millisecond) in `<user>/crashes`. The newest 20 are kept. It holds:

- the kind and the time (UTC);
- the engine version with its commit (`0.2.0-alpha+abc1234`, see [Versions](versions.md)), the .NET version and whether it is a NativeAOT build;
- the OS and its architecture, the processor count and the memory;
- the game, its version, and the GPU once the device is created (`CrashHandler.SetContext` adds more lines);
- the thread, and the exception with its inner exceptions and stack trace;
- the last lines of the log (200 by default).

The kinds:

| Kind | When | The program |
| --- | --- | --- |
| `unhandled` | an exception that nothing caught ends the program (`AppDomain.UnhandledException`) | ends. The log gets the exception, the line `A crash report was written to ...`, and `End of run.`; the path is also on stderr |
| `task` | a task failed and nobody looked at its exception (`TaskScheduler.UnobservedTaskException`) | goes on. Only the first one of a run gets a report; every one is in the log |
| `unclean-exit` | the previous run did not end cleanly | is starting normally |

A stack trace of a NativeAOT build has the method names but no source lines. The report's other lines tell which build it was.

A Luau error is an error of the script, which the engine reports and logs; it is not an unhandled exception. The reports are for engine bugs, bad native libraries and mistakes in a game's C# code.

## A run that did not end cleanly

A crash in native code (a Vulkan driver, SDL, Jolt, a bug in an engine shim), `kill -9`, the task manager, a power cut or an out-of-memory kill take the process down without running any managed
code, so nothing can write a report at that moment. The next start finds out instead:

1. While a run lives, `<user>/logs/<run>.running` holds its process id, the time the process started and the name of its log.
2. A clean end deletes it, and so does the crash handler once it has written a report for an unhandled exception.
3. At the next start, every marker whose process is gone (or whose process id belongs to a newer process, which a reboot makes common) gets a report of kind `unclean-exit` with the last 200 lines of
   *that run's* log, and the marker is removed. A marker of a process that still runs, another instance of the game, is left alone.

So the report comes one start late, and says only what the log said; for the native stack, see below. A player who closes a frozen game from the task manager gets one of these reports too, which is
accurate: the run did not end by itself.

## Getting a native stack from the OS

The operating system captures native crashes better than a program can from inside itself. To find the cause of a crash in native code:

- **Linux.** Allow core dumps (`ulimit -c unlimited` in the shell that runs the game), and see where they go with `cat /proc/sys/kernel/core_pattern`. On systemd machines `coredumpctl list` and
  `coredumpctl gdb` open the last one. `gdb` with `bt` (and `thread apply all bt`) prints the stacks. `./build.sh aot` keeps the symbols in `Weft.Runtime.dbg` next to the program: keep that file
  for every build you ship, and load it in gdb with `symbol-file`.
- **Windows.** Turn on Windows Error Reporting's local dumps: in the registry key `HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps` set `DumpFolder` (a folder that exists) and
  `DumpType` to `2` (a full dump), or add a key named after the executable to limit it to the game. Open the `.dmp` in Visual Studio or WinDbg with the `.pdb` files that the build kept. The Event
  Viewer (Windows Logs, Application) also lists the faulting module and offset.
- **macOS.** The crash reporter writes an `.ips` file to `~/Library/Logs/DiagnosticReports` (the Console app shows it under Crash Reports) with the stack of every thread. Keep the `.dSYM` of
  each build to read the frames.
- **CoreCLR (the editor, `dotnet run`).** `DOTNET_DbgEnableMiniDump=1` makes the runtime write a minidump of a crash, which `dotnet-dump analyze` reads (see
  [Profiling](profiling.md#where-to-look) for the other tools).

## What is deliberately not done

- **No handler for fatal signals** (`SIGSEGV`, `SIGABRT`, Windows structured exceptions). Both CoreCLR and NativeAOT own those signals (they turn a fault in managed code into a
  `NullReferenceException`), and a handler added later would see the runtime's own faults first and have to chain to it in a way that differs per runtime and OS. The OS already does the job above.
  If games turn out to need a report at the moment of the crash, a small C shim for it is possible future work.
- **No upload and no telemetry.** Files never leave the machine on their own.
- **No symbolication**, no minidump writing from the engine, no crash-time screenshot.

## Privacy

A report and a log may contain the command line, folder names (which often include the user name), the GPU's name and anything the game logs. Tell players what the files hold before they send them.

## Using it from C#

`Weft.Runtime` and the editor do all of this themselves. A program of your own does the same in a few lines:

```csharp
var recent = new RecentLogSink();                       // the lines a report quotes
Log.AddSink(recent);
CrashHandler.Install(Path.Combine(userDirectory, "crashes"), recent);   // as early as possible

using FileLogSink logFile = RunDiagnostics.Start(userDirectory, "My Game", "1.2.0");  // checks the previous run, then opens <user>/logs/<run>.log
CrashHandler.SetContext("GPU", device.Name);            // any more lines for the reports
```

`RunDiagnostics.Start` also moves the reports to `<user>/crashes`; `ScriptSession.UserDirectoryFor` gives the same `<user>` that `StartGame` mounts as `user://`. Dispose the returned sink when the run
ends cleanly: until then the run counts as one that died.
