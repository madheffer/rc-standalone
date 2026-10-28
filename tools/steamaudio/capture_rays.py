"""capture_rays.py <capture_dir> <out.bin> [--calls N]: Embree's answers to the reverb kernel's ray packets, from a
replay_phonon.py run (this machine's python process, not the game) under Frida.

phonon 0923's reverb kernel (180201a50, the AVX2 simulateEnergyField) traces through two Embree 4 packet entry
points: 180b40a40 (rtcIntersect8: valid*, scene, RTCRayHit8*, args*) and 180b418e0 (rtcOccluded8: valid*, scene,
RTCRay8*, args*). This records the first --calls (default 20000) of each, in call order, as records of

    u8 kind (0 intersect, 1 occluded), u8[3] 0, i32 valid[8],
    RTCRay8 before (12 x 8 words: org xyz, tnear, dir xyz, time, tfar, mask, id, flags),
    RTCHit8 before and after (intersect only: Ng xyz, u, v, primID, geomID, instID: 8 x 8 words),
    RTCRay8 after.

The replay's mesh is the one Valve's compile handed phonon, so these are the ground truth for our intersector.
"""
import argparse
import os
import struct
import subprocess
import sys
import threading

import frida

HERE = os.path.dirname(os.path.abspath(__file__))
INTERSECT8, OCCLUDED8 = 0xb40a40, 0xb418e0

AGENT = r"""
const LIMIT = LIMIT_VALUE;
const counts = [0, 0];
function hook(m) {
  for (const [kind, rva] of [[0, INTERSECT], [1, OCCLUDED]]) {
    Interceptor.attach(m.base.add(rva), {
      onEnter(args) {
        this.go = counts[kind]++ < LIMIT;
        if (!this.go) return;
        this.valid = args[0]; this.ray = args[2];
        this.before = args[2].readByteArray(kind === 0 ? 384 + 256 : 384);
        this.mask = args[0].readByteArray(32);
      },
      onLeave() {
        if (!this.go) return;
        const after = this.ray.readByteArray(kind === 0 ? 384 + 256 : 384);
        send({kind}, concat([this.mask, this.before, after]));
      }
    });
  }
  send({hooked: m.base.toString()});
}
function concat(parts) {
  const n = parts.reduce((a, p) => a + p.byteLength, 0), out = new Uint8Array(n);
  let o = 0;
  for (const p of parts) { out.set(new Uint8Array(p), o); o += p.byteLength; }
  return out.buffer;
}
const m0 = Process.findModuleByName('phonon.dll');
if (m0) hook(m0);
else Process.attachModuleObserver({ onAdded(m) { if (m.name.toLowerCase() === 'phonon.dll') hook(m); } });
"""


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("capture_dir")
    p.add_argument("out")
    p.add_argument("--calls", type=int, default=20000)
    a = p.parse_args()
    out = open(a.out, "wb")
    lock = threading.Lock()
    n = [0, 0]

    def on_message(msg, data):
        if msg.get("type") != "send":
            print(msg, flush=True)
            return
        pay = msg["payload"]
        if "kind" in pay:
            with lock:
                out.write(struct.pack("<B3x", pay["kind"]) + data)
                n[pay["kind"]] += 1
            return
        print(pay, flush=True)

    dev = frida.get_local_device()
    pid = dev.spawn([sys.executable, os.path.join(HERE, "replay_phonon.py"), a.capture_dir], cwd=HERE)
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT.replace("LIMIT_VALUE", str(a.calls)).replace("INTERSECT", hex(INTERSECT8)).replace("OCCLUDED", hex(OCCLUDED8)))
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    done.wait()
    out.close()
    print("intersect8", n[0], "occluded8", n[1], "->", a.out)


if __name__ == "__main__":
    main()
