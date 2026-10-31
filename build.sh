#!/usr/bin/env bash
# Thin wrapper over dotnet and Tools/Apogee.Build.
#   ./build.sh            build the solution
#   ./build.sh test       build and run the tests
#   ./build.sh native     build the shared libraries in Native/ into Bin/native/<rid>
#   ./build.sh aot        publish Apogee.Runtime with NativeAOT, then compile its shaders ahead of time (Shaders/imported)
#   ./build.sh aot-viewer publish the Viewer sample (Samples/Viewer) the same way: a sample, run by hand, never by CI
#   ./build.sh bench [filter]  run the BenchmarkDotNet measurements in Samples/Benchmarks (Release; e.g. `bench '*Culling*'`): a sample, run by hand
#   ./build.sh docs [serve]   build the API reference (DocFX for C#, the runtime for Luau), then Docs/ with mkdocs --strict into Bin/site
#   ./build.sh pack <folder> <out.pack> [...]   pack a folder with Apogee.Runtime --pack (this wrapper loads .env, so APOGEE_PACK_KEY applies)
#   ./build.sh sdk steam <zip>   unpack the Steamworks SDK headers the bindings generator needs (see ThirdParty/README.md)
#   ./build.sh <command>  anything else goes to Tools/Apogee.Build (bindings, ...)
set -euo pipefail
cd "$(dirname "$0")"

[ -f .env ] && set -a && . ./.env && set +a

case "${1:-build}" in
  build) dotnet build Apogee.slnx -c Debug ;;
  test)  dotnet test --solution Apogee.slnx -c Debug ;;
  native) cmake -S Native -B Bin/native-build -G Ninja && cmake --build Bin/native-build ;;
  aot)   dotnet publish Source/Apogee.Runtime -c Release -p:PublishAot=true -o Bin/publish/runtime
         # A shipped program loads compiled shaders and never needs the Slang compiler: cook them with the runtime we just built.
         Bin/publish/runtime/Apogee.Runtime --import Bin/publish/runtime/Shaders ;;
  aot-viewer) dotnet publish Samples/Viewer -c Release -p:PublishAot=true -o Bin/publish/viewer
         # Cooked by the published runtime (build it first with ./build.sh aot).
         Bin/publish/runtime/Apogee.Runtime --import Bin/publish/viewer/Shaders ;;
  bench) shift; filter="*"; if [ $# -gt 0 ]; then filter="$1"; shift; fi
         dotnet run --project Samples/Benchmarks -c Release -- --filter "$filter" --artifacts Bin/benchmarks "$@" ;;
  pack)  shift; dotnet run --project Source/Apogee.Runtime -c Release -- --pack "$@" ;;
  *)     dotnet run --project Tools/Apogee.Build -- "$@" ;;
esac
