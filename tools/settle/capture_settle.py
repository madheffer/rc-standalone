"""Record what resourcecompiler's physics settle does, step by step, then stop the
compile once the settle is over, before it writes anything.

The settle (resourcecompiler FUN_180f1e490) builds a vphysics2 world, adds the
props, and steps it for 30 s at 1/90 s through RnWorld::Step (vphysics2
FUN_180200840). This hooks both and sends:

    {"ev": "settle"}                       the settle starts
    {"ev": "step", "n": i, "dt": f, ...}   before each RnWorld::Step
    {"ev": "blob", "name": s}  + bytes     a raw memory dump from a probe
    {"ev": "settle_done"}                  the settle returned
    {"ev": "done"}                         the settle returned and no probe holds the compile

    python capture_settle.py <addon> <map> [--out dir] [--agent extra.js]

Everything lands in <out>/events.jsonl, and each blob in <out>/<name>.bin. The
agent source has an EXTRA hook point: --agent appends a JS file that may assign
onSettleStart, onStep = function (world, n) and onSettleEnd, for probes that
read body state (assign; a function declaration would be overwritten). A probe
that needs the compile to go on past the settle calls hold() at load and
release() when done; the compile is stopped once the settle is over and nothing
holds it, or when it exits. Never run
it with CS2 open; the tool refuses to, and kills the compile if the game
starts while it runs.
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
# resourcecompiler.dll (2026-09-23) and vphysics2.dll (2026-09-24) RVAs,
# moved to the installed build at run time (tools/re/rva_map.py).
SETTLE_RVA = 0xf1e490
STEP_RVA = 0x200840
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "re"))
from rva_map import installed, installed_data  # noqa: E402


def rebase_tables(js):
    """The probe's RC and VP tables moved to the installed build: functions
    by their code, globals (names starting with 'group') through the code
    that addresses them."""
    import re

    def entry(dll, e):
        find = installed_data if e.group(1).startswith("group") else installed
        return "%s: 0x%x" % (e.group(1), find(dll, int(e.group(2), 16)))

    def table(m):
        dll = "resourcecompiler" if m.group(1) == "RC" else "vphysics2"
        body = re.sub(r"(\w+): (0x[0-9a-fA-F]+)", lambda e: entry(dll, e), m.group(2))
        return "const %s = {%s};" % (m.group(1), body)
    return re.sub(r"const (RC|VP) = \{(.*?)\};", table, js, flags=re.S)

AGENT = r"""
'use strict';
let step = 0;
const SETTLE_ONLY = %(settle_only)s;   // --settle-only: probes skip what holds the compile past the settle
let inSettle = false;
var onStep = null, onSettleEnd = null, onSettleStart = null;
function blob(name, ptr, size) {
  send({ev: 'blob', name: name, size: size}, ptr.readByteArray(size));
}
// Frida drops a message over 128 MiB, and the session with it: a record whose
// JSON is long goes as its text in parts the host joins back into one event.
function sendJson(obj) {
  const s = JSON.stringify(obj), PART = 32 * 1024 * 1024;
  if (s.length < PART) { send(obj); return; }
  for (let at = 0; at < s.length; at += PART) {
    const piece = s.substring(at, Math.min(at + PART, s.length)), buf = new Uint8Array(piece.length);
    for (let i = 0; i < piece.length; i++) buf[i] = piece.charCodeAt(i) & 0xff;
    send({ev: 'jsonpart'}, buf.buffer);
  }
  send({ev: 'jsonend'});
}
function hookRc(m) {
  Interceptor.attach(m.base.add(%(settle)d), {
    onEnter() { inSettle = true; send({ev: 'settle'}); if (onSettleStart) onSettleStart(); },
    onLeave() {
      inSettle = false;
      if (onSettleEnd) onSettleEnd();
      settleDone = true;
      send({ev: 'settle_done', steps: step});
      finishIfDone();
    }
  });
}
// A probe that needs the compile to run on past the settle calls hold() when
// it loads and release() when it has what it needs.
let holds = 0, settleDone = false, finished = false;
function hold() { holds++; }
function release() { holds--; finishIfDone(); }
function finishIfDone() {
  if (settleDone && holds <= 0 && !finished) { finished = true; send({ev: 'done', steps: step}); }
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
    parser.add_argument("--settle-only", action="store_true", help="stop when the settle returns; probes skip their later hooks")
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
    parts = []

    def on_message(message, data):
        if message["type"] != "send":
            print("agent:", message.get("description") or message, file=sys.stderr)
            if message.get("stack"):
                print(message["stack"], file=sys.stderr)
            return
        payload = message["payload"]
        if payload.get("ev") == "jsonpart":
            parts.append(data or b"")
            return
        if payload.get("ev") == "jsonend":
            payload = json.loads(b"".join(parts).decode("ascii"))
            parts.clear()
        if payload.get("ev") == "blob" and data is not None:
            with open(os.path.join(out_dir, payload["name"] + ".bin"), "wb") as f:
                f.write(data)
        events.write(json.dumps(payload) + "\n")
        if payload.get("ev") == "done":
            done.set()

    source_js = AGENT % {"settle": installed("resourcecompiler", SETTLE_RVA), "step": installed("vphysics2", STEP_RVA),
                         "settle_only": "true" if args.settle_only else "false"}
    if args.agent:
        with open(args.agent, encoding="utf-8") as f:
            source_js += "\n" + rebase_tables(f.read())

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

    # If the game starts while we capture, stop at once.
    def watchdog():
        while not done.is_set():
            if cs2_running():
                print("cs2.exe started; killing the compile.", file=sys.stderr)
                done.set()
                return
            time.sleep(1)

    threading.Thread(target=watchdog, daemon=True).start()
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
