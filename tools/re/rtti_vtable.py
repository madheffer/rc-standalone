"""rtti_vtable.py <dll> <mangled class name, e.g. .?AVCMapBuilderContext@@> [slots] -- vtables of a class via RTTI."""
import sys, struct, pefile
pe = pefile.PE(sys.argv[1], fast_load=True); b = pe.OPTIONAL_HEADER.ImageBase
img = pe.get_memory_mapped_image(); name = sys.argv[2].encode(); slots = int(sys.argv[3]) if len(sys.argv) > 3 else 40
at = img.find(name + b'\0')
if at < 0: sys.exit('no type descriptor name')
td = at - 0x10                                   # TypeDescriptor: vftable ptr, spare, name
td_rva = td
cols = []
for off in range(0, len(img) - 24, 4):
    if struct.unpack_from('<I', img, off)[0] == 1 and struct.unpack_from('<I', img, off + 12)[0] == td_rva:
        cols.append(off)                         # CompleteObjectLocator: sig=1, offset, cdOffset, pTypeDescriptor(rva)
for col in cols:
    offset = struct.unpack_from('<I', img, col + 4)[0]
    ref = struct.pack('<Q', b + col); p = img.find(ref)
    while p >= 0:
        vt = p + 8
        entries = struct.unpack_from('<%dQ' % slots, img, vt)
        print('vtable %x (object offset %x):' % (b + vt, offset))
        for i, e in enumerate(entries):
            print('  %3x  %x' % (i * 8, e))
        p = img.find(ref, p + 1)
