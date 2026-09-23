"""Record the sampler's per ray results for clusters centred at given points.

    python capture_rays.py <addon> <map> x,y,z [x,y,z ...] [--out file]

Hooks TallyRays, which turns a cluster's traced rays into the records the walk
reads (origin, direction, distance, hit flag), and keeps those whose centre is
one asked for. With capture_merge.py this separates "we traced differently"
from "we walked the same segment differently".

Output: per match, a json line then the records as hex (0x20 bytes each:
origin[3], dir[3], dist, u16 index, u8 flags at +0x1e) and the tracer's raw hits
(0x38 bytes each).
"""
import argparse
import json
import os
import sys
import threading

import frida

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.join(ROOT, "tools"))
from sigscan import resolve                                # noqa: E402

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
const RVA = %(rva)d, WANT = %(want)s;
function hook(m) {
  Interceptor.attach(m.base.add(RVA), {
    onEnter(a) { this.recs = a[1]; this.hits = a[2]; this.c = a[3]; },
    onLeave() {
      const c = [0, 4, 8].map(k => this.c.add(k).readFloat());
      if (!WANT.some(w => w[0] === c[0] && w[1] === c[1] && w[2] === c[2])) return;
      const n = this.recs.readS32(), p = this.recs.add(8).readPointer();
      send({centre: c, n}, p.readByteArray(n * 0x20));
      send({centre: c, n, hits: true}, this.hits.readByteArray(n * 0x38));
    }
  });
}
const f = Process.findModuleByName('visbuilder.dll');
if (f) hook(f); else Process.attachModuleObserver({onAdded(m) {
  if (m.name.toLowerCase() === 'visbuilder.dll') hook(m); }});
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("addon")
    parser.add_argument("map")
    parser.add_argument("centres", nargs="+")
    parser.add_argument("--out")
    args = parser.parse_args()

    with open(os.path.join(ROOT, "docs", "visbuilder.signatures.json"), encoding="utf-8") as h:
        manifest = json.load(h)
    with open(os.path.join(BIN, "visbuilder.dll"), "rb") as h:
        row = resolve(h.read(), manifest, {"TallyRays"})[0]
    rva = int(row["now"], 16) - 0x180000000
    want = [[float(v) for v in c.split(",")] for c in args.centres]

    out_path = args.out or os.path.join(os.environ.get("TEMP", "."), "vis_capture",
                                        "%s.rays.jsonl" % args.map)
    out = open(out_path, "w", encoding="utf-8")
    lock = threading.Lock()

    def on_message(message, data):
        if message["type"] != "send":
            print(message, file=sys.stderr)
            return
        p = message["payload"]
        p["hex"] = data.hex()
        with lock:
            out.write(json.dumps(p) + "\n")

    vpk = os.path.join(CS2, "game", "csgo_addons", args.addon, "maps", args.map + ".vpk")
    if os.path.exists(vpk):
        os.remove(vpk)
    source = os.path.join(CS2, "content", "csgo_addons", args.addon, "maps", args.map + ".vmap")
    device = frida.get_local_device()
    pid = device.spawn([os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game",
                        os.path.join(CS2, "game", "csgo"), "-i", source,
                        "-world", "-vis", "-fshallow"], cwd=BIN, stdio="pipe")
    session = device.attach(pid)
    script = session.create_script(AGENT % {"rva": rva, "want": json.dumps(want)})
    script.on("message", on_message)
    script.load()
    done = threading.Event()
    session.on("detached", lambda *a: done.set())
    device.resume(pid)
    done.wait()
    out.close()
    print("->", out_path)


if __name__ == "__main__":
    main()
