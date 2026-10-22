# rdc_debugpixel.py -- RenderDoc's pixel shader debugger at one pixel of one draw: every variable's last value.
#   RDC_IN=<capture.rdc> RDC_OUT=<file.json> RDC_X=<x> RDC_Y=<y> [RDC_EID=<eid>, default: the last draw]
#   qrenderdoc.exe --python tools/cubehost/rdc_debugpixel.py
# For comparing our device's replay of a draw with the game's capture: the same shader at the same pixel.
import json
import os
import traceback

import renderdoc as rd

src = os.environ["RDC_IN"]
dst = os.environ["RDC_OUT"]
want = int(os.environ.get("RDC_EID", "0"))
x, y = int(os.environ["RDC_X"]), int(os.environ["RDC_Y"])
result = {}


def flat(v):
    """A ShaderVariable as plain numbers: its members, or its first rows x columns floats/ints."""
    if len(v.members):
        return {m.name: flat(m) for m in v.members}
    n = max(1, v.rows) * max(1, v.columns)
    if v.type in (rd.VarType.UInt, rd.VarType.Bool):
        return list(v.value.u32v[:n])
    if v.type == rd.VarType.SInt:
        return list(v.value.s32v[:n])
    return list(v.value.f32v[:n])


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
    inputs = rd.DebugPixelInputs()
    trace = controller.DebugPixel(x, y, inputs)
    result["eid"] = action.eventId
    if trace is None or trace.debugger is None:
        result["error"] = "no trace (no fragment at that pixel?)"
    else:
        result["inputs"] = {v.name: flat(v) for v in trace.inputs}
        last = {}
        steps = 0
        while True:
            states = controller.ContinueDebug(trace.debugger)
            if not states:
                break
            for s in states:
                steps += 1
                for change in s.changes:
                    after = change.after
                    if after.name:
                        last[after.name] = flat(after)
        result["steps"] = steps
        result["variables"] = last
        controller.FreeDebugger(trace.debugger)
    controller.Shutdown()
    cap.Shutdown()
except Exception:
    result["error"] = traceback.format_exc()
with open(dst, "w") as f:
    json.dump(result, f, indent=1)
os._exit(0)
