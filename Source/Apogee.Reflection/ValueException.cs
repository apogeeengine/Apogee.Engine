using System;

namespace Apogee.Reflection;

/// <summary>A value of the wrong type or out of range, or a bad call from a script. Scripts see it as an error with this message.</summary>
public sealed class ValueException : Exception
{
    public ValueException(string message)
        : base(message)
    {
    }
}
