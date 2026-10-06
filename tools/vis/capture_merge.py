"""Run Valve's vis build under Frida and record what its merge actually does.

Everything the port had to go on so far was a count at the end of a stage.
This records the stage from inside: every call of the merge loop with the exact
set it was handed, every pair it merged and at what cost, the set it gave back,
and (optionally) the visibility and candidate lists it priced them from. Fed
back into the port with the SAME input, the first merge that differs is the
defect, rather than a number several thousand merges downstream of it.

    python capture_merge.py <addon> <map> [--out file] [--gen] [--vis]

  --gen   also record cluster generation's per region merges (padded, many)
  --vis   also dump each set's sampled visibility and initial candidate lists
  --passes  only the sets entering and leaving each of the five passes ("pass"
          and "passout" records), no per-merge hooks, and the compile is
          stopped after the fifth pass (minutes rather than hours)
  --vis-n N  with --vis, only buckets handed N clusters (a 30k bucket's bits run to gigabytes)
  --stop-after P  stop the compile when pass P returns
  --rays=x,y,z;x,y,z  also the sampler's rays for clusters centred there
  --gen-box=x,y,z;...  only cluster generation's merges of the leaves with
          those minimums (with --vis, their visibility); stops once all are in
  --premerge  only the distance pre-merge (DistancePreMerge, 180030b40): the
          sets it is handed ("gen"), the runs at the start of every round of
          MergeBestCandidates ("round": box, owner set, index per run), the
          pairs each round sorts ("pairs": union box, run, partner) and the
          sets it leaves ("premerged"); the compile is stopped after it

Addresses come from docs/visbuilder.signatures.json through sigscan, so this
survives a game update the same way the tests do.

Output is a stream of records: u32 json length, json, u32 blob length, blob.
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
import time

import frida

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.join(ROOT, "tools"))
from sigscan import resolve                                # noqa: E402

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
IMAGE_BASE = 0x180000000
HOOKED = ["MergeLoop", "CheapestPair", "AbsorbPair", "BuildCandidates", "TallyRays",
          "Regrid", "MergeClusterSet"]

AGENT = r"""
'use strict';
const RVA = %(rvas)s;
const GEN = %(gen)s, VIS = %(vis)s, PASSES = %(passes)s, OUTSIDE = %(outside)d;
const PREMERGE = %(premerge)s, PM = %(pm)s, VISN = %(visn)d, STOP = %(stop)d, RAYS = %(rays)s;
const GENBOX = %(genbox)s;
let genboxSeen = 0;
let pass = -1, seq = 0;

// Frida drops a message over 128 MiB (and the session with it): a bigger blob
// goes ahead in 'part' messages the host joins onto the record that follows.
const PART = 64 * 1024 * 1024;
function sendBig(head, buf) {
  if (buf === null || buf.byteLength <= PART) { send(head, buf); return; }
  let parts = 0;
  for (let at = 0; at < buf.byteLength; at += PART, parts++)
    send({ev: 'part'}, buf.slice(at, Math.min(at + PART, buf.byteLength)));
  head.parts = parts;
  send(head, null);
}
const live = {};   // thread id -> current MergeLoop record
const known = new Set();   // leaves whose box has been sent

// A pair's leaf is an index into the table the sampler's leaf provider keeps at
// +0x70, 0x18 bytes a leaf (its box). Taking it from Valve's own memory keeps
// the voxelizer out of anything the replay compares.
function sendLeaves(sampler, set) {
  const table = sampler.add(8).add(0x70).readPointer();
  const n = set.readS32(), base = set.add(8).readPointer(), fresh = [];
  for (let i = 0; i < n; i++) {
    const r = base.add(i * 0x58), c = r.readS32(), p = r.add(8).readPointer();
    for (let k = 0; k < c; k++) {
      const leaf = p.add(k * 16 + 8).readS32();
      if (!known.has(leaf)) { known.add(leaf); fresh.push(leaf); }
    }
  }
  if (!fresh.length) return;
  const out = new Uint8Array(fresh.length * 28), dv = new DataView(out.buffer);
  fresh.forEach((leaf, j) => {
    dv.setInt32(j * 28, leaf, true);
    out.set(new Uint8Array(table.add(leaf * 0x18).readByteArray(0x18)), j * 28 + 4);
  });
  send({ev: 'leaves', n: fresh.length}, out.buffer);
}

function dumpSet(set) {
  const n = set.readS32();
  const base = set.add(8).readPointer();
  let total = 0;
  const pairs = [];
  for (let i = 0; i < n; i++) {
    const r = base.add(i * 0x58);
    const c = r.readS32();
    pairs.push(c);
    total += 0x58 + c * 16;
  }
  const out = new Uint8Array(total);
  let at = 0;
  for (let i = 0; i < n; i++) {
    const r = base.add(i * 0x58);
    out.set(new Uint8Array(r.readByteArray(0x58)), at); at += 0x58;
    if (pairs[i] > 0) {
      out.set(new Uint8Array(r.add(8).readPointer().readByteArray(pairs[i] * 16)), at);
      at += pairs[i] * 16;
    }
  }
  return [n, out.buffer];
}

function hook(m) {
  const at = name => m.base.add(RVA[name]);

  // Every set the pass is handed, in order: pass 0's is generation and the
  // pre-merge's output, and each later one is the pass before it, so the
  // bucketing can be replayed against the next pass's inputs.
  function dumpSets(sets) {
    const n = sets.readS32(), base = sets.add(8).readPointer();
    const parts = [];
    let total = 0;
    for (let i = 0; i < n; i++) { const [c, blob] = dumpSet(base.add(i * 0x18)); parts.push([c, blob]); total += 8 + blob.byteLength; }
    const out = new Uint8Array(total), dv = new DataView(out.buffer);
    let at2 = 0;
    for (const [c, blob] of parts) {
      dv.setInt32(at2, c, true); dv.setInt32(at2 + 4, blob.byteLength, true); at2 += 8;
      out.set(new Uint8Array(blob), at2); at2 += blob.byteLength;
    }
    return [n, out.buffer];
  }
  Interceptor.attach(at('Regrid'), {
    onEnter(a) {
      pass++;
      this.sets = a[1];
      const scene = a[0].add(0xe8).readPointer();
      const box = []; for (let k = 0; k < 6; k++) box.push(scene.add(4 + k * 4).readFloat());
      const [n, buf] = dumpSets(a[1]);
      sendBig({ev: 'pass', pass, sets: n, scene: box}, buf);
    },
    onLeave(ret) {
      if (STOP >= 0 && pass === STOP) { send({ev: 'done'}); return; }
      if (!PASSES) return;
      const [n, buf] = dumpSets(this.sets);
      sendBig({ev: 'passout', pass, sets: n, ret: ret.toInt32()}, buf);
      if (pass === 4) send({ev: 'done'});
    }
  });
  if (PREMERGE) {
    // The pre-merge's input and output sets, and every round of its box merge.
    // A run record (0x40 bytes) holds its box at 0, the set that made it at
    // +0x18 and its index in the live list at +0x20; the tree keeps that list
    // as a count at +0x20 and an array of run pointers at +0x28.
    let inside = false;
    Interceptor.attach(m.base.add(PM.pre), {
      onEnter(a) {
        this.sets = a[1];
        inside = true;
        const [n, buf] = dumpSets(a[1]);
        sendBig({ev: 'gen', sets: n}, buf);
      },
      onLeave() {
        inside = false;
        const [n, buf] = dumpSets(this.sets);
        sendBig({ev: 'premerged', sets: n}, buf);
        send({ev: 'done'});
      }
    });
    let round = 0;
    Interceptor.attach(m.base.add(PM.merge), {
      onEnter(a) {
        if (!inside) return;
        const tree = a[0], n = tree.add(0x20).readS32(), list = tree.add(0x28).readPointer();
        const out = new Uint8Array(n * 32), dv = new DataView(out.buffer);
        for (let i = 0; i < n; i++) {
          const r = list.add(i * 8).readPointer();
          out.set(new Uint8Array(r.readByteArray(24)), i * 32);
          dv.setInt32(i * 32 + 24, r.add(0x18).readS32(), true);
          dv.setInt32(i * 32 + 28, r.add(0x20).readS32(), true);
        }
        send({ev: 'round', round: round++, runs: n}, out.buffer);
      }
    });
    Interceptor.attach(m.base.add(PM.sort), {
      onEnter(a) {
        if (!inside) return;
        const n = a[1].sub(a[0]).toInt32() / 32;
        send({ev: 'pairs', round: round - 1, pairs: n}, n > 0 ? a[0].readByteArray(n * 32) : null);
      }
    });
    send({ev: 'hooked', base: m.base.toString()});
    return;
  }
  if (PASSES) {
    // The regions and their verdicts as OutsideDetection (180033890) leaves
    // them, in the same compile as the passes (capture_outside.py's records).
    Interceptor.attach(m.base.add(OUTSIDE), {
      onEnter(a) { this.p = a[0]; },
      onLeave() {
        const p = this.p, n = p.add(0x40).readS32();
        sendBig({ev: 'regions', when: 'leave', count: n}, n > 0 ? p.add(0x48).readPointer().readByteArray(n * 16) : null);
        send({ev: 'status', count: n}, n > 0 ? p.add(0x120).readPointer().readByteArray(n) : null);
      }
    });
    send({ev: 'hooked', base: m.base.toString()});
    return;
  }

  const origLoop = new NativeFunction(at('MergeLoop'), 'float',
      ['pointer', 'pointer', 'pointer', 'float', 'int', 'int']);
  Interceptor.replace(at('MergeLoop'), new NativeCallback(
    function (sampler, set, box, limit, budget, padded) {
      padded &= 0xff;
      const tid = Process.getCurrentThreadId();
      // --gen-box: only generation's merges of leaves with those minimums.
      const b = [];
      for (let k = 0; k < 6; k++) b.push(box.add(k * 4).readFloat());
      const boxed = GENBOX.length > 0 && padded && GENBOX.some(w => w[0] === b[0] && w[1] === b[1] && w[2] === b[2]);
      const want = GENBOX.length ? boxed : (GEN || !padded);
      let rec = null;
      if (want) {
        sendLeaves(sampler, set);
        const [n, blob] = dumpSet(set);
        rec = {id: seq++, pass, padded, limit, budget, box: b, n, merges: []};
        send({ev: 'in', id: rec.id, pass, padded, limit, budget, box: b, n, set: set.toString()}, blob);
        live[tid] = rec;
      }
      const ret = origLoop(sampler, set, box, limit, budget, padded);
      if (rec) {
        const [n, blob] = dumpSet(set);
        send({ev: 'out', id: rec.id, ret, n, merges: rec.merges}, blob);
        delete live[tid];
        if (boxed && ++genboxSeen >= GENBOX.length) send({ev: 'done'});
      }
      return ret;
    }, 'float', ['pointer', 'pointer', 'pointer', 'float', 'int', 'int']));

  Interceptor.attach(at('CheapestPair'), {
    onEnter(a) { this.o = a[1]; },
    onLeave() {
      const rec = live[Process.getCurrentThreadId()];
      if (rec) rec.merges.push(['c', this.o.readS32(), this.o.add(4).readS32(),
                                this.o.add(8).readFloat()]);
    }
  });
  Interceptor.attach(at('AbsorbPair'), {
    onEnter(a) {
      const rec = live[Process.getCurrentThreadId()];
      if (rec) rec.merges.push(['a', a[1].add(0x4c).readS32(), a[2].add(0x4c).readS32()]);
    }
  });

  // --rays: TallyRays' records (0x20 each) and the tracer's raw hits (0x38
  // each) for clusters centred at the given points, tagged with the pass
  // (-1 is cluster generation), as capture_rays.py records them.
  if (RAYS.length) Interceptor.attach(at('TallyRays'), {
    onEnter(a) { this.recs = a[1]; this.hits = a[2]; this.c = a[3]; },
    onLeave() {
      const c = [0, 4, 8].map(k => this.c.add(k).readFloat());
      if (!RAYS.some(w => w[0] === c[0] && w[1] === c[1] && w[2] === c[2])) return;
      const n = this.recs.readS32(), p = this.recs.add(8).readPointer();
      send({ev: 'rays', pass, centre: c, n}, p.readByteArray(n * 0x20));
      send({ev: 'rayhits', pass, centre: c, n}, this.hits.readByteArray(n * 0x38));
    }
  });
  if (VIS) Interceptor.attach(at('BuildCandidates'), {
    onEnter(a) { this.s = a[0]; },
    onLeave() {
      const rec = live[Process.getCurrentThreadId()];
      if (!rec || (VISN > 0 && rec.n !== VISN)) return;
      const s = this.s, n = s.add(8).readS32(), slots = s.add(0x10).readPointer();
      const parts = [];
      let total = 0;
      for (let i = 0; i < n; i++) {
        const slot = slots.add(i * 0x20);
        const words = slot.add(4).readS32();
        const data = words > 2 ? slot.add(8).readPointer() : slot.add(8);
        const cl = slot.add(0x10).readPointer();
        const cc = cl.isNull() ? 0 : cl.add(0x18).readS32();
        parts.push([data, words, cl, cc]);
        total += 8 + words * 4 + cc * 8;
      }
      const out = new Uint8Array(total), dv = new DataView(out.buffer);
      let at = 0;
      for (const [data, words, cl, cc] of parts) {
        dv.setInt32(at, words, true); dv.setInt32(at + 4, cc, true); at += 8;
        if (words) { out.set(new Uint8Array(data.readByteArray(words * 4)), at); at += words * 4; }
        if (cc) { out.set(new Uint8Array(cl.add(0x20).readPointer().readByteArray(cc * 8)), at); at += cc * 8; }
      }
      send({ev: 'vis', id: rec.id, slots: n}, out.buffer);
    }
  });
  send({ev: 'hooked', base: m.base.toString()});
}

const found = Process.findModuleByName('visbuilder.dll');
if (found) hook(found);
else Process.attachModuleObserver({
  onAdded(m) { if (m.name.toLowerCase() === 'visbuilder.dll') hook(m); }
});
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("addon")
    parser.add_argument("map")
    parser.add_argument("--out")
    parser.add_argument("--gen", action="store_true")
    parser.add_argument("--vis", action="store_true")
    parser.add_argument("--passes", action="store_true")
    parser.add_argument("--premerge", action="store_true")
    parser.add_argument("--vis-n", type=int, default=0, help="with --vis: only buckets handed this many clusters")
    parser.add_argument("--rays", default="", help="cluster centres 'x,y,z;x,y,z' whose rays to record (pass as --rays=...)")
    parser.add_argument("--gen-box", default="", help="leaf minimums 'x,y,z;x,y,z': only generation's merges there (pass as --gen-box=...); stops once all are in")
    parser.add_argument("--stop-after", type=int, default=-1, help="stop the compile when this pass returns")
    args = parser.parse_args()

    with open(os.path.join(ROOT, "docs", "visbuilder.signatures.json"), encoding="utf-8") as h:
        manifest = json.load(h)
    with open(os.path.join(BIN, "visbuilder.dll"), "rb") as h:
        image = h.read()
    rows = {r["name"]: r for r in resolve(image, manifest, set(HOOKED))}
    missing = [n for n in HOOKED if rows.get(n, {}).get("status") not in ("same", "moved")]
    if missing:
        sys.exit("unresolved: %s -- re-sign before capturing" % ", ".join(missing))
    rvas = {n: int(rows[n]["now"], 16) - IMAGE_BASE for n in HOOKED}
    sys.path.insert(0, os.path.join(ROOT, "tools", "re"))
    from rva_map import installed
    outside = installed("visbuilder", 0x33890)
    # DistancePreMerge, MergeBestCandidates and SortPairs in the analysis build.
    pm = {"pre": installed("visbuilder", 0x30b40), "merge": installed("visbuilder", 0x293c0),
          "sort": installed("visbuilder", 0x2a960)}

    out_path = args.out or os.path.join(
        os.environ.get("TEMP", "."), "vis_capture", "%s.bin" % args.map)
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    out = open(out_path, "wb")
    lock = threading.Lock()
    stats = {"in": 0, "out": 0, "vis": 0, "merges": 0}
    running = {}
    pending = []

    def on_message(message, data):
        if message["type"] != "send":
            print("agent:", message.get("description") or message, file=sys.stderr)
            return
        payload = message["payload"]
        ev = payload["ev"]
        if ev == "part":
            pending.append(data or b"")
            return
        if payload.get("parts"):
            data = b"".join(pending)
            pending.clear()
            del payload["parts"]
        if ev == "hooked":
            print("hooked visbuilder at", payload["base"])
        elif ev == "pass":
            print("pass", payload["pass"])
        stats[ev] = stats.get(ev, 0) + 1
        if ev == "out":
            stats["merges"] += sum(1 for m in payload["merges"] if m[0] == "a")
        if ev == "done" and "pid" in running:
            # --passes / --premerge: what was asked for is in; the rest of the build is not needed.
            subprocess.run(["taskkill", "/F", "/PID", str(running["pid"])], capture_output=True)
        head = json.dumps(payload).encode()
        blob = data or b""
        with lock:
            out.write(struct.pack("<I", len(head)) + head + struct.pack("<I", len(blob)) + blob)

    # A current VPK makes RC skip the phase and leave yesterday's output, so it
    # is moved aside and put back afterwards (other tools read it as Valve's).
    vpk = os.path.join(CS2, "game", "csgo_addons", args.addon, "maps", args.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
        os.remove(vpk)
    source = os.path.join(CS2, "content", "csgo_addons", args.addon, "maps", args.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4",
            "-game", os.path.join(CS2, "game", "csgo"), "-i", source,
            "-world", "-vis", "-fshallow"]

    device = frida.get_local_device()
    pid = device.spawn(argv, cwd=BIN, stdio="pipe")
    running["pid"] = pid
    session = device.attach(pid)
    script = session.create_script(AGENT % {
        "rvas": json.dumps(rvas), "gen": str(args.gen).lower(), "vis": str(args.vis).lower(),
        "passes": str(args.passes).lower(), "outside": outside,
        "premerge": str(args.premerge).lower(), "pm": json.dumps(pm),
        "visn": args.vis_n, "stop": args.stop_after,
        "rays": json.dumps([[float(v) for v in c.split(",")] for c in args.rays.split(";") if c]),
        "genbox": json.dumps([[float(v) for v in c.split(",")] for c in args.gen_box.split(";") if c])})
    script.on("message", on_message)
    script.load()

    done = threading.Event()
    log = []
    device.on("output", lambda p, fd, d: log.append(d.decode("utf-8", "replace")) if d else None)
    session.on("detached", lambda *a: done.set())
    started = time.time()
    device.resume(pid)
    done.wait()
    out.close()
    with open(out_path + ".log", "w", encoding="utf-8") as h:
        h.write("".join(log))
    print("%.1fs  %s  -> %s" % (time.time() - started, stats, out_path))
    while subprocess.run(["tasklist", "/FI", "PID eq %d" % pid], capture_output=True, text=True).stdout.count(str(pid)):
        time.sleep(1)
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        if filecmp.cmp(backup, vpk, shallow=False):
            os.remove(backup)
            print("restored", vpk)
        else:
            print("RESTORE CMP FAILED", vpk, "backup kept at", backup)
    # The scene this compile traced, kept beside the capture as <stem>.merge.rte
    # and .viscfg: the next compile of the map overwrites the one under
    # %TEMP%/csgo_addons, and the .rte is not byte-stable between compiles.
    stem = (out_path[:-len(".bin")] if out_path.endswith(".bin") else out_path) + ".merge"
    scene = os.path.join(os.environ.get("TEMP", "."), "csgo_addons", args.addon, "maps", args.map)
    for ext in (".rte", ".viscfg"):
        if os.path.exists(scene + ext):
            shutil.copyfile(scene + ext, stem + ext)
            print("->", stem + ext)


if __name__ == "__main__":
    main()
