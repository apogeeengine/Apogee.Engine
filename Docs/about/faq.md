# FAQ

**Is Apogee ready to make a game with?** Not yet in the sense of a stable engine: it is pre-alpha and APIs change. It is complete enough to build on if you read the code, and every library has tests and a Manual page.

**Why not Unity, Unreal or Godot?** Use them if they fit: for many games they are the right choice. Apogee is for games where the engine's assumptions get in the way of numbers: very large worlds, heavy simulation, custom data layouts.
There you want to own the stack and have a foundation instead of a framework.

**Why C#?** Modern C# (unsafe code, spans, function pointers, SIMD, NativeAOT) is fast enough for the hot paths and much faster to work in than C++. Shipped games are NativeAOT, so there is no JIT and no runtime reflection.

**Why Luau and not C# for mods?** A mod must be sandboxed, limited in time and memory, and reloadable while the game runs. Luau was built for that. Mods are Luau and data, never native or managed code.

**Which platforms?** Windows, Linux and macOS, on x64 and Arm64 where the libraries allow. Vulkan 1.3 is the graphics API on Windows and Linux; macOS uses Metal 3, which is written but untested ([help wanted](../contributing/help-wanted.md)).
There is no console, mobile or web target.

**Is there a Direct3D 12 backend?** No, and it is not planned: Vulkan covers Windows. The [graphics page](../manual/graphics/graphics.md#other-backends) says what it would take.

**Do I have to use Steam?** No. `Apogee.Steam` is a separate library, and a game that does not start it never touches Steam.

**Can I use FMOD or Wwise?** Not out of the box: the repository only ships libraries it may redistribute, so audio is OpenAL Soft. If your game needs one of them, replace `Apogee.Audio` in your project.

**Do I have to credit Apogee in my game?** The Apache License asks you to keep the license and `NOTICE` with copies of the engine you redistribute. The third-party libraries ask for their notices in your credits or documentation:
[Third-party notices](https://github.com/apogeeengine/Apogee.Engine/blob/main/Legal/THIRD_PARTY_NOTICES.md) lists what each one needs. We do not ask for royalties or a logo.

**Is the engine written with AI?** Parts of it are written with an AI assistant, under the maintainer's review and with tests. [CONTRIBUTING.md](https://github.com/apogeeengine/Apogee.Engine/blob/main/CONTRIBUTING.md) says what we expect when you do the same.
