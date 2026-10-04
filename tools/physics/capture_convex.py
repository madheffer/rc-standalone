"""capture_convex.py <addon> <map> <out.json> [--full] -- the meshes physicsbuilder hulls for convex world meshes.

physicsbuilder's world node callback (0924: 18001b0a0) hands a mesh set to
convex_single to 18001a950 and one set to convex_multi to 18001ac20; the
first copies every vertex of that CMesh (1800c3430) into the quickhull
builder (1800d3f40). This records, per call, the CMesh (vertex floats,
stream table, indices, and its material name at +0x68, a CBufferString
with flags at +0x64), and every quickhull build's input points with a short
backtrace, so the port's hull input can be checked point for point.

A full compile overwrites the map's .vpk: back it up. CS2 must be closed,
and the script refuses to start if it is running or another compile is.
"""
import argparse
import json
import os
import sys
import threading

import frida

from capture_physshapes import BIN, CS2, busy, low_disk
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "re"))
from rva_map import require  # noqa: E402

AGENT = r"""
'use strict';
function str(p) { try { const s = p.readPointer(); return s.isNull() ? null : s.readUtf8String(); } catch (e) { return '?'; } }
function hex(b) { return Array.from(new Uint8Array(b)).map(x => x.toString(16).padStart(2, '0')).join(''); }
function bufstr(p) {
  const flags = p.add(0x64).readU32();
  if ((flags & 0x3fffffff) === 0) return '';
  return ((flags >>> 30) & 1) ? p.add(0x68).readUtf8String() : p.add(0x68).readPointer().readUtf8String();
}
function cmesh(p) {
  const nv = p.add(0x18).readS32(), stride = p.add(0x1c).readS32(), ns = p.add(0x20).readS32(), ni = p.add(0x24).readS32();
  const streams = [];
  const t = p.add(8).readPointer();
  for (let i = 0; i < ns && !t.isNull(); i++) {
    const e = t.add(i * 0x28);
    streams.push({name: str(e), index: e.add(0x10).readS32(), offset: e.add(0x14).readS32(), count: e.add(0x18).readS32()});
  }
  const o = {vertices: nv, stride: stride, indices: ni, streams: streams, material: bufstr(p)};
  if (nv > 0 && stride > 0) o.vdata = hex(p.readPointer().readByteArray(nv * stride * 4));
  if (ni > 0) o.idata = hex(p.add(0x10).readPointer().readByteArray(ni * 4));
  return o;
}
function trace(ctx) {
  return Thread.backtrace(ctx, Backtracer.ACCURATE).slice(0, 8).map(a => {
    const mod = Process.findModuleByAddress(a);
    return mod ? mod.name.replace('.dll', '') + '+0x' + a.sub(mod.base).toString(16) : a.toString();
  });
}
let n = 0;
function hook() {
  const pb = Process.findModuleByName('physicsbuilder.dll');
  if (pb === null) { setTimeout(hook, 5); return; }
  for (const [rva, kind] of [[0x1a950, 'single'], [0x1ac20, 'multi']]) {
    Interceptor.attach(pb.base.add(rva), {
      onEnter(args) {
        try { send({call: n++, convex: kind, mesh: cmesh(args[1])}); }
        catch (e) { send({call: n++, convex: kind, err: String(e)}); }
      }
    });
  }
  Interceptor.attach(pb.base.add(0xd3f40), {
    onEnter(args) {
      const count = args[2].toInt32();
      const e = {call: n++, quickhull: count, stack: trace(this.context)};
      try { e.points = hex(args[1].readByteArray(count * 12)); } catch (err) { e.err = String(err); }
      send(e);
    }
  });
  send({hooked: pb.base.toString()});
}
hook();
"""


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("out")
    p.add_argument("--full", action="store_true", help="a full compile rather than -world -fshallow")
    a = p.parse_args()
    # Literal 0924 addresses below: refuse a build that moved them.
    require("physicsbuilder", [0x1a950, 0x1ac20, 0xd3f40])
    if busy():
        raise SystemExit("CS2 or another resourcecompiler is running; not starting")
    if low_disk():
        raise SystemExit("under 10 GB free on the game drive; not starting")
    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-f", "-game", os.path.join(CS2, "game", "csgo"), "-i", source]
    if not a.full:
        argv += ["-world", "-fshallow"]
    calls = []
    lock = threading.Lock()

    def on_message(msg, data):
        if msg.get("type") != "send":
            print(msg, flush=True)
            return
        pay = msg["payload"]
        if "call" not in pay:
            print(pay, flush=True)
            return
        with lock:
            calls.append(pay)
        if "convex" in pay:
            m = pay.get("mesh", {})
            print("convex", pay["convex"], m.get("vertices"), m.get("material"), flush=True)

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
    with open(a.out, "w", encoding="utf-8") as fh:
        json.dump(calls, fh)
    print(a.out, len(calls), "calls")


if __name__ == "__main__":
    main()
