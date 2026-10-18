"""capture_accel.py <addon> <out.txt>: how vrad3 builds its ray tracing acceleration structures.

Runs vrad3.exe on the addon's _vrad3 job (gpu_init, the scene, block 5 at one sample) under Frida
and logs, at the device entry points vkGetDeviceProcAddr hands out:
  - vkGetAccelerationStructureBuildSizesKHR / vkCmdBuildAccelerationStructuresKHR: per build info
    the type, build flags, mode, src/dst, and per geometry its type and flags, the triangle
    vertex format, stride, maxVertex, index type, whether a transform is given, the instance
    array-of-pointers bit, and the build range (primitive count, offsets);
  - vkCreateAccelerationStructureKHR: create flags, type, size, offset;
  - vkCmdCopyAccelerationStructureKHR: the copy mode (compaction or clone);
  - vkCmdWriteAccelerationStructuresPropertiesKHR: the query type;
  - the TLAS's instance array (VkAccelerationStructureInstanceKHR: transform, custom index, mask,
    SBT offset, flags), read through the buffer that holds its device address, its bound memory
    and that memory's vkMapMemory pointer, or the staging buffer a vkCmdCopyBuffer filled it from.
A summary at the end counts the distinct build shapes. RtxBakeHost builds the same way.

Uses the GPU: run it only when CS2 is closed and the PC is free.
"""
import collections
import os
import sys
import time

import frida

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
VRAD3 = os.path.join(CS2, "game", "bin", "win64", "vrad3.exe")

AGENT = r"""
const hooked = {};
const buffers = {};     // buffer handle -> { memory, offset, address }
const mapped = {};      // memory handle -> pointer at offset 0
const copies = {};      // dst buffer -> [src buffer, srcOffset, dstOffset, size]
let allocations = 0, maps = 0;
const blasAddresses = new Set();   // vkGetAccelerationStructureDeviceAddressKHR results
function pattern(hex) {
    hex = hex.padStart(16, '0'); const b = [];
    for (let k = 14; k >= 0; k -= 2) b.push(hex.substr(k, 2));
    return b.join(' ');
}
// The instance array still in host memory: a 64-byte stride run of records whose BLAS field is a BLAS address.
function scanInstances(count) {
    for (const first of Array.from(blasAddresses).slice(0, 8)) {
        for (const range of Process.enumerateRanges('rw-')) {
            let found;
            try { found = Memory.scanSync(range.base, range.size, pattern(first)); } catch (e) { continue; }
            for (const m of found) {
                for (let start = 0; start < count; start++) {
                    const base = m.address.sub(56 + start * 64);
                    let ok = true;
                    try { for (let i = 0; i < count && ok; i++) ok = blasAddresses.has(base.add(i * 64 + 56).readU64().toString(16)); } catch (e) { ok = false; }
                    if (ok) return base;
                }
            }
        }
    }
    return null;
}
function hostPointer(address) {
    for (const b of Object.keys(buffers)) {
        const e = buffers[b];
        if (e.address === undefined || address < e.address || address >= e.address + 0x10000000n) continue;
        const delta = Number(address - e.address);
        if (e.memory !== undefined && mapped[e.memory]) return mapped[e.memory].add(e.offset + delta);
        const c = copies[b];
        if (c && buffers[c[0]] && mapped[buffers[c[0]].memory])
            return mapped[buffers[c[0]].memory].add(buffers[c[0]].offset + c[1] + delta - c[2]);
    }
    return null;
}
function instances(address, count) {
    let p = hostPointer(address);
    if (p === null) p = scanInstances(count);
    if (p === null) {
        const known = Object.values(buffers).filter(e => e.address !== undefined);
        let best = null;
        for (const e of known) if (e.address <= address && (best === null || e.address > best.address)) best = e;
        send({ kind: 'instances', error: 'no host copy of ' + address.toString(16), buffers: Object.keys(buffers).length, withAddress: known.length,
               mappedMemories: Object.keys(mapped).length, allocations, maps, copies: Object.keys(copies).length,
               copy: best === null ? '-' : JSON.stringify(copies[Object.keys(buffers).find(k => buffers[k] === best)] || null),
               nearest: best === null ? '-' : best.address.toString(16) + ' mem ' + best.memory + ' mapped ' + (best.memory !== undefined && !!mapped[best.memory]) });
        return;
    }
    for (let i = 0; i < count; i++) {
        const q = p.add(i * 64), f = [];
        for (let k = 0; k < 12; k++) f.push(q.add(k * 4).readFloat());
        const a = q.add(48).readU32(), b = q.add(52).readU32();
        send({ kind: 'instance', index: i, custom: a & 0xffffff, mask: (a >>> 24).toString(16), sbt: b & 0xffffff, flags: (b >>> 24).toString(16),
               blas: q.add(56).readU64().toString(16), transform: f.join(',') });
    }
}
function geometries(info) {
    const n = info.add(48).readU32(), p = info.add(56).readPointer(), pp = info.add(64).readPointer();
    const r = [];
    for (let k = 0; k < n; k++) {
        const g = p.isNull() ? pp.add(k * 8).readPointer() : p.add(k * 96);
        const type = g.add(16).readU32(), flags = g.add(88).readU32();
        const d = g.add(24);
        if (type === 0)
            r.push({ type, flags, format: d.add(16).readU32(), stride: d.add(32).readU64().toNumber(), maxVertex: d.add(40).readU32(),
                     indexType: d.add(44).readU32(), transform: !d.add(56).readPointer().isNull(), pNext: d.add(8).readPointer().isNull() ? 0 : d.add(8).readPointer().readU32() });
        else if (type === 2)
            r.push({ type, flags, arrayOfPointers: d.add(16).readU32() });
        else
            r.push({ type, flags });
    }
    return r;
}
function info(i) {
    return { type: i.add(16).readU32(), flags: i.add(20).readU32(), mode: i.add(24).readU32(),
             src: i.add(32).readU64().toString(16), dst: i.add(40).readU64().toString(16), pNext: i.add(8).readPointer().isNull() ? 0 : i.add(8).readPointer().readU32(),
             geometries: geometries(i) };
}
const wanted = {
    vkGetAccelerationStructureBuildSizesKHR: at => Interceptor.attach(at, {
        onEnter(args) {
            const n = info(args[2]); const counts = [];
            for (let k = 0; k < n.geometries.length; k++) counts.push(args[3].add(k * 4).readU32());
            send({ kind: 'sizes', buildType: args[1].toUInt32(), info: n, maxPrimitives: counts });
        }
    }),
    vkCmdBuildAccelerationStructuresKHR: at => Interceptor.attach(at, {
        onEnter(args) {
            const count = args[1].toUInt32();
            for (let i = 0; i < count; i++) {
                const n = info(args[2].add(i * 80));
                const ranges = args[3].add(i * 8).readPointer(); const rs = [];
                for (let k = 0; k < n.geometries.length; k++) {
                    const r = ranges.add(k * 16);
                    rs.push([r.readU32(), r.add(4).readU32(), r.add(8).readU32(), r.add(12).readU32()]);
                }
                send({ kind: 'build', batch: count, index: i, info: n, ranges: rs });
                if (n.type === 0) {
                    const g = args[2].add(i * 80).add(56).readPointer();
                    instances(BigInt(g.add(24).add(24).readU64().toString()), rs[0][0]);
                }
            }
        }
    }),
    vkCreateAccelerationStructureKHR: at => Interceptor.attach(at, {
        onEnter(args) { const c = args[1]; this.out = args[3];
            this.m = { kind: 'create', createFlags: c.add(16).readU32(), offset: c.add(32).readU64().toNumber(), size: c.add(40).readU64().toNumber(), type: c.add(48).readU32(),
                       pNext: c.add(8).readPointer().isNull() ? 0 : c.add(8).readPointer().readU32() }; },
        onLeave() { this.m.handle = this.out.readU64().toString(16); send(this.m); }
    }),
    vkGetBufferDeviceAddress: at => Interceptor.attach(at, {
        onEnter(args) { this.buffer = args[1].add(16).readU64().toString(16); },
        onLeave(r) { (buffers[this.buffer] = buffers[this.buffer] || {}).address = BigInt(r.toString()); }
    }),
    vkGetBufferDeviceAddressKHR: at => Interceptor.attach(at, {
        onEnter(args) { this.buffer = args[1].add(16).readU64().toString(16); },
        onLeave(r) { (buffers[this.buffer] = buffers[this.buffer] || {}).address = BigInt(r.toString()); }
    }),
    vkBindBufferMemory: at => Interceptor.attach(at, {
        onEnter(args) { const e = buffers[args[1].toString(16)] = buffers[args[1].toString(16)] || {}; e.memory = args[2].toString(16); e.offset = args[3].toNumber(); }
    }),
    vkBindBufferMemory2: at => Interceptor.attach(at, {
        onEnter(args) { for (let k = 0; k < args[1].toUInt32(); k++) { const i = args[2].add(k * 40);
            const e = buffers[i.add(16).readU64().toString(16)] = buffers[i.add(16).readU64().toString(16)] || {}; e.memory = i.add(24).readU64().toString(16); e.offset = i.add(32).readU64().toNumber(); } }
    }),
    vkGetAccelerationStructureDeviceAddressKHR: at => Interceptor.attach(at, {
        onEnter(args) { this.handle = args[1].add(16).readU64().toString(16); },
        onLeave(r) { blasAddresses.add(r.toString(16)); send({ kind: 'address', handle: this.handle, address: r.toString(16) }); }
    }),
    vkMapMemory: at => Interceptor.attach(at, {
        onEnter(args) { this.memory = args[1].toString(16); this.offset = args[2].toNumber(); this.out = args[5]; },
        onLeave() { maps++; mapped[this.memory] = this.out.readPointer().sub(this.offset); }
    }),
    vkAllocateMemory: at => Interceptor.attach(at, {
        onEnter(args) {
            this.out = args[3]; this.host = null;
            for (let q = args[1].add(8).readPointer(); !q.isNull(); q = q.add(8).readPointer())
                if (q.readU32() === 1000178000) this.host = q.add(24).readPointer();   // VkImportMemoryHostPointerInfoEXT
            allocations++;
        },
        onLeave() { if (this.host !== null) mapped[this.out.readU64().toString(16)] = this.host; }
    }),
    vkMapMemory2: at => Interceptor.attach(at, {
        onEnter(args) { this.memory = args[1].add(24).readU64().toString(16); this.offset = args[1].add(32).readU64().toNumber(); this.out = args[2]; },
        onLeave() { mapped[this.memory] = this.out.readPointer().sub(this.offset); }
    }),
    vkMapMemory2KHR: at => Interceptor.attach(at, {
        onEnter(args) { this.memory = args[1].add(24).readU64().toString(16); this.offset = args[1].add(32).readU64().toNumber(); this.out = args[2]; },
        onLeave() { mapped[this.memory] = this.out.readPointer().sub(this.offset); }
    }),
    vkCmdCopyBuffer: at => Interceptor.attach(at, {
        onEnter(args) { const r = args[4]; copies[args[2].toString(16)] = [args[1].toString(16), r.readU64().toNumber(), r.add(8).readU64().toNumber(), r.add(16).readU64().toNumber()]; }
    }),
    vkCmdCopyAccelerationStructureKHR: at => Interceptor.attach(at, {
        onEnter(args) { const c = args[1]; send({ kind: 'copy', src: c.add(16).readU64().toString(16), dst: c.add(24).readU64().toString(16), mode: c.add(32).readU32() }); }
    }),
    vkCmdWriteAccelerationStructuresPropertiesKHR: at => Interceptor.attach(at, {
        onEnter(args) { send({ kind: 'properties', count: args[1].toUInt32(), queryType: args[3].toUInt32() }); }
    }),
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


def shape(p):
    i = p["info"]
    return (p["kind"], i["type"], i["flags"], i["mode"], i["pNext"],
            tuple(tuple(sorted((k, v) for k, v in g.items() if k != "maxVertex")) for g in i["geometries"]))


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    addon, out = sys.argv[1], os.path.abspath(sys.argv[2])
    job = os.path.join(CS2, "game", "csgo_addons", addon, "_vrad3")
    script = open(os.path.join(job, "script-gpu.vrad3"), encoding="utf-8").read()
    vmap = next(line.split()[1] for line in script.splitlines() if line.startswith("// maps/"))
    name = "script-capture-accel.vrad3"
    open(os.path.join(job, name), "w", encoding="utf-8").write("\n".join(
        ["gpu_init", "lightmap_type ahd", "sceneinfo_gpu scene_info.kv3", "lightmap_image_gpu 4096 4096 1024",
         "lightmap_load_packing_geometry_gpu lightmap_packing_geometry.dat", "lightmap_load_block_gpu lightmap_info.dat 5",
         "lightmap_compute_block_gpu 1 1 0.200", "exit 0", ""]))
    args = [VRAD3, "-map", vmap, "-script", name, "-vulkan", "-gpuraytracing", "-allthreads", "-unbufferedio"]
    pid = frida.spawn(args, cwd=job)
    session = frida.attach(pid)
    records = []

    def on_message(message, data):
        records.append(message.get("payload", message))

    agent = session.create_script(AGENT)
    agent.on("message", on_message)
    agent.load()
    done = [False]
    session.on("detached", lambda reason, crash: done.__setitem__(0, True))
    frida.resume(pid)
    while not done[0]:
        time.sleep(0.5)
    # BLAS index of each instance: its address -> handle -> the build that made it (or the build whose COMPACT copy it is).
    built = [p["info"]["dst"] for p in records if isinstance(p, dict) and p.get("kind") == "build" and p["info"]["type"] == 1]
    compacted = {p["dst"]: p["src"] for p in records if isinstance(p, dict) and p.get("kind") == "copy"}
    handle_of = {p["address"]: p["handle"] for p in records if isinstance(p, dict) and p.get("kind") == "address"}
    for p in records:
        if isinstance(p, dict) and p.get("kind") == "instance":
            h = handle_of.get(p["blas"])
            h = compacted.get(h, h)
            p["blasIndex"] = built.index(h) if h in built else "?"
    summary = collections.Counter(shape(p) for p in records if isinstance(p, dict) and p.get("kind") in ("sizes", "build"))
    with open(out, "w", encoding="utf-8") as f:
        for p in records:
            f.write((" ".join(f"{k}={v}" for k, v in p.items()) if isinstance(p, dict) else repr(p)) + "\n")
        f.write("\nsummary (kind, type, flags, mode, pNext, geometries): count\n")
        for k, v in summary.most_common():
            f.write(f"{k}: {v}\n")
    print(len(records), "records in", out, flush=True)


if __name__ == "__main__":
    main()
