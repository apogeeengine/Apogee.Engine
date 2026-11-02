# Reflection

`Apogee.Reflection` is the registry of everything scripts, serialization and inspectors see: classes with properties, methods
and signals, enums, ECS components and script libraries. It is written by a Roslyn source generator
(`Apogee.Reflection.Generator`), so there is no runtime reflection and it works under NativeAOT.

## Describing a class

A class derives from `EngineObject`, is `partial`, and is marked `[ApogeeClass]`. Members are opted in:

```csharp
public enum Mode { Idle, FastMove }

/// <summary>Something that moves.</summary>
[ApogeeClass(Creatable = true)]
public partial class Actor : EngineObject
{
    /// <summary>How fast it goes, in meters per second.</summary>
    [Property] public float Speed { get; set; } = 2;
    [Property] public Mode Mode { get; set; }
    [Property] public int Count { get; private set; }              // no public setter: read-only

    [Method] public bool TryMove(Vector3 to, float speed = 1f) { … }
    [Method] public (int, string) Pair() => (7, "seven");            // a tuple is several results in a script
    [Method] public static int Twice(int x) => x * 2;

    [Signal] public Signal<int, string> Damaged { get; } = new();
}
```

- **Names** in scripts are snake_case (`TryMove` → `try_move`, `FastMove` → `"fast_move"`); `Name = "…"` on an attribute
  overrides it. Classes keep their C# name (`[ApogeeClass(Name = "…")]`).
- **Options:** `Scriptable = false` keeps the class out of scripts, `ModsBlocked = true` refuses its objects to mods, and
  `Creatable = true` gives scripts a `new()` (it needs a parameterless constructor).
- **Documentation:** the `<summary>` above a member is copied into the registry, for the generated `.d.luau` and the inspectors.
- **Types** a member may have: `bool`, the integer types, `float`, `double`, `string`, `Vector3`, `Double3` (seen as a vector in scripts), `Quaternion`, enums (a `[Flags]` enum is a list of
  names in scripts), `Entity`, `Value` (any), `ScriptFunction`, `EngineObject` classes, and `T[]`, `List<T>` and `IReadOnlyList<T>` of
  those. `T?` is nullable. Anything else is error `APG002` that names the member, as are non-partial classes (`APG001`), two members
  with one script name (`APG003`), a component that is not an unmanaged struct or has an unsupported field (`APG004`) and a member
  that cannot be described, such as a `ref` parameter or a signal of the wrong type (`APG005`). A type with an error writes nothing,
  so the first error is the only one.

## Components and libraries

```csharp
[Component]
public struct Position { public Vector3 Value; }               // fields: bool, integers, float, double, Vector3, Double3, Quaternion, Entity

[Component] public struct Frozen { }                           // no fields: a tag

[ScriptLibrary("imgui")]
public static partial class ImGuiLibrary
{
    [Method] public static bool Button(string label) { … }
}
```

A component's `ComponentInfo` has its size, alignment and every field's offset, taken from the compiled struct. A library is a
global table of its static `[Method]`s in scripts.

### What an inspector shows

`[Property]` carries hints for inspectors, on a class's properties and on a component's fields (`FieldDef.Meta`, `PropertyDef.Meta`: an `InspectorMeta`). All are optional:

```csharp
[Component]
public struct Engine
{
    [Property(Min = 0, Max = 100, Step = 0.5, Category = "Power")] public float Throttle;   // a slider from 0 to 100 under the heading "Power"
    [Property(Min = 0)] public int Gear;                                                    // a drag field that does not go below 0
    [Property(Hidden = true)] public uint Handle;                                           // an inspector does not show it
    [Property(ReadOnly = true)] public double Hours;                                        // shown, not editable
}
```

`Min`, `Max` and `Step` are hints for widgets: they do not stop a script or a file from setting another value. `InspectorMeta.HasRange` says both ends are given (a slider can be drawn),
`RangeText` is `0 to 100` for the generated Luau definitions, which add `(Range: 0 to 1.)` to the doc of a property. A field without `[Property]` has no hints. A component a script defines gives
them in a table: `world:component("Vehicle", { speed = { type = "f32", min = 0, max = 90, step = 0.5, category = "Motion" }, gear = "i32", token = { type = "u32", hidden = true } })`;
the plain `name = "type"` form still works.

## Using the registry

`TypeRegistry.GetClass("Actor")` gives a `ClassInfo`; `FindProperty("speed")`, `FindMethod("try_move")` and `FindSignal("damaged")`
look a member up by its script name, inherited ones included. Everything crosses as a `Value`, a 24-byte struct with no boxing:

```csharp
var results = new Value[1];
actor.Class.FindMethod("try_move")!.Invoke(actor, [Value.Vector(target), Value.Float(2)], results);
bool moved = results[0].ToBool();
```

`MethodDef.Invoke` checks the argument count; the `To…` methods check kind and range and throw a `ValueException` whose message
a script can show (`expected number, got string`, `300 is out of range for byte`). Registration happens in a module initializer of
each assembly that has described types, so the registry is complete before the first lookup.

## Objects and handles

Every `EngineObject` takes a slot in one table: its `Handle` is an index and a generation (`SlotTable<T>` in `Apogee.Core`).
Scripts hold the handle, never the object. `Destroy()` frees the slot and bumps the generation, so a handle to a destroyed
object, or to one whose slot was reused, is recognised (`attempt to use a destroyed object`). An object a script creates is destroyed when the script lets go of it
unless the engine calls `Adopt()` on it (see [Scripting](../scripting/scripting.md)).

## Signals

`Signal`, `Signal<T1>` … `Signal<T1, T2, T3, T4>`: `Connect(handler)` returns a `Connection` (`Disconnect()`, `IsConnected`) and
`Emit(…)` calls a snapshot of the handlers, so a handler may disconnect itself while it runs. A handler that throws is logged and
the others still run; the emitter never sees it. `SignalDef.Connect` connects a handler that receives the arguments as `Value`s.

## Tests

`Tests/Apogee.Reflection.Tests` compiles C# snippets with the generator (`CSharpGeneratorDriver`), checks the generated text and the
diagnostics, loads the result and drives it through the registry; it also tests `Value`, handles, signals and the registry itself.
