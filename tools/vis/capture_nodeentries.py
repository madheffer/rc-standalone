"""capture_nodeentries.py <addon> <map> [--out file] [--full]: record a node's mesh entries through BuildNode.

Runs a map compile under Frida and dumps every mesh entry of the node
(CWorldRendererBuilderNode +0x148) before and after BuildNode
(resourcecompiler 18024b400) and each of its steps: FUN_180256690,
FUN_180257b50, Step_RemovingTrianglesInside, FixTJunctionEdgeCracks,
FUN_18025ece0 and FUN_18025d500. Each entry is its 0x238 bytes, then its
CMesh's vertex floats and indices. NodeEntriesFromVmap compares ours with
them stage by stage.

Output is a stream of records: u32 json length, json, u32 blob length, blob.

CS2 must be closed (a watchdog kills the compile if it starts), 10 GB must
be free, and the map's package is backed up first and restored, cmp-checked,
afterwards; the compile's own package is kept as <out>.vpk.
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
import time

import frida

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
# resourcecompiler.dll 2026-09-23 (unchanged through the 09-25 patch).
STEPS = [(0x24b430, "BuildNode", 1), (0x2566c0, "Step256690", 0), (0x257b80, "Step257b50", 0),
         (0x259b30, "RemoveInside", 0), (0x259d60, "FixTJunctions", 0), (0x25ed10, "Step25ece0", 0),
         (0x25d530, "Step25d500", 0)]

AGENT = r"""
'use strict';
// A node's mesh entries: CUtlVector at node +0x148 (count), +0x150 (data), 0x238 each.
function dumpEntries(node, stage, call) {
  const n = node.add(0x148).readS32(), ep = node.add(0x150).readPointer();
  send({ev: 'stage', stage: stage, call: call, n: n});
  for (let i = 0; i < n; i++) {
    const e = ep.add(i * 0x238), m = e.readPointer();
    const nv = m.add(0x18).readS32(), stride = m.add(0x1c).readS32();
    const ns = m.add(0x20).readS32(), ni = m.add(0x24).readS32();
    const streams = [];
    const sp = m.add(8).readPointer();
    for (let k = 0; k < ns; k++) {
      const s = sp.add(k * 0x28);
      const np = s.readPointer();
      streams.push({name: np.isNull() ? '' : np.readUtf8String(), index: s.add(0x10).readS32(),
                    first: s.add(0x14).readS32(), count: s.add(0x18).readS32(), type: s.add(0x20).readS32(),
                    b1c: s.add(0x1c).readU8(), b1d: s.add(0x1d).readU8(), b1e: s.add(0x1e).readU8(), b1f: s.add(0x1f).readU8()});
    }
    const mlen = m.add(0x60).readU32() & 0x3fffffff, mflags = m.add(0x64).readU32();
    const material = mlen === 0 ? '' : ((mflags >> 30) & 1)
        ? m.add(0x68).readUtf8String(mlen) : m.add(0x68).readPointer().readUtf8String(mlen);
    const raw = e.readByteArray(0x238);
    const vb = nv > 0 ? m.readPointer().readByteArray(nv * stride * 4) : new ArrayBuffer(0);
    const ib = ni > 0 ? m.add(0x10).readPointer().readByteArray(ni * 4) : new ArrayBuffer(0);
    const out = new Uint8Array(0x238 + vb.byteLength + ib.byteLength);
    out.set(new Uint8Array(raw), 0);
    out.set(new Uint8Array(vb), 0x238);
    out.set(new Uint8Array(ib), 0x238 + vb.byteLength);
    send({ev: 'entry', stage: stage, call: call, i: i, nv: nv, stride: stride, ni: ni, streams: streams,
          material: material, mesh: m.toString()}, out.buffer);
  }
}
// (rva, name, index of the node argument)
const STEPS = %(steps)s;
let calls = 0;
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  send({ev: 'hooked'});
  for (const [rva, name, arg] of STEPS) {
    try {
    Interceptor.attach(mod.base.add(rva), {
      onEnter(args) {
        this.node = args[arg];
        this.call = name === 'BuildNode' ? calls++ : calls - 1;
        dumpEntries(this.node, name + ':in', this.call);
      },
      onLeave(ret) {
        dumpEntries(this.node, name + ':out', this.call);
      }
    });
    } catch (e) {
      send({ev: 'hookfailed', name: name, error: e.toString()});
    }
  }
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
    p.add_argument("--out")
    p.add_argument("--full", action="store_true", help="a full compile rather than -world -fshallow")
    a = p.parse_args()
    if running("cs2.exe"):
        raise SystemExit("CS2 is running; not starting")
    if running("resourcecompiler.exe"):
        raise SystemExit("another resourcecompiler is running; not starting")
    if shutil.disk_usage(os.path.splitdrive(CS2)[0] + "\\").free < 10 * 1024 ** 3:
        raise SystemExit("under 10 GB free on the game drive; not starting")

    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "nodeentries_capture", a.map + ".bin")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
        os.remove(vpk)  # a current package makes RC skip the compile

    out = open(out_path, "wb")
    lock = threading.Lock()
    stats = {}

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg, file=sys.stderr)
            return
        pay = msg["payload"]
        stats[pay["ev"]] = stats.get(pay["ev"], 0) + 1
        head = json.dumps(pay).encode()
        blob = data or b""
        with lock:
            out.write(struct.pack("<I", len(head)) + head + struct.pack("<I", len(blob)) + blob)

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4",
            "-game", os.path.join(CS2, "game", "csgo"), "-i", source]
    if not a.full:
        argv += ["-world", "-fshallow"]
    dev = frida.get_local_device()
    rc_log = open(out_path + ".rc.log", "wb")
    dev.on("output", lambda _pid, _fd, data: rc_log.write(data or b""))
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT % {"steps": json.dumps(STEPS)})
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    killed = False
    while not done.wait(2):
        if running("cs2.exe"):
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
            killed = True
    # Restore only once no compile can still write the package.
    while running("resourcecompiler.exe"):
        threading.Event().wait(2)
    out.close()
    rc_log.close()
    print(out_path, stats, "(killed: cs2 started)" if killed else "")

    # The compile's own package goes beside the capture: its node models are
    # what the captured buckets become.
    if os.path.exists(vpk):
        shutil.copyfile(vpk, out_path + ".vpk")
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
