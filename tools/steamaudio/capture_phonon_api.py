"""capture_phonon_api.py <addon> <map> <out_dir> [--seed N] [--threads N] [--paths]: every Steam Audio API call
resourcecompiler makes while it bakes <map> (-world -fshallow -sareverb, and -sapaths), with its inputs.

Hooks phonon.dll's exports (the ipl* functions steamaudio.dll imports) and writes <out_dir>/api.jsonl, one line per
call: name, the caller as an RVA in steamaudio.dll, the first six argument words, and for the calls whose inputs
make the bake a raw dump of the settings struct they point at (STRUCTS, sizes from phonon.h 4.8.1 rounded up; the
4.9.0 layouts are read from these dumps and steamaudio.dll's call sites, not assumed). Static mesh arrays go to
<out_dir>/mesh_<n>_{vertices,triangles,materialindices,materials}.bin. Float arguments travel in xmm
registers, which a hook does not see: read them at the call site. A call is named by the steamaudio.dll import slot
it goes through, since phonon folds several exports onto one address (a tail call shows as '<export>?'). Ray seeds
are pinned as capture_sareverb.py does, so the bake is Valve's single-threaded, seed-pinned output.

A compile overwrites the map's .vpk. CS2 must be closed; run it through tools/re/heavy.py.
"""
import argparse
import base64
import json
import os
import subprocess
import sys
import threading

import frida

sys.path.insert(0, os.path.dirname(__file__))
from capture_sareverb import BIN, CS2, RDTSC  # noqa: E402

# name -> (argument index of the struct pointer, bytes to dump)
STRUCTS = {
    "iplContextCreate": (0, 0x40),
    "iplEmbreeDeviceCreate": (1, 0x10),
    "iplSceneCreate": (1, 0x60),
    "iplStaticMeshCreate": (1, 0x40),
    "iplProbeArrayGenerateProbes": (2, 0x60),
    "iplProbeBatchAddProbe": (1, 0x10),
    "iplProbeArraySetProbe": (2, 0x10),
    "iplReflectionsBakerBake": (1, 0x80),
    "iplPathBakerBake": (1, 0x80),
    "iplProbeBatchCreate": (0, 0x10),
    "iplSimulatorCreate": (1, 0xc0),
}

AGENT = r"""
const SEED = SEED_VALUE, SITES = SITES_VALUE, STRUCTS = STRUCTS_VALUE, SLOTS = SLOTS_VALUE;
const WANT = Object.values(SLOTS);
// phonon folds identical functions (one address for several exports), so the export a call goes through is the
// steamaudio.dll import slot its call instruction reads (call qword ptr [rip + disp32], FF 15).
function slotName(ret, fallback) {
  try {
    if (sa === null) sa = Process.findModuleByName('steamaudio.dll');
    const at = ret.sub(6);
    if (sa && at.readU8() === 0xff && at.add(1).readU8() === 0x15) {
      const slot = ret.add(at.add(2).readS32()).sub(sa.base).toInt32();
      if (SLOTS[slot] !== undefined) return SLOTS[slot];
    }
  } catch (e) {}
  return fallback + '?';
}
let sa = null, meshes = 0, saves = 0;
function rva(p) {
  if (sa === null) sa = Process.findModuleByName('steamaudio.dll');
  if (sa && p.compare(sa.base) >= 0 && p.compare(sa.base.add(sa.size)) < 0) return 'sa+' + p.sub(sa.base).toString(16);
  const m = Process.findModuleByAddress(p);
  return m ? m.name + '+' + p.sub(m.base).toString(16) : p.toString();
}
function dump(p, n) { try { return p.isNull() ? null : _b64(p.readByteArray(n)); } catch (e) { return 'unreadable'; } }
function _b64(buf) {
  const u = new Uint8Array(buf), c = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
  let s = '';
  for (let i = 0; i < u.length; i += 3) {
    const a = u[i], b = i + 1 < u.length ? u[i + 1] : 0, d = i + 2 < u.length ? u[i + 2] : 0;
    s += c[a >> 2] + c[((a & 3) << 4) | (b >> 4)] + (i + 1 < u.length ? c[((b & 15) << 2) | (d >> 6)] : '=') + (i + 2 < u.length ? c[d & 63] : '=');
  }
  return s;
}
function hook(m) {
  for (const rv of SITES) {
    const at = m.base.add(rv);
    if (at.readU16() !== 0x310f) { send({error: 'not rdtsc at ' + rv.toString(16)}); continue; }
    Interceptor.attach(at.add(2), function () { this.context.rax = ptr(SEED); this.context.rdx = ptr(0); });
  }
  const seen = {};
  for (const e of m.enumerateExports()) {
    if (e.type !== 'function' || !WANT.includes(e.name)) continue;
    const exportName = e.name, key = e.address.toString();
    if (seen[key]) continue;
    seen[key] = exportName;
    try { Interceptor.attach(e.address, {
      onEnter(args) {
        const name = slotName(this.returnAddress, exportName);
        const rec = {op: 'call', name, caller: rva(this.returnAddress), tid: this.threadId,
                     args: [0, 1, 2, 3, 4, 5].map(i => args[i].toString())};
        const s = STRUCTS[name];
        if (s) rec.struct = dump(args[s[0]], s[1]);
        if (name === 'iplStaticMeshCreate') {
          const st = args[1];
          const nv = st.readS32(), nt = st.add(4).readS32(), nm = st.add(8).readS32();
          rec.mesh = meshes;
          send({op: 'blob', file: 'mesh_' + meshes + '_vertices.bin'}, st.add(16).readPointer().readByteArray(nv * 12));
          send({op: 'blob', file: 'mesh_' + meshes + '_triangles.bin'}, st.add(24).readPointer().readByteArray(nt * 12));
          send({op: 'blob', file: 'mesh_' + meshes + '_materialindices.bin'}, st.add(32).readPointer().readByteArray(nt * 4));
          send({op: 'blob', file: 'mesh_' + meshes + '_materials.bin'}, st.add(40).readPointer().readByteArray(nm * 28));
          meshes++;
        }
        if (name === 'iplContextSetVariableInt32' || name === 'iplContextSetVariableFloat32' || name === 'iplContextSetVariableBool') {
          try { rec.variable = args[1].readUtf8String(); } catch (e) {}
          rec.value = name.endsWith('Float32') ? null : args[2].toInt32();
        }
        this.rec = rec;
        send(rec);
      },
      onLeave(ret) {
        send({op: 'ret', name: this.rec.name, ret: ret.toString()});
      }
    }); } catch (err) { send({error: 'cannot hook ' + exportName + ': ' + err.message}); }
  }
  globalThis.hooked = true;
  send({hooked: m.base.toString()});
}
const m0 = Process.findModuleByName('phonon.dll');
if (m0) hook(m0);
else Process.attachModuleObserver({ onAdded(m) {
  const n = m.name.toLowerCase();
  if (n === 'steamaudio.dll' || n === 'phonon.dll') {
    const ph = Process.findModuleByName('phonon.dll');
    if (ph && !globalThis.hooked) hook(ph);
  }
} });
"""


def import_slots():
    """steamaudio.dll's phonon.dll import slots, {slot RVA: name}: the only functions the bake can reach."""
    import pefile
    pe = pefile.PE(os.path.join(BIN, "steamaudio.dll"), fast_load=True)
    pe.parse_data_directories([1])
    base = pe.OPTIONAL_HEADER.ImageBase
    return {i.address - base: i.name.decode() for d in pe.DIRECTORY_ENTRY_IMPORT if d.dll.decode().lower() == "phonon.dll"
            for i in d.imports if i.name}


def busy():
    out = subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower()
    return "cs2.exe" in out or "resourcecompiler.exe" in out


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("out_dir")
    p.add_argument("--seed", type=int, default=1)
    p.add_argument("--threads", type=int, default=1)
    p.add_argument("--paths", action="store_true")
    a = p.parse_args()
    if busy():
        raise SystemExit("CS2 or another resourcecompiler is running; not starting")
    os.makedirs(a.out_dir, exist_ok=True)
    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"), "-i", source,
            "-world", "-fshallow", "-sareverb", "-sareverb_threads", str(a.threads)]
    if a.paths:
        argv += ["-sapaths"]
    api = open(os.path.join(a.out_dir, "api.jsonl"), "w", encoding="utf-8")
    log = open(os.path.join(a.out_dir, "rc.log"), "w", encoding="utf-8")
    lock = threading.Lock()
    calls = {}

    def on_message(msg, data):
        if msg.get("type") != "send":
            print(msg, flush=True)
            return
        pay = msg["payload"]
        if pay.get("op") == "blob":
            open(os.path.join(a.out_dir, pay["file"]), "wb").write(data)
            return
        if pay.get("op") in ("call", "ret"):
            with lock:
                api.write(json.dumps(pay) + "\n")
            if pay["op"] == "call":
                calls[pay["name"]] = calls.get(pay["name"], 0) + 1
            return
        print(pay, flush=True)

    agent = (AGENT.replace("SEED_VALUE", str(a.seed)).replace("SITES_VALUE", "[" + ",".join(hex(r) for r in RDTSC) + "]")
             .replace("STRUCTS_VALUE", json.dumps({k: list(v) for k, v in STRUCTS.items()}))
             .replace("SLOTS_VALUE", json.dumps(import_slots())))
    dev = frida.get_local_device()
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(agent)
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.on("output", lambda _pid, _fd, data: (log.write(data.decode("utf-8", "replace")) if data else None))
    dev.resume(pid)
    done.wait()
    api.close()
    log.close()
    for k, v in sorted(calls.items(), key=lambda kv: -kv[1]):
        print(f"{v:8d} {k}")
    print(a.out_dir)


if __name__ == "__main__":
    main()
