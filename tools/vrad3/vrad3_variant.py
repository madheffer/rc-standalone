"""vrad3_variant.py <addon | job folder> <map> <name> [--strip-key key ...]: Valve's lightmap compute on an edited scene.

Copies the _vrad3 folder's scene_info.kv3 to scene_info_<name>.kv3 with every
`key = { ... }` block of the named keys removed, writes script-<name>.vrad3
(the map's script-gpu.vrad3 reading that scene, saving the pre-post-process
image as <name> right after compute_all_lightmap_blocks, then exiting), and
runs vrad3 on it. A ze_hold_em_p compute takes about a minute on the GPU, so an
experiment on what vrad3 reads is cheap; score the saved image against another
with exr_ratio.py. GPU: run only with CS2 closed.
"""
import argparse
import os
import subprocess
import sys

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
VRAD3 = os.path.join(CS2, "game", "bin", "win64", "vrad3.exe")


def strip_blocks(text, keys):
    out, lines, i, removed = [], text.split("\n"), 0, 0
    while i < len(lines):
        line = lines[i]
        name = line.strip().split(" = ")[0].rstrip(" =")
        if name in keys and line.rstrip().endswith("="):
            # `key = ` then a `{` line at the same indent; skip to its matching close.
            depth, i = 0, i + 1
            while i < len(lines):
                depth += lines[i].count("{") + lines[i].count("[") - lines[i].count("}") - lines[i].count("]")
                i += 1
                if depth <= 0:
                    break
            removed += 1
            continue
        out.append(line)
        i += 1
    return "\n".join(out), removed


def swap_materials(text, pairs, keep=()):
    """old=new material swaps on the instances; old '*' swaps every instance's material but those in keep."""
    count = 0
    for pair in pairs:
        old, new = pair.split("=", 1)
        lines = text.split("\n")
        for i, line in enumerate(lines):
            if line.strip().startswith('material = resource_name:"'):
                current = line.split('"')[1]
                if (old == "*" and current not in keep) or current == old:
                    lines[i] = line.replace(f'"{current}"', f'"{new}"')
                    count += 1
        text = "\n".join(lines)
    return text, count


def keep_lights(text, keep):
    """Only the lights array entries whose indices are in keep."""
    lines = text.split("\n")
    start = next(i for i, l in enumerate(lines) if l.strip() == "lights =")
    if lines[start + 1].strip() != "[":
        raise SystemExit("unexpected lights array")
    out, i, depth, index, entry = lines[:start + 2], start + 2, 0, 0, []
    while True:
        line = lines[i]
        if depth == 0 and line.strip() == "]":
            break
        entry.append(line)
        depth += line.count("{") + line.count("[") - line.count("}") - line.count("]")
        if depth == 0:
            if index in keep:
                out.extend(entry)
            entry, index = [], index + 1
        i += 1
    return "\n".join(out + lines[i:]), index


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("addon")
    ap.add_argument("map")
    ap.add_argument("name")
    ap.add_argument("--strip-key", action="append", default=[])
    ap.add_argument("--material", action="append", default=[], help="old=new (old * for every instance)")
    ap.add_argument("--keep-material", action="append", default=[], help="materials a '*' swap leaves alone")
    ap.add_argument("--lights", help="comma-separated indices of the lights to keep")
    a = ap.parse_args()
    # The addon's _vrad3 folder, or a kept job folder (compile_fresh.py's <map>.vpk.vrad3) given as a path.
    folder = a.addon if os.path.isdir(a.addon) else os.path.join(CS2, "game", "csgo_addons", a.addon, "_vrad3")
    scene = open(os.path.join(folder, "scene_info.kv3"), encoding="utf-8", newline="").read()
    scene, removed = strip_blocks(scene, set(a.strip_key))
    print(f"removed {removed} blocks ({', '.join(a.strip_key) or 'none'})", flush=True)
    if a.material:
        scene, swapped = swap_materials(scene, a.material, set(a.keep_material))
        print(f"swapped {swapped} instance materials", flush=True)
    if a.lights is not None:
        keep = {int(x) for x in a.lights.split(",") if x}
        scene, total = keep_lights(scene, keep)
        print(f"kept lights {sorted(keep)} of {total}", flush=True)
    scene_name = f"scene_info_{a.name}.kv3"
    open(os.path.join(folder, scene_name), "w", encoding="utf-8", newline="").write(scene)
    script = open(os.path.join(folder, "script-gpu.vrad3"), encoding="utf-8").read()
    if "sceneinfo_gpu scene_info.kv3" not in script or "run compute_all_lightmap_blocks\n" not in script:
        raise SystemExit("unexpected script-gpu.vrad3")
    script = script.replace("sceneinfo_gpu scene_info.kv3", f"sceneinfo_gpu {scene_name}", 1)
    script = script.replace("run compute_all_lightmap_blocks\n",
                            f"run compute_all_lightmap_blocks\nlightmap_image_save {a.name}\nexit 0\n", 1)
    script_name = f"script-{a.name}.vrad3"
    open(os.path.join(folder, script_name), "w", encoding="utf-8").write(script)
    args = [VRAD3, "-map", f"maps/{a.map}.vmap", "-script", script_name, "-vulkan", "-gpuraytracing", "-allthreads", "-unbufferedio"]
    print(" ".join(args), flush=True)
    sys.exit(subprocess.run(args, cwd=folder).returncode)


if __name__ == "__main__":
    main()
