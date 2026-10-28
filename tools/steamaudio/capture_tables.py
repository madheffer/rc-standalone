"""capture_tables.py <capture_dir> <out_dir>: the reverb kernel's inputs at its first call, from a replay_phonon.py
run under Frida (this machine's python process, not the game).

phonon 0923's AVX2 simulateEnergyField (180201a50) takes (scene, simulator, start, end, threadIndex, energyFields,
numBands). At the first call this writes, to <out_dir>:
  simulator.bin   the ispc EmbreeReflectionSimulator struct (0x80 bytes) as it is;
  listener.bin    *simulator.listener (0x40: right, up, ahead, origin as float<3> padded to 16);
  sources.bin     simulator.sources[0..numSources) the same way, directivities.bin (0x18 each);
  listener_samples.bin, diffuse_samples.bin, listener_coeffs.bin
                  the float tables behind listenerSamples[3], diffuseSamples[3], listenerCoeffs[(order+1)^2],
                  maxNumRays / numDiffuseSamples floats per row;
  scene.bin       the ispc EmbreeScene (0x18) and materials.bin: materialsForGeometry[0][0..8) as
                  (absorption*, scattering, transmission*) with the absorption floats;
  energyfield.bin energyFields[0] (0x20: numChannels, numBins, binDuration, subBins, data***, subBinData**);
  args.json       the scalar arguments of every call (start, end, threadIndex, numBands).
The struct layout is the one embree_reflection_simulator.ispc declares (4.8.1); the dump shows whether 4.9 agrees.
"""
import argparse
import json
import os
import struct
import sys
import threading

import frida

HERE = os.path.dirname(os.path.abspath(__file__))
KERNEL = 0x201a50

AGENT = r"""
let first = true;
function hook(m) {
  Interceptor.attach(m.base.add(KERNEL), {
    onEnter(args) {
      send({op: 'args', start: args[2].toInt32(), end: args[3].toInt32(), thread: args[4].toInt32(),
            bands: this.context.rsp.add(0x38).readS32()});
      if (!first) return;
      first = false;
      const sim = args[1];
      send({op: 'file', name: 'simulator.bin'}, sim.readByteArray(0x80));
      const maxRays = sim.add(4).readS32(), nDiffuse = sim.add(8).readS32(), maxOrder = sim.add(0x10).readS32();
      const numSources = sim.add(0x18).readS32();
      send({op: 'file', name: 'listener.bin'}, sim.add(0x28).readPointer().readByteArray(0x40));
      send({op: 'file', name: 'sources.bin'}, sim.add(0x20).readPointer().readByteArray(0x40 * numSources));
      send({op: 'file', name: 'directivities.bin'}, sim.add(0x30).readPointer().readByteArray(0x18 * numSources));
      function rows(p, n, len) {
        const parts = [];
        for (let i = 0; i < n; i++) parts.push(p.add(8 * i).readPointer().readByteArray(4 * len));
        return concat(parts);
      }
      const nc = (maxOrder + 1) * (maxOrder + 1);
      send({op: 'file', name: 'listener_samples.bin'}, rows(sim.add(0x50).readPointer(), 3, maxRays));
      send({op: 'file', name: 'diffuse_samples.bin'}, rows(sim.add(0x58).readPointer(), 3, nDiffuse));
      send({op: 'file', name: 'listener_coeffs.bin'}, rows(sim.add(0x60).readPointer(), nc, maxRays));
      const scene = args[0];
      send({op: 'file', name: 'scene.bin'}, scene.readByteArray(0x18));
      const mats = scene.add(8).readPointer().readPointer();
      const out = [];
      for (let i = 0; i < 8; i++) {
        const mat = mats.add(0x18 * i);
        out.push(mat.readByteArray(0x18));
        out.push(mat.readPointer().readByteArray(12));
        out.push(mat.add(0x10).readPointer().readByteArray(12));
      }
      send({op: 'file', name: 'materials.bin'}, concat(out));
      const ef = args[5];
      send({op: 'file', name: 'energyfield.bin'}, ef.readByteArray(0x20));
      send({op: 'meta', maxRays, nDiffuse, maxOrder, numSources});
    }
  });
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
    p.add_argument("out_dir")
    a = p.parse_args()
    os.makedirs(a.out_dir, exist_ok=True)
    calls = []

    def on_message(msg, data):
        if msg.get("type") != "send":
            print(msg, flush=True)
            return
        pay = msg["payload"]
        if pay.get("op") == "file":
            open(os.path.join(a.out_dir, pay["name"]), "wb").write(data)
        elif pay.get("op") == "args":
            calls.append([pay["start"], pay["end"], pay["thread"], pay["bands"]])
        else:
            print(pay, flush=True)

    dev = frida.get_local_device()
    pid = dev.spawn([sys.executable, os.path.join(HERE, "replay_phonon.py"), a.capture_dir], cwd=HERE)
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT.replace("KERNEL", hex(KERNEL)))
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    done.wait()
    json.dump(calls, open(os.path.join(a.out_dir, "args.json"), "w"))
    print(len(calls), "kernel calls; first", calls[:3], "->", a.out_dir)


if __name__ == "__main__":
    main()
