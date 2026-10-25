# Runs inside RenderDoc's own Python (qrenderdoc.exe --python rdc_indirect.py): every indirect draw of a cubehost
# capture with the call's own arguments (buffer, byte offset, draw count, stride) and the commands it reads
# (VkDrawIndexedIndirectCommand: index count, instance count, first index, vertex offset, first instance). The
# offset over 20 is the command's slot in the frame's indirect array (CAggregateSceneObjectDesc's "non-culled
# draws", docs/CUBEMAPS.md).
#   RDC_IN=<capture.rdc> RDC_OUT=<file.jsonl> [RDC_LAST=<eid>] qrenderdoc.exe --python rdc_indirect.py
# Also writes <file>.buffers/<name>_<eid>.bin: each indirect buffer whole, at its first use in each render pass.
import json
import os
import struct
import traceback

import renderdoc as rd

src = os.environ["RDC_IN"]
dst = os.environ["RDC_OUT"]
last = int(os.environ.get("RDC_LAST", "1000000000"))
bufdir = dst + ".buffers"
os.makedirs(bufdir, exist_ok=True)
log = open(dst + ".log", "w")


def params(chunk):
    """The call's arguments by name, as plain values (handles as resource names)."""
    out = {}
    for i in range(chunk.NumChildren()):
        c = chunk.GetChild(i)
        t = c.type.basetype
        if t == rd.SDBasic.Resource:
            out[c.name] = names.get(c.data.basic.id, str(c.data.basic.id))
        elif t in (rd.SDBasic.UnsignedInteger, rd.SDBasic.Enum):
            out[c.name] = c.data.basic.u
        elif t == rd.SDBasic.SignedInteger:
            out[c.name] = c.data.basic.i
    return out


try:
    cap = rd.OpenCaptureFile()
    if cap.OpenFile(src, "", None) != rd.ResultCode.Succeeded:
        raise RuntimeError("open failed")
    log.write("opening\n")
    log.flush()
    result, controller = cap.OpenCapture(rd.ReplayOptions(), None)
    if result != rd.ResultCode.Succeeded:
        raise RuntimeError("replay: %s" % result)
    names = {r.resourceId: r.name for r in controller.GetResources()}
    ids = {r.name: r.resourceId for r in controller.GetResources()}
    sf = controller.GetStructuredFile()
    draws = []

    def walk(xs):
        for a in xs:
            n = a.GetName(sf)
            # A multi-draw's parent carries no draw flag (its sub-commands do), so match the call by name.
            if (n.startswith("vkCmdDrawIndexedIndirect") or n.startswith("vkCmdDrawIndirect")) and not n.endswith(" end"):
                draws.append(a)
            walk(a.children)

    walk(controller.GetRootActions())
    out = open(dst, "w")
    dumped = set()
    for a in draws:
        if a.eventId > last:
            break
        chunk = None
        for ev in a.events:
            ch = sf.chunks[ev.chunkIndex]
            if "Indirect" in ch.name:
                chunk = ch
        p = params(chunk) if chunk is not None else {}
        rec = {"eid": a.eventId, "name": a.GetName(sf), "call": chunk.name if chunk is not None else None, "args": p}
        buf = p.get("buffer")
        if buf in ids:
            controller.SetFrameEvent(a.eventId, True)
            count = p.get("count", p.get("drawCount", 1)) or 1
            stride = p.get("stride", 20) or 20
            data = bytes(controller.GetBufferData(ids[buf], p.get("offset", 0), stride * (count - 1) + 20))
            rec["commands"] = [list(struct.unpack_from("<IIIiI", data, k * stride)) for k in range(count)]
            pass_key = (buf, str(a.parent.eventId) if a.parent else "")
            if pass_key not in dumped:
                dumped.add(pass_key)
                whole = bytes(controller.GetBufferData(ids[buf], 0, 0))
                with open(os.path.join(bufdir, "%s_%d.bin" % (buf.replace(" ", "_"), a.eventId)), "wb") as f:
                    f.write(whole)
        out.write(json.dumps(rec) + "\n")
        out.flush()
    out.close()
    controller.Shutdown()
    cap.Shutdown()
    log.write("done %d draws\n" % len(draws))
except Exception:
    log.write("error: %s\n" % traceback.format_exc())
log.close()
os._exit(0)
