"""callers.py <dll> <hex addr>... -- functions that CALL (E8 rel32) or JMP to each address, via .pdata."""
import sys, struct, bisect, pefile
pe = pefile.PE(sys.argv[1], fast_load=True); pe.parse_data_directories([3])
b = pe.OPTIONAL_HEADER.ImageBase; img = pe.get_memory_mapped_image()
text = [s for s in pe.sections if s.Name.startswith(b'.text')][0]
va = text.VirtualAddress; data = img[va:va + text.Misc_VirtualSize]
fs = sorted((b + e.struct.BeginAddress, b + e.struct.EndAddress) for e in pe.DIRECTORY_ENTRY_EXCEPTION)
starts = [f[0] for f in fs]
def owner(a):
    i = bisect.bisect_right(starts, a) - 1
    return fs[i][0] if i >= 0 and fs[i][0] <= a < fs[i][1] else None
targets = {int(x, 16) for x in sys.argv[2:]}
hits = {t: [] for t in targets}
for i in range(len(data) - 5):
    if data[i] in (0xE8, 0xE9):
        t = b + va + i + 5 + struct.unpack('<i', data[i + 1:i + 5])[0]
        if t in targets:
            hits[t].append((b + va + i, owner(b + va + i)))
for t, l in hits.items():
    print(hex(t), [(hex(a), hex(o or 0)) for a, o in l])
