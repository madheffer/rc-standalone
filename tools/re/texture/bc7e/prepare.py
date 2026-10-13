"""prepare.py [--out dir]: bc7e.ispc with rcpps taken from a table, so BC7 output does not depend on the CPU.

Fetches bc7e.ispc (richgel999/bc7enc_rdo c4a839c, Apache 2.0; the encoder in Valve's bc7e.dll)
and writes bc7e_rcp.ispc. Under --opt=fast-math ISPC emits a / b as
    iv = rcpps(b); a * (iv * (2 - b * iv))
and rcpps is the only instruction in the encoder whose result differs between CPU vendors
(FMA and sqrt are exact). Each variable division is rewritten as that same expression with iv
from rcp_amd.txt, or rcp_intel.txt when compiled with -DBC7E_RCP_INTEL. For x = s * 2^e * 1.m
both vendors return s * 2^-e * table[top 12 bits of m] (rcpedge.c checks every exponent).

Build exactly as Valve's avx2 path: ispc 1.13..1.24, --target=avx2-i32x4 --opt=fast-math
--opt=disable-assertions. Checked on 101,632 blocks: the AMD table on an Intel CPU equals AMD
hardware, the Intel table on AMD equals Intel hardware, and the AMD build matches
resourcecompiler's BC7 (compiled on AMD) block for block.
"""
import argparse, os, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
URL = "https://raw.githubusercontent.com/richgel999/bc7enc_rdo/c4a839c/bc7e.ispc"


def table(name):
    words = [l.strip() for l in open(os.path.join(HERE, name)) if l.strip() and not l.startswith("#")]
    assert len(words) == 4096, name
    return "\n".join("    " + ", ".join("0x" + w for w in words[i:i + 8]) + "," for i in range(0, 4096, 8))


HELPERS = r'''
// rcpps as a table (tools/re/texture/bc7e/prepare.py).
#ifdef BC7E_RCP_INTEL
static const uniform unsigned int32 RCP_TAB[4096] = {
@INTEL@
};
#else
static const uniform unsigned int32 RCP_TAB[4096] = {
@AMD@
};
#endif

static inline unsigned int32 emu_rcp_bits(unsigned int32 b)
{
    unsigned int32 sign = b & 0x80000000;
    int32 e = (int32)((b >> 23) & 0xff);
    if (e == 0) return sign | 0x7f800000;
    if (e == 255) return (b & 0x7fffff) != 0 ? 0x7fc00000 : sign;
    unsigned int32 r = RCP_TAB[(b >> 11) & 0xfff];
    int32 re = (int32)((r >> 23) & 0xff) - (e - 127);
    if (re <= 0) return sign;
    return sign | ((unsigned int32)re << 23) | (r & 0x7fffff);
}
static inline uniform unsigned int32 emu_rcp_bits(uniform unsigned int32 b)
{
    uniform unsigned int32 sign = b & 0x80000000;
    uniform int32 e = (uniform int32)((b >> 23) & 0xff);
    if (e == 0) return sign | 0x7f800000;
    if (e == 255) return (b & 0x7fffff) != 0 ? 0x7fc00000 : sign;
    uniform unsigned int32 r = RCP_TAB[(b >> 11) & 0xfff];
    uniform int32 re = (uniform int32)((r >> 23) & 0xff) - (e - 127);
    if (re <= 0) return sign;
    return sign | ((uniform unsigned int32)re << 23) | (r & 0x7fffff);
}
static inline float emu_div(float a, float b)
{
    float iv = floatbits(emu_rcp_bits(intbits(b)));
    return a * (iv * (2.0f - b * iv));
}
static inline uniform float emu_div(uniform float a, uniform float b)
{
    uniform float iv = floatbits(emu_rcp_bits(intbits(b)));
    return a * (iv * (2.0f - b * iv));
}
'''

# Every division by a value not known at compile time. `inv_n = 1.0f / ((float)N - 1)` stays a
# division: N is a constant at each call site, so ISPC folds it to an exactly rounded constant
# and no rcpps runs (rewriting it parted from the hardware on 8 Intel blocks in 101,632).
REWRITES = [
    ("s = 1.0f / sqrt(s);", "s = emu_div(1.0f, sqrt(s));"),
    ("det = 1.0f / det;", "det = emu_div(1.0f, det);"),
    ("const float f = N / (dr * dr + dg * dg + db * db);", "const float f = emu_div(N, (dr * dr + dg * dg + db * db));"),
    ("const float f = N / (dr * dr + dg * dg + db * db + da * da);",
     "const float f = emu_div(N, (dr * dr + dg * dg + db * db + da * da));"),
    ("vec4F_mul(&meanColor, 1.0f / (float)((int)num_pixels));", "vec4F_mul(&meanColor, emu_div(1.0f, (float)((int)num_pixels)));"),
    ("vec4F_mul(&meanColor, 1.0f / (float)((int)num_pixels * 255.0f));",
     "vec4F_mul(&meanColor, emu_div(1.0f, (float)((int)num_pixels * 255.0f)));"),
    ("m = 1.0f / m;", "m = emu_div(1.0f, m);"),
    ("len = 1.0f / sqrt(len);", "len = emu_div(1.0f, sqrt(len));"),
    ("floor((float)max_selector * ((float)(int)selectors_temp[i] - (float)ly) / ((float)hy - (float)ly) + .5f)",
     "floor(emu_div((float)max_selector * ((float)(int)selectors_temp[i] - (float)ly), ((float)hy - (float)ly)) + .5f)"),
    ("float scale = ((float)N - 1) / (float)(high - low);", "float scale = emu_div(((float)N - 1), (float)(high - low));"),
]


def main():
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--out", default=HERE)
    a = p.parse_args()
    with urllib.request.urlopen(URL) as r:
        src = r.read().decode("latin-1")
    for old, new in REWRITES:
        assert old in src, old
        src = src.replace(old, new)
    helpers = HELPERS.replace("@AMD@", table("rcp_amd.txt")).replace("@INTEL@", table("rcp_intel.txt"))
    at = src.index("struct vec4F")
    src = src[:at] + helpers + "\n" + src[at:]
    out = os.path.join(a.out, "bc7e_rcp.ispc")
    open(out, "w", encoding="latin-1", newline="\n").write(src)
    print("wrote", out)


if __name__ == "__main__":
    main()
