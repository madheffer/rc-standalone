"""capture_amel_keyw.py <addon> <in image> [x0,y0,channel ...]: the stack float FillBlocks hashes as its colour key's fourth float.

AmeliorateBlockCompressionArtefacts_FillBlocks (vrad3 180066230 in the 2026-10-15 build) keys its
per-block colour histogram on four floats but never writes the fourth ([rsp+0x74] after its
prologue), so the key's hash, and with it the slot order that breaks ties, holds whatever the stack
had there. This runs vrad3 on <in image> (a folder in the addon's _vrad3, as post_oracle.py chain)
with lightmap_ameliorate_block_compression_artefacts -mips 1 under Frida, hooked at the instruction
that reads the slot (1800666e9), and prints each distinct value per thread. The hook sits past the
prologue, so Frida's own frame lands below the function's and leaves the slot as Valve's code saw it.
Each x0,y0,channel given also prints, for that block and channel, every texel's value and key as
FillBlocks hashes them, in its order.

Uses the GPU: run it only when CS2 is closed and the PC is free.
"""
import os
import sys
import time

import frida
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "re"))
from rva_map import require  # noqa: E402

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
VRAD3 = os.path.join(CS2, "game", "bin", "win64", "vrad3.exe")
READ_RVA = 0x666e9  # vrad3 1800666e9: imul eax, dword ptr [rsp + 0x74], 0x5bd1e995

AGENT = """
const RVA = %d;
const TRACE = %s;
const seen = {};
function hook(m) {
    Interceptor.attach(m.base.add(RVA), {
        onEnter() {
            const sp = this.context.rsp;
            const x0 = this.context.rbp.add(0x40).readS32(), y0 = this.context.rbp.add(0x48).readS32();
            const ch = this.context.r15.toInt32();
            for (const t of TRACE)
                if (t[0] === x0 && t[1] === y0 && t[2] === ch) {
                    const f = [];
                    for (let k = 0; k < 8; k++) f.push(sp.add(0x58 + 4 * k).readFloat());
                    send({ kind: 'trace', x0, y0, ch, thread: Process.getCurrentThreadId(), f, x: this.context.rbx.toInt32(), y: this.context.rsi.toInt32() });
                }
            const v = sp.add(0x74).readU32();
            const key = Process.getCurrentThreadId() + ':' + v;
            if (seen[key]) return;
            seen[key] = true;
            send({ kind: 'value', thread: Process.getCurrentThreadId(), bits: v });
        }
    });
    // 18006676e: the count FillBlocks just gave the texel's key (eax), and the slot's address (rcx + rax*8).
    Interceptor.attach(m.base.add(0x6676e), {
        onEnter() {
            const x0 = this.context.rbp.add(0x40).readS32(), y0 = this.context.rbp.add(0x48).readS32();
            const ch = this.context.r15.toInt32();
            for (const t of TRACE)
                if (t[0] === x0 && t[1] === y0 && t[2] === ch)
                    send({ kind: 'count', x0, y0, ch, count: this.context.rax.toInt32(), max: this.context.r14.toInt32() });
        }
    });
    // A texel of channel ch read straight from the image (CVrad3LightmapImage at 180399e70: per channel,
    // 0x9d8 bytes from +0x10, data at +0x28, x stride at +0x3a8, y stride at +0x4a8).
    function texel(ch, x, y) {
        const c = m.base.add(0x399e70 + ch * 0x9d8);
        const data = c.add(0x28).readPointer();
        return data.add(x * c.add(0x3a8).readS64() + y * c.add(0x4a8).readS64()).readFloat();
    }
    // 180066a86: the block's write loop is done.
    Interceptor.attach(m.base.add(0x66a86), {
        onEnter() {
            const x0 = this.context.rbp.add(0x40).readS32(), y0 = this.context.rbp.add(0x48).readS32();
            const ch = this.context.r15.toInt32();
            for (const t of TRACE)
                if (t[0] === x0 && t[1] === y0 && t[2] === ch) {
                    const row = [];
                    for (let y = y0; y < y0 + 4; y++) for (let x = x0; x < x0 + 4; x++) row.push(texel(ch, x, y));
                    send({ kind: 'written', x0, y0, ch, row });
                }
        }
    });
    // lambda_40 (1800741b0) runs after FillBlocks.
    Interceptor.attach(m.base.add(0x741b0), {
        onEnter() {
            for (const t of TRACE) {
                const row = [];
                for (let y = t[1]; y < t[1] + 4; y++) for (let x = t[0]; x < t[0] + 4; x++) row.push(texel(t[2], x, y));
                send({ kind: 'later', x0: t[0], y0: t[1], ch: t[2], row });
            }
        }
    });
    send({ kind: 'hooked', base: m.base.toString() });
}
const found = Process.findModuleByName('vrad3.dll');
if (found) hook(found);
else Process.attachModuleObserver({ onAdded(m) { if (m.name.toLowerCase() === 'vrad3.dll') hook(m); } });
"""


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(2)
    addon, source = sys.argv[1], sys.argv[2]
    # Literal 10-15 addresses (hooks and the per-channel state): refuse a build that moved them.
    require("vrad3", [READ_RVA, 0x6676e, 0x399e70, 0x66a86, 0x741b0], "20261015")
    trace = [[int(v) for v in a.split(",")] for a in sys.argv[3:]]
    job = os.path.join(CS2, "game", "csgo_addons", addon, "_vrad3")
    lines = ["gpu_init", "lightmap_type ahd", "lightmap_image_gpu 4096 4096 1024",
             "lightmap_load_packing_geometry_gpu lightmap_packing_geometry.dat", f"lightmap_image_load {source}",
             "lightmap_ameliorate_block_compression_artefacts -mips 1", "exit 0", ""]
    name = "script-capture-keyw.vrad3"
    open(os.path.join(job, name), "w", encoding="utf-8").write("\n".join(lines))
    args = [VRAD3, "-map", "maps/oracle.vmap", "-script", name, "-vulkan", "-gpuraytracing", "-allthreads", "-unbufferedio"]
    print(" ".join(args), flush=True)
    pid = frida.spawn(args, cwd=job)
    session = frida.attach(pid)
    values = {}

    def on_message(message, data):
        if message["type"] != "send":
            print(message, flush=True)
            return
        p = message["payload"]
        if p["kind"] == "hooked":
            print("hooked vrad3.dll at", p["base"], flush=True)
        elif p["kind"] in ("written", "later"):
            print(p["kind"], p["x0"], p["y0"], "ch", p["ch"], p["row"], flush=True)
        elif p["kind"] == "count":
            print("count", p["x0"], p["y0"], "ch", p["ch"], "count", p["count"], "max before", p["max"], flush=True)
        elif p["kind"] == "trace":
            print("trace", p["x0"], p["y0"], "ch", p["ch"], "thread", p["thread"], "at", p["x"], p["y"], "v", p["f"][:4], "key", p["f"][4:], flush=True)
        else:
            values.setdefault(p["bits"], set()).add(p["thread"])

    agent = session.create_script(AGENT % (READ_RVA, trace))
    agent.on("message", on_message)
    agent.load()
    done = [False]
    session.on("detached", lambda reason, crash: done.__setitem__(0, True))
    frida.resume(pid)
    while not done[0]:
        time.sleep(0.5)
    for bits, threads in sorted(values.items()):
        print("0x%08x on %d threads" % (bits, len(threads)), flush=True)


if __name__ == "__main__":
    main()
