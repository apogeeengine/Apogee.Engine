# Physics

`Apogee.Physics` is Jolt Physics for the ECS: entities get **bodies**, the simulation steps at a fixed rate, and the entities' transforms follow. The raw library is in
[Native libraries](../native/native-libraries.md#jolt-physics-apogeenativejolt); this is what the engine makes of it. In a game, scripts see the world as the global `physics`.

```lua
local Transform = world:component("Transform")
local ground = world:create_entity("ground")
world:set(ground, Transform, { Position = Vector3(0, -1, 0), Rotation = Quat.identity, Scale = Vector3(1, 1, 1) })
local floor = BodyDesc.new()
floor.shape = PhysicsShape.box(Vector3(100, 1, 100))        -- half extents
floor.motion = "static"
physics:add_body(ground, floor)

local crate = world:create_entity("crate")
world:set(crate, Transform, { Position = Vector3(0, 5, 0), Rotation = Quat.identity, Scale = Vector3(1, 1, 1) })
local body = BodyDesc.new()
body.shape = PhysicsShape.box(Vector3(0.5, 0.5, 0.5))
body.mass = 20
body.events = true                                           -- report its contacts
physics:add_body(crate, body)

physics.contact_began:connect(function(a, b, point, normal) print("hit", a, b, point) end)
local hit, entity, point, normal, distance = physics:raycast(Vector3(0, 10, 0), Vector3(0, -1, 0), 50)
```

## The world and its frame

`PhysicsWorld(ecsWorld, options)` makes one simulation for an ECS world; a game session makes it for you. **Call `Update(deltaSeconds)` once a frame, before `World.Progress`**
(`ScriptSession.Progress` does): the physics then writes the transforms of its bodies, the frame's systems and scripts see them, and the world's transform system composes the `WorldTransform`s as usual.

- **A fixed step**, `StepRate` times a second (64 in the tests, 60 by default). `Update` adds the frame's time to an accumulator and runs as many steps as fit, at most `MaxSubsteps` (4): a slow frame loses
  time instead of spiralling. The results depend on the step rate, not on the frame rate or the number of threads.
- **`stepping`** (`Stepping` in C#) is raised before each fixed step. Anything that must act once per step, vehicle controls above all, connects there, not to a per-frame callback.
- **Interpolation** (`Interpolate`, on by default): the entity of a moving dynamic body shows the pose between its last two steps by how far the accumulator is into the next one, so motion is smooth at any frame
  rate, at the price of showing the past by up to one step. `physics:pose(entity)` is the exact pose of the last step. A body that comes to rest shows exactly where it is, not a blend.
- **Determinism:** the library is built so that the same inputs give the same bits on every OS (see [Native libraries](../native/native-libraries.md#jolt-physics-apogeenativejolt)), whatever `ThreadCount` is.
- Options: `MaxBodies` (10 240), `MaxBodyPairs`, `MaxContactConstraints`, `TempBufferBytes` (32 MiB), `Gravity`, `VelocitySteps` 10, `PositionSteps` 2, `EventCapacity` (4096). These are the old project's settings.

## Bodies

A `BodyDesc` says what a body is: its `shape`, `motion`, `mass` (or a `density`, with the shape's volume), `friction`, `restitution`, damping, `gravity_factor`, `layers` and `mask`, whether it is a `sensor`,
whether it reports `events`, `allow_sleeping`, `bullet` (sweep the whole path, so a fast body cannot tunnel through a thin wall) and its starting velocities. One description can make any number of bodies.
`physics:add_body(entity, desc)` puts the body where the entity's transform is now; the entity gets a `Transform` if it has none, and a `Body` component (`Id`, `Slot`, `Motion`). Removing the component
(`physics:remove_body`) or destroying the entity removes the body.

| `motion` | Meaning |
| --- | --- |
| `static` | Never moves (ground, walls). `physics:teleport` moves it, slowly. |
| `kinematic` | The game moves it: it goes to where its entity is by the end of each frame's steps, and pushes dynamic bodies (doors, lifts, platforms). |
| `dynamic` | The simulation moves it, and its entity follows. |

### Shapes

A `PhysicsShape` is made with a static function and shared by any number of bodies: `box(halfExtents)`, `sphere(radius)`, `capsule(halfHeight, radius)` and `cylinder(halfHeight, radius)` (along Y), `convex_hull(points)`,
`mesh(vertices, indices)` and `height_field(heights, size, offset, scale)` (static and kinematic bodies only), `compound(children, positions, rotations)`, `offset(shape, position, rotation)` (moves the shape inside its own space,
so a box can stand on the entity's origin) and `scaled(shape, scale)`. Jolt counts references, so a shape that is dropped lives on in the bodies that use it. `volume`, `center_of_mass` and `is_valid_scale(scale)` say what
it is. Dimensions are checked: a zero radius or a mesh index past the vertices is an error with the reason.

**Scale.** The entity's world scale is applied to the shape when the body is made (a scale a shape cannot take, such as a stretched sphere, is an error). Changing the scale later does not change the body: make it again.

### How a body fits the entity tree

`Transform` is local to the parent and `WorldTransform` is computed top-down (see [Math](../core/math.md)). A body takes its **world** pose from `World.ComputeWorldTransform(entity)` when it is made, and a dynamic body
**writes back a local `Transform`**: its world pose from Jolt, taken into the space of its parent (`parentWorld⁻¹ × pose`, with the position subtraction done in double). The scale is left alone. So a crate under a moving
platform entity falls in the world's space, and its local transform changes to match. A kinematic body reads its entity's world transform. The rule that keeps this simple: **a body's ancestors must not be dynamic bodies**
(`add_body` refuses); a parent that is not a body, or is static or kinematic, is fine.

## Layers

A body is in some of 14 collision layers (`layers`, a bit each: `1` is the default layer) and collides with the layers in its `mask` (all by default). Two bodies collide when each is in a layer the other's mask holds.
Two more bits are the world's own and sort bodies into three broad-phase trees (static, sensor, moving); Jolt never collides two bodies that are both static or kinematic. The native layer builds the object layer from them, and the query filters (`mask`, `ignore`) run natively too: see `Native/jolt/apogee_jolt.cpp`.

## Events

Bodies that have `events` (and every sensor) report to the world's event queue (`apogee_jolt`), which is drained after each step on the main thread:

| Signal | Arguments |
| --- | --- |
| `contact_began` | the two entities, the contact point (a position) and the normal |
| `contact_ended` | the two entities (also when one of them goes to sleep: Jolt ends the contacts of a sleeping body) |
| `sensor_entered`, `sensor_exited` | the sensor's entity, then the other |
| `body_woke`, `body_slept` | the entity |

A **sensor** (`sensor = true`) does not collide and is not pushed; it reports the bodies inside it. Events a step produced beyond `EventCapacity` are dropped and counted (`dropped_events`).
A signal handler runs while the world is not inside Jolt, so it may add or remove bodies.

## Queries

All positions are in the world, in double precision, and layers are a `mask`; `ignore` is an entity the query passes through. Sensors are never hit.

- `raycast(origin, direction, maxDistance, mask?, ignore?)` returns `hit, entity, point, normal, distance`.
- `cast_sphere(origin, direction, radius, maxDistance, mask?, ignore?)` returns the same for the first body a swept sphere touches (`point` is on the body, `distance` how far the sphere got).
- `overlap_sphere(center, radius, mask?, ignore?)` and `overlap_box(center, rotation, halfExtents, mask?, ignore?)` return the entities of the bodies that touch the shape, each once.

## Bodies from code

`get_linear_velocity`, `set_linear_velocity`, `get_angular_velocity`, `set_angular_velocity`, `add_force` (newtons, for the next step only: call it every step), `add_torque`, `add_impulse` (newton seconds), `wake`, `is_awake`, `pose`
(the exact pose), `teleport(entity, position, rotation)` (body and entity at once, no blend).

## Vehicles

`physics:create_vehicle` and `physics:create_hitch`: see [Vehicles and hitches](vehicles.md). `BodyDesc.center_of_mass` and `physics:get_mass(entity)` are for them.

## Characters

`physics:create_character(entity, height, radius, layers?, mask?)` makes a **character controller** (Jolt's `CharacterVirtual`): a capsule whose feet are at the entity's position that slides along walls, walks up
stairs (steps up to 0.4 m) and slopes (up to `max_slope_degrees`, read-only, 50), sticks to the ground going down and stands on moving platforms. It is not a rigid body: dynamic bodies are pushed by it and do not push it.
Set `desired_velocity` (the horizontal part counts; gravity and jumps decide the vertical one), call `jump(speed)` (from the ground only), read `is_on_ground`, `ground_velocity`, `velocity` and `position`. It
steps with the world, and its entity follows, blended like a dynamic body. `teleport(position)` and `set_rotation(rotation)` move it; destroying it stops the following.

## Far from the origin

Jolt is built with double positions, so a body, a ray or a character is as exact 10 000 km out as at the origin; the tests run the same scenes at `x = 1e7`. Rotations, scales, velocities and forces are floats. Scripts
see positions as `vector`s, narrowed to float (see [Math](../core/math.md)): a script that works that far out keeps its coordinates relative to something near.

## Pausing

`physics.paused = true` stops the world: `Update` adds no time and runs no step, so bodies stay where they are, keep their velocities, and resume from there when it is false again. A pause menu sets it.

## Debug drawing

`physics.debug_draw = true` (or the *Physics shapes* checkbox in the dev overlay's Debug section) draws the world over the picture: every body's shape as wireframe, colored by what it is, the joints and drawbars, a vehicle's wheels
and suspension, and the capsules of characters. In C#, `PhysicsWorld.DrawDebug(DebugDraw draw, PhysicsDebugOptions? options, Double3 camera)` adds the same to any [`DebugDraw`](../graphics/rendering.md#debug-drawing);
`PhysicsDebugOptions` chooses what: shapes (solid or wireframe), bounding boxes, centers of mass, velocities, constraints and their limits, characters. `camera` picks the level of detail of round shapes.

Jolt draws, and our C API only collects: `aj_world_draw_debug` runs Jolt's `DebugRendererSimple` into one process-wide collector (Jolt allows one debug renderer at a time; it is made on first use and freed by `aj_shutdown`),
and `aj_debug_lines` / `aj_debug_triangles` hand its arrays over, so no managed code runs inside Jolt's drawing. `DebugLine` and `DebugTriangle` have the layout of the C structs (a test pins it), so the arrays are copied as they are.
A character is not a body, so `aj_character_draw_debug` draws its capsule separately. Jolt's wireframe is the edges of the shape's triangle mesh (a box is its twelve triangles, so 36 lines), not just its outline.

## Not done

Constraints (joints beyond the [hitches](vehicles.md)), soft bodies, ragdolls, and changing a body's shape or scale after it
is made. The Jolt library has all of them in its C API (`Apogee.Native.Jolt`).

## Tests

`Tests/Apogee.Physics.Tests` covers falling and resting, double precision 10 000 km out, parent spaces, kinematic carrying, scale, teleport, impulses and mass, the fixed step and interpolation, queries, events, sensors,
layers, every shape, the character, debug drawing (the layout of its structs, a box's lines at their place 1 000 km out, solid and wireframe, bounding boxes, the character's capsule), and that the result is the same with 1 and 4 threads. `Tests/Luau/physics.test.luau` runs the script surface in the NativeAOT binary too.
