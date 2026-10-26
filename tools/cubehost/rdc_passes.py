# Runs inside RenderDoc's own Python (qrenderdoc.exe --python rdc_passes.py): every render pass of a cubehost capture
# (vkCmdBeginRendering) with its attachments, load and store ops and clear values, as the call recorded them. A
# chained replay from cleared targets starts each target its pass clears from that pass's clear value. The first line
# maps each image view to its image and VkFormat ({"views": {view: {"image", "format"}}}), since the attachments name
# views and a pass may render through a view of another format than the image's (the AO target's UNORM view).
#   RDC_IN=<capture.rdc> RDC_OUT=<passes.jsonl> qrenderdoc.exe --python rdc_passes.py
import json
import os
import traceback

import renderdoc as rd

src = os.environ["RDC_IN"]
dst = os.environ["RDC_OUT"]
log = open(dst + ".log", "w")


def plain(o):
    """An SDObject as JSON: structs by member name, arrays as lists, handles as resource names."""
    t = o.type.basetype
    if t == rd.SDBasic.Struct:
        return {o.GetChild(i).name: plain(o.GetChild(i)) for i in range(o.NumChildren())}
    if t == rd.SDBasic.Array:
        return [plain(o.GetChild(i)) for i in range(o.NumChildren())]
    if t == rd.SDBasic.Resource:
        return names.get(o.data.basic.id, str(o.data.basic.id))
    if t in (rd.SDBasic.UnsignedInteger, rd.SDBasic.Enum):
        return o.data.basic.u
    if t == rd.SDBasic.SignedInteger:
        return o.data.basic.i
    if t == rd.SDBasic.Float:
        return o.data.basic.d
    if t == rd.SDBasic.Boolean:
        return o.data.basic.b
    if t == rd.SDBasic.String:
        return o.data.str
    if t == rd.SDBasic.Null:
        return None
    # Unions (VkClearValue) and anything else: their members, if any.
    return {o.GetChild(i).name: plain(o.GetChild(i)) for i in range(o.NumChildren())} if o.NumChildren() else None


try:
    cap = rd.OpenCaptureFile()
    if cap.OpenFile(src, "", None) != rd.ResultCode.Succeeded:
        raise RuntimeError("open failed")
    result, controller = cap.OpenCapture(rd.ReplayOptions(), None)
    if result != rd.ResultCode.Succeeded:
        raise RuntimeError("replay: %s" % result)
    names = {r.resourceId: r.name for r in controller.GetResources()}
    sf = controller.GetStructuredFile()
    out = open(dst, "w")
    # The views' images from their creation chunks (a view's parent resource is the device).
    views = {}
    for ch in sf.chunks:
        if ch.name == "vkCreateImageView":
            args = {ch.GetChild(i).name: plain(ch.GetChild(i)) for i in range(ch.NumChildren())}
            info = args.get("CreateInfo", {})
            views[args.get("View")] = {"image": info.get("image"), "format": info.get("format")}
    out.write(json.dumps({"views": views}) + "\n")
    count = 0

    def walk(xs):
        global count
        for a in xs:
            if a.GetName(sf).startswith("vkCmdBeginRendering"):
                for ev in a.events:
                    ch = sf.chunks[ev.chunkIndex]
                    if ch.name.startswith("vkCmdBeginRendering"):
                        out.write(json.dumps({"eid": a.eventId, "args": {ch.GetChild(i).name: plain(ch.GetChild(i)) for i in range(ch.NumChildren())}}) + "\n")
                        count += 1
            walk(a.children)

    walk(controller.GetRootActions())
    out.close()
    controller.Shutdown()
    cap.Shutdown()
    log.write("done %d passes\n" % count)
except Exception:
    log.write("error: %s\n" % traceback.format_exc())
log.close()
os._exit(0)
