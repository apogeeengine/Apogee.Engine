--!strict
-- The task library, on the runner's clock: every frame of a case is 1/60 s of game time and of real time.
local test = require("@engine/test")

test.describe("task", function()
	test.it("spawn runs the function at once, until its first wait", function()
		local log: { string } = {}
		local thread = task.spawn(function(a: string, b: string)
			table.insert(log, a)
			task.wait()
			table.insert(log, b)
		end, "first", "second")
		test.expect(type(thread)).toBe("thread")
		test.expect(log).toEqual({ "first" })
		test.frames(1)
		test.expect(log).toEqual({ "first", "second" })
	end)

	test.it("wait returns the game time that passed", function()
		local elapsed = 0
		task.spawn(function()
			elapsed = task.wait(0.5)
		end)
		test.frames(29)
		test.expect(elapsed).toBe(0)
		test.frames(1)
		test.expect(elapsed).toBeNear(0.5, 1e-6)
	end)

	test.it("wait(0) lets one frame pass", function()
		local count = 0
		task.spawn(function()
			while true do
				count += 1
				task.wait(0)
			end
		end)
		test.expect(count).toBe(1)
		test.frames(3)
		test.expect(count).toBe(4)
	end)

	test.it("defer runs after the current step, delay after its time", function()
		local log: { string } = {}
		task.defer(function()
			table.insert(log, "deferred")
		end)
		task.delay(0.1, function(word: string)
			table.insert(log, word)
		end, "delayed")
		test.expect(#log).toBe(0)
		test.frames(1)
		test.expect(log).toEqual({ "deferred" })
		test.frames(5)
		test.expect(log).toEqual({ "deferred", "delayed" })
	end)

	test.it("every calls the function once per period and cancel stops it", function()
		local count = 0
		local ticker = task.every(0.25, function(step: number)
			count += step
		end, 2)
		test.frames(14)
		test.expect(count).toBe(0)
		test.frames(1)
		test.expect(count).toBe(2)
		test.frames(30)
		test.expect(count).toBe(6)
		task.cancel(ticker)
		test.frames(60)
		test.expect(count).toBe(6)
	end)

	test.it("cancel stops a waiting task and ignores one that is over", function()
		local ran = false
		local waiting = task.spawn(function()
			task.wait(0.1)
			ran = true
		end)
		task.cancel(waiting)
		task.cancel(waiting)
		task.cancel(task.spawn(function() end))
		test.frames(20)
		test.expect(ran).toBe(false)
	end)

	test.it("time counts game seconds, one frame at a time", function()
		local before = task.time()
		local real_before = task.real_time()
		test.frames(60)
		test.expect(task.time() - before).toBeNear(1, 1e-6)
		test.expect(task.real_time() - real_before).toBeNear(1, 1e-6)
	end)

	test.it("wait_real waits on the real clock", function()
		local done = false
		task.spawn(function()
			task.wait_real(0.25)
			done = true
		end)
		test.frames(14)
		test.expect(done).toBe(false)
		test.frames(1)
		test.expect(done).toBe(true)
	end)

	test.it("an error in a task is logged and does not reach the caller", function()
		task.spawn(function()
			error("on purpose")
		end)
		test.frames(1)
	end)

	test.it("wait outside a task is an error", function()
		-- The case itself is a plain coroutine, not a task.
		test.expect(function()
			task.wait(1)
		end).toThrow("must run in a task")
		test.expect(function()
			coroutine.wrap(function()
				task.wait(1)
			end)()
		end).toThrow("must run in a task")
	end)

	test.it("a task can adopt a suspended coroutine", function()
		local log: { string } = {}
		local co = coroutine.create(function()
			table.insert(log, "start")
			task.wait()
			table.insert(log, "end")
		end)
		task.spawn(co)
		test.frames(1)
		test.expect(log).toEqual({ "start", "end" })
		test.expect(function()
			task.spawn(co)
		end).toThrow("not suspended")
	end)
end)
