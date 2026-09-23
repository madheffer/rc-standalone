"""Record the post-assignment half of Valve's vis build from inside the compile.

    python capture_pvs.py <addon> <map> [--out file]

Writes %TEMP%/vis_capture/<map>.pvs.bin, the same record stream as
capture_merge.py (u32 json length, json, u32 blob length, blob):

  state      at scan entry: the flat entry array (16 bytes each), the node
             array (8 each), node boxes (24 each) and cluster boxes (24 each)
  neighbors  the finished CNeighboringClustersList: per cluster its i64
             accumulator, then its neighbour ids
  pass<k>    at each generator begin-pass: the matrix going in, then the pairs
             it produced (u32 pairs)
  after      the matrix after each generator's SamplerDriver returns, with its
             index in the order they ran
  matrix     the MutualVisibilityMatrix as the PVS scan leaves it: rows of
             ceil(n/32) u32 words, n = clusters + 2 (sky and sun)
  steps      the vis-cluster merge's inputs: the volume per cluster, grid size,
             the pre-merge's group volume and count (sampler +0x108, +0x110),
             the neighbour growth (+0xf0), and the per-cluster u16 pairs at +0x18
  built      the vis-cluster records as built: per record its neighbour ids,
             weight (u64) and box
  merges     every merge the vis-cluster merge made, (u32 lo, u32 hi) in order
  applied    the final cluster map (u32 each) and count, then the entries,
             cluster boxes and matrix once the sampler has taken it
  borders    what AdaptivelySampleBorders found: the border entry indices, and
             per border entry its (cluster, box) records (i32 count, then 28
             bytes each: box then cluster)
  resampled  the entries and nodes once the border stage has rewritten them
  assigned2  the entries and nodes once AssignClusters2 has consolidated them
  sky, sun   the clusters visible to sky and to the sun, one bit each, with
             whether the stage succeeded
  collapsed<k>  entries, nodes and node boxes after each collapse iteration

The functions hooked here are not in docs/visbuilder.signatures.json yet, so
their RVAs are pinned to one build and the tool refuses any other.
"""
import argparse
import hashlib
import json
import os
import struct
import sys
import threading

import frida

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
BUILD = "13f375272e9c99d3d1755013e9eaf1c5"
RVA = {
    "SampleVisForClusters": 0x37ee0,
    "NeighborsBuild": 0x1e7c0,
    "SamplerDriver": 0x19c40,
    "BeginPass": 0x1edc0,
    "ClustersSteps": 0x46120,
    "BuiltClusters": 0x45440,
    "MergePair": 0x42f00,
    "ApplyClusterMap": 0x38130,
    "SampleBorders": 0x3c3d0,
    "BorderBoxes": 0x3a1a0,
    "AssignClusters2": 0x38ed0,
    "Sky": 0x25f00,
    "Sun": 0x24d30,
    "Collapse": 0x39390,
}

AGENT = r"""
const RVA = %(rva)s;
function rows(m) {
  const n = m.readS32(), ptrs = m.add(8).readPointer(), bits = m.add(0x18).readS32();
  const words = (bits + 31) >> 5, out = new Uint8Array(n * words * 4);
  for (let i = 0; i < n; i++)
    out.set(new Uint8Array(ptrs.add(i * 8).readPointer().readByteArray(words * 4)), i * words * 4);
  return [n, bits, out.buffer];
}
function hook(m) {
  Interceptor.attach(m.base.add(RVA.NeighborsBuild), {
    onEnter(a) { this.l = a[0]; },
    onLeave() {
      const n = this.l.add(8).readS32(), recs = this.l.add(0x10).readPointer();
      let total = 0;
      for (let i = 0; i < n; i++) total += 12 + recs.add(i * 0x20 + 8).readS32() * 4;
      const out = new Uint8Array(total), dv = new DataView(out.buffer);
      let at = 0;
      for (let i = 0; i < n; i++) {
        const r = recs.add(i * 0x20), c = r.add(8).readS32();
        out.set(new Uint8Array(r.readByteArray(8)), at); dv.setInt32(at + 8, c, true); at += 12;
        if (c) { out.set(new Uint8Array(r.add(0x10).readPointer().readByteArray(c * 4)), at); at += c * 4; }
      }
      send({ev: 'neighbors', clusters: n}, out.buffer);
    }
  });
  let generator = 0, passes = 0;
  Interceptor.attach(m.base.add(RVA.BeginPass), {
    onEnter(a) {
      this.g = a[0];
      const [n, bits, blob] = rows(this.g.add(0x18).readPointer());
      send({ev: 'passin' + passes, generator, rows: n, bits}, blob);
    },
    onLeave() {
      const count = this.g.add(0x48).readS32();
      send({ev: 'pairs' + passes, generator, pairs: count},
           count > 0 ? this.g.add(0x50).readPointer().readByteArray(count * 8) : new ArrayBuffer(0));
      passes++;
    }
  });
  Interceptor.attach(m.base.add(RVA.SamplerDriver), {
    onEnter(a) { this.m = a[0].readPointer(); },
    onLeave() {
      const [n, bits, blob] = rows(this.m);
      send({ev: 'after' + generator, generator: generator++, rows: n, bits}, blob);
    }
  });
  Interceptor.attach(m.base.add(RVA.SampleVisForClusters), {
    onEnter(a) {
      this.s = a[0];
      const s = a[0];
      const ne = s.add(0x40).readS32(), nn = s.add(0x28).readS32(), nc = s.add(0x198).readS32();
      send({ev: 'entries', n: ne}, s.add(0x48).readPointer().readByteArray(ne * 16));
      send({ev: 'nodes', n: nn}, s.add(0x30).readPointer().readByteArray(nn * 8));
      send({ev: 'nodeboxes', n: nn}, s.add(0x78).readPointer().readByteArray(nn * 24));
      send({ev: 'clusterboxes', n: nc}, s.add(0x1a0).readPointer().readByteArray(nc * 24));
    },
    onLeave() {
      const [n, bits, blob] = rows(this.s.add(0x148));
      send({ev: 'matrix', rows: n, bits, clusters: this.s.add(0x198).readS32()}, blob);
    }
  });
  let merges = [];
  Interceptor.attach(m.base.add(RVA.ClustersSteps), {
    onEnter(a) {
      const s = a[2];
      const n = s.add(0x10).readS32();
      send({ev: 'steps', grid: this.context.rsp.add(0x30).readFloat(),
            limit: this.context.rsp.add(0x28).readDouble(),
            premergeVolume: s.add(0x108).readDouble(), premergeGroups: s.add(0x110).readU32(),
            growth: s.add(0xf0).readFloat(), infos: n},
           n > 0 ? s.add(0x18).readPointer().readByteArray(n * 4) : new ArrayBuffer(0));
    }
  });
  Interceptor.attach(m.base.add(RVA.BuiltClusters), {
    onEnter(a) { this.l = a[0]; },
    onLeave() {
      const n = this.l.add(0x20).readS32(), recs = this.l.add(0x28).readPointer();
      let total = 0;
      for (let i = 0; i < n; i++) total += 4 + recs.add(i * 0x58).readS32() * 4 + 8 + 24;
      const out = new Uint8Array(total), dv = new DataView(out.buffer);
      let at = 0;
      for (let i = 0; i < n; i++) {
        const r = recs.add(i * 0x58), c = r.readS32();
        dv.setInt32(at, c, true); at += 4;
        if (c) { out.set(new Uint8Array(r.add(8).readPointer().readByteArray(c * 4)), at); at += c * 4; }
        out.set(new Uint8Array(r.add(0x30).readByteArray(32)), at); at += 32;
      }
      send({ev: 'built', n}, out.buffer);
    }
  });
  Interceptor.attach(m.base.add(RVA.MergePair), {
    onEnter(a) { merges.push(a[1].toInt32() >>> 0, a[2].toInt32() >>> 0); }
  });
  Interceptor.attach(m.base.add(RVA.ApplyClusterMap), {
    onEnter(a) {
      this.s = a[0];
      send({ev: 'merges', n: merges.length / 2}, new Uint32Array(merges).buffer);
      merges = [];
      const list = a[0].add(0x1b0), count = list.add(8).readS32();
      send({ev: 'clustermap', n: count, total: a[2].toInt32()}, a[1].readByteArray(count * 4));
    },
    onLeave() {
      const s = this.s, ne = s.add(0x40).readS32(), nc = s.add(0x198).readS32();
      send({ev: 'appliedentries', n: ne}, s.add(0x48).readPointer().readByteArray(ne * 16));
      send({ev: 'appliedboxes', n: nc}, s.add(0x1a0).readPointer().readByteArray(nc * 24));
      const [rn, bits, blob] = rows(s.add(0x148));
      send({ev: 'appliedmatrix', rows: rn, bits}, blob);
    }
  });
  Interceptor.attach(m.base.add(RVA.BorderBoxes), {
    onEnter(a) {
      const b = a[0], n = b.readS32(), ids = b.add(8).readPointer();
      send({ev: 'borderentries', n}, n ? ids.readByteArray(n * 4) : new ArrayBuffer(0));
      const k = b.add(0x30).readS32(), lists = b.add(0x38).readPointer();
      let total = 0;
      for (let i = 0; i < k; i++) total += 4 + lists.add(i * 0x18).readS32() * 0x1c;
      const out = new Uint8Array(total), dv = new DataView(out.buffer);
      let at = 0;
      for (let i = 0; i < k; i++) {
        const c = lists.add(i * 0x18).readS32();
        dv.setInt32(at, c, true); at += 4;
        if (c) { out.set(new Uint8Array(lists.add(i * 0x18 + 8).readPointer().readByteArray(c * 0x1c)), at); at += c * 0x1c; }
      }
      send({ev: 'borders', n: k}, out.buffer);
    }
  });
  Interceptor.attach(m.base.add(RVA.SampleBorders), {
    onEnter(a) { this.s = a[0]; },
    onLeave() {
      const s = this.s, ne = s.add(0x40).readS32(), nn = s.add(0x28).readS32();
      send({ev: 'resampledentries', n: ne}, s.add(0x48).readPointer().readByteArray(ne * 16));
      send({ev: 'resamplednodes', n: nn}, s.add(0x30).readPointer().readByteArray(nn * 8));
    }
  });
  Interceptor.attach(m.base.add(RVA.AssignClusters2), {
    onEnter(a) { this.s = a[0]; },
    onLeave() {
      const s = this.s, ne = s.add(0x40).readS32(), nn = s.add(0x28).readS32();
      send({ev: 'assigned2entries', n: ne}, s.add(0x48).readPointer().readByteArray(ne * 16));
      send({ev: 'assigned2nodes', n: nn}, s.add(0x30).readPointer().readByteArray(nn * 8));
    }
  });
  for (const [name, ev] of [['Sky', 'sky'], ['Sun', 'sun']]) {
    Interceptor.attach(m.base.add(RVA[name]), {
      onEnter(a) { this.v = a[0]; },
      onLeave(r) {
        const n = this.v.readS32();
        send({ev, words: n, ok: r.toInt32() & 0xff},
             n > 0 ? this.v.add(8).readPointer().readByteArray(n * 4) : new ArrayBuffer(0));
      }
    });
  }
  let collapses = 0;
  Interceptor.attach(m.base.add(RVA.Collapse), {
    onEnter(a) { this.s = a[0]; },
    onLeave() {
      const s = this.s, ne = s.add(0x40).readS32(), nn = s.add(0x28).readS32(), k = collapses++;
      send({ev: 'collapsedentries' + k, n: ne}, s.add(0x48).readPointer().readByteArray(ne * 16));
      send({ev: 'collapsednodes' + k, n: nn}, s.add(0x30).readPointer().readByteArray(nn * 8));
      send({ev: 'collapsedboxes' + k, n: nn}, s.add(0x78).readPointer().readByteArray(nn * 24));
    }
  });
  send({ev: 'hooked'});
}
const f = Process.findModuleByName('visbuilder.dll');
if (f) hook(f); else Process.attachModuleObserver({onAdded(m) {
  if (m.name.toLowerCase() === 'visbuilder.dll') hook(m); }});
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("addon")
    parser.add_argument("map")
    parser.add_argument("--out")
    args = parser.parse_args()

    with open(os.path.join(BIN, "visbuilder.dll"), "rb") as h:
        if hashlib.md5(h.read()).hexdigest() != BUILD:
            sys.exit("visbuilder.dll is not the build these RVAs were read from; sign them first")

    out_path = args.out or os.path.join(os.environ.get("TEMP", "."), "vis_capture", "%s.pvs.bin" % args.map)
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    out = open(out_path, "wb")
    lock = threading.Lock()

    def on_message(message, data):
        if message["type"] != "send":
            print("agent:", message.get("description") or message, file=sys.stderr)
            return
        head = json.dumps(message["payload"]).encode()
        print(message["payload"])
        with lock:
            blob = data or b""
            out.write(struct.pack("<I", len(head)) + head + struct.pack("<I", len(blob)) + blob)

    vpk = os.path.join(CS2, "game", "csgo_addons", args.addon, "maps", args.map + ".vpk")
    if os.path.exists(vpk):
        os.remove(vpk)
    source = os.path.join(CS2, "content", "csgo_addons", args.addon, "maps", args.map + ".vmap")
    device = frida.get_local_device()
    pid = device.spawn([os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game",
                        os.path.join(CS2, "game", "csgo"), "-i", source,
                        "-world", "-vis", "-fshallow"], cwd=BIN, stdio="pipe")
    session = device.attach(pid)
    script = session.create_script(AGENT % {"rva": json.dumps(RVA)})
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
