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

Addresses come from docs/visbuilder.signatures.json through sigscan, so this
survives a game update the same way the tests do.

Output is a stream of records: u32 json length, json, u32 blob length, blob.
"""
import argparse
import json
import os
import struct
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
HOOKED = ["MergeLoop", "CheapestPair", "AbsorbPair", "BuildCandidates",
          "Regrid", "MergeClusterSet"]

AGENT = r"""
'use strict';
const RVA = %(rvas)s;
const GEN = %(gen)s, VIS = %(vis)s;
let pass = -1, seq = 0;
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

  Interceptor.attach(at('Regrid'), { onEnter() { pass++; send({ev: 'pass', pass}); } });

  const origLoop = new NativeFunction(at('MergeLoop'), 'float',
      ['pointer', 'pointer', 'pointer', 'float', 'int', 'int']);
  Interceptor.replace(at('MergeLoop'), new NativeCallback(
    function (sampler, set, box, limit, budget, padded) {
      padded &= 0xff;
      const tid = Process.getCurrentThreadId();
      const want = GEN || !padded;
      let rec = null;
      if (want) {
        const b = [];
        for (let k = 0; k < 6; k++) b.push(box.add(k * 4).readFloat());
        sendLeaves(sampler, set);
        const [n, blob] = dumpSet(set);
        rec = {id: seq++, pass, padded, limit, budget, box: b, n, merges: []};
        send({ev: 'in', id: rec.id, pass, padded, limit, budget, box: b, n}, blob);
        live[tid] = rec;
      }
      const ret = origLoop(sampler, set, box, limit, budget, padded);
      if (rec) {
        const [n, blob] = dumpSet(set);
        send({ev: 'out', id: rec.id, ret, n, merges: rec.merges}, blob);
        delete live[tid];
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

  if (VIS) Interceptor.attach(at('BuildCandidates'), {
    onEnter(a) { this.s = a[0]; },
    onLeave() {
      const rec = live[Process.getCurrentThreadId()];
      if (!rec) return;
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

    out_path = args.out or os.path.join(
        os.environ.get("TEMP", "."), "vis_capture", "%s.bin" % args.map)
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    out = open(out_path, "wb")
    lock = threading.Lock()
    stats = {"in": 0, "out": 0, "vis": 0, "merges": 0}

    def on_message(message, data):
        if message["type"] != "send":
            print("agent:", message.get("description") or message, file=sys.stderr)
            return
        payload = message["payload"]
        ev = payload["ev"]
        if ev == "hooked":
            print("hooked visbuilder at", payload["base"])
        elif ev == "pass":
            print("pass", payload["pass"])
        stats[ev] = stats.get(ev, 0) + 1
        if ev == "out":
            stats["merges"] += sum(1 for m in payload["merges"] if m[0] == "a")
        head = json.dumps(payload).encode()
        blob = data or b""
        with lock:
            out.write(struct.pack("<I", len(head)) + head + struct.pack("<I", len(blob)) + blob)

    # A current VPK makes RC skip the phase and leave yesterday's output.
    vpk = os.path.join(CS2, "game", "csgo_addons", args.addon, "maps", args.map + ".vpk")
    if os.path.exists(vpk):
        os.remove(vpk)
    source = os.path.join(CS2, "content", "csgo_addons", args.addon, "maps", args.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4",
            "-game", os.path.join(CS2, "game", "csgo"), "-i", source,
            "-world", "-vis", "-fshallow"]

    device = frida.get_local_device()
    pid = device.spawn(argv, cwd=BIN, stdio="pipe")
    session = device.attach(pid)
    script = session.create_script(AGENT % {
        "rvas": json.dumps(rvas), "gen": str(args.gen).lower(), "vis": str(args.vis).lower()})
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


if __name__ == "__main__":
    main()
