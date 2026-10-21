# Writes every shader module of a capture (vkCreateShaderModule chunks) to <out>/<resource id hex>.spv, for
# spvmatch (which finds the .vcs program and combo each one came from) and for rdc_actions' module names.
#   python rdc_spirv.py <capture.rdc> <out dir>
import os
import struct
import sys

import rdc_stream

VK_CREATE_SHADER_MODULE = 1019


def main():
    cap = rdc_stream.Capture(sys.argv[1])
    out = sys.argv[2]
    os.makedirs(out, exist_ok=True)
    stream = rdc_stream.Stream(cap.pages())
    for ch in rdc_stream.chunks(stream, keep=lambda cid, size: cid == VK_CREATE_SHADER_MODULE):
        if ch.data is None:
            continue
        d = ch.data
        start = d.find(b"#")                      # SPIR-V magic, after the struct and padding
        # codeSize (written twice: the field and the byte array's length) is the largest whole-word value
        # before the code that still fits in the chunk
        size = max(v for k in range(start - 7) for v in [struct.unpack_from("<Q", d, k)[0]]
                   if v % 4 == 0 and 0 < v <= len(d) - start)
        tail = len(d) - 8 if d[-8:] != bytes(8) else len(d) - 16  # the module's resource id ends the chunk
        (rid,) = struct.unpack_from("<Q", d, tail)
        code = d[start:start + size]
        open(os.path.join(out, "%x.spv" % rid), "wb").write(code)
        print("%x %d bytes" % (rid, len(code)))


if __name__ == "__main__":
    main()
