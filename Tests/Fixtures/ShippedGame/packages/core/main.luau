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

-- Prefabs: the scene holds an instance of one that holds another (stack, with an override of the lid's handle), and a script makes one of its own. A cooked pack holds the prefab files as binary XML at the same paths.
local Transform = world:component("Transform")
local function height(path: string): number
	local entity = assert(world:lookup(path), path .. " is there")
	return math.floor(world:get(entity, Transform).Position.y * 100 + 0.5)
end
local stack = assert(world:lookup("stack"), "the scene's instance is there")
assert(world:prefab_of(stack), "and is an instance")
local made_crate = world:instantiate("mod://core/prefabs/crate.aprefab")
assert(world:prefab_of(made_crate) and world:prefab_of(world:lookup("Crate.Lid")), "the crate, and the lid inside it, are instances")
assert(fs.write_text("user://prefab.txt", "scene handle " .. height("stack.Lid.Handle") .. ", made handle " .. height("Crate.Lid.Handle") .. ", " .. #world:children(made_crate) .. " children, " .. #world:children(stack) .. " in the scene's"))

local crate = assert(defs.get("Crate", "fixture"), "the definition is loaded")
local file = assert(xml.load("mod://core/data/crates.xml"))
assert(fs.write_text("user://def.txt", file.children[1].attrs.id .. " weighs " .. crate.weight .. ", " .. #crate.Tag .. " tags, read from " .. file.name))

assert(fs.write_text("user://ran.txt", "core ran"))
print("fixture game: ready")
