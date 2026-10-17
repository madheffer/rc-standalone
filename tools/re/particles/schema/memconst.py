import ctypes, sys, struct
from load import setup
ss, pt, iss, ISB = setup(); ISB(b"SchemaSystem_001", iss)
for a in sys.argv[1:]:
    r = int(a, 16) - 0x180000000
    b = ctypes.string_at(pt + r, 16)
    print(a, [round(x, 7) for x in struct.unpack('<4f', b)], struct.unpack('<4i', b))
