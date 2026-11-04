-- The ECS from a script: components, views, queries and systems.
local test = require("@engine/test")

test.describe("components", function()
	test.it("are defined by name and hold their fields", function()
		local world = test.world()
		local Position = world:component("Position", { value = "vec3", on = "bool" })
		local entity = world:create_entity("hero")
		world:set(entity, Position, { value = Vector3(1, 2, 3), on = true })

		local position = world:get(entity, Position)
		test.expect(position.value).toBe(Vector3(1, 2, 3))
		test.expect(position.on).toBe(true)
		test.expect(world:lookup("hero")).toBe(entity)
		test.expect(world:component("Position")).toBe(Position)
	end)

	test.it("reject a field that is not there", function()
		local world = test.world()
		local Position = world:component("Position", { value = "vec3" })
		test.expect(function()
			world:set(world:create_entity(), Position, { nope = 1 })
		end).toThrow("'nope' is not a field of Position")
	end)

	test.it("are tags when they have no fields", function()
		local world = test.world()
		local Frozen = world:component("Frozen", {})
		local entity = world:create_entity()
		test.expect(world:has(entity, Frozen)).toBe(false)
		world:set(entity, Frozen)
		test.expect(world:has(entity, Frozen)).toBe(true)
		world:remove(entity, Frozen)
		test.expect(world:has(entity, Frozen)).toBe(false)
	end)
end)

test.describe("each", function()
	test.it("gives views that write in place", function()
		local world = test.world()
		local Counter = world:component("Counter", { n = "i32" })
		local entities = {}
		for i = 1, 3 do
			entities[i] = world:create_entity()
			world:set(entities[i], Counter, { n = i })
		end

		local visited = 0
		world:each({ Counter }, function(_, counter)
			visited += 1
			counter.n *= 10
		end)

		test.expect(visited).toBe(3)
		test.expect(world:get(entities[2], Counter).n).toBe(20)
	end)

	test.it("can leave out a component", function()
		local world = test.world()
		local Counter = world:component("Counter", { n = "i32" })
		local Frozen = world:component("Frozen", {})
		for i = 1, 4 do
			local entity = world:create_entity()
			world:set(entity, Counter, { n = i })
			if i > 1 then
				world:set(entity, Frozen)
			end
		end

		test.expect(world:count({ Counter })).toBe(4)
		test.expect(world:count({ Counter, { Frozen, "not" } })).toBe(1)
	end)

	test.it("does not let a view outlive its call", function()
		local world = test.world()
		local Counter = world:component("Counter", { n = "i32" })
		world:set(world:create_entity(), Counter, { n = 1 })
		local kept
		world:each({ Counter }, function(_, counter)
			kept = counter
		end)
		test.expect(function()
			return kept.n
		end).toThrow("only valid during the call")
	end)
end)

test.describe("systems", function()
	test.it("run in their phases", function()
		local world = test.world()
		local Marker = world:component("Marker", {})
		world:set(world:create_entity(), Marker)
		local order = {}
		world:system("Late", { Marker }, function()
			table.insert(order, "late")
		end, "post_update")
		world:system("Main", { Marker }, function()
			table.insert(order, "main")
		end)
		world:system("Early", { Marker }, function()
			table.insert(order, "early")
		end, "pre_update")

		test.frames(1)

		test.expect(order).toEqual({ "early", "main", "late" })
	end)

	test.it("see the time step", function()
		local world = test.world()
		local Clock = world:component("Clock", { t = "f64" })
		local entity = world:create_entity()
		world:set(entity, Clock, { t = 0 })
		world:system("Tick", { Clock }, function(_, clock)
			clock.t += world.delta_time
		end)

		test.frames(60)

		test.expect(world:get(entity, Clock).t).toBeNear(1, 1e-3)
	end)
end)

test.describe("scenes", function()
	test.it("are saved as text and loaded again, with the hierarchy, components and a reference between entities", function()
		local world = test.world()
		local Health = world:component("Health", { current = "i32", max = "i32" })
		local Link = world:component("Link", { target = "entity" })
		local Frozen = world:component("Frozen", {})
		local root = world:create_entity("root")
		local child = world:create_entity("child", root)
		world:set(root, Health, { current = 7, max = 9 })
		world:set(child, Link, { target = root })
		world:add(child, Frozen)

		local text = world:save_scene()
		test.expect(type(text)).toBe("string")
		world:destroy(child)
		world:destroy(root)
		test.expect(#world:roots()).toBe(0)

		local loaded = world:load_scene(text)
		test.expect(#loaded).toBe(2)
		local root2 = world:lookup("root")
		local child2 = world:lookup("root.child")
		test.expect(world:get(root2, Health).current).toBe(7)
		test.expect(world:get(child2, Link).target).toBe(root2)
		test.expect(world:has(child2, Frozen)).toBe(true)
		test.expect(world:children(root2)[1]).toBe(child2)
		test.expect(world:get_parent(child2)).toBe(root2)
	end)

	test.it("reject text that is not a scene", function()
		local world = test.world()
		test.expect(function() world:load_scene("not a scene") end).toThrow("not valid JSON")
	end)
end)
