namespace Apogee.Physics;

/// <summary>How a body moves.</summary>
public enum MotionKind
{
    /// <summary>It never moves (ground, walls); teleporting it is allowed but slow, and it does not push anything.</summary>
    Static,

    /// <summary>The game moves it: it follows its entity's transform and pushes dynamic bodies (doors, lifts, animated machines).</summary>
    Kinematic,

    /// <summary>The simulation moves it, and its entity's transform follows.</summary>
    Dynamic,
}
