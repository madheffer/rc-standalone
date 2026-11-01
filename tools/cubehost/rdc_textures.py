# Runs inside RenderDoc's own Python (qrenderdoc.exe --python rdc_textures.py): lists every image of a cubehost
# capture (size, mips, format) with no replay of the actions, so two captures' texture residency can be compared.
#   RDC_IN=<capture.rdc> RDC_OUT=<file.json> qrenderdoc.exe --python rdc_textures.py
# Writes {"textures": {name: {width, height, depth, arraysize, mips, format, cubemap}}} and, at the end, a count
# of the block-compressed images by width ("bc_widths").
import json
import os
import traceback
import renderdoc as rd

src = os.environ["RDC_IN"]
dst = os.environ["RDC_OUT"]
log = open(dst + ".log", "w")
try:
    cap = rd.OpenCaptureFile()
    if cap.OpenFile(src, "", None) != rd.ResultCode.Succeeded:
        raise RuntimeError("open failed")
    result, controller = cap.OpenCapture(rd.ReplayOptions(), None)
    if result != rd.ResultCode.Succeeded:
        raise RuntimeError("replay: %s" % result)
    names = {r.resourceId: r.name for r in controller.GetResources()}
    textures = {}
    widths = {}
    for t in controller.GetTextures():
        f = t.format.Name()
        textures[names.get(t.resourceId, str(t.resourceId))] = {
            "width": t.width, "height": t.height, "depth": t.depth, "arraysize": t.arraysize, "mips": t.mips,
            "format": f, "cubemap": t.cubemap}
        if f.startswith("BC"):
            k = "%dx%d" % (t.width, t.height)
            widths[k] = widths.get(k, 0) + 1
    with open(dst, "w") as out:
        json.dump({"textures": textures, "bc_widths": widths}, out, indent=1)
    controller.Shutdown()
    cap.Shutdown()
    log.write("done\n")
except Exception:
    log.write("error: %s\n" % traceback.format_exc())
log.close()
os._exit(0)
