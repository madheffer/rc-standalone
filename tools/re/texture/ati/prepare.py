"""Fetch Compressonator 2.3's DXTC codec and patch it to behave like ati_compress_wrapper.dll.

    python prepare.py            writes xcodec23.cpp and hs/CompressonatorXCodec.h here
    powershell -File build.ps1   builds atitest.exe (MSVC x64, USE_SSE)

Compressonator (MIT, GPUOpen-Tools/compressonator) at tag V2.3.2953 is the last release
that still carries the SSE2 paths Valve's DLL runs. One patch: where Refine1 inlines the
alpha ramp search, the DLL's horizontal sum reads a register it has just zeroed, so only
lanes 0 and 1 count (see docs/TEXTURES.md).
"""
import os, urllib.request
HERE = os.path.dirname(os.path.abspath(__file__))
BASE = "https://raw.githubusercontent.com/GPUOpen-Tools/compressonator/V2.3.2953/Compressonator/"
def fetch(path):
    with urllib.request.urlopen(BASE + path) as r:
        return r.read().decode("latin-1")
src = fetch("Source/Codec/ATI/CompressonatorXCodec.cpp")
start = src.index("static CODECFLOAT RmpSrch1SSE2(")
end = src.index("#endif // USE_SSE", start)
two = src[start:end].replace("static CODECFLOAT RmpSrch1SSE2(", "static CODECFLOAT RmpSrch1SSE2Refine(")
reduce4 = "       err = _mm_add_ps(\n           _mm_movelh_ps(err, _mm_setzero_ps()),\n           _mm_movehl_ps(_mm_setzero_ps(), err)\n       );"
assert reduce4 in two
two = two.replace(reduce4, "       err = _mm_movelh_ps(err, _mm_setzero_ps());")
src = src[:end] + two + src[end:]
call = "                error = RmpSrch1SSE2(_Blk, _Rpt, maxerror, cr_min, cr_max, _NmbrClrs, dwNumPoints);"
assert src.count(call) == 1
src = src.replace(call, call.replace("RmpSrch1SSE2(", "RmpSrch1SSE2Refine("))
open(os.path.join(HERE, "xcodec23.cpp"), "w", encoding="latin-1").write(src)
open(os.path.join(HERE, "hs", "CompressonatorXCodec.h"), "w", encoding="latin-1").write(fetch("Header/Codec/ATI/CompressonatorXCodec.h"))
print("xcodec23.cpp and hs/CompressonatorXCodec.h written")
