"""token_names.py <decompile.txt> [entity dump] -- names for the MurmurHash2 (seed 0x31415926, lowercase) attribute hashes a
decompile passes to scenesystem 18004d390 / 18004d270 / 18004cb60, from the strings of scenesystem and client
(and, given an EntityKeyDump output, the entity keys and more DLLs). CUBEMAPS.md A.6."""
import os,re,sys,struct
sys.path.insert(0,os.path.dirname(os.path.abspath(__file__))); import paths
def mm2(b,seed=0x31415926):
    m=0x5bd1e995; h=(seed^len(b))&0xffffffff; i=0
    while len(b)-i>=4:
        k=struct.unpack_from('<I',b,i)[0]; k=(k*m)&0xffffffff; k^=k>>24; k=(k*m)&0xffffffff
        h=((h*m)&0xffffffff)^k; i+=4
    r=len(b)-i
    if r==3: h^=b[i+2]<<16
    if r>=2: h^=b[i+1]<<8
    if r>=1: h^=b[i]; h=(h*m)&0xffffffff
    h^=h>>13; h=(h*m)&0xffffffff; h^=h>>15
    return h
src=open(sys.argv[1],encoding='utf-8').read()
want=set(int(x,16) for x in re.findall(r'FUN_18004(?:d390|d270|cb60)\([^,]*,(?:[^,]*,)?\s*(0x[0-9a-f]{6,8})',src))
want|=set(int(x,16) for x in re.findall(r'(0x[0-9a-f]{7,8})',src) if 0x1000000<int(x,16))
names={}
for dll in ('ss','cl'):
    data=open(paths.binary(dll),'rb').read()
    for s in set(re.findall(rb'[A-Za-z_][A-Za-z0-9_ .:/]{2,60}(?=\x00)',data)):
        h=mm2(s.lower())
        if h in want: names[h]=s.decode()
for h in sorted(want):
    print(hex(h), names.get(h,'?'))
# second pass: entity keys from the dump and strings from more DLLs
if len(sys.argv) < 3:
    sys.exit()
extra=set(re.findall(r' ([a-z_0-9.]+)=', open(sys.argv[2],encoding='utf-8',errors='replace').read()))
bin_=os.path.join(os.environ.get('CS2_DIR',r'D:\Steam\steamapps\common\Counter-Strike Global Offensive'),'game','bin','win64')
for f in ('engine2.dll','worldrenderer.dll','materialsystem2.dll','rendersystemvulkan.dll'):
    data=open(os.path.join(bin_,f),'rb').read()
    extra|=set(s.decode() for s in re.findall(rb'[A-Za-z_][A-Za-z0-9_]{2,60}(?=\x00)',data))
for s in extra:
    h=mm2(s.lower().encode())
    if h in want and h not in names: names[h]=s
print('--- pass 2')
for h in sorted(want):
    print(hex(h), names.get(h,'?'))
