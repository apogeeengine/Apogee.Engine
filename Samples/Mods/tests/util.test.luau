local test = require("@engine/test")
local util = require("../game/packages/base_game/lib/util")

test.describe("util", function()
	test.it("writes a weight with the decimals it needs", function()
		test.expect(util.format_weight(20)).toBe("20 kg")
		test.expect(util.format_weight(12.5)).toBe("12.5 kg")
	end)

	test.it("describes a crate with its tags", function()
		test.expect(util.describe({ id = "w", name = "Wooden crate", weight = 20, size = "large", Tag = { "fragile", "reinforced" } })).toBe("Wooden crate: 20 kg, large, fragile reinforced")
		test.expect(util.describe({ id = "s", weight = 80, size = "large" })).toBe("s: 80 kg, large")
	end)

	test.it("adds up weights", function()
		test.expect(util.total_weight({ { id = "a", weight = 1.5, size = "small" }, { id = "b", weight = 2, size = "small" } })).toBe(3.5)
		test.expect(util.total_weight({})).toBe(0)
	end)
end)
