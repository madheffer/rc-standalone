"""Record the post-assignment half of Valve's vis build from inside the compile.

    python capture_pvs.py <addon> <map> [--out file]

Writes %TEMP%/vis_capture/<map>.pvs.bin, the same record stream as
capture_merge.py (u32 json length, json, u32 blob length, blob):

  state      at scan entry: the flat entry array (16 bytes each), the node
             array (8 each), node boxes (24 each) and cluster boxes (24 each)
  neighbors  the finished CNeighboringClustersList: per cluster its i64
             accumulator, then its neighbour ids
  pass<k>    at each generator begin-pass: the pairs it produced (u32 pairs),
             and with --passin the matrix going in
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
  flatboxes  FlatVisClusterVector as the border stage leaves it: per cluster
             an i32 count, then that many 24 byte boxes
  mutualvis  MutualVisibilityMatrix as exported for the world renderer: per row
             an i32 count (N), then N floats

The hooked functions are found through docs/visbuilder.signatures.json, so the
tool follows a game update that moves them and refuses one that changes them.
"""
import argparse
import json
import os
import shutil
import struct
import sys
import threading

import frida

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from package_guard import PackageGuard  # noqa: E402

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
# Hook name -> symbol in docs/visbuilder.signatures.json. The addresses are
# resolved from the installed DLL at run time, so a game update that only moves
# code needs nothing here; one that changes a hooked function reports it.
# The structure offsets read inside the hooks are still those of the
# 2026-09-23 build and must be re-checked by hand if a layout changes.
HOOKS = {
    "SampleVisForClusters": "SampleVisForClusters",
    "NeighborsBuild": "NeighborsBuild",
    "SamplerDriver": "SamplerDriver",
    "BeginPass": "BeginPass",
    "ClustersSteps": "ClustersSteps",
    "BuiltClusters": "BuiltClusters",
    "MergePair": "MergePair",
    "ApplyClusterMap": "ApplyClusterMap",
    "SampleBorders": "SampleBorders",
    "BorderBoxes": "BorderBoxes",
    "AssignClusters2": "AssignClusters2",
    "Sky": "SkyVisibility",
    "Sun": "SunVisibility",
    "Collapse": "CollapseOnce",
    "ExportMatrix": "ExportMutualVisibility",
    "BorderTrace": "BorderTrace",
}


def resolve_hooks(dll):
    """RVAs for HOOKS from the signature manifest; exits naming any that do not resolve."""
    here = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    sys.path.insert(0, os.path.join(here, "tools"))
    import sigscan
    with open(os.path.join(here, "docs", "visbuilder.signatures.json"), encoding="utf-8") as handle:
        manifest = json.load(handle)
    with open(dll, "rb") as handle:
        image = handle.read()
    base, _ = sigscan.sections(image)
    rows = {r["name"]: r for r in sigscan.resolve(image, manifest, set(HOOKS.values()))}
    lost = [name for name in HOOKS.values() if rows.get(name, {}).get("status") not in ("same", "moved")]
    if lost:
        sys.exit("these hooked functions no longer resolve, re-sign them first: " + ", ".join(lost))
    return {hook: int(rows[name]["now"], 16) - base for hook, name in HOOKS.items()}

AGENT = r"""
const RVA = %(rva)s;
const PASSIN = %(passin)s;
// Frida drops the session on a message over 128 MiB, and a big map's matrix is
// larger than that, so blobs go in parts the host puts back together.
const PART = 48 * 1024 * 1024;
function sendBlob(head, buf) {
  const n = Math.max(1, Math.ceil(buf.byteLength / PART));
  if (n === 1) { send(head, buf); return; }
  for (let k = 0; k < n; k++)
    send(Object.assign({}, head, {part: k, parts: n}), buf.slice(k * PART, Math.min(buf.byteLength, (k + 1) * PART)));
}
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
      sendBlob({ev: 'neighbors', clusters: n}, out.buffer);
    }
  });
  let generator = 0, passes = 0;
  Interceptor.attach(m.base.add(RVA.BeginPass), {
    onEnter(a) {
      this.g = a[0];
      if (PASSIN) {
        const [n, bits, blob] = rows(this.g.add(0x18).readPointer());
        sendBlob({ev: 'passin' + passes, generator, rows: n, bits}, blob);
      }
    },
    onLeave() {
      const count = this.g.add(0x48).readS32();
      sendBlob({ev: 'pairs' + passes, generator, pairs: count},
           count > 0 ? this.g.add(0x50).readPointer().readByteArray(count * 8) : new ArrayBuffer(0));
      passes++;
    }
  });
  Interceptor.attach(m.base.add(RVA.SamplerDriver), {
    onEnter(a) { this.m = a[0].readPointer(); },
    onLeave() {
      const [n, bits, blob] = rows(this.m);
      sendBlob({ev: 'after' + generator, generator: generator++, rows: n, bits}, blob);
    }
  });
  Interceptor.attach(m.base.add(RVA.SampleVisForClusters), {
    onEnter(a) {
      this.s = a[0];
      const s = a[0];
      const ne = s.add(0x40).readS32(), nn = s.add(0x28).readS32(), nc = s.add(0x198).readS32();
      sendBlob({ev: 'entries', n: ne}, s.add(0x48).readPointer().readByteArray(ne * 16));
      sendBlob({ev: 'nodes', n: nn}, s.add(0x30).readPointer().readByteArray(nn * 8));
      sendBlob({ev: 'nodeboxes', n: nn}, s.add(0x78).readPointer().readByteArray(nn * 24));
      send({ev: 'clusterboxes', n: nc}, s.add(0x1a0).readPointer().readByteArray(nc * 24));
    },
    onLeave() {
      const [n, bits, blob] = rows(this.s.add(0x148));
      sendBlob({ev: 'matrix', rows: n, bits, clusters: this.s.add(0x198).readS32()}, blob);
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
      sendBlob({ev: 'built', n}, out.buffer);
    }
  });
  Interceptor.attach(m.base.add(RVA.MergePair), {
    onEnter(a) { merges.push(a[1].toInt32() >>> 0, a[2].toInt32() >>> 0); }
  });
  // Once per merge step: a big map merges in steps of 8,192 before the final
  // one. Each step is recorded under its index, and the plain name holds the last.
  let applies = 0;
  Interceptor.attach(m.base.add(RVA.ApplyClusterMap), {
    onEnter(a) {
      this.s = a[0];
      this.k = applies++;
      const list = a[0].add(0x1b0), count = list.add(8).readS32();
      for (const ev of ['merges' + this.k, 'merges'])
        sendBlob({ev, n: merges.length / 2}, new Uint32Array(merges).buffer);
      merges = [];
      for (const ev of ['clustermap' + this.k, 'clustermap'])
        send({ev, n: count, total: a[2].toInt32()}, a[1].readByteArray(count * 4));
    },
    onLeave() {
      const s = this.s, ne = s.add(0x40).readS32(), nc = s.add(0x198).readS32(), k = this.k;
      sendBlob({ev: 'appliedentries', n: ne}, s.add(0x48).readPointer().readByteArray(ne * 16));
      send({ev: 'appliedboxes', n: nc}, s.add(0x1a0).readPointer().readByteArray(nc * 24));
      const [rn, bits, blob] = rows(s.add(0x148));
      sendBlob({ev: 'appliedmatrix', rows: rn, bits}, blob);
      send({ev: 'applied' + k, entries: ne, clusters: nc});
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
      sendBlob({ev: 'borders', n: k}, out.buffer);
    }
  });
  // The named blocks live in the vis compile's context and are fetched the
  // way the compile fetches them, through the context's own getter.
  function named(ctx, name) {
    const get = new NativeFunction(ctx.readPointer().add(0x80).readPointer(), 'pointer', ['pointer', 'pointer', 'int']);
    return get(ctx, Memory.allocUtf8String(name), 0);
  }
  function vectors(v, stride) {
    const n = v.readS32(), items = v.add(8).readPointer();
    let total = 0;
    for (let i = 0; i < n; i++) total += 4 + items.add(i * 0x18).readS32() * stride;
    const out = new Uint8Array(total), dv = new DataView(out.buffer);
    let at = 0;
    for (let i = 0; i < n; i++) {
      const c = items.add(i * 0x18).readS32();
      dv.setInt32(at, c, true); at += 4;
      if (c) { out.set(new Uint8Array(items.add(i * 0x18 + 8).readPointer().readByteArray(c * stride)), at); at += c * stride; }
    }
    return [n, out.buffer];
  }
  Interceptor.attach(m.base.add(RVA.ExportMatrix), {
    onEnter(a) { this.ctx = a[1]; },
    onLeave() {
      const v = named(this.ctx, 'MutualVisibilityMatrix');
      if (v.isNull()) return;
      const [n, blob] = vectors(v, 4);
      sendBlob({ev: 'mutualvis', n}, blob);
    }
  });
  Interceptor.attach(m.base.add(RVA.SampleBorders), {
    onEnter(a) { this.s = a[0]; this.ctx = a[1]; },
    onLeave() {
      const flat = named(this.ctx, 'FlatVisClusterVector');
      if (!flat.isNull()) {
        const [n, blob] = vectors(flat, 24);
        sendBlob({ev: 'flatboxes', n}, blob);
      }
      const s = this.s, ne = s.add(0x40).readS32(), nn = s.add(0x28).readS32();
      sendBlob({ev: 'resampledentries', n: ne}, s.add(0x48).readPointer().readByteArray(ne * 16));
      sendBlob({ev: 'resamplednodes', n: nn}, s.add(0x30).readPointer().readByteArray(nn * 8));
    }
  });
  Interceptor.attach(m.base.add(RVA.AssignClusters2), {
    onEnter(a) { this.s = a[0]; },
    onLeave() {
      const s = this.s, ne = s.add(0x40).readS32(), nn = s.add(0x28).readS32();
      sendBlob({ev: 'assigned2entries', n: ne}, s.add(0x48).readPointer().readByteArray(ne * 16));
      sendBlob({ev: 'assigned2nodes', n: nn}, s.add(0x30).readPointer().readByteArray(nn * 8));
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
      sendBlob({ev: 'collapsedentries' + k, n: ne}, s.add(0x48).readPointer().readByteArray(ne * 16));
      sendBlob({ev: 'collapsednodes' + k, n: nn}, s.add(0x30).readPointer().readByteArray(nn * 8));
      sendBlob({ev: 'collapsedboxes' + k, n: nn}, s.add(0x78).readPointer().readByteArray(nn * 24));
    }
  });
  // The tracer the compile rebuilt, taken once at the first border trace:
  // its kd nodes (u32 word, f32 split or count), the leaf index list, and
  // every slot's 0x30 byte record through the paged array (+0x80 shift,
  // +0x84 mask, +0x90 pages of 16 bytes, the base at +8), plus its flag
  // word and box.
  const tracerHook = Interceptor.attach(m.base.add(RVA.BorderTrace), {
    onEnter(a) {
      tracerHook.detach();
      const p = a[0], nodes = p.add(0x68).readPointer();
      let maxNode = 0, maxIndex = 0;
      const stack = [0];
      while (stack.length) {
        const i = stack.pop();
        if (i > maxNode) maxNode = i;
        const w = nodes.add(i * 8).readU32();
        if ((w & 3) === 3) {
          const end = (w >> 2) + nodes.add(i * 8 + 4).readU32();
          if (end > maxIndex) maxIndex = end;
        } else {
          stack.push(w >>> 2, (w >>> 2) + 1);
        }
      }
      const index = p.add(0xa8).readPointer();
      const list = new Uint32Array(index.readByteArray(maxIndex * 4));
      let slots = 0;
      for (const v of list) if (v + 1 > slots) slots = v + 1;
      const shift = p.add(0x80).readU32(), mask = p.add(0x84).readU32(), pages = p.add(0x90).readPointer();
      const out = new Uint8Array(slots * 0x30);
      for (let s = 0; s < slots; s += mask + 1) {
        const n = Math.min(mask + 1, slots - s);
        out.set(new Uint8Array(pages.add((s >>> shift) * 16 + 8).readPointer().readByteArray(n * 0x30)), s * 0x30);
      }
      sendBlob({ev: 'kdnodes', n: maxNode + 1, flags: p.readU32()}, nodes.readByteArray((maxNode + 1) * 8));
      sendBlob({ev: 'kdindex', n: maxIndex}, list.buffer);
      sendBlob({ev: 'kdrecords', n: slots}, out.buffer);
      send({ev: 'kdbox'}, p.add(4).readByteArray(24));
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
    parser.add_argument("--passin", action="store_true", help="also record the matrix going into every pass")
    args = parser.parse_args()

    rva = resolve_hooks(os.path.join(BIN, "visbuilder.dll"))

    out_path = args.out or os.path.join(os.environ.get("TEMP", "."), "vis_capture", "%s.pvs.bin" % args.map)
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    out = open(out_path, "wb")
    lock = threading.Lock()

    pending = {}

    def on_message(message, data):
        if message["type"] != "send":
            print("agent:", message.get("description") or message, file=sys.stderr, flush=True)
            return
        payload = dict(message["payload"])
        blob = data or b""
        if "parts" in payload:
            parts = pending.setdefault(payload["ev"], [])
            parts.append(blob)
            if len(parts) < payload["parts"]:
                return
            blob = b"".join(pending.pop(payload["ev"]))
            del payload["part"], payload["parts"]
        head = json.dumps(payload).encode()
        print(payload, flush=True)
        with lock:
            out.write(struct.pack("<I", len(head)) + head + struct.pack("<I", len(blob)) + blob)

    vpk = os.path.join(CS2, "game", "csgo_addons", args.addon, "maps", args.map + ".vpk")
    # Moved aside (a current package makes RC skip the compile) and put back,
    # byte checked, once the compile has exited (tools/package_guard.py).
    with PackageGuard(vpk, out_path + ".vpk.bak") as guard:
        source = os.path.join(CS2, "content", "csgo_addons", args.addon, "maps", args.map + ".vmap")
        device = frida.get_local_device()
        pid = device.spawn([os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game",
                            os.path.join(CS2, "game", "csgo"), "-i", source,
                            "-world", "-vis", "-fshallow"], cwd=BIN, stdio="pipe")
        guard.pid = pid
        session = device.attach(pid)
        script = session.create_script(AGENT % {"rva": json.dumps(rva), "passin": "true" if args.passin else "false"})
        script.on("message", on_message)
        script.load()
        done = threading.Event()
        session.on("detached", lambda *a: done.set())
        device.resume(pid)
        done.wait()
        out.close()
        print("->", out_path)
        # The scene this compile traced. The .rte is not byte-stable between
        # compiles and the next compile of the map overwrites it, so a replay must
        # read this copy, not the one under %TEMP%/csgo_addons.
        stem = out_path[:-len(".pvs.bin")] if out_path.endswith(".pvs.bin") else out_path
        scene = os.path.join(os.environ.get("TEMP", "."), "csgo_addons", args.addon, "maps", args.map)
        for ext in (".rte", ".viscfg"):
            if os.path.exists(scene + ext):
                shutil.copyfile(scene + ext, stem + ext)
                print("->", stem + ext)


if __name__ == "__main__":
    main()
