"""cmp_globals.py <ours dir> <face dump> <spirv dir> -- our cubemap $Globals against a capture's.

<ours dir> is S2C_CUBE_GLOBALS_OUT from a CubemapFaceRenderer run: <key>.bin per block and <key>.txt with the
program's SPIR-V sha1 and its constants. Each block is matched to the capture's draws of the same module (by SPIR-V
hash against <spirv dir>'s <hex id>.spv) and compared word for word with the closest of their set 0 / 1 binding 0
blocks, bindless index words left out; the sampler index words are printed as (ours, the game's).
"""
import sys,os,json,hashlib,struct,collections
ours_dir,dump,spirv=sys.argv[1:4]
mod={}
for f in os.listdir(spirv):
    if f.endswith('.spv'): mod[hashlib.sha1(open(os.path.join(spirv,f),'rb').read()).hexdigest()]=int(f[:-4],16)
draws=collections.defaultdict(list)
for line in open(os.path.join(dump,'actions.jsonl'),encoding='utf-8'):
    r=json.loads(line); st=r.get('stages') or {}
    for stage,setn in (('vs',0),('ps',1)):
        s=st.get(stage)
        if not s: continue
        mid=int(s['shader'].split()[-1])
        for cb in s.get('constantBlocks') or []:
            b=cb.get('block') or {}
            if b.get('fixedBindSetOrSpace')==setn and b.get('fixedBindNumber')==0 and cb.get('bytes'):
                draws[mid].append((r['eid'],cb['bytes']['sha1']))
for f in sorted(os.listdir(ours_dir)):
    if not f.endswith('.txt'): continue
    lines=open(os.path.join(ours_dir,f)).read().split('\n'); sha=lines[0]
    consts=[l.split(' ') for l in lines[1:] if l]
    idx={int(o):(n,t) for o,n,t in consts if 'Index' in t}
    ours=open(os.path.join(ours_dir,f[:-4]+'.bin'),'rb').read()
    m=mod.get(sha)
    if m is None: print(f[:70],'NO MODULE in capture'); continue
    blocks=set(b for _,b in draws[m])
    best=None
    for b in blocks:
        cap=open(os.path.join(dump,'blobs',b+'.bin'),'rb').read()
        n=min(len(cap),len(ours))//4
        diff=[i*4 for i in range(n) if ours[i*4:i*4+4]!=cap[i*4:i*4+4] and i*4 not in idx]
        cand=(len(diff),b,cap,diff)
        if best is None or cand[0]<best[0]: best=cand
    if best is None: print(f[:70],'module',m,'no draws'); continue
    nd,b,cap,diff=best
    samp=[(n,struct.unpack_from('<i',ours,o)[0],struct.unpack_from('<i',cap,o)[0]) for o,(n,t) in sorted(idx.items()) if t=='SamplerStateIndex']
    print(f[:70],'module',m,'draws',len(draws[m]),'blocks',len(blocks),'non-index diffs',nd,diff[:6],'samplers (ours,game)',samp)
