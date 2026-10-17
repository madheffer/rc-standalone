"""strxref.py <dll> <substring>... -- find strings and the functions that reference them (RIP-relative lea/mov)."""
import sys, struct, pefile
pe = pefile.PE(sys.argv[1]); b = pe.OPTIONAL_HEADER.ImageBase; img = pe.get_memory_mapped_image()
text = [s for s in pe.sections if s.Name.startswith(b'.text')][0]
va = text.VirtualAddress; data = img[va:va + text.Misc_VirtualSize]
def owner(a):
    for e in pe.DIRECTORY_ENTRY_EXCEPTION:
        s, t = b + e.struct.BeginAddress, b + e.struct.EndAddress
        if s <= a < t:
            return s
refs = {}
for i in range(len(data) - 7):
    if data[i] in (0x48, 0x4c) and data[i + 1] in (0x8d, 0x8b):
        tgt = b + va + i + 7 + struct.unpack('<i', data[i + 3:i + 7])[0]
        refs.setdefault(tgt, []).append(b + va + i)
for sub in sys.argv[2:]:
    at = img.find(sub.encode())
    while at >= 0:
        start = img.rfind(b'\0', 0, at) + 1
        s = img[start:img.find(b'\0', at)].decode('latin1')
        print(hex(b + start), repr(s), [(hex(r), hex(owner(r) or 0)) for r in refs.get(b + start, [])])
        at = img.find(sub.encode(), at + 1)
