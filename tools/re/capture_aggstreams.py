"""capture_aggstreams.py <addon> <map> [--match REGEX] [--out file]: where an aggregate draw gains a stream
it lacks (OPEN 1.c7, ledger 243, CS2_CLOSED_QUEUE A.12).

atixref's agg_prop_inferno_concrete_trim_weathered_dirt_blend_01_0 is one vertex buffer in Valve's output,
with VertexPaintBlendParams = 0 for the inferno_fountain_01 draw, whose CMesh has no such stream. A -world
-fshallow compile under Frida logs the CMeshes' stream lists at three points:

  modeldoc  WRB_MeshesToModelDoc (0923 1801fee20) entry: the (CMesh, group) pairs, args[0] a CUtlVector
  dme       DmeMeshToCMesh (0923 180d47b90) exit: the CMeshes it made (state args[0] +0x20 count, +0x28 data)
  load      WRB_LoadPropMeshes (0923 1801d30d0) exit: the (CMesh, ref) pairs it appended to args[4]
  aggregate WRB_BuildAggregate (0923 180268e80) entry: the entries' CMeshes (args[3], 0x238 each, +0x10)
  layout    CMesh_BuildPackedInputLayout (0923 181364720; 1103 181365ad0, same prologue) entry: args[0]

A CMesh (capture_physshapes.py's reading): vertex floats +0, stream table +8 (0x28 each: name, semantic,
index +0x10, first float +0x14, float count +0x18), vertex count +0x18, stride +0x1c, streams +0x20; the
material name is a CBufferString at +0x60 (WRB_MeshesToModelDoc reads it there). For meshes whose material
matches --match, each stream also says whether all its values are zero, and the layout hook sends a
backtrace (installed RVAs); --dump tangent sends a matched mesh's whole tangent stream as hex (A.13). CS2 must be closed; the package is backed up and restored, cmp-checked.
"""
import argparse
import filecmp
import json
import os
import shutil
import subprocess
import sys
import threading

import frida

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from rva_map import installed  # noqa: E402

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
LAYOUT_1103 = 0x1365ad0  # rva_map: "changed"; the 1103 function has 0923's prologue and arguments

AGENT = r"""
'use strict';
const MATCH = new RegExp(%(match)s, 'i');
const DUMP = %(dump)s;
let mod = null;
function cstr(p) { try { return p.isNull() ? '' : p.readUtf8String(); } catch (e) { return '?'; } }
function bufstr(p) {
  const flags = p.add(4).readU32();
  if ((flags >>> 30) & 1) return cstr(p.add(8));
  if ((flags & 0x3fffffff) === 0) return '';
  return cstr(p.add(8).readPointer());
}
function cmesh(p) {
  const nv = p.add(0x18).readS32(), stride = p.add(0x1c).readS32(), ns = p.add(0x20).readS32();
  let material = '';
  try { material = bufstr(p.add(0x60)); } catch (e) { material = '?'; }
  const want = MATCH.test(material);
  const streams = [];
  const t = p.add(8).readPointer();
  for (let i = 0; i < ns && i < 64 && !t.isNull(); i++) {
    const e = t.add(i * 0x28);
    const s = {name: cstr(e.readPointer()), semantic: cstr(e.add(8).readPointer()), index: e.add(0x10).readS32(),
               first: e.add(0x14).readS32(), count: e.add(0x18).readS32()};
    if (want && nv > 0 && stride > 0) {
      const v = new Float32Array(p.readPointer().readByteArray(nv * stride * 4));
      let zero = true;
      for (let k = 0; k < nv && zero; k++)
        for (let c = 0; c < s.count; c++) if (v[k * stride + s.first + c] !== 0) { zero = false; break; }
      s.allZero = zero;
      s.v0 = Array.from(v.slice(s.first, s.first + s.count));
      if (DUMP && s.name.toLowerCase().indexOf(DUMP) >= 0) {
        const u = new Uint32Array(v.buffer), parts = [];
        for (let k = 0; k < nv; k++)
          for (let c = 0; c < s.count; c++) parts.push(u[k * stride + s.first + c].toString(16).padStart(8, '0'));
        s.hex = parts.join('');
      }
    }
    streams.push(s);
  }
  return {mesh: p.toString(), material: material, match: want, vertices: nv, stride: stride, lod: p.add(0x58).readU8(),
          streams: streams};
}
function bt(ctx) {
  return Thread.backtrace(ctx, Backtracer.ACCURATE).slice(0, 10).map(a => {
    const m = Process.findModuleByAddress(a);
    return m ? m.name + '+' + a.sub(m.base).toString(16) : a.toString();
  });
}
function hook() {
  mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(%(modeldoc)d), {
    onEnter(args) {
      const v = args[0], n = v.readS32(), data = v.add(8).readPointer();
      const meshes = [];
      for (let i = 0; i < n; i++)
        meshes.push(Object.assign({group: data.add(i * 16 + 8).readS32()}, cmesh(data.add(i * 16).readPointer())));
      send({stage: 'modeldoc', meshes: meshes});
    }
  });
  Interceptor.attach(mod.base.add(%(dme)d), {
    onEnter(args) { this.state = args[0]; },
    onLeave(ret) {
      const n = this.state.add(0x20).readS32(), data = this.state.add(0x28).readPointer();
      const meshes = [];
      for (let i = 0; i < n && i < 4096; i++) meshes.push(cmesh(data.add(i * 8).readPointer()));
      if (meshes.some(m => m.match)) send({stage: 'dme', ok: ret.toInt32() & 0xff, meshes: meshes});
    }
  });
  Interceptor.attach(mod.base.add(%(layout)d), {
    onEnter(args) {
      const m = cmesh(args[0]);
      const e = {stage: 'layout', mesh: m, requested: []};
      const n = args[2].toInt32(), t = args[1];
      for (let i = 0; i < n && i < 64; i++)
        e.requested.push(cstr(t.add(i * 0x28).readPointer()));
      if (m.match) e.backtrace = bt(this.context);
      send(e);
    }
  });
  Interceptor.attach(mod.base.add(%(load)d), {
    onEnter(args) { this.out = args[4]; this.n0 = this.out.readS32(); },
    onLeave() {
      const n = this.out.readS32(), data = this.out.add(8).readPointer();
      const meshes = [];
      for (let i = this.n0; i < n; i++) meshes.push(cmesh(data.add(i * 16).readPointer()));
      if (meshes.some(m => m.match)) send({stage: 'load', meshes: meshes});
    }
  });
  Interceptor.attach(mod.base.add(%(aggregate)d), {
    onEnter(args) {
      const v = args[3], n = v.readS32(), data = v.add(8).readPointer();
      const meshes = [];
      for (let i = 0; i < n; i++) {
        const m = data.add(i * 0x238 + 0x10).readPointer();
        if (!m.isNull()) meshes.push(cmesh(m));
      }
      if (meshes.some(m => m.match)) send({stage: 'aggregate', entries: n, meshes: meshes});
    }
  });
  send({hooked: true});
}
hook();
"""


def running(name):
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq " + name], capture_output=True, text=True).stdout
    return name.lower() in out.lower()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("--match", default="concrete_trim_weathered_dirt_blend|fountain")
    p.add_argument("--dump", default="", help="also send every value, as hex, of the matched meshes' streams whose name contains this")
    p.add_argument("--out")
    a = p.parse_args()
    if running("cs2.exe"):
        raise SystemExit("CS2 is running; not starting")
    if running("resourcecompiler.exe"):
        raise SystemExit("another resourcecompiler is running; not starting")
    if shutil.disk_usage(os.path.splitdrive(CS2)[0] + "\\").free < 10 * 1024 ** 3:
        raise SystemExit("under 10 GB free on the game drive; not starting")
    rvas = {"modeldoc": installed("resourcecompiler", 0x1fee20), "dme": installed("resourcecompiler", 0xd47b90),
            "layout": LAYOUT_1103, "load": installed("resourcecompiler", 0x1d30d0),
            "aggregate": installed("resourcecompiler", 0x268e80), "match": json.dumps(a.match),
            "dump": json.dumps(a.dump.lower())}
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "aggstreams_capture", a.map + ".jsonl")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
        os.remove(vpk)
    out = open(out_path, "w")
    lock = threading.Lock()
    hooked = threading.Event()

    def on_message(msg, _data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg)
        elif msg["payload"].get("hooked"):
            hooked.set()
        else:
            with lock:
                out.write(json.dumps(msg["payload"]) + "\n")

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
            "-i", source, "-world", "-fshallow"]
    dev = frida.get_local_device()
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT % rvas)
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    while not done.wait(2):
        if running("cs2.exe"):
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    while running("resourcecompiler.exe"):
        threading.Event().wait(2)
    out.close()
    print(out_path, "(hooked)" if hooked.is_set() else "(NEVER HOOKED)")
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
