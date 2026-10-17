import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pe import PE
p = PE('particles.dll')
def vt(addr, n=80):
    r = addr - p.base; out = []
    for i in range(n):
        v = p.q(r + i*8)
        if not (p.base + 0x1000 <= v < p.base + 0x468000): break
        out.append(v)
    return out
if __name__ == '__main__':
    a = vt(int(sys.argv[1], 16)); b = vt(int(sys.argv[2], 16)) if len(sys.argv) > 2 else []
    for i, v in enumerate(a):
        print(i, hex(i*8), hex(v), '' if i >= len(b) else ('same' if b[i] == v else hex(b[i])))
