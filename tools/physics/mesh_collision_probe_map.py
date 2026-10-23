"""mesh_collision_probe_map.py <base.vmap> <out.vmap>: a probe map for Hammer's per-mesh collision settings.
Every CMapMesh of the base (world and brush entity meshes alike) takes the next entry of SETTINGS, cycling:
the collision property (scripts/collision_properties.txt names, one in another case, one unknown), the group, and
the interacts-as / with / exclude lists alone and together. Meshes set to physics type "none" are left alone.
The world physics, entity models and settle Valve builds from it settle how physicsbuilder and the map builder
fold these into a shape's collision attribute."""
import os, sys, tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402

SETTINGS = [
    {"physicsCollisionProperty": "csgo_grenadeclip"},
    {"physicsCollisionProperty": "clip"},
    {"physicsCollisionProperty": "window"},
    {"physicsCollisionProperty": "default_player_movement_exclude"},
    {"physicsCollisionProperty": "Grating"},
    {"physicsCollisionProperty": "s2c_no_such_property"},
    {"physicsGroup": "ConditionallySolid"},
    {"physicsInteractsAs": "playerclip"},
    {"physicsInteractsWith": "player"},
    {"physicsInteractsExclude": "player"},
    {"physicsGroup": "debris", "physicsInteractsAs": "csgo_grenadeclip, npcclip"},
    {"physicsCollisionProperty": "window", "physicsInteractsAs": "blocksound"},
    {},
]


def main(base, out):
    work = tempfile.mkdtemp(prefix="mesh_collision_probe_")
    text_path = os.path.join(work, "base_text.vmap")
    pm.convert(base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    n = 0
    for top in tops:
        for e in pm.walk(top):
            if e.type != "CMapMesh" or e.get("physicsType") == "none":
                continue
            for key, value in SETTINGS[n % len(SETTINGS)].items():
                e.set(key, "string", value)
            n += 1
    text_out = os.path.join(work, "out_text.vmap")
    open(text_out, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_out, out, "binary")
    print("wrote", out, n, "meshes")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
