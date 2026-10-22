# Runs inside RenderDoc's own Python (qrenderdoc.exe --python rdc_replay.py): replays a cubehost capture and writes,
# for every draw and dispatch, the state a port needs to render the face itself: the Vulkan render state, push
# constants, every constant block / read-only / read-write / sampler descriptor the shaders use (with the bytes of
# each constant range), the vertex and index buffers and the draw's resolved (indirect) arguments.
#   RDC_IN=<capture.rdc> RDC_OUT=<dir> [RDC_FIRST=<eid>] [RDC_LAST=<eid>] qrenderdoc.exe --python rdc_replay.py
# Writes <dir>/actions.jsonl (one object per action) and <dir>/blobs/<sha1>.bin (constant ranges, deduplicated).
import enum
import hashlib
import json
import os
import traceback
import renderdoc as rd

src = os.environ["RDC_IN"]
dst = os.environ["RDC_OUT"]
first = int(os.environ.get("RDC_FIRST", "0"))
last = int(os.environ.get("RDC_LAST", "1000000000"))
blob_cap = 4 << 20
os.makedirs(os.path.join(dst, "blobs"), exist_ok=True)
log = open(os.path.join(dst, "log.txt"), "w")
SKIP = {"acquire", "append", "disown", "next", "own", "this", "thisown"}


def plain(o, depth=0):
    """A JSON-able copy of a SWIG struct: every non-callable public attribute, recursively."""
    if o is None or isinstance(o, (bool, int, float, str)):
        return o
    if isinstance(o, (bytes, bytearray)):
        return bytes(o).hex()
    if isinstance(o, rd.ResourceId):
        return names.get(o, str(o)) if o != rd.ResourceId.Null() else None
    if depth > 7:
        return repr(o)
    if isinstance(o, (list, tuple)) or (hasattr(o, "__len__") and hasattr(o, "__getitem__")):
        try:  # rdcarray_of_* has no __iter__
            return [plain(o[i], depth + 1) for i in range(len(o))]
        except Exception:
            pass
    if hasattr(o, "Name") and callable(o.Name) and type(o).__name__ == "ResourceFormat":
        return o.Name()
    if isinstance(o, enum.Enum):
        return str(o)
    d = {}
    for a in dir(o):
        if a.startswith("_") or a in SKIP:
            continue
        try:
            v = getattr(o, a)
        except Exception:
            continue
        if callable(v) and not isinstance(v, rd.ResourceId):
            continue
        d[a] = plain(v, depth + 1)
    return d if d else str(o)


def blob(rid, offset, size):
    if rid == rd.ResourceId.Null() or size == 0:
        return None
    size = min(size, blob_cap)
    data = bytes(controller.GetBufferData(rid, offset, size))
    h = hashlib.sha1(data).hexdigest()
    p = os.path.join(dst, "blobs", h + ".bin")
    if not os.path.exists(p):
        with open(p, "wb") as f:
            f.write(data)
    return {"sha1": h, "size": len(data)}


STAGES = [rd.ShaderStage.Vertex, rd.ShaderStage.Pixel, rd.ShaderStage.Compute]


def used(u):
    a, d = u.access, u.descriptor
    e = {"access": plain(a), "descriptor": plain(d)}
    return e, d


def stage_state(pipe, stage):
    refl = pipe.GetShaderReflection(stage)
    if refl is None:
        return None
    s = {"shader": names.get(pipe.GetShader(stage), str(pipe.GetShader(stage))),
         "entry": pipe.GetShaderEntryPoint(stage)}
    cbs = []
    for u in pipe.GetConstantBlocks(stage):
        e, d = used(u)
        try:
            e["bytes"] = blob(d.resource, d.byteOffset, d.byteSize)
        except Exception as ex:
            e["bytes_error"] = repr(ex)
        idx = u.access.index
        if 0 <= idx < len(refl.constantBlocks):
            cb = refl.constantBlocks[idx]
            e["block"] = {"name": cb.name, "bufferBacked": cb.bufferBacked, "byteSize": cb.byteSize,
                          "fixedBindSetOrSpace": cb.fixedBindSetOrSpace, "fixedBindNumber": cb.fixedBindNumber}
        cbs.append(e)
    s["constantBlocks"] = cbs
    def bound(e, table, idx):
        if 0 <= idx < len(table):
            r = table[idx]
            e["bind"] = {"name": r.name, "set": r.fixedBindSetOrSpace, "binding": r.fixedBindNumber,
                         "arraySize": r.bindArraySize}
        return e

    s["readOnly"] = [bound(used(u)[0], refl.readOnlyResources, u.access.index)
                     for u in pipe.GetReadOnlyResources(stage)]
    s["readWrite"] = [bound(used(u)[0], refl.readWriteResources, u.access.index)
                      for u in pipe.GetReadWriteResources(stage)]
    s["samplers"] = [bound({"access": plain(u.access), "sampler": plain(u.sampler)}, refl.samplers,
                           u.access.index) for u in pipe.GetSamplers(stage)]
    return s


dump = os.environ.get("RDC_DUMP") == "1"


def texture_blobs(rid):
    """Every subresource of a texture at the current event, as blobs (3D mips come back whole)."""
    t = textures.get(rid)
    if t is None:
        return None
    subs = []
    for mip in range(t.mips):
        for sl in range(max(1, t.arraysize)):
            data = bytes(controller.GetTextureData(rid, rd.Subresource(mip, sl, 0)))
            h = hashlib.sha1(data).hexdigest()
            p = os.path.join(dst, "blobs", h + ".bin")
            if not os.path.exists(p):
                with open(p, "wb") as f:
                    f.write(data)
            subs.append({"mip": mip, "slice": sl, "sha1": h, "size": len(data)})
    return subs


def buffer_whole(rid):
    if rid == rd.ResourceId.Null() or rid not in buffers:
        return None
    data = bytes(controller.GetBufferData(rid, 0, 0))
    h = hashlib.sha1(data).hexdigest()
    p = os.path.join(dst, "blobs", h + ".bin")
    if not os.path.exists(p):
        with open(p, "wb") as f:
            f.write(data)
    return {"sha1": h, "size": len(data)}


def contents(a, pipe, compute):
    """The resources a draw reads (at its event, before it runs) and its targets after it."""
    c = {"before": {}, "after": {}}
    stages = [rd.ShaderStage.Compute] if compute else [rd.ShaderStage.Vertex, rd.ShaderStage.Pixel]
    rids = set()
    bufs = set()
    for st in stages:
        for u in pipe.GetReadOnlyResources(st):
            rids.add(u.descriptor.resource)
        for u in pipe.GetReadWriteResources(st):
            (bufs if u.descriptor.resource in buffers else rids).add(u.descriptor.resource)
    if not compute:
        ib = pipe.GetIBuffer()
        bufs.add(ib.resourceId)
        for v in pipe.GetVBuffers():
            bufs.add(v.resourceId)
    outs = [o for o in a.outputs if o != rd.ResourceId.Null()]
    if a.depthOut != rd.ResourceId.Null():
        outs.append(a.depthOut)
    # The bindings were read at the draw's event; their contents are the ones the draw sees.
    controller.SetFrameEvent(a.eventId - 1, True)
    for r in sorted(rids | set(outs), key=str):
        if r != rd.ResourceId.Null():
            c["before"][names.get(r, str(r))] = texture_blobs(r)
    for r in sorted(bufs, key=str):
        if r != rd.ResourceId.Null():
            c["before"][names.get(r, str(r))] = buffer_whole(r)
    controller.SetFrameEvent(a.eventId, True)
    for r in outs:
        c["after"][names.get(r, str(r))] = texture_blobs(r)
    if compute:  # a dispatch's outputs are its read-write buffers
        for r in sorted(bufs, key=str):
            c["after"][names.get(r, str(r))] = buffer_whole(r)
    return c


try:
    cap = rd.OpenCaptureFile()
    if cap.OpenFile(src, "", None) != rd.ResultCode.Succeeded:
        raise RuntimeError("open failed")
    result, controller = cap.OpenCapture(rd.ReplayOptions(), None)
    if result != rd.ResultCode.Succeeded:
        raise RuntimeError("replay: %s" % result)
    names = {r.resourceId: r.name for r in controller.GetResources()}
    textures = {t.resourceId: t for t in controller.GetTextures()}
    buffers = {b.resourceId: b for b in controller.GetBuffers()}
    sf = controller.GetStructuredFile()
    out = open(os.path.join(dst, "actions.jsonl"), "w")

    actions = []

    def walk(xs):
        for a in xs:
            if a.flags & (rd.ActionFlags.Drawcall | rd.ActionFlags.Dispatch | rd.ActionFlags.Clear |
                          rd.ActionFlags.Copy):
                actions.append(a)
            walk(a.children)

    walk(controller.GetRootActions())
    log.write("%d actions\n" % len(actions))
    for a in actions:
        if a.eventId < first or a.eventId > last:
            continue
        rec = {"eid": a.eventId, "name": a.GetName(sf), "flags": str(a.flags),
               "numIndices": a.numIndices, "numInstances": a.numInstances, "indexOffset": a.indexOffset,
               "baseVertex": a.baseVertex, "vertexOffset": a.vertexOffset, "instanceOffset": a.instanceOffset,
               "drawIndex": a.drawIndex, "dispatchDimension": list(a.dispatchDimension),
               "outputs": [names.get(o, str(o)) for o in a.outputs if o != rd.ResourceId.Null()],
               "depthOut": names.get(a.depthOut) if a.depthOut != rd.ResourceId.Null() else None}
        if a.flags & (rd.ActionFlags.Drawcall | rd.ActionFlags.Dispatch):
            try:
                controller.SetFrameEvent(a.eventId, True)
                vk = controller.GetVulkanPipelineState()
                pipe = controller.GetPipelineState()
                compute = bool(a.flags & rd.ActionFlags.Dispatch)
                rec["pushconsts"] = bytes(vk.pushconsts).hex()
                if compute:
                    rec["pipeline"] = plain(vk.compute)
                    rec["stages"] = {"cs": stage_state(pipe, rd.ShaderStage.Compute)}
                else:
                    for k in ("rasterizer", "depthStencil", "colorBlend", "inputAssembly", "multisample",
                              "viewportScissor", "vertexInput", "currentPass"):
                        rec[k] = plain(getattr(vk, k))
                    rec["pipeline"] = plain(vk.graphics)
                    rec["stages"] = {"vs": stage_state(pipe, rd.ShaderStage.Vertex),
                                     "ps": stage_state(pipe, rd.ShaderStage.Pixel)}
                    ib = pipe.GetIBuffer()
                    rec["ibuffer"] = plain(ib)
                    rec["vbuffers"] = [plain(v) for v in pipe.GetVBuffers()]
                    rec["vertexInputs"] = [plain(v) for v in pipe.GetVertexInputs()]
                if dump:
                    rec["contents"] = contents(a, pipe, compute)
            except Exception:
                rec["error"] = traceback.format_exc()
        out.write(json.dumps(rec) + "\n")
        out.flush()
    out.close()
    with open(os.path.join(dst, "resources.json"), "w") as f:
        json.dump({"textures": {names.get(t.resourceId, str(t.resourceId)): {
            "width": t.width, "height": t.height, "depth": t.depth, "arraysize": t.arraysize, "mips": t.mips,
            "format": t.format.Name(), "cubemap": t.cubemap} for t in controller.GetTextures()},
            "buffers": {names.get(b.resourceId, str(b.resourceId)): b.length for b in controller.GetBuffers()}},
            f, indent=1)
    controller.Shutdown()
    cap.Shutdown()
    log.write("done\n")
except Exception:
    log.write("error: %s\n" % traceback.format_exc())
log.close()
os._exit(0)
