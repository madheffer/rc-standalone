"""Find the assets a map source names that nothing can resolve - BEFORE compiling it.

resourcecompiler validates materials LATE. On ze_ffvii_mako_reactor_v6_p it spent
22 minutes on lights, voxelisation and visibility and only then stopped at the
first mesh whose material was missing, so every stub round pays that 22 minutes
again. This reads the .vmap directly and answers the same question in seconds.

    python check_references.py <addon> <map>            # report
    python check_references.py <addon> <map> --stub     # and write placeholders

A reference counts as resolvable if it exists as a SOURCE under the addon's
content, as a compiled resource under the addon's game directory, or as a
compiled resource in the game's own pak01. That is the same set resourcecompiler
sees, which is why the answer matches its verdict.
"""
import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "map-survey"))
import dmx                                              # noqa: E402  (path set above)
from vpk import Vpk                                     # noqa: E402

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")

STUB = """// Placeholder written by check_references.py so the map compiles.
Layer0
{
\tshader "csgo_simple.vfx"
\tF_TRANSLUCENT 0
\t$color "[1.000000 1.000000 1.000000 0.000000]"
}
"""


def references(vmap):
    """Every material and model path the map source names, as lowercase paths."""
    document = dmx.read_file(vmap)
    found = set()
    for element in document["elements"]:
        for value in element.attrs.values():
            for item in (value if isinstance(value, list) else [value]):
                if isinstance(item, str) and item.lower().endswith((".vmat", ".vmdl")):
                    found.add(item.lower().replace("\\", "/"))
    return found


def game_pak_entries():
    """
    Compiled resource paths in every archive the game mounts, lowercase and
    without the _c.

    There is more than one: the tool materials a map is full of (toolsnodraw,
    toolstrigger) live in game/core, not game/csgo, and reading only the latter
    reports them as missing on every map ever made.
    """
    found = set()
    for mod in ("csgo", "core", "csgo_core", "csgo_imported", "csgo_lv"):
        pak = os.path.join(CS2, "game", mod, "pak01_dir.vpk")
        if not os.path.exists(pak):
            continue
        archive = Vpk(pak)
        found.update(p[:-2].lower() for p in archive.entries if p.endswith("_c"))
        archive.close()
    return found


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("addon")
    ap.add_argument("map")
    ap.add_argument("--stub", action="store_true", help="write placeholder materials for what is missing")
    args = ap.parse_args()

    content = os.path.join(CS2, "content", "csgo_addons", args.addon)
    compiled = os.path.join(CS2, "game", "csgo_addons", args.addon)
    vmap = os.path.join(content, "maps", args.map + ".vmap")
    if not os.path.exists(vmap):
        sys.exit(f"no map source at {vmap}")

    wanted = references(vmap)
    stock = game_pak_entries()

    missing = []
    for path in sorted(wanted):
        native = path.replace("/", os.sep)
        if (os.path.exists(os.path.join(content, native))                 # a source we can compile
                or os.path.exists(os.path.join(compiled, native + "_c"))  # already compiled here
                or path in stock):                                        # the game ships it
            continue
        missing.append(path)

    materials = [p for p in missing if p.endswith(".vmat")]
    models = [p for p in missing if p.endswith(".vmdl")]
    print(f"{len(wanted)} referenced, {len(missing)} unresolvable "
          f"({len(materials)} materials, {len(models)} models)")
    for path in missing[:15]:
        print("   " + path)
    if len(missing) > 15:
        print(f"   ... and {len(missing) - 15} more")

    if not args.stub:
        if materials:
            print("\nmaterials are FATAL to resourcecompiler; re-run with --stub, then compile")
            print(f"  resourcecompiler -nop4 -f -game <game/csgo> -i \"{content}\\materials\\*.vmat\" -r")
        return

    written = 0
    for path in materials:
        target = os.path.join(content, path.replace("/", os.sep))
        if os.path.exists(target):
            continue
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with open(target, "w", encoding="utf-8", newline="\n") as f:
            f.write(STUB)
        written += 1
    print(f"\nwrote {written} stub material(s); compile them before the map")
    if models:
        print(f"{len(models)} model(s) stay missing - not fatal, but their geometry "
              "is absent from the ray trace scene, so lighting and timings are a lower bound")


if __name__ == "__main__":
    main()
