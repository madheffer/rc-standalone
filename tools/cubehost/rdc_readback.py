# Runs inside RenderDoc's own Python (qrenderdoc.exe --python rdc_readback.py): writes the image each
# vkCmdCopyImageToBuffer of a cubehost capture reads back (one per face, the frame CubemapFaceExr starts from).
#   RDC_IN=<capture.rdc> RDC_OUT=<dir> qrenderdoc.exe --python rdc_readback.py
# Writes <dir>/readback_<n>.bin (the source image's mip 0 slice 0 at the copy, as the GPU holds it) and
# <dir>/readback.json (per copy: eid, the source's name, size and format, the buffer region).
import json
import os
import traceback
import renderdoc as rd

src = os.environ["RDC_IN"]
dst = os.environ["RDC_OUT"]
os.makedirs(dst, exist_ok=True)
log = open(os.path.join(dst, "readback_log.txt"), "w")

try:
    cap = rd.OpenCaptureFile()
    if cap.OpenFile(src, "", None) != rd.ResultCode.Succeeded:
        raise RuntimeError("open failed")
    log.write("opening\n")
    log.flush()
    result, controller = cap.OpenCapture(rd.ReplayOptions(), None)
    if result != rd.ResultCode.Succeeded:
        raise RuntimeError("replay: %s" % result)
    log.write("replay open\n")
    log.flush()
    names = {r.resourceId: r.name for r in controller.GetResources()}
    textures = {t.resourceId: t for t in controller.GetTextures()}
    sf = controller.GetStructuredFile()
    copies = []

    def walk(xs):
        for a in xs:
            if (a.flags & rd.ActionFlags.Copy) and "CopyImageToBuffer" in a.GetName(sf):
                copies.append(a)
            walk(a.children)

    walk(controller.GetRootActions())
    recs = []
    for n, a in enumerate(copies):
        controller.SetFrameEvent(a.eventId, True)
        t = textures.get(a.copySource)
        data = bytes(controller.GetTextureData(a.copySource, rd.Subresource(0, 0, 0)))
        with open(os.path.join(dst, "readback_%d.bin" % n), "wb") as f:
            f.write(data)
        recs.append({"eid": a.eventId, "source": names.get(a.copySource, str(a.copySource)),
                     "destination": names.get(a.copyDestination, str(a.copyDestination)),
                     "width": t.width if t else None, "height": t.height if t else None,
                     "format": t.format.Name() if t else None, "bytes": len(data)})
        log.write("copy %d at %d: %s\n" % (n, a.eventId, recs[-1]))
        log.flush()
    with open(os.path.join(dst, "readback.json"), "w") as f:
        json.dump(recs, f, indent=1)
    controller.Shutdown()
    cap.Shutdown()
    log.write("done\n")
except Exception:
    log.write("error: %s\n" % traceback.format_exc())
log.close()
os._exit(0)
