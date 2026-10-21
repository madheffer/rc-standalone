# The frame of a cubehost capture as a list, without RenderDoc or a GPU (rdc_stream.py reads the file): every
# command in execution order with its command buffer, the pipeline each draw and dispatch uses (resolved through
# pipeline libraries to the shader modules), and the engine's own annotations (the game calls RenderDoc's
# SetCommandAnnotation: batch list descriptors, flags, layer names), which a release build's capture carries in
# place of debug markers.
#   python rdc_actions.py <capture.rdc> [modules.txt] > actions.txt
# modules.txt: "<module resource id hex> <name>" lines (from matching the modules' SPIR-V against the .vcs files,
# see docs/CUBEMAPS.md); unnamed modules print as their id.
import os
import struct
import sys

import rdc_stream

HERE = os.path.dirname(os.path.abspath(__file__))
NAMES = dict(l.split() for l in open(os.path.join(HERE, "rdc_vkchunks.txt")))   # VulkanChunk, RenderDoc v1.44
NAMES.update({"1": "DriverInit", "2": "InitialContentsList", "3": "InitialContents", "4": "CaptureBegin",
              "5": "CaptureScope", "6": "CaptureEnd"})
CAPTURE_BEGIN = 4
SKIP = {"vkCmdPipelineBarrier2", "vkCmdSetStencilCompareMask", "vkCmdSetStencilWriteMask", "vkCmdSetStencilReference"}


def annotation(d):
    # Serialise_SetCommandAnnotation: cmd (u64), key (u32 length + bytes), valueType, valueVectorWidth,
    # then the value: a string as u32 length + bytes, anything else as 8 bytes and the 32-byte value union.
    (n,) = struct.unpack_from("<I", d, 8)
    key = d[12:12 + n].decode(errors="replace")
    p = 12 + n
    vtype, width = struct.unpack_from("<II", d, p)
    p += 8
    if vtype == 8:
        (m,) = struct.unpack_from("<I", d, p)
        return key, d[p + 4:p + 4 + m].decode(errors="replace")
    fmt = {1: "?", 2: "i", 3: "I", 4: "q", 5: "Q", 6: "f", 7: "d", 9: "Q"}.get(vtype)
    if fmt is None or p + 8 + 32 > len(d):
        return key, "-"
    vals = struct.unpack_from("<%d%s" % (max(width, 1), fmt), d, p + 8)
    return key, " ".join(("%x" % v) if vtype in (5, 9) else str(v) for v in vals)


def main():
    path = sys.argv[1]
    modules = {}
    if len(sys.argv) > 2:
        for line in open(sys.argv[2]):
            if line.strip():
                rid, name = line.split(None, 1)
                modules[int(rid, 16)] = name.strip()
    cap = rdc_stream.Capture(path)
    pipes = {}
    framed = False
    actions = []
    keep = lambda cid, size: size < (1 << 16) and cid != 3
    for i, ch in enumerate(rdc_stream.chunks(rdc_stream.Stream(cap.pages()), keep)):
        name = NAMES.get(str(ch.id), str(ch.id))
        d = ch.data
        if ch.id == 1019 and d is not None:
            modules.setdefault(struct.unpack_from("<Q", d, len(d) - 8 if d[-8:] != bytes(8) else len(d) - 16)[0], None)
        if name in ("vkCreateGraphicsPipelines", "vkCreateComputePipelines") and d is not None:
            # the created pipeline is the last resource id (566-byte library creates end with one more field)
            created = struct.unpack_from("<Q", d, len(d) - (24 if len(d) == 566 else 16))[0]
            pipes[created] = d
        if ch.id == CAPTURE_BEGIN:
            framed = True
        if not framed or d is None or not name.startswith(("vkCmd", "SetCommandAnnotation", "vkBegin", "vkEnd", "vkQueue")):
            continue
        actions.append((i, name, d))

    known = set(pipes) | {m for m in modules}

    def shaders(pid, seen=()):
        d = pipes.get(pid)
        if d is None:
            return []
        out = []
        for k in range(len(d) - 7):
            v = struct.unpack_from("<Q", d, k)[0]
            if v == pid or v not in known:
                continue
            if v in modules:
                out.append(modules[v] or "module %x" % v)
            elif v not in seen:
                out += shaders(v, seen + (pid,))
        return sorted(set(out))

    # Execution order: each command buffer's recording (vkBeginCommandBuffer .. vkEndCommandBuffer) plays
    # where a vkQueueSubmit lists it, so recordings made on several threads come out in GPU order.
    recordings, open_ = {}, {}
    order = []
    for i, name, d in actions:
        cb = struct.unpack_from("<Q", d, 0)[0] if len(d) >= 8 else 0
        if name == "vkBeginCommandBuffer":
            open_[cb] = []
        elif name == "vkEndCommandBuffer":
            recordings[cb] = open_.pop(cb, [])
        elif name in ("vkQueueSubmit", "vkQueueSubmit2"):
            for cbs in submitted(d, set(recordings)):
                order.append((i, "vkQueueSubmit", d, cbs))
                for c in cbs:
                    order.extend((j, n, e, None) for j, n, e in recordings.get(c, []))
        elif cb in open_:
            open_[cb].append((i, name, d))

    bound = {}
    for i, name, d, cbs in order:
        cb = struct.unpack_from("<Q", d, 0)[0] if len(d) >= 8 else 0
        if name in SKIP:
            continue
        if name == "vkQueueSubmit":
            print("%d  submit %s" % (i, " ".join("%x" % c for c in cbs)))
            continue
        if name == "SetCommandAnnotation":
            key, value = annotation(d)
            print("%d  cb %x  @ %s = %s" % (i, cb, key, value))
            continue
        extra = ""
        if name == "vkCmdBindPipeline":
            point, pid = struct.unpack_from("<IQ", d, 8)
            bound[(cb, point)] = pid
            extra = "%s %x: %s" % ("compute" if point else "graphics", pid, ", ".join(shaders(pid)))
        elif name == "vkCmdDraw":
            extra = "verts %d inst %d first %d %d" % struct.unpack_from("<4I", d, 8)
        elif name == "vkCmdDrawIndexed":
            extra = "idx %d inst %d first %d vofs %d finst %d" % struct.unpack_from("<IIIiI", d, 8)
        elif name in ("vkCmdDrawIndexedIndirect", "vkCmdDrawIndirect"):
            buf, off, count, stride = struct.unpack_from("<QQII", d, 8)
            extra = "buffer %x +%d count %d stride %d" % (buf, off, count, stride)
        elif name == "vkCmdDispatch":
            extra = "%d x %d x %d" % struct.unpack_from("<3I", d, 8)
        if name.startswith(("vkCmdDraw", "vkCmdDispatch")):
            pid = bound.get((cb, 1 if "Dispatch" in name else 0))
            extra += "  [pipeline %x]" % pid if pid else ""
        print("%d  cb %x  %s %s" % (i, cb, name, extra))


def submitted(d, recorded):
    """The command buffer lists of a vkQueueSubmit: the count as u32 and again as u64, then that many resource
    ids, each a command buffer recorded in the frame (found by pattern, not by walking VkSubmitInfo)."""
    out = []
    k = 8
    while k + 12 <= len(d):
        n, n2 = struct.unpack_from("<IQ", d, k)
        if 0 < n == n2 and k + 12 + 8 * n <= len(d):
            ids = struct.unpack_from("<%dQ" % n, d, k + 12)
            if all(x in recorded for x in ids):
                out.append(list(ids))
                k += 12 + 8 * n
                continue
        k += 1
    return out


if __name__ == "__main__":
    main()
