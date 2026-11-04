namespace Apogee.Graphics;

/// <summary>How an image is being used; each state means one layout and one set of pipeline stages and accesses (in a backend that has them).</summary>
public enum ImageState
{
    Undefined,
    TransferSource,
    TransferDestination,
    /// <summary>Sampled in fragment or compute shaders.</summary>
    ShaderRead,
    /// <summary>Written and read by compute (and fragment) shaders as a storage image.</summary>
    StorageReadWrite,
    ColorAttachment,
    DepthAttachment,
    /// <summary>Depth tested against but not written, and sampled.</summary>
    DepthRead,
    Present,
}

/// <summary>How a buffer is being used.</summary>
public enum BufferState
{
    Undefined,
    TransferSource,
    TransferDestination,
    /// <summary>Read by shaders (uniform or storage, through its device address).</summary>
    ShaderRead,
    /// <summary>Written (and read) by compute shaders.</summary>
    ShaderWrite,
    VertexIndexRead,
    IndirectRead,
    HostRead,
}

/// <summary>Which states write a resource.</summary>
public static class ResourceStates
{
    /// <summary>Whether a state writes the resource (two reads in a row need no barrier).</summary>
    public static bool Writes(ImageState state) =>
        state is ImageState.TransferDestination or ImageState.StorageReadWrite or ImageState.ColorAttachment or ImageState.DepthAttachment;

    public static bool Writes(BufferState state) => state is BufferState.TransferDestination or BufferState.ShaderWrite;
}
