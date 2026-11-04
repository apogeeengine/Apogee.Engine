--!strict
-- The audio system as scripts see it. OpenAL Soft ships beside the program but may be missing, so these hold either way: with it the calls play (into nowhere here), without it they do nothing
-- and return objects that are inert.
local test = require("@engine/test")

-- Only in hosts that have the audio library (the runtime does; the scripting tests' host does not).
if AudioSystem == nil then
	return
end

test.describe("audio", function()
	test.it("is active exactly when OpenAL Soft is installed, and says so", function()
		local world = test.world()
		local audio = AudioSystem.create(world)
		test.expect(audio.is_active).toBe(AudioSystem.is_installed())
		audio.volume = 0.5
		test.expect(audio.volume).toBe(0.5)
		audio:update(0.016)
	end)

	test.it("gives buses a volume of their own, and the same bus for the same name", function()
		local world = test.world()
		local audio = AudioSystem.create(world)
		local music = audio:get_bus("music")
		music.volume = 0.25
		music.muted = true
		test.expect(music.volume).toBe(0.25)
		test.expect(music.muted).toBe(true)
		test.expect(audio:get_bus("music")).toBe(music)
		test.expect(audio:get_bus("music") ~= audio:get_bus("ui")).toBe(true)
	end)

	test.it("refuses a sound that is not there only when it could play it", function()
		local world = test.world()
		local audio = AudioSystem.create(world)
		local ok, message = pcall(function()
			audio:load_sound("res://no_such_sound.wav")
		end)
		if audio.is_active then
			test.expect(ok).toBe(false)
			test.expect(tostring(message)).toContainText("could not load the sound")
		else
			test.expect(ok).toBe(true)
			local voice = audio:load_sound("res://no_such_sound.wav"):play("music", 1, 1)
			test.expect(voice.is_playing).toBe(false)
		end
	end)

	test.it("has a listener and reverb zones as components", function()
		local world = test.world()
		local audio = AudioSystem.create(world)
		local Transform = world:component("Transform")
		local AudioListener = world:component("AudioListener")
		local ReverbZone = world:component("ReverbZone")
		local ear = world:create_entity("ear")
		world:set(ear, Transform, { Position = Vector3(0, 0, 0), Rotation = Quat.identity, Scale = Vector3(1, 1, 1) })
		world:set(ear, AudioListener, { Index = 0 })
		local garage = world:create_entity("garage")
		world:set(garage, Transform, { Position = Vector3(0, 0, 0), Rotation = Quat.identity, Scale = Vector3(1, 1, 1) })
		world:set(garage, ReverbZone, { HalfExtents = Vector3(10, 5, 10), Preset = 4, Priority = 1, Level = 0 })
		world:progress(0.016)
		audio:update(0.016)
		if audio.is_active then
			test.expect(audio.current_reverb).toBe("garage")
		else
			test.expect(audio.current_reverb).toBe("off")
		end
	end)
end)
