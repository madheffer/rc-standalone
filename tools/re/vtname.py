"""vtname.py <dll> <slot address>... -- the RTTI class of the vtable holding each slot, and the slot's offset."""
import sys, struct, pefile
pe = pefile.PE(sys.argv[1], fast_load=True); b = pe.OPTIONAL_HEADER.ImageBase
img = pe.get_memory_mapped_image()
def name_of(col_va):
    col = col_va - b
    if col < 0 or col + 24 > len(img): return None
    sig, off, cd, td = struct.unpack_from('<IIII', img, col)
    if sig != 1 or td >= len(img): return None
    n = img[td + 0x10: td + 0x10 + 200].split(b'\0')[0]
    return n.decode('latin1') if n.startswith(b'.?A') else None
for a in sys.argv[2:]:
    slot = int(a, 16)
    p = slot
    for k in range(0, 0x2000, 8):
        q = struct.unpack_from('<Q', img, p - 8 - b)[0]
        n = name_of(q)
        if n:
            print(a, n, 'slot +0x%x' % (slot - p)); break
        p -= 8
    else:
        print(a, '?')
