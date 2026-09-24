"""Record the order resourcecompiler bakes a map's instances in, and the node id
each one's copy starts at, then stop the compile before it writes anything.

The instance bake (FUN_180f60740) collects the map's CMapInstance nodes, drops
the ones a node predicate rejects, and collapses each of the rest
(FUN_180f5ff40), repeating while anything collapsed so a nested instance is
baked in a later round. Each collapse makes a copy of the target group with
fresh node ids, and that order is what decides every instanced entity's
hammerUniqueId.

    python capture_instances.py <addon> <map> [--out file]

Writes one JSON line per event: {"ev": "round"} when a bake round starts,
{"ev": "bake", "instance": id, "copy": id} per collapse, {"ev": "done"} at the
end. Never run it with CS2 open.
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
# resourcecompiler.dll 2026-09-24, byte-identical here to 2026-09-23.
BAKE_ALL_RVA = 0xf60740
BAKE_ONE_RVA = 0xf5ff40
# A map node's id: the u32 FUN_180f5ff40 hashes as the instance's key.
NODE_ID = 0x300

AGENT = r"""
'use strict';
function hook(m) {
  Interceptor.attach(m.base.add(%(all)d), {
    onEnter() { send({ev: 'round'}); },
    onLeave() { send({ev: 'done'}); }
  });
  Interceptor.attach(m.base.add(%(one)d), {
    onEnter(args) { this.node = args[1]; this.id = this.node.add(%(id)d).readU32(); },
    onLeave(ret) {
      send({ev: 'bake', instance: this.id, copy: ret.isNull() ? null : ret.add(%(id)d).readU32()});
    }
  });
}
// The DLL loads after the process starts.
const found = Process.findModuleByName('resourcecompiler.dll');
if (found) hook(found);
else Process.attachModuleObserver({
  onAdded(m) { if (m.name.toLowerCase() === 'resourcecompiler.dll') hook(m); }
});
"""


def cs2_running():
    out = subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower()
    return "cs2.exe" in out


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("addon")
    parser.add_argument("map")
    parser.add_argument("--out")
    args = parser.parse_args()
    if cs2_running():
        sys.exit("cs2.exe is running; captures never run beside the game.")

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

    out_path = args.out or os.path.join(os.environ.get("TEMP", "."), "instance_capture", args.map + ".jsonl")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    out = open(out_path, "w")
    done = threading.Event()

    def on_message(message, data):
        if message["type"] != "send":
            print("agent:", message.get("description") or message, file=sys.stderr)
            return
        out.write(json.dumps(message["payload"]) + "\n")
        if message["payload"]["ev"] == "done":
            done.set()

    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-f",
            "-game", os.path.join(CS2, "game", "csgo"), "-i", source]
    device = frida.get_local_device()
    pid = device.spawn(argv, cwd=BIN, stdio="pipe")
    session = device.attach(pid)
    script = session.create_script(AGENT % {"all": BAKE_ALL_RVA, "one": BAKE_ONE_RVA, "id": NODE_ID})
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
    out.close()
    time.sleep(1)
    if os.path.exists(backup):
        shutil.copy2(backup, vpk)
        os.remove(backup)
    print("%.1fs -> %s" % (time.time() - started, out_path))


if __name__ == "__main__":
    main()
