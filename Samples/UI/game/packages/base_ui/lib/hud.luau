--!nonstrict
-- The HUD's data: the "hud" data model that ui/hud.rml shows ({{speed}}, the minimap dot), and the slots other packages put panels in. `open` has to run before the HUD document loads.
local state = require("./state")

local MINIMAP = 112
local EXTENT = 10 -- meters from the middle of the minimap to an edge

local M = {}

local data = { speed = "0 km/h", top_speed = "0 km/h", fps = 0, ball_left = "52px", ball_top = "52px" }
local model = nil

--- The HUD document, once it is loaded (set by `screens`).
M.document = nil

--- Opens the data model the HUD document uses.
function M.open()
	model = ui:open_data_model("hud", data)
end

--- Puts the game's numbers in the model: the elements that show them change at the next frame.
function M.update(speed: number, top_speed: number, fps: number, ball_x: number, ball_z: number)
	if not model then
		return
	end
	data.speed = state.format_speed(speed)
	data.top_speed = state.format_speed(top_speed)
	data.fps = fps
	local left, top = state.minimap_position(ball_x, ball_z, EXTENT, MINIMAP)
	data.ball_left = `{math.floor(left - 4)}px`
	data.ball_top = `{math.floor(top - 4)}px`
	model:dirty()
end

--- The element of a slot (a place in the HUD where other packages put panels: "hud_right"), or nil before the HUD exists.
function M.slot(name: string)
	return if M.document then M.document:by_id("slot_" .. name) else nil
end

--- Puts a panel in a slot: `rml` is its RML (one element, with the ids it needs), `rcss` the rules it needs. Returns the panel's element, nil when there is no such slot.
function M.add_panel(slot_name: string, rml: string, rcss: string?)
	local slot = M.slot(slot_name)
	if not slot then
		return nil
	end
	if rcss then
		M.document:add_style(rcss)
	end
	local holder = M.document:create_element("div")
	holder.inner_rml = rml
	slot:append(holder)
	return holder
end

return M
