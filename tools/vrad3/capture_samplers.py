"""capture_samplers.py <addon> <out.txt>: Valve's bindless sampler table during a vrad3 run.

Runs vrad3.exe on the addon's _vrad3 job (gpu_init, the scene, block 5 at one sample) under Frida and logs:
  - every vkCreateSampler (VkSamplerCreateInfo fields and the handle it returns);
  - every descriptor write of type SAMPLER (vkUpdateDescriptorSets: set, binding, array element,
    handle), and the template updates if rendersystem uses them.
The device functions are hooked where vkGetDeviceProcAddr hands them out (and at vulkan-1's
exports). The bindless sampler slot a material block names is the array element of binding 29.

Uses the GPU: run it only when CS2 is closed and the PC is free.
"""
import os
import sys
import time

import frida

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
VRAD3 = os.path.join(CS2, "game", "bin", "win64", "vrad3.exe")

AGENT = r"""
const hooked = {};
function onCreateSampler(at) {
    Interceptor.attach(at, {
        onEnter(args) { this.info = args[1]; this.out = args[3]; },
        onLeave(ret) {
            const i = this.info;
            const f = o => i.add(o).readU32();
            const r = o => i.add(o).readFloat();
            send({ kind: 'sampler', handle: this.out.readU64().toString(16), result: ret.toInt32(),
                   flags: f(16), mag: f(20), min: f(24), mip: f(28), u: f(32), v: f(36), w: f(40), bias: r(44),
                   aniso: f(48), maxAniso: r(52), compare: f(56), compareOp: f(60), minLod: r(64), maxLod: r(68),
                   border: f(72), unnorm: f(76), pNext: i.add(8).readPointer().isNull() ? 0 : 1 });
        }
    });
}
function onUpdateSets(at) {
    Interceptor.attach(at, {
        onEnter(args) {
            const n = args[1].toUInt32();
            const w = args[2];
            for (let k = 0; k < n; k++) {
                const e = w.add(k * 64);
                const type = e.add(36).readU32();
                if (type !== 0 && type !== 1)
                    continue;
                const count = e.add(32).readU32();
                const first = e.add(28).readU32();
                const binding = e.add(24).readU32();
                const set = e.add(16).readU64().toString(16);
                const img = e.add(40).readPointer();
                const handles = [];
                for (let j = 0; j < count; j++)
                    handles.push(img.add(j * 24).readU64().toString(16));
                send({ kind: 'write', set, binding, first, type, handles });
            }
        }
    });
}
// Descriptor update templates: their sampler entries (VkDescriptorUpdateTemplateEntry: binding, array
// element, count, type, offset, stride), then each update's sampler handles from pData.
const templates = {};
function onCreateTemplate(at) {
    Interceptor.attach(at, {
        onEnter(args) { this.info = args[1]; this.out = args[3]; },
        onLeave(ret) {
            const n = this.info.add(20).readU32();
            const e = this.info.add(24).readPointer();
            const entries = [];
            for (let k = 0; k < n; k++) {
                const x = e.add(k * 32);
                entries.push({ binding: x.readU32(), first: x.add(4).readU32(), count: x.add(8).readU32(), type: x.add(12).readU32(),
                               offset: x.add(16).readU64().toNumber(), stride: x.add(24).readU64().toNumber() });
            }
            const h = this.out.readU64().toString(16);
            templates[h] = entries;
            send({ kind: 'templ', handle: h, entries: JSON.stringify(entries.filter(t => t.type === 0 || t.type === 1)) });
        }
    });
}
function onTemplate(at, name) {
    Interceptor.attach(at, {
        onEnter(args) {
            const set = args[1].toString(16);
            const t = templates[args[2].toString(16)];
            if (!t) { send({ kind: 'update', name, set, template: 'unknown' }); return; }
            for (const x of t) {
                if (x.type !== 0 && x.type !== 1)
                    continue;
                const handles = [];
                for (let j = 0; j < x.count; j++)
                    handles.push(args[3].add(x.offset + j * x.stride).readU64().toString(16));
                send({ kind: 'update', set, binding: x.binding, first: x.first, type: x.type, handles: handles.join(',') });
            }
        }
    });
}
// Immutable samplers in descriptor set layouts: per binding with pImmutableSamplers, its handles.
function onSetLayout(at) {
    Interceptor.attach(at, {
        onEnter(args) { this.info = args[1]; this.out = args[3]; },
        onLeave() {
            const n = this.info.add(20).readU32(), b = this.info.add(24).readPointer();
            for (let k = 0; k < n; k++) {
                const x = b.add(k * 24), imm = x.add(16).readPointer();
                if (imm.isNull()) continue;
                const count = x.add(8).readU32(), handles = [];
                for (let j = 0; j < count; j++) handles.push(imm.add(j * 8).readU64().toString(16));
                send({ kind: 'immutable', layout: this.out.readU64().toString(16), binding: x.readU32(), type: x.add(4).readU32(), handles: handles.join(',') });
            }
        }
    });
}
// Push descriptors: vkCmdPushDescriptorSetWithTemplate(KHR)(cmd, template, layout, set, pData) and
// vkCmdPushDescriptorSet(KHR)(cmd, bindPoint, layout, set, count, pWrites); the samplers they push.
function onPushTemplate(at, name) {
    Interceptor.attach(at, {
        onEnter(args) {
            const t = templates[args[1].toString(16)];
            if (!t) { send({ kind: 'push', name, template: 'unknown' }); return; }
            for (const x of t) {
                if (x.type !== 0 && x.type !== 1)
                    continue;
                const handles = [];
                for (let j = 0; j < x.count; j++)
                    handles.push(args[4].add(x.offset + j * x.stride).readU64().toString(16));
                send({ kind: 'push', set: args[3].toUInt32(), binding: x.binding, first: x.first, type: x.type, handles: handles.join(',') });
            }
        }
    });
}
function onPushSet(at, name) {
    Interceptor.attach(at, {
        onEnter(args) {
            const n = args[4].toUInt32(), w = args[5];
            for (let k = 0; k < n; k++) {
                const x = w.add(k * 64);
                const type = x.add(36).readU32();
                if (type !== 0 && type !== 1) continue;
                const count = x.add(32).readU32(), images = x.add(40).readPointer(), handles = [];
                for (let j = 0; j < count; j++) handles.push(images.add(j * 24).readU64().toString(16));
                send({ kind: 'push', set: args[3].toUInt32(), binding: x.add(24).readU32(), first: x.add(28).readU32(), type, handles: handles.join(',') });
            }
        }
    });
}
// VK_EXT_descriptor_buffer: vkGetDescriptorEXT(device, info {sType, pNext, type, data}, size, out).
function onGetDescriptor(at) {
    Interceptor.attach(at, {
        onEnter(args) {
            const type = args[1].add(16).readU32();
            if (type === 0)
                send({ kind: 'getdesc', type, sampler: args[1].add(24).readPointer().readU64().toString(16), out: args[3].toString(16), size: args[2].toNumber() });
            else if (type === 2)
                send({ kind: 'getdesc', type, out: args[3].toString(16), size: args[2].toNumber() });
        }
    });
}
const wanted = {
    vkGetDescriptorEXT: onGetDescriptor,
    vkCreateSampler: onCreateSampler,
    vkUpdateDescriptorSets: onUpdateSets,
    vkCreateDescriptorUpdateTemplate: onCreateTemplate,
    vkCreateDescriptorUpdateTemplateKHR: onCreateTemplate,
    vkUpdateDescriptorSetWithTemplate: a => onTemplate(a, 'vkUpdateDescriptorSetWithTemplate'),
    vkCreateDescriptorSetLayout: onSetLayout,
    vkCmdPushDescriptorSetWithTemplateKHR: a => onPushTemplate(a, 'vkCmdPushDescriptorSetWithTemplateKHR'),
    vkCmdPushDescriptorSetWithTemplate: a => onPushTemplate(a, 'vkCmdPushDescriptorSetWithTemplate'),
    vkCmdPushDescriptorSetKHR: a => onPushSet(a, 'vkCmdPushDescriptorSetKHR'),
    vkCmdPushDescriptorSet: a => onPushSet(a, 'vkCmdPushDescriptorSet'),
    vkUpdateDescriptorSetWithTemplateKHR: a => onTemplate(a, 'vkUpdateDescriptorSetWithTemplateKHR'),
};
function hookAt(name, at) {
    const key = at.toString();
    if (hooked[key] || at.isNull())
        return;
    hooked[key] = true;
    wanted[name](at);
    send({ kind: 'hooked', name, at: key });
}
function hookLoader(m) {
    for (const name of Object.keys(wanted)) {
        const e = m.findExportByName(name);
        if (e) hookAt(name, e);
    }
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
    name = "script-capture-samplers.vrad3"
    open(os.path.join(job, name), "w", encoding="utf-8").write("\n".join(
        ["gpu_init", "lightmap_type ahd", "sceneinfo_gpu scene_info.kv3", "lightmap_image_gpu 4096 4096 1024",
         "lightmap_load_packing_geometry_gpu lightmap_packing_geometry.dat", "lightmap_load_block_gpu lightmap_info.dat 5",
         "lightmap_compute_block_gpu 1 1 0.200", "exit 0", ""]))
    args = [VRAD3, "-map", vmap, "-script", name, "-vulkan", "-gpuraytracing", "-allthreads", "-unbufferedio"]
    print(" ".join(args), flush=True)
    pid = frida.spawn(args, cwd=job)
    session = frida.attach(pid)
    lines = []

    def on_message(message, data):
        if message["type"] != "send":
            lines.append(repr(message))
            return
        p = message["payload"]
        lines.append(" ".join(f"{k}={v}" for k, v in p.items()))

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
