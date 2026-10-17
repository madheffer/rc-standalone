"""disasm.py <start hex> <end hex> [dll] -- disassemble a range of an archived build: an alias from
paths.ALIASES, a file in tools/re/binaries, or a path (default rc, resourcecompiler_20260923.dll)."""
import os, sys, pefile, capstone
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import paths
dll = paths.binary(sys.argv[3] if len(sys.argv) > 3 else "rc")
pe = pefile.PE(dll, fast_load=True)
a, b = int(sys.argv[1], 16), int(sys.argv[2], 16)
base = pe.OPTIONAL_HEADER.ImageBase
data = pe.get_data(a - base, b - a)
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
for i in md.disasm(data, a):
    print("%x: %-8s %s" % (i.address, i.mnemonic, i.op_str))
