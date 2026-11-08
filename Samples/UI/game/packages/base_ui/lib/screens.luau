--!nonstrict
-- The documents of the UI and which of them show: the menu, the options, the HUD and the pause menu are four RML files in ui/, loaded once, and shown and hidden as the state's screen changes.
local state = require("./state")
local hud = require("./hud")
local options = require("./options")

local M = {}

local documents = {}

local function load(name: string)
	local document = ui:load_document(`mod://base_ui/ui/{name}.rml`)
	assert(document, `ui/{name}.rml did not load`)
	documents[name] = document
	-- Escape, in whichever document has the focus.
	document:listen("keydown", function(event)
		if event:get_number("key_identifier") == 81 then -- Escape, in RmlUi's key list
			state.escape()
		end
	end)
	return document
end

local function set_shown(document, shown: boolean)
	if shown then
		document:show()
	else
		document:hide()
	end
end

local function show(screen: string)
	local playing = screen == "playing" or screen == "paused"
	set_shown(documents.menu, screen == "menu")
	set_shown(documents.options, screen == "options")
	set_shown(documents.hud, playing)
	-- Shown after the HUD, so it is drawn over it.
	set_shown(documents.pause, screen == "paused")
end

local function dress(theme: string)
	for _, document in documents do
		document.class_name = if theme == "light" then "light" else ""
	end
end

--- A document by name ("menu", "options", "hud" or "pause"), once `start` has loaded them.
function M.document(name: string)
	return documents[name]
end

--- Loads the documents (their data models first) and shows the one for the current screen.
function M.start()
	hud.open()
	options.open()
	load("menu")
	load("options")
	hud.document = load("hud")
	load("pause")
	state.on_screen(show)
	state.on_theme(dress)
	dress(state.theme)
	show(state.screen)
end

return M
