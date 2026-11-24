using System;
using System.Numerics;
using Weft.Native.Luau;
using Weft.Reflection;
using EcsWorld = Weft.Ecs.World;
using LuaType = Weft.Native.Luau.Type;

namespace Weft.Scripting;

public sealed unsafe partial class LuauVm
{
    /// <summary>Registers the <c>tween</c> library and <c>Tween:wait</c>. They come with their definitions.</summary>
    private static void RegisterTweenNatives()
    {
        ScriptNatives.RegisterLibrary("tween", "value", &TweenValueBody, "<T>(from: T, to: T, seconds: number, set: (T) -> (), options: TweenOptions?) -> Tween",
            "Animates a number, a vector (also a colour: linear RGB, as Light.color) or a Quat (slerped) from `from` to `to` in that many seconds, calling `set` with the value each step. `from` and `to` must be the same kind. " +
            "The tween plays by itself, even if the script drops the handle. Nothing is set at once: the first call is in the next step, and the last one is exactly `to`.");
        ScriptNatives.RegisterLibrary("tween", "property", &TweenPropertyBody, "(object: any, name: string, to: number | vector | Quat, seconds: number, options: TweenOptions?) -> Tween",
            "Animates a property of an engine object (a number, vector or Quat one that can be set) from its value when the delay ends to `to`. No Luau runs per step. The tween is cancelled if the object is destroyed.");
        ScriptNatives.RegisterLibrary("tween", "field", &TweenFieldBody, "(world: World, entity: number, component: number, field: string, to: number | vector | Quat, seconds: number, options: TweenOptions?) -> Tween",
            "Animates a field of an entity's component (a number, vector, Quat, or a world position) from its value when the delay ends to `to`, writing it in place and telling the world it changed. No Luau runs per step. " +
            "The tween is cancelled if the entity dies or loses the component.");
        ScriptNatives.RegisterLibrary("tween", "ease", &TweenEaseBody, "(ease: Ease, t: number) -> number",
            "The easing curve at time t (0 to 1, clamped): 0 at 0 and 1 at 1, and the eased progress in between (curves such as back and elastic overshoot).");
        ScriptNatives.Register("Tween", "wait", &TweenWaitBody, "function wait(self): boolean");
    }

    // ---- Reading the arguments -----------------------------------------------------------------------------------------------------------------

    private static bool TryValueKind(State* L, int index, out Tween.KindOfValue kind)
    {
        if (Stack.Is(L, index, LuaType.Tnumber))
        {
            kind = Tween.KindOfValue.Number;
        }
        else if (Stack.Is(L, index, LuaType.Tvector))
        {
            kind = Tween.KindOfValue.Vector;
        }
        else if (IsQuat(L, index))
        {
            kind = Tween.KindOfValue.Quat;
        }
        else
        {
            kind = default;
            return false;
        }

        return true;
    }

    private static string KindName(Tween.KindOfValue kind) => kind switch
    {
        Tween.KindOfValue.Number => "number",
        Tween.KindOfValue.Vector => "vector",
        Tween.KindOfValue.Double3 => "vector",
        _ => "Quat",
    };

    private static double TweenSeconds(State* L, int index, string function)
    {
        if (!Stack.Is(L, index, LuaType.Tnumber))
        {
            throw new ValueException($"bad argument #{index} to '{function}' (number expected, got {Stack.TypeName(L, index)})");
        }

        double seconds = Lua.Tonumberx(L, index, null);
        return double.IsFinite(seconds) ? Math.Max(seconds, 0) : throw new ValueException($"bad argument #{index} to '{function}' (the time must be a finite number)");
    }

    /// <summary>Reads the number, vector or Quat at <paramref name="index"/> that must be of <paramref name="kind"/>; the one it is returns in the matching out value.</summary>
    private static void ReadEnd(State* L, int index, Tween.KindOfValue kind, string function, out double number, out Vector3 vector, out Quaternion quat)
    {
        number = 0;
        vector = default;
        quat = Quaternion.Identity;
        bool right = TryValueKind(L, index, out Tween.KindOfValue given) && (given == kind || (kind == Tween.KindOfValue.Double3 && given == Tween.KindOfValue.Vector));
        if (!right)
        {
            throw new ValueException($"bad argument #{index} to '{function}' ({KindName(kind)} expected, got {Stack.TypeName(L, index)})");
        }

        switch (kind)
        {
            case Tween.KindOfValue.Number:
                number = Lua.Tonumberx(L, index, null);
                if (!double.IsFinite(number))
                {
                    throw new ValueException($"bad argument #{index} to '{function}' (the value must be a finite number)");
                }

                break;
            case Tween.KindOfValue.Quat:
                quat = ReadQuat(L, index);
                break;
            default:
                vector = Stack.ReadValue(L, index).ToVector3();
                break;
        }
    }

    /// <summary>Reads the options table at <paramref name="index"/> (nil or absent gives the defaults), and checks every key.</summary>
    private static TweenSettings ReadTweenOptions(State* L, int index, double seconds, string function)
    {
        Ease ease = Ease.Linear;
        double delay = 0;
        int repeats = 0;
        bool reverses = false;
        bool unscaled = false;
        if (Lua.Type(L, index) != Lua.Tnone && !Stack.Is(L, index, LuaType.Tnil))
        {
            if (!Stack.Is(L, index, LuaType.Ttable))
            {
                throw new ValueException($"bad argument #{index} to '{function}' (a table of options expected, got {Stack.TypeName(L, index)})");
            }

            int table = Lua.Absindex(L, index);
            Stack.Reserve(L, 4);
            for (int iterator = 0; (iterator = Lua.Rawiter(L, table, iterator)) >= 0;)
            {
                try
                {
                    string key = Stack.Is(L, -2, LuaType.Tstring) ? Stack.ReadString(L, -2) : throw new ValueException($"the options of '{function}' are named by strings");
                    switch (key)
                    {
                        case "ease":
                            string name = Stack.Is(L, -1, LuaType.Tstring) ? Stack.ReadString(L, -1) : throw new ValueException("the option 'ease' must be a string");
                            ease = ParseEase(name);
                            break;
                        case "delay":
                            delay = Stack.Is(L, -1, LuaType.Tnumber) ? Lua.Tonumberx(L, -1, null) : throw new ValueException("the option 'delay' must be a number");
                            if (!double.IsFinite(delay) || delay < 0)
                            {
                                throw new ValueException("the option 'delay' must be a finite number of seconds, 0 or more");
                            }

                            break;
                        case "repeats":
                            double count = Stack.Is(L, -1, LuaType.Tnumber) ? Lua.Tonumberx(L, -1, null) : throw new ValueException("the option 'repeats' must be a number");
                            if (!double.IsFinite(count) || count != Math.Floor(count) || count < -1 || count > int.MaxValue)
                            {
                                throw new ValueException("the option 'repeats' must be a whole number of extra passes, 0 or more, or -1 to repeat forever");
                            }

                            repeats = (int)count;
                            break;
                        case "reverses":
                            reverses = Stack.Is(L, -1, LuaType.Tboolean) ? Lua.Toboolean(L, -1) != 0 : throw new ValueException("the option 'reverses' must be a boolean");
                            break;
                        case "unscaled":
                            unscaled = Stack.Is(L, -1, LuaType.Tboolean) ? Lua.Toboolean(L, -1) != 0 : throw new ValueException("the option 'unscaled' must be a boolean");
                            break;
                        default:
                            throw new ValueException($"'{key}' is not an option of '{function}' (ease, delay, repeats, reverses, unscaled)");
                    }
                }
                finally
                {
                    Lua.Settop(L, -3);
                }
            }
        }

        return new TweenSettings(seconds, ease, delay, repeats, reverses, unscaled);
    }

    private static Ease ParseEase(string name)
    {
        EnumInfo info = TypeRegistry.GetEnum(nameof(Ease));
        return info.TryGetValue(name, out long value) ? (Ease)value : throw new ValueException($"'{name}' is not an ease (linear, quad_in, quad_out, quad_in_out, cubic_*, quart_*, quint_*, sine_*, expo_*, circ_*, back_*, elastic_*, bounce_*)");
    }

    /// <summary>Makes the tween's userdata, starts it, and leaves the userdata on the stack.</summary>
    private int StartTween(State* L, Tween tween)
    {
        PushObject(L, tween, owned: true);
        Scheduler.Tweens.Add(L, tween);
        return 1;
    }

    // ---- The functions --------------------------------------------------------------------------------------------------------------------------

    private static int TweenValueBody(State* L)
    {
        LuauVm vm = VmOf(L);
        if (!TryValueKind(L, 1, out Tween.KindOfValue kind))
        {
            throw new ValueException($"bad argument #1 to 'value' (number, vector or Quat expected, got {Stack.TypeName(L, 1)})");
        }

        ReadEnd(L, 1, kind, "value", out double fromNumber, out Vector3 fromVector, out Quaternion fromQuat);
        if (!TryValueKind(L, 2, out Tween.KindOfValue toKind) || toKind != kind)
        {
            throw new ValueException($"bad argument #2 to 'value' ({KindName(kind)} expected like 'from', got {Stack.TypeName(L, 2)})");
        }

        ReadEnd(L, 2, kind, "value", out double toNumber, out Vector3 toVector, out Quaternion toQuat);
        double seconds = TweenSeconds(L, 3, "value");
        if (!Stack.Is(L, 4, LuaType.Tfunction))
        {
            throw new ValueException($"bad argument #4 to 'value' (function expected, got {Stack.TypeName(L, 4)})");
        }

        TweenSettings settings = ReadTweenOptions(L, 5, seconds, "value");
        LuauFunction setter = vm.CreateFunction(L, 4);
        var tween = new Tween(vm.Scheduler.Tweens, vm.OwnerOf(L), kind, Tween.TargetKind.Setter, settings, setter, null, null, null, default, default, null);
        tween.SetNumbers(fromNumber, toNumber);
        tween.SetVectors(fromVector, toVector);
        tween.SetQuats(fromQuat, toQuat);
        return vm.StartTween(L, tween);
    }

    private static int TweenPropertyBody(State* L)
    {
        LuauVm vm = VmOf(L);
        EngineObject subject = vm.GetObject(L, 1);
        string name = StringArgument(L, 2, "the property name");
        PropertyDef property = subject.Class.FindProperty(name) ?? throw new ValueException($"'{name}' is not a property of {subject.Class.Name}");
        if (property.Setter is null)
        {
            throw new ValueException($"{subject.Class.Name}.{name} cannot be set");
        }

        Tween.KindOfValue kind = property.Type.Kind switch
        {
            ValueKind.Int or ValueKind.Float => Tween.KindOfValue.Number,
            ValueKind.Vector3 => Tween.KindOfValue.Vector,
            ValueKind.Quaternion => Tween.KindOfValue.Quat,
            _ => throw new ValueException($"{subject.Class.Name}.{name} is a {property.Type} property: only numbers, vectors and Quats can be tweened"),
        };
        ReadEnd(L, 3, kind, "property", out double toNumber, out Vector3 toVector, out Quaternion toQuat);
        double seconds = TweenSeconds(L, 4, "property");
        TweenSettings settings = ReadTweenOptions(L, 5, seconds, "property");

        var tween = new Tween(vm.Scheduler.Tweens, vm.OwnerOf(L), kind, Tween.TargetKind.Property, settings, null, subject, property, null, default, default, null)
        {
            Integer = property.Type.Kind == ValueKind.Int,
        };
        tween.SetNumbers(0, toNumber);
        tween.SetVectors(default, toVector);
        tween.SetQuats(Quaternion.Identity, toQuat);
        return vm.StartTween(L, tween);
    }

    private static int TweenFieldBody(State* L)
    {
        LuauVm vm = VmOf(L);
        EcsWorld world = vm.GetWorld(L, 1);
        Entity entity = ReadEntity(L, 2);
        Entity component = ReadEntity(L, 3);
        string name = StringArgument(L, 4, "the field name");
        ComponentInfo info = world.GetComponentInfo(component) is { IsTag: false } known ? known : throw new ValueException($"component {component} has no fields to tween");
        FieldDef field = info.FindField(name) ?? throw new ValueException($"'{name}' is not a field of {info.Name}");
        if (!world.IsAlive(entity))
        {
            throw new ValueException($"entity {entity} is not alive");
        }

        if (!world.Has(entity, component))
        {
            throw new ValueException($"entity {entity} has no {info.Name}");
        }

        Tween.KindOfValue kind = field.Kind switch
        {
            FieldKind.Float32 or FieldKind.Float64 or FieldKind.Int8 or FieldKind.UInt8 or FieldKind.Int16 or FieldKind.UInt16 or FieldKind.Int32 or FieldKind.UInt32 or FieldKind.Int64 or FieldKind.UInt64
                => Tween.KindOfValue.Number,
            FieldKind.Vector3 => Tween.KindOfValue.Vector,
            FieldKind.Double3 => Tween.KindOfValue.Double3,
            FieldKind.Quaternion => Tween.KindOfValue.Quat,
            _ => throw new ValueException($"{info.Name}.{name} is a {field.Kind} field: only numbers, vectors and Quats can be tweened"),
        };
        ReadEnd(L, 5, kind, "field", out double toNumber, out Vector3 toVector, out Quaternion toQuat);
        double seconds = TweenSeconds(L, 6, "field");
        TweenSettings settings = ReadTweenOptions(L, 7, seconds, "field");

        var tween = new Tween(vm.Scheduler.Tweens, vm.OwnerOf(L), kind, Tween.TargetKind.Field, settings, null, null, null, world, entity, component, field)
        {
            Integer = field.Kind is not (FieldKind.Float32 or FieldKind.Float64),
        };
        tween.SetNumbers(0, toNumber);
        tween.SetVectors(default, toVector);
        tween.SetQuats(Quaternion.Identity, toQuat);
        tween.SetDoubleEnd(toVector);
        return vm.StartTween(L, tween);
    }

    private static int TweenEaseBody(State* L)
    {
        string name = Stack.Is(L, 1, LuaType.Tstring) ? Stack.ReadString(L, 1) : throw new ValueException($"bad argument #1 to 'ease' (string expected, got {Stack.TypeName(L, 1)})");
        if (!Stack.Is(L, 2, LuaType.Tnumber))
        {
            throw new ValueException($"bad argument #2 to 'ease' (number expected, got {Stack.TypeName(L, 2)})");
        }

        double t = Lua.Tonumberx(L, 2, null);
        if (double.IsNaN(t))
        {
            throw new ValueException("bad argument #2 to 'ease' (the time must not be NaN)");
        }

        Lua.Pushnumber(L, Easing.Apply(ParseEase(name), t));
        return 1;
    }

    /// <summary><c>tween:wait()</c>: yields the running task until the tween ends; returns true if it completed and false if it was cancelled. A tween that has ended gives its answer at once.</summary>
    private static int TweenWaitBody(State* L)
    {
        LuauVm vm = VmOf(L);
        Tween tween = vm.GetObject(L, 1) as Tween ?? throw new ValueException($"expected a Tween, got {Stack.TypeName(L, 1)}");
        if (tween.State != Tween.StateKind.Active)
        {
            Lua.Pushboolean(L, tween.State == Tween.StateKind.Completed ? 1 : 0);
            return 1;
        }

        ScriptScheduler.ParkedTask parked = vm.Scheduler.Park(L, "Tween:wait");
        (tween.Waiters ??= []).Add(parked);
        return Lua.Yield;
    }
}
