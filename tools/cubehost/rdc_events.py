# rdc_events.py -- a target's contents (hash) at each event before a draw, with the events' names: which event
# shows a render pass clear and which already holds the draw's output.
#   RDC_IN=<capture.rdc> RDC_OUT=<file.json> RDC_EID=<eid> [RDC_BACK=8]
#   qrenderdoc.exe --python tools/cubehost/rdc_events.py
import hashlib
import json
import os
import traceback

import renderdoc as rd

src = os.environ["RDC_IN"]
dst = os.environ["RDC_OUT"]
want = int(os.environ["RDC_EID"])
back = int(os.environ.get("RDC_BACK", "8"))
result = {}
try:
    cap = rd.OpenCaptureFile()
    if cap.OpenFile(src, "", None) != rd.ResultCode.Succeeded:
        raise RuntimeError("open failed")
    status, controller = cap.OpenCapture(rd.ReplayOptions(), None)
    if status != rd.ResultCode.Succeeded:
        raise RuntimeError("replay: %s" % status)
    sf = controller.GetStructuredFile()
    actions = {}

    def walk(xs):
        for a in xs:
            actions[a.eventId] = a
            walk(a.children)

    walk(controller.GetRootActions())
    action = actions[want]
    target = action.depthOut if action.depthOut != rd.ResourceId.Null() else action.outputs[0]
    names = {}
    for a in actions.values():
        for e in a.events:
            names[e.eventId] = sf.chunks[e.chunkIndex].name if e.chunkIndex < len(sf.chunks) else "?"
    rows = []
    for eid in range(want - back, want + 1):
        controller.SetFrameEvent(eid, True)
        data = controller.GetTextureData(target, rd.Subresource(0, 0, 0))
        rows.append({"eid": eid, "event": names.get(eid, ""), "sha1": hashlib.sha1(bytes(data)).hexdigest()[:12]})
    result = {"eid": want, "rows": rows}
    controller.Shutdown()
    cap.Shutdown()
except Exception:
    result["error"] = traceback.format_exc()
with open(dst, "w") as f:
    json.dump(result, f, indent=1)
os._exit(0)
