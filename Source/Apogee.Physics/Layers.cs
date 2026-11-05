namespace Apogee.Physics;

/// <summary>
/// Which bodies collide with which. A body has <see cref="BodyDesc.Layers"/>, the collision layers it belongs to (a bit each), and <see cref="BodyDesc.Mask"/>, the layers it collides with;
/// two bodies collide when each is in a layer the other's mask holds. There are 14 layers for the game (bits 0 to 13). The native layer keeps two more bits of its own to sort bodies into the
/// broad phase (static, sensor, moving); static bodies never collide with each other. See <c>Native/jolt/apogee_jolt.h</c>.
/// </summary>
public static class PhysicsLayers
{
    /// <summary>How many layers a game can use.</summary>
    public const int Count = 14;

    /// <summary>Every layer of the game: the default mask.</summary>
    public const uint All = (1u << Count) - 1;

    /// <summary>The layer a body is in unless it says otherwise.</summary>
    public const uint Default = 1;
}
