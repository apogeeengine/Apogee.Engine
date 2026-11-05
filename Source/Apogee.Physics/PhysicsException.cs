using System;

namespace Apogee.Physics;

/// <summary>The physics library could not do what was asked (it did not load, or Jolt refused a shape).</summary>
public sealed class PhysicsException : Exception
{
    public PhysicsException(string message)
        : base(message)
    {
    }
}
