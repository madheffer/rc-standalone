"""ff_oracle.py <job folder> <out folder> <map>: Valve's far field routine alone, with the atlas saved after every stage.

Copies the job's scene_info.kv3 / scene_meshes.kv3 (and the other inputs vrad3's GPU init reads) to
<out folder>, writes script-ff.vrad3: gpu_init, lightmap_type ahd, sceneinfo_gpu, then each
compute_farfield_<n> routine of the job's script-gpu.vrad3 with a farfield_write_atlas after
compute_lighting (_raw), median_filter (_median), dilate (_dilated), filter_fireflies (_fireflies)
and the routine's own final write, then exit 0; and runs vrad3 there. The stage atlases are what
FarfieldAtlasTests compares our port with. GPU: run only with CS2 closed, through tools/re/heavy.py.
"""
import os
import shutil
import subprocess
import sys

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
VRAD3 = os.path.join(CS2, "game", "bin", "win64", "vrad3.exe")
STAGES = {"farfield_compute_lighting": "_raw", "farfield_median_filter": "_median", "farfield_dilate": "_dilated",
          "farfield_filter_fireflies": "_fireflies"}


def main(job, out, vmap):
    os.makedirs(os.path.join(out, "ff"), exist_ok=True)
    for f in ("scene_info.kv3", "scene_meshes.kv3", "lightmap_packing_geometry.dat", "lightmap_info.dat"):
        shutil.copyfile(os.path.join(job, f), os.path.join(out, f))
    lines = open(os.path.join(job, "script-gpu.vrad3"), encoding="utf-8").read().split("\n")
    script = ["gpu_init", "lightmap_type ahd", "sceneinfo_gpu scene_info.kv3", ""]
    routines, body, name = [], [], None
    for line in lines:
        s = line.strip()
        if s.startswith("compute_farfield_") and s.endswith(":"):
            name, body = s[:-1], []
            continue
        if name is None:
            continue
        if s == "done":
            routines.append((name, body))
            name = None
            continue
        if not s or s.startswith("//"):
            continue
        body.append(s)
    if not routines:
        raise SystemExit("no compute_farfield routine in script-gpu.vrad3")
    script += [f"run {n}" for n, _ in routines] + ["exit 0", ""]
    for n, b in routines:
        script.append(f"{n}:")
        target = next(x.split()[1] for x in b if x.startswith("farfield_write_atlas "))
        for x in b:
            script.append("    " + x)
            suffix = STAGES.get(x.split()[0])
            if suffix:
                script.append(f"    farfield_write_atlas {target} -suffix {suffix}")
        script += ["    done", ""]
    open(os.path.join(out, "script-ff.vrad3"), "w", encoding="utf-8", newline="\n").write("\n".join(script))
    args = [VRAD3, "-map", f"maps/{vmap}.vmap", "-script", "script-ff.vrad3", "-vulkan", "-gpuraytracing", "-allthreads", "-unbufferedio"]
    print(" ".join(args), flush=True)
    sys.exit(subprocess.run(args, cwd=out).returncode)


if __name__ == "__main__":
    main(*sys.argv[1:4])
