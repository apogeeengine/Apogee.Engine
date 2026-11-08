# Writing docs

The documentation is Markdown in `Docs/`, built with [MkDocs](https://www.mkdocs.org/) and the Material theme, and published at [apogeeengine.github.io/docs](https://apogeeengine.github.io/docs/). It lives next to the code so that a pull request changes both.

```text
Docs/
  about/            introduction, FAQ, license
  getting-started/  building, samples
  manual/           one folder per area: core, native, platform, graphics, physics, audio, network, scripting, content, ui, tools
  api/              the generated reference (not committed, see below), with a committed index page each
  contributing/     for people who work on the engine
```

## Building the site

```sh
pip install mkdocs-material
./build.sh docs          # the API reference, then the site into Bin/site (mkdocs --strict: a broken link fails the build)
./build.sh docs serve    # the same, then a local server that reloads when a page changes
```

`./build.sh docs` first writes the API reference, which is why you do not run `mkdocs` directly:

- **C#:** [DocFX](https://dotnet.github.io/docfx/) (a pinned local dotnet tool, `.config/dotnet-tools.json`) reads the XML documentation comments of the libraries listed in `Tools/docfx/docfx.json` and writes one page per type into `Docs/api/csharp`.
  The raw bindings (`Apogee.Native.*`) are left out. Document public members with `///` comments: that is the C# reference.
- **Luau:** `Apogee.Runtime --luau-docs Docs/api/luau` writes one page per class and library from the registry. The text comes from the same `///` comments of `[ApogeeClass]`, `[Property]`, `[Method]` and `[Signal]` members, so documenting
  them documents the script API too.

Both folders are git-ignored apart from their `index.md`.

## Style

- A page explains how a feature fits together and why; the API reference lists the members. Show the smallest example that runs.
- Say what is missing and what is untested. A page that does not match the code is a bug.
- Relative links between pages (`../core/ecs.md`), so the strict build checks them.
- No screenshots of samples as references: samples are run by hand.
