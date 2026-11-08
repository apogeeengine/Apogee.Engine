--!strict
-- A mod's script: it may use its dependencies' modules (@base_game/...), read the game, and write only user://moddata/bigger_crates/.
local util = require("@base_game/lib/util")

local crates = defs.all("Crate") :: { util.Crate }
local ok = fs.write_text("user://moddata/bigger_crates/last_run.txt", `{#crates} crates, {util.format_weight(util.total_weight(crates))}`)
print(`bigger_crates: loaded, wrote its data: {tostring(ok)}`)
