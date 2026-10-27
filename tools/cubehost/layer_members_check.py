"""layer_members_check.py <run dir>...: scenesystem's layer membership tests, transcribed, against layer_members.js logs.

For each logged 180041f30 call, the material gate (180041f30, scenesystem 20261015 decompile in
tools/re/out/culling/ss_180041f30.c) and each visible fragment's test (180041a00, ss_180041a00.c) are evaluated from
the logged inputs alone and compared with what the game did (whether 180041a00 ran, which fragments it kept). The
size split (+0x6d0) and the culling-box test (+0xc18 bit 0) are AggregateCulling's; fragments they could remove
are reported, not decided here. Nothing is fitted: a mismatch means an input is missing or the reading is wrong.
"""
import json
import os
import sys
from collections import Counter

U = lambda h: int(h, 16)


def gate(t, mat1c, mat14, obj_e0):
    """180041f30 before its call to 180041a00 (lVar11 = context +0x10, the layer)."""
    L = t["layer"]
    l480, l48 = U(L["f480"]), U(L["f48"])
    if (l480 >> 1) & 1:
        return False
    flags = (mat1c if mat1c is not None else 0) | obj_e0          # uVar15 (no material: a fixed global, not seen here)
    u3 = L["f4a0"]
    s2c = L["f4ac"] & 0xffff                                     # uStack_2c: (ushort) +0x4ac
    if not ((l48 >> 24) & 1) or (l480 & 0xc000000000) != 0xc000000000:
        u4, u12 = 0x100, u3
    else:
        s2c = (L["f4ac"]) & 0xffff                               # (ushort)(uint6 at +0x4a8 >> 32): +0x4ac's low half
        u4, u12 = 0x200, u3 | 0x20
    if t["c65"] == 0:
        if l480 & 1:
            u12 |= 1
    else:
        u12 &= 0xfffffffa
    u18 = L["f4a4"] & flags
    u6 = L["f4a8"] | 0x800
    if t["c3c"] >= 0:                                            # 0 < f || f == 0
        u6 = L["f4a8"]
    return (flags & u6) == 0 and (mat14 & (s2c | u4)) == 0 and ((u12 & flags) == u12 or u18 != 0)


def fragment(t, draw14, entry_c):
    """180041a00's flag tests for one visible fragment (lVar15 = context +0x18; uVar4 = context +0x10's +0x48)."""
    V, layer_flags = t["view"], U(t["layer"]["f48"])
    v480, v488, v498 = U(V["f480"]), U(V["f488"]), U(V["f498"])
    excl = 0
    if layer_flags & 0x1000000:
        excl = (v480 >> 0x24) & 4
        if v488 & 0x840000000000:
            excl |= 8
    if v498 & 0x800000000:
        excl |= 2
    req = (v480 >> 0x16) & 1
    if v480 & 0x2000000000:
        req |= 0x100
    side = (entry_c & 1) == ((layer_flags >> 0x1d) & 1) or U(V["f780"]) == U(V["at"])
    return side and (draw14 & req) == req and (excl & draw14) == 0


def main():
    stats = Counter()
    bad = []
    for run in sys.argv[1:]:
        objects, tests = {}, []
        for line in open(os.path.join(run, "frida.jsonl"), encoding="utf-8"):
            r = json.loads(line)
            if r.get("kind") == "object":
                objects[r["object"]] = r
            elif r.get("kind") == "test":
                tests.append(r)
        for t in tests:
            o = objects.get(t["object"])
            if o is None or not isinstance(o["material1c14"], list):
                stats["no object record"] += 1
                continue
            mat1c, mat14 = o["material1c14"]
            ran = gate(t, mat1c, mat14, o["objectE0"])
            stats[f"gate {'agrees' if ran == t['ran'] else 'DIFFERS'}"] += 1
            if ran != t["ran"]:
                bad.append(("gate", run, t["layer"]["name"], t["object"], ran, t["ran"]))
            if not t["ran"] or t["visible"] is None:
                continue
            kept = set(t["kept"] or [])
            sized = t["view"]["split"] != 0 or t["view"]["fc18"] & 1
            for f, entry_c in t["visible"]:
                ours = fragment(t, o["fragments"][f][0], entry_c)
                if ours == (f in kept):
                    stats["fragment agrees"] += 1
                elif ours and sized:
                    stats["fragment passes the flags, removed by size or box (AggregateCulling)"] += 1
                else:
                    stats["fragment DIFFERS"] += 1
                    bad.append(("fragment", run, t["layer"]["name"], t["object"], f, o["fragments"][f][0], ours, f in kept))
        # 1800f3b10's pre-filter (decompile lines 214 .. 223) for each (layer, visible object) it was handed: it
        # passes exactly when the game went on to call 180041f30 for that object and layer.
        # Layer objects are reused across views and frames: a layer is its address and its fields at the call.
        def state(L):
            return (L["at"], L["name"], L["f48"], L["f480"], L["f488"], L.get("f490"), L["f498"], L.get("f6dc"), L["viewName"])
        tested = {(state(t["layer"]), t["object"]) for t in tests}
        for line in open(os.path.join(run, "frida.jsonl"), encoding="utf-8"):
            r = json.loads(line)
            if r.get("kind") != "record" or not r.get("fields") or r["object"] not in objects:
                continue
            name, f48, f480, f488, f490, f498, f6dc, view = r["fields"]
            L = {"at": r["layer"], "name": name, "f48": f48, "f480": f480, "f488": f488, "f490": f490, "f498": f498, "f6dc": f6dc, "viewName": view}
            o = objects[r["object"]]
            if "f78" not in o:
                continue
            if int(L["f6dc"]) != 0:
                stats["pre-filter: layer +0x6dc set (not ported)"] += 1
                continue
            rec = U(r["flags"])
            ours = (U(L["f488"]) & rec) == 0 and (U(L["f480"]) & rec) == U(L["f480"])                 and ((U(L["f48"]) >> 0x1a) & 1 or int(L["f6dc"]) == int(o["fc4"]))                 and (U(L["f498"]) & U(o["f78"])) == 0 and (U(L["f490"]) & U(o["f78"])) == U(L["f490"])
            game = (state(L), r["object"]) in tested
            # The loop's later conditions (the culling box +0xc18, the object size against the split, LOD fade)
            # can still skip an object the flag test passes; the flag test must never reject one the game tested.
            if ours == game:
                stats["pre-filter agrees"] += 1
            elif ours:
                stats[f"pre-filter passes, not tested (later conditions) on {L['name']}"] += 1
            else:
                stats["pre-filter REJECTS AN OBJECT THE GAME TESTED"] += 1
                bad.append(("pre-filter", run, L["name"], r["object"], ours, game))
    for k, v in sorted(stats.items()):
        print(f"{v:8d}  {k}")
    for b in bad[:30]:
        print(b)


if __name__ == "__main__":
    main()
