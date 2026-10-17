"""capture_host.py <addon> <block> <out dir>: Valve's ray tracing host buffers for one lightmap block.

Runs vrad3.exe on the addon's _vrad3 job (gpu_init, the scene, the lightmap image, one block's
load and compute, lightmap_image_save, moved to <out>/image) under Frida, hooking scenesystem's structured
buffer upload (180099c00 in the 2026-10-15 build: holder, render context, data, stride, count).
Every upload is saved as <out>/upload_<n>_stride<stride>_count<count>.bin, in call order:
stride 0x20 the PackedRtxInstanceData_t table, 0x150 GeometryInfo_t, 0x1f0 PathTraceBarnLightGPU_t,
0xc0 SceneGlobalInfoGPU_t. CpuBakeTests.Bake with CPUBAKE_HOSTCAP=<out dir> compares them with ours.

Uses the GPU: run it only when CS2 is closed and the PC is free.
"""
import os
import sys
import time

import frida

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
VRAD3 = os.path.join(CS2, "game", "bin", "win64", "vrad3.exe")
UPLOAD_RVA = 0x99c00  # scenesystem 180099c00 (md5 3b785e13dd52e34f56b9b544956eb7b1)

AGENT = """
const RVA = %d;
function hook(m) {
    const at = m.base.add(RVA);
    Interceptor.attach(at, {
        onEnter(args) {
            const stride = args[3].toUInt32();
            const count = args[4].toInt32();
            const size = stride * count;
            const bytes = size > 0 ? args[2].readByteArray(size) : null;
            send({ kind: 'upload', stride, count }, bytes);
        }
    });
    send({ kind: 'hooked', base: m.base.toString() });
}
const found = Process.findModuleByName('scenesystem.dll');
if (found) hook(found);
else Process.attachModuleObserver({ onAdded(m) { if (m.name.toLowerCase() === 'scenesystem.dll') hook(m); } });
""" % UPLOAD_RVA


def main():
    if len(sys.argv) != 4:
        print(__doc__)
        sys.exit(2)
    addon, block, out = sys.argv[1], int(sys.argv[2]), os.path.abspath(sys.argv[3])
    os.makedirs(out, exist_ok=True)
    job = os.path.join(CS2, "game", "csgo_addons", addon, "_vrad3")
    script = open(os.path.join(job, "script-gpu.vrad3"), encoding="utf-8").read()
    vmap = next(line.split()[1] for line in script.splitlines() if line.startswith("// maps/"))
    lines = ["gpu_init", "lightmap_type ahd", "sceneinfo_gpu scene_info.kv3", "lightmap_image_gpu 4096 4096 1024",
             "lightmap_load_packing_geometry_gpu lightmap_packing_geometry.dat",
             f"lightmap_load_block_gpu lightmap_info.dat {block}", "lightmap_compute_block_gpu 16 16 0.200",
             "lightmap_image_save capture_host_image", "exit 0", ""]
    name = "script-capture-host.vrad3"
    open(os.path.join(job, name), "w", encoding="utf-8").write("\n".join(lines))
    args = [VRAD3, "-map", vmap, "-script", name, "-vulkan", "-gpuraytracing", "-allthreads", "-unbufferedio"]
    print(" ".join(args), flush=True)
    pid = frida.spawn(args, cwd=job)
    session = frida.attach(pid)
    n = [0]

    def on_message(message, data):
        if message["type"] != "send":
            print(message, flush=True)
            return
        p = message["payload"]
        if p["kind"] == "hooked":
            print("hooked scenesystem at", p["base"], flush=True)
        elif p["kind"] == "upload":
            path = os.path.join(out, "upload_%02d_stride%x_count%d.bin" % (n[0], p["stride"], p["count"]))
            with open(path, "wb") as f:
                f.write(data or b"")
            print("upload", n[0], "stride", hex(p["stride"]), "count", p["count"], flush=True)
            n[0] += 1

    agent = session.create_script(AGENT)
    agent.on("message", on_message)
    agent.load()
    done = [False]
    session.on("detached", lambda reason, crash: done.__setitem__(0, True))
    frida.resume(pid)
    while not done[0]:
        time.sleep(0.5)
    # vrad3 splits its arguments at spaces: the image goes to the job folder, then moves here.
    image = os.path.join(out, "image")
    os.makedirs(image, exist_ok=True)
    saved = os.path.join(job, "capture_host_image")
    for f in os.listdir(saved):
        os.replace(os.path.join(saved, f), os.path.join(image, f))
    os.rmdir(saved)
    print("vrad3 exited;", n[0], "uploads and the image in", out, flush=True)


if __name__ == "__main__":
    main()
