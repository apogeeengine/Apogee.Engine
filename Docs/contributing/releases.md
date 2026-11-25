# Releases

A release is a commit that bumps the version, and a git tag on it. The version is written in one file, `Engine.props` ([Versions](../manual/core/versions.md)); the tag is that version with a `v` in front.

## Making a release

1. **Choose the version.** Weft follows [semantic versioning](https://semver.org); before 1.0 a minor version may break anything. The status is `alpha`, `beta`, `rc<N>` or `stable`, and it is part of the
   version until the release is `stable`. If the release breaks the mods written for the level before, raise `WeftApiVersion` too.
2. **Bump `Engine.props`** in a commit of its own, with nothing else in it:

   ```sh
   git commit -s -am "chore(release): 0.2.0-alpha"
   ```

   `./build.sh version` prints the version the file now says.
3. **Check it.** `./build.sh version check --tag v0.2.0-alpha` is what CI runs on the tag: `Engine.props` parses, the builds read it, nothing else sets a version, and the tag is `v` and the version.
4. **Tag that commit and push the tag:**

   ```sh
   git tag -s v0.2.0-alpha -m "Weft 0.2.0-alpha"
   git push origin v0.2.0-alpha
   ```

   The push of a tag runs the whole CI (`runner.yml`) with `WEFT_BUILD_NUMBER` set to the run number, so the assemblies of the release are `0.2.0.<run>`. The static checks fail the run when the tag is not
   `v` and the version of `Engine.props`: delete the tag, fix the commit and tag again.
5. **Write the notes.** `git cliff --tag v0.2.0-alpha` writes the changelog from the Conventional Commits since the last tag (`cliff.toml`); `git cliff --latest` is the notes of the newest release alone.

## The version after a release

The commit after a release leaves `Engine.props` as it is: the version is that of the last release until the next bump. A development build is told apart from the release by its commit
(`0.2.0-alpha+abc1234`) and by `EngineVersion.Build`, which is 0 outside CI.

## What the check refuses

`./build.sh version check [--tag <name>]` (`Tools/Weft.Build/VersionCommand.cs`) fails when:

- `Engine.props` is missing or is not valid: a part that is not a number from 0 to 65535, a status other than `alpha`, `beta`, `rc<N>` and `stable`, a codename with a quote in it;
- `Directory.Build.props` or `Native/CMakeLists.txt` no longer reads `Engine.props`;
- a project, a props or targets file other than the root `Directory.Build.props` sets `<Version>`, `<VersionPrefix>`, `<AssemblyVersion>`, `<FileVersion>` or `<InformationalVersion>`, or code has an
  `[assembly: AssemblyVersion(...)]` (`Bin/`, `obj/` and `ThirdParty/` are not looked at);
- with `--tag`, the tag is not `v` and the version text (`refs/tags/` in front is fine).
