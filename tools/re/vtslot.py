"""vtslot.py <dll> <entry addr> <slot offset>... -- for a vtable entry, whether the table starts slot bytes before it:
the qword before that start must point at an RTTI complete object locator (signature 1, its self RVA matching),
and the locator's type descriptor names the class."""
import sys, struct, pefile
pe = pefile.PE(sys.argv[1], fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
img = pe.get_memory_mapped_image()
q = lambda va: struct.unpack_from("<Q", img, va - base)[0]
d = lambda va, o=0: struct.unpack_from("<I", img, va - base + o)[0]
entry = int(sys.argv[2], 16)
for slot in (int(s, 16) for s in sys.argv[3:]):
    start = entry - slot
    col = q(start - 8)
    if not base <= col < base + len(img):
        print(hex(slot), "no locator"); continue
    sig, self_rva = d(col), d(col, 20)
    if sig != 1 or self_rva != col - base:
        print(hex(slot), "not a locator"); continue
    td = base + d(col, 12)
    name = img[td - base + 16: td - base + 16 + 80].split(b"\0")[0].decode(errors="replace")
    print(hex(slot), "table", hex(start), name)
