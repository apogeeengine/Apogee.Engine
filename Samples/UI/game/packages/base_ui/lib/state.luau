--!nonstrict
-- The game's state, as plain values and the few functions that change them. Only this file knows what the screens are called and which follows which; the documents call into it, and
-- it tells whoever registered (`on_screen`, `on_theme`) when something changed, so nothing here knows what a document is. The helpers are plain functions, which is what the sample's tests exercise.
local M = {}

-- "menu", "options", "playing" or "paused".
M.screen = "menu"
-- Where Back from the options screen goes: the screen that opened it.
M.options_from = "menu"

M.volume = 0.8
M.theme = "dark"
M.speed = 0 -- the ball's speed, in meters per second
M.top_speed = 0

local screen_listeners = {}
local theme_listeners = {}

--- Calls `fn(screen)` whenever the screen changes.
function M.on_screen(fn: (string) -> ())
	table.insert(screen_listeners, fn)
end

--- Calls `fn(theme)` whenever the theme changes.
function M.on_theme(fn: (string) -> ())
	table.insert(theme_listeners, fn)
end

function M.set_screen(name: string)
	if M.screen == name then
		return
	end
	M.screen = name
	for _, fn in screen_listeners do
		fn(name)
	end
end

function M.set_theme(name: string)
	if M.theme == name then
		return
	end
	M.theme = name
	for _, fn in theme_listeners do
		fn(name)
	end
end

--- A speed for the HUD: "50 km/h".
function M.format_speed(meters_per_second: number): string
	return string.format("%d km/h", math.floor(meters_per_second * 3.6 + 0.5))
end

--- The volume after a step of `delta`, kept to tenths and to 0 to 1.
function M.change_volume(volume: number, delta: number): number
	return math.clamp(math.floor((volume + delta) * 10 + 0.5) / 10, 0, 1)
end

--- The theme a toggle switches to.
function M.next_theme(current: string): string
	return if current == "dark" then "light" else "dark"
end

--- A world position on a minimap of `size` pixels that shows `extent` meters either way from the middle, clamped to its edge.
function M.minimap_position(x: number, z: number, extent: number, size: number): (number, number)
	local function along(meters: number): number
		return math.clamp((meters / extent * 0.5 + 0.5) * size, 0, size)
	end
	return along(x), along(z)
end

-- ---- Flow: which screen follows which. ----

--- Starts (or restarts) the game from the main menu.
function M.start()
	M.set_screen("playing")
end

--- Opens the options screen from `from` ("menu" or "paused").
function M.open_options(from: string)
	M.options_from = from
	M.set_screen("options")
end

--- Leaves the options screen for the one that opened it.
function M.close_options()
	M.set_screen(M.options_from)
end

--- Back to the main menu: the next Start builds the scene again.
function M.to_menu()
	M.set_screen("menu")
end

--- Escape: pauses and resumes, and backs out of the options.
function M.escape()
	if M.screen == "playing" then
		M.set_screen("paused")
	elseif M.screen == "paused" then
		M.set_screen("playing")
	elseif M.screen == "options" then
		M.close_options()
	end
end

return M
