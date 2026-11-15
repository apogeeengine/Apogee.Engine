using System;
using System.IO;
using Apogee.Content.Packages;
using Apogee.Core.Diagnostics;
using Apogee.Core.Logging;
using Apogee.Host;
using Xunit;

namespace Apogee.Host.Tests;

// Log and CrashHandler are process-wide state.
[Collection("Log")]
public sealed class RunDiagnosticsTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("apogee-diag-");

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public void TheUserDirectoryIsTheGivenOneThenTheManifestsThenOneNamedAfterTheFolder()
    {
        Assert.Equal("given", ScriptSession.UserDirectoryFor("games/Truck", "given", manifest: null));
        Assert.EndsWith(Path.Combine("Apogee", "Truck"), ScriptSession.UserDirectoryFor("games/Truck/", null, manifest: null), StringComparison.Ordinal);

        string folder = Path.Combine(_dir.FullName, "game");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, GameManifest.FileName), "<Game name=\"Big Truck\" organization=\"Acme\" version=\"1.0.0\"/>");
        GameManifest? manifest = GameManifest.Load(folder, out var problems);
        Assert.Empty(problems);
        Assert.EndsWith(Path.Combine("Acme", "Big Truck"), ScriptSession.UserDirectoryFor(folder, null, manifest), StringComparison.Ordinal);
    }

    [Fact]
    public void StartOpensTheLogInTheUserFolderAndReportsARunThatDidNotEnd()
    {
        var recent = new RecentLogSink();
        CrashHandler.Install(Path.Combine(_dir.FullName, "elsewhere"), recent);
        string user = Path.Combine(_dir.FullName, "user");
        string logs = Path.Combine(user, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "old.log"), "the dead run's last line\n");
        File.WriteAllText(Path.Combine(logs, "old.running"), "pid=2147483646\nstart=0\nlog=old.log\n");

        using (FileLogSink sink = RunDiagnostics.Start(user, "Fixture", "1.2.3"))
        {
            Log.Info("Test", "during the run");
            // Read while the sink has the file open: shared with the writer, as a user tailing the log would.
            using var stream = new FileStream(sink.Path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            Assert.Contains("during the run", reader.ReadToEnd(), StringComparison.Ordinal);
            Assert.Equal(Path.Combine(user, "logs"), Path.GetDirectoryName(sink.Path));
        }

        string report = File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(user, "crashes"))));
        Assert.Contains("the dead run's last line", report, StringComparison.Ordinal);
        Assert.Contains("Game: Fixture", report, StringComparison.Ordinal);
        Assert.Contains("Game version: 1.2.3", report, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_dir.FullName, "elsewhere")));
    }
}
