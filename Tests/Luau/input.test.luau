--!strict
-- InputActionMap and InputActionRouter as scripts see them, with an action catalog of about 45 actions.
local test = require("@engine/test")

-- Only in hosts that have the input library (the runtime does; the scripting tests' host does not).
if InputActionMap == nil then
	return
end

local MOVING = { "on_foot", "vehicle" }
local DRIVING = { "vehicle" }
local ON_FOOT_SITUATION = { "world", "on_foot" }
local DRIVING_SITUATION = { "world", "vehicle", "mouse_tool" }
local MENU_SITUATION = { "menu" }

-- id, contexts, defaults, analog
local ACTIONS: { { any } } = {
	{ "move_forward", MOVING, { "W", "Joy LY-" }, true },
	{ "move_back", MOVING, { "S", "Joy LY+" }, true },
	{ "move_left", MOVING, { "A", "Joy LX-" }, true },
	{ "move_right", MOVING, { "D", "Joy LX+" }, true },
	{ "run", { "on_foot" }, { "Shift", "Joy L3" }, true },
	{ "brake", DRIVING, { "Space", "Joy LT" }, true },
	{ "attach", DRIVING, { "G", "Joy Down" } },
	{ "select_implement", DRIVING, { "T", "Joy R3" } },
	{ "lower", DRIVING, { "V", "Joy A" } },
	{ "fold", DRIVING, { "N" } },
	{ "move_parts", DRIVING, { "Shift+N" } },
	{ "cover", DRIVING, { "C" } },
	{ "turn_on", DRIVING, { "B", "Joy B" } },
	{ "unload", DRIVING, { "U", "Joy Up" } },
	{ "tip_side", DRIVING, { "Shift+U" } },
	{ "tip_ground", DRIVING, { "Ctrl+U" } },
	{ "cycle_seed", DRIVING, { "X" } },
	{ "steering", DRIVING, { "K" } },
	{ "tool_up", DRIVING, { "Up" }, true },
	{ "tool_down", DRIVING, { "Down" }, true },
	{ "tool_left", DRIVING, { "Left" }, true },
	{ "tool_right", DRIVING, { "Right" }, true },
	{ "tool_ik", DRIVING, { "I" } },
	{ "tool_mouse", DRIVING, { "Mouse Right" }, true },
	{ "tool_action", { "mouse_tool" }, { "Mouse Left" } },
	{ "lights", DRIVING, { "L" } },
	{ "lights_back", DRIVING, { "Alt+L" } },
	{ "high_beam", DRIVING, { "Ctrl+Shift+L" } },
	{ "road_lights", DRIVING, {} },
	{ "work_lights_front", DRIVING, {} },
	{ "work_lights_rear", DRIVING, {} },
	{ "beacons", DRIVING, { "Shift+L" } },
	{ "turn_left", DRIVING, { "Ctrl+Q" } },
	{ "turn_right", DRIVING, { "Ctrl+E" } },
	{ "hazards", DRIVING, { "Ctrl+L" } },
	{ "use", MOVING, { "R", "Joy X" } },
	{ "helper", DRIVING, { "H" } },
	{ "enter", MOVING, { "F", "Joy Y" } },
	{ "next_vehicle", MOVING, { "Tab", "Joy Right" } },
	{ "prev_vehicle", MOVING, { "Shift+Tab", "Joy Left" } },
	-- The game's: a camera key and a menu key on the same Q, and the camera's pan.
	{ "camera_left", { "world" }, { "Q" } },
	{ "tab_left", { "menu" }, { "Q" } },
	{ "pan", { "world" }, { "Mouse Middle" }, true },
}

local NONE: { string } = {}
local CTRL: { string } = { "ctrl" }
local SHIFT: { string } = { "shift" }

local function catalog(): any
	local map = InputActionMap.new()
	map:add_situation(ON_FOOT_SITUATION)
	map:add_situation(DRIVING_SITUATION)
	map:add_situation(MENU_SITUATION)
	for _, a in ACTIONS do
		map:add_action(a[1], a[2], a[3], a[4] == true)
	end
	return map
end

local function router(contexts: { string }, map: any?): any
	local r = InputActionRouter.new()
	r.map = map or catalog()
	r.contexts = contexts
	return r
end

test.describe("bindings", function()
	test.it("read from their text form", function()
		local cases: { { any } } = {
			{ "W", "W", NONE, "press" },
			{ "Shift+Tab", "Tab", SHIFT, "press" },
			{ "hold ctrl+alt+V", "V", { "ctrl", "alt" }, "hold" },
			{ "Double Shift", "Shift", NONE, "double_tap" },
			{ "mouse wheel up", "Mouse Wheel Up", NONE, "press" },
			{ "Ctrl+Mouse Left", "Mouse Left", CTRL, "press" },
			{ "Joy LX-", "Joy LX-", NONE, "press" },
			{ "Hold joy a", "Joy A", NONE, "hold" },
		}
		for _, c in cases do
			local input, modifiers, trigger = InputActionMap.parse_binding(c[1])
			test.expect(input).toBe(c[2])
			test.expect(modifiers).toEqual(c[3])
			test.expect(trigger).toBe(c[4])
		end
		test.expect(InputActionMap.normalize_binding(" hold SHIFT+tab")).toBe("Hold Shift+Tab")
	end)

	test.it("refuse wrong ones", function()
		for _, text in { "", "Ctrl+", "Hold Alt+", "Mouse Thumb", "Joy Z", "Shift+Joy A", "Banana" } do
			test.expect(InputActionMap.binding_error(text)).never.toBe("")
		end
		test.expect(InputActionMap.binding_error("Ctrl+Shift+F5")).toBe("")
	end)

	test.it("of the defaults clash nowhere", function()
		test.expect(#catalog():get_all_conflicts()).toBe(0)
	end)

	test.it("conflict per context", function()
		local map = catalog()
		-- Q rotates the camera walking and driving, so lowering on it would clash; the menu's Q never meets them.
		test.expect(map:get_conflicts("lower", "Q")).toEqual({ "camera_left" })
		test.expect(#map:get_conflicts("tab_left", "Tab")).toBe(0)
		-- Different modifiers or triggers are told apart; an analog action takes its input whatever they are.
		test.expect(#map:get_conflicts("lower", "Ctrl+V")).toBe(0)
		test.expect(#map:get_conflicts("lower", "Hold B")).toBe(0)
		test.expect(map:get_conflicts("lights", "Ctrl+A")).toEqual({ "move_left" })

		map:set_bindings("use", { "Q" })
		test.expect(map:get_all_conflicts()).toEqual({ "use\tcamera_left\tQ" })
	end)

	test.it("save what differs from the defaults and load it back", function()
		local map = catalog()
		map:set_bindings("lights", { "Hold L", "Joy RB" })
		local text = map:save_text()
		test.expect(text).toBe("lights = Hold L | Joy RB\n")
		local other = catalog()
		local errors = other:load_text(text .. "nope = X\nlower = Joy Q\n")
		test.expect(other:get_bindings("lights")).toEqual({ "Hold L", "Joy RB" })
		test.expect(#errors).toBe(2)
	end)

	test.it("raise errors a script can catch", function()
		local map = catalog()
		test.expect(function()
			map:set_bindings("lights", { "Banana" })
		end).toThrow("unknown key")
		test.expect(function()
			map:add_action("lights", DRIVING, {})
		end).toThrow("defined twice")
	end)
end)

test.describe("the router", function()
	test.it("lets the most specific modifiers win", function()
		local r = router(DRIVING_SITUATION)
		r:press("Tab", SHIFT, 0)
		test.expect(r:poll(0)).toEqual({ "prev_vehicle" })
		r:release("Tab", 0.1)

		-- Running with Shift held, F still gets in.
		r.contexts = ON_FOOT_SITUATION
		r:press("F", SHIFT, 1)
		test.expect(r:poll(1)).toEqual({ "enter" })

		-- Ctrl+Q signals left, Q alone rotates the camera.
		r.contexts = DRIVING_SITUATION
		r:press("Q", CTRL, 2)
		r:release("Q", 2.1)
		r:press("Q", NONE, 3)
		test.expect(r:poll(3)).toEqual({ "turn_left", "camera_left" })
	end)

	test.it("does what the context needs with the same key", function()
		local r = router(ON_FOOT_SITUATION)
		r:press("Q", NONE, 0)
		r:release("Q", 0.1)
		r.contexts = MENU_SITUATION
		r:press("Q", NONE, 1)
		-- Out of context: lowering on V, walking on W.
		r:press("V", NONE, 1)
		r:press("W", NONE, 1)
		test.expect(r:poll(1)).toEqual({ "camera_left", "tab_left" })
		test.expect(r:get_strength("move_forward")).toBe(0)
		r.contexts = ON_FOOT_SITUATION
		test.expect(r:get_strength("move_forward")).toBe(1)
	end)

	test.it("fires a key's press on a quick release when it also has a hold", function()
		local map = catalog()
		map:set_bindings("lights", { "L" })
		map:set_bindings("helper", { "Hold L" })
		local r = router(DRIVING_SITUATION, map)

		r:press("L", NONE, 0)
		test.expect(#r:poll(0.2)).toBe(0)
		r:release("L", 0.25)
		test.expect(r:poll(0.25)).toEqual({ "lights" })

		r:press("L", NONE, 1)
		test.expect(#r:poll(1.3)).toBe(0)
		test.expect(r:poll(1.41)).toEqual({ "helper" })
		r:release("L", 2)
		test.expect(#r:poll(2)).toBe(0)
	end)

	test.it("fires a double tap instead of two presses", function()
		local map = catalog()
		map:set_bindings("steering", { "Double L" })
		local r = router(DRIVING_SITUATION, map)

		r:press("L", NONE, 0)
		r:release("L", 0.1)
		r:press("L", NONE, 0.3)
		r:release("L", 0.35)
		test.expect(r:poll(1)).toEqual({ "steering" })

		-- A single tap fires once the second didn't come.
		r:press("L", NONE, 2)
		r:release("L", 2.1)
		test.expect(#r:poll(2.3)).toBe(0)
		test.expect(r:poll(2.41)).toEqual({ "lights" })
	end)

	test.it("reads analog actions from keys and from sticks past the dead zone", function()
		local r = router(DRIVING_SITUATION)
		r:press("Space", CTRL, 0)
		test.expect(r:is_held("brake")).toBe(true)
		test.expect(#r:poll(0)).toBe(0)

		r:axis("Joy LY", -0.6, 0)
		test.expect(r:get_strength("move_forward")).toBeNear(0.5, 1e-3)
		test.expect(r:get_strength("move_back")).toBe(0)
		r:axis("Joy LY", 0.15, 0)
		test.expect(r:get_strength("move_back")).toBe(0)
		r:axis("Joy LT", 1, 0)
		r:release("Space", 0)
		test.expect(r:is_held("brake")).toBe(true)

		r:press("Mouse Middle", NONE, 0)
		test.expect(r:is_held("pan")).toBe(true)
	end)

	test.it("gives the mouse's motion of the frame", function()
		local map = catalog()
		map:add_action("look_right", { "world" }, { "Mouse X+" }, true)
		local r = router(DRIVING_SITUATION, map)
		r:axis("Mouse X", 12, 0)
		r:axis("Mouse X", -2, 0)
		r:axis("Mouse X", 3, 0)
		test.expect(r:get_strength("look_right")).toBe(15)
		r:end_frame()
		test.expect(r:get_strength("look_right")).toBe(0)
	end)

	test.it("treats a stick pushed past half way as a button", function()
		local map = catalog()
		map:set_bindings("next_vehicle", { "Joy RX+" })
		local r = router(DRIVING_SITUATION, map)
		r:axis("Joy RX", 0.3, 0)
		test.expect(#r:poll(0)).toBe(0)
		-- Pressed at half way, released below 0.4 only.
		r:axis("Joy RX", 0.7, 0)
		r:axis("Joy RX", 0.45, 0.1)
		r:axis("Joy RX", 0.9, 0.2)
		test.expect(r:poll(0.2)).toEqual({ "next_vehicle" })
		r:axis("Joy RX", 0, 0.3)
		r:axis("Joy RX", 0.8, 0.4)
		test.expect(r:poll(0.4)).toEqual({ "next_vehicle" })
	end)

	test.it("drops what was pending on a context change or a lost focus", function()
		local map = catalog()
		map:set_bindings("helper", { "Hold H" })
		local r = router(DRIVING_SITUATION, map)
		r:press("H", NONE, 0)
		r.contexts = MENU_SITUATION
		test.expect(#r:poll(1)).toBe(0)
		r.contexts = DRIVING_SITUATION
		r:release_all()
		r:press("H", NONE, 2)
		r:release_all()
		test.expect(#r:poll(3)).toBe(0)
		test.expect(#r:get_down()).toBe(0)
	end)

	test.it("tells what was pressed for a rebinding screen, and what to show for an action", function()
		local r = router(ON_FOOT_SITUATION)
		local captured: { string } = {}
		r.captured:connect(function(binding: string)
			table.insert(captured, binding)
		end)
		r:listen()
		r:press("F", SHIFT, 0)
		r:update(0)
		test.expect(captured).toEqual({ "Shift+F" })
		r:release("F", 0.1)

		local fired: { string } = {}
		r.action_fired:connect(function(action: string)
			table.insert(fired, action)
		end)
		r:press("F", NONE, 1)
		r:update(1)
		test.expect(fired).toEqual({ "enter" })

		r:axis("Joy LY", -1, 2)
		test.expect(r:get_strength("move_forward")).toBe(1)
		test.expect(r.is_gamepad_last).toBe(true)
		test.expect(r:get_label("enter")).toBe("Pad Y")
		test.expect(InputActionRouter.label_binding("Hold Ctrl+Mouse Left")).toBe("Hold Ctrl+Left mouse")
	end)
end)

test.describe("the session's input", function()
	test.it("is a global router with a map of its own", function()
		-- `--luau-test` runs files without a game: the global is there only in sessions, so check the class instead.
		local r = InputActionRouter.new()
		test.expect(r.map).toBeNil()
		r.map = InputActionMap.new()
		test.expect(r.map:has_action("anything")).toBe(false)
	end)
end)
