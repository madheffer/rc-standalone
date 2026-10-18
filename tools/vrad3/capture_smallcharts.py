"""capture_smallcharts.py <addon | job folder> <block> <our dump prefix> <out dir> [generation]: vrad3's small-chart compute inputs.

Runs vrad3.exe on the job (gpu_init, the scene, block <block> at 4 g + 1 samples: generation g is the
last; default 0)
under Frida. At the block's lightmap_generate_small_chart_samples dispatch (vkCmdDispatch with
w / 8 x h / 8 x 1 groups, 128 x 128 for a 1024 block) it finds, in the process's host memory, the
copies vrad3 uploaded from, by patterns taken from our own dump of the same pass
(RtxBakeHost with GPUBAKE_DUMPSMALL=<prefix>; <prefix>_0_*.bin):
  - g_TriangleArea and g_Vertices by their first large-chart triangles (the large charts are
    exact, so both sides start alike), g_TriangleListForThreadGroup by its leading run of equal
    tile starts and a non-decreasing check, $Globals by its stride / offsets / block rect head;
and writes each as <out>/valve_<name>.bin (the vertex and area arrays read past our sizes, up
to 4x, where the memory allows; the tile list as long as ours). Compare offline.
SMALLCHARTS_ONLY=globals,... limits the scans (each takes minutes).

Uses the GPU: run it only when CS2 is closed and the PC is free.
"""
import json
import os
import struct
import sys
import time

import frida

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
VRAD3 = os.path.join(CS2, "game", "bin", "win64", "vrad3.exe")

AGENT = r"""
const P = %s;
function hexOf(bytes) { return bytes.map(b => ('0' + b.toString(16)).slice(-2)).join(' '); }
function ranges() { return Process.enumerateRanges('rw-'); }
function scan(bytes) {
    const out = [];
    for (const r of ranges()) {
        let m;
        try { m = Memory.scanSync(r.base, r.size, hexOf(bytes)); } catch (e) { continue; }
        for (const x of m) out.push({ at: x.address, end: r.base.add(r.size) });
    }
    return out;
}
function readUpTo(at, end, size) {
    const n = Math.min(size, Number(end.sub(at).toString()));
    return at.readByteArray(n);
}
let done = false, seen = 0;
function onDispatch(at) {
    Interceptor.attach(at, {
        onEnter(args) {
            const x = args[1].toUInt32(), y = args[2].toUInt32(), z = args[3].toUInt32();
            send({ kind: 'dispatch', x, y, z });
            if (done || x !== P.groupsX || y !== P.groupsY || z !== 1) return;
            if (seen++ < P.generation) return;
            done = true;
            for (const name of P.only) {
                const hits = scan(P.patterns[name]);
                send({ kind: 'found', name, count: hits.length });
                hits.slice(0, 4).forEach((h, k) => send({ kind: 'data', name, index: k }, readUpTo(h.at, h.end, P.sizes[name])));
            }
            if (P.only.indexOf('tiles') < 0) return;
            // The tile list: a run of the first tile start, then non-decreasing to the end.
            const hits = scan(P.patterns.tiles);
            let k = 0;
            for (const h of hits) {
                if (Number(h.end.sub(h.at).toString()) < P.sizes.tiles) continue;
                const a = new Uint32Array(h.at.readByteArray(P.sizes.tiles));
                let ok = a[0] === P.firstTile;
                for (let i = 1; i < a.length && ok; i++) ok = a[i] >= a[i - 1] && a[i] < 1000000;
                if (!ok) continue;
                send({ kind: 'data', name: 'tiles', index: k++ }, a.buffer);
                if (k >= 4) break;
            }
            send({ kind: 'found', name: 'tiles', count: k });
        }
    });
}
const hooked = {};
const wanted = { vkCmdDispatch: onDispatch };
function hookAt(name, at) {
    const key = at.toString();
    if (hooked[key] || at.isNull()) return;
    hooked[key] = true;
    wanted[name](at);
}
function hookLoader(m) {
    for (const name of Object.keys(wanted)) { const e = m.findExportByName(name); if (e) hookAt(name, e); }
    for (const gpa of ['vkGetDeviceProcAddr', 'vkGetInstanceProcAddr']) {
        const at = m.findExportByName(gpa);
        if (!at) continue;
        Interceptor.attach(at, {
            onEnter(args) { this.name = args[1].readUtf8String(); },
            onLeave(ret) { if (wanted[this.name] && !ret.isNull()) hookAt(this.name, ret); }
        });
    }
}
const found = Process.findModuleByName('vulkan-1.dll');
if (found) hookLoader(found);
else Process.attachModuleObserver({ onAdded(m) { if (m.name.toLowerCase() === 'vulkan-1.dll') hookLoader(m); } });
"""


def main():
    if len(sys.argv) not in (5, 6):
        print(__doc__)
        sys.exit(2)
    addon, block, prefix, out = sys.argv[1], int(sys.argv[2]), sys.argv[3], os.path.abspath(sys.argv[4])
    generation = int(sys.argv[5]) if len(sys.argv) == 6 else 0
    os.makedirs(out, exist_ok=True)
    job = addon if os.path.isdir(addon) else os.path.join(CS2, "game", "csgo_addons", addon, "_vrad3")
    ours = {n: open(f"{prefix}_{generation}_{n}.bin", "rb").read() for n in ("areas", "vertices", "tiles", "globals")}
    g = struct.unpack("<11i", ours["globals"])
    params = {
        "only": os.environ.get("SMALLCHARTS_ONLY", "areas,vertices,globals,tiles").split(","),
        "groupsX": g[5] // 8, "groupsY": g[5] // 8, "generation": generation, "firstTile": struct.unpack_from("<I", ours["tiles"])[0],
        "patterns": {"areas": list(ours["areas"][:16]), "vertices": list(ours["vertices"][:24]), "globals": list(ours["globals"][:24]),
                     "tiles": list(ours["tiles"][:64])},
        "sizes": {"areas": 4 * len(ours["areas"]), "vertices": 4 * len(ours["vertices"]), "globals": 44, "tiles": len(ours["tiles"])},
    }
    script = open(os.path.join(job, "script-gpu.vrad3"), encoding="utf-8").read()
    vmap = next(line.split()[1] for line in script.splitlines() if line.startswith("// maps/"))
    name = "script-capture-smallcharts.vrad3"
    open(os.path.join(job, name), "w", encoding="utf-8").write("\n".join(
        ["gpu_init", "lightmap_type ahd", "sceneinfo_gpu scene_info.kv3", "lightmap_image_gpu 4096 4096 1024",
         "lightmap_load_packing_geometry_gpu lightmap_packing_geometry.dat", f"lightmap_load_block_gpu lightmap_info.dat {block}",
         f"lightmap_compute_block_gpu {4 * generation + 1} {4 * generation + 1} 0.200", "exit 0", ""]))
    args = [VRAD3, "-map", vmap, "-script", name, "-vulkan", "-gpuraytracing", "-allthreads", "-unbufferedio"]
    pid = frida.spawn(args, cwd=job)
    session = frida.attach(pid)
    log = []

    def on_message(message, data):
        p = message.get("payload", message)
        if isinstance(p, dict) and p.get("kind") == "data":
            with open(os.path.join(out, f"valve_{p['name']}_{p['index']}.bin"), "wb") as f:
                f.write(data or b"")
        if not (isinstance(p, dict) and p.get("kind") == "dispatch"):
            log.append(json.dumps(p))

    agent = session.create_script(AGENT % json.dumps(params))
    agent.on("message", on_message)
    agent.load()
    done = [False]
    session.on("detached", lambda reason, crash: done.__setitem__(0, True))
    frida.resume(pid)
    while not done[0]:
        time.sleep(0.5)
    print("\n".join(log), flush=True)


if __name__ == "__main__":
    main()
