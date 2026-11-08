--!nonstrict
-- The UI sample's game: a physics scene (lib/scene.luau) and four RML documents (ui/) that lib/screens.luau shows and hides as the state's screen changes. Documents carry their own scripts and
-- data models; mods put panels in the HUD's slots (lib/hud.luau).
local state = require("./lib/state")
local scene = require("./lib/scene")
local screens = require("./lib/screens")

-- Escape is this game's key: it opens and closes the pause menu, and does not end the run.
game.set_escape_quits(false)

-- A test or a demo can start somewhere else than the main menu: user://autostart.txt holds the screen's name.
if fs.exists("user://autostart.txt") then
	local name = fs.read_text("user://autostart.txt")
	if name then
		state.screen = string.gsub(name, "%s+", "")
	end
end

-- The scene follows the screen: still behind the menus, running while playing. It is built and the ball launched the first time the game is playing, and built again when the player goes back
-- to the main menu.
local started = false
local function follow(screen: string)
	physics.paused = screen ~= "playing"
	if screen == "playing" and not started then
		started = true
		scene.reset()
		scene.launch()
	elseif screen == "menu" and started then
		started = false
		scene.reset()
	end
end

state.on_screen(follow)
screens.start()
follow(state.screen)

print("base_ui: ready")
