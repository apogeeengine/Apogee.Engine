using System;

namespace Apogee.Content;

/// <summary>A file could not be read or holds something the engine does not support; the message says which file and why.</summary>
public class ContentException : Exception
{
    public ContentException(string message)
        : base(message)
    {
    }

    public ContentException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
