"""capture_propmeshes.py <addon> <map> [--out file] [--full]: record the meshes WRB_LoadPropMeshes returns.

Hooks WRB_LoadPropMeshes (old build 1801d30d0): the CMeshes a baked prop's
model LOD becomes, model space, vertices already unpacked to floats by the
resource system (vertices, indices, streams), with the call's string
argument when it reads as one. PropEntriesProbe compares them with VRF's
decode of the model's buffers.

CS2 must be closed (a watchdog kills the compile if it starts), 10 GB must
be free, and the map's package is backed up first and restored, cmp-checked,
afterwards. The hook's first bytes are checked against the 09-23 build
before anything is attached.
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

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
OLD_DLL = r"D:\tools\binaries\resourcecompiler_20260923.dll"
# (old 09-23 address, new RVA): 0x18025xxxx moved +0x30, 0x18136xxxx +0x370 (to be checked by bytes).
HOOKS = {"load": (0x1801d30d0, 0x1d3100)}
# The geometry interface pointer read by 18174eec0 (old 183b5b510), new build.
IFACE_RVA = 0x3b5b350

AGENT = r"""
'use strict';
function bytes(p, n) { return (p.isNull() || n <= 0) ? new ArrayBuffer(0) : p.readByteArray(n); }
function dumpMesh(m, tag) {
  const nv = m.add(0x18).readS32(), stride = m.add(0x1c).readS32();
  const ns = m.add(0x20).readS32(), ni = m.add(0x24).readS32();
  const streams = [];
  const sp = m.add(8).readPointer();
  for (let k = 0; k < ns; k++) {
    const s = sp.add(k * 0x28);
    const np = s.readPointer();
    streams.push({name: np.isNull() ? '' : np.readUtf8String(), index: s.add(0x10).readS32(),
                  first: s.add(0x14).readS32(), count: s.add(0x18).readS32(), type: s.add(0x20).readS32()});
  }
  const mlen = m.add(0x60).readU32() & 0x3fffffff, mflags = m.add(0x64).readU32();
  let material = '';
  try { material = mlen === 0 ? '' : ((mflags >> 30) & 1) ? m.add(0x68).readUtf8String(mlen) : m.add(0x68).readPointer().readUtf8String(mlen); } catch (e) {}
  const vb = bytes(m.readPointer(), nv * stride * 4), ib = bytes(m.add(0x10).readPointer(), ni * 4);
  const out = new Uint8Array(vb.byteLength + ib.byteLength);
  out.set(new Uint8Array(vb), 0);
  out.set(new Uint8Array(ib), vb.byteLength);
  tag.nv = nv; tag.stride = stride; tag.ni = ni; tag.streams = streams; tag.material = material;
  send(tag, out.buffer);
}
let call = 0;
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  send({ev: 'hooked'});
  Interceptor.attach(mod.base.add(%(load)d), {
    onEnter(args) {
      if (call === 0) {
        // The resource system interface behind the prop's geometry (old 183b5b510).
        const iface = mod.base.add(%(iface)d).readPointer();
        const vtbl = iface.readPointer();
        const fns = [];
        for (let k = 0; k < 64; k++) {
          const f = vtbl.add(k * 8).readPointer();
          const m = Process.findModuleByAddress(f);
          fns.push(m === null ? f.toString() : m.name + '+' + f.sub(m.base).toString());
        }
        send({ev: 'iface', vtable: fns});
      }
      this.out = args[4];
      this.lod = args[3].toInt32();
      let name = '';
      try { name = args[1].readUtf8String(); } catch (e) {}
      this.name = name;
      this.call = call++;
    },
    onLeave(ret) {
      const n = this.out.readS32(), p = this.out.add(8).readPointer();
      send({ev: 'load', call: this.call, name: this.name, lod: this.lod, ok: ret.toInt32() & 0xff, meshes: n});
      for (let i = 0; i < n; i++) {
        const m = p.add(i * 16).readPointer();
        if (!m.isNull()) dumpMesh(m, {ev: 'mesh', call: this.call, i: i});
      }
    }
  });
}
hook();
"""


def running(name):
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq " + name], capture_output=True, text=True).stdout
    return name.lower() in out.lower()


def check_hooks():
    old = pefile.PE(OLD_DLL, fast_load=True)
    new = pefile.PE(os.path.join(BIN, "resourcecompiler.dll"), fast_load=True)
    for name, (va, rva) in HOOKS.items():
        a = old.get_data(va - old.OPTIONAL_HEADER.ImageBase, 24)
        b = new.get_data(rva, 24)
        if a != b:
            raise SystemExit("hook %s: bytes at new RVA %#x differ from the 09-23 build; not starting" % (name, rva))


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
    check_hooks()

    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "propmesh_capture", a.map + ".bin")
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
    sc = ses.create_script(AGENT % dict({k: v[1] for k, v in HOOKS.items()}, iface=IFACE_RVA))
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

    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
