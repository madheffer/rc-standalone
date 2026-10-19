"""call_navsystem.py triangulate [--out file] [--cases N] [--seed S]: navsystem's own functions as an oracle.

Spawns resourcecompiler.exe suspended under Frida (it never runs), loads navsystem.dll (20261015) from
the game's bin directory and calls functions directly on generated inputs.

  triangulate  1800f3780(float3 *corners, int n, float3 *normal, int *out, int cap) -> int, the fan
               split's fallback triangulation. Cases: convex polygons in tilted planes (3 to 16
               corners), slivers, collinear and repeated corners, near-degenerate quads, each with
               its float corners, normal, cap and Valve's index list. Written as JSON (default
               %TEMP%/navgen_capture/triangulate.json) for NavGenTriangulateTests.

CS2 must be closed (the rule for any resourcecompiler run on this PC).
"""
import argparse
import json
import math
import os
import random
import struct
import subprocess
import threading

import frida

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
'use strict';
let nav = null;
function load() {
  if (nav === null) nav = Module.load(BIN_DIR + '\\navsystem.dll');
  return nav;
}
rpc.exports = {
  triangulate(cases) {
    const m = load();
    const fn = new NativeFunction(m.base.add(0xf3780), 'int', ['pointer', 'int', 'pointer', 'pointer', 'int']);
    const out = [];
    for (const c of cases) {
      const n = c.corners.length;
      const pts = Memory.alloc(Math.max(1, n) * 12);
      c.corners.forEach((p, i) => { pts.add(i * 12).writeFloat(p[0]); pts.add(i * 12 + 4).writeFloat(p[1]); pts.add(i * 12 + 8).writeFloat(p[2]); });
      const nrm = Memory.alloc(12);
      nrm.writeFloat(c.normal[0]); nrm.add(4).writeFloat(c.normal[1]); nrm.add(8).writeFloat(c.normal[2]);
      const tris = Memory.alloc(Math.max(1, c.cap) * 4 + 64);
      const count = fn(pts, n, nrm, tris, c.cap);
      const idx = [];
      for (let i = 0; i < count; i++) idx.push(tris.add(i * 4).readS32());
      out.push({count: count, tris: idx});
    }
    return out;
  }
};
"""


def f32(x):
    return struct.unpack("<f", struct.pack("<f", x))[0]


def unit(v):
    l = math.sqrt(sum(c * c for c in v))
    return [c / l for c in v]


def plane_cases(rng, count):
    cases = []
    for k in range(count):
        n = rng.choice([3, 4, 4, 5, 5, 6, 6, 7, 8, 9, 10, 12, 16])
        nrm = unit([rng.uniform(-0.3, 0.3), rng.uniform(-0.3, 0.3), 1.0])
        # Two in-plane axes.
        a = unit([1.0, 0.0, -nrm[0] / nrm[2]])
        b = [nrm[1] * a[2] - nrm[2] * a[1], nrm[2] * a[0] - nrm[0] * a[2], nrm[0] * a[1] - nrm[1] * a[0]]
        centre = [rng.uniform(-3000, 3000), rng.uniform(-3000, 3000), rng.uniform(-500, 500)]
        kind = rng.choice(["convex", "sliver", "collinear", "repeat", "ellipse"])
        angles = sorted(rng.uniform(0, 2 * math.pi) for _ in range(n))
        rx = rng.uniform(4, 600)
        ry = rx * (rng.uniform(0.002, 0.05) if kind == "sliver" else rng.uniform(0.3, 1.0))
        pts2 = [(rx * math.cos(t), ry * math.sin(t)) for t in angles]
        if kind == "collinear" and n >= 5:
            i = rng.randrange(n)
            j = (i + 2) % n
            m = (i + 1) % n
            pts2[m] = ((pts2[i][0] + pts2[j][0]) / 2, (pts2[i][1] + pts2[j][1]) / 2)
        if kind == "repeat" and n >= 5:
            i = rng.randrange(n)
            pts2[(i + 1) % n] = pts2[i]
        corners = []
        for (u, v) in pts2:
            p = [centre[d] + u * a[d] + v * b[d] for d in range(3)]
            corners.append([f32(c) for c in p])
        if rng.random() < 0.5:
            corners.reverse()
        flip = -1.0 if rng.random() < 0.1 else 1.0
        cases.append({"kind": kind, "corners": corners, "normal": [f32(flip * c) for c in nrm],
                      "cap": (n - 2) * 3 if rng.random() < 0.9 else rng.randrange(0, (n - 2) * 3 + 1)})
    return cases


def running(name):
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq " + name], capture_output=True, text=True).stdout
    return name.lower() in out.lower()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("what", choices=["triangulate"])
    p.add_argument("--out")
    p.add_argument("--cases", type=int, default=4000)
    p.add_argument("--seed", type=int, default=1)
    a = p.parse_args()
    if running("cs2.exe"):
        raise SystemExit("CS2 is running; not starting")
    out = a.out or os.path.join(os.environ.get("TEMP", "."), "navgen_capture", "triangulate.json")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    rng = random.Random(a.seed)
    cases = plane_cases(rng, a.cases)
    dev = frida.get_local_device()
    pid = dev.spawn([os.path.join(BIN, "resourcecompiler.exe")], cwd=BIN)
    try:
        ses = dev.attach(pid)
        sc = ses.create_script(AGENT.replace("BIN_DIR", json.dumps(BIN)))
        sc.on("message", lambda m, d: print("agent:", m))
        sc.load()
        results = []
        for i in range(0, len(cases), 200):
            results += sc.exports_sync.triangulate(cases[i:i + 200])
    finally:
        subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    for c, r in zip(cases, results):
        c["valve"] = r
    with open(out, "w") as f:
        json.dump(cases, f)
    print(len(cases), "cases ->", out, "; failures (0 indices):", sum(1 for c in cases if c["valve"]["count"] == 0))


if __name__ == "__main__":
    main()
