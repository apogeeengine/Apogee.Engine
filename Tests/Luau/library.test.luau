--!strict
-- The test library itself: what a test can ask of a value, and how a case waits for frames.
local test = require("@engine/test")

test.describe("expect", function()
	test.it("compares values", function()
		test.expect(1 + 1).toBe(2)
		test.expect({ 1, 2, { a = 3 } }).toEqual({ 1, 2, { a = 3 } })
		test.expect(0.1 + 0.2).toBeNear(0.3)
		test.expect("hello world").toContainText("lo wo")
		test.expect(nil).toBeNil()
		test.expect(0).toBeTruthy() -- 0 is true in Luau
		test.expect(false).toBeFalsy()
	end)

	test.it("negates with never", function()
		test.expect(1).never.toBe(2)
		test.expect({ 1 }).never.toEqual({ 2 })
		test.expect("abc").never.toContainText("x")
	end)

	test.it("checks that a function throws", function()
		test.expect(function()
			error("boom")
		end).toThrow("boom")
		test.expect(function() end).never.toThrow()
	end)

	test.it("says what was expected when it fails", function()
		local ok, err = pcall(function()
			test.expect(1).toBe(2)
		end)
		test.expect(ok).toBe(false)
		test.expect(err).toContainText("expected: 1 to be 2")
	end)
end)

test.describe("describe", function()
	test.describe("nests", function()
		test.it("and the cases run in order", function()
			test.expect(true).toBe(true)
		end)
	end)
end)

test.describe("cases", function()
	test.it("wait for frames of a world of their own", function()
		local world = test.world()
		local Counter = world:component("Counter", { n = "i32" })
		local entity = world:create_entity()
		world:set(entity, Counter, { n = 0 })
		world:system("Increment", { Counter }, function(_, counter)
			counter.n += 1
		end)

		test.frames(5)

		test.expect(world:get(entity, Counter).n).toBe(5)
		test.expect(world.frame).toBe(5)
	end)

	test.it("each get a world that is a fresh one", function()
		test.expect(test.world().frame).toBe(0)
	end)
end)
