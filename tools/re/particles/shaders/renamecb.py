# Rename cbuffer members in SPIRV-Cross HLSL to offset-based names: <CB>_c<reg>[.<comp>]
import re, sys
comp = 'xyzw'
for path in sys.argv[1:]:
    s = open(path, encoding='utf-8').read()
    names = {}
    for cb, body in re.findall(r'cbuffer (\w+) : register\([^)]*\)\s*\{(.*?)\};', s, re.S):
        for typ, name, off in re.findall(r'^\s*(?:column_major |row_major )?([\w<>]+) (\w+) : packoffset\((c\d+(?:\.[xyzw])?)\);', body, re.M):
            short = {'SpriteCardPixelControls_t':'PC','SpritecardVertexControls_t':'VC','PerViewConstantBuffer_t':'PV','PerViewConstantBufferCsgo_t':'CSGO','PerViewLightingConstantBufferGpu_t':'LIT'}.get(cb, cb)
            names[name] = f"{short}_{off.replace('.', '')}"
    for k in sorted(names, key=len, reverse=True):
        s = re.sub(r'\b' + k + r'\b', names[k], s)
    out = path[:-5] + '.cb.hlsl'
    open(out, 'w', encoding='utf-8').write(s)
