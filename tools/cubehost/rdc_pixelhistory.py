# rdc_pixelhistory.py -- RenderDoc's pixel history of one texel of a draw's depth (or colour) target.
#   RDC_IN=<capture.rdc> RDC_OUT=<file.json> RDC_X=<x> RDC_Y=<y> [RDC_EID=<eid>, default: the last draw]
#   qrenderdoc.exe --python tools/cubehost/rdc_pixelhistory.py
# Each modification up to that draw: its event, the fragment's flags (discarded, depth/stencil test failed...) and
# the values before and after.
import json
import os
import traceback

import renderdoc as rd

src = os.environ["RDC_IN"]
dst = os.environ["RDC_OUT"]
want = int(os.environ.get("RDC_EID", "0"))
x, y = int(os.environ["RDC_X"]), int(os.environ["RDC_Y"])
result = {}
try:
    cap = rd.OpenCaptureFile()
    if cap.OpenFile(src, "", None) != rd.ResultCode.Succeeded:
        raise RuntimeError("open failed")
    status, controller = cap.OpenCapture(rd.ReplayOptions(), None)
    if status != rd.ResultCode.Succeeded:
        raise RuntimeError("replay: %s" % status)
    draws = []

    def walk(xs):
        for a in xs:
            if a.flags & rd.ActionFlags.Drawcall:
                draws.append(a)
            walk(a.children)

    walk(controller.GetRootActions())
    action = next((a for a in draws if a.eventId == want), None) if want else draws[-1]
    controller.SetFrameEvent(action.eventId, True)
    target = action.depthOut if action.depthOut != rd.ResourceId.Null() else action.outputs[0]
    history = controller.PixelHistory(target, x, y, rd.Subresource(0, 0, 0), rd.CompType.Typeless)
    mods = []
    for m in history:
        if m.eventId != action.eventId:
            continue
        row = {k: getattr(m, k) for k in ("eventId", "primitiveID", "shaderDiscarded", "depthTestFailed",
                                            "stencilTestFailed", "backfaceCulled", "scissorClipped",
                                            "depthClipped", "viewClipped", "sampleMasked", "unboundPS")
               if hasattr(m, k)}
        row.update({"preDepth": m.preMod.depth, "postDepth": m.postMod.depth, "shaderDepth": m.shaderOut.depth})
        mods.append(row)
    result = {"eid": action.eventId, "modifications": mods}
    controller.Shutdown()
    cap.Shutdown()
except Exception:
    result["error"] = traceback.format_exc()
with open(dst, "w") as f:
    json.dump(result, f, indent=1)
os._exit(0)
