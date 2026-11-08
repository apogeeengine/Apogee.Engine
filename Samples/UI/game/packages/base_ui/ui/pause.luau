--!nonstrict
local state = require("../lib/state")

function on_resume()
	state.set_screen("playing")
end

function on_options()
	state.open_options("paused")
end

function on_menu()
	state.to_menu()
end
