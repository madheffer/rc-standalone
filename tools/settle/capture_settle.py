"""Record what resourcecompiler's physics settle does, step by step, then stop the
compile once the settle is over, before it writes anything.

The settle (resourcecompiler FUN_180f1e490) builds a vphysics2 world, adds the
props, and steps it for 30 s at 1/90 s through RnWorld::Step (vphysics2
FUN_180200840). This hooks both and sends:

    {"ev": "settle"}                       the settle starts
    {"ev": "step", "n": i, "dt": f, ...}   before each RnWorld::Step
    {"ev": "blob", "name": s}  + bytes     a raw memory dump from a probe
    {"ev": "done"}                         the settle returned

    python capture_settle.py <addon> <map> [--out dir] [--agent extra.js]

Everything lands in <out>/events.jsonl, and each blob in <out>/<name>.bin. The
agent source has an EXTRA hook point: --agent appends a JS file that may assign
onSettleStart, onStep = function (world, n) and onSettleEnd, for probes that
read body state (assign; a function declaration would be overwritten). Never run
it with CS2 open; the tool refuses to.
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import threading
import time

import frida

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
# resourcecompiler.dll and vphysics2.dll as of 2026-09-24.
SETTLE_RVA = 0xf1e490
STEP_RVA = 0x200840

AGENT = r"""
'use strict';
let step = 0;
let inSettle = false;
var onStep = null, onSettleEnd = null, onSettleStart = null;
function blob(name, ptr, size) {
  send({ev: 'blob', name: name, size: size}, ptr.readByteArray(size));
}
function hookRc(m) {
  Interceptor.attach(m.base.add(%(settle)d), {
    onEnter() { inSettle = true; send({ev: 'settle'}); if (onSettleStart) onSettleStart(); },
    onLeave() { inSettle = false; if (onSettleEnd) onSettleEnd(); send({ev: 'done', steps: step}); }
  });
}
function hookPhys(m) {
  Interceptor.attach(m.base.add(%(step)d), {
    onEnter(args) {
      if (!inSettle) return;
      const world = args[0];
      if (onStep) onStep(world, step);
      step++;
    }
  });
}
function watch(name, fn) {
  const found = Process.findModuleByName(name);
  if (found) { fn(found); return; }
  const obs = Process.attachModuleObserver({
    onAdded(m) { if (m.name.toLowerCase() === name) fn(m); }
  });
}
watch('resourcecompiler.dll', hookRc);
watch('vphysics2.dll', hookPhys);
"""


def cs2_running():
    out = subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower()
    return "cs2.exe" in out


def compiling():
    out = subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower()
    return "resourcecompiler.exe" in out


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("addon")
    parser.add_argument("map")
    parser.add_argument("--out")
    parser.add_argument("--agent", help="extra JS appended to the agent")
    args = parser.parse_args()
    if cs2_running():
        sys.exit("cs2.exe is running; captures never run beside the game.")
    if compiling():
        sys.exit("another resourcecompiler.exe is running; wait for it.")

    source = None
    for root, _, files in os.walk(os.path.join(CS2, "content", "csgo_addons", args.addon, "maps")):
        if args.map + ".vmap" in files:
            source = os.path.join(root, args.map + ".vmap")
            break
    if source is None:
        sys.exit("no source for %s/%s" % (args.addon, args.map))

    # The compile is stopped before it writes, but keep the package safe anyway.
    vpk = os.path.join(CS2, "game", "csgo_addons", args.addon, "maps", args.map + ".vpk")
    backup = vpk + ".capture_backup"
    if os.path.exists(vpk):
        shutil.copy2(vpk, backup)

    out_dir = args.out or os.path.join(os.environ.get("TEMP", "."), "settle_capture", args.map)
    os.makedirs(out_dir, exist_ok=True)
    events = open(os.path.join(out_dir, "events.jsonl"), "w")
    done = threading.Event()

    def on_message(message, data):
        if message["type"] != "send":
            print("agent:", message.get("description") or message, file=sys.stderr)
            if message.get("stack"):
                print(message["stack"], file=sys.stderr)
            return
        payload = message["payload"]
        if payload.get("ev") == "blob" and data is not None:
            with open(os.path.join(out_dir, payload["name"] + ".bin"), "wb") as f:
                f.write(data)
        events.write(json.dumps(payload) + "\n")
        if payload.get("ev") == "done":
            done.set()

    source_js = AGENT % {"settle": SETTLE_RVA, "step": STEP_RVA}
    if args.agent:
        with open(args.agent, encoding="utf-8") as f:
            source_js += "\n" + f.read()

    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-f",
            "-game", os.path.join(CS2, "game", "csgo"), "-i", source]
    device = frida.get_local_device()
    pid = device.spawn(argv, cwd=BIN, stdio="pipe")
    session = device.attach(pid)
    script = session.create_script(source_js)
    script.on("message", on_message)
    script.load()
    session.on("detached", lambda *a: done.set())
    started = time.time()
    device.resume(pid)
    done.wait()
    try:
        device.kill(pid)
    except frida.ProcessNotFoundError:
        pass
    events.close()
    time.sleep(1)
    if os.path.exists(backup):
        shutil.copy2(backup, vpk)
        os.remove(backup)
    print("%.1fs -> %s" % (time.time() - started, out_dir))


if __name__ == "__main__":
    main()
