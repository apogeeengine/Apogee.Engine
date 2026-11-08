--!strict
-- The simulation of the sample: boxes that move and bounce inside a cube. Everything it does to the world is here, so the tests can run it.

local sim = {}

export type Settings = { speed: number, bound: number }
export type Handles = { Position: number, Velocity: number, Box: number, settings: Settings, seed: number }

-- Defines the components and installs the systems into `world`. `settings` is read by the systems every frame, so changing it changes the simulation.
function sim.install(world: any, settings: Settings): Handles
	local Position = world:component("Position", { value = "vec3" })
	local Velocity = world:component("Velocity", { value = "vec3" })
	local Box = world:component("Box", {}) -- a tag: what makes an entity one of the boxes

	world:system("Move", { Position, Velocity }, function(_, position: any, velocity: any)
		position.value += velocity.value * (settings.speed * world.delta_time)
	end)

	-- After the move, in a later phase: a box past a wall is put back on it and turned around.
	world:system("Bounce", { Position, Velocity }, function(_, position: any, velocity: any)
		local p: vector, v: vector, bound = position.value, velocity.value, settings.bound
		local x, y, z = p.x, p.y, p.z
		local vx, vy, vz = v.x, v.y, v.z
		if math.abs(x) > bound then
			x, vx = math.sign(x) * bound, -math.sign(x) * math.abs(vx)
		end
		if math.abs(y) > bound then
			y, vy = math.sign(y) * bound, -math.sign(y) * math.abs(vy)
		end
		if math.abs(z) > bound then
			z, vz = math.sign(z) * bound, -math.sign(z) * math.abs(vz)
		end
		position.value = Vector3(x, y, z)
		velocity.value = Vector3(vx, vy, vz)
	end, "post_update")

	return { Position = Position, Velocity = Velocity, Box = Box, settings = settings, seed = 1 }
end

-- A number in [0, 1) that is the same sequence every run (Park-Miller, small enough to be exact in a double).
local function random(handles: Handles): number
	handles.seed = (handles.seed * 16807) % 2147483647
	return handles.seed / 2147483647
end

-- Makes `count` boxes at random places in the cube, moving in random directions.
function sim.spawn(world: any, handles: Handles, count: number)
	local half = handles.settings.bound / 2
	for _ = 1, count do
		local box = world:create_entity()
		world:set(box, handles.Position, { value = Vector3((random(handles) * 2 - 1) * half, (random(handles) * 2 - 1) * half, (random(handles) * 2 - 1) * half) })
		world:set(box, handles.Velocity, { value = Vector3(random(handles) * 2 - 1, random(handles) * 2 - 1, random(handles) * 2 - 1) })
		world:set(box, handles.Box)
	end
end

function sim.count(world: any, handles: Handles): number
	return world:count({ handles.Box })
end

return sim
