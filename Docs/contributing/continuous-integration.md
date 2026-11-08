# Continuous integration

GitHub Actions builds, tests and publishes every push to `main` and every pull request. The layout follows the habit of large open-source engines: `runner.yml` is the entry point and calls one workflow per concern, and each of those can also be started by hand from the Actions tab.

| Workflow | What it does |
| --- | --- |
| `runner.yml` | the entry point: static checks first, then the three platforms and the docs in parallel |
| `static_checks.yml` | the DCO sign-off of every commit of a pull request, no CRLF or trailing whitespace, Git LFS pointers (`git lfs fsck --pointers`), and [actionlint](https://github.com/rhysd/actionlint) on the workflows |
| `linux_builds.yml` | **Tests:** Release build and every test project, GPU tests on lavapipe (Mesa's software Vulkan) with the validation layer, the bindings generator's tests with libclang. **NativeAOT:** `./build.sh aot`, the Luau tests and a fixture game from the published binary, rendered offscreen, shipped as a pack and without the shader compiler |
| `windows_builds.yml` | **Tests:** Release build and every test project. **NativeAOT:** the publish, the Luau tests and the fixture game, with no GPU |
| `macos_builds.yml` | Apple silicon and Intel: Release build, the tests (the Metal GPU tests skip unless the runner's GPU qualifies), the NativeAOT publish and the fixture game, uploaded as artifacts. The Intel job is experimental while a rare crash is investigated (see [Help wanted](help-wanted.md)) |
| `docs.yml` | `./build.sh docs` (the API reference, then `mkdocs build --strict`); on a push to `main`, publishes the site to the [`apogeeengine/docs`](https://github.com/apogeeengine/docs) repository's GitHub Pages |
| `thirdparty_*.yml` | manual workflows that build the vendored binaries we cannot take from upstream (GameNetworkingSockets, OpenAL Soft, SDL for Linux). Their recipes and the checksums are in `ThirdParty/README.md` |

`.github/actions/setup` is the part the build workflows share: the Git LFS objects of the platform (cached by object id, so a push does not spend the repository's LFS bandwidth), the .NET SDK and the native libraries (Flecs, Luau, and so on).

## Rules the workflows hold to

- **Tests are not samples.** CI runs `Tests/` and the fixture game in `Tests/Fixtures`; nothing runs `Samples/`, no test depends on one, and there are no reference screenshots of samples.
- **Required natives fail, they do not skip.** `APOGEE_REQUIRE_NATIVE` lists the native libraries that must load on each platform; `APOGEE_REQUIRE_VULKAN`, `APOGEE_REQUIRE_CLANG` and `APOGEE_REQUIRE_METAL_COMPILER` do the same for the GPU, libclang and Apple's shader compiler.
- **Run what CI runs.** `./build.sh test` is `dotnet test`, and `./build.sh aot` is the publish step: the same commands a developer uses.

## Reproducing a failure

Linux needs `mesa-vulkan-drivers` and `vulkan-validationlayers` for the GPU tests (lavapipe is the software device). Windows and macOS runners have no usable Vulkan or Metal device: a GPU test that skips there ran on Linux.
