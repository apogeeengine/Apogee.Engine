# Vehicles and hitches

Vehicles tuned for heavy trucks, trailers and attachments: Jolt's wheeled and tracked `VehicleConstraint`, with the game owning the gearbox, plus the two ways
to hitch things together. They are part of [Physics](physics.md) (`PhysicsWorld.CreateVehicle`, `CreateHitch`); in a game, scripts reach them through the global `physics`.

```lua
local desc = VehicleDesc.new()
desc.engine_max_torque = 2500
desc.wheels = {}
for _, axle in { { z = 2.2, r = 0.55, steer = 35, share = 0.513 }, { z = -0.2, r = 0.8, steer = 0, share = 1.579 } } do
    for _, x in { 0.95, -0.95 } do
        local wheel = WheelDesc.new()
        wheel.position = Vector3(x, axle.r, axle.z)      -- the wheel's center with its suspension fully extended
        wheel.radius, wheel.powered, wheel.max_steer_angle, wheel.torque_share = axle.r, true, axle.steer, axle.share
        wheel.entity = wheelEntity[x][axle.z]            -- optional: follows the wheel each frame
        table.insert(desc.wheels, wheel)
    end
end
local truck = physics:create_vehicle(truckEntity, desc)   -- the entity has a dynamic body

truck.stepping:connect(function(dt)                  -- before every fixed step: the game drives
    truck.forward, truck.steering = throttle, steer
    truck.drive_ratio, truck.clutch = gearbox:ratio(), gearbox:clutch()
end)
```

## The vehicle

The vehicle's space is the body's: **+Z forward, +Y up, +X to its left** (glTF's). A wheel's `position` is where its center sits with the suspension extended; the suspension reaches `suspension_length`
above that. `WheelDesc`: `radius`, `width`, `suspension_length`, `suspension_frequency` (Hz) and `suspension_damping`, `max_steer_angle` (degrees), `steer_ratio`, `powered`, `max_brake_torque`,
`max_handbrake_torque`, `friction` (scales the tire's grip), `torque_share` and `brake_factor`. `VehicleDesc`: `drive` (`wheels` or `tracks`), `wheels`, the engine (`engine_max_torque`, `engine_min_rpm`,
`engine_max_rpm`, `engine_torque_curve`, `engine_inertia`), `differential_ratio`, `clutch_slip_rpm`, `track_brake_torque` and `max_pitch_roll_angle`. A heavier body with a **low center of mass**
(`BodyDesc.center_of_mass`) drives better.

**The game owns the gearbox.** Jolt's transmission stays in manual mode with one forward and one reverse gear, whose ratio is rewritten every step from `drive_ratio` (signed: negative reverses; 0 is neutral) and
`clutch` (0 drives nothing, 1 is engaged; the clutch's strength comes from `clutch_slip_rpm`). The inputs are `forward` (throttle), `steering` (-1 to 1, positive toward +X), `brake` and `handbrake`.
Set all of them in the `stepping` signal of the vehicle (or of the physics world): it runs before each step, then the vehicle gives the values to Jolt. A dynamic gearbox, an automatic with a torque converter,
a CVT sweeping its ratio, ABS and engine braking are all game code on top of this (a game's own Luau scripts).

### What the port does for the powertrain

- **Differentials:** powered wheels at about the same Z (within 0.25 m) form an axle with one differential, the wheel with the larger X as its left one. Its ratio is `differential_ratio` scaled by the axle's
  wheel radius over the largest powered radius, so axles of smaller wheels turn them faster and every wheel covers the same ground.
- **Torque shares:** the engine's torque goes to the axles by the `torque_share` of their wheels (an even split by default). By their load, the lightly loaded front axle of a truck would spin.
- **A vehicle with no powered wheel** (a trailer) gets a dummy differential and stays in neutral whatever it is told.
- **Wheel inertia** is that of a wheel of about 250 kg per meter of radius (`0.5 · 250 · r · r²`): Jolt's car default is knocked to a stop by the brakes in a single step on a truck.
- **The engine's torque curve** is sampled at 17 points of rpm / max rpm; `engine_peak_power_rpm` is where torque times rpm peaks, found over 65 points.

### Steering

Each wheel steers by `max_steer_angle` times the steering input times its `steer_ratio` (1 for front steering, -1 for rear steering, 1 on every axle for crab steering). A wheel with no steer angle is the
fixed axle. With wheels of different ratios, **Ackermann geometry** applies: from two such wheels the vehicle finds the wheelbase and the pivot (the axle where the ratio is 0), and every steered wheel turns about
the one turning center: the inner wheel of an axle more than the outer, at most 80°. (Jolt sets all wheels from one input before its casts; the port puts each wheel's angle in the wheel's settings and gives Jolt an
input of 1, which comes to the same.)

### Tracks

`drive = "tracks"`: a track per side (+X is left) made of the wheels on that side, driven by its powered wheel (the sprocket), or the rearmost one. Steering slows one side (and reverses it at full lock); steering
with no throttle turns on the spot, the two sides running opposite ways. `track_brake_torque` is a sprocket's brake.

### Readings

`speed_kmh`, `engine_rpm`, `engine_torque`, `engine_load`, `engine_peak_power_rpm`, `gear_ratio`, `brake_capacity` (what all the brakes give together), `wheel_count`; per wheel `get_wheel_contact`,
`get_wheel_compression` (0 extended to 1), `get_wheel_rpm`, `get_wheel_steer_degrees`, `get_wheel_contact_point`, `get_wheel_contact_normal`, `get_wheel_contact_entity`; and
`get_wheel_readings()`, four numbers per wheel for the assists: contact (1 or 0), angular velocity in rad/s, the load in newtons (the suspension's force; a vehicle's wheels together carry its weight) and the tire's grip.
`set_brake_factor(wheel, factor)` scales one wheel's brake torque from the next step on (an ABS releases a locking wheel with it); `physics:get_mass(entity)` is the body's mass.

Wheel entities (`WheelDesc.entity`, usually children of the vehicle's entity) get their local `Transform` written after each update: their place on the suspension, steered and spinning, in the vehicle's space
(wheel models have their axle along X). A vehicle ends when it is destroyed, its body goes or its entity does.

## Hitches

A hitch is an entity under the vehicle's entity (any depth), whose transform is the hitch's frame in the vehicle's space. `physics:create_hitch(hitchEntity, "mount" | "trailer")` makes the `Hitch`;
`hitch:attach(entity, point, rotation)` takes the **attachment's entity** and the attach point in the **attachment's** space, which meets the hitch's frame; it returns false if the hitch holds something. A hitch
holds one body. The two never collide while hitched.

### Mount hitch: loader, crane, blade

The attachment becomes part of the truck. Its collision shape is added to the truck's body, which becomes a mutable compound of its own shape and one child per attachment (so the truck feels what the
attachment hits, and several attachments can be mounted), its mass is added to the truck's (the inertia of the whole shape, scaled to the total mass), and the attachment's **body is removed**: its entity stays,
as a child of the hitch entity at the inverse of the attach point, and the transform cascade carries it with the truck's interpolated pose. **Lifting is moving the hitch entity:** each step the compound child is
moved to match (and the truck's center of mass moves with it). The attachment must be a dynamic or kinematic body without a vehicle.

`hitch:detach()` gives the attachment a body again where it is (the same shape and settings as before), moving as the point of the truck it was at (`v + ω × r`), takes its mass back, makes it a root entity in the
world and restores the truck's own shape when nothing else is mounted. The same happens, as far as it can, when the truck's body is removed. Destroying the attachment's entity while it is mounted lets go of
it. Destroying the truck's entity destroys the mounted attachment's entity too: it is a child of the hitch entity.

### Trailer hitch: drawbar, fifth wheel

The trailer keeps its body (it may be a vehicle with wheels of its own, which stays in neutral) and a six-degrees-of-freedom joint holds the attach point to the hitch: the three linear axes are locked and the rotation is
limited about the hitch's X, Y and Z by `angular_limits` (degrees; default 25, 75, 15: pitch, yaw, roll). The joint's frames are taken from the exact poses of the bodies when you attach, so attach after
placing the trailer behind the truck; the joint pulls the two points together if they are apart. The pair do not collide (the native layer rejects every contact of the pair: `aj_ignore_pair`).

## Not done

No hydraulics or linkage geometry (lift is the hitch entity's height, set by the game; what an attachment does is game code), no motorcycles (Jolt has the controller, the engine
does not use it), no per-wheel slip reading and no hitch for more than one body.

## Tests

`Tests/Apogee.Physics.Tests/VehicleTests.cs` is the old `vehicle.test.luau` (the ratio drives it, neutral and an open clutch drive nothing, the step runs every step before the inputs, brake factors, the readings
with the load equal to the weight, torque shares, a trailer in neutral) with Ackermann angles checked against `cot(outer) − cot(inner) = track / wheelbase`, rear and crab steering, wheel entities, a tracked
vehicle turning on the spot. `HitchTests.cs` covers a scenario (a mounted attachment lifted 0.35 m, riding within 1 cm of the hitch, lowered and detached to rest on the ground; a trailer holding its
drawbar within 5 cm through a turn and staying upright), plus the cleanup paths. `Tests/Luau/physics.test.luau` drives a vehicle and a mount hitch from a script, in the NativeAOT binary too.
