local test = require("@engine/test")
local state = require("../game/packages/base_ui/lib/state")

test.describe("state", function()
	test.it("formats a speed in kilometers an hour", function()
		test.expect(state.format_speed(0)).toBe("0 km/h")
		test.expect(state.format_speed(10)).toBe("36 km/h")
		test.expect(state.format_speed(16)).toBe("58 km/h")
	end)

	test.it("changes the volume in tenths, between 0 and 1", function()
		test.expect(state.change_volume(0.8, 0.1)).toBe(0.9)
		test.expect(state.change_volume(0.95, 0.1)).toBe(1)
		test.expect(state.change_volume(0.05, -0.1)).toBe(0)
		test.expect(state.change_volume(0.3, -0.1)).toBeNear(0.2, 1e-9)
	end)

	test.it("switches between the two themes", function()
		test.expect(state.next_theme("dark")).toBe("light")
		test.expect(state.next_theme("light")).toBe("dark")
	end)

	test.it("puts the ball on the minimap and keeps it on the edge", function()
		local x, z = state.minimap_position(0, 0, 10, 112)
		test.expect(x).toBe(56)
		test.expect(z).toBe(56)
		x = state.minimap_position(100, 0, 10, 112)
		test.expect(x).toBe(112)
	end)
end)

test.describe("the flow of screens", function()
	local function fresh()
		state.screen = "menu"
		state.options_from = "menu"
	end

	test.it("goes from the menu to the game, and escape pauses and resumes", function()
		fresh()
		local seen = {}
		state.on_screen(function(screen) table.insert(seen, screen) end)
		state.start()
		state.escape()
		state.escape()
		test.expect(seen).toEqual({ "playing", "paused", "playing" })
	end)

	test.it("returns from the options to the screen that opened them", function()
		fresh()
		state.set_screen("paused")
		state.open_options("paused")
		test.expect(state.screen).toBe("options")
		state.escape()
		test.expect(state.screen).toBe("paused")
		state.to_menu()
		test.expect(state.screen).toBe("menu")
	end)

	test.it("only says it when the screen changes", function()
		fresh()
		local count = 0
		state.on_screen(function() count += 1 end)
		state.set_screen("menu")
		test.expect(count).toBe(0)
	end)
end)
