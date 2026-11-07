using System;
using Apogee.Interop;
using Apogee.Native.RmlUi;
using Apogee.Reflection;

namespace Apogee.UI;

/// <summary>An event that reached a listener (<see cref="UiElement.Listen"/>). It is only good inside the handler: afterwards its calls do nothing.</summary>
[ApogeeClass]
public sealed unsafe partial class UiEvent : EngineObject
{
    private readonly UiSystem _system;
    private ArEvent* _native;

    internal UiEvent(UiSystem system, ArEvent* native)
    {
        _system = system;
        _native = native;
    }

    internal void Invalidate() => _native = null;

    /// <summary>The kind of event: <c>click</c>, <c>mousedown</c>, <c>keydown</c>, <c>change</c>...</summary>
    [Property]
    public string Type => _native == null ? "" : UiSystem.ElementText(0, (_, b, c) => Rml.EventType(_native, b, c));

    /// <summary>The element the event is about (the one that was hit, or that has the focus).</summary>
    [Property]
    public UiElement? Target => _native == null || Rml.EventTarget(_native) is var handle && handle == 0 ? null : new UiElement(_system, handle);

    /// <summary>The element whose listener is running.</summary>
    [Property]
    public UiElement? Current => _native == null || Rml.EventCurrent(_native) is var handle && handle == 0 ? null : new UiElement(_system, handle);

    /// <summary>1 going down (capture), 2 at the target, 4 going up.</summary>
    [Property]
    public int Phase => _native == null ? 0 : Rml.EventPhase(_native);

    /// <summary>Keeps the event from reaching the elements above (or, in the capture phase, below) this one.</summary>
    [Method]
    public void StopPropagation()
    {
        if (_native != null)
        {
            Rml.EventStopPropagation(_native);
        }
    }

    [Method]
    public bool Has(string key)
    {
        if (_native == null)
        {
            return false;
        }

        fixed (byte* k = Utf8.ToZeroTerminated(key))
        {
            return Rml.EventHasParameter(_native, k);
        }
    }

    /// <summary>A parameter as text (<c>value</c> of a <c>change</c>), or nothing.</summary>
    [Method]
    public string? GetString(string key)
    {
        if (_native == null)
        {
            return null;
        }

        fixed (byte* k = Utf8.ToZeroTerminated(key))
        {
            byte* pin = k;
            ArEvent* native = _native;
            return UiSystem.ElementTextOrNull(0, (_, b, c) => Rml.EventGetString(native, pin, b, c));
        }
    }

    /// <summary>A parameter as a number (<c>mouse_x</c>, <c>key_identifier</c>, <c>wheel_delta_y</c>), or <paramref name="fallback"/>.</summary>
    [Method]
    public double GetNumber(string key, double fallback = 0)
    {
        if (_native == null)
        {
            return fallback;
        }

        fixed (byte* k = Utf8.ToZeroTerminated(key))
        {
            return Rml.EventGetNumber(_native, k, fallback);
        }
    }
}
