"""coloradjust_specimen.py [<base.vmap>] [<out.vmap>]: s2c_coloradjust, probe01 with stock materials whose colour
adjustment is not the default, for ledger 197's open check (TextureAverageColor and the HSV path of
MatrixColorTint2 / MatrixColorCorrect2 against the game's $Globals and cube pixels).

  - materials/dev/dev_measuregeneric01.vmat -> materials/cs_italy/ground/cobblestone_ground_large_1.vmat
    (csgo_environment: g_fTextureColorBrightness1 2, Contrast1 1.193, Saturation1 0.366)
  - materials/dev/reflectivity_30.vmat -> materials/cs_italy/cobblestone/italy_cobblestone_01.vmat
    (csgo_environment: g_fTextureColorContrast1 1.497)

Both are in the stock pak01 (ColorMatrixTests.NonDefaultColorAdjust lists them). The map goes through Valve's
dmxconvert to keyvalues2 and back, as tools/coverage/probe_map.py does, so the result is a binary vmap in the
base's format. Then, CS2 closed: compile it full with Valve's tools (tools/pipeline/compile_fresh.py
s2c_rc_probe s2c_coloradjust --full) and capture a cube (tools/cubehost/run_probe.py --renderdoc 1, Steam closed);
docs/CS2_CLOSED_QUEUE.md has the order.
"""
import os
import sys
import tempfile

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "coverage"))
import probe_map as pm  # noqa: E402

CONTENT = os.path.join(os.path.dirname(pm.GAME), "content", "csgo_addons", "s2c_rc_probe", "maps")
SWAPS = {
    "materials/dev/dev_measuregeneric01.vmat": "materials/cs_italy/ground/cobblestone_ground_large_1.vmat",
    "materials/dev/reflectivity_30.vmat": "materials/cs_italy/cobblestone/italy_cobblestone_01.vmat",
}


def main(base, out):
    with tempfile.TemporaryDirectory() as tmp:
        text = os.path.join(tmp, "base.vmap")
        pm.convert(base, text, "keyvalues2")
        with open(text, encoding="utf-8") as f:
            kv2 = f.read()
        for old, new in SWAPS.items():
            count = kv2.count('"' + old + '"')
            if count == 0:
                raise SystemExit(f"{old} not in {base}")
            kv2 = kv2.replace('"' + old + '"', '"' + new + '"')
            print(f"{old} -> {new}: {count}")
        swapped = os.path.join(tmp, "swapped.vmap")
        with open(swapped, "w", encoding="utf-8") as f:
            f.write(kv2)
        pm.convert(swapped, out, "binary")
    print(f"wrote {out}")


if __name__ == "__main__":
    args = sys.argv[1:]
    main(args[0] if args else os.path.join(CONTENT, "probe01.vmap"),
         args[1] if len(args) > 1 else os.path.join(CONTENT, "s2c_coloradjust.vmap"))
