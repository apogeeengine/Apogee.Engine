--!nonstrict
-- The CI's game: a RmlUi document with a script of its own and a data model (fonts, the UI shader), an ImGui panel, and marks in the user folder so the run can be checked.
local state = { clicks = 0 }
local model = ui:open_data_model("fixture", state) -- before the document that uses it loads

local document = ui:load_document("mod://core/ui/fixture.rml")
assert(document, "the fixture's document loads")
document:show()

devtools.on_gui(function()
	if imgui.begin_window("Fixture") then
		imgui.text("ImGui in a shipped game")
		local pressed = imgui.button("Click")
		if pressed then
			state.clicks += 1
			model:dirty("clicks")
		end
	end
	imgui.end_window()
end)

assert(fs.write_text("user://ran.txt", "core ran"))
print("fixture game: ready")
