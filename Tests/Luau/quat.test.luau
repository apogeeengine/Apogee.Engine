-- Rotations and world transforms as scripts see them: the Quat userdata and the Transform component.
local test = require("@engine/test")

local HALF_PI = math.pi / 2

local function near(a: vector, b: vector, epsilon: number?)
	local e = epsilon or 1e-5
	test.expect(math.abs(a.x - b.x) < e and math.abs(a.y - b.y) < e and math.abs(a.z - b.z) < e).toBe(true)
end

test.describe("Quat", function()
	test.it("has an identity and reads its components", function()
		local q = Quat.new(0.1, 0.2, 0.3, 0.9)
		test.expect(q.x).toBeNear(0.1, 1e-6)
		test.expect(q.w).toBeNear(0.9, 1e-6)
		test.expect(Quat.identity * Vector3(1, 2, 3)).toBe(Vector3(1, 2, 3))
		test.expect(Quat.identity.w).toBe(1)
	end)

	test.it("rotates a vector about an axis", function()
		-- A quarter turn about +Y sends +Z to +X (right-handed).
		local q = Quat.axisAngle(Vector3(0, 1, 0), HALF_PI)
		near(q * Vector3(0, 0, 1), Vector3(1, 0, 0))
		near(q * Vector3(1, 0, 0), Vector3(0, 0, -1))
		-- The axis need not be a unit vector.
		near(Quat.axisAngle(Vector3(0, 5, 0), HALF_PI) * Vector3(0, 0, 1), Vector3(1, 0, 0))
	end)

	test.it("composes right to left: (a * b) * v == a * (b * v)", function()
		local a = Quat.axisAngle(Vector3(0, 1, 0), 0.7)
		local b = Quat.axisAngle(Vector3(1, 0, 0), -0.4)
		local v = Vector3(1, 2, 3)
		near((a * b) * v, a * (b * v))
		test.expect((a * b) == (b * a)).toBe(false)
	end)

	test.it("inverts, normalizes and compares", function()
		local q = Quat.axisAngle(Vector3(1, 1, 0), 1.1)
		near((q:inverse() * q) * Vector3(1, 2, 3), Vector3(1, 2, 3))
		test.expect(q == Quat.axisAngle(Vector3(1, 1, 0), 1.1)).toBe(true)
		local scaled = Quat.new(0, 0, 0, 2):normalized()
		test.expect(scaled.w).toBeNear(1, 1e-6)
		test.expect(q:dot(q)).toBeNear(1, 1e-5)
	end)

	test.it("interpolates along the shortest arc", function()
		local a = Quat.identity
		local b = Quat.axisAngle(Vector3(0, 1, 0), HALF_PI)
		local half = a:slerp(b, 0.5)
		near(half * Vector3(0, 0, 1), Quat.axisAngle(Vector3(0, 1, 0), HALF_PI / 2) * Vector3(0, 0, 1))
		near(a:slerp(b, 0) * Vector3(0, 0, 1), Vector3(0, 0, 1))
		near(a:slerp(b, 1) * Vector3(0, 0, 1), Vector3(1, 0, 0))
	end)

	test.it("converts to and from Euler angles (pitch, yaw, roll)", function()
		local angles = Vector3(0.3, -1.2, 0.5)
		near(Quat.euler(angles):toEuler(), angles, 1e-4)
		-- A pure yaw turns +Z like a rotation about +Y.
		near(Quat.euler(Vector3(0, HALF_PI, 0)) * Vector3(0, 0, 1), Vector3(1, 0, 0))
	end)

	test.it("looks down -Z at identity and can be aimed", function()
		near(Quat.identity * Vector3(0, 0, -1), Vector3(0, 0, -1))
		local aimed = Quat.lookAt(Vector3(1, 0, 0), Vector3(0, 1, 0))
		near(aimed * Vector3(0, 0, -1), Vector3(1, 0, 0))
		near(aimed * Vector3(0, 1, 0), Vector3(0, 1, 0))
		-- Straight up still gives a rotation.
		near(Quat.lookAt(Vector3(0, 1, 0)) * Vector3(0, 0, -1), Vector3(0, 1, 0))
	end)

	test.it("prints and rejects what makes no sense", function()
		test.expect(tostring(Quat.new(0, 0, 0, 1))).toBe("Quat(0, 0, 0, 1)")
		test.expect(function()
			return Quat.identity * 2
		end).toThrow("cannot multiply a Quat by number")
		test.expect(function()
			return Vector3(1, 0, 0) * Quat.identity
		end).toThrow()
		test.expect(function()
			return Quat.identity.nope
		end).toThrow("not a field of Quat")
		test.expect(function()
			return Quat.identity:nope()
		end).toThrow("no method nope")
		test.expect(function()
			Quat.axisAngle(Vector3(0, 0, 0), 1)
		end).toThrow("axis that is not zero")
		test.expect(function()
			Quat.new(1, 2, 3)
		end).toThrow("x, y, z and w")
		test.expect(function()
			Quat.identity.x = 1
		end).toThrow()
	end)
end)

test.describe("Transform", function()
	test.it("is a built-in component that propagates down the hierarchy", function()
		local world = test.world()
		local Transform = world:component("Transform")
		local WorldTransform = world:component("WorldTransform")
		local root = world:create_entity("root")
		local child = world:create_entity("child", root)

		world:set(root, Transform, { Position = Vector3(100, 0, 0), Rotation = Quat.axisAngle(Vector3(0, 1, 0), HALF_PI), Scale = Vector3(2, 2, 2) })
		world:set(child, Transform, { Position = Vector3(0, 0, 1), Rotation = Quat.identity, Scale = Vector3(1, 1, 1) })
		world:progress(0.016)

		local at = world:get(child, WorldTransform)
		-- (0, 0, 1) scaled by 2 and turned a quarter about +Y is (2, 0, 0), next to the parent at (100, 0, 0).
		near(at.Position, Vector3(102, 0, 0), 1e-4)
		near(at.Scale, Vector3(2, 2, 2))
		near(at.Rotation * Vector3(0, 0, 1), Vector3(1, 0, 0))
	end)

	test.it("takes position and rotation fields in a script's own component", function()
		local world = test.world()
		local Body = world:component("Body", { at = "dvec3", facing = "quat" })
		local entity = world:create_entity()
		world:set(entity, Body, { at = Vector3(1, 2, 3), facing = Quat.axisAngle(Vector3(0, 1, 0), 1) })

		local body = world:get(entity, Body)
		test.expect(body.at).toBe(Vector3(1, 2, 3))
		test.expect(body.facing == Quat.axisAngle(Vector3(0, 1, 0), 1)).toBe(true)
		test.expect(function()
			world:set(entity, Body, { facing = Vector3(0, 0, 0) })
		end).toThrow("expected Quat, got vector")
	end)
end)
