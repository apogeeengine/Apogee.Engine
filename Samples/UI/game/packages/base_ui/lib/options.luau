--!nonstrict
-- The options screen's data: the "options" data model that ui/options.rml shows, kept in step with the state. `open` has to run before the options document loads.
local state = require("./state")

local M = {}

local data = { theme = "dark", volume_text = "80%", volume_width = "80%" }
local model = nil

local function refresh()
	data.theme = state.theme
	data.volume_text = `{math.floor(state.volume * 100 + 0.5)}%`
	data.volume_width = data.volume_text
	if model then
		model:dirty()
	end
end

function M.open()
	model = ui:open_data_model("options", data)
	refresh()
end

function M.toggle_theme()
	state.set_theme(state.next_theme(state.theme))
	refresh()
end

function M.change_volume(delta: number)
	state.volume = state.change_volume(state.volume, delta)
	refresh()
end

return M
