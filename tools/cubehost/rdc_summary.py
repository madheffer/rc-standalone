# Runs inside RenderDoc's own Python (qrenderdoc.exe --python rdc_summary.py; RenderDoc ships no standalone module):
# a text summary of a cubehost capture, the action tree with each pass's render targets and the textures it made.
#   RDC_IN=<capture.rdc> RDC_OUT=<summary.txt> "C:\Program Files\RenderDoc\qrenderdoc.exe" --python rdc_summary.py
import os
import renderdoc as rd

src = os.environ["RDC_IN"]
out = open(os.environ["RDC_OUT"], "w")
try:
    cap = rd.OpenCaptureFile()
    result = cap.OpenFile(src, "", None)
    if result != rd.ResultCode.Succeeded:
        raise RuntimeError("open: %s" % result)
    result, controller = cap.OpenCapture(rd.ReplayOptions(), None)
    if result != rd.ResultCode.Succeeded:
        raise RuntimeError("replay: %s" % result)
    textures = {t.resourceId: t for t in controller.GetTextures()}
    names = {r.resourceId: r.name for r in controller.GetResources()}

    def texture(rid):
        if rid == rd.ResourceId.Null():
            return "-"
        t = textures.get(rid)
        if t is None:
            return names.get(rid, str(rid))
        return "%s %dx%d%s %s" % (names.get(rid, str(rid)), t.width, t.height,
                                 (" x%d" % t.arraysize) if t.arraysize > 1 else "", t.format.Name())

    counts = {"actions": 0, "draws": 0, "dispatches": 0}

    def walk(actions, depth):
        for a in actions:
            counts["actions"] += 1
            if a.flags & rd.ActionFlags.Drawcall:
                counts["draws"] += 1
            if a.flags & rd.ActionFlags.Dispatch:
                counts["dispatches"] += 1
            name = a.GetName(controller.GetStructuredFile())
            line = "%s%d %s" % ("  " * depth, a.eventId, name)
            if a.children:
                line += " [%d]" % len(a.children)
            elif a.flags & (rd.ActionFlags.Drawcall | rd.ActionFlags.Dispatch):
                if a.flags & rd.ActionFlags.Drawcall:
                    line += " idx %d inst %d" % (a.numIndices, a.numInstances)
                outs = [texture(o) for o in a.outputs if o != rd.ResourceId.Null()]
                if outs:
                    line += " -> " + ", ".join(outs)
                if a.depthOut != rd.ResourceId.Null():
                    line += " depth " + texture(a.depthOut)
            out.write(line + "\n")
            walk(a.children, depth + 1)

    walk(controller.GetRootActions(), 0)
    out.write("\n%(actions)d actions, %(draws)d draws, %(dispatches)d dispatches\n" % counts)
    out.write("\ntextures:\n")
    for t in sorted(textures.values(), key=lambda t: -t.width * t.height):
        out.write("  " + texture(t.resourceId) + " mips %d\n" % t.mips)
    controller.Shutdown()
    cap.Shutdown()
except Exception as e:
    out.write("error: %r\n" % e)
out.close()
os._exit(0)
