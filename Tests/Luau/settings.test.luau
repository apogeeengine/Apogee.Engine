--!strict
-- The settings store as scripts see it: typed keys with a default, change signals, ranges and the errors. A new store lives in memory and is never written.
local test = require("@engine/test")

-- Only in hosts that have the settings (the runtime does; the scripting tests' host does not).
if Settings == nil then
	return
end

test.describe("settings", function()
	test.it("defines typed keys and reads and writes them", function()
		local settings = Settings.new()
		settings:define("game.hints", { type = "bool", default = true, help = "Show hints." })
		settings:define("game.fov", { type = "int", default = 90, min = 60, max = 120 })
		settings:define("game.sensitivity", { type = "number", default = 1.5, min = 0.1, max = 5 })
		settings:define("game.name", { type = "string", default = "Driver" })
		settings:define("game.quality", { type = "choice", default = "high", choices = { "low", "high" } })

		test.expect(settings:get("game.hints")).toBe(true)
		test.expect(settings:get("game.fov")).toBe(90)
		test.expect(settings:get("game.sensitivity")).toBe(1.5)
		test.expect(settings:get("game.name")).toBe("Driver")
		test.expect(settings:get("game.quality")).toBe("high")

		settings:set("game.hints", false)
		settings:set("game.fov", 100)
		settings:set("game.name", "Ana")
		settings:set("game.quality", "low")
		test.expect(settings:get("game.hints")).toBe(false)
		test.expect(settings:get("game.fov")).toBe(100)
		test.expect(settings:get("game.name")).toBe("Ana")
		test.expect(settings:get("game.quality")).toBe("low")

		test.expect(settings:keys("game.")).toEqual({ "game.fov", "game.hints", "game.name", "game.quality", "game.sensitivity" })
		test.expect(settings:keys("nothing.")).toEqual({})
	end)

	test.it("describes a key with info", function()
		local settings = Settings.new()
		settings:define("game.fov", { type = "int", default = 90, min = 60, max = 120, restart = true, help = "Field of view." })
		settings:define("game.quality", { type = "choice", default = "high", choices = { "low", "high" } })

		local info = settings:info("game.fov")
		test.expect(info ~= nil).toBe(true)
		test.expect(info.type).toBe("int")
		test.expect(info.default).toBe(90)
		test.expect(info.min).toBe(60)
		test.expect(info.max).toBe(120)
		test.expect(info.restart).toBe(true)
		test.expect(info.help).toBe("Field of view.")
		test.expect(info.choices).toBeNil()

		local quality = settings:info("game.quality")
		test.expect(quality.choices).toEqual({ "low", "high" })
		test.expect(quality.restart).toBe(false)
		test.expect(quality.min).toBeNil()

		test.expect(settings:info("game.nothing")).toBeNil()
	end)

	test.it("clamps numbers to their range and refuses the wrong type", function()
		local settings = Settings.new()
		settings:define("game.fov", { type = "int", default = 90, min = 60, max = 120 })
		settings:define("game.hints", { type = "bool", default = true })
		settings:define("game.quality", { type = "choice", default = "high", choices = { "low", "high" } })

		settings:set("game.fov", 500)
		test.expect(settings:get("game.fov")).toBe(120)
		settings:set("game.fov", 1)
		test.expect(settings:get("game.fov")).toBe(60)

		test.expect(function() settings:set("game.fov", 90.5) end).toThrow("integer")
		test.expect(function() settings:set("game.fov", "90") end).toThrow("game.fov")
		test.expect(function() settings:set("game.hints", 1) end).toThrow("boolean")
		test.expect(function() settings:set("game.quality", "ultra") end).toThrow("is not one of low, high")
		test.expect(function() settings:set("game.nothing", 1) end).toThrow("there is no setting 'game.nothing'")
		test.expect(function() settings:get("game.nothing") end).toThrow("there is no setting")
	end)

	test.it("refuses a definition that is wrong", function()
		local settings = Settings.new()
		test.expect(function() settings:define("loud", { type = "bool", default = true }) end).toThrow("not a setting key")
		test.expect(function() settings:define("Game.Loud", { type = "bool", default = true }) end).toThrow("not a setting key")
		test.expect(function() settings:define("game.a", { type = "float", default = 1 }) end).toThrow("type must be")
		test.expect(function() settings:define("game.a", { type = "int" }) end).toThrow("needs a default")
		test.expect(function() settings:define("game.a", { type = "int", default = 1, colour = "red" }) end).toThrow("'colour' is not part of a setting")
		test.expect(function() settings:define("game.a", { type = "int", default = 1, min = 5, max = 2 }) end).toThrow("above max")
		test.expect(function() settings:define("game.a", { type = "int", default = 99, max = 10 }) end).toThrow("outside")
		test.expect(function() settings:define("game.a", { type = "choice", default = "x", choices = { "a", "b" } }) end).toThrow("not one of")
		test.expect(function() settings:define("game.a", { type = "choice", default = "a" }) end).toThrow("choices")
		test.expect(function() settings:define("game.a", { type = "bool", default = true, choices = { "a" } }) end).toThrow("only a choice")
		test.expect(function() settings:define("audio.bus.music", { type = "bool", default = true }) end).toThrow("number")
	end)

	test.it("tells scripts when a value really changed", function()
		local settings = Settings.new()
		settings:define("game.fov", { type = "int", default = 90 })
		local changes: { string } = {}
		local seen: { any } = {}
		settings.changed:connect(function(key: string)
			table.insert(changes, key)
			table.insert(seen, settings:get(key))
		end)

		settings:set("game.fov", 90)
		test.expect(#changes).toBe(0)
		settings:set("game.fov", 100)
		settings:set("game.fov", 100)
		test.expect(changes).toEqual({ "game.fov" })
		test.expect(seen).toEqual({ 100 })
	end)

	test.it("resets one key or a whole prefix to the defaults", function()
		local settings = Settings.new()
		settings:define("game.fov", { type = "int", default = 90 })
		settings:define("game.name", { type = "string", default = "Driver" })
		settings:define("other.thing", { type = "bool", default = false })
		settings:set("game.fov", 100)
		settings:set("game.name", "Ana")
		settings:set("other.thing", true)

		settings:reset("game.fov")
		test.expect(settings:get("game.fov")).toBe(90)
		test.expect(settings:get("game.name")).toBe("Ana")

		settings:reset_all("game.")
		test.expect(settings:get("game.name")).toBe("Driver")
		test.expect(settings:get("other.thing")).toBe(true)

		settings:reset_all()
		test.expect(settings:get("other.thing")).toBe(false)
	end)

	test.it("keeps a value when a script defines its key again", function()
		local settings = Settings.new()
		settings:define("game.fov", { type = "int", default = 90, help = "old" })
		settings:set("game.fov", 110)
		settings:define("game.fov", { type = "int", default = 95, help = "new" })
		test.expect(settings:get("game.fov")).toBe(110)
		test.expect(settings:info("game.fov").help).toBe("new")
		test.expect(function() settings:define("game.fov", { type = "bool", default = true }) end).toThrow("defined as int")
	end)

	test.it("saves nothing without a file, and knows when a restart is needed", function()
		local settings = Settings.new()
		settings:define("game.hints", { type = "bool", default = true })
		settings:set("game.hints", false)
		test.expect(settings:save()).toBe(true) -- nowhere to write: nothing to do
		test.expect(settings.needs_restart).toBe(false)

		settings:define("game.vsync", { type = "bool", default = true, restart = true })
		settings:set("game.vsync", false)
		test.expect(settings.needs_restart).toBe(true)
		settings:set("game.vsync", true)
		test.expect(settings.needs_restart).toBe(false)
	end)
end)
