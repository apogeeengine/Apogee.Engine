# Contributing to Apogee Engine

Thank you for wanting to help. Apogee is maintained by one person for now, so please be patient: reviews take time, and a pull request that does not fit may be closed even if it is good work.
Read this page before you open a pull request. It is short on purpose.

## Where to start

- **Questions, ideas, "does it run on my machine?":** [Discussions](https://github.com/apogeeengine/Apogee.Engine/discussions).
- **Bugs:** an issue with a *minimal reproduction* (steps, a script or, best, a failing test). Say your OS, GPU and driver.
- **Something to work on:** the [help-wanted list](Docs/contributing/help-wanted.md) says what is missing and how to check your work. Open an issue or a discussion before a big change, so nobody writes a feature that cannot be merged.
- **Security problems:** privately, see [SECURITY.md](SECURITY.md).

## Scope

Apogee is an engine for games that crunch numbers: big worlds, many entities, simulation. It is not trying to be an engine for everyone, and the most important decision for each feature is whether it belongs.
**Pull requests that drift from this purpose will be rejected**, however well made. Out of scope:

- consoles, mobile and the web (the engine targets Windows, Linux and macOS);
- C# modding (mods are Luau and data) and other scripting languages;
- feature toggles (everything is always compiled; options are build modes and dev tools only);
- abstraction layers, wrappers or tooling that nothing in the repository needs;
- libraries we may not redistribute (proprietary SDKs), and anything that makes a clone need a download to build.

In scope, and welcome: bug fixes, tests, documentation, performance work with a benchmark, rendering, physics, editor and tooling features that fit the existing design, samples, and platform work (see the help-wanted list).

## How the code is written

The conventions are in [CLAUDE.md](CLAUDE.md) (the project's style guide). The ones that cause most review comments:

- **AOT-safe:** no runtime reflection, no `Reflection.Emit`, no loading managed code at run time in `Source/`. Use the source generators (`[ApogeeClass]`, `[Property]`, `[Method]`, `[Signal]`). Shipped games are NativeAOT.
- **Warnings are errors**, trim and AOT analyzers included. Allocation-free hot paths; `unsafe` and unmanaged memory where they count, and only there.
- **Bindings are generated** by our own generator (`Tools/Apogee.Build/Bindings`); never edit `Generated/` by hand, change the config and regenerate.
- **Every feature has tests** in `Tests/` and a page in `Docs/`. Tests are not samples: nothing in `Tests/` depends on `Samples/`.
- **Never copy code** from a source whose license does not allow it (proprietary, non-free or copyleft code we would have to relicense). Say where an algorithm comes from.
- New third-party libraries need their license, the build recipe in `ThirdParty/README.md`, and an entry in `Legal/THIRD_PARTY_NOTICES.md`.

Build and test with `./build.sh` and `./build.sh test` (see [Building](Docs/getting-started/building.md)).

## Commits and sign-off

- **Conventional Commits** (`feat(rendering): …`, `fix(luau): …`, `docs: …`). The changelog is built from them. Keep a pull request to one purpose.
- **Sign off every commit** with `git commit -s`. That adds a `Signed-off-by:` line, which means you agree to the [Developer Certificate of Origin](https://developercertificate.org/): you wrote the change, or have the right to submit it, under the project's license (Apache-2.0). CI checks it.

## AI-assisted contributions

Using an assistant is fine. Submitting code you do not understand is not. If an assistant wrote part of your change:

- you must have read it, built it and run the tests, and be able to explain every line in review;
- say so in the pull request, and add the assistant's attribution trailer to the commits;
- do not open pull requests in bulk or on speculation. Large, unrequested, machine-generated changes will be closed without review.

## Review

Expect questions rather than just approvals. A pull request is merged when CI is green on Linux, Windows and macOS, the change is in scope, and it has tests and docs. Be kind in reviews: we follow the
[Code of Conduct](CODE_OF_CONDUCT.md).

## License

By contributing you agree that your contribution is licensed under the [Apache License 2.0](LICENSE), like the rest of the engine.
