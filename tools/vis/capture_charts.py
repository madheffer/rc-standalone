"""capture_charts.py <addon> <map> [--out file]: the lightmap chart meshes through Valve's charting.

Runs a full compile (-f, so the map bakes and charts are made) under Frida
and snapshots each chart mesh around StaticLighting_ChartMesh (0923
18022d920), as StaticLighting_PrepareChartMesh (180210730) calls it:

  call    at the call (180210f2b): the chart mesh pointer, the two floats
          the caller passes in xmm1 / xmm2 ([rbp+0x140], [rbp+0x168]), the
          constant in xmm3, the per-triangle scale vector ([rsp+0x20]) and
          the flag byte ([rsp+0x28])
  enter   the 0x88-byte chart mesh and its arrays going in
  leave   the same coming out

A chart mesh (docs/BAKED.md "Lightmap charts"): +0x00 corner count and +0x08
a per-corner array, +0x18 vertices (+0x24 count, +0x20 stride), +0x28 each
vertex's chart uv (float2), +0x30 indices (+0x38 count), +0x40 each triangle's
chart, +0x48 the chart count, +0x58..+0x64 the
position / texcoord / normal / tangent offsets, +0x70 / +0x78 a list.
Every array is hex; one JSON line per snapshot.

CS2 must be closed and 10 GB free; the map's package is backed up first and
restored, cmp-checked, afterwards. The capture's own _vrad3 folder is kept
beside the output.
"""
import argparse
import filecmp
import json
import os
import shutil
import struct
import subprocess
import sys
import threading

import frida
import pefile

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "re"))
from rva_map import installed  # noqa: E402

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

PREP, CORE = 0x210730, 0x22d920
CALL_AT = 0x210f2b - PREP      # the call into the core, inside the prep
XMM3_AT = 0x210f01 - PREP      # movss xmm3, [rip + disp] just before it

AGENT = r"""
'use strict';
function hex(p, n) {
  if (p.isNull() || n <= 0) return '';
  const b = new Uint8Array(p.readByteArray(n));
  let s = '';
  for (let i = 0; i < b.length; i++) s += b[i].toString(16).padStart(2, '0');
  return s;
}
function mesh(m) {
  const i32 = o => m.add(o).readS32();
  const ptr = o => m.add(o).readPointer();
  const corners = i32(0x00), stride = i32(0x20), verts = i32(0x24), indices = i32(0x38), list = i32(0x70);
  return {
    head: hex(m, 0x88),
    corners: hex(ptr(0x08), corners * 4),
    vertices: hex(ptr(0x18), verts * stride),
    indices: hex(ptr(0x30), indices * 4),
    triangle_charts: hex(ptr(0x40), (indices / 3 | 0) * 4),
    chart_uvs: hex(ptr(0x28), verts * 8),
    list: hex(ptr(0x78), list * 4),
  };
}
let calls = 0;
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(%(call)d), function () {
    const c = this.context;
    const scales = c.rsp.add(0x20).readPointer();
    const n = scales.readS32();
    send({stage: 'call', call: calls, mesh: c.rcx.toString(),
          xmm1: c.rbp.add(0x140).readU32(), xmm2: c.rbp.add(0x168).readU32(), xmm3: mod.base.add(%(xmm3)d).readU32(),
          flag: c.rsp.add(0x28).readU8(), scales: n > 0 ? hex(scales.add(8).readPointer(), n * 4) : ''});
  });
  Interceptor.attach(mod.base.add(%(core)d), {
    onEnter(args) { this.m = args[0]; this.call = calls; send(Object.assign({stage: 'enter', call: calls, mesh: this.m.toString()}, mesh(this.m))); },
    onLeave() { send(Object.assign({stage: 'leave', call: this.call, mesh: this.m.toString()}, mesh(this.m))); calls++; }
  });
  send({hooked: true});
}
hook();
"""


def running(name):
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq " + name], capture_output=True, text=True).stdout
    return name.lower() in out.lower()


def xmm3_constant(prep):
    """The RVA the movss before the call loads xmm3 from, read off the installed DLL."""
    pe = pefile.PE(os.path.join(BIN, "resourcecompiler.dll"), fast_load=True)
    at = prep + XMM3_AT
    code = pe.get_data(at, 8)
    if code[:4] != b"\xf3\x0f\x10\x1d":
        raise SystemExit(f"no movss xmm3, [rip + disp] at 0x{at:x}: {code.hex()}")
    return at + 8 + struct.unpack_from("<i", code, 4)[0]


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("--out")
    a = p.parse_args()
    if running("cs2.exe"):
        raise SystemExit("CS2 is running; not starting")
    if running("resourcecompiler.exe"):
        raise SystemExit("another resourcecompiler is running; not starting")
    if shutil.disk_usage(os.path.splitdrive(CS2)[0] + "\\").free < 10 * 1024 ** 3:
        raise SystemExit("under 10 GB free on the game drive; not starting")
    prep = installed("resourcecompiler", PREP)
    rvas = {"call": prep + CALL_AT, "core": installed("resourcecompiler", CORE), "xmm3": xmm3_constant(prep)}
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "chart_capture", a.map + ".jsonl")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copy2(vpk, backup)
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
            "-i", source, "-f"]
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
    vrad3 = os.path.join(CS2, "game", "csgo_addons", a.addon, "_vrad3")
    if os.path.isdir(vrad3):
        shutil.rmtree(out_path + ".vrad3", ignore_errors=True)
        shutil.copytree(vrad3, out_path + ".vrad3")
    if os.path.exists(backup):
        shutil.copy2(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
