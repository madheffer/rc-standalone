"""capture_colormatrix.py <addon> <out.txt>: materialsystem2's colour matrices during a vrad3 scene load.

Hooks MatrixColorCorrect2's core (180081260: out, contrast, saturation, brightness, &average) and
MatrixColorTint2's (1800818d0: out, &tint, amount) in the 2026-10-15 build (md5
2c9bf761f57cb44c76821344109568f8) and logs each call's pointer arguments (the scalars travel in xmm registers, which Frida does not
expose) and the 16 floats it returns, as hex words. VfxEval.ColorCorrect / ColorTint are checked against these.

Uses the GPU: run it only when CS2 is closed and the PC is free.
"""
import os
import sys
import time

import frida

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
VRAD3 = os.path.join(CS2, "game", "bin", "win64", "vrad3.exe")

AGENT = r"""
function words(p, n) { const r = []; for (let i = 0; i < n; i++) r.push(p.add(i * 4).readU32().toString(16)); return r.join(','); }
function hook(m) {
    Interceptor.attach(m.base.add(0x81260), {
        onEnter(args) {
            this.out = args[0];
            this.avg = words(args[4], 3);
        },
        onLeave() { send({ kind: 'correct', avg: this.avg, out: words(this.out, 16) }); }
    });
    Interceptor.attach(m.base.add(0x818d0), {
        onEnter(args) { this.out = args[0]; this.tint = words(args[1], 3); },
        onLeave() { send({ kind: 'tint', tint: this.tint, out: words(this.out, 16) }); }
    });
    send({ kind: 'hooked', base: m.base.toString() });
}
const found = Process.findModuleByName('materialsystem2.dll');
if (found) hook(found);
else Process.attachModuleObserver({ onAdded(m) { if (m.name.toLowerCase() === 'materialsystem2.dll') hook(m); } });
"""


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    addon, out = sys.argv[1], os.path.abspath(sys.argv[2])
    job = os.path.join(CS2, "game", "csgo_addons", addon, "_vrad3")
    script = open(os.path.join(job, "script-gpu.vrad3"), encoding="utf-8").read()
    vmap = next(line.split()[1] for line in script.splitlines() if line.startswith("// maps/"))
    name = "script-capture-colormatrix.vrad3"
    open(os.path.join(job, name), "w", encoding="utf-8").write("\n".join(
        ["gpu_init", "lightmap_type ahd", "sceneinfo_gpu scene_info.kv3", "exit 0", ""]))
    args = [VRAD3, "-map", vmap, "-script", name, "-vulkan", "-gpuraytracing", "-allthreads", "-unbufferedio"]
    pid = frida.spawn(args, cwd=job)
    session = frida.attach(pid)
    lines = []

    def on_message(message, data):
        p = message.get("payload", message)
        lines.append(" ".join(f"{k}={v}" for k, v in p.items()) if isinstance(p, dict) else repr(p))

    agent = session.create_script(AGENT)
    agent.on("message", on_message)
    agent.load()
    done = [False]
    session.on("detached", lambda reason, crash: done.__setitem__(0, True))
    frida.resume(pid)
    while not done[0]:
        time.sleep(0.5)
    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print(len(lines), "records in", out, flush=True)


if __name__ == "__main__":
    main()
