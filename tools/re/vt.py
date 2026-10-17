"""vt.py <dll> <vtable_va> <first_slot_off> <count> -- print slot offset -> function va."""
import sys, struct, pefile
pe = pefile.PE(sys.argv[1], fast_load=True); b = pe.OPTIONAL_HEADER.ImageBase
vt, first, n = int(sys.argv[2],16), int(sys.argv[3],16), int(sys.argv[4],0)
for i in range(n):
    off = first + 8*i
    print(hex(off), hex(struct.unpack('<Q', pe.get_data(vt + off - b, 8))[0]))
