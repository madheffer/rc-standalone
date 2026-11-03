"""skytint_specimen.py: two probe01 variants for the scene info's ibl (181113f20, SceneLights.Ibl):

  s2c_skytint       env_sky tint_color "200 128 64", brightnessscale "1.5"
  s2c_skylightonly  the same, plus sky_lightingonly_name (the same sky material) and lighting_only_tint_color
                    "100 220 160", so the lighting-only tint and material are taken

Each goes through Valve's dmxconvert to keyvalues2 and back (tools/coverage/probe_map.py). Then compile them with
Valve's tools (tools/pipeline/compile_fresh.py s2c_rc_probe <map> --full --cs2-open) and with ours
(tools/pipeline/full_compile_test.py ... --valve <that vpk>); the lightmaps follow the sky's scale.
"""
import os
import sys
import tempfile

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "coverage"))
import probe_map as pm  # noqa: E402

CONTENT = os.path.join(os.path.dirname(pm.GAME), "content", "csgo_addons", "s2c_rc_probe", "maps")
SKY = '\t\t"skyname" "string" "materials/editor/toolscene_lighting_sky_dust.vmat"\n'
TINT = '\t\t"tint_color" "string" "255 255 255"\n\t\t"brightnessscale" "string" "1.0"\n'
VARIANTS = {
    "s2c_skytint": '\t\t"tint_color" "string" "200 128 64"\n\t\t"brightnessscale" "string" "1.5"\n',
    "s2c_skylightonly": '\t\t"tint_color" "string" "200 128 64"\n\t\t"brightnessscale" "string" "1.5"\n'
                        '\t\t"sky_lightingonly_name" "string" "materials/editor/toolscene_lighting_sky_dust.vmat"\n'
                        '\t\t"lighting_only_tint_color" "string" "100 220 160"\n',
}


def main():
    base = os.path.join(CONTENT, "probe01.vmap")
    with tempfile.TemporaryDirectory() as tmp:
        text = os.path.join(tmp, "base.vmap")
        pm.convert(base, text, "keyvalues2")
        with open(text, encoding="utf-8") as f:
            kv2 = f.read()
        block = SKY + TINT
        if kv2.count(block) != 1:
            raise SystemExit("probe01's env_sky keys are not as expected")
        for name, keys in VARIANTS.items():
            out = os.path.join(CONTENT, name + ".vmap")
            if os.path.exists(out):
                raise SystemExit(f"{out} exists; not overwriting")
            edited = os.path.join(tmp, name + ".vmap")
            with open(edited, "w", encoding="utf-8") as f:
                f.write(kv2.replace(block, SKY + keys))
            pm.convert(edited, out, "binary")
            print("wrote", out)


if __name__ == "__main__":
    main()
