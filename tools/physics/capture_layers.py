"""capture_layers.py [names...]: vphysics2's intersection dictionary entries, read live in a resourcecompiler run.

ShapeBuilder_BuildPart (rc 0923: 180c28230) sets part flag 2 for a mesh whose collision attribute names a
detail layer the dictionary does not know, or knows with the byte at entry + 0x10 clear: it hashes the name
(MurmurHash2LowerCase, 1800c7c10: string, length, seed 0x31415926), asks g_pVPhysics2Interface (183b5b5c0) vf 0xd8 for the dictionary and its
vf 0x28 (CIntersectionDictionary::vf5, vphysics2 180296c00) for the entry. This runs the same calls from inside
the first BuildPart of a map compile and prints each name's entry (null, or its first 0x40 bytes). The compile is
-world -phys -fshallow on s2c_rc_probe/s2c_cablecoll with its .vpk moved aside and restored. CS2 must be closed.
"""
import json
import os
import sys
import threading

import frida

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "re"))
from package_guard import PackageGuard  # noqa: E402
from rva_map import installed, installed_data  # noqa: E402

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
NAMES = [
    "always", "never", "trigger", "ConditionallySolid", "hitboxes", "sky", "coarse", "hires", "Default",
    "interactive_debris", "breakable_glass", "vehicle", "player_movement", "npc", "in_vehicle", "weapon",
    "Projectile", "door_blocker", "passable_door", "dissolving", "pushaway", "npc_actor", "npc_scripted",
    "pz_clip", "props", "playerclip", "npcclip", "blocklos", "blocklight", "ladder", "pickup", "blocksound",
    "window", "water", "slime", "passbullets", "worldGeometry", "touchall", "player", "physics_prop",
    "CarriedObject", "serverentityonclient", "CarriedWeapon", "StaticLevel", "NavIgnore", "NavLocalIgnore",
    "PostProcessingVolume", "vehicleclip", "CONTENTS_SOLID", "CONTENTS_SOLID_NO_BLOCK_LOS",
    "csgo_thrown_grenade", "csgo_hostage", "csgo_physicsbox", "nosuchlayer",
]

AGENT = r"""
'use strict';
let done = false;
function hook() {
  const m = Process.findModuleByName('resourcecompiler.dll');
  if (m === null) { setTimeout(hook, 5); return; }
  const hash = new NativeFunction(m.base.add(%(hash)d), 'uint32', ['pointer', 'uint32', 'uint32']);
  Interceptor.attach(m.base.add(%(part)d), {
    onEnter(args) {
      if (done) return;
      done = true;
      const iface = m.base.add(%(iface)d).readPointer();
      const getDict = new NativeFunction(iface.readPointer().add(0xd8).readPointer(), 'pointer', ['pointer']);
      const dict = getDict(iface);
      const find = new NativeFunction(dict.readPointer().add(0x28).readPointer(), 'pointer', ['pointer', 'uint32']);
      const out = {'(entries)': {hash: dict.add(0x250).readU32(), entry: null}};
      for (const name of %(names)s) {
        const h = hash(Memory.allocUtf8String(name), name.length, 0x31415926);
        const e = find(dict, h);
        out[name] = {hash: h, entry: e.isNull() ? null : Array.from(new Uint8Array(e.readByteArray(0x40)))};
      }
      send(out);
    }
  });
}
hook();
"""


def main():
    names = sys.argv[1:] or NAMES
    rva = {"part": installed("resourcecompiler", 0xc28230, build="20260923"),
           "hash": installed("resourcecompiler", 0xc7c10, build="20260923"),
           "iface": installed_data("resourcecompiler", 0x3b5b5c0, build="20260923"),
           "names": json.dumps(names)}
    got = threading.Event()
    result = {}

    def on_message(message, data):
        if message["type"] == "send":
            result.update(message["payload"])
            got.set()
        else:
            print("agent:", message.get("description") or message, file=sys.stderr)

    vpk = os.path.join(CS2, "game", "csgo_addons", "s2c_rc_probe", "maps", "s2c_cablecoll.vpk")
    with PackageGuard(vpk, os.path.join(os.environ.get("TEMP", "."), "capture_layers.vpk.bak")) as guard:
        source = os.path.join(CS2, "content", "csgo_addons", "s2c_rc_probe", "maps", "s2c_cablecoll.vmap")
        argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
                "-i", source, "-world", "-fshallow", "-phys"]
        device = frida.get_local_device()
        pid = device.spawn(argv, cwd=BIN, stdio="pipe")
        guard.pid = pid
        session = device.attach(pid)
        script = session.create_script(AGENT % rva)
        script.on("message", on_message)
        script.load()
        done = threading.Event()
        session.on("detached", lambda *a: done.set())
        device.resume(pid)
        done.wait()
    for name, e in result.items():
        entry = e["entry"]
        print("%-28s %08x %s" % (name, e["hash"], "-" if entry is None else bytes(entry).hex()))


if __name__ == "__main__":
    main()
