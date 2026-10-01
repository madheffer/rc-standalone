"""capture_overlays.py <addon> <map> [--out file] [--full]: record what BuildNode's overlay pass works from.

Hooks WRBNode_GenerateOverlayMeshes (old build 18025ece0) on entry and dumps
the node's overlay descriptors (node +0x1f8 count, +0x200 pointers, 0xc0
bytes each: mode, far, render order, back faces, angle, tint, material,
target id set, faces of 0x78 with positions, texcoords and two vec4
streams) and the mesh pointer of every node entry. Hooks
COverlayProjector_ProjectOntoTarget (old 181362050): the 0x50 target record
it is given and the mesh it returns (vertices, indices, streams), so the
clipper can be checked per target. OverlayCaptureProbe reads the file.

CS2 must be closed (a watchdog kills the compile if it starts), 10 GB must
be free, and the map's package is backed up first and restored, cmp-checked,
afterwards. Each hook's first bytes are checked against the 09-23 build
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
HOOKS = {"overlays": (0x18025ece0, 0x25ed10), "project": (0x181362050, 0x13623c0)}

AGENT = r"""
'use strict';
function str(p) { const s = p.readPointer(); return s.isNull() ? '' : s.readUtf8String(); }
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
  const vb = bytes(m.readPointer(), nv * stride * 4), ib = bytes(m.add(0x10).readPointer(), ni * 4);
  const out = new Uint8Array(vb.byteLength + ib.byteLength);
  out.set(new Uint8Array(vb), 0);
  out.set(new Uint8Array(ib), vb.byteLength);
  tag.nv = nv; tag.stride = stride; tag.ni = ni; tag.streams = streams; tag.mesh = m.toString();
  send(tag, out.buffer);
}
let node = 0, overlay = -1;
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  send({ev: 'hooked'});
  Interceptor.attach(mod.base.add(%(overlays)d), {
    onEnter(args) {
      const nd = args[0];
      const ne = nd.add(0x148).readS32(), ep = nd.add(0x150).readPointer();
      const meshes = [];
      for (let i = 0; i < ne; i++) meshes.push(ep.add(i * 0x238).readPointer().toString());
      const n = nd.add(0x1f8).readS32(), list = nd.add(0x200).readPointer();
      send({ev: 'node', node: node, entries: meshes, overlays: n});
      for (let i = 0; i < n; i++) {
        const o = list.add(i * 8).readPointer();
        const nf = o.add(0x48).readS32(), fp = o.add(0x50).readPointer();
        const faces = [];
        const parts = [o.readByteArray(0xc0)];
        // target id set (mode 3): 8-byte slots at +0x30, bucket count +0x3c
        const slots = o.add(0x3c).readS32();
        parts.push(bytes(o.add(0x30).readPointer(), slots > 0 ? slots * 8 : 0));
        for (let k = 0; k < nf; k++) {
          const f = fp.add(k * 0x78);
          const c = [0, 0x18, 0x30, 0x48, 0x60].map(x => f.add(x).readS32());
          faces.push(c);
          parts.push(f.readByteArray(0x78));
          parts.push(bytes(f.add(0x08).readPointer(), c[0] * 12));
          parts.push(bytes(f.add(0x20).readPointer(), c[1] * 8));
          parts.push(bytes(f.add(0x38).readPointer(), c[2] * 16));
          parts.push(bytes(f.add(0x50).readPointer(), c[3] * 16));
        }
        let len = 0;
        for (const p of parts) len += p.byteLength;
        const out = new Uint8Array(len);
        let at = 0;
        for (const p of parts) { out.set(new Uint8Array(p), at); at += p.byteLength; }
        send({ev: 'overlay', node: node, i: i, material: str(o.add(0x20)), s70: str(o.add(0x70)),
              name: str(o.add(0x78)), slots: slots, faces: faces}, out.buffer);
      }
      overlay = -1;
      node++;
    }
  });
  Interceptor.attach(mod.base.add(%(project)d), {
    onEnter(args) {
      this.out = args[0];
      this.rec = args[2].readByteArray(0x50);
      this.target = args[2].readPointer().toString();
    },
    onLeave(ret) {
      const m = this.out.readPointer();
      const tag = {ev: 'projected', node: node - 1, target: this.target, empty: m.isNull()};
      send({ev: 'target', node: node - 1, target: this.target}, this.rec);
      if (!m.isNull()) dumpMesh(m, tag); else send(tag);
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

    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "overlay_capture", a.map + ".bin")
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
    sc = ses.create_script(AGENT % {k: v[1] for k, v in HOOKS.items()})
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
