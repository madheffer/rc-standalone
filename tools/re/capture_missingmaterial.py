"""capture_missingmaterial.py <addon> <map> [--match REGEX] [--out file]: what a material the content lacks
resolves to (OPEN 1.c5, GROUND_TRUTH 42, CS2_CLOSED_QUEUE A.10).

cardtest's teleport02 lives in another addon, yet at collect time Valve gives it attribute flags
0x160100000 and a 256x256 representative texture. WRB_MeshEntryFlags (0923 180252ae0) loads a mesh's
material through 1801d4dc0 (names, count, out vector): each name gets ".vmat" (0x74616d76) and the
resource system's vf 0x70 returns a handle, stored in the out vector; element 0 is what the flags read,
and only a null one warns "Cannot load". This hooks 1801d4dc0 in a -world -fshallow compile and logs, per
call, the names and the handles; for names matching --match also the handle's target, the resource
name strings near it and the first 0x80 bytes of both. CS2 must be closed; the package is backed up and
restored, cmp-checked.
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

AGENT = r"""
'use strict';
const MATCH = new RegExp(%(match)s, 'i');
function cstr(p) { try { return p.isNull() ? "" : p.readCString(); } catch (e) { return null; } }
function hex(p, n) { try { return Array.from(new Uint8Array(p.readByteArray(n))).map(b => b.toString(16).padStart(2, '0')).join(''); } catch (e) { return null; } }
// Printable strings at, or one pointer from, the first 0x200 bytes of p.
function strings(p) {
  const out = [];
  for (let o = 0; o < 0x200; o += 8) {
    try {
      const q = p.add(o).readPointer();
      let s = cstr(q);
      if (s !== null && /^[ -~]{4,}$/.test(s)) out.push(o.toString(16) + ':' + s);
      for (let o2 = 0; o2 < 0x20; o2 += 8) {
        s = cstr(q.add(o2).readPointer());
        if (s !== null && /^[ -~]{4,}$/.test(s) && /[\/.]/.test(s)) out.push(o.toString(16) + '.' + o2.toString(16) + ':' + s);
      }
    } catch (e) {}
  }
  return out.slice(0, 40);
}
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(%(load)d), {
    onEnter(args) {
      this.n = args[1].toInt32();
      this.out = args[2];
      this.names = [];
      for (let i = 0; i < this.n && i < 64; i++) this.names.push(cstr(args[0].add(i * 8).readPointer()));
    },
    onLeave() {
      const n = this.out.readS32(), data = this.out.add(8).readPointer();
      const e = {names: this.names, handles: []};
      let want = this.names.some(s => s !== null && MATCH.test(s));
      for (let i = 0; i < n && i < 64; i++) {
        const h = data.add(i * 8).readPointer();
        const r = {handle: h.toString()};
        if (want && !h.isNull()) {
          r.head = hex(h, 0x80);
          r.strings = strings(h);
          try {
            const t = h.readPointer();
            r.target = t.toString();
            r.targetHead = hex(t, 0x80);
            r.targetStrings = strings(t);
          } catch (err) { r.err = String(err); }
        }
        e.handles.push(r);
      }
      send(e);
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
    p.add_argument("--match", default="teleport|error")
    p.add_argument("--out")
    a = p.parse_args()
    if running("cs2.exe"):
        raise SystemExit("CS2 is running; not starting")
    if running("resourcecompiler.exe"):
        raise SystemExit("another resourcecompiler is running; not starting")
    if shutil.disk_usage(os.path.splitdrive(CS2)[0] + "\\").free < 10 * 1024 ** 3:
        raise SystemExit("under 10 GB free on the game drive; not starting")
    rvas = {"load": installed("resourcecompiler", 0x1d4dc0), "match": json.dumps(a.match)}
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "missingmaterial_capture", a.map + ".jsonl")
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
