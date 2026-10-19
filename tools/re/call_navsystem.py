"""call_navsystem.py triangulate|gjk [--out file] [--cases N] [--seed S]: navsystem's own functions as an oracle.

Spawns resourcecompiler.exe suspended under Frida (it never runs), loads navsystem.dll (20261015) from
the game's bin directory and calls functions directly on generated inputs.

  triangulate  1800f3780(float3 *corners, int n, float3 *normal, int *out, int cap) -> int, the fan
               split's fallback triangulation. Cases: convex polygons in tilted planes (3 to 16
               corners), slivers, collinear and repeated corners, near-degenerate quads, each with
               its float corners, normal, cap and Valve's index list. Written as JSON (default
               %TEMP%/navgen_capture/triangulate.json) for NavGenTriangulateTests.

  gjk          1800a3060(hull *a, transform *xa, hull *b, transform *xb) -> float, IslandRemoval's
               point-cloud distance (both transforms the identity at 180139ed0), and vphysics2's
               CVPhysics2Interface vfn 0x190 (180073970: matrix3x4 *xa, float3 *a, int na, matrix3x4 *xb,
               float3 *b, int nb -> float, 0 when at most 0.125) that its seed and link boxes use. Cases:
               nav-like polygons and boxes apart, touching, overlapping and stacked, near 4 and 0.125.
               Written as JSON (default %TEMP%/navgen_capture/gjk.json) for NavGjkTests.

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
  },
  gjk(cases) {
    const m = load();
    const fn = new NativeFunction(m.base.add(0xa3060), 'float', ['pointer', 'pointer', 'pointer', 'pointer']);
    const xf = m.base.add(0x139ed0);
    const vp = Module.load(BIN_DIR + '\\vphysics2.dll');
    const ci = new NativeFunction(vp.getExportByName('CreateInterface'), 'pointer', ['pointer', 'pointer']);
    const iface = ci(Memory.allocUtf8String('VPhysics2_Interface_001'), ptr(0));
    const dist = new NativeFunction(iface.readPointer().add(0x190).readPointer(), 'float',
      ['pointer', 'pointer', 'pointer', 'int', 'pointer', 'pointer', 'int']);
    const ident = Memory.alloc(48);
    [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0].forEach((v, i) => ident.add(i * 4).writeFloat(v));
    function points(list) {
      const p = Memory.alloc(Math.max(1, list.length) * 12);
      list.forEach((q, i) => { p.add(i * 12).writeFloat(q[0]); p.add(i * 12 + 4).writeFloat(q[1]); p.add(i * 12 + 8).writeFloat(q[2]); });
      return p;
    }
    function hull(p, n) {
      const h = Memory.alloc(0x30);
      h.add(0x18).writePointer(p);
      h.add(0x20).writeS32(n);
      h.add(0x28).writeU8(4);
      return h;
    }
    const out = [];
    for (const c of cases) {
      const pa = points(c.a), pb = points(c.b);
      const nav = fn(hull(pa, c.a.length), xf, hull(pb, c.b.length), xf);
      const phys = dist(iface, ident, pa, c.a.length, ident, pb, c.b.length);
      out.push({nav: nav, phys: phys});
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


def polygon(rng, centre, size, n, tilt):
    nrm = unit([rng.uniform(-tilt, tilt), rng.uniform(-tilt, tilt), 1.0])
    a = unit([1.0, 0.0, -nrm[0] / nrm[2]])
    b = [nrm[1] * a[2] - nrm[2] * a[1], nrm[2] * a[0] - nrm[0] * a[2], nrm[0] * a[1] - nrm[1] * a[0]]
    angles = sorted(rng.uniform(0, 2 * math.pi) for _ in range(n))
    return [[f32(centre[d] + size * math.cos(t) * a[d] + size * math.sin(t) * b[d]) for d in range(3)] for t in angles]


def box(centre, half):
    lo = [f32(c - half) for c in centre]
    hi = [f32(c + half) for c in centre]
    return [[x, y, z] for z in (lo[2], hi[2]) for y in (lo[1], hi[1]) for x in (lo[0], hi[0])]


def gjk_cases(rng, count):
    cases = []
    for k in range(count):
        kind = rng.choice(["pair", "pair", "stacked", "grid", "box", "box_near"])
        c = [rng.uniform(-3000, 3000), rng.uniform(-3000, 3000), rng.uniform(-500, 500)]
        if kind == "grid":
            # Axis-aligned grid squares a whole number of units apart (gaps of exactly 4 and 0 included).
            s = rng.choice([8, 16, 32, 64])
            gap = rng.choice([0, 1, 2, 3, 4, 4, 4, 5, 8])
            x0, y0, z0 = (round(v) for v in c)
            dz = rng.choice([0, 0, 2, 4, 6])
            a = [[x0, y0, z0], [x0 + s, y0, z0], [x0 + s, y0 + s, z0], [x0, y0 + s, z0]]
            b = [[x0 + s + gap, y0, z0 + dz], [x0 + 2 * s + gap, y0, z0 + dz], [x0 + 2 * s + gap, y0 + s, z0 + dz], [x0 + s + gap, y0 + s, z0 + dz]]
            a = [[float(v) for v in p] for p in a]
            b = [[float(v) for v in p] for p in b]
        elif kind == "stacked":
            size = rng.uniform(8, 200)
            a = polygon(rng, c, size, rng.randint(3, 8), 0.2)
            b = polygon(rng, [c[0] + rng.uniform(-size, size), c[1] + rng.uniform(-size, size), c[2] + rng.uniform(-8, 8)], size, rng.randint(3, 8), 0.2)
        elif kind == "pair":
            size = rng.uniform(8, 200)
            a = polygon(rng, c, size, rng.randint(3, 8), 0.4)
            off = rng.choice([2 * size + rng.uniform(-6, 6), 2 * size, 2 * size + 4, rng.uniform(0, 3 * size)])
            t = rng.uniform(0, 2 * math.pi)
            b = polygon(rng, [c[0] + off * math.cos(t), c[1] + off * math.sin(t), c[2] + rng.uniform(-5, 5)], size, rng.randint(3, 8), 0.4)
        else:
            half = rng.choice([16.0, 40.0])
            a = box(c, half)
            size = rng.uniform(8, 200)
            d = half + rng.choice([0.0, 0.125, 0.12, 0.13, rng.uniform(-half, 3 * half)]) + (size if kind == "box" else 0)
            t = rng.uniform(0, 2 * math.pi)
            b = polygon(rng, [c[0] + d * math.cos(t), c[1] + d * math.sin(t), c[2] + rng.uniform(-half - 2, half + 2)], size, rng.randint(3, 8), 0.3)
        if rng.random() < 0.5:
            a, b = b, a
        cases.append({"kind": kind, "a": a, "b": b})
    return cases


def running(name):
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq " + name], capture_output=True, text=True).stdout
    return name.lower() in out.lower()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("what", choices=["triangulate", "gjk"])
    p.add_argument("--out")
    p.add_argument("--cases", type=int, default=4000)
    p.add_argument("--seed", type=int, default=1)
    a = p.parse_args()
    if running("cs2.exe"):
        raise SystemExit("CS2 is running; not starting")
    out = a.out or os.path.join(os.environ.get("TEMP", "."), "navgen_capture", a.what + ".json")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    rng = random.Random(a.seed)
    cases = plane_cases(rng, a.cases) if a.what == "triangulate" else gjk_cases(rng, a.cases)
    dev = frida.get_local_device()
    pid = dev.spawn([os.path.join(BIN, "resourcecompiler.exe")], cwd=BIN)
    try:
        ses = dev.attach(pid)
        sc = ses.create_script(AGENT.replace("BIN_DIR", json.dumps(BIN)))
        sc.on("message", lambda m, d: print("agent:", m))
        sc.load()
        results = []
        for i in range(0, len(cases), 200):
            results += getattr(sc.exports_sync, a.what)(cases[i:i + 200])
    finally:
        subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    for c, r in zip(cases, results):
        c["valve"] = r
    with open(out, "w") as f:
        json.dump(cases, f)
    if a.what == "triangulate":
        print(len(cases), "cases ->", out, "; failures (0 indices):", sum(1 for c in cases if c["valve"]["count"] == 0))
    else:
        print(len(cases), "cases ->", out, "; within 4:", sum(1 for c in cases if c["valve"]["nav"] <= 4))


if __name__ == "__main__":
    main()
