"""leascan.py <dll> <hex addr>... -- instructions that take a global's address RIP-relatively (lea/mov/cmp), with the owning .pdata function."""
import sys, struct, bisect, pefile
pe = pefile.PE(sys.argv[1], fast_load=True); pe.parse_data_directories([3])
b = pe.OPTIONAL_HEADER.ImageBase; img = pe.get_memory_mapped_image()
text = [s for s in pe.sections if s.Name.startswith(b'.text')][0]
va = text.VirtualAddress; data = img[va:va + text.Misc_VirtualSize]
fs = sorted((b + e.struct.BeginAddress, b + e.struct.EndAddress) for e in pe.DIRECTORY_ENTRY_EXCEPTION)
starts = [f[0] for f in fs]
def owner(a):
    i = bisect.bisect_right(starts, a) - 1
    return fs[i][0] if i >= 0 and fs[i][0] <= a < fs[i][1] else 0
targets = {int(x, 16) for x in sys.argv[2:]}
for i in range(len(data) - 7):
    if data[i] in (0x48, 0x4c) and data[i + 1] == 0x8d and (data[i + 2] & 0xc7) == 0x05:
        t = b + va + i + 7 + struct.unpack('<i', data[i + 3:i + 7])[0]
        if t in targets:
            print(hex(t), hex(b + va + i), 'fn', hex(owner(b + va + i)))
