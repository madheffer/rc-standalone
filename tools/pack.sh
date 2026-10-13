#!/usr/bin/env bash
# Build the Source2.Compiler NuGet package (and its symbols) into artifacts/packages.
# Tests run first against the lowest ValveKeyValue the package supports, which is also the
# dependency's lower bound (vpkstuff's VRF pins 0.70.0.499; 0.71 gives the same output).
#   tools/pack.sh             dev build: version suffix dev.<yyyymmdd>.<short sha>
#   tools/pack.sh --release   the plain version
#   tools/pack.sh --skip-tests
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/artifacts/packages"
kv="/p:Source2CompilerValveKeyValueVersion=${VALVEKEYVALUE_VERSION:-0.70.0.499}"
release=0; tests=1
for a in "$@"; do
  case "$a" in
    --release) release=1 ;;
    --skip-tests) tests=0 ;;
    *) echo "unknown option $a" >&2; exit 2 ;;
  esac
done
if [ "$tests" = 1 ]; then
  dotnet test "$root/tests/Source2.Compiler.Tests" -c Release "$kv" --nologo
fi
suffix=()
if [ "$release" = 0 ]; then
  suffix=("/p:VersionSuffix=dev.$(date +%Y%m%d).$(git -C "$root" rev-parse --short HEAD)")
fi
mkdir -p "$out"
dotnet pack "$root/src/Source2.Compiler" -c Release -o "$out" "$kv" "${suffix[@]}" --nologo
ls -t "$out"/Source2.Compiler.* | head -2
