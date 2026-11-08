--!nonstrict
-- The main menu's script: the functions its onclick attributes call. The game's logic is in lib/state.
local state = require("../lib/state")

function on_start()
	state.start()
end

function on_options()
	state.open_options("menu")
end

function on_quit()
	game.quit()
end
