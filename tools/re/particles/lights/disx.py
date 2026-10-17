import sys, struct, re
sys.path.insert(0,'.')
from xrefs import load
import capstone
def fstart(p, va):
    # walk back to cc/c3 padding boundary
    a = va
    while a > va - 0x20000:
        if p.rd(a-1,1) in (b'\xcc',) and p.rd(a-2,1) in (b'\xcc', b'\xc3'):
            return a
        a -= 1
    return None
def dis(p, va, end=None, maxn=3000):
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    code = p.rd(va, (end-va) if end else 0x4000)
    out=[]
    for ins in md.disasm(code, va):
        s = '%x: %-8s %s' % (ins.address, ins.mnemonic, ins.op_str)
        m = re.search(r'\[rip ([+-]) (0x[0-9a-f]+)\]', ins.op_str)
        if m:
            t = ins.address + ins.size + int(m.group(2),16)*(1 if m.group(1)=='+' else -1)
            ann = hex(t)
            try:
                if ins.mnemonic in ('movss','mulss','addss','subss','divss','comiss','ucomiss','maxss','minss','cvtss2sd') or 'dword' in ins.op_str:
                    ann += ' f=%r i=%d' % (p.f(t), p.i(t))
                elif 'qword' in ins.op_str and ('sd' in ins.mnemonic):
                    ann += ' d=%r' % struct.unpack('<d', p.rd(t,8))[0]
                elif ins.mnemonic == 'lea':
                    st = p.cstr(t, 80)
                    if len(st) > 3 and st.isprintable(): ann += ' "%s"' % st
                elif 'xmmword' in ins.op_str:
                    ann += ' f4=%s' % (struct.unpack('<4f', p.rd(t,16)),)
            except Exception: pass
            s += '    ; ' + ann
        out.append(s)
        if not end and ins.mnemonic == 'ret' and len(out) > maxn: break
        if not end and ins.mnemonic == 'int3' : break
        if len(out) >= maxn: break
    return out
if __name__ == '__main__':
    p = load(sys.argv[1])
    a = int(sys.argv[2], 16)
    if len(sys.argv) > 3 and sys.argv[3] == 'start':
        a = fstart(p, a); print('start', hex(a))
    print('\n'.join(dis(p, a)))
