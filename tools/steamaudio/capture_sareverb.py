"""capture_sareverb.py <addon> <map> <out_dir> [--seed N] [--threads N] [--paths] [--no-pin]: a Valve compile of
<map> with -world -fshallow -sareverb (and -sapaths), with Steam Audio's ray seeds pinned so the reverb bake repeats.

Valve's .sareverb changes from run to run (three compiles of s2c_saprobe, 10-28: three files, values off by up to
~10%). phonon.dll (Steam Audio 4.9.0, 0923) traces reverb rays with its Embree simulator, whose ISPC jobs seed an
Embree RandomSampler from clock(), i.e. rdtsc: each rdtsc in 1801f5e48..180249a08 (16 sites, two job kernels per
ISPC target) feeds its low 32 bits straight into the murmur mix (imul 0xcc9e2d51 / 0x16a88000, then 0x1b873593),
as embree_reflection_simulator.ispc's RandomSampler_init(rng, clock()) does in the public 4.8.1 source. The
RandomNumberGenerator constructor (180a580e0, seeded from time(NULL)) is not reached by the bake.

This hooks the instruction after each of those rdtsc and sets eax to --seed (edx 0), counting the hits per site.

The package's .sareverb and .sapaths are copied to <out_dir>, with the compile's log. A compile overwrites the
map's .vpk. CS2 must be closed; run it through tools/re/heavy.py.
"""
import argparse
import os
import shutil
import subprocess
import sys
import threading

import frida

CS2 = r"D:\Steam\steamapps\common\Counter-Strike Global Offensive"
BIN = os.path.join(CS2, "game", "bin", "win64")
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "map-survey"))
from vpk import Vpk  # noqa: E402

# phonon.dll 0923 (FileVersion 4.9.0); patch_check tracks the file. Each is an rdtsc (2 bytes).
RDTSC = [0x1f5e48, 0x1f9dfb, 0x1fdb28, 0x201adb, 0x2063ab, 0x20d965, 0x21320b, 0x21a7c5,
         0x220684, 0x225fe6, 0x22ba84, 0x2313e6, 0x237707, 0x23d658, 0x243ab7, 0x249a08]

AGENT = r"""
const SEED = SEED_VALUE, PIN = PIN_VALUE, SITES = SITES_VALUE;
function hook(m) {
  for (const rva of SITES) {
    const at = m.base.add(rva);
    if (at.readU16() !== 0x310f) { send({error: 'not rdtsc at ' + rva.toString(16)}); continue; }
    Interceptor.attach(at.add(2), function () {
      send({op: 'rdtsc', site: rva.toString(16), eax: this.context.rax.and(0xffffffff).toString()});
      if (PIN) { this.context.rax = ptr(SEED); this.context.rdx = ptr(0); }
    });
  }
  globalThis.hooked = true;
  send({hooked: m.base.toString()});
}
const m0 = Process.findModuleByName('phonon.dll');
if (m0) hook(m0);
else Process.attachModuleObserver({ onAdded(m) {
  if (m.name.toLowerCase() === 'steamaudio.dll' || m.name.toLowerCase() === 'phonon.dll') {
    const ph = Process.findModuleByName('phonon.dll');
    if (ph && !globalThis.hooked) hook(ph);
  }
} });
"""


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
    p.add_argument("--paths", action="store_true", help="also -sapaths")
    p.add_argument("--no-pin", action="store_true", help="log the clock but leave it alone")
    a = p.parse_args()
    if busy():
        raise SystemExit("CS2 or another resourcecompiler is running; not starting")
    os.makedirs(a.out_dir, exist_ok=True)
    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"), "-i", source,
            "-world", "-fshallow", "-sareverb", "-sareverb_threads", str(a.threads)]
    if a.paths:
        argv += ["-sapaths"]
    log = open(os.path.join(a.out_dir, "rc.log"), "w", encoding="utf-8")
    lock = threading.Lock()
    counts = {"rdtsc": 0}
    sites = {}

    def on_message(msg, data):
        if msg.get("type") != "send":
            print(msg, flush=True)
            return
        pay = msg["payload"]
        op = pay.get("op")
        if op in counts:
            counts[op] += 1
            sites[pay["site"]] = sites.get(pay["site"], 0) + 1
            if counts[op] <= 4:
                print(pay, flush=True)
            return
        print(pay, flush=True)

    agent = (AGENT.replace("SEED_VALUE", str(a.seed)).replace("PIN_VALUE", "false" if a.no_pin else "true")
             .replace("SITES_VALUE", "[" + ",".join(hex(r) for r in RDTSC) + "]"))
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
    log.close()
    print("rdtsc seeds seen", counts["rdtsc"], sites)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    v = Vpk(vpk)
    for ext in ("sareverb", "sapaths"):
        name = f"maps/{a.map}.{ext}"
        if name in v.entries:
            open(os.path.join(a.out_dir, f"{a.map}.{ext}"), "wb").write(v.read(name))
    v.close()
    shutil.copy(vpk, os.path.join(a.out_dir, a.map + ".vpk"))
    print(a.out_dir)


if __name__ == "__main__":
    main()
