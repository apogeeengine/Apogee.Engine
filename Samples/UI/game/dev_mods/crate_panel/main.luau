--!nonstrict
-- A mod's UI: an RML fragment and its rules, put in the HUD's right-hand slot. The mod uses the game's modules (@base_ui/...) and the engine's `ui`; nothing in the base game knows it exists.
local hud = require("@base_ui/lib/hud")
local scene = require("@base_ui/lib/scene")
local state = require("@base_ui/lib/state")

local panel = hud.add_panel("hud_right", assert(fs.read_text("mod://crate_panel/panel.rml")), fs.read_text("mod://crate_panel/panel.rcss"))
if not panel then
	print("crate_panel: the HUD has no right-hand slot")
	return
end

-- The mod listens on its own elements (the fragment is the mod's, so it does not use onclick attributes, which would be compiled as this mod too).
local boosts = 0
local boost = panel:by_id("crate_boost")
boost:listen("click", function()
	scene.boost()
	boosts += 1
	boost.inner_rml = `Boost ({boosts})`
end)

-- The best speed follows the game every frame.
local best = panel:by_id("crate_best")
ui:on_update(function()
	best.inner_rml = state.format_speed(state.top_speed)
end)

print("crate_panel: added its panel to the HUD")
