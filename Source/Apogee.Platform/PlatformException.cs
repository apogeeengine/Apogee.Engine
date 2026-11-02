using System;

namespace Apogee.Platform;

/// <summary>An SDL call failed; the message carries SDL's own error text.</summary>
public sealed class PlatformException : Exception
{
    public PlatformException(string message)
        : base(message)
    {
    }
}
