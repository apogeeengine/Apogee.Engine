using System;
using System.Threading;

namespace Weft.Core.Logging;

/// <summary>Keeps the last lines that were logged, in memory: what a crash report shows of the run's end.</summary>
public sealed class RecentLogSink : ILogSink
{
    private readonly Lock _lock = new();
    private readonly string[] _lines;
    private int _next;
    private int _count;

    /// <summary>A sink that keeps the last <paramref name="capacity"/> lines.</summary>
    public RecentLogSink(int capacity = 200)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _lines = new string[capacity];
    }

    public void Write(in LogEntry entry)
    {
        string line = entry.ToLine();
        lock (_lock)
        {
            _lines[_next] = line;
            _next = (_next + 1) % _lines.Length;
            _count = Math.Min(_count + 1, _lines.Length);
        }
    }

    /// <summary>The kept lines, the oldest first.</summary>
    public string[] Snapshot()
    {
        lock (_lock)
        {
            var result = new string[_count];
            int start = (_next - _count + _lines.Length) % _lines.Length;
            for (int i = 0; i < _count; i++)
            {
                result[i] = _lines[(start + i) % _lines.Length];
            }

            return result;
        }
    }
}
