"""capture_entitylist_trace.py <addon> <map> [--out dir] [--switches "-world -phys -nav"]: every edit of every world's
entity list during the instance bake, and the root world's list around every collapse, on the installed 1023 build.

Written to <out> (default %TEMP%/navgen_capture/<map>_eltrace/):
  edits.jsonl     in call order, the referrer counts (0923 180f8ba70: the target, its cached counts +0x388 / +0x38c
                  before and after, the result) and one line per OnNodeAdded (0923 180fca010) / OnNodeRemoved (180fcb150) call while the bake runs:
                  kind, world pointer, node pointer, node id (+0x300), the node's list slot (+0x498, 180fef240) and the
                  world's count (+0x1828) on entry and on exit, and whether the world is the root (doc +0x88).
  collapses.jsonl one line per MapDoc_CollapseInstance (0923 180f5ff40) call: the node pointer, id, vtable RVA, and the
                  index of the root list snapshot taken on entry and on exit (snap_<n>.bin: count int32, then
                  pointers).
  nodes.json      for every pointer seen in a snapshot: id, vtable RVA and origin (+0xa0), read at the nav link
                  collector (1023 180f21570), whose list (the last snapshot, bake_leave.json) ends the trace; a map
                  can bake more than once before it (bake_enter.json lists each bake's root).
CS2 must be closed; the map's package is backed up and restored as capture_entitylist.py does.
"""
import argparse
import filecmp
import json
import os
import shutil
import struct
import subprocess
import threading

import frida

from capture_navgen import BIN, CS2, running
from rva_map import installed

# 1023 addresses (tools/re/port_addr.py from 0923).
BAKE, COLLAPSE, ADD, REMOVE, REFS = 0xf61900, 0xf61100, 0xfcb1d0, 0xfcc310, 0xf8cc30
SETTARGET, CLEARTARGET = 0x101a9b0, 0x101a160
LINKS = 0xf21570
# Mapped to the installed build (the 1.41.9.3 build moved them all by 0x40).
BAKE, COLLAPSE, ADD, REMOVE, REFS = (installed("resourcecompiler", a, "20261023") for a in (BAKE, COLLAPSE, ADD, REMOVE, REFS))
SETTARGET, CLEARTARGET, LINKS = (installed("resourcecompiler", a, "20261023") for a in (SETTARGET, CLEARTARGET, LINKS))

AGENT = r"""
'use strict';
const rc = () => Process.findModuleByName('resourcecompiler.dll');
let root = null, inBake = false, snaps = 0;
const seen = new Set();
function hx(p) { return p.toString(); }
function snap() {
  const n = root.add(0x1828).readS32(), arr = root.add(0x1830).readPointer();
  const buf = new ArrayBuffer(4 + n * 8);
  const v = new DataView(buf);
  v.setInt32(0, n, true);
  if (n > 0) {
    const bytes = new Uint8Array(arr.readByteArray(n * 8));
    new Uint8Array(buf, 4).set(bytes);
    for (let i = 0; i < n; i++) seen.add(hx(arr.add(i * 8).readPointer()));
  }
  const id = snaps++;
  send({tag: 'snap', id: id}, buf);
  return id;
}
function info(p) {
  const r = {p: hx(p)};
  try { r.id = p.add(0x300).readU32(); } catch (x) {}
  try { r.vt = p.readPointer().sub(rc().base).toInt32(); } catch (x) {}
  try { r.slot = p.add(0x498).readS32(); } catch (x) {}
  return r;
}
function hook() {
  if (rc() === null) { setTimeout(hook, 5); return; }
  const base = rc().base;
  Interceptor.attach(base.add(BAKE), {
    onEnter(args) { root = args[0].add(0x88).readPointer(); inBake = true; send({tag: 'bake_enter', root: hx(root), snap: snap()}); },
    onLeave() { inBake = false; const s = snap(); send({tag: 'bake_left', snap: s}); if (BAKEONLY) finish(s); } });
  // The nav link collector (0923 180f203b0): the list its search walks, param_2 the world; the trace ends here.
  Interceptor.attach(base.add(LINKS), { onEnter(args) { root = args[1]; finish(snap()); } });
  hook2();
}
function finish(s) {
  const nodes = {};
  for (const k of seen) {
    const p = ptr(k);
    const r = info(p);
    try { r.o = [p.add(0xa0).readFloat(), p.add(0xa4).readFloat(), p.add(0xa8).readFloat()]; } catch (x) {}
    nodes[k] = r;
  }
  send({tag: 'bake_leave', snap: s, nodes: nodes});
}
function hook2() {
  const base = rc().base;
  Interceptor.attach(base.add(COLLAPSE), {
    onEnter(args) { this.node = args[1]; this.r = info(args[1]); this.r.before = inBake ? snap() : null; },
    onLeave(ret) { if (!inBake) return; this.r.after = snap(); this.r.ret = hx(ret); send({tag: 'collapse', r: this.r}); } });
  for (const [addr, kind] of [[ADD, 'add'], [REMOVE, 'remove']]) {
    Interceptor.attach(base.add(addr), {
      onEnter(args) {
        if (!inBake) return;
        this.w = args[0]; this.n = args[1];
        this.e = {kind: kind, w: hx(args[0]), root: args[0].equals(root), n: info(args[1])};
        try { this.e.count0 = args[0].add(0x1828).readS32(); } catch (x) {}
      },
      onLeave() {
        if (!this.e) return;
        try { this.e.count1 = this.w.add(0x1828).readS32(); } catch (x) {}
        try { this.e.slot1 = this.n.add(0x498).readS32(); } catch (x) {}
        send({tag: 'edit', e: this.e});
      } });
  }
  // The target's referrer count (0923 180f8ba70): cached at +0x388 (kind 0) / +0x38c (kind 1), recomputed when negative.
  Interceptor.attach(base.add(REFS), {
    onEnter(args) {
      if (!inBake) return;
      this.t = args[0];
      this.e = {kind: 'refs', n: info(args[0]), which: args[1].toInt32(), c0: [args[0].add(0x388).readS32(), args[0].add(0x38c).readS32()]};
    },
    onLeave(ret) {
      if (!this.e) return;
      this.e.ret = ret.toInt32();
      this.e.c1 = [this.t.add(0x388).readS32(), this.t.add(0x38c).readS32()];
      send({tag: 'edit', e: this.e});
    } });
  // CMapInstance's target setter (0923 1810197f0) and clearer (181018fa0): each resets its targets' cached counts.
  for (const [addr, kind] of [[SETTARGET, 'settarget'], [CLEARTARGET, 'cleartarget']]) {
    Interceptor.attach(base.add(addr), { onEnter(args) {
      if (!inBake) return;
      const e = {kind: kind, n: info(args[0])};
      try { e.old = hx(args[0].add(0x550).readPointer()); } catch (x) {}
      if (kind === 'settarget') e.t = hx(args[1]);
      if (kind === 'settarget' && !args[1].isNull() && (globalThis.bt = (globalThis.bt || 0) + 1) <= 6)
        e.bt = Thread.backtrace(this.context, Backtracer.ACCURATE).map(a => { const m = Process.findModuleByAddress(a); return m ? m.name + '+' + a.sub(m.base).toString(16) : a.toString(); });
      send({tag: 'edit', e: e});
    } });
  }
  send({hooked: true});
}
hook();
"""


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("--out")
    p.add_argument("--switches", default="-world -phys -nav")
    p.add_argument("--bake-only", action="store_true", help="end at the (last) bake's exit, for a map with no nav step")
    a = p.parse_args()
    if running("cs2.exe"):
        raise SystemExit("CS2 is running; not starting")
    if running("resourcecompiler.exe"):
        raise SystemExit("another resourcecompiler is running; not starting")
    out = a.out or os.path.join(os.environ.get("TEMP", "."), "navgen_capture", a.map + "_eltrace")
    os.makedirs(out, exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = os.path.join(out, "package.vpk.bak")
    if os.path.exists(vpk):
        shutil.copy2(vpk, backup)
    edits = open(os.path.join(out, "edits.jsonl"), "w")
    collapses = open(os.path.join(out, "collapses.jsonl"), "w")
    hooked, left = threading.Event(), threading.Event()
    bakes = []

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg)
            return
        m = msg["payload"]
        if m.get("hooked"):
            hooked.set()
        elif m["tag"] == "snap":
            open(os.path.join(out, "snap_%d.bin" % m["id"]), "wb").write(data)
        elif m["tag"] == "edit":
            edits.write(json.dumps(m["e"]) + "\n")
        elif m["tag"] == "collapse":
            collapses.write(json.dumps(m["r"]) + "\n")
        elif m["tag"] == "bake_enter":
            bakes.append(m)
            json.dump(bakes, open(os.path.join(out, "bake_enter.json"), "w"))
        elif m["tag"] == "bake_left":
            print("bake left, root snapshot", m["snap"])
        elif m["tag"] == "bake_leave":
            json.dump(m["nodes"], open(os.path.join(out, "nodes.json"), "w"))
            json.dump({"snap": m["snap"]}, open(os.path.join(out, "bake_leave.json"), "w"))
            left.set()

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
            "-i", source] + a.switches.split()
    dev = frida.get_local_device()
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    script = AGENT
    for name, value in (("BAKE", BAKE), ("COLLAPSE", COLLAPSE), ("ADD", ADD), ("REMOVE", REMOVE), ("REFS", REFS),
                        ("SETTARGET", SETTARGET), ("CLEARTARGET", CLEARTARGET), ("LINKS", LINKS)):
        script = script.replace("base.add(%s)" % name, "base.add(%s)" % hex(value)).replace("[%s, " % name, "[%s, " % hex(value))
    script = script.replace("if (BAKEONLY)", "if (%s)" % ("true" if a.bake_only else "false"))
    sc = ses.create_script(script)
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    while not done.wait(2):
        if running("cs2.exe"):
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
        # The trace ends at the nav link collector: the rest of the compile is not needed.
        if left.is_set():
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    edits.close()
    collapses.close()
    print(out, "(hooked)" if hooked.is_set() else "(NEVER HOOKED)", "(bake left)" if left.is_set() else "(BAKE NEVER LEFT)")
    if os.path.exists(backup):
        shutil.copy2(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
