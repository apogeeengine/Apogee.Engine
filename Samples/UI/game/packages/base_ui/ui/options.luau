--!nonstrict
local state = require("../lib/state")
local options = require("../lib/options")

function on_theme()
	options.toggle_theme()
end

function on_volume(delta)
	options.change_volume(delta)
end

function on_back()
	state.close_options()
end
