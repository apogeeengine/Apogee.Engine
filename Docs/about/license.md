# License

Apogee's own code, tools and documentation are under the **Apache License, Version 2.0**: you may use, modify and distribute them, in open or closed source, for free games and commercial ones, with no royalties. The license text is
[`LICENSE`](https://github.com/apogeeengine/Apogee.Engine/blob/main/LICENSE) in the repository, and [`NOTICE`](https://github.com/apogeeengine/Apogee.Engine/blob/main/NOTICE) is the notice to keep with it when you redistribute the engine.
Contributions are accepted under the same license (see [Contributing](../contributing/index.md)).

## Third-party libraries

The engine links, loads or vendors third-party libraries, each under its own license (MIT, BSD, Zlib, Apache-2.0, the FreeType License, LGPL for OpenAL Soft). `ThirdParty/README.md` says where each came from, at what version and how its binaries were built.
[`Legal/THIRD_PARTY_NOTICES.md`](https://github.com/apogeeengine/Apogee.Engine/blob/main/Legal/THIRD_PARTY_NOTICES.md) lists what a game that ships a library has to include with it (most ask for their copyright notice in the credits or documentation).

Two things worth knowing:

- **OpenAL Soft is LGPL**, so it is its own shared library beside the program, loaded at run time and replaceable, never linked statically.
- **Steamworks is Valve's.** Only its `redistributable_bin` libraries are in the repository, as the SDK Access Agreement allows. The C# bindings generated from its headers carry Valve's terms, not the Apache License, and using Steamworks needs a partner account and an agreement with Valve.
