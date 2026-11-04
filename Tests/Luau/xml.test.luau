local test = require("@engine/test")

test.describe("xml", function()
	test.it("decodes an element with its attributes, children and lines", function()
		local node = xml.decode('<Crate id="a" weight="2">\n  <Tag>x</Tag>\n  <Tag>y</Tag>\n</Crate>')

		test.expect(node.name).toBe("Crate")
		test.expect(node.attrs.weight).toBe("2")
		test.expect(node.order).toEqual({ "id", "weight" })
		test.expect(#node.children).toBe(2)
		test.expect(node.children[2].text).toBe("y")
		test.expect(node.children[2].line).toBe(3)
	end)

	test.it("encodes what it decoded", function()
		local text = '<Crate id="a">\n  <Tag>x &lt; y</Tag>\n  <Empty/>\n</Crate>\n'

		test.expect(xml.encode(xml.decode(text))).toBe(text)
	end)

	test.it("raises an error that names the line for broken xml", function()
		local ok, err = pcall(xml.decode, "<a>\n<b>\n</a>")

		test.expect(ok).toBe(false)
		test.expect(err).toContainText("xml:3:")
	end)
end)

test.describe("defs", function()
	test.it("finds nothing when the game loaded no definitions", function()
		test.expect(defs.get("Crate", "a")).toBeNil()
		test.expect(#defs.all("Crate")).toBe(0)
		test.expect(#defs.types()).toBe(0)
	end)
end)
