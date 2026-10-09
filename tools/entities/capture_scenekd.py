"""capture_scenekd.py <addon> <map> --bounds x,y,z,X,Y,Z[;...] [--out file]: Valve's kd trees of ray scenes.

Hooks RayScene_SetupAccelerationStructure (rc 0923 181c19be0, moved to the
installed build by tools/re/rva_map.py). When it returns, a scene whose box
(+0x4 mins, +0x10 maxs) equals one of the given boxes has its tree walked from
the root and written as JSON lines (--bounds survey: every scene's box,
flag word, no-refine byte and node count instead, with every
RayScene_AddInstance 181c13ac0 and SetInstanceTransform 181c19970 call): the scene's flag word (+0x0), the byte at
+0x1c4 that stops refining, the triangle reference shift and mask (+0x80,
+0x84), then one line per node in depth-first order (lower child first):
"axis split" for a split, "leaf r1 r2 ..." for a leaf (raw references, as
RayScene_TraceMode2 181c0a000 reads them: nodes at +0x68, 8 bytes each, a
word whose low 2 bits are the axis (3 a leaf) and whose rest is the child
pair or the reference offset, then the split or the count; references at
+0xa8), and every triangle's 0x30-byte record (blocks at +0x90, count
+0x7c). meshsystem.dll's copy of the setup (models' own scenes) is hooked
too; each line names its module. Used to check TracerKd's build for the
light scene (ledger 31).

CS2 must be closed; the map's package is backed up and restored.
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

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "re"))
from rva_map import installed  # noqa: E402

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
'use strict';
function setupHook(tag) { return {
    onEnter(args) { this.scene = args[0]; },
    onLeave(ret) {
      const s = this.scene;
      const box = [];
      for (let i = 0; i < 6; i++) box.push(s.add(4 + i * 4).readFloat());
      if (BOXES.length === 0) {  // survey: every scene's address, box, flag word and node count
        send({survey: true, module: tag, scene: s.toString(), box: box, flags: s.readU32(), norefine: s.add(0x1c4).readU8(),
              nodes: s.add(0x60).readU32()});
        return;
      }
      let which = -1;
      for (let k = 0; k < BOXES.length && which < 0; k++) {
        let same = true;
        for (let i = 0; i < 6; i++) same = same && Math.abs(BOXES[k][i] - box[i]) < 0.01;
        if (same) which = k;
      }
      if (which < 0) return;
      const nodes = s.add(0x68).readPointer(), refs = s.add(0xa8).readPointer();
      const lines = [];
      function walk(at) {
        const n = nodes.add(at * 8);
        const word = n.readU32();
        if ((word & 3) === 3) {
          const count = n.add(4).readU32(), off = word >>> 2;
          const r = [];
          for (let i = 0; i < count; i++) r.push(refs.add((off + i) * 4).readU32());
          lines.push('leaf ' + r.join(' '));
          return;
        }
        lines.push((word & 3) + ' ' + n.add(4).readFloat());
        walk((word >>> 2));
        walk((word >>> 2) + 1);
      }
      if (s.add(0x60).readU32() !== 0) walk(0);
      // Triangle records (0x30 bytes, CacheOptimizedTriangle): +0x7c the count,
      // +0x90 the blocks (0x10 each, +8 the block's records), id >> shift, id & mask.
      const count = s.add(0x7c).readS32(), shift = s.add(0x80).readU32(), tmask = s.add(0x84).readU32();
      const blocks = s.add(0x90).readPointer();
      const records = [];
      for (let id = 0; id < count; id++) {
        const rec = blocks.add((id >>> shift) * 0x10 + 8).readPointer().add((id & tmask) * 0x30);
        records.push(Array.from(new Uint8Array(rec.readByteArray(0x30))).map(b => ('0' + b.toString(16)).slice(-2)).join(''));
      }
      send({which: which, module: tag, scene: s.toString(), box: box, flags: s.readU32(), norefine: s.add(0x1c4).readU8(),
            shift: shift, mask: tmask, nodes: lines, records: records});
    }
  }; }
// meshsystem.dll keeps its own copy of the ray scene code (models' scenes are built there).
function hookMeshsystem() {
  const ms = Process.findModuleByName('meshsystem.dll');
  if (ms === null) { setTimeout(hookMeshsystem, 5); return; }
  Interceptor.attach(ms.base.add(MS_SETUP), setupHook('meshsystem'));
  send({hooked: 'meshsystem'});
}
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(SETUP), setupHook('rc'));
  if (BOXES.length === 0) {
    // RayScene_AddInstance (parent, child, 3x4, owner flags) and SetInstanceTransform (scene, instance, 3x4).
    function m34(p) { const m = []; for (let i = 0; i < 12; i++) m.push(p.add(i * 4).readFloat()); return m; }
    Interceptor.attach(mod.base.add(ADDINST), {
      onEnter(args) { this.a = [args[0].toString(), args[1].toString(), m34(args[2]), args[3].toUInt32()]; },
      onLeave(ret) { send({inst: this.a, handle: ret.toString()}); }
    });
    Interceptor.attach(mod.base.add(SETXF), {
      onEnter(args) { send({xf: [args[0].toString(), args[1].toString(), m34(args[2])]}); }
    });
  }
  send({hooked: true});
}
hook();
hookMeshsystem();
"""


def meshsystem_copy(rc_rva, length):
    """meshsystem.dll's copy of a resourcecompiler function: its first bytes found once there."""
    import pefile

    def image(name):
        return pefile.PE(os.path.join(BIN, name)).get_memory_mapped_image()
    pattern = image("resourcecompiler.dll")[rc_rva:rc_rva + length]
    img = image("meshsystem.dll")
    first = img.find(pattern)
    if first < 0 or img.find(pattern, first + 1) >= 0:
        raise SystemExit(f"meshsystem.dll: no single copy of rc {rc_rva:#x}")
    return first


def running(name):
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq " + name], capture_output=True, text=True).stdout
    return name.lower() in out.lower()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("--bounds", required=True, help="boxes separated by ';', or 'survey' for every scene's flag word")
    p.add_argument("--out")
    a = p.parse_args()
    if running("cs2.exe") or running("resourcecompiler.exe"):
        raise SystemExit("CS2 or a compile is running; not starting")
    if shutil.disk_usage(os.path.splitdrive(CS2)[0] + "\\").free < 10 * 1024 ** 3:
        raise SystemExit("under 10 GB free on the game drive; not starting")
    boxes = [] if a.bounds == "survey" else [[float(v) for v in b.split(",")] for b in a.bounds.split(";")]
    head = (f"const SETUP = {installed('resourcecompiler', 0x1c19be0)};\nconst BOXES = {boxes};\n"
            f"const ADDINST = {installed('resourcecompiler', 0x1c13ac0)};\nconst SETXF = {installed('resourcecompiler', 0x1c19970)};\n"
            f"const MS_SETUP = {meshsystem_copy(installed('resourcecompiler', 0x1c19be0), 24)};\n")
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "scenekd_capture", a.map + ".jsonl")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
        os.remove(vpk)
    sink = open(out_path, "w")
    lock = threading.Lock()
    count = [0]

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg)
            return
        if msg["payload"].get("hooked") is not None:
            print("hooked", flush=True)
            return
        with lock:
            sink.write(json.dumps(msg["payload"]) + "\n")
            count[0] += 1
            if "which" in msg["payload"]:
                print("scene", msg["payload"]["which"], len(msg["payload"]["nodes"]), "nodes", flush=True)

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    dev = frida.get_local_device()
    pid = dev.spawn([os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
                     "-i", source, "-world", "-phys", "-fshallow"], cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT.replace("'use strict';\n", "'use strict';\n" + head, 1))
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
    sink.close()
    print(out_path, count[0], "scenes")
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored" if ok else "RESTORE CMP FAILED", vpk)
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
