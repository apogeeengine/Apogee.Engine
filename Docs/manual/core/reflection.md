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
  names in scripts), `Entity`, `AssetRef` (a string in scripts, see [asset fields](#asset-fields)), `Value` (any), `ScriptFunction`, `EngineObject` classes, and `T[]`, `List<T>` and `IReadOnlyList<T>` of
  those. `T?` is nullable. Anything else is error `APG002` that names the member, as are non-partial classes (`APG001`), two members
  with one script name (`APG003`), a component that is not an unmanaged struct or has an unsupported field (`APG004`) and a member
  that cannot be described, such as a `ref` parameter or a signal of the wrong type (`APG005`), `Asset` where it does not belong (`APG006`), `Enum` where it does not belong or does not fit (`APG007`) and `Color` on anything but a `Vector3` (`APG008`). A type with an error writes nothing,
  so the first error is the only one.

## Components and libraries

```csharp
[Component]
public struct Position { public Vector3 Value; }               // fields: bool, integers, enums, float, double, Vector3, Double3, Quaternion, Entity

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

Two hints say what a number or a vector *is*:

```csharp
public enum Gear : byte { Reverse, Neutral, Drive }

[Component]
public struct Gearbox
{
    public Gear Current;                                                    // an enum-typed field: stored as a byte, FieldDef.Enum = "Gear"
    [Property(Enum = typeof(Gear))] public byte Previous;                   // a byte that holds Gear values: the same, without changing the field's type
    [Property(Color = true)] public Vector3 Glow;                           // a linear RGB colour: InspectorMeta.Color, drawn as a colour picker
}
```

- **Enums.** A component field declared with an enum type is stored as the enum's underlying integer type (`FieldDef.Kind` is `UInt8` for an enum of `byte`) and `FieldDef.Enum` is the name of the
  registered `EnumInfo`, so an inspector can show a list of the members (a `[Flags]` enum, `EnumInfo.IsFlags`, a set of check boxes). Everything else sees the integer, as before: scripts read and write a number
  (the generated `.d.luau` has no component fields, but it declares the registered enum with its member names, as it does for the enums of classes), and scenes and snapshots hold the number, so a renamed member never breaks a file. A value with no member
  stays what it is. `Enum = typeof(X)` does the same for an integer field that is not declared with the enum (`Light.Kind` is a `byte`, and `LightKind` is an enum of `byte`). `X` must be an enum whose underlying type
  has the field's size and whose members all fit the field (an enum of `byte` with a member 200 does not fit an `sbyte`); the signedness is not compared. It is for integer fields only: on a `bool`, a float, a vector, an
  `Entity`, an `Asset` handle, a field that has an enum type already, anything but `typeof(an enum)`, or a class property, it is error `APG007`. The registry knows an enum by its simple name, so two different enums with one name in an
  assembly are error `APG003` (the closed constructions of one generic type share a name and members, and are fine). The enum may come from another assembly; it needs to be accessible to the assembly that uses it.
- **Colour.** `Color = true` on a `Vector3` component field or class property sets `InspectorMeta.Color`: the three numbers are a linear red, green and blue, and an inspector draws a colour picker that keeps values above 1 (a light is
  brighter than white). On anything else (including `Double3`, which is a position) it is error `APG008`. The field stays a `Vector3` in memory, in scripts and in scenes.

`InspectorMeta.Color` and `FieldDef.Enum` were added after the other members, as the last constructor parameters, so existing code that builds them positionally means what it did.

`Min`, `Max` and `Step` are hints for widgets: they do not stop a script or a file from setting another value. `InspectorMeta.HasRange` says both ends are given (a slider can be drawn),
`RangeText` is `0 to 100` for the generated Luau definitions, which add `(Range: 0 to 1.)` to the doc of a property. A field without `[Property]` has no hints. A component a script defines gives
them in a table: `world:component("Vehicle", { speed = { type = "f32", min = 0, max = 90, step = 0.5, category = "Motion" }, gear = "i32", token = { type = "u32", hidden = true } })`;
the plain `name = "type"` form still works.

### Asset fields

A component is made of unmanaged memory, so a field that names an asset (a model, a texture) is a `uint` handle in the loaded library of that asset type. `[Property(Asset = "model")]` on a `uint`
field says so: the field's `FieldDef.Kind` is `FieldKind.AssetRef` (four bytes, like a `uint`) and `FieldDef.AssetType` is `"model"`. Scripts still read and write a number; scenes and the editor
use the kind and the type to turn the handle into a reference by path and UID.

```csharp
[Component]
public struct Furnished
{
    [Property(Category = "Model", Asset = "model")] public uint Model;   // FieldKind.AssetRef, AssetType "model"
}
```

`Asset` is only for `uint` fields of a `[Component]` struct, and needs a non-empty type name; anywhere else it is error `APG006`. Without it a `uint` stays a number.

On a class, a property that names an asset has the type `Apogee.Core.Assets.AssetRef` (path and UID). Scripts see its text form as a string (`res://models/car.glb|uid://4k2m9x7q1w3ze`, a path alone or a
`uid://…` alone), and the generated `.d.luau` says `string` (`string?` for `AssetRef?`). Text that is not an asset reference is an error in the script (`Value.ToAssetRef` throws a `ValueException`)
and the property keeps its value.

### Properties of classes in the inspector

The hints above (`Min`, `Max`, `Step`, `Category`, `Hidden`, `ReadOnly`, `Color`) are read the same way from a class's properties, and the editor's object inspector (*Debug > Engine objects*, see [the editor](../tools/editor.md#engine-objects))
draws an `[ApogeeClass]` object from them: the properties of its base classes first, each by its `TypeRef` (a bool, a number, a string, a vector, a rotation, an enum from its `EnumInfo`, an entity, an object that opens in place, a list of any of
those), through `PropertyDef.Getter` and `PropertyDef.Setter` over `Value`, which are the delegates scripts use. A property with no setter is shown and not edited. A setter that throws a `ValueException` (or anything else) is how a class
refuses a value: the inspector shows the message and leaves the property alone, so a setter has to leave the object as it was when it throws. A getter should not throw, and an object that a property returns may be destroyed at any time (`IsDestroyed`): the inspector copes with both, but it
reads every property each frame, so a getter should be cheap. Two things the registry does not say, because it never had to: whether a string is long text (the editor has `InspectorDrawers.RegisterMultiline` for that), and which string is an asset reference (an `AssetRef` property is a plain string in its `TypeRef`, so the inspector shows its text).

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
