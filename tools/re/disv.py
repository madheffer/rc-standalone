"""disv.py <dll> <start hex> <end hex> -- disassemble a range, annotating RIP-relative float/double constants."""
import sys, struct, pefile, capstone
pe = pefile.PE(sys.argv[1], fast_load=True); base = pe.OPTIONAL_HEADER.ImageBase; img = pe.get_memory_mapped_image()
a, b = int(sys.argv[2], 16), int(sys.argv[3], 16)
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
for x in md.disasm(img[a - base:b - base], a):
    note = ''
    if '[rip' in x.op_str:
        d = int(x.op_str.split('[rip ')[1].split(']')[0].replace(' ', ''), 16)
        t = x.address + x.size + d - base
        raw = img[t:t + 16]
        f = struct.unpack('<4f', raw); dd = struct.unpack('<d', raw[:8])[0]
        note = f'   ; {hex(t + base)} f={f[0]:.9g},{f[1]:.9g},{f[2]:.9g},{f[3]:.9g} d={dd:.17g}'
    print(f'{x.address:x} {x.mnemonic} {x.op_str}{note}')
