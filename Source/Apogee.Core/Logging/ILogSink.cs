namespace Apogee.Core.Logging;

/// <summary>Receives log entries. <see cref="Write"/> can be called from any thread.</summary>
public interface ILogSink
{
    void Write(in LogEntry entry);
}
