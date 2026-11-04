-- The language as the engine runs it: the sandbox, vectors and require.
local test = require("@engine/test")
local helper = require("./modules/helper")

test.describe("the sandbox", function()
	test.it("keeps the built-ins read-only", function()
		test.expect(function()
			string.len = nil
		end).toThrow("readonly")
	end)

	test.it("has no dynamic code", function()
		test.expect(loadstring).toBeNil()
		test.expect(getfenv).toBeNil()
		test.expect(setfenv).toBeNil()
	end)

	test.it("keeps the globals a script sets to itself", function()
		some_global = 5
		test.expect(some_global).toBe(5)
	end)
end)

test.describe("vectors", function()
	test.it("have methods and arithmetic", function()
		local v = Vector3(3, 4, 0)
		test.expect(v:length()).toBe(5)
		test.expect(v.x + v.y).toBe(7)
		test.expect(v + Vector3(1, 1, 1)).toBe(Vector3(4, 5, 1))
		test.expect(v * 2).toBe(Vector3(6, 8, 0))
		test.expect(Vector3(1, 0, 0):cross(Vector3(0, 1, 0))).toBe(Vector3(0, 0, 1))
		test.expect(Vector3(1, 2, 3):dot(Vector3(4, 5, 6))).toBe(32)
	end)

	test.it("default to zero", function()
		test.expect(Vector3()).toBe(Vector3(0, 0, 0))
	end)
end)

test.describe("require", function()
	test.it("loads modules relative to the file", function()
		test.expect(helper.double(21)).toBe(42)
	end)

	test.it("returns the same module every time", function()
		test.expect(require("./modules/helper")).toBe(helper)
	end)

	test.it("reports a module that is missing", function()
		test.expect(function()
			require("./modules/nothing")
		end).toThrow("module not found")
	end)
end)
