-- The sample's simulation, run for as many frames as each case needs.
local test = require("@engine/test")
local sim = require("../sim")

local function install(bound: number?, speed: number?)
	local world = test.world()
	local settings = { speed = speed or 1, bound = bound or 10 }
	return world, sim.install(world, settings), settings
end

-- A box's component; the case fails when it has none.
local function component(world: World, box: number, handle: number): { [string]: any }
	local value = world:get(box, handle)
	assert(value, "the box has no such component")
	return value
end

test.describe("spawning", function()
	test.it("makes the boxes it is asked for", function()
		local world, handles = install()
		sim.spawn(world, handles, 25)
		test.expect(sim.count(world, handles)).toBe(25)
		sim.spawn(world, handles, 5)
		test.expect(sim.count(world, handles)).toBe(30)
	end)

	test.it("puts them in the same places every run", function()
		local world, handles = install()
		sim.spawn(world, handles, 3)
		local other = World.new()
		local otherHandles = sim.install(other, { speed = 1, bound = 10 })
		sim.spawn(other, otherHandles, 3)

		local first, second = {}, {}
		world:each({ handles.Position }, function(_, position)
			table.insert(first, position.value)
		end)
		other:each({ otherHandles.Position }, function(_, position)
			table.insert(second, position.value)
		end)
		test.expect(#first).toBe(3)
		test.expect(first).toEqual(second)
	end)
end)

test.describe("moving", function()
	test.it("carries a box by its velocity", function()
		local world, handles = install()
		local box = world:create_entity()
		world:set(box, handles.Position, { value = Vector3(0, 0, 0) })
		world:set(box, handles.Velocity, { value = Vector3(2, 0, 0) })

		test.frames(30) -- half a second

		test.expect(component(world, box, handles.Position).value.x).toBeNear(1, 1e-3)
	end)

	test.it("is scaled by the speed setting", function()
		local world, handles, settings = install()
		local box = world:create_entity()
		world:set(box, handles.Position, { value = Vector3(0, 0, 0) })
		world:set(box, handles.Velocity, { value = Vector3(1, 0, 0) })

		settings.speed = 0
		test.frames(30)
		test.expect(component(world, box, handles.Position).value.x).toBe(0)

		settings.speed = 3
		test.frames(60)
		test.expect(component(world, box, handles.Position).value.x).toBeNear(3, 1e-3)
	end)
end)

test.describe("bouncing", function()
	test.it("turns a box around at a wall", function()
		local world, handles = install(5)
		local box = world:create_entity()
		world:set(box, handles.Position, { value = Vector3(4.9, 0, 0) })
		world:set(box, handles.Velocity, { value = Vector3(3, 0, 0) })

		test.frames(30)

		local position = component(world, box, handles.Position).value
		local velocity = component(world, box, handles.Velocity).value
		test.expect(position.x <= 5).toBe(true)
		test.expect(velocity.x < 0).toBe(true)
	end)

	test.it("keeps every box inside the cube", function()
		local world, handles = install(8, 5)
		sim.spawn(world, handles, 60)

		test.frames(600) -- ten seconds, many bounces

		local outside = 0
		world:each({ handles.Position }, function(_, position)
			local p = position.value
			if math.abs(p.x) > 8 or math.abs(p.y) > 8 or math.abs(p.z) > 8 then
				outside += 1
			end
		end)
		test.expect(outside).toBe(0)
	end)
end)
