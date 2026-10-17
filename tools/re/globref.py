"""globref.py <dll> <hex addr> -- instructions referencing a global RIP-relatively, with the next vtable call offset."""
import sys, pefile, capstone, bisect
pe = pefile.PE(sys.argv[1], fast_load=True); pe.parse_data_directories([3])
b = pe.OPTIONAL_HEADER.ImageBase; img = pe.get_memory_mapped_image()
g = int(sys.argv[2], 16)
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
for e in pe.DIRECTORY_ENTRY_EXCEPTION:
    s, t = e.struct.BeginAddress, e.struct.EndAddress
    ins = list(md.disasm(img[s:t], b + s))
    for i, x in enumerate(ins):
        if 'rip' in x.op_str and '[rip' in x.op_str:
            try: disp = int(x.op_str.split('rip ')[1].split(']')[0].replace('+ ', '').replace('- ', '-').strip(), 16)
            except Exception: continue
            if x.address + x.size + disp == g:
                call = next((y for y in ins[i+1:i+12] if y.mnemonic == 'call' and 'ptr [' in y.op_str), None)
                print(hex(b + s), hex(x.address), x.mnemonic, x.op_str, '->', call.op_str if call else '')
