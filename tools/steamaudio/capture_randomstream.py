"""capture_randomstream.py <addon> <map> <out_dir> [--switches "..."]: tier0's random streams through a compile,
and their state where Steam Audio's nav coverage appender starts.

Hooks tier0.dll (20261023) CUniformRandomStreamImpl::SetSeed (all three mutex kinds share 0x15e780) and
GenerateRandomNumber_Locked (0x15e1b0): every seed and every raw draw, with the stream's address, the thread and
the first return address outside tier0 (module+rva). At resourcecompiler.dll (20261029) 180fbdd10 (the appender,
0923 180fbcb10) and 1818e5f60 (Polygon_RandomPoint, 0923 1818e3290) it marks entry and exit and dumps 0x100 bytes
of every stream seen so far. Writes <out_dir>/stream.jsonl and rc.log. A compile overwrites the map's .vpk: it is
backed up and restored (cmp). --pin SEED reseeds the global stream (tier0 +0x3cc328) at the appender's entry as
RandomSeed(SEED) would: every seed tier0 itself gives it is time-based (static init 1800057e0, 18017bd20,
18017c72e), so Valve's appender is not reproducible run to run without it. CS2 must be closed; run it through tools/re/heavy.py.
"""
import argparse
import filecmp
import json
import os
import shutil
import subprocess
import threading

import frida

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
'use strict';
const streams = {}, PIN = PIN_VALUE;
let inAppender = 0, t0 = null, rc = null;
function caller(ctx) {
  for (const a of Thread.backtrace(ctx, Backtracer.FUZZY).slice(0, 8)) {
    const m = Process.findModuleByAddress(a);
    if (m !== null && m.name !== 'tier0.dll') return m.name + '+0x' + a.sub(m.base).toString(16);
  }
  return '?';
}
function dumpAll(tag) {
  for (const k of Object.keys(streams)) send({op: 'state', tag: tag, stream: k}, ptr(k).readByteArray(0x100));
}
function hookTier0() {
  t0 = Process.findModuleByName('tier0.dll');
  if (t0 === null) { setTimeout(hookTier0, 1); return; }
  Interceptor.attach(t0.base.add(0x15e780), { onEnter(args) {
    streams[args[0].toString()] = 1;
    send({op: 'seed', stream: args[0].toString(), seed: args[1].toInt32(), tid: this.threadId, from: caller(this.context), app: inAppender});
  } });
  Interceptor.attach(t0.base.add(0x15e1b0), {
    onEnter(args) { this.s = args[0].toString(); streams[this.s] = 1; this.from = caller(this.context); },
    onLeave(ret) { send({op: 'draw', stream: this.s, v: ret.toInt32(), tid: this.threadId, from: this.from, app: inAppender}); } });
}
function hookRc() {
  rc = Process.findModuleByName('resourcecompiler.dll');
  if (rc === null) { setTimeout(hookRc, 5); return; }
  Interceptor.attach(rc.base.add(0xfbdd10), {
    onEnter(args) {
      inAppender++; this.probes = args[4]; send({op: 'enter', fn: 'appender', tid: this.threadId}); dumpAll('appender_enter');
      // arg 3: the generation box; arg 5: the probes so far (int count, Vec3 * at +8); arg 4: spacing, height
      send({op: 'box'}, args[2].readByteArray(0x40));
      send({op: 'spacing'}, args[3].readByteArray(8));
      const na = args[1].add(8).readS32(), areas = args[1].add(0x10).readPointer(), cs = new ArrayBuffer(na * 12), cv = new DataView(cs);
      for (let i = 0; i < na; i++) { const ar = areas.add(8 * i).readPointer();  // the area's centre (+0xc)
        for (let k = 0; k < 3; k++) cv.setFloat32(12 * i + 4 * k, ar.add(0xc + 4 * k).readFloat(), true); }
      send({op: 'areas', n: na}, cs);
      const n = args[4].readS32();
      send({op: 'probes', n: n}, n > 0 ? args[4].add(8).readPointer().readByteArray(n * 12) : new ArrayBuffer(0));
      if (PIN !== null) {  // RandomSeed(PIN) on the global stream: idum = -|seed|, iy = 0 (tier0 18015d1c3)
        const g = t0.base.add(0x3cc328);
        g.writeS32(-Math.abs(PIN)); g.add(4).writeS32(0); dumpAll('appender_pinned');
      } },
    onLeave(ret) {
      const n = this.probes.readS32();  // the probes after the appender, its appends at the end
      send({op: 'probes_out', n: n}, n > 0 ? this.probes.add(8).readPointer().readByteArray(n * 12) : new ArrayBuffer(0));
      dumpAll('appender_leave'); send({op: 'leave', fn: 'appender'}); inAppender--; } });
  Interceptor.attach(rc.base.add(0x18e5f60), { onEnter(args) { send({op: 'enter', fn: 'randompoint', tid: this.threadId}); } });
  send({hooked: true});
}
hookTier0();
hookRc();
"""


def running(name):
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq " + name], capture_output=True, text=True).stdout
    return name.lower() in out.lower()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("out_dir")
    p.add_argument("--pin", type=int)
    p.add_argument("--switches", default="-world -fshallow -phys -nav -sareverb -sareverb_threads 1")
    a = p.parse_args()
    if running("cs2.exe") or running("resourcecompiler.exe"):
        raise SystemExit("CS2 or another resourcecompiler is running; not starting")
    os.makedirs(a.out_dir, exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = os.path.join(a.out_dir, "package.vpk.bak")
    if os.path.exists(vpk):
        shutil.copy2(vpk, backup)
    out = open(os.path.join(a.out_dir, "stream.jsonl"), "w", encoding="utf-8")
    log = open(os.path.join(a.out_dir, "rc.log"), "w", encoding="utf-8")
    lock = threading.Lock()
    hooked = threading.Event()

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg, flush=True)
            return
        pay = msg["payload"]
        if pay.get("hooked"):
            hooked.set()
            return
        if data is not None:
            pay["hex"] = data.hex()
        with lock:
            out.write(json.dumps(pay) + "\n")
        if pay["op"] in ("enter", "leave", "seed") and pay.get("fn") != "randompoint":
            print(pay, flush=True)

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
            "-i", source] + a.switches.split()
    dev = frida.get_local_device()
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT.replace("PIN_VALUE", "null" if a.pin is None else str(a.pin)))
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.on("output", lambda _pid, _fd, d: (log.write(d.decode("utf-8", "replace")) if d else None))
    dev.resume(pid)
    while not done.wait(2):
        if running("cs2.exe"):
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    out.close()
    log.close()
    print(a.out_dir, "(hooked)" if hooked.is_set() else "(NEVER HOOKED)")
    if os.path.exists(vpk):  # the compiled package, before the backup goes back over it
        shutil.copy2(vpk, os.path.join(a.out_dir, a.map + ".vpk"))
    if os.path.exists(backup):
        shutil.copy2(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
