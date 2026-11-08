--!strict
-- The sample game's script: two panels, one for the packages (which loaded, which did not and why) and one for the crates that the definitions of all the packages add up to.
local util = require("./lib/util")

local function packages_panel()
	imgui.set_next_window_pos(12, 12)
	imgui.set_next_window_size(700, 170)
	if imgui.begin_window("Packages", nil, { "no_resize", "no_move", "no_collapse" }) then
		for _, package in packages.list() do
			local failure = if package.error then string.match(package.error, "[^\n]+") else nil -- the first line of the error
			local line = string.format("%s %s (%s)", package.id, package.version, package.kind)
			if not package.loads then
				imgui.text_colored({ 1, 0.55, 0.3, 1 }, `{line}: does not load`)
				imgui.text_wrapped(`    {package.reason}`)
			elseif not package.ran then
				imgui.text_colored({ 1, 0.4, 0.4, 1 }, `{line}: failed`)
				imgui.text_wrapped(`    {failure}`)
			else
				imgui.text_colored({ 0.5, 1, 0.6, 1 }, `{line}: loaded`)
			end
		end
	end
	imgui.end_window()
end

local function crates_panel()
	imgui.set_next_window_pos(12, 196)
	imgui.set_next_window_size(700, 130)
	if imgui.begin_window("Crates", nil, { "no_resize", "no_move", "no_collapse" }) then
		local crates = defs.all("Crate") :: { util.Crate }
		for _, crate in crates do
			imgui.text(util.describe(crate))
		end
		imgui.separator()
		imgui.text(`{#crates} crates, {util.format_weight(util.total_weight(crates))} together`)
	end
	imgui.end_window()
end

devtools.on_gui(function()
	packages_panel()
	crates_panel()
end)

print(`base_game: {#defs.all("Crate")} crates, {#defs.problems()} problems`)
