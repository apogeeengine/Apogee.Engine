--!strict
-- The physics world as scripts see it: shapes, bodies on entities, the fixed step, queries and events.
local test = require("@engine/test")

-- Only in hosts that have the physics library (the runtime does; the scripting tests' host does not).
if PhysicsWorld == nil then
	return
end

local STEP = 1 / 64

local function setup(): (World, any, any)
	local world = test.world()
	local physics = PhysicsWorld.create(world)
	local Transform = world:component("Transform")
	return world, physics, Transform
end

local function place(world: World, Transform: any, position: vector, name: string?): number
	local entity = world:create_entity(name)
	world:set(entity, Transform, { Position = position, Rotation = Quat.identity, Scale = Vector3(1, 1, 1) })
	return entity
end

local function ground(world: World, physics: any, Transform: any): number
	local entity = place(world, Transform, Vector3(0, -1, 0), "ground")
	local desc = BodyDesc.new()
	desc.shape = PhysicsShape.box(Vector3(100, 1, 100))
	desc.motion = "static"
	physics:add_body(entity, desc)
	return entity
end

local function run(world: World, physics: any, seconds: number)
	for _ = 1, math.floor(seconds / STEP) do
		physics:update(STEP)
		world:progress(STEP)
	end
end

test.describe("shapes", function()
	test.it("know what they are and their volume", function()
		local box = PhysicsShape.box(Vector3(0.5, 0.5, 0.5))
		test.expect(box.kind).toBe("box")
		test.expect(box.volume).toBeNear(1, 1e-3)
		test.expect(PhysicsShape.sphere(1).volume).toBeNear(4.18879, 1e-3)
		test.expect(PhysicsShape.capsule(1, 0.5).kind).toBe("capsule")
		local hull = PhysicsShape.convex_hull({ Vector3(0, 0, 0), Vector3(1, 0, 0), Vector3(0, 1, 0), Vector3(0, 0, 1) })
		test.expect(hull.volume).toBeNear(1 / 6, 1e-3)
		local both = PhysicsShape.compound({ box, box }, { Vector3(-2, 0, 0), Vector3(2, 0, 0) }, {})
		test.expect(both.volume).toBeNear(2, 1e-3)
		test.expect(PhysicsShape.scaled(box, Vector3(2, 2, 2)).volume).toBeNear(8, 1e-2)
	end)

	test.it("refuse what cannot work, with the reason", function()
		test.expect(function()
			PhysicsShape.sphere(0)
		end).toThrow("radius must be a positive number")
		test.expect(function()
			PhysicsShape.convex_hull({ Vector3(0, 0, 0) })
		end).toThrow("at least 4 points")
		test.expect(function()
			PhysicsShape.mesh({ Vector3(0, 0, 0), Vector3(1, 0, 0), Vector3(0, 0, 1) }, { 0, 1, 9 })
		end).toThrow("vertex that does not exist")
	end)
end)

test.describe("bodies", function()
	test.it("fall, land and write their entity's transform", function()
		local world, physics, Transform = setup()
		ground(world, physics, Transform)
		local box = place(world, Transform, Vector3(0, 3, 0), "box")
		local desc = BodyDesc.new()
		desc.shape = PhysicsShape.box(Vector3(0.5, 0.5, 0.5))
		physics:add_body(box, desc)
		test.expect(physics:has_body(box)).toBe(true)
		test.expect(physics.body_count).toBe(2)

		run(world, physics, 4)

		local WorldTransform = world:component("WorldTransform")
		test.expect(world:get(box, WorldTransform).Position.y).toBeNear(0.5, 0.03)
		test.expect(physics:is_awake(box)).toBe(false)
		local position = physics:pose(box)
		test.expect(position.y).toBeNear(0.5, 0.03)
	end)

	test.it("take velocities, impulses and gravity", function()
		local world, physics, Transform = setup()
		physics.gravity = Vector3(0, 0, 0)
		local box = place(world, Transform, Vector3(0, 5, 0))
		local desc = BodyDesc.new()
		desc.shape = PhysicsShape.box(Vector3(0.5, 0.5, 0.5))
		desc.mass = 50
		physics:add_body(box, desc)

		physics:add_impulse(box, Vector3(100, 0, 0))
		test.expect(physics:get_linear_velocity(box).x).toBeNear(2, 1e-3)
		physics:set_linear_velocity(box, Vector3(0, 0, 1))
		run(world, physics, 1)
		test.expect(physics:pose(box).z).toBeNear(1, 0.05)
	end)

	test.it("raise errors a script can read", function()
		local world, physics, Transform = setup()
		local e = place(world, Transform, Vector3(0, 0, 0))
		test.expect(function()
			physics:add_body(e, BodyDesc.new())
		end).toThrow("needs a shape")
		test.expect(function()
			physics:get_linear_velocity(e)
		end).toThrow("has no physics body")
	end)
end)

test.describe("queries and events", function()
	test.it("cast rays, overlap spheres and report contacts", function()
		local world, physics, Transform = setup()
		local floor = ground(world, physics, Transform)
		local hit, entity, point, normal, distance = physics:raycast(Vector3(0, 5, 0), Vector3(0, -1, 0), 20)
		test.expect(hit).toBe(true)
		test.expect(entity).toBe(floor)
		test.expect(point.y).toBeNear(0, 1e-3)
		test.expect(normal.y).toBeNear(1, 1e-2)
		test.expect(distance).toBeNear(5, 1e-3)
		local miss = physics:raycast(Vector3(0, 5, 0), Vector3(0, 1, 0), 20)
		test.expect(miss).toBe(false)
		test.expect(#physics:overlap_sphere(Vector3(0, 0, 0), 1)).toBe(1)

		local ball = place(world, Transform, Vector3(0, 2, 0), "ball")
		local desc = BodyDesc.new()
		desc.shape = PhysicsShape.sphere(0.5)
		desc.events = true
		physics:add_body(ball, desc)
		local contacts = 0
		local stepping = 0
		physics.contact_began:connect(function(a: number, b: number, at: vector, n: vector)
			contacts += 1
		end)
		physics.stepping:connect(function(dt: number)
			stepping += 1
		end)

		run(world, physics, 3)

		test.expect(contacts).toBe(1)
		test.expect(stepping).toBe(physics.steps)
	end)
end)

test.describe("characters", function()
	test.it("walk on the ground", function()
		local world, physics, Transform = setup()
		ground(world, physics, Transform)
		local entity = place(world, Transform, Vector3(0, 0.1, 0), "walker")
		local walker = physics:create_character(entity, 1.8, 0.4)
		walker.desired_velocity = Vector3(3, 0, 0)

		run(world, physics, 1)

		test.expect(walker.is_on_ground).toBe(true)
		local WorldTransform = world:component("WorldTransform")
		test.expect(world:get(entity, WorldTransform).Position.x).toBeNear(3, 0.3)
	end)
end)

test.describe("vehicles and hitches", function()
	local function truck(world: World, physics: any, Transform: any, at: vector): (number, any)
		local entity = place(world, Transform, at, "truck")
		local body = BodyDesc.new()
		body.shape = PhysicsShape.offset(PhysicsShape.box(Vector3(1, 0.6, 2)), Vector3(0, 1.3, 0), Quat.identity)
		body.mass = 6000
		body.center_of_mass = Vector3(0, 0.9, 0.5)
		physics:add_body(entity, body)

		local wheels = {}
		for _, axle in { { z = 2.2, r = 0.55, steer = 35 }, { z = -0.2, r = 0.8, steer = 0 } } do
			for _, x in { 0.95, -0.95 } do
				local wheel = WheelDesc.new()
				wheel.position = Vector3(x, axle.r, axle.z)
				wheel.radius = axle.r
				wheel.powered = true
				wheel.max_steer_angle = axle.steer
				table.insert(wheels, wheel)
			end
		end
		local desc = VehicleDesc.new()
		desc.wheels = wheels
		desc.engine_max_torque = 2500
		return entity, physics:create_vehicle(entity, desc)
	end

	test.it("drive through the gearbox the script runs in their stepping signal", function()
		local world, physics, Transform = setup()
		ground(world, physics, Transform)
		local entity, vehicle = truck(world, physics, Transform, Vector3(0, 0.2, 0))
		local steps = 0
		vehicle.stepping:connect(function(dt: number)
			steps += 1
			vehicle.forward = 1
			vehicle.drive_ratio = 12
			vehicle.clutch = 1
		end)

		run(world, physics, 5)

		test.expect(vehicle.is_ready).toBe(true)
		test.expect(steps).toBeTruthy()
		test.expect(vehicle.speed_kmh).toBeTruthy()
		test.expect(vehicle.speed_kmh > 5).toBe(true)
		test.expect(vehicle.wheel_count).toBe(4)
		test.expect(vehicle:get_wheel_contact(0)).toBe(true)
		test.expect(#vehicle:get_wheel_readings()).toBe(16)
		test.expect(physics:get_mass(entity)).toBeNear(6000, 1)
	end)

	test.it("carry an attachment on a mount hitch that the script lifts by moving the hitch entity", function()
		local world, physics, Transform = setup()
		ground(world, physics, Transform)
		local entity = truck(world, physics, Transform, Vector3(0, 0.2, 0))
		local hitchEntity = world:create_entity("hitch", entity)
		world:set(hitchEntity, Transform, { Position = Vector3(0, 0.6, -1.4), Rotation = Quat.identity, Scale = Vector3(1, 1, 1) })
		local hitch = physics:create_hitch(hitchEntity, "mount")

		local attachment = place(world, Transform, Vector3(0, 0.5, -3), "attachment")
		local body = BodyDesc.new()
		body.shape = PhysicsShape.offset(PhysicsShape.box(Vector3(1.5, 0.3, 0.6)), Vector3(0, 0.3, 0), Quat.identity)
		body.mass = 1200
		physics:add_body(attachment, body)
		run(world, physics, 0.2)

		test.expect(hitch:attach(attachment, Vector3(0, 0.3, 0.6), Quat.identity)).toBe(true)
		test.expect(hitch.is_attached).toBe(true)
		test.expect(physics:get_mass(entity)).toBeNear(7200, 1)
		test.expect(physics:has_body(attachment)).toBe(false)

		local WorldTransform = world:component("WorldTransform")
		run(world, physics, 1)
		local before = world:get(attachment, WorldTransform).Position.y
		world:set(hitchEntity, Transform, { Position = Vector3(0, 1.0, -1.4), Rotation = Quat.identity, Scale = Vector3(1, 1, 1) })
		run(world, physics, 0.2)
		test.expect(world:get(attachment, WorldTransform).Position.y - before > 0.35).toBe(true)

		hitch:detach()
		test.expect(hitch.is_attached).toBe(false)
		test.expect(physics:get_mass(entity)).toBeNear(6000, 1)
		test.expect(physics:has_body(attachment)).toBe(true)
	end)
end)
