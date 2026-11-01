# Math and the double-precision world

Everything the GPU and the shaders see is `float`; the *world* is not. A `float` has 24 bits of precision, so 5.8 km from the origin (the corner of an
8192 m map) it moves in 0.5 mm steps, which shows as jitter in physics and in the picture. So **positions are `double`**, and
every other quantity (directions, rotations, scales, velocities, everything on the GPU) stays `float`.

## Types

The engine does not have a math library of its own. It uses `System.Numerics` (`Vector2/3/4`, `Quaternion`, `Matrix4x4`, `Plane`): the JIT and NativeAOT
treat those as SIMD intrinsic types (a `Vector3` lives in one register and `+`, `Dot`, `Normalize` compile to a few vector instructions), which a struct
of our own would only match by wrapping them. `Apogee.Core.Numerics` adds what `System.Numerics` has no type for:

| Type | What it is |
| --- | --- |
| `Double3` | A world position: three `double`s, 24 bytes, scalar code (positions are added and subtracted, not crunched in bulk). Converts implicitly from `Vector3` and explicitly to it. |
| `Trs` | A placement: `Double3` position, `Quaternion` rotation, `Vector3` scale. `InWorldOf(parent)`, `TransformPoint`, `InverseTransformPoint`, `TransformVector`, `ToMatrix(origin)`. |

**Conventions** (the same as the renderer's): right-handed, +Y up, an object or camera looks down -Z, angles are radians, matrices are row-major and used with
row vectors (`Vector3.Transform(v, m)`; `a * b` applies `a` first), and quaternions compose as in mathematics: `(q1 * q2) * v == q1 * (q2 * v)`, so `q2` applies first.

### Relative to an origin

`Double3.RelativeTo(origin)` subtracts in `double` and *then* narrows to `float`, so the result is exact however far both points are from the world's origin:

```csharp
Vector3 onScreen = enemy.Position.RelativeTo(camera.Position);     // a few meters, precise to a micrometre, 10 000 km from the origin
Matrix4x4 world = placement.ToMatrix(camera.Position);             // the translation is relative to the camera
```

That is how positions reach the GPU: the renderer works camera-relative (see [Rendering](../graphics/rendering.md): `Camera.Position` and a scene's `Origin` are `Double3`). Jolt is built with `JPH_DOUBLE_PRECISION` (see [Native libraries](../native/native-libraries.md#jolt-physics-apogeenativejolt)), so
that body positions are `double` too.

## Transforms in the ECS

`Transform` (`Position: Double3`, `Rotation: Quaternion`, `Scale: Vector3`) is a component of every world. It is *local*: relative to the entity's parent (the
`ChildOf` relationship), and in the world for an entity with no parent. Set the scale to one (`Transform.Identity`, `Transform.At(position)`): a zeroed transform,
which is what `Add` gives, has no size.

`WorldTransform` is the same placement in world space, and the transform system computes it once a frame (in the `PostUpdate` phase) top down: a Flecs query with
the parent's `WorldTransform` as a *cascade* term, so parents are always done before their children, whatever order they were created in. Adding a `Transform` adds
its `WorldTransform` (Flecs' `With` trait). Read `WorldTransform`; do not write it.

```csharp
Entity truck = world.CreateEntity("truck");
Entity wheel = world.CreateEntity("wheel", parent: truck);
world.Set(truck, Transform.At(new Double3(5_000_000, 0, 0)));
world.Set(wheel, new Transform(new Double3(1, 0.4, 2), Quaternion.Identity, Vector3.One));
world.Progress(0.016f);
Double3 where = world.Get<WorldTransform>(wheel).Position;       // (5 000 001, 0.4, 2)
```

**Limits.** Scale may differ per axis, but there is no shear: a child's world scale is the product of its own and its parent's, which is exact when the parent's scale is
uniform or its rotation lines up with the child's axes (the rule most engines use). The child's position inside its parent is narrowed to `float` while it is rotated,
so keep children within about ten kilometres of their parent; the parent itself can be anywhere.

## In scripts

Luau's `vector` is three **floats** (`LUA_VECTOR_SIZE 3`), so scripts see positions as `vector`s: a `Double3` is narrowed when it goes into Luau and widened when it
comes back. A `float` is good to about a millimetre at 8 km from the origin, which is enough for gameplay logic, and nothing is lost where it matters, in the physics and the renderer,
which keep the doubles. A script that needs an exact far position works with offsets from a known place instead.

`Quat` is an immutable userdata:

```lua
local turn = Quat.axisAngle(Vector3(0, 1, 0), math.rad(90))      -- Quat.new(x, y, z, w), Quat.euler(vector(pitch, yaw, roll)), Quat.lookAt(forward, up?)
turn * Vector3(0, 0, 1)                                          -- rotates a vector: (1, 0, 0)
turn * other                                                     -- composes: `other` applies first
turn:inverse(), turn:normalized(), turn:slerp(other, t), turn:dot(other), turn:toEuler()
turn.x, turn.y, turn.z, turn.w; Quat.identity; turn == other
```

The built-in components are reachable by name: `world:component("Transform")` and `world:component("WorldTransform")`, with fields `Position`, `Rotation` and `Scale`.
A component a script defines can have fields of type `"dvec3"` (a `Double3`, seen as a `vector`) and `"quat"`.

## In the registry and in files

- `[Component]` structs may have `Double3` and `Quaternion` fields (`FieldKind.Double3`, `FieldKind.Quaternion`), and `[Property]`/`[Method]` members may use both types.
  A `Double3` member crosses to scripts as `ValueKind.Vector3`, a `Quaternion` as `ValueKind.Quaternion`. Flecs knows both as struct types, so its JSON keeps all 17 digits
  of a position.
- `Value` carries a quaternion in the spare float next to a vector's `z` (a `Value` is 32 bytes); it has no room for a `Double3`, which is why positions cross as vectors.
- In XML definitions a `vector3` attribute (`type="xs:vector3"`; any prefix) is three numbers, `at="1 2.5 -3"` (spaces or commas), decoded as a `DefVector` and a `vector` in Luau.

## Tests

`Tests/Apogee.Core.Tests` (`Double3`, `Trs`: composing, inverting, precision at 10 000 km, the camera-relative matrix), `Tests/Apogee.Ecs.Tests` (propagation through three
levels created in any order, reparenting, the `With` trait, the layout and the JSON of the components), `Tests/Apogee.Reflection.Tests` (the new field kinds, members and values),
`Tests/Luau/quat.test.luau` (`Quat` and the transform from a script), the `vector3` case of `Tests/Apogee.Content.Tests` and, in `Tests/Apogee.Rendering.Tests`, the golden scene
rendered 100 km and 10 000 km from the origin.
