using System;
using System.Collections.Generic;
using System.Numerics;
using Apogee.Reflection;

namespace Apogee.Editor.Tests;

/// <summary>An enum a probe's property names.</summary>
public enum ProbeGait
{
    Idle,
    Walk,
    Run,
}

/// <summary>A flags enum a probe's property names.</summary>
[Flags]
public enum ProbeParts
{
    None = 0,
    Head = 1,
    Arm = 2,
    Leg = 4,
}

/// <summary>The base class of <see cref="InspectProbe"/>: what a derived class inherits, and hides.</summary>
[ApogeeClass]
public partial class InspectProbeBase : EngineObject
{
    [Property(Category = "Identity")]
    public string Title { get; set; } = "base title";

    [Property]
    public int Shared { get; set; } = 1;

    [Property]
    public int Extra { get; set; }
}

/// <summary>An object with a property of each kind the object inspector draws, and some that misbehave.</summary>
[ApogeeClass]
public partial class InspectProbe : InspectProbeBase
{
    private int _limited;
    private float _ticks;

    [Property]
    public bool Flag { get; set; }

    [Property(Min = 0, Max = 10, Step = 2)]
    public int Count { get; set; } = 3;

    [Property(Min = 0, Max = 1)]
    public float Level { get; set; } = 0.5f;

    [Property]
    public string Name { get; set; } = "probe";

    [Property]
    public string Notes { get; set; } = "";

    [Property]
    public Vector3 Position { get; set; }

    [Property(Color = true)]
    public Vector3 Tint { get; set; } = Vector3.One;

    [Property]
    public Quaternion Turn { get; set; } = Quaternion.Identity;

    [Property]
    public ProbeGait Gait { get; set; }

    [Property]
    public ProbeParts Parts { get; set; } = ProbeParts.Head;

    [Property]
    public Entity Target { get; set; }

    [Property]
    public InspectProbeChild? Child { get; set; }

    [Property]
    public string[] Tags { get; set; } = [];

    [Property]
    public List<int> Numbers { get; set; } = [];

    [Property]
    public List<InspectProbeChild> Children { get; set; } = [];

    /// <summary>The id: it cannot be set.</summary>
    [Property]
    public int Id { get; } = 7;

    [Property(ReadOnly = true)]
    public float Locked { get; set; } = 2;

    [Property(Hidden = true)]
    public int Secret { get; set; } = 9;

    [Property(Category = "Identity")]
    public string Title2 { get; set; } = "second";

    /// <summary>A setter that refuses values above 5.</summary>
    [Property]
    public int Limited
    {
        get => _limited;
        set => _limited = value > 5 ? throw new ValueException("too big: at most 5") : value;
    }

    /// <summary>A getter that throws.</summary>
    [Property]
    public int Broken => _limited > -1 ? throw new InvalidOperationException("the getter blew up") : 0;

    [Property]
    public int? Maybe { get; set; }

    [Property]
    public InspectProbeChild? Missing { get; set; }

    /// <summary>A value that changes by itself every time it is read.</summary>
    [Property]
    public float Ticking => ++_ticks;
}

/// <summary>A small object that probes hold, and that holds another (a cycle is easy to make).</summary>
[ApogeeClass]
public partial class InspectProbeChild : EngineObject
{
    [Property]
    public string Label { get; set; } = "child";

    [Property]
    public int Amount { get; set; }

    [Property]
    public InspectProbeChild? Next { get; set; }
}

/// <summary>A class with a property that is a list of the base class (to test a list of objects).</summary>
[ApogeeClass]
public partial class InspectProbeHolder : EngineObject
{
    [Property]
    public List<InspectProbeChild> Items { get; set; } = [];

    [Property]
    public List<string> Words { get; set; } = [];

    [Property]
    public List<ProbeGait> Gaits { get; set; } = [];
}
