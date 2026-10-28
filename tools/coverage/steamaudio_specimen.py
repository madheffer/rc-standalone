"""steamaudio_specimen.py <base.vmap> <out.vmap> [key=value ...]: a copy of <base.vmap> whose worldspawn bakes
Steam Audio. The reverb and pathing rebake options go to Auto (2) so resourcecompiler's -sareverb and -sapaths
bake on every compile; any other worldspawn key (steamaudio_reverb_rays=4096, ...) can be set on the command
line. The rest of the map is the base's. Built through Valve's dmxconvert as probe_map.py does."""
import os
import sys
import tempfile

import probe_map as pm

DEFAULTS = {"steamaudio_reverb_rebake_option": "2", "steamaudio_pathing_rebake_option": "2"}


def main(base, out, *pairs):
    keys = dict(DEFAULTS)
    keys.update(p.split("=", 1) for p in pairs)
    work = tempfile.mkdtemp(prefix="steamaudio_")
    text_path = os.path.join(work, "base.vmap")
    pm.convert(base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    root = next(t for t in tops if t.type == "CMapRootElement")
    props = root.get("world").get("entity_properties")
    for k, v in keys.items():
        props.set(k, "string", v)
    out_text = os.path.join(work, "out.vmap")
    open(out_text, "w", encoding="utf-8", newline="\n").write(pm.emit(tops, header))
    pm.convert(out_text, out, "binary")
    print("wrote", out, keys)


if __name__ == "__main__":
    main(*sys.argv[1:])
