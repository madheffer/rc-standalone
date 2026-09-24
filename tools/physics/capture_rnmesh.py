"""capture_rnmesh.py <addon> <map> <out.bin> [--full] -- every RnMeshCreate call of a compile.

vphysics2's RnMeshCreate(triangles, indices, materials, vertexCount,
vertices, mesh, options) builds a triangle-mesh shape. This records each
call's inputs (int indices, per-triangle materials when given, float3
vertices, the 16-byte options) and, on return, the mesh it filled: the
first 0xc0 bytes, the nodes (32 bytes each, count at +0x18, data at +0x20),
vertices (12 bytes, +0x30/+0x38), triangles (12 bytes, +0x48/+0x50) and
materials (1 byte, +0x90/+0x98).

Records go to <out.bin> as [u32 json length][json][u32 blob length][blob].
The compile overwrites the map's .vpk: back it up. CS2 must be closed, and
the script refuses to start if it is running or another compile is.
"""
import argparse
import json
import os
import struct
import subprocess
import threading

import frida

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
'use strict';
function blob(p, n) { return (n > 0 && !p.isNull()) ? p.readByteArray(n) : null; }
function hook() {
  const m = Process.findModuleByName('vphysics2.dll');
  if (m === null) { setTimeout(hook, 5); return; }
  const f = m.findExportByName('RnMeshCreate');
  let n = 0;
  Interceptor.attach(f, {
    onEnter(args) {
      this.id = n++;
      const tris = args[0].toInt32();
      const vcount = args[3].toInt32();
      const opt = args[6];
      const parts = {
        indices: blob(args[1], tris * 12),
        materials: blob(args[2], tris),
        vertices: blob(args[4], vcount * 12),
        options: opt.isNull() ? null : blob(opt, 16),
      };
      this.out = args[5];
      send({ev: 'in', id: this.id, tris: tris, vcount: vcount, hasMaterials: !args[2].isNull(),
            hasOptions: !opt.isNull(), outGiven: !args[5].isNull(),
            sizes: Object.fromEntries(Object.entries(parts).map(([k, v]) => [k, v ? v.byteLength : 0])),
            caller: DebugSymbol.fromAddress(this.returnAddress).toString(),
            stack: Thread.backtrace(this.context, Backtracer.ACCURATE).slice(0, 24).map(a => {
              const mod = Process.findModuleByAddress(a);
              return mod ? mod.name.replace('.dll', '') + '+0x' + a.sub(mod.base).toString(16) : a.toString();
            })},
           concat([parts.indices, parts.materials, parts.vertices, parts.options]));
    },
    onLeave(ret) {
      const mesh = ret;
      if (mesh.isNull()) { send({ev: 'out', id: this.id, none: true}); return; }
      const head = mesh.readByteArray(0xc0);
      const nodes = blob(mesh.add(0x20).readPointer(), mesh.add(0x18).readS32() * 32);
      const verts = blob(mesh.add(0x38).readPointer(), mesh.add(0x30).readS32() * 12);
      const tris = blob(mesh.add(0x50).readPointer(), mesh.add(0x48).readS32() * 12);
      const mats = blob(mesh.add(0x98).readPointer(), mesh.add(0x90).readS32());
      const parts = [head, nodes, verts, tris, mats];
      send({ev: 'out', id: this.id, sizes: parts.map(p => p ? p.byteLength : 0)}, concat(parts));
    }
  });
  send({hooked: f.toString()});
}
function concat(list) {
  let total = 0;
  for (const b of list) if (b) total += b.byteLength;
  const out = new Uint8Array(total);
  let at = 0;
  for (const b of list) if (b) { out.set(new Uint8Array(b), at); at += b.byteLength; }
  return out.buffer;
}
hook();
"""


def busy():
    out = subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower()
    return "cs2.exe" in out or "resourcecompiler.exe" in out


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("out")
    p.add_argument("--full", action="store_true", help="a full compile rather than -world -fshallow")
    a = p.parse_args()
    if busy():
        raise SystemExit("CS2 or another resourcecompiler is running; not starting")
    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"), "-i", source]
    if not a.full:
        argv += ["-world", "-fshallow"]
    out = open(a.out, "wb")
    lock = threading.Lock()

    def on_message(msg, data):
        if msg.get("type") != "send":
            print(msg)
            return
        pay = msg["payload"]
        if "ev" not in pay:
            print(pay)
            return
        head = json.dumps(pay).encode()
        with lock:
            out.write(struct.pack("<I", len(head)) + head + struct.pack("<I", len(data or b"")) + (data or b""))
        if pay["ev"] == "in":
            print("in", pay["id"], pay["tris"], "tris", pay["vcount"], "verts", " ".join(pay.get("stack", [])[:14]))

    dev = frida.get_local_device()
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT)
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    done.wait()
    out.close()


if __name__ == "__main__":
    main()
