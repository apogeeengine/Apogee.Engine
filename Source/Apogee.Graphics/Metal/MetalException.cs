namespace Apogee.Graphics.Metal;

/// <summary>A Metal call failed or the machine cannot do what the engine needs; the message says which.</summary>
public sealed class MetalException(string message) : GraphicsException(message)
{
}
