using System;
using System.Collections.Generic;
using System.Numerics;
using Apogee.Native.Sdl;
using Apogee.Platform;
using Apogee.Reflection;

namespace Apogee.Input;

/// <summary>
/// Turns the player's input into the actions of an <see cref="InputActionMap"/> that fire in the active contexts, and says how far analog actions are pushed.
/// Of the bindings on an input whose modifiers are all held, the one with the most modifiers wins, so Shift+Tab isn't also Tab, and a key still fires while running
/// with Shift. A key with a hold binding fires its press on a quick release instead; a key with a double tap waits a moment for the second tap before firing its press.
/// <see cref="InputDevices"/> feeds it from SDL; <see cref="Press"/>, <see cref="Release"/>, <see cref="Axis"/> and <see cref="Poll"/> feed it by hand, with explicit times
/// (seconds, any origin), for tests and replays. Call <see cref="Update"/> once a frame to raise <see cref="ActionFired"/>, and <see cref="EndFrame"/> after the frame's logic ran.
/// </summary>
[ApogeeClass(Creatable = true)]
public sealed partial class InputActionRouter : EngineObject
{
    /// <summary>How long a key is held before its hold binding fires.</summary>
    public const double HoldSeconds = 0.4;

    /// <summary>How long a second tap may take.</summary>
    public const double DoubleTapSeconds = 0.3;

    /// <summary>Stick and trigger travel that is ignored at rest; the rest scales to 0..1.</summary>
    public const float DeadZone = 0.2f;

    /// <summary>An axis past this acts as a pressed button, and is released below <see cref="ReleaseAt"/>.</summary>
    public const float PressAt = 0.5f;

    public const float ReleaseAt = 0.4f;

    private readonly record struct Candidate(string Action, Trigger Trigger);

    // The bindings of one input with the same modifiers: what a press of it may fire.
    private sealed class Pending(string input, Candidate[] group, double time)
    {
        public string Input { get; } = input;

        public Candidate[] Group { get; } = group;

        public double Time { get; set; } = time;

        public bool Done { get; set; } // its hold fired, or it was the second tap: its release fires nothing

        public bool Has(Trigger trigger) => Array.Exists(Group, c => c.Trigger == trigger);

        public void Fire(Trigger trigger, List<string> fired)
        {
            foreach (Candidate c in Group)
            {
                if (c.Trigger == trigger)
                {
                    fired.Add(c.Action);
                }
            }
        }
    }

    private InputActionMap? _map;
    private string[] _contexts = [];
    private readonly HashSet<string> _down = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _axes = new(StringComparer.Ordinal);
    private readonly List<Pending> _pending = [];
    private readonly List<Pending> _taps = [];
    private readonly List<string> _fired = [];
    private readonly List<string> _captured = [];
    private bool _listening;

    /// <summary>The actions to route. The router takes the map over: it lives as long as the router does.</summary>
    [Property]
    public InputActionMap? Map
    {
        get => _map;
        set
        {
            _map = value;
            value?.Adopt();
        }
    }

    /// <summary>The active contexts (a situation); none ignores every input. A game sets them every frame from what the player is doing.</summary>
    [Property]
    public string[] Contexts
    {
        get => _contexts;
        set
        {
            if (value.AsSpan().SequenceEqual(_contexts))
            {
                return;
            }

            _contexts = [.. value];
            _pending.Clear();
            _taps.Clear();
        }
    }

    /// <summary>True when the last input pressed was on a gamepad (the labels then name its buttons).</summary>
    [Property]
    public bool IsGamepadLast { get; private set; }

    [Signal]
    public Signal<string> ActionFired { get; } = new();

    /// <summary>The binding text of what was pressed while <see cref="Listen"/> was on.</summary>
    [Signal]
    public Signal<string> Captured { get; } = new();

    [Method]
    public bool InContext(string action)
    {
        InputAction? found = _map?.Find(action);
        return found is not null && Array.Exists(found.ContextArray, c => Array.IndexOf(_contexts, c) >= 0);
    }

    /// <summary>
    /// Rebinding: the next key (with the modifiers held), mouse button, wheel step, gamepad button or stick or trigger pushed past half way is not routed; <see cref="Captured"/>
    /// gets its binding text (<c>Shift+F</c>, <c>Joy LY-</c>) at the next <see cref="Update"/>, and listening ends. A modifier key alone does not count: it waits for the key it goes with.
    /// </summary>
    [Method]
    public void Listen() => _listening = true;

    [Method]
    public void StopListening() => _listening = false;

    // ---- Feeding it -------------------------------------------------------------------------------------------------------------

    private Candidate[] Candidates(string input, Modifiers held)
    {
        var group = new List<Candidate>();
        if (_map is null)
        {
            return [];
        }

        int best = -1;
        foreach (InputAction action in _map.Actions)
        {
            if (action.Analog || !InContext(action.Id))
            {
                continue;
            }

            foreach (Binding b in action.Bindings)
            {
                if (b.Input != input || (b.Modifiers & ~held) != 0)
                {
                    continue;
                }

                int count = BitOperations.PopCount((uint)b.Modifiers);
                if (count < best)
                {
                    continue;
                }

                if (count > best)
                {
                    group.Clear();
                }

                best = count;
                group.Add(new Candidate(action.Id, b.Trigger));
            }
        }

        return [.. group];
    }

    /// <summary>An input goes down. <paramref name="modifiers"/> are the Ctrl, Shift and Alt held with it, not counting the input itself.</summary>
    [Method]
    public void Press(string input, Modifiers modifiers, double time)
    {
        if (!_down.Add(input))
        {
            return;
        }

        IsGamepadLast = Binding.DeviceOf(input) == InputDevice.Gamepad;
        if (_listening && !KeyNames.IsModifier(input))
        {
            _listening = false;
            _captured.Add(new Binding(input, Binding.DeviceOf(input) == InputDevice.Gamepad ? Modifiers.None : modifiers, Trigger.Press).ToString());
            return;
        }

        var pending = new Pending(input, Candidates(input, modifiers), time);
        if (pending.Group.Length == 0)
        {
            return;
        }

        if (!pending.Has(Trigger.Hold) && !pending.Has(Trigger.DoubleTap))
        {
            pending.Fire(Trigger.Press, _fired);
            return;
        }

        for (int i = 0; i < _taps.Count; i++)
        {
            if (_taps[i].Group.AsSpan().SequenceEqual(pending.Group) && time - _taps[i].Time <= DoubleTapSeconds)
            {
                _taps.RemoveAt(i);
                pending.Fire(Trigger.DoubleTap, _fired);
                pending.Done = true;
                break;
            }
        }

        _pending.Add(pending);
    }

    /// <summary>An input goes up.</summary>
    [Method]
    public void Release(string input, double time)
    {
        if (!_down.Remove(input))
        {
            return;
        }

        for (int i = 0; i < _pending.Count; i++)
        {
            if (_pending[i].Input != input)
            {
                continue;
            }

            Pending p = _pending[i];
            _pending.RemoveAt(i--);
            if (p.Done)
            {
                continue;
            }

            if (p.Has(Trigger.DoubleTap))
            {
                p.Time = time;
                _taps.Add(p);
            }
            else
            {
                p.Fire(Trigger.Press, _fired);
            }
        }
    }

    private void SetAxis(string input, float value, double time)
    {
        _axes[input] = value;
        if (value >= PressAt)
        {
            Press(input, Modifiers.None, time);
        }
        else if (value < ReleaseAt)
        {
            Release(input, time);
        }
    }

    /// <summary>
    /// An axis moves: <c>Joy LX</c>, <c>Joy LY</c>, <c>Joy RX</c>, <c>Joy RY</c> (-1..1), <c>Joy LT</c>, <c>Joy RT</c> (0..1), or the mouse's motion in pixels since the last event
    /// (<c>Mouse X</c>, <c>Mouse Y</c>), which adds up until <see cref="EndFrame"/>.
    /// </summary>
    [Method]
    public void Axis(string axis, float value, double time)
    {
        if (Binding.DeviceOf(axis) == InputDevice.Mouse)
        {
            _axes[axis + "+"] = _axes.GetValueOrDefault(axis + "+") + Math.Max(value, 0f);
            _axes[axis + "-"] = _axes.GetValueOrDefault(axis + "-") + Math.Max(-value, 0f);
        }
        else if (Binding.IsTrigger(axis))
        {
            SetAxis(axis, value, time);
        }
        else
        {
            SetAxis(axis + "+", Math.Max(value, 0f), time);
            SetAxis(axis + "-", Math.Max(-value, 0f), time);
        }
    }

    /// <summary>The frame's logic has read the mouse's motion: it starts again from zero.</summary>
    [Method]
    public void EndFrame()
    {
        foreach (string key in new List<string>(_axes.Keys))
        {
            if (Binding.DeviceOf(key) == InputDevice.Mouse)
            {
                _axes[key] = 0;
            }
        }
    }

    /// <summary>Everything let go and forgotten: the window lost focus, or the game is changing screens.</summary>
    [Method]
    public void ReleaseAll()
    {
        _down.Clear();
        _axes.Clear();
        _pending.Clear();
        _taps.Clear();
    }

    /// <summary>
    /// Fires what time has made due (a hold that has been held long enough, a tap that no second tap followed) and returns the actions fired since the last call, in order.
    /// An action bound to several inputs fires once for each.
    /// </summary>
    [Method]
    public string[] Poll(double time)
    {
        foreach (Pending p in _pending)
        {
            if (!p.Done && p.Has(Trigger.Hold) && time - p.Time >= HoldSeconds)
            {
                p.Fire(Trigger.Hold, _fired);
                p.Done = true;
            }
        }

        for (int i = 0; i < _taps.Count; i++)
        {
            if (time - _taps[i].Time > DoubleTapSeconds)
            {
                _taps[i].Fire(Trigger.Press, _fired);
                _taps.RemoveAt(i--);
            }
        }

        string[] result = [.. _fired];
        _fired.Clear();
        return result;
    }

    /// <summary><see cref="Poll"/>, and raises <see cref="ActionFired"/> for each action and <see cref="Captured"/> for a binding that was listened for. Once a frame, before the logic that reads the actions.</summary>
    [Method]
    public void Update(double time)
    {
        foreach (string action in Poll(time))
        {
            ActionFired.Emit(action);
        }

        if (_captured.Count > 0)
        {
            string[] captured = [.. _captured];
            _captured.Clear();
            foreach (string binding in captured)
            {
                Captured.Emit(binding);
            }
        }
    }

    /// <summary>The inputs that are down, sorted.</summary>
    [Method]
    public string[] GetDown()
    {
        string[] down = [.. _down];
        Array.Sort(down, StringComparer.Ordinal);
        return down;
    }

    // ---- Reading it -------------------------------------------------------------------------------------------------------------

    private float StrengthOf(string input)
    {
        if (!Binding.IsAxis(input))
        {
            return _down.Contains(input) ? 1f : 0f;
        }

        float v = _axes.GetValueOrDefault(input);
        if (Binding.DeviceOf(input) == InputDevice.Mouse)
        {
            return v;
        }

        return v <= DeadZone ? 0f : Math.Min(1f, (v - DeadZone) / (1f - DeadZone));
    }

    /// <summary>How far an action is pushed: 0 to 1 for keys, buttons, sticks and triggers (the largest of its bindings), more for the mouse's motion; 0 outside its contexts.</summary>
    [Method]
    public float GetStrength(string action)
    {
        InputAction? found = InContext(action) ? _map!.Find(action) : null;
        float strength = 0;
        if (found is not null)
        {
            foreach (Binding b in found.Bindings)
            {
                strength = Math.Max(strength, StrengthOf(b.Input));
            }
        }

        return strength;
    }

    [Method]
    public bool IsHeld(string action) => GetStrength(action) >= 0.5f;

    // ---- Labels -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// What to press for an action, as the player's keyboard names it (<c>Z</c> on AZERTY for a <c>W</c> binding), or its gamepad button when the gamepad was used last;
    /// an en dash when it is unbound.
    /// </summary>
    [Method]
    public string GetLabel(string action)
    {
        InputAction? found = _map?.Find(action);
        if (found is null || found.Bindings.Count == 0)
        {
            return "–";
        }

        foreach (Binding b in found.Bindings)
        {
            if ((Binding.DeviceOf(b.Input) == InputDevice.Gamepad) == IsGamepadLast)
            {
                return LabelBinding(b.ToString());
            }
        }

        return LabelBinding(found.Bindings[0].ToString());
    }

    /// <summary>A binding as the player reads it: <c>Hold Ctrl+Left mouse</c>, <c>Pad A</c>, and a key by what it types on their keyboard layout.</summary>
    [Method]
    public static string LabelBinding(string text) => Labels.Of(text);
}

internal static unsafe class Labels
{
    public static string Of(string text)
    {
        if (!Binding.TryParse(text, out Binding b, out _))
        {
            return text;
        }

        string name;
        switch (Binding.DeviceOf(b.Input))
        {
            case InputDevice.Mouse:
                string button = b.Input[6..];
                name = button switch
                {
                    "Left" or "Right" or "Middle" => button + " mouse",
                    "Wheel Up" => "Wheel up",
                    "Wheel Down" => "Wheel down",
                    _ => "Mouse " + button.ToLowerInvariant(),
                };
                break;
            case InputDevice.Gamepad:
                name = "Pad " + b.Input[4..];
                break;
            default:
                name = KeyLabel(b.Input);
                break;
        }

        return Binding.Prefix(b.Trigger) + Binding.Prefix(b.Modifiers) + name;
    }

    // Without SDL's video system (a headless run) there is no layout to translate to.
    private static string KeyLabel(string input)
    {
        if (KeyNames.IsModifier(input) || !PlatformHost.IsInitialized || !KeyNames.TryParse(input, out Scancode key, out _))
        {
            return input;
        }

        uint code = SDL.GetKeyFromScancode(key, 0, false);
        if (code == 0)
        {
            return input;
        }

        // A key that types a sign goes by it ("&", "-", "ù"), not by its name.
        if (code > ' ' && code < 0x20000000)
        {
            return char.ConvertFromUtf32((int)code).ToUpperInvariant();
        }

        return Apogee.Interop.Utf8.FromPointer(SDL.GetKeyName(code)) is { Length: > 0 } sdlName ? sdlName : input;
    }
}
