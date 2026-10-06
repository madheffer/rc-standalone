"""capture_rayscene.py <addon> <map> [--out file]: the editor ray scene the light precompute traces.

Hooks (09-23 analysis addresses, found in the installed resourcecompiler by
tools/re/rva_map.py): RayScene_AddTriangles 181c14020 (scene, id, a, b, c,
?, flags), RayScene_AddInstance 181c13ac0 (parent, child, 3x4, owner flags;
returns the instance) and RayScene_SetInstanceTransform 181c19970 (scene,
instance, 3x4), the scene constructor 1801f6f40 and its triangle clear
181c14cc0 (addresses are reused, so these start a new generation of one). Every call
is one 96-byte record: u32 kind (1 instance, 2 triangle, 3 transform, 4
made, 5 cleared), u32 0, four u64 (kind 1: parent, child, instance, owner
flags; 2: scene, id, flags, 0; 3: scene, instance, 0, 0; 4 and 5: scene),
then 14 floats (1 and 3: the 3x4; 2: a, b, c).
EditorSceneCapture reads them back and diffs them against EditorTraceScene.

CS2 must be closed; the map's package is backed up and restored.
"""
import argparse
import filecmp
import json
import os
import shutil
import subprocess
import threading

import sys

import frida

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "re"))
from rva_map import installed  # noqa: E402

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
AGENT = r"""
'use strict';
const RECORD = 96, BATCH = 8192;
let buf = new ArrayBuffer(RECORD * BATCH), view = new DataView(buf), n = 0;
function flush() {
  if (n === 0) return;
  send({count: n}, buf.slice(0, n * RECORD));
  n = 0;
}
function put(kind, a, b, c, d, floats) {
  const at = n * RECORD;
  view.setUint32(at, kind, true);
  view.setUint32(at + 4, 0, true);
  view.setBigUint64(at + 8, BigInt(a.toString()), true);
  view.setBigUint64(at + 16, BigInt(b.toString()), true);
  view.setBigUint64(at + 24, BigInt(c.toString()), true);
  view.setBigUint64(at + 32, BigInt(d.toString()), true);
  for (let i = 0; i < 14; i++)
    view.setFloat32(at + 40 + i * 4, i < floats.length ? floats[i] : 0, true);
  if (++n === BATCH) flush();
}
function matrix(p) {
  const m = [];
  for (let i = 0; i < 12; i++) m.push(p.add(i * 4).readFloat());
  return m;
}
function vec(p) { return [p.readFloat(), p.add(4).readFloat(), p.add(8).readFloat()]; }
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(ADD), {
    onEnter(args) {
      put(2, args[0], args[1].and(0xffffffff), args[6].and(0xffff), ptr(0),
          vec(args[2]).concat(vec(args[3])).concat(vec(args[4])));
    }
  });
  const parents = {};
  function where(ctx) {
    return Thread.backtrace(ctx, Backtracer.ACCURATE).slice(0, 8).map(a => {
      const m = Process.findModuleByAddress(a);
      return m ? m.name.replace('.dll', '') + '+0x' + a.sub(m.base).toString(16) : a.toString();
    });
  }
  Interceptor.attach(mod.base.add(INST), {
    onEnter(args) {
      this.a = [args[0], args[1], args[3], matrix(args[2])];
      const key = args[0].toString();
      if (!(key in parents)) { parents[key] = 1; send({parent: key, trace: where(this.context)}); }
    },
    onLeave(ret) { put(1, this.a[0], this.a[1], ret, this.a[2], this.a[3]); }
  });
  Interceptor.attach(mod.base.add(XFORM), {
    onEnter(args) { put(3, args[0], args[1], ptr(0), ptr(0), matrix(args[2])); }
  });
  // A scene made (the constructor) or emptied of triangles: a new generation of that address.
  Interceptor.attach(mod.base.add(MADE), { onEnter(args) {
    put(4, args[0], ptr(0), ptr(0), ptr(0), []);
    send({made: args[0].toString(), trace: where(this.context)});
  } });
  Interceptor.attach(mod.base.add(CLEAR), { onEnter(args) { put(5, args[0], ptr(0), ptr(0), ptr(0), []); } });
  send({hooked: mod.base.toString()});
}
rpc.exports = { flush: flush };
hook();
"""


def running(name):
    return name.lower() in subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("--out")
    a = p.parse_args()
    if running("cs2.exe") or running("resourcecompiler.exe"):
        raise SystemExit("CS2 or a compile is running; not starting")
    if shutil.disk_usage(os.path.splitdrive(CS2)[0] + "\\").free < 10 * 1024 ** 3:
        raise SystemExit("under 10 GB free on the game drive; not starting")
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "rayscene_capture", a.map + ".bin")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
        os.remove(vpk)
    out = open(out_path, "wb")
    lock = threading.Lock()
    count = [0]
    # Who made each scene and who first parented an instance to it, as
    # module+rva backtraces (<out>.traces.json).
    traces = []

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg)
            return
        if data is None:
            pay = msg["payload"]
            if "trace" in pay:
                traces.append(pay)
            else:
                print("agent:", pay)
            return
        with lock:
            out.write(data)
            count[0] += msg["payload"]["count"]

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    dev = frida.get_local_device()
    pid = dev.spawn([os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
                     "-i", source, "-world", "-phys", "-fshallow"], cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    hooks = {"ADD": 0x1c14020, "INST": 0x1c13ac0, "XFORM": 0x1c19970, "MADE": 0x1f6f40, "CLEAR": 0x1c14cc0}
    head = "".join(f"const {k} = {installed('resourcecompiler', v)};\n" for k, v in hooks.items())
    sc = ses.create_script(AGENT.replace("'use strict';\n", "'use strict';\n" + head, 1))
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    # The process exits on its own; flush what the agent holds whenever it pauses.
    while not done.wait(2):
        try:
            sc.exports_sync.flush()
        except Exception:
            pass
        if running("cs2.exe"):
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    while running("resourcecompiler.exe"):
        threading.Event().wait(2)
    out.close()
    print(out_path, count[0], "records")
    with open(out_path + ".traces.json", "w", encoding="utf-8") as f:
        json.dump(traces, f)
    print(out_path + ".traces.json", len(traces), "traces")
    if os.path.exists(vpk):
        shutil.copyfile(vpk, out_path + ".vpk")
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored" if ok else "RESTORE CMP FAILED", vpk)
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
