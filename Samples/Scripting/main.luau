--!strict
-- The sample: boxes bouncing in a cube, with a panel to watch and steer them. Run it with
--   Apogee.Runtime --run Samples/Scripting/main.luau --frames 120 --headless
local sim = require("./sim")

local settings: sim.Settings = { speed = 1, bound = 10 }
local handles = sim.install(world, settings)
sim.spawn(world, handles, 50)

print(`{sim.count(world, handles)} boxes in a cube of {settings.bound * 2}`)

-- Drawn every frame inside the engine's ImGui frame; with no frame (a headless run draws one but shows nothing) the calls do nothing.
devtools.on_gui(function()
	if imgui.begin_window("Bouncing boxes", nil, { "always_auto_resize" }) then
		imgui.text(`boxes: {sim.count(world, handles)}`)
		local _, speed = imgui.slider_float("Speed", settings.speed, 0, 5)
		settings.speed = speed
		if imgui.button("Spawn 10 more") then
			sim.spawn(world, handles, 10)
		end
		devtools.watch("speed", settings.speed)
		devtools.watch("frame", world.frame)
	end
	imgui.end_window()
end)
