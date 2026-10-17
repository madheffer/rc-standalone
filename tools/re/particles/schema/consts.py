import sys, struct, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pe import PE
p = PE(sys.argv[1] if len(sys.argv) > 2 and sys.argv[1].endswith('.dll') else 'particles.dll')
args = sys.argv[2:] if sys.argv[1].endswith('.dll') else sys.argv[1:]
for a in args:
    r = int(a, 16) - p.base
    b = p.read(r, 16)
    print(a, 'f32', [struct.unpack_from('<f', b, k)[0] for k in (0, 4, 8, 12)], 'i32', struct.unpack_from('<i', b)[0], 'hex', b[:8].hex())
