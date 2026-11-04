using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Apogee.Reflection;

namespace Apogee.Scripting.Tests;

public enum Mood
{
    Calm,
    Angry,
    VeryHappy,
}

[Flags]
public enum Traits
{
    None = 0,
    Fast = 1,
    Strong = 2,
    ToughSkin = 4,
}

/// <summary>An animal of the test zoo.</summary>
[ApogeeClass(Creatable = true)]
public partial class Animal : EngineObject
{
    public static int DestroyedCount;

    /// <summary>What it is called.</summary>
    [Property]
    public string Name { get; set; } = "animal";

    [Property]
    public float Speed { get; set; } = 1;

    [Property]
    public int Legs { get; set; } = 4;

    [Property]
    public Vector3 Position { get; set; }

    [Property]
    public Mood Mood { get; set; }

    [Property]
    public Traits Traits { get; set; }

    [Property]
    public Animal? Friend { get; set; }

    [Property]
    public string[] Tags { get; set; } = [];

    [Property]
    public int Id { get; } = 7;

    [Signal]
    public Signal<int, string> Hurt { get; } = new();

    [Signal]
    public Signal Died { get; } = new();

    [Method]
    public string Speak(string word = "...") => $"{Name}: {word}";

    [Method]
    public (int, string) Pair() => (1, "one");

    [Method]
    public Vector3 Move(Vector3 by)
    {
        Position += by;
        return Position;
    }

    [Method]
    public int Sum(int[] numbers) => numbers.Sum();

    [Method]
    public string Describe(Mood mood, Traits traits) => $"{mood}/{traits}";

    [Method]
    public static Animal Make(string name) => new() { Name = name };

    [Method]
    public void Wound(int amount) => Hurt.Emit(amount, Name);

    [Method]
    public void Kill() => Died.Emit();

    [Method]
    public void Keep() => Adopt();

    [Method]
    public void Call(ScriptFunction callback) => callback.Call([Value.Int(5), Value.String("five")], []);

    [Method]
    public double Apply(ScriptFunction callback)
    {
        var result = new Value[1];
        callback.Call([Value.Float(2)], result);
        return result[0].ToDouble();
    }

    protected override void OnDestroyed() => DestroyedCount++;
}

[ApogeeClass]
public partial class Dog : Animal
{
    [Property]
    public bool Barks { get; set; } = true;
}

[ApogeeClass(ModsBlocked = true)]
public partial class Secret : EngineObject
{
    [Method]
    public string Reveal() => "hidden";
}

[ApogeeClass(Scriptable = false)]
public partial class Internal : EngineObject
{
    [Method]
    public static Internal Create() => new();
}

[ApogeeClass]
public partial class Holder : EngineObject
{
    [Property]
    public Internal? Hidden { get; set; }

    [Property]
    public Secret? Secret { get; set; }
}

[ScriptLibrary("zoo")]
public static partial class ZooLibrary
{
    public static List<Animal> Residents { get; } = [];

    [Method]
    public static int Count() => Residents.Count;

    [Method]
    public static Animal? Find(string name) => Residents.Find(a => a.Name == name);

    [Method]
    public static int Groom(Traits? traits) => traits is { } given ? (int)given : -1;
}
