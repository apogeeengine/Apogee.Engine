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

-- A scene and a definition file. A pack made with --cooked holds them as binary XML at the same paths, so they are read with world:load_scene_file, defs and xml.load, which take a text file
-- and a cooked one alike (fs.read_text of a cooked file would give the bytes).
local made = world:load_scene_file("mod://core/scenes/start.ascene")
local camera = world:lookup("camera")
local MainCamera = world:component("MainCamera")
assert(camera and world:has(camera, MainCamera), "the scene's camera is there")
assert(world:lookup("camera.marker"), "and its child")
assert(fs.write_text("user://scene.txt", #made .. " entities, a main camera and its child"))

local crate = assert(defs.get("Crate", "fixture"), "the definition is loaded")
local file = assert(xml.load("mod://core/data/crates.xml"))
assert(fs.write_text("user://def.txt", file.children[1].attrs.id .. " weighs " .. crate.weight .. ", " .. #crate.Tag .. " tags, read from " .. file.name))

assert(fs.write_text("user://ran.txt", "core ran"))
print("fixture game: ready")
