# rdc_bindings.py -- the pixel (and vertex) shader's used descriptors at one draw: images with format, mips and view
# range, samplers with their state, for comparing our replay's capture with the game's.
#   RDC_IN=<capture.rdc> RDC_OUT=<file.json> [RDC_EID=<eid>, default: the last draw]
#   qrenderdoc.exe --python tools/cubehost/rdc_bindings.py
import json
import os
import traceback

import renderdoc as rd

src = os.environ["RDC_IN"]
dst = os.environ["RDC_OUT"]
want = int(os.environ.get("RDC_EID", "0"))
result = {}


def plain(o, depth=0):
    if depth > 4:
        return str(o)
    if isinstance(o, (int, float, str, bool)) or o is None:
        return o
    if isinstance(o, (list, tuple)):
        return [plain(x, depth + 1) for x in o]
    out = {}
    for k in dir(o):
        if k.startswith("_") or k in ("this", "thisown"):
            continue
        try:
            v = getattr(o, k)
        except Exception:
            continue
        if callable(v):
            continue
        out[k] = plain(v, depth + 1)
    return out


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
    pipe = controller.GetPipelineState()
    textures = {t.resourceId: t for t in controller.GetTextures()}
    result["eid"] = action.eventId
    for stage, key in ((rd.ShaderStage.Vertex, "vs"), (rd.ShaderStage.Pixel, "ps")):
        images = []
        for u in pipe.GetReadOnlyResources(stage):
            d = u.descriptor
            t = textures.get(d.resource)
            images.append({"element": u.access.arrayElement, "view": plain(d),
                           "texture": {"format": t.format.Name(), "width": t.width, "height": t.height,
                                       "mips": t.mips} if t else None})
        samplers = [{"element": u.access.arrayElement, "sampler": plain(u.sampler)} for u in pipe.GetSamplers(stage)]
        result[key] = {"images": images, "samplers": samplers}
    controller.Shutdown()
    cap.Shutdown()
except Exception:
    result["error"] = traceback.format_exc()
with open(dst, "w") as f:
    json.dump(result, f, indent=1, default=str)
os._exit(0)
