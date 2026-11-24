--!strict
-- @engine/test: the engine's test library. Test files (*.test.luau) describe their cases; --luau-test runs every
-- file under a folder and reports the results.
--
--   local test = require("@engine/test")
--   test.describe("fields", function()
--       test.it("counts cells", function()
--           test.expect(count()).toBe(4)
--       end)
--   end)
--
-- A file's describe() bodies run as it loads, collecting its cases; the cases then run in order, each in a
-- coroutine, so one can wait for frames of a world of its own:
--
--   test.it("moves", function()
--       local world = test.world()           -- the case's own World, freed after it
--       local Position = world:component("Position", { x = "f32" })
--       local e = world:create_entity()
--       world:set(e, Position, { x = 0 })
--       world:system("Move", { Position }, function(entity, position) position.x += 1 end)
--       test.frames(3)                       -- three frames of that world
--       test.expect(world:get(e, Position).x).toBe(3)
--   end)

local test = {}

type Failure = { name: string, message: string }
type Results = { passed: number, failed: number, skipped: number, failures: { Failure } }
type Case = { name: string, body: () -> () }
type Running = { case: Case, co: thread, frames: number, world: World? }

local results: Results = { passed = 0, failed = 0, skipped = 0, failures = {} }
local path: { string } = {}
local cases: { Case } = {}
local next_case = 1
local running: Running? = nil
local filter: string? = nil

local function describeValue(value: any, depth: number?): string
	if type(value) == "string" then
		return string.format("%q", value)
	end
	if type(value) ~= "table" then
		return tostring(value)
	end
	-- A table by its content: arrays in order, other keys sorted, nested ones a few levels deep, long ones cut.
	local level = depth or 0
	if level >= 3 then
		return "{…}"
	end
	local parts = {}
	local count = #value
	for i = 1, count do
		table.insert(parts, describeValue(value[i], level + 1))
	end
	local keys = {}
	for key in value do
		if not (type(key) == "number" and key >= 1 and key <= count and key == math.floor(key)) then
			table.insert(keys, key)
		end
	end
	table.sort(keys, function(a, b)
		return tostring(a) < tostring(b)
	end)
	for _, key in keys do
		local name = if type(key) == "string" and string.match(key, "^[%a_][%w_]*$") then key else `[{describeValue(key, 3)}]`
		table.insert(parts, `{name} = {describeValue(value[key], level + 1)}`)
	end
	local text = "{ " .. table.concat(parts, ", ") .. " }"
	if #parts == 0 then
		text = "{}"
	end
	if #text > 300 then
		text = string.sub(text, 1, 297) .. "…"
	end
	return text
end

local function deepEqual(a: any, b: any): boolean
	if a == b then
		return true
	end
	if type(a) ~= "table" or type(b) ~= "table" then
		return false
	end
	for key, value in a do
		if not deepEqual(value, b[key]) then
			return false
		end
	end
	for key in b do
		if a[key] == nil then
			return false
		end
	end
	return true
end

local function fullName(name: string): string
	if #path == 0 then
		return name
	end
	return table.concat(path, " > ") .. " > " .. name
end

local function fail(name: string, message: string)
	results.failed += 1
	table.insert(results.failures, { name = name, message = message })
end

function test.describe(name: string, body: () -> ())
	table.insert(path, name)
	local ok: boolean, err: any = pcall(body :: any)
	table.remove(path)
	if not ok then
		fail(fullName(name), "error outside a test: " .. tostring(err))
	end
end

-- A case, run after the file has loaded (or skipped when --luau-filter's text isn't in its full name).
function test.it(name: string, body: () -> ())
	local full = fullName(name)
	if filter and not string.find(full, filter, 1, true) then
		results.skipped += 1
		return
	end
	table.insert(cases, { name = full, body = body })
end

-- Waits for n frames in a case: the case's world, if it made one, runs them.
function test.frames(n: number)
	if not running or coroutine.running() ~= running.co then
		error("test.frames waits inside a test case", 2)
	end
	coroutine.yield(n)
end

-- The running case's own World, made on first use and dropped (and so destroyed) when the case ends.
function test.world(): World
	local case = running
	if not case then
		error("test.world is for a test case", 2)
	end
	if not case.world then
		case.world = World.new()
	end
	return case.world :: World
end

local function finish(case: Running, err: string?)
	if err then
		fail(case.case.name, err)
	else
		results.passed += 1
	end
	case.world = nil
	running = nil
end

-- Resumes the running case; it either waits again, ends, or fails.
local function resume(case: Running)
	local ok, frames = coroutine.resume(case.co)
	if not ok then
		finish(case, tostring(frames))
	elseif coroutine.status(case.co) == "dead" then
		finish(case)
	else
		case.frames = frames or 0
	end
end

-- For the runner, once per frame: runs a frame of the waiting case's world, then the case when its wait is over, then the
-- cases after it. Returns true once every case has run.
function test.step(): boolean
	local case = running
	if case then
		if case.world then
			case.world:progress(1 / 60)
		end
		case.frames -= 1
		if case.frames > 0 then
			return false
		end
		resume(case)
	end
	while not running and next_case <= #cases do
		local new: Running = { case = cases[next_case], co = coroutine.create(cases[next_case].body), frames = 0, world = nil }
		next_case += 1
		running = new
		resume(new)
	end
	return running == nil
end

type Expectation = {
	toBe: (expected: any) -> (),
	toEqual: (expected: any) -> (),
	toBeNear: (expected: number, epsilon: number?) -> (),
	toBeTruthy: () -> (),
	toBeFalsy: () -> (),
	toBeNil: () -> (),
	toThrow: (pattern: string?) -> (),
	toContainText: (text: string) -> (),
	never: Expectation,
}

local function makeExpectation(actual: any, negated: boolean): Expectation
	local function check(passed: boolean, message: string)
		if passed == negated then
			error((if negated then "expected not: " else "expected: ") .. message, 3)
		end
	end

	local expectation = {}
	function expectation.toBe(expected: any)
		check(actual == expected, string.format("%s to be %s", describeValue(actual), describeValue(expected)))
	end
	function expectation.toEqual(expected: any)
		check(deepEqual(actual, expected), string.format("%s to equal %s", describeValue(actual), describeValue(expected)))
	end
	function expectation.toBeNear(expected: number, epsilon: number?)
		local tolerance = epsilon or 1e-5
		check(
			type(actual) == "number" and math.abs(actual - expected) <= tolerance,
			string.format("%s to be within %s of %s", describeValue(actual), tostring(tolerance), tostring(expected))
		)
	end
	function expectation.toBeTruthy()
		check(not not actual, describeValue(actual) .. " to be truthy")
	end
	function expectation.toBeFalsy()
		check(not actual, describeValue(actual) .. " to be falsy")
	end
	function expectation.toBeNil()
		check(actual == nil, describeValue(actual) .. " to be nil")
	end
	function expectation.toContainText(text: string)
		check(
			type(actual) == "string" and string.find(actual, text, 1, true) ~= nil,
			string.format("%s to contain %q", describeValue(actual), text)
		)
	end
	function expectation.toThrow(pattern: string?)
		local ok, err = pcall(actual)
		local matches = not ok and (pattern == nil or string.find(tostring(err), pattern, 1, true) ~= nil)
		check(matches, if ok then "the function to throw" else string.format("error %q to contain %q", tostring(err), pattern or ""))
	end
	return expectation :: any
end

-- Expectations on a value (a call's first result: test.expect(fs.read_text(path)) ignores the error after it).
function test.expect(actual: any, ...: any): Expectation
	local expectation = makeExpectation(actual, false)
	expectation.never = makeExpectation(actual, true)
	return expectation
end

function test.results(): Results
	return results
end

-- For the runner: the counts, and the names and messages of the failures as two lists.
function test.summary(): (number, number, number, { string }, { string })
	local names, messages = {}, {}
	for _, failure in results.failures do
		table.insert(names, failure.name)
		table.insert(messages, failure.message)
	end
	return results.passed, results.failed, results.skipped, names, messages
end

-- Forgets the cases and results; with a filter, only the cases whose full name contains it will run.
function test.reset(only: string?)
	results = { passed = 0, failed = 0, skipped = 0, failures = {} }
	table.clear(path)
	table.clear(cases)
	next_case = 1
	running = nil
	filter = only
end

return table.freeze(test)
