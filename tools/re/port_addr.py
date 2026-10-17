"""port_addr.py <old dll> <new dll> <hex addr>... -- find functions in a newer build.

Takes the first 96 bytes at each address in the old image, masks the
rel32/rip displacements capstone reports, and searches the new image's
executable sections for the unique match.
"""
import sys, re, pefile, capstone

old = pefile.PE(sys.argv[1], fast_load=True)
new = pefile.PE(sys.argv[2], fast_load=True)
ob, nb = old.OPTIONAL_HEADER.ImageBase, new.OPTIONAL_HEADER.ImageBase
oi, ni = old.get_memory_mapped_image(), new.get_memory_mapped_image()
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
md.detail = True
text = [s for s in new.sections if s.Characteristics & 0x20000000]

for a in sys.argv[3:]:
    addr = int(a, 16)
    raw = oi[addr - ob: addr - ob + 96]
    mask = bytearray(b'\xff' * len(raw))
    for x in md.disasm(raw, addr):
        off = x.address - addr
        disp = x.disp_offset if x.disp_offset else 0
        if disp and ('rip' in x.op_str):
            for k in range(4):
                if off + disp + k < len(mask):
                    mask[off + disp + k] = 0
        if x.mnemonic in ('call', 'jmp') or x.mnemonic.startswith('j'):
            if x.size >= 5:
                for k in range(4):
                    if off + x.size - 4 + k < len(mask):
                        mask[off + x.size - 4 + k] = 0
    pat = b''.join(re.escape(bytes([b])) if m else b'.' for b, m in zip(raw, mask))
    rx = re.compile(pat, re.S)
    hits = []
    for s in text:
        lo = s.VirtualAddress
        data = ni[lo: lo + s.Misc_VirtualSize]
        hits += [nb + lo + m.start() for m in rx.finditer(data)]
    print(a, ' '.join(hex(h) for h in hits[:5]), '' if len(hits) == 1 else f'({len(hits)} hits)')
