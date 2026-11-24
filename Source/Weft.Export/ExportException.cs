using System;

namespace Weft.Export;

/// <summary>An export could not be done; the message says what is wrong and, when there is something to do about it, what.</summary>
public sealed class ExportException : Exception
{
    /// <summary>Makes the exception.</summary>
    public ExportException(string message)
        : base(message)
    {
    }

    /// <summary>Makes the exception for a failure that came from <paramref name="inner"/>.</summary>
    public ExportException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
