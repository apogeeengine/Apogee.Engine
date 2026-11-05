using System;

namespace Apogee.Rendering.Graph;

/// <summary>The graph is inconsistent: a pass reads something nothing writes, uses a resource two incompatible ways, or has mismatched attachments.</summary>
public sealed class RenderGraphException : Exception
{
    public RenderGraphException(string message)
        : base(message)
    {
    }
}
