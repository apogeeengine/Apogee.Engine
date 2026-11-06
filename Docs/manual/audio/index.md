# Audio

`Apogee.Audio` is the game's sound: **OpenAL Soft** behind one `AudioSystem`, playing WAV, FLAC, MP3 and Ogg Vorbis files in 3D around the entity that has an `AudioListener`, on buses, with reverb zones and occlusion.
In a game, scripts see it as the global `audio`.

> OpenAL Soft is LGPL, so it is its own shared library beside the program (`libopenal.so.1`, `OpenAL32.dll`, `libopenal.1.dylib` in `ThirdParty/openal-soft/lib`), loaded at run time and replaceable: see `Legal/THIRD_PARTY_NOTICES.md`
> for what a game that ships it does. Without the library `audio.is_active` is false: every call does nothing and returns an inert object, and the game runs in silence (the log says so once).
> The files are decoded by dr_wav, dr_flac, dr_mp3 and stb_vorbis in our `audio_decode` library ([Native libraries](../native/native-libraries.md)).

```lua
local horn = audio:load_sound("res://sounds/horn.ogg")   -- a file from the game or a mod
local voice = horn:play("vehicles", 0.8, 1.0, false, truckEntity)   -- bus, volume, pitch, loop, entity
audio:get_bus("music").volume = 0.5

local music = audio:load_sound("res://music/field.ogg", true, false, true)   -- stream, is_3d, loop
music:play("music")
```

**Another audio engine.** There is no backend layer: the engine has one audio library. Middleware such as FMOD or Wwise is built around authored events, which a "play a file" API does not cover, so a shared seam
would only get in the way; a game that needs one replaces `Apogee.Audio`.

## The system

`new AudioSystem(world, options, files)` (a session makes it for you): the device and context start with it; `audio.is_active` says whether they did (no library, a library that cannot start: both give an inactive
system and a line in the log). `Update(dt)` is called once a frame **after** the world has progressed (`ScriptSession.Progress` does), because it reads world transforms.

`AudioOptions`: the output, the sample rate (48 kHz), and the Doppler and rolloff scales and the distance factor.
- `AudioOutput.Default` is the platform's device, in real time. With no device at all the system mixes into nowhere and says so.
- `AudioOutput.None` is OpenAL Soft's **loopback** device: nothing is played, but `Update(dt)` mixes the `dt` seconds it is given and drops the samples, so sounds start, loop and end exactly as they would be heard.
  Headless runs, script tests and the editor (when not playing) use it, and it needs no sound card.
- `AudioOutput.WavFile` is the same, written to `WavPath` as a 32-bit float WAV when the system is disposed: what the tests record and measure.

`audio.volume` is the master volume (the listener's gain, which is also the volume of the `master` bus).

**Files.** Sounds are read through the game's virtual file system: `res://`, `mod://<id>/`, packs and zips, so a mod brings its sounds and a pack can hold them. **Only virtual paths are read**: a path with no
`scheme://` is an error, so a script cannot ask for a file by its place on the disk. (A mod can still read any `res://`, `mod://` or `user://` file: the audio classes do not know which mod called them, like the UI and model classes.) The format is told from the file's first bytes, not its name. A file that is missing or is not audio we read is an error with the reason.
OpenAL's current context is process-wide: **one audio system per process**.

**Loading.** `load_sound(path, stream, is_3d, loop)` decodes the whole file into one OpenAL buffer, unless `stream` is true: then the file stays in memory and each voice decodes it as it plays, into four buffers of a
quarter of a second (music, long ambience). Files with more than two channels are folded down to stereo.

## 3D, the listener and far from the origin

An entity with the `AudioListener` component (`Index`, 0 by default) is the ear: it looks along its local -Z with +Y up, as a camera does. Up to eight listeners (split screen). **OpenAL's own listener stays at the origin.**
Each voice is placed relative to the **nearest** `AudioListener`, in that listener's frame, taken in double precision and then narrowed, so a source 10 000 km from the origin is as exact as one near it. The voice's velocity
relative to that listener drives the Doppler effect (clamped to 300 m/s: a teleport must not shift the pitch to nonsense).

A voice is positional when its sound is 3D and it has an entity (the last argument of `play`): each frame it is placed at that entity's world transform. A sound loaded with `is_3d = false` is not. Sound fades with distance by
OpenAL's inverse-distance rolloff, clamped: no quieter-than-full inside `min_distance` (1 m), no quieter than it is at `max_distance` (10 000 m). Set them on a sound (`sound.min_distance`, `sound.max_distance`: every voice
it makes starts with them) or on one voice.

## Voices

`AudioVoice` is what is playing: `sound:play(bus, volume, pitch, loop, entity)`. `volume`, `pitch`, `paused`, `is_playing`, `position` (seconds), `min_distance`, `max_distance`, `occlusion`, `muffling`, `stop()`.
**Letting go:** a sound that does not loop plays to its end whether you keep the voice or not; a looping sound is stopped when the voice is destroyed, which is when a script drops it, so keep the voice of whatever loops.
Destroying a sound cuts every voice that is playing it.

## Buses

`audio:get_bus(name)` returns the bus of that name (the same object each time): `master` or an empty name is the whole mix, and any other name is a bus of our own, made on first use, that sounds are played on
(`sound:play("vehicles")`). `volume`, `muted`, `set_paused(bool)` and `stop_all()`. A voice's gain is its volume times its bus's times the master's. The old project's four buses (Music, Vehicles, Environment, UI)
are the game's to make; the engine defines none.

## Reverb zones

The `ReverbZone` component is a box centered on its entity (`HalfExtents`, scaled by the entity's scale) with a `Preset`, a `Priority` and a `Level` (dB added to the wet level). Where the first listener is in several, the
highest priority wins, then the smallest. Only that zone applies; there is no crossfade (one EFX reverb effect carries it, and the change is heard at once). `audio.current_reverb` is the preset the listener hears now.
The presets (`generic`, `room`, `hallway`, `garage`, `barn`, `hangar`, `cave`, `forest`, `plain`; `off` outside every zone) are our own approximations of those spaces, in `ReverbPresets` as OpenAL Soft's EAX reverb
parameters, to be tuned by ear. Every voice sends to the one effect slot. Without the EFX extension (OpenAL Soft has it) there is no reverb and no occlusion filter.

## Occlusion

A voice with `occlusion = true` and an entity, in an audio system with its `physics` set (`audio.physics = physics`; a session does), casts a ray from the nearest listener to the voice each frame and, when something blocks
the way, puts a low-pass and an attenuation on its direct sound and its reverb send (OpenAL's filters), moving smoothly toward the target (`8 · dt` a frame). `voice.muffling` is how much (0 to 1). The ray ignores sensors
and passes the voice's own body; it is one ray, so a source behind a thin pole is not muffled and behind a thick wall is.

## Not done

HRTF settings (OpenAL Soft's own defaults apply), the other effects (echo, chorus…) from scripts (the C bindings have all of EFX), geometry-based occlusion, crossfading between zones, more than one reverb at a time,
recording, `.pck`-style sound banks, and audio in the editor's own viewport (the editor plays sound in play mode only).

## Tests

`Tests/Apogee.Audio.Tests` mixes into a WAV file (`AudioOutput.WavFile`) and measures it, with no sound card, so **they run in CI**: a sound from the virtual file system is heard and ends, every format plays, only virtual
paths are read, a stream plays to its end and a streamed loop goes on, a source on the left is louder in the left speaker and the sound follows when it moves, a far source is quieter and the distances of a voice
change that, a bus scales what plays on it by exactly its gain (4:1 for 0.25), the master bus mutes the mix, mute and pause silence a bus, pitch and stop, destroying a sound cuts its voices, a reverb zone leaves a tail
that no zone does not, zones are picked by priority then size, and a wall between the listener and the source muffles it (with the physics world's rays). `Tests/Apogee.Native.Tests` renders a source on the loopback
device and tests the decoders. `Tests/Luau/audio.test.luau` runs the script surface, passing with or without the library, in the NativeAOT binary too.
