"""capture_pipelines.py <addon> <out.txt>: how vrad3 creates its ray tracing pipelines.

Runs vrad3.exe on the addon's _vrad3 job (gpu_init, the scene, block 5 at one sample) under Frida
and logs, at the device entry points vkGetDeviceProcAddr hands out (and vulkan-1's exports):
  - vkCreateRayTracingPipelinesKHR: deferred operation, pipeline cache, and per create info the
    flags, stage and group counts, maxPipelineRayRecursionDepth, library and dynamic state
    pointers and the pNext chain's sTypes; each stage's kind, entry name and module (the md5 of
    the SPIR-V vkCreateShaderModule was given);
  - vkCreatePipelineCache: initial data size and flags;
  - vkCreateComputePipelines / vkCreateGraphicsPipelines: the cache handle and flags.
RtxBakeHost creates its pipelines the same way.

Uses the GPU: run it only when CS2 is closed and the PC is free.
"""
import hashlib
import os
import sys
import time

import frida

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
VRAD3 = os.path.join(CS2, "game", "bin", "win64", "vrad3.exe")

AGENT = r"""
const hooked = {};
const moduleSize = {};
function chain(p) {
    const r = [];
    while (!p.isNull()) { r.push(p.readU32()); p = p.add(8).readPointer(); }
    return r.join('/');
}
function onModule(at) {
    Interceptor.attach(at, {
        onEnter(args) { this.size = args[1].add(24).readU64().toNumber(); this.code = args[1].add(32).readPointer(); this.out = args[3]; },
        onLeave() {
            const h = this.out.readU64().toString(16);
            moduleSize[h] = this.size;
            send({ kind: 'module', handle: h, size: this.size }, this.code.readByteArray(this.size));
        }
    });
}
function onRt(at) {
    Interceptor.attach(at, {
        onEnter(args) {
            const n = args[3].toUInt32();
            const ci = args[4];
            for (let i = 0; i < n; i++) {
                const c = ci.add(i * 104);
                const stages = c.add(20).readU32(), pStages = c.add(24).readPointer();
                const names = [];
                for (let k = 0; k < stages; k++) {
                    const st = pStages.add(k * 48);
                    names.push(st.add(20).readU32().toString(16) + ':' + st.add(32).readPointer().readUtf8String() + ':' + st.add(24).readU64().toString(16) +
                               (st.add(40).readPointer().isNull() ? '' : ':spec'));
                }
                const dyn = c.add(72).readPointer();
                let dynStates = '';
                if (!dyn.isNull()) {
                    const dn = dyn.add(20).readU32(), dp = dyn.add(24).readPointer();
                    const ds = []; for (let k = 0; k < dn; k++) ds.push(dp.add(k * 4).readU32()); dynStates = ds.join(',');
                }
                const lib = c.add(64).readPointer();
                const gp = c.add(40).readPointer(), gn = c.add(32).readU32(), grp = [];
                const u = x => x === 0xffffffff ? '-' : x;
                for (let k = 0; k < gn; k++) { const g = gp.add(k * 48); grp.push(g.add(16).readU32() + ':' + u(g.add(20).readU32()) + '/' + u(g.add(24).readU32()) + '/' + u(g.add(28).readU32())); }
                send({ kind: 'rtpipeline', deferred: args[1].toString(), cache: args[2].toString(), flags: c.add(16).readU32().toString(16),
                       stages, groups: c.add(32).readU32(), recursion: c.add(48).readU32(),
                       library: c.add(56).readPointer().toString(),
                       maxPayload: lib.isNull() ? '-' : lib.add(16).readU32() + '/' + lib.add(20).readU32(),
                       dynamic: dynStates, pNext: chain(c.add(8).readPointer()), groupList: grp.join(' '), names: names.join(' ') });
            }
        }
    });
}
function onCache(at) {
    Interceptor.attach(at, {
        onEnter(args) { send({ kind: 'cache', flags: args[1].add(16).readU32(), size: args[1].add(24).readU64().toNumber() }); }
    });
}
function onPipelines(at, name) {
    Interceptor.attach(at, {
        onEnter(args) { send({ kind: name, cache: args[1].toString(), count: args[2].toUInt32(), flags: args[3].add(16).readU32().toString(16) }); }
    });
}
function onStack(at) { Interceptor.attach(at, { onEnter(args) { send({ kind: 'stacksize', size: args[1].toUInt32() }); } }); }
function onJoin(at) { Interceptor.attach(at, { onEnter(args) { this.t = Process.getCurrentThreadId(); }, onLeave(r) { send({ kind: 'join', thread: this.t, result: r.toInt32() }); } }); }
function onConcurrency(at) { Interceptor.attach(at, { onLeave(r) { send({ kind: 'concurrency', value: r.toUInt32() }); } }); }
// vkCreateDevice: the enabled extensions and every pNext struct (sType, its bytes as hex, 512 at most).
function onDevice(at) {
    Interceptor.attach(at, {
        onEnter(args) {
            const ci = args[1];
            const n = ci.add(48).readU32(), names = ci.add(56).readPointer(), ext = [];
            for (let k = 0; k < n; k++) ext.push(names.add(k * 8).readPointer().readUtf8String());
            send({ kind: 'device', extensions: ext.join(' '), features10: ci.add(64).readPointer().isNull() ? '-' : 'set' });
            for (let q = ci.add(8).readPointer(); !q.isNull(); q = q.add(8).readPointer()) {
                const bytes = new Uint8Array(q.readByteArray(512));
                let hex = '';
                for (let k = 16; k < 512; k++) hex += ('0' + bytes[k].toString(16)).slice(-2);
                send({ kind: 'feature', sType: q.readU32(), bytes: hex });
            }
        }
    });
}
const wanted = {
    vkCreateDevice: onDevice,
    vkCmdSetRayTracingPipelineStackSizeKHR: onStack,
    vkDeferredOperationJoinKHR: onJoin,
    vkGetDeferredOperationMaxConcurrencyKHR: onConcurrency,
    vkCreateShaderModule: onModule,
    vkCreateRayTracingPipelinesKHR: onRt,
    vkCreatePipelineCache: onCache,
    vkCreateComputePipelines: a => onPipelines(a, 'compute'),
    vkCreateGraphicsPipelines: a => onPipelines(a, 'graphics'),
};
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
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    addon, out = sys.argv[1], os.path.abspath(sys.argv[2])
    job = os.path.join(CS2, "game", "csgo_addons", addon, "_vrad3")
    script = open(os.path.join(job, "script-gpu.vrad3"), encoding="utf-8").read()
    vmap = next(line.split()[1] for line in script.splitlines() if line.startswith("// maps/"))
    name = "script-capture-pipelines.vrad3"
    open(os.path.join(job, name), "w", encoding="utf-8").write("\n".join(
        ["gpu_init", "lightmap_type ahd", "sceneinfo_gpu scene_info.kv3", "lightmap_image_gpu 4096 4096 1024",
         "lightmap_load_packing_geometry_gpu lightmap_packing_geometry.dat", "lightmap_load_block_gpu lightmap_info.dat 5",
         "lightmap_compute_block_gpu 1 1 0.200", "exit 0", ""]))
    args = [VRAD3, "-map", vmap, "-script", name, "-vulkan", "-gpuraytracing", "-allthreads", "-unbufferedio"]
    pid = frida.spawn(args, cwd=job)
    session = frida.attach(pid)
    lines = []

    modules = {}

    def on_message(message, data):
        p = message.get("payload", message)
        if isinstance(p, dict) and p.get("kind") == "module":
            modules[p["handle"]] = hashlib.md5(data).hexdigest()
            return
        if isinstance(p, dict) and p.get("kind") == "rtpipeline":
            # each stage's module as the md5 of its SPIR-V
            p["names"] = " ".join(":".join(part if i != 2 else modules.get(part, part) for i, part in enumerate(n.split(":"))) for n in p["names"].split())
        lines.append(" ".join(f"{k}={v}" for k, v in p.items()) if isinstance(p, dict) else repr(p))

    agent = session.create_script(AGENT)
    agent.on("message", on_message)
    agent.load()
    done = [False]
    session.on("detached", lambda reason, crash: done.__setitem__(0, True))
    frida.resume(pid)
    while not done[0]:
        time.sleep(0.5)
    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print(len(lines), "records in", out, flush=True)


if __name__ == "__main__":
    main()
