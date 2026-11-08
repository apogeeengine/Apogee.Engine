--!nonstrict
-- The scene behind the UI: a ground, a pyramid of crates and a ball that rolls into it, drawn as physics debug wireframes (the sample ships no models). The world is paused until the
-- game starts; every frame a system reads the ball into the HUD's signals.
local state = require("./state")
local hud = require("./hud")

local Transform = world:component("Transform")
local CameraComponent = world:component("CameraComponent")
local MainCamera = world:component("MainCamera")
local Ticker = world:component("Ticker", {})

local M = {}

local entities = {}
local ball = nil
local top_speed = 0

local function place(position: vector, name: string)
	local entity = world:create_entity(name)
	world:set(entity, Transform, { Position = position, Rotation = Quat.identity, Scale = Vector3(1, 1, 1) })
	return entity
end

local function body(entity, shape, motion: string)
	local desc = BodyDesc.new()
	desc.shape = shape
	desc.motion = motion
	physics:add_body(entity, desc)
	table.insert(entities, entity)
end

--- Builds the scene (again): throws away the old one. The ball waits at the near end until `launch`.
function M.reset()
	for _, entity in entities do
		if world:is_alive(entity) then
			if physics:has_body(entity) then
				physics:remove_body(entity)
			end
			world:destroy(entity)
		end
	end
	entities = {}
	top_speed = 0
	state.speed = 0
	state.top_speed = 0

	body(place(Vector3(0, -0.5, 0), "ground"), PhysicsShape.box(Vector3(12, 0.5, 12)), "static")

	-- A pyramid of crates: 4 on the bottom row, 3, 2, 1.
	for row = 0, 3 do
		for column = 0, 3 - row do
			local x = (column - (3 - row) / 2) * 1.05
			body(place(Vector3(x, 0.5 + row * 1.02, -4), `crate {row}.{column}`), PhysicsShape.box(Vector3(0.5, 0.5, 0.5)), "dynamic")
		end
	end

	ball = place(Vector3(0, 0.6, 5), "ball")
	body(ball, PhysicsShape.sphere(0.6), "dynamic")

	physics.debug_draw = true
end

--- Sends the ball at the crates.
function M.launch()
	if ball and world:is_alive(ball) then
		physics:set_linear_velocity(ball, Vector3(0, 0, -16))
	end
end

--- A kick for the ball: 8 m/s more along -z, whatever its mass is (an impulse of 8 * mass). A ball at rest is woken up, and the pyramid it hits next is knocked about.
function M.boost()
	if ball and world:is_alive(ball) and physics:has_body(ball) then
		physics:wake(ball)
		physics:add_impulse(ball, Vector3(0, 0, -8 * physics:get_mass(ball)))
	end
end

-- The camera: high behind the ball's start, looking at the pyramid. It exists for the whole run, so the menus have a scene behind them.
local eye = place(Vector3(0, 4.5, 11), "eye")
world:set(eye, CameraComponent, { FovDegrees = 60, Near = 0.1, ClusterFar = 100 })
world:set(eye, Transform, { Position = Vector3(0, 4.5, 11), Rotation = Quat.lookAt(Vector3(0, -0.4, -1), Vector3(0, 1, 0)), Scale = Vector3(1, 1, 1) })
world:add(eye, MainCamera)

world:add(world:create_entity("hud ticker"), Ticker)
local fps = 0
local ball_x, ball_z = 0, 0
world:system("ReadBall", { Ticker }, function()
	local dt = world.delta_time
	if dt > 0 then
		fps = math.floor(1 / dt + 0.5)
	end
	if ball and world:is_alive(ball) and physics:has_body(ball) then
		local speed = vector.magnitude(physics:get_linear_velocity(ball))
		state.speed = speed
		if speed > top_speed then
			top_speed = speed
			state.top_speed = speed
		end
		local position = physics:pose(ball)
		ball_x, ball_z = position.x, position.z
	end
	hud.update(state.speed, state.top_speed, fps, ball_x, ball_z)
end)

M.reset()

return M
