"""ripconst.py <start hex> <end hex> [dll] -- disassemble a range and resolve every rip-relative
memory operand to its address and the float / dword (and 16 bytes) stored there."""
import os, re, struct, sys
import capstone, pefile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import paths
pe = pefile.PE(paths.binary(sys.argv[3] if len(sys.argv) > 3 else "rc"), fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
a, b = int(sys.argv[1], 16), int(sys.argv[2], 16)
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
for i in md.disasm(pe.get_data(a - base, b - a), a):
    note = ""
    m = re.search(r"\[rip ([+-]) (0x[0-9a-f]+)\]", i.op_str)
    if m:
        t = i.address + i.size + (int(m.group(2), 16) * (1 if m.group(1) == "+" else -1))
        raw = pe.get_data(t - base, 16).ljust(16, b"\0")
        f, = struct.unpack("<f", raw[:4])
        note = "  ; [%x] = %r (0x%08x) q=%s" % (t, f, struct.unpack("<I", raw[:4])[0], raw.hex())
    print("%x: %-8s %s%s" % (i.address, i.mnemonic, i.op_str, note))
