"""dumptable.py <dll> <start_va> <count> <stride> -- rows of (string ptr, then dwords)."""
import pefile, struct, sys
pe = pefile.PE(sys.argv[1], fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
start, count, stride = int(sys.argv[2], 16), int(sys.argv[3], 0), int(sys.argv[4], 0)
def read(va, n): return pe.get_data(va - base, n)
def cstr(va):
    try:
        b = read(va, 80); return b.split(b'\0')[0].decode('latin1')
    except Exception: return '?'
for r in range(count):
    va = start + r * stride
    row = read(va, stride)
    q = struct.unpack_from('<Q', row, 0)[0]
    dws = struct.unpack_from('<%dI' % ((stride - 8) // 4), row, 8)
    print(hex(va), cstr(q) if base <= q < base + 0x5000000 else hex(q), [hex(d) for d in dws])
