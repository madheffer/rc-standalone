"""cascade_summary.py [frida.jsonl]: what run_probe.py --frida cascade_sources.js saw, and the verdict on cascade 0's 75.

Default input: %TEMP%/cubehost_run/probe01/frida.jsonl. The verdict lines answer ledger 121's open item: whether
the 0x61fac369 attribute is set (by whom, to what), whether 180cb2100 found a scripted camera, and what the cascade
setup read (the light's distances, the frustum's far, ShadowFarZ and csm_max_shadow_dist_override, the split and
cascade 0 after the attribute pass).
"""
import collections
import json
import os
import sys

path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.environ.get("TEMP", "."), "cubehost_run", "probe01", "frida.jsonl")
records = [json.loads(line) for line in open(path, encoding="utf-8") if line.strip()]
by = collections.defaultdict(list)
for r in records:
    by[r.get("kind", r.get("type", "?"))].append(r)

for kind in ("module", "hooked", "mismatch", "error"):
    for r in by.get(kind, []):
        print(kind, {k: v for k, v in r.items() if k != "kind"})
for kind in ("set", "camera", "enter", "far", "split", "d0"):
    rows = by.get(kind, [])
    print(f"\n{kind}: {len(rows)} distinct")
    for r in rows[:12]:
        print("  ", {k: v for k, v in r.items() if k != "kind"})

sets = [r for r in by.get("set", []) if r["name"] == "cascade0"]
cameras = by.get("camera", [])
print("\nverdict:")
print("  0x61fac369 set:", ", ".join(f"{r['value']} by {r['caller']}" for r in sets) or "never")
print("  scripted camera found:", sorted({r["found"] for r in cameras}) or "180cb2100 never called from 1808898d0")
print("  cascade 0 after the attribute pass:", sorted({r["d0"] for r in by.get("d0", [])}))
print("  M inputs (ShadowFarZ, override):", sorted({(r["shadowFarZ"], r["csm_max_shadow_dist_override"]) for r in by.get("far", [])}))
