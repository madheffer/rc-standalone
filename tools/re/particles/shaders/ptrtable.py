import sys; sys.stdout.reconfigure(errors="backslashreplace")
import struct, sys, re
exec(open('schemaenum.py').read().split("for name in sys.argv[2:]:")[0])
for name in sys.argv[2:]:
    soff = 0
    while True:
        soff = b.find(name.encode() + b'\0', soff + 1)
        if soff < 0 or b[soff-1] == 0: break
    sva = off2va(soff)
    refs = find_ptrs(sva)
    print('==', name, hex(sva), [hex(r) for r in refs])
    for r in refs:
        for stride in (8, 16):
            out = []
            for k in range(-12, 20):
                p = struct.unpack_from('<Q', b, r + k*stride)[0]
                s = cstr(p) if 0x180000000 < p < 0x190000000 else None
                extra = struct.unpack_from('<q', b, r + k*stride + 8)[0] if stride == 16 else ''
                out.append(f"{k}:{s}:{extra}")
            print(' stride', stride, ' | '.join(out))
