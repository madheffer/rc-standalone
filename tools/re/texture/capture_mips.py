"""capture_mips.py <vtex content path> <out dir>: Valve's float mip chain, level by level.

Runs resourcecompiler on one .vtex under Frida and dumps the input and output of every
FloatBitMap_t::QuarterSize (0923 180c43450) and of the per-channel half (18127a9c0) as RGBA
float32 (NNN_<tag>_<w>x<h>.f32 plus index.json). This is how the box/3-tap orders and the linear
loader were settled (docs/TEXTURES.md). CS2 must be closed; ask whoever else uses RC first.
A tiny texture can finish before Frida delivers; rerun if it reports 0 dumps.
"""
import json, os, struct, subprocess, sys, threading
import frida
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "..", "source2-compiler", "tools", "re"))
from rva_map import installed

CS2 = r"D:\Steam\steamapps\common\Counter-Strike Global Offensive"
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
'use strict';
function dump(bm, tag) {
  const w = bm.readS32(), h = bm.add(4).readS32(), d = bm.add(8).readS32();
  const out = new Float32Array(w * h * 4);
  for (let c = 0; c < 4; c++) {
    const base = bm.add(0x20 + 8 * c).readPointer();
    const sx = bm.add(0x3a0 + 8 * c).readS64().toNumber();
    const sy = bm.add(0x4a0 + 8 * c).readS64().toNumber();
    if (base.isNull()) continue;
    for (let y = 0; y < h; y++)
      for (let x = 0; x < w; x++)
        out[(y * w + x) * 4 + c] = base.add(y * sy + x * sx).readFloat();
  }
  send({tag: tag, w: w, h: h, d: d}, out.buffer);
  recv('ack', function () {}).wait();
}
function hook() {
  let mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) mod = Module.load('%(dll)s');
  Interceptor.attach(mod.base.add(%(quarter)d), {
    onEnter(args) { this.src = args[0]; this.dst = args[1]; },
    onLeave() { dump(this.src, 'src'); dump(this.dst, 'dst'); }
  });
  Interceptor.attach(mod.base.add(%(half)d), {
    onEnter(args) { this.dst = args[0]; this.src = args[1]; this.ch = args[2].toInt32(); },
    onLeave() { if (this.ch === 3) { dump(this.src, 'hsrc'); dump(this.dst, 'hdst'); } }
  });
  send({hooked: true});
}
hook();
"""

def running(name):
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq " + name], capture_output=True, text=True).stdout
    return name.lower() in out.lower()

def main():
    src, out_dir = sys.argv[1], sys.argv[2]
    if running("cs2.exe"): raise SystemExit("CS2 is running")
    if running("resourcecompiler.exe"): raise SystemExit("another resourcecompiler is running")
    os.makedirs(out_dir, exist_ok=True)
    rva = installed("resourcecompiler", 0xc43450)
    print("QuarterSize rva", hex(rva))
    index = []
    hooked = threading.Event()
    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg); return
        p = msg["payload"]
        if p.get("hooked"): hooked.set(); return
        n = len(index)
        name = "%03d_%s_%dx%d.f32" % (n, p["tag"], p["w"], p["h"])
        open(os.path.join(out_dir, name), "wb").write(data)
        index.append(dict(p, file=name))
        sc.post({'type': 'ack'})
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-f", "-game", os.path.join(CS2, "game", "csgo"), "-i", src]
    dev = frida.get_local_device()
    dev.on('output', lambda pid, fd, data: sys.stdout.write(data.decode(errors='replace')) if data else None)
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT % {"dll": os.path.join(BIN, "resourcecompiler.dll").replace(chr(92), "/"), "quarter": rva, "half": installed("resourcecompiler", 0x127a9c0)})
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    hooked.wait(30)
    dev.resume(pid)
    done.wait(600)
    while running("resourcecompiler.exe"):
        threading.Event().wait(1)
    json.dump(index, open(os.path.join(out_dir, "index.json"), "w"), indent=1)
    print("hooked" if hooked.is_set() else "NEVER HOOKED", len(index), "dumps")

main()
