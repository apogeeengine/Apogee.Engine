using System;

namespace Apogee.Graphics;

/// <summary>A graphics call failed or the machine cannot do what the engine needs (no usable GPU, a missing feature); the message says which. Each backend has its own subclass.</summary>
public class GraphicsException(string message) : Exception(message)
{
}
