# Runs inside RenderDoc's own Python (qrenderdoc.exe --python rdc_texture_data.py): saves one image of a cubehost
# capture, every mip and slice, as raw bytes in subresource order (slice-major, mip within slice), to compare an
# engine texture with the file it came from.
#   RDC_IN=<capture.rdc> RDC_TEX=<resource name, e.g. "2D Array Image 5917"> RDC_OUT=<file.bin> [RDC_EID=<event>]
#   qrenderdoc.exe --python rdc_texture_data.py
import os
import traceback
import renderdoc as rd

src = os.environ["RDC_IN"]
want = os.environ["RDC_TEX"]
dst = os.environ["RDC_OUT"]
log = open(dst + ".log", "w")
try:
    cap = rd.OpenCaptureFile()
    if cap.OpenFile(src, "", None) != rd.ResultCode.Succeeded:
        raise RuntimeError("open failed")
    result, controller = cap.OpenCapture(rd.ReplayOptions(), None)
    if result != rd.ResultCode.Succeeded:
        raise RuntimeError("replay: %s" % result)
    if os.environ.get("RDC_EID"):
        controller.SetFrameEvent(int(os.environ["RDC_EID"]), True)
    names = {r.resourceId: r.name for r in controller.GetResources()}
    tex = next(t for t in controller.GetTextures() if names.get(t.resourceId) == want)
    with open(dst, "wb") as out:
        for s in range(tex.arraysize):
            for m in range(tex.mips):
                data = controller.GetTextureData(tex.resourceId, rd.Subresource(m, s, 0))
                out.write(bytes(data))
                log.write("slice %d mip %d: %d bytes\n" % (s, m, len(data)))
    controller.Shutdown()
    cap.Shutdown()
    log.write("done\n")
except Exception:
    log.write("error: %s\n" % traceback.format_exc())
log.close()
os._exit(0)
