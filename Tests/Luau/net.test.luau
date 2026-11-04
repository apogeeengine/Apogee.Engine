--!strict
-- The network as scripts see it. The library may be missing on a machine (it is a prebuilt binary), so these hold either way: with it, two systems of the script talk over the loopback interface;
-- without it, the calls do nothing and return nothing.
local test = require("@engine/test")

-- Only in hosts that have the network library (the runtime does; the scripting tests' host does not).
if NetSystem == nil then
	return
end

test.describe("net", function()
	test.it("is inactive when made inactive, and every call does nothing", function()
		local off = NetSystem.create_inactive()
		test.expect(off.is_active).toBe(false)
		test.expect(off:listen(0)).toBe(0)
		test.expect(off:connect("127.0.0.1:27020")).toBe(nil)
		off:update()
	end)

	test.it("is active exactly when the library is installed, and says so", function()
		local net = NetSystem.create()
		test.expect(net.is_active).toBe(NetSystem.is_installed())
		net:update()
	end)

	test.it("sends a message over the loopback interface when it can", function()
		local server = NetSystem.create()
		local client = NetSystem.create()
		if not server.is_active then
			test.expect(server:listen(0)).toBe(0)
			return
		end

		local port = server:listen(0)
		test.expect(port > 0).toBe(true)
		test.expect(server.listening_port).toBe(port)
		local received: { string } = {}
		local greeted = false
		server.connected:connect(function(connection)
			greeted = true
		end)
		server.message:connect(function(connection, text, lane)
			table.insert(received, text)
			connection:send("echo " .. text)
		end)
		local echoes: { string } = {}
		client.message:connect(function(connection, text, lane)
			table.insert(echoes, text)
		end)
		local connection = client:connect("127.0.0.1:" .. tostring(port))
		test.expect(connection ~= nil).toBe(true)

		local function pump(done: () -> boolean)
			local start = os.clock()
			while not done() and os.clock() - start < 3 do
				server:update()
				client:update()
			end
		end

		pump(function()
			return greeted and connection ~= nil and connection.state == "connected"
		end)
		test.expect(greeted).toBe(true)
		test.expect(connection.state).toBe("connected")
		connection:send("hello")
		pump(function()
			return #echoes > 0
		end)
		test.expect(received[1]).toBe("hello")
		test.expect(echoes[1]).toBe("echo hello")
		connection:close("done")
		test.expect(connection.state).toBe("closed")
	end)
end)
