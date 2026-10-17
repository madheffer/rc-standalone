import sys, struct; sys.path.insert(0, __import__('os').path.dirname(__file__))
from pe import PE
import capstone
def dis(p, rva, n=200, stop_ret=True):
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64); md.detail=False
    code = p.read(rva, n*16)
    out=[]
    for ins in md.disasm(code, p.base+rva):
        c=''
        if 'rip' in ins.op_str and '[' in ins.op_str:
            import re
            m=re.search(r'\[rip ([+-]) (0x[0-9a-f]+)\]', ins.op_str)
            if m:
                t=ins.address+ins.size+(int(m.group(2),16)*(1 if m.group(1)=='+' else -1))
                s=p.cstr(t-p.base) if p.rva2off(t-p.base) is not None else None
                c=f' ; -> {t:#x}'+(f' {s[:50]!r}' if s and len(s)>2 and s.isprintable() else '')
        out.append(f'{ins.address:#x}: {ins.mnemonic} {ins.op_str}{c}')
        n-=1
        if n<=0 or (stop_ret and ins.mnemonic in ('ret','int3')): break
    return out
if __name__=='__main__':
    p=PE(sys.argv[1]); a=int(sys.argv[2],16); a = a-p.base if a>p.base else a
    print('\n'.join(dis(p,a,int(sys.argv[3]) if len(sys.argv)>3 else 200)))
