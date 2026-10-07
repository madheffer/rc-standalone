"""post_oracle.py: run Valve's vrad3 on a saved lightmap image, for scoring our post-process port.

    post_oracle.py bake <addon> <map>
        Re-runs the map's _vrad3 script with `lightmap_image_save pre_post` right
        after compute_all_lightmap_blocks and stops there: the saved image every
        post-process command starts from (a GPU bake; minutes on probe01).

    post_oracle.py run <addon> <in image> <out image> <command...>
        A script of: gpu_init, lightmap_type ahd, lightmap_image_gpu, the map's
        packing geometry, lightmap_image_load <in>, the command,
        lightmap_image_save <out>, exit 0. vrad3 runs in the map's _vrad3
        folder. The packing geometry loader builds render buffers, so every
        run needs the GPU.

    post_oracle.py oracle <addon>
        LightmapPostOracleTests' synthetic 256 x 256 atlas (oracle_in and
        oracle_packing_geometry.dat, written by the test's first run): each
        command the test checks, into oracle_out_<name>.

Images are folders lightmap_image_save writes (lightmap_data.dat + channelNN.exr),
under the _vrad3 folder. LightmapPostOracleTests (VRAD3ORACLE=<_vrad3 folder>)
compares our port with each output. Everything here uses the GPU: run it only
when CS2 is closed and the PC is free.
"""
import os
import re
import subprocess
import sys

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
VRAD3 = os.path.join(CS2, "game", "bin", "win64", "vrad3.exe")

# LightmapPostOracleTests.Commands: output name and the vrad3 command.
ORACLE = [
    ("consolidate_2", "lightmap_image_consolidate_small_charts 2"),
    ("fireflies", "lightmap_image_remove_fireflies 4.0"),
    ("median_7", "lightmap_image_filter_median 7"),
    ("jfa", "lightmap_image_dilate_over_invalid_jfa"),
    ("encode_ahd", "lightmap_image_encode_ahd"),
    ("gutters_1", "lightmap_image_fill_gutters 1"),
    ("gutters_2", "lightmap_image_fill_gutters 2"),
    ("weld", "uber_weld oracle_seams.dat"),
]
# Run on vrad3's encode_ahd output, after the list above: the weld on AHD channels.
ORACLE_ON_AHD = [("weld_ahd", "uber_weld oracle_seams.dat")]


def folder(addon):
    return os.path.join(CS2, "game", "csgo_addons", addon, "_vrad3")


def vrad3(addon, script_name, map_name="maps/oracle.vmap"):
    d = folder(addon)
    args = [VRAD3, "-map", map_name, "-script", script_name, "-vulkan", "-gpuraytracing", "-allthreads", "-unbufferedio"]
    print(" ".join(args), flush=True)
    return subprocess.run(args, cwd=d).returncode


def bake(addon, map_name):
    d = folder(addon)
    script = open(os.path.join(d, "script-gpu.vrad3"), encoding="utf-8").read()
    # Stop after the compute: save, then exit before the post-process routine runs.
    script = script.replace("run compute_all_lightmap_blocks\n",
                            "run compute_all_lightmap_blocks\nlightmap_image_save pre_post\nexit 0\n", 1)
    if "lightmap_image_save pre_post" not in script:
        raise SystemExit("could not find 'run compute_all_lightmap_blocks' in the script")
    open(os.path.join(d, "script-oracle-bake.vrad3"), "w", encoding="utf-8").write(script)
    return vrad3(addon, "script-oracle-bake.vrad3", f"maps/{map_name}.vmap")


def run(addon, source, target, command, geometry="lightmap_packing_geometry.dat", size=(4096, 4096, 1024)):
    d = folder(addon)
    lines = ["gpu_init", "lightmap_type ahd", "lightmap_image_gpu %d %d %d" % size,
             f"lightmap_load_packing_geometry_gpu {geometry}", f"lightmap_image_load {source}",
             " ".join(command), f"lightmap_image_save {target}", "exit 0", ""]
    name = "script-oracle-" + re.sub(r"[^a-z0-9_]+", "_", target.lower()) + ".vrad3"
    open(os.path.join(d, name), "w", encoding="utf-8").write("\n".join(lines))
    return vrad3(addon, name)


def oracle(addon):
    if not os.path.exists(os.path.join(folder(addon), "oracle_in", "lightmap_data.dat")):
        raise SystemExit("no oracle_in: run LightmapPostOracleTests once with VRAD3ORACLE set")
    failed = 0
    for name, command in ORACLE:
        code = run(addon, "oracle_in", "oracle_out_" + name, command.split(),
                   "oracle_packing_geometry.dat", (256, 256, 256))
        print(name, "exit", code, flush=True)
        failed += code != 0
    for name, command in ORACLE_ON_AHD:
        code = run(addon, "oracle_out_encode_ahd", "oracle_out_" + name, command.split(),
                   "oracle_packing_geometry.dat", (256, 256, 256))
        print(name, "exit", code, flush=True)
        failed += code != 0
    return 1 if failed else 0


def main():
    if len(sys.argv) >= 4 and sys.argv[1] == "bake":
        sys.exit(bake(sys.argv[2], sys.argv[3]))
    if len(sys.argv) >= 3 and sys.argv[1] == "oracle":
        sys.exit(oracle(sys.argv[2]))
    if len(sys.argv) >= 6 and sys.argv[1] == "run":
        sys.exit(run(sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5:]))
    print(__doc__)
    sys.exit(2)


if __name__ == "__main__":
    main()
