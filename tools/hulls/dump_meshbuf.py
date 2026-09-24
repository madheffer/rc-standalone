"""dump_meshbuf.py <addon> <map> <outdir> -- the mesh DMX the map builder reads each node from.

FUN_180ffd010 unserialises a node's `hammerMeshDataBuffer` (a CUtlBuffer at
+0x18 of a 0xf0-byte entry: data pointer at +0x20, size at +0x38) into the
DMX FUN_180d47810 turns into CMeshes. This writes every such buffer to
<outdir>/<n>.dmx and one JSON line per buffer to <outdir>/index.jsonl with
the entry's bytes and the producer that made its list. FUN_18023bd00 asks
an interface (slot 0x168) that forwards to another one's slot 0x640; the
producer is that second function.

The compile overwrites the map's .vpk; back it up first. CS2 must be closed.
"""
import argparse
import json
import os
import threading

import frida

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
'use strict';
function where(p) {
  const m = Process.findModuleByAddress(p);
  return m ? m.name + '+0x' + p.sub(m.base).toString(16) : p.toString();
}
function str(p) {
  try { const s = p.readPointer(); return s.isNull() ? null : s.readUtf8String(); } catch (e) { return '?'; }
}
function hook() {
  const m = Process.findModuleByName('resourcecompiler.dll');
  if (m === null) { setTimeout(hook, 5); return; }
  let n = 0;
  let producer = null;
  Interceptor.attach(m.base.add(0x23bd00), {
    onEnter(args) {
      const vt = args[0].readPointer();
      const inner = args[0].add(8).readPointer();
      producer = where(inner.readPointer().add(0x640).readPointer());
      send({source: where(vt.add(0x168).readPointer()), vtable: where(vt),
            inner: producer, caller: where(this.returnAddress)});
    }
  });
  Interceptor.attach(m.base.add(0xffd010), {
    onEnter(args) {
      const e = args[1];
      const raw = Array.from(new Uint8Array(e.readByteArray(0xf0))).map(b => b.toString(16).padStart(2, '0')).join('');
      const size = e.add(0x38).readS32();
      const data = e.add(0x20).readPointer();
      const meta = {n: n++, size: size, producer: producer, raw: raw,
                    strings: [str(e.add(0x98)), str(e.add(0xa0)), str(e.add(0xa8)), str(e.add(0xb0))],
                    caller: where(this.returnAddress)};
      send(meta, size > 0 && !data.isNull() ? data.readByteArray(size) : null);
    }
  });
  send({hooked: true});
}
hook();
"""


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("outdir")
    a = p.parse_args()
    os.makedirs(a.outdir, exist_ok=True)
    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4",
            "-game", os.path.join(CS2, "game", "csgo"), "-i", source, "-world", "-fshallow"]
    index = open(os.path.join(a.outdir, "index.jsonl"), "w", encoding="utf-8", newline="\n")
    sources = set()

    def on_message(msg, data):
        if msg.get("type") != "send":
            print(msg)
            return
        pay = msg["payload"]
        if "source" in pay:
            key = json.dumps(pay, sort_keys=True)
            if key not in sources:
                sources.add(key)
                print(pay)
        elif "n" in pay:
            if data:
                with open(os.path.join(a.outdir, "%d.dmx" % pay["n"]), "wb") as f:
                    f.write(data)
            index.write(json.dumps(pay) + "\n")
        else:
            print(pay)

    dev = frida.get_local_device()
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT)
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    done.wait()
    index.close()


if __name__ == "__main__":
    main()
