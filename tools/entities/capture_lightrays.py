"""capture_lightrays.py <addon> <map> --near x,y,z [--radius r] [--out file]: one light's rays and hits in Valve's pass.

Hooks LightPrecompute_TraceRay (rc 180f18820, 0923 analysis addresses moved to
the installed build by tools/re/rva_map.py). Its stack arguments 5 and 6 are
the starts and ends (CUtlVector of Vector: count at +0, data at +8). A call
whose first start lies within --radius of --near is the light's: the first
ray's result pointer is noted at the push (181c1bf10: tracer, batch, start,
end, result; results are contiguous in ray order), and the results are read
when the trace hands them on (180f17540), after the flush (181c1c530) and
the wait on its jobs; at the flush itself they are not yet written. Written
per call: u32 count, count starts (3 floats), count ends, count raw results
(0x38 bytes: +0xc the triangle id, 0xffffffff for none; +0x20 the object).
Used to settle GROUND_TRUTH 31 (Mako's lights: Valve's rays pass geometry
ours hits). CS2 must be closed; the map's package is backed up and restored.
"""
import argparse
import filecmp
import os
import shutil
import struct
import subprocess
import threading

import frida

from capture_rayscene import BIN, CS2, resolve, running

AGENT = r"""
'use strict';
let active = null;  // the matching call: its rays and result pointers
function hook() {
if (Process.findModuleByName('resourcecompiler.dll') === null) { setTimeout(hook, 5); return; }
Interceptor.attach(base().add(TRACE), {
  onEnter(args) {
    const starts = args[4], ends = args[5];
    const count = ends.readS32();
    const sd = starts.add(8).readPointer(), ed = ends.add(8).readPointer();
    const x = sd.readFloat(), y = sd.add(4).readFloat(), z = sd.add(8).readFloat();
    if (RADIUS < 0) {  // survey: every call's first ray and count
      send({survey: [count, x, y, z, ed.readFloat(), ed.add(4).readFloat(), ed.add(8).readFloat()]});
      return;
    }
    const dx = x - NEAR[0], dy = y - NEAR[1], dz = z - NEAR[2];
    if (count <= 0 || dx * dx + dy * dy + dz * dz > RADIUS * RADIUS) return;
    active = {count: count, starts: sd.readByteArray(count * 12), ends: ed.readByteArray(count * 12), results: null};
  },
  onLeave(ret) {
    if (active !== null && active.results !== null) {
      const head = new ArrayBuffer(4);
      new DataView(head).setUint32(0, active.count, true);
      send({call: active.count}, concat([head, active.starts, active.ends, active.results]));
    }
    active = null;
  }
});
Interceptor.attach(base().add(PUSH), {
  onEnter(args) {
    if (active !== null && active.first === undefined) active.first = args[4];
  }
});
// The flush hands the batch to jobs and the trace waits on them before it reads
// the results (USE, its fifth direct call): read them there, not at the flush.
Interceptor.attach(base().add(USE), {
  onEnter(args) {
    if (active !== null && active.results === null && active.first !== undefined)
      active.results = active.first.readByteArray(active.count * 0x38);
  }
});
send({hooked: base().toString()});
}
hook();
function base() { return Process.findModuleByName('resourcecompiler.dll').base; }
function concat(parts) {
  let n = 0;
  for (const p of parts) n += p.byteLength;
  const out = new Uint8Array(n);
  let at = 0;
  for (const p of parts) { out.set(new Uint8Array(p), at); at += p.byteLength; }
  return out.buffer;
}
"""


def direct_calls(dll, rva, length):
    """The targets (RVAs) of the direct calls in <length> bytes from <rva>."""
    import capstone
    import pefile
    pe = pefile.PE(dll, fast_load=True)
    image = pe.get_memory_mapped_image()
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    out = []
    for ins in md.disasm(image[rva:rva + length], rva):
        if ins.mnemonic == "call" and ins.op_str.startswith("0x"):
            out.append(int(ins.op_str, 16))
    return out


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("--near", required=True, help="x,y,z of the light")
    p.add_argument("--radius", type=float, default=32.0)
    p.add_argument("--out")
    a = p.parse_args()
    if running("cs2.exe") or running("resourcecompiler.exe"):
        raise SystemExit("CS2 or a compile is running; not starting")
    # The push and flush have byte-identical twins in the 10-06 build, so they are
    # taken from the installed trace function's own calls (the third and fourth
    # direct calls, as 0923's 180f1892a and 180f1896a are).
    trace = resolve(0xf18820)
    calls = direct_calls(os.path.join(BIN, "resourcecompiler.dll"), trace, 0x400)
    resolved = {"TRACE": trace, "PUSH": calls[2], "FLUSH": calls[3], "USE": calls[4]}
    near = [float(v) for v in a.near.split(",")]
    head = "".join(f"const {k} = {v};\n" for k, v in resolved.items()) + f"const NEAR = {near};\nconst RADIUS = {a.radius};\n"
    if shutil.disk_usage(os.path.splitdrive(CS2)[0] + "\\").free < 10 * 1024 ** 3:
        raise SystemExit("under 10 GB free on the game drive; not starting")
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "lightrays_capture", f"{a.map}_{'_'.join(a.near.split(','))}.bin")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
        os.remove(vpk)
    out = open(out_path, "wb")
    calls = [0]

    def on_message(msg, data):
        if msg.get("type") != "send" or data is None:
            print("agent:", msg.get("payload", msg))
            return
        out.write(data)
        calls[0] += 1
        print("light call:", struct.unpack_from("<I", data)[0], "rays", flush=True)

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    dev = frida.get_local_device()
    pid = dev.spawn([os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
                     "-i", source, "-world", "-phys", "-fshallow"], cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT.replace("'use strict';\n", "'use strict';\n" + head, 1))
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    while not done.wait(2):
        if running("cs2.exe"):
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    while running("resourcecompiler.exe"):
        threading.Event().wait(2)
    out.close()
    print(out_path, calls[0], "calls")
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored" if ok else "RESTORE CMP FAILED", vpk)
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
