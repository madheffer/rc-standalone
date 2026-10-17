#!/usr/bin/env bash
# Builds prebuilt/linux-x64/libbc7e_s2c_amd.so and libbc7e_s2c_intel.so.
# Needs ispc 1.24 (ISPC=path, else ispc on PATH), g++ and python3.
set -euo pipefail
S="$(cd "$(dirname "$0")" && pwd)"
ISPC="${ISPC:-ispc}"
OBJ="$S/obj"; OUT="$S/prebuilt/linux-x64"
mkdir -p "$OBJ" "$OUT"
python3 "$S/prepare.py" --out "$OBJ"
for v in amd intel; do
  def=""; [ "$v" = intel ] && def="-DBC7E_RCP_INTEL"
  "$ISPC" -O2 "$OBJ/bc7e_rcp.ispc" -o "$OBJ/bc7e_$v.o" -h "$OBJ/bc7e_ispc.h" \
    --target=avx2-i32x4 --opt=fast-math --opt=disable-assertions --pic $def
  g++ -O2 -shared -fPIC -fvisibility=hidden -I "$OBJ" "$S/bc7e_s2c.cpp" "$OBJ/bc7e_$v.o" -o "$OUT/libbc7e_s2c_$v.so"
done
ls -la "$OUT"
