-- The dev tools' tables, when the host has them: with no ImGui frame open every call does nothing, so tool code can stay in a game.
local test = require("@engine/test")

if imgui == nil then
	return
end

test.describe("imgui without a frame", function()
	test.it("is not drawing", function()
		test.expect(devtools.frame_open()).toBe(false)
	end)

	test.it("returns what it was given", function()
		local visible, open = imgui.begin_window("Ghost", true)
		test.expect(visible).toBe(false)
		test.expect(open).toBe(true)

		local changed, on = imgui.checkbox("On", true)
		test.expect(changed).toBe(false)
		test.expect(on).toBe(true)

		local _, value = imgui.slider_float("Speed", 0.25, 0, 1)
		test.expect(value).toBe(0.25)

		local _, name = imgui.input_text("Name", "rex")
		test.expect(name).toBe("rex")

		local _, index = imgui.combo("Pick", 2, { "a", "b", "c" })
		test.expect(index).toBe(2)

		test.expect(imgui.button("Press")).toBe(false)
		imgui.text("nothing happens")
		imgui.end_window()
	end)

	test.it("still checks what it is given", function()
		test.expect(function()
			imgui.begin_window("x", nil, { "no_such_flag" })
		end).toThrow("is not a ImGuiWindowFlags")
	end)
end)
