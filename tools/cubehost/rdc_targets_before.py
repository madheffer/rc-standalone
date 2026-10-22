# rdc_targets_before.py -- fixes a dump made before 10-22: each single indirect draw's targets as they were before it
# (event eventId - 2; eventId - 1 is RenderDoc's internal "Indirect sub-command", which already holds the draw).
#   RDC_IN=<capture.rdc> RDC_DUMP_DIR=<rdc_replay.py output with RDC_DUMP=1>
#   qrenderdoc.exe --python tools/cubehost/rdc_targets_before.py
# Writes the blobs into <dir>/blobs and <dir>/targets_before.json: {eid: {target name: sha1}}, which
# CubemapFaceReplayTests prefers over its previous-action heuristic.
import hashlib
import json
import os
import traceback

import renderdoc as rd

src = os.environ["RDC_IN"]
out = os.environ["RDC_DUMP_DIR"]
fixed = {}
log = open(os.path.join(out, "targets_before.log"), "w")
try:
    cap = rd.OpenCaptureFile()
    if cap.OpenFile(src, "", None) != rd.ResultCode.Succeeded:
        raise RuntimeError("open failed")
    status, controller = cap.OpenCapture(rd.ReplayOptions(), None)
    if status != rd.ResultCode.Succeeded:
        raise RuntimeError("replay: %s" % status)
    names = {r.resourceId: r.name for r in controller.GetResources()}
    by_name = {v: k for k, v in names.items()}
    for line in open(os.path.join(out, "actions.jsonl")):
        rec = json.loads(line)
        if "Indirect(1)" not in rec["name"] or not rec.get("contents"):
            continue
        targets = [n for n in rec["contents"]["after"] if n in by_name and isinstance(rec["contents"]["after"][n], list)]
        if not targets:
            continue
        controller.SetFrameEvent(rec["eid"] - 2, True)
        entry = {}
        for n in targets:
            data = bytes(controller.GetTextureData(by_name[n], rd.Subresource(0, 0, 0)))
            h = hashlib.sha1(data).hexdigest()
            path = os.path.join(out, "blobs", h + ".bin")
            if not os.path.exists(path):
                with open(path, "wb") as f:
                    f.write(data)
            entry[n] = h
        fixed[str(rec["eid"])] = entry
        log.write("%d %s\n" % (rec["eid"], entry))
    controller.Shutdown()
    cap.Shutdown()
except Exception:
    log.write("error: %s\n" % traceback.format_exc())
with open(os.path.join(out, "targets_before.json"), "w") as f:
    json.dump(fixed, f, indent=1)
log.close()
os._exit(0)
