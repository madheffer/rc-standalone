"""capture_meshmerge.py <addon> <map> [--out file] [--full]: record CVisibilityMeshMerger::MergeMeshes.

Runs a map compile under Frida and dumps every call of the merger
(resourcecompiler FUN_180234180, visdrivenclustering.cpp) twice. On entry:
the merger's settings, vis's FlatVisClusterVector and MutualVisibilityMatrix
(once per compile), every input mesh entry (its 0x238 bytes, then its CMesh:
vertex floats and indices), and Valve's own WRBMeshEntry_CanMerge
(FUN_1802b63b0) for every ordered pair of inputs. On return: each bucket the
call appended to the cluster output, and each mesh it appended to the
unclustered output. VisibilityMeshMergerReplay feeds the same input through
the port and compares.

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
MERGE_RVA = 0x234180
CANMERGE_RVA = 0x2b63b0

AGENT = r"""
'use strict';
let calls = 0;
let flatSent = null;

// CMesh: +0 vertex floats, +8 streams (0x28 each), +0x10 indices,
// +0x18 vertex count, +0x1c floats per vertex, +0x20 stream count,
// +0x24 index count. A stream: +0 name, +8 semantic, +0x14 first float,
// +0x18 float count.
function mesh(entry, ev, extra) {
  const m = entry.readPointer();
  const nv = m.add(0x18).readS32(), stride = m.add(0x1c).readS32();
  const ns = m.add(0x20).readS32(), ni = m.add(0x24).readS32();
  const streams = [];
  const sp = m.add(8).readPointer();
  for (let i = 0; i < ns; i++) {
    const s = sp.add(i * 0x28);
    const np = s.readPointer(), sem = s.add(8).readPointer();
    streams.push({name: np.isNull() ? '' : np.readUtf8String(),
                  semantic: sem.isNull() ? '' : sem.readUtf8String(),
                  first: s.add(0x14).readS32(), count: s.add(0x18).readS32()});
  }
  const raw = entry.readByteArray(0x238);
  const vb = nv > 0 ? m.readPointer().readByteArray(nv * stride * 4) : new ArrayBuffer(0);
  const ib = ni > 0 ? m.add(0x10).readPointer().readByteArray(ni * 4) : new ArrayBuffer(0);
  const out = new Uint8Array(0x238 + vb.byteLength + ib.byteLength);
  out.set(new Uint8Array(raw), 0);
  out.set(new Uint8Array(vb), 0x238);
  out.set(new Uint8Array(ib), 0x238 + vb.byteLength);
  // +0x218: the entry's precomputed vis cluster set (CUtlVector<uint16>).
  const nk = entry.add(0x218).readS32();
  const key = [];
  for (let k = 0; k < nk; k++)
    key.push(entry.add(0x220).readPointer().add(k * 2).readU16());
  const head = {ev: ev, nv: nv, stride: stride, ni: ni, streams: streams, mesh: m.toString(),
                key: key, flags: entry.add(0xbc).readU32()};
  for (const k in extra) head[k] = extra[k];
  send(head, out.buffer);
}

function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  const canMerge = new NativeFunction(mod.base.add(%(canmerge)d), 'uint8', ['pointer', 'pointer']);
  send({ev: 'hooked', base: mod.base.toString()});
  Interceptor.attach(mod.base.add(%(merge)d), {
    onEnter(args) {
      const merger = args[0], input = args[1];
      this.call = calls++;
      this.out = args[2];
      this.nov = args[3];
      this.out0 = this.out.readS32();
      this.nov0 = this.nov.readS32();
      const flat = merger.add(8).readPointer(), mutual = merger.add(0x10).readPointer();
      const key = flat.toString() + '/' + mutual.toString();
      if (flatSent !== key) {
        flatSent = key;
        const nc = flat.readS32(), fp = flat.add(8).readPointer();
        const counts = [];
        const parts = [];
        for (let c = 0; c < nc; c++) {
          const n = fp.add(c * 0x18).readS32();
          counts.push(n);
          if (n > 0)
            parts.push(new Uint8Array(fp.add(c * 0x18 + 8).readPointer().readByteArray(n * 24)));
        }
        const nr = mutual.readS32(), rp = mutual.add(8).readPointer();
        const rows = [];
        for (let r = 0; r < nr; r++) {
          const n = rp.add(r * 0x18).readS32();
          rows.push(n);
          if (n > 0)
            parts.push(new Uint8Array(rp.add(r * 0x18 + 8).readPointer().readByteArray(n * 4)));
        }
        let size = 0;
        for (const p of parts) size += p.byteLength;
        const blob = new Uint8Array(size);
        let at = 0;
        for (const p of parts) { blob.set(p, at); at += p.byteLength; }
        send({ev: 'vis', call: this.call, boxes: counts, rows: rows}, blob.buffer);
      }
      const n = input.readS32(), ep = input.add(8).readPointer();
      send({ev: 'call', call: this.call, n: n,
            settings: [merger.add(0x18).readS32(), merger.add(0x1c).readS32(),
                       merger.add(0x20).readS32(), merger.add(0x24).readS32()],
            debug: merger.readPointer().toString(), out0: this.out0, nov0: this.nov0});
      for (let i = 0; i < n; i++)
        mesh(ep.add(i * 0x238), 'in', {call: this.call, i: i});
      const pairs = new Uint8Array(n * n);
      for (let i = 0; i < n; i++)
        for (let j = 0; j < n; j++)
          pairs[i * n + j] = canMerge(ep.add(i * 0x238), ep.add(j * 0x238));
      send({ev: 'canmerge', call: this.call, n: n}, pairs.buffer);
    },
    onLeave(ret) {
      const on = this.out.readS32(), op = this.out.add(8).readPointer();
      for (let k = this.out0; k < on; k++) {
        const v = op.add(k * 0x18), cnt = v.readS32(), vp = v.add(8).readPointer();
        send({ev: 'bucket', call: this.call, k: k, count: cnt});
        for (let j = 0; j < cnt; j++)
          mesh(vp.add(j * 0x238), 'out', {call: this.call, k: k, j: j});
      }
      const nn = this.nov.readS32(), np = this.nov.add(8).readPointer();
      for (let j = this.nov0; j < nn; j++)
        mesh(np.add(j * 0x238), 'nov', {call: this.call, j: j});
      send({ev: 'done', call: this.call});
    }
  });
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

    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "meshmerge_capture", a.map + ".bin")
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
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT % {"merge": MERGE_RVA, "canmerge": CANMERGE_RVA})
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
    out.close()
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
