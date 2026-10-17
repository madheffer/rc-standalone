"""findptr.py <dll> <va> [<va>...] -- file locations (as VAs) holding a pointer to va."""
import pefile, struct, sys
pe = pefile.PE(sys.argv[1], fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
data = pe.__data__
for arg in sys.argv[2:]:
    va = int(arg, 16)
    needle = struct.pack('<Q', va)
    hits = []
    i = data.find(needle)
    while i != -1 and len(hits) < 20:
        hits.append(hex(pe.get_rva_from_offset(i) + base))
        i = data.find(needle, i + 1)
    print(arg, hits)
