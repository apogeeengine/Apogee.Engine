--!strict
-- Small helpers for the sample's panel; the Luau tests of Samples/Mods cover them.
local util = {}

export type Crate = { id: string, name: string?, weight: number, size: string, Tag: { string }? }

-- "12.5 kg", with the decimals it needs.
function util.format_weight(kilograms: number): string
	if kilograms == math.floor(kilograms) then
		return string.format("%d kg", kilograms)
	end
	return string.format("%.1f kg", kilograms)
end

-- "Wooden crate: 20 kg, large, fragile stackable reinforced"
function util.describe(crate: Crate): string
	local tags = if crate.Tag then table.concat(crate.Tag, " ") else ""
	local text = string.format("%s: %s, %s", crate.name or crate.id, util.format_weight(crate.weight), crate.size)
	if tags ~= "" then
		text ..= ", " .. tags
	end
	return text
end

function util.total_weight(crates: { Crate }): number
	local total = 0
	for _, crate in crates do
		total += crate.weight
	end
	return total
end

return util
