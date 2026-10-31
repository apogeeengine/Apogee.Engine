# Third-party notices

Apogee's own code, tools and documentation are under the Apache License 2.0 (`LICENSE`, `NOTICE`).

Third-party libraries keep their own licenses, listed in `ThirdParty/README.md` as they are vendored. The
Steamworks SDK (Valve) is proprietary and not redistributable beyond what its agreement allows: of it only the
`redistributable_bin` libraries are in this repository (see below).

Libraries must allow closed-source distribution; LGPL libraries are linked dynamically and ship as their own,
replaceable libraries with their license text. A game shipped with the engine includes the notices of the third-party
libraries it uses in its credits or documentation (Luau's team also asks for attribution in user-facing documentation).

## FreeType

FreeType is used under the FreeType License (`ThirdParty/freetype/docs/FTL.TXT`), which asks that a product that includes it says so in its documentation, such as its
credits, with this text (the year is that of the FreeType version, 2.15.0):

> Portions of this software are copyright © 2026 The FreeType Project (https://freetype.org).  All rights reserved.

## OpenAL Soft

The audio system plays through OpenAL Soft (`ThirdParty/openal-soft/COPYING`: GNU Library General Public License, version 2, "or any later version"; its modified PFFFT carries a BSD-style license, `ThirdParty/openal-soft/LICENSE-pffft`).
It is LGPL, so it is used as its own shared library (`libopenal.so.1`, `OpenAL32.dll`, `libopenal.1.dylib`), loaded at run time and never linked statically into the engine or a game. A game that ships it:

- ships the library as a separate file beside the program, so that its user can replace it with another build of OpenAL Soft (or an OpenAL of their own);
- includes OpenAL Soft's license text (`COPYING`) and says where its source is: https://github.com/kcat/openal-soft at the tag named in `ThirdParty/README.md` (we do not modify it);
- does not forbid reverse engineering for debugging such a replacement.

## stb_vorbis and dr_libs

The audio decoders are public domain / MIT (stb_vorbis: Sean Barrett, `ThirdParty/stb/LICENSE.txt`) and public domain / MIT-0 (dr_wav, dr_flac and dr_mp3: David Reid, `ThirdParty/dr_libs/LICENSE.txt`). They need no notice, but the
MIT alternative of stb asks that its copyright line travels with copies of the source.

## GameNetworkingSockets

The network system uses Valve's GameNetworkingSockets (`ThirdParty/gns/LICENSE`: BSD-3-Clause, "Copyright (c) 2018, Valve Corporation"). Its shared library has Google's protobuf (BSD-3-Clause, "Copyright 2008 Google Inc.")
and Abseil (Apache-2.0) linked in, and OpenSSL's libcrypto (Apache-2.0) on Linux and macOS (Windows uses the system's BCrypt). A game that ships it includes these notices in its credits or documentation: the BSD-3-Clause text
requires the copyright notice, the conditions and the disclaimer to be reproduced in the documentation that comes with a binary, and the Apache-2.0 libraries' licenses travel with them.

## Steamworks

`ThirdParty/steamworks/lib` holds the SDK's `redistributable_bin` libraries (`steam_api64.dll`, `libsteam_api.so`, `libsteam_api.dylib`), which the Steamworks SDK Access
Agreement (section 1.1) lets a game distribute in binary form. Nothing else of the SDK is in the repository. Using Steamworks needs a Steamworks partner account and
agreement with Valve; the library does nothing for a game Steam does not know.

`Source/Apogee.Native.Steam/Generated` is generated from the SDK's headers, and Valve's terms apply to those files, not the Apache License 2.0 that covers the rest of the repository.

## Dear ImGui, ImGuizmo, ImPlot and imnodes

The dev tools and the editor use Dear ImGui (`ThirdParty/cimgui/imgui/LICENSE.txt`: "Copyright (c) 2014-2026 Omar Cornut"), its C API cimgui (`ThirdParty/cimgui/LICENSE.txt`: "Copyright (c) 2015 Stephan Dilly"), ImGuizmo
(`ThirdParty/imguizmo/LICENSE`: "Copyright (c) 2016 Cedric Guillemet"), ImPlot (`ThirdParty/implot/LICENSE`: "Copyright (c) 2020 Evan Pezent") and imnodes (`ThirdParty/imnodes/LICENSE.md`: "Copyright (c) 2019 Johann
Muszynski"), all MIT-licensed and compiled into the one `cimgui` library. A game that ships it includes the notices in its credits or documentation (a shipped game normally has no dev tools, but the library is
part of the runtime).

## Jolt Physics

Jolt Physics is MIT-licensed (`ThirdParty/jolt/LICENSE`: "Copyright 2021 Jorrit Rouwe"). A game that ships the `jolt` library includes the notice in its credits or documentation.

## RmlUi

RmlUi is MIT-licensed (`ThirdParty/rmlui/LICENSE.txt`: "Copyright (c) 2008-2014 CodePoint Ltd, Shift Technology Ltd, and contributors; Copyright (c) 2019-2026 The RmlUi Team, and contributors"). A game that ships the `rmlui` library
includes the notice. RmlUi carries two third-party parts, whose licenses travel with it: `robin_hood` and `itlib` (MIT; `ThirdParty/rmlui/Include/RmlUi/Core/Containers/LICENSE.txt`), and the fonts of its debugger,
Courier Prime Code and Courier Prime Code Italic (Copyright (c) 2013 Quote-Unquote Apps, with the Reserved Font Name Courier Prime, under the SIL Open Font License 1.1; `ThirdParty/rmlui/Source/Debugger/LICENSE.txt`).
The debugger is part of the library in every build, so a game includes the font notice with it.

## Inter

The engine's default UI font, Inter (`Source/Apogee.UI/Fonts`: Regular, Bold and Italic, embedded in `Apogee.UI.dll`), is Copyright (c) 2016 The Inter Project Authors (https://github.com/rsms/inter) and licensed under the SIL Open Font
License, Version 1.1 (`Source/Apogee.UI/Fonts/Inter-LICENSE.txt`). The license allows bundling it in a game, closed source ones included, as long as the copyright notice and the license travel with the font; a game that
ships `Apogee.UI` includes them in its credits or documentation, with the notices of the Courier Prime fonts of RmlUi's debugger (see RmlUi above).

## Sample assets

`Samples/Viewer/Assets` holds six models from [KhronosGroup/glTF-Sample-Assets](https://github.com/KhronosGroup/glTF-Sample-Assets), all
CC0-1.0 (WaterBottle, Lantern, BoomBox, NormalTangentTest, ToyCar, SheenChair). Each folder keeps its upstream `LICENSE.md` with the
authors; `Samples/Viewer/Assets/README.md` lists the commit and checksums. They are samples and are not part of the engine.
