# rdc_postvs.py -- a draw's vertex shader outputs (RenderDoc's post-VS data), for comparing two captures.
#   RDC_IN=<capture.rdc> RDC_OUT=<file.json> [RDC_EID=<eid>, default: the last draw] [RDC_COUNT=12]
#   qrenderdoc.exe --python tools/cubehost/rdc_postvs.py
# Writes, per output signature element, the first RDC_COUNT vertices' values of instance 0 (and 1 when drawn).
import json
import os
import struct
import traceback

import renderdoc as rd

src = os.environ["RDC_IN"]
dst = os.environ["RDC_OUT"]
want = int(os.environ.get("RDC_EID", "0"))
count = int(os.environ.get("RDC_COUNT", "12"))
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
    pipe = controller.GetPipelineState()
    refl = pipe.GetShaderReflection(rd.ShaderStage.Vertex)
    result["eid"] = action.eventId
    result["instances"] = []
    for inst in range(min(2, max(1, action.numInstances))):
        mesh = controller.GetPostVSData(inst, 0, rd.MeshDataStage.VSOut)
        data = controller.GetBufferData(mesh.vertexResourceId, mesh.vertexByteOffset, 0)
        verts = []
        for v in range(min(count, mesh.numIndices)):
            base = v * mesh.vertexByteStride
            row = {}
            offset = 0
            for sig in refl.outputSignature:
                n = sig.compCount
                fmt = "<%d%s" % (n, "I" if sig.varType == rd.VarType.UInt else "i" if sig.varType == rd.VarType.SInt else "f")
                row[sig.varName or sig.semanticName or str(sig.regIndex)] = list(struct.unpack_from(fmt, data, base + offset))
                offset += 4 * n
            verts.append(row)
        result["instances"].append({"stride": mesh.vertexByteStride, "numIndices": mesh.numIndices, "vertices": verts})
    controller.Shutdown()
    cap.Shutdown()
except Exception:
    result["error"] = traceback.format_exc()
with open(dst, "w") as f:
    json.dump(result, f, indent=1)
os._exit(0)
