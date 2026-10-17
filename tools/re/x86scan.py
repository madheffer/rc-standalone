"""x86scan.py <dll> <start> <end> <regex>: linear sweep, print matching instructions."""
import sys, re, pefile, capstone
pe = pefile.PE(sys.argv[1], fast_load=True)
a, b = int(sys.argv[2], 16), int(sys.argv[3], 16)
base = pe.OPTIONAL_HEADER.ImageBase
data = pe.get_data(a - base, b - a)
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
rx = re.compile(sys.argv[4])
off = 0
while off < len(data):
    got = False
    for i in md.disasm(data[off:], a + off):
        got = True
        s = "%x: %s %s" % (i.address, i.mnemonic, i.op_str)
        if rx.search(s): print(s)
        off = i.address + i.size - a
    if not got: off += 1
