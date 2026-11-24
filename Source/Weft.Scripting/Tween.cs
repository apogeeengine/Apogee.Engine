using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Weft.Core.Logging;
using Weft.Core.Numerics;
using Weft.Native.Luau;
using Weft.Reflection;
using EcsWorld = Weft.Ecs.World;

namespace Weft.Scripting;

/// <summary>How a tween was made to end, as the runner sees it.</summary>
internal enum TweenEnd
{
    /// <summary>It ran to its end value: <c>completed</c> is emitted.</summary>
    Completed,

    /// <summary>A script (or an error, or a target that is gone) stopped it: <c>cancelled</c> is emitted.</summary>
    Cancelled,

    /// <summary>The VM, the module or the mod that started it is going away: no signal is emitted, since the handlers belong to what is being removed.</summary>
    Dropped,
}

/// <summary>
/// An animation that <c>tween.value</c>, <c>tween.property</c> and <c>tween.field</c> start: a number, a vector, a colour (a linear-RGB vector) or a rotation (slerped) moving from one value to another over some
/// seconds, with an easing curve. It plays by itself, on the game clock (or the real clock with the <c>unscaled</c> option), stepped by <see cref="LuauVm.AdvanceTime"/>. A playing tween stays alive when the script
/// drops the handle. A tween on a property or a component field makes no Luau call per frame.
/// </summary>
[WeftClass]
public sealed unsafe partial class Tween : EngineObject
{
    /// <summary>The tolerance of the comparisons with the clock, as the task scheduler's: 30 frames of 1/60 s meet half a second.</summary>
    private const double Epsilon = 1e-9;

    internal enum StateKind
    {
        Active,
        Completed,
        Cancelled,
    }

    internal enum KindOfValue
    {
        Number,
        Vector,
        Double3,
        Quat,
    }

    internal enum TargetKind
    {
        Setter,
        Property,
        Field,
    }

    // Which value a write is: the interpolated one, or exactly one of the ends.
    private const int AtProgress = 0;
    private const int AtFrom = 1;
    private const int AtTo = 2;

    private readonly Ease _ease;
    private readonly double _seconds;
    private readonly double _delay;
    private readonly int _repeats;
    private readonly bool _reverses;
    private double _delayLeft;
    private double _passTime;
    private long _pass;
    private double _progress;
    private bool _started;
    private bool _paused;

    // The values (a tween uses the pair of its kind).
    private double _fromNumber;
    private double _toNumber;
    private Vector3 _fromVector;
    private Vector3 _toVector;
    private Double3 _fromDouble;
    private Double3 _toDouble;
    private Quaternion _fromQuat;
    private Quaternion _toQuat;

    // The target (the one of its kind).
    private readonly LuauFunction? _setter;
    private readonly EngineObject? _subject;
    private readonly PropertyDef? _property;
    private readonly EcsWorld? _world;
    private readonly Weft.Reflection.Entity _entity;
    private readonly Weft.Reflection.Entity _component;
    private readonly FieldDef? _field;

    internal Tween(TweenRunner runner, ScriptOwner owner, KindOfValue kind, TargetKind target, TweenSettings settings, LuauFunction? setter, EngineObject? subject, PropertyDef? property, EcsWorld? world,
        Weft.Reflection.Entity entity, Weft.Reflection.Entity component, FieldDef? field)
    {
        Runner = runner;
        Owner = owner;
        Kind = kind;
        Target = target;
        _ease = settings.Ease;
        _seconds = settings.Seconds;
        _delay = settings.Delay;
        _delayLeft = settings.Delay;
        _repeats = settings.Repeats;
        _reverses = settings.Reverses;
        Unscaled = settings.Unscaled;
        _setter = setter;
        _subject = subject;
        _property = property;
        _world = world;
        _entity = entity;
        _component = component;
        _field = field;
    }

    /// <summary>True while the tween runs (it is not paused and has not ended).</summary>
    [Property]
    public bool Playing => State == StateKind.Active && !_paused;

    /// <summary>True while the tween is paused with <see cref="Pause"/>. An ended tween is not paused.</summary>
    [Property]
    public bool Paused => State == StateKind.Active && _paused;

    /// <summary>How far the current pass has got, from 0 to 1, in time (the easing curve is not applied). It is 1 once the tween has completed.</summary>
    [Property]
    public double Progress => _progress;

    /// <summary>The seconds one pass takes (not counting the delay or the repeats).</summary>
    [Property]
    public double Duration => _seconds;

    /// <summary>The easing curve.</summary>
    [Property]
    public Ease Ease => _ease;

    /// <summary>Emitted once when the tween ends by reaching its last value, after that value is written. A tween that repeats forever never emits it.</summary>
    [Signal]
    public Signal Completed { get; } = new();

    /// <summary>Emitted once when the tween ends early: <see cref="Cancel"/>, a setter that raised an error, or a target that is gone (a destroyed object, a dead entity, a removed component).</summary>
    [Signal]
    public Signal Cancelled { get; } = new();

    internal TweenRunner Runner { get; }

    internal ScriptOwner Owner { get; }

    internal KindOfValue Kind { get; }

    internal TargetKind Target { get; }

    internal bool Unscaled { get; }

    internal StateKind State { get; private set; } = StateKind.Active;

    /// <summary>The module scope the tween was made in (or by a task of), whose reload cancels it.</summary>
    internal ReloadScope? Scope { get; set; }

    /// <summary>The registry slot that keeps the userdata alive while the tween plays.</summary>
    internal int Reference { get; set; } = Lua.Noref;

    /// <summary>Set for a tween made while the tasks of this step ran: its time starts with the next step.</summary>
    internal bool SkipStep { get; set; }

    /// <summary>The tasks that wait for the end of the tween.</summary>
    internal List<ScriptScheduler.ParkedTask>? Waiters { get; set; }

    /// <summary>The function of a <c>tween.value</c>, which the runner lets go of at the end.</summary>
    internal LuauFunction? Setter => _setter;

    /// <summary>Sets the ends of a number tween.</summary>
    internal void SetNumbers(double from, double to)
    {
        _fromNumber = from;
        _toNumber = to;
    }

    internal void SetVectors(Vector3 from, Vector3 to)
    {
        _fromVector = from;
        _toVector = to;
    }

    internal void SetQuats(Quaternion from, Quaternion to)
    {
        _fromQuat = from;
        _toQuat = to;
    }

    /// <summary>Sets the end of a tween on a <c>Double3</c> field; the start is read from the component when the delay ends.</summary>
    internal void SetDoubleEnd(Double3 to) => _toDouble = to;

    internal bool ReadsItsStart => Target != TargetKind.Setter;

    /// <summary>True when the tween writes whole numbers (an integer property or field): the interpolated value is rounded.</summary>
    internal bool Integer { get; set; }

    /// <summary>Stops the tween where it is: the value stays what the last step wrote, and <c>cancelled</c> is emitted. Does nothing once the tween has ended.</summary>
    [Method]
    public void Cancel() => Runner.Finish(this, TweenEnd.Cancelled);

    /// <summary>
    /// Jumps to the end value (the last pass's: <c>to</c>, or <c>from</c> when the tween reverses an odd number of times), writes it and emits <c>completed</c>. For a tween that repeats forever it stops at <c>to</c>.
    /// Does nothing once the tween has ended.
    /// </summary>
    [Method]
    public void Complete()
    {
        if (State != StateKind.Active || !Prepare())
        {
            return;
        }

        _progress = 1;
        if (Put(FinalIsFrom ? AtFrom : AtTo, 1))
        {
            Runner.Finish(this, TweenEnd.Completed);
        }
    }

    /// <summary>Holds the tween, delay included, until <see cref="Resume"/>. A paused tween is still alive: it keeps the object it animates and does not end.</summary>
    [Method]
    public void Pause() => _paused = true;

    /// <summary>Lets a paused tween go on from where it stopped.</summary>
    [Method]
    public void Resume() => _paused = false;

    private bool FinalIsFrom => _reverses && _repeats >= 0 && (_repeats & 1) == 1;

    // ---- Stepping -----------------------------------------------------------------------------------------------------------------------

    /// <summary>Moves the tween by <paramref name="seconds"/> on its clock: spends the delay, reads the start, then writes the value of the time it has reached.</summary>
    internal void Step(double seconds)
    {
        double remaining = seconds;
        if (_delayLeft > 0)
        {
            if (remaining < _delayLeft - Epsilon)
            {
                _delayLeft -= remaining;
                return;
            }

            remaining = Math.Max(remaining - _delayLeft, 0);
            _delayLeft = 0;
        }

        if (!Prepare())
        {
            return;
        }

        if (_seconds <= Epsilon)
        {
            // No duration: the first step after the delay ends it.
            _progress = 1;
            if (Put(FinalIsFrom ? AtFrom : AtTo, 1))
            {
                Runner.Finish(this, TweenEnd.Completed);
            }

            return;
        }

        _passTime += remaining;
        bool forever = _repeats < 0;
        while (_passTime >= _seconds - Epsilon)
        {
            if (!forever && _pass >= _repeats)
            {
                _progress = 1;
                if (Put(FinalIsFrom ? AtFrom : AtTo, 1))
                {
                    Runner.Finish(this, TweenEnd.Completed);
                }

                return;
            }

            _passTime = Math.Max(_passTime - _seconds, 0);
            _pass++;
            if (forever && _passTime >= _seconds)
            {
                // A frame that covers many passes (a big time scale): only the parity of the pass counts.
                long skipped = (long)(_passTime / _seconds);
                _pass += skipped;
                _passTime -= skipped * _seconds;
            }
        }

        _progress = Math.Clamp(_passTime / _seconds, 0, 1);
        double along = _reverses && (_pass & 1) == 1 ? 1 - _progress : _progress;
        _ = Put(AtProgress, Easing.Apply(_ease, along));
    }

    /// <summary>Checks that the target is still there and reads the start value the first time. Ends the tween and returns false when it cannot go on.</summary>
    private bool Prepare()
    {
        if (TargetIsGone())
        {
            Runner.Finish(this, TweenEnd.Cancelled);
            return false;
        }

        if (_started)
        {
            return true;
        }

        _started = true;
        if (!ReadsItsStart)
        {
            return true;
        }

        try
        {
            ReadStart();
            return true;
        }
        catch (ValueException e)
        {
            Fail(e.Message);
            return false;
        }
    }

    private bool TargetIsGone() => Target switch
    {
        TargetKind.Property => _subject!.IsDestroyed,
        TargetKind.Field => _world!.IsDestroyed || !_world.IsAlive(_entity) || !_world.Has(_entity, _component),
        _ => false,
    };

    private void ReadStart()
    {
        if (Target == TargetKind.Property)
        {
            Value value = _property!.Getter(_subject!);
            switch (Kind)
            {
                case KindOfValue.Number: _fromNumber = value.ToDouble(); break;
                case KindOfValue.Vector: _fromVector = value.ToVector3(); break;
                default: _fromQuat = value.ToQuaternion(); break;
            }

            return;
        }

        byte* data = _world!.GetPointer(_entity, _component);
        if (data is null)
        {
            throw new ValueException("the component is gone");
        }

        if (Kind == KindOfValue.Double3)
        {
            _fromDouble = Unsafe.ReadUnaligned<Double3>(data + _field!.Offset);
            return;
        }

        Value read = FieldAccess.Read(data, _field!);
        switch (Kind)
        {
            case KindOfValue.Number: _fromNumber = read.ToDouble(); break;
            case KindOfValue.Vector: _fromVector = read.ToVector3(); break;
            default: _fromQuat = read.ToQuaternion(); break;
        }
    }

    // ---- Writing ------------------------------------------------------------------------------------------------------------------------

    /// <summary>The value for <paramref name="mode"/> (with the eased progress <paramref name="e"/> for <see cref="AtProgress"/>), as a script value.</summary>
    private Value ValueFor(int mode, double e)
    {
        switch (Kind)
        {
            case KindOfValue.Number:
                double number = mode == AtFrom ? _fromNumber : mode == AtTo ? _toNumber : _fromNumber + ((_toNumber - _fromNumber) * e);
                return Integer ? Value.Int((long)Math.Round(number)) : Value.Float(number);
            case KindOfValue.Vector:
                return Value.Vector(mode == AtFrom ? _fromVector : mode == AtTo ? _toVector : Vector3.Lerp(_fromVector, _toVector, (float)e));
            default:
                return Value.Quat(mode == AtFrom ? _fromQuat : mode == AtTo ? _toQuat : Quaternion.Slerp(_fromQuat, _toQuat, (float)e));
        }
    }

    /// <summary>Writes the value; a failure (a setter that raised an error, a value out of range) ends the tween as cancelled and is logged for its owner.</summary>
    private bool Put(int mode, double e)
    {
        try
        {
            switch (Target)
            {
                case TargetKind.Setter:
                    _setter!.Call([ValueFor(mode, e)], []);
                    break;
                case TargetKind.Property:
                    Value value = ValueFor(mode, e);
                    using (ScriptCaller.Enter(Owner))
                    {
                        _property!.Setter!(_subject!, in value);
                    }

                    break;
                default:
                    PutField(mode, e);
                    break;
            }

            return true;
        }
        catch (Exception x) when (x is ScriptException or ValueException)
        {
            Fail(x.Message);
            return false;
        }
    }

    /// <summary>Writes the component's field in place and tells the world, so that observers and the transform propagation see it.</summary>
    private void PutField(int mode, double e)
    {
        byte* data = _world!.GetPointer(_entity, _component);
        if (data is null)
        {
            throw new ValueException("the component is gone");
        }

        if (Kind == KindOfValue.Double3)
        {
            Double3 position = mode == AtFrom ? _fromDouble : mode == AtTo ? _toDouble : Double3.Lerp(_fromDouble, _toDouble, e);
            Unsafe.WriteUnaligned(data + _field!.Offset, position);
        }
        else
        {
            FieldAccess.Write(data, _field!, ValueFor(mode, e));
        }

        _world.Modified(_entity, _component);
    }

    private void Fail(string message)
    {
        Log.Error("Script:" + Owner.Id, "tween failed: " + message);
        Runner.Finish(this, TweenEnd.Cancelled);
    }

    // ---- Called by the runner ---------------------------------------------------------------------------------------------------------------

    internal bool IsPaused => _paused;

    internal void MarkEnded(TweenEnd how) => State = how == TweenEnd.Completed ? StateKind.Completed : StateKind.Cancelled;

    internal void EmitEnd(TweenEnd how)
    {
        if (how == TweenEnd.Completed)
        {
            Completed.Emit();
        }
        else if (how == TweenEnd.Cancelled)
        {
            Cancelled.Emit();
        }
    }
}

/// <summary>The options of a tween, as <c>tween.value</c> and the others read them from the optional table.</summary>
internal readonly record struct TweenSettings(double Seconds, Ease Ease, double Delay, int Repeats, bool Reverses, bool Unscaled);
