"""Record the traced segments the PVS scan walks, for rays starting at given points.

    python capture_segments.py <addon> <map> x,y,z [x,y,z ...] [--passes 0,40] [--out file]

Hooks RayFold, which walks a ray list's traced segments through the octree, and
keeps every segment whose start is one of the points (bit-exact float32), with
the scan pass it ran in (BeginPass calls, numbered as capture_pvs.py numbers
pairs<k>) and whether it was walked through blockers. The compile is stopped
once the last pass asked for has finished. This separates "we traced the ray
differently" (the end differs) from "we walked the same segment differently".

Output: json lines {pass, through, start[3], end[3]} with the floats as round-
trip reprs, then also the raw 24 bytes as hex.
"""
import argparse
import json
import os
import struct
import sys
import threading

import frida

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from package_guard import PackageGuard  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.join(ROOT, "tools"))
import sigscan  # noqa: E402

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
HOOKS = ["RayFold", "BeginPass"]

AGENT = r"""
const RVA = %(rva)s, WANT = %(want)s, PASSES = %(passes)s, LAST = Math.max(...PASSES);
let pass = -1;
function hook(m) {
  Interceptor.attach(m.base.add(RVA.BeginPass), {
    onEnter() {
      pass++;
      if (pass > LAST) { send({done: true}); Thread.sleep(3600); }
    }
  });
  Interceptor.attach(m.base.add(RVA.RayFold), {
    onEnter(a) {
      if (!PASSES.includes(pass)) return;
      const data = a[1].add(8).readPointer(), from = a[2].toInt32(), to = a[3].toInt32();
      const through = a[6].toInt32() & 0xff;
      for (let i = from; i < to; i++) {
        const p = data.add(i * 0x18);
        const x = p.readFloat(), y = p.add(4).readFloat(), z = p.add(8).readFloat();
        if (!WANT.some(w => w[0] === x && w[1] === y && w[2] === z)) continue;
        send({pass, through, index: i}, p.readByteArray(0x18));
      }
    }
  });
}
const f = Process.findModuleByName('visbuilder.dll');
if (f) hook(f); else Process.attachModuleObserver({onAdded(m) {
  if (m.name.toLowerCase() === 'visbuilder.dll') hook(m); }});
"""


def resolve_hooks(dll):
    with open(os.path.join(ROOT, "docs", "visbuilder.signatures.json"), encoding="utf-8") as handle:
        manifest = json.load(handle)
    with open(dll, "rb") as handle:
        image = handle.read()
    base, _ = sigscan.sections(image)
    rows = {r["name"]: r for r in sigscan.resolve(image, manifest, set(HOOKS))}
    lost = [name for name in HOOKS if rows.get(name, {}).get("status") not in ("same", "moved")]
    if lost:
        sys.exit("these hooked functions no longer resolve, re-sign them first: " + ", ".join(lost))
    return {name: int(rows[name]["now"], 16) - base for name in HOOKS}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("addon")
    parser.add_argument("map")
    parser.add_argument("points", nargs="+")
    parser.add_argument("--passes", default="0")
    parser.add_argument("--out")
    args = parser.parse_args()

    # Each point as the float32 the compile holds, so the agent can compare exactly.
    want = [[struct.unpack("<f", struct.pack("<f", float(v)))[0] for v in p.split(",")] for p in args.points]
    passes = [int(p) for p in args.passes.split(",")]
    rva = resolve_hooks(os.path.join(BIN, "visbuilder.dll"))

    out_path = args.out or os.path.join(os.environ.get("TEMP", "."), "vis_capture", "%s.segments.jsonl" % args.map)
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    out = open(out_path, "w", encoding="utf-8")
    done = threading.Event()

    def on_message(message, data):
        if message["type"] != "send":
            print("agent:", message.get("description") or message, file=sys.stderr, flush=True)
            return
        payload = message["payload"]
        if payload.get("done"):
            done.set()
            return
        start = struct.unpack("<3f", data[:12])
        end = struct.unpack("<3f", data[12:24])
        row = dict(payload, start=[repr(v) for v in start], end=[repr(v) for v in end], hex=data.hex())
        out.write(json.dumps(row) + "\n")
        out.flush()
        print(row, flush=True)

    vpk = os.path.join(CS2, "game", "csgo_addons", args.addon, "maps", args.map + ".vpk")
    with PackageGuard(vpk, out_path + ".vpk.bak") as guard:
        source = os.path.join(CS2, "content", "csgo_addons", args.addon, "maps", args.map + ".vmap")
        device = frida.get_local_device()
        pid = device.spawn([os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game",
                            os.path.join(CS2, "game", "csgo"), "-i", source,
                            "-world", "-vis", "-fshallow"], cwd=BIN, stdio="pipe")
        guard.pid = pid
        session = device.attach(pid)
        script = session.create_script(AGENT % {"rva": json.dumps(rva), "want": json.dumps(want),
                                                "passes": json.dumps(passes)})
        script.on("message", on_message)
        script.load()
        session.on("detached", lambda *a: done.set())
        device.resume(pid)
        done.wait()
        try:
            device.kill(pid)
        except frida.ProcessNotFoundError:
            pass
        out.close()
        print("->", out_path)


if __name__ == "__main__":
    main()
