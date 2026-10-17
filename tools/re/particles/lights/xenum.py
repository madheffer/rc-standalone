import sys, re; sys.path.insert(0,'.')
from pe import PE
p=PE(sys.argv[1])
pat=sys.argv[2].encode()
seen=set()
for m in re.finditer(rb'[A-Z0-9_]*'+pat+rb'[A-Z0-9_]*\0', p.img):
    va=m.start()+p.base
    nm=m.group()[:-1].decode()
    for r in p.ptrs_to(va):
        print('%-60s %d  @%x' % (nm, p.q(r+8), r))
