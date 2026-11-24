#!/usr/bin/env bash
# Thin wrapper over dotnet and Tools/Weft.Build.
#   ./build.sh            build the solution
#   ./build.sh test       build and run the tests
#   ./build.sh native     build the shared libraries in Native/ into Bin/native/<rid>
#   ./build.sh aot        publish Weft.Runtime with NativeAOT, then compile its shaders ahead of time (Shaders/imported)
#   ./build.sh aot-viewer publish the Viewer sample (Samples/Viewer) the same way: a sample, run by hand, never by CI
#   ./build.sh bench [filter]  run the BenchmarkDotNet measurements in Samples/Benchmarks (Release; e.g. `bench '*Culling*'`): a sample, run by hand
#   ./build.sh docs [serve]   build the API reference (DocFX for C#, the runtime for Luau), then Docs/ with mkdocs --strict into Bin/site
#   ./build.sh pack <folder> <out.pack> [...]   pack a folder with Weft.Runtime --pack (this wrapper loads .env, so WEFT_PACK_KEY applies)
#   ./build.sh export <game folder> <out folder> --target <rid> [--cooked] [...]   export a game with Weft.Runtime --export (.env is loaded, so WEFT_PACK_KEY applies; needs ./build.sh aot first)
#   ./build.sh sdk steam <zip>   unpack the Steamworks SDK headers the bindings generator needs (see ThirdParty/README.md)
#   ./build.sh <command>  anything else goes to Tools/Weft.Build (bindings, ...)
set -euo pipefail
cd "$(dirname "$0")"

[ -f .env ] && set -a && . ./.env && set +a

case "${1:-build}" in
  build) dotnet build Weft.slnx -c Debug ;;
  test)  dotnet test --solution Weft.slnx -c Debug ;;
  native) cmake -S Native -B Bin/native-build -G Ninja && cmake --build Bin/native-build ;;
  aot)   dotnet publish Source/Weft.Runtime -c Release -p:PublishAot=true -o Bin/publish/runtime
         # A shipped program loads compiled shaders and never needs the Slang compiler: cook them with the runtime we just built.
         Bin/publish/runtime/Weft.Runtime --import Bin/publish/runtime/Shaders ;;
  aot-viewer) dotnet publish Samples/Viewer -c Release -p:PublishAot=true -o Bin/publish/viewer
         # Cooked by the published runtime (build it first with ./build.sh aot).
         Bin/publish/runtime/Weft.Runtime --import Bin/publish/viewer/Shaders ;;
  bench) shift; filter="*"; if [ $# -gt 0 ]; then filter="$1"; shift; fi
         dotnet run --project Samples/Benchmarks -c Release -- --filter "$filter" --artifacts Bin/benchmarks "$@" ;;
  pack)  shift; dotnet run --project Source/Weft.Runtime -c Release -- --pack "$@" ;;
  export) shift; dotnet run --project Source/Weft.Runtime -c Release -- --export "$@" ;;
  *)     dotnet run --project Tools/Weft.Build -- "$@" ;;
esac
