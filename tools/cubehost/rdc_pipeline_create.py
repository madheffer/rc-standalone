# rdc_pipeline_create.py -- how a draw's graphics pipeline was created: the vkCreateGraphicsPipelines chunk of the
# pipeline and of every library it links (flags, VkGraphicsPipelineLibraryCreateInfoEXT parts, library lists).
#   RDC_IN=<capture.rdc> RDC_OUT=<file.txt> RDC_EID=<eid>
#   qrenderdoc.exe --python tools/cubehost/rdc_pipeline_create.py
import os
import traceback

import renderdoc as rd

src = os.environ["RDC_IN"]
dst = os.environ["RDC_OUT"]
want = int(os.environ["RDC_EID"])
out = open(dst, "w")


def dump(obj, depth, keep):
    """A structured chunk's tree, skipping shader code and other large arrays."""
    name = obj.name
    if obj.NumChildren() == 0:
        b = obj.data.basic
        t = obj.type.basetype
        if t == rd.SDBasic.String:
            v = obj.data.str
        elif t == rd.SDBasic.Resource:
            v = b.id
        elif t == rd.SDBasic.Float:
            v = b.d
        elif t == rd.SDBasic.SignedInteger:
            v = b.i
        elif t == rd.SDBasic.Boolean:
            v = b.b
        else:
            v = b.u
        out.write("  " * depth + "%s = %s
" % (name, v))
        return
    if depth > 7 or (obj.NumChildren() > 64 and depth > 1):
        out.write("  " * depth + "%s [%d children]\n" % (name, obj.NumChildren()))
        return
    out.write("  " * depth + "%s:\n" % name)
    for i in range(obj.NumChildren()):
        c = obj.GetChild(i)
        if c.name in ("pCode",):
            continue
        dump(c, depth + 1, keep)


try:
    cap = rd.OpenCaptureFile()
    if cap.OpenFile(src, "", None) != rd.ResultCode.Succeeded:
        raise RuntimeError("open failed")
    status, controller = cap.OpenCapture(rd.ReplayOptions(), None)
    if status != rd.ResultCode.Succeeded:
        raise RuntimeError("replay: %s" % status)
    controller.SetFrameEvent(want, True)
    vk = controller.GetVulkanPipelineState()
    pipeline = vk.graphics.pipelineResourceId
    sf = controller.GetStructuredFile()
    descriptions = {r.resourceId: r for r in controller.GetResources()}
    seen = set()
    todo = [pipeline]
    while todo:
        rid = todo.pop()
        if rid in seen:
            continue
        seen.add(rid)
        desc = descriptions[rid]
        out.write("== %s %s\n" % (rid, desc.name))
        for idx in desc.initialisationChunks:
            chunk = sf.chunks[idx]
            out.write("chunk %s\n" % chunk.name)
            dump(chunk, 1, None)
        # follow the libraries it links
        for parent in desc.parentResources:
            todo.append(parent)
    controller.Shutdown()
    cap.Shutdown()
except Exception:
    out.write("error: %s\n" % traceback.format_exc())
out.close()
os._exit(0)
