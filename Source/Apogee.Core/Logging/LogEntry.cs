using System;

namespace Apogee.Core.Logging;

public readonly record struct LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message);
