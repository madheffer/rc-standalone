/* rcpedge <table.bin>: rcpps over every exponent (0..255) and a mantissa sample, against the
   table model used by emu_patch.py; prints the first mismatches per exponent. */
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <immintrin.h>

static uint32_t tab[1u << 23];

static uint32_t hw(uint32_t x) { float f; memcpy(&f, &x, 4); float r = _mm_cvtss_f32(_mm_rcp_ps(_mm_set1_ps(f))); uint32_t b; memcpy(&b, &r, 4); return b; }
static uint32_t model(uint32_t b)
{
    uint32_t sign = b & 0x80000000u; int e = (int)((b >> 23) & 0xff);
    if (e == 0) return sign | 0x7f800000u;
    if (e == 255) return (b & 0x7fffff) ? 0x7fc00000u : sign;
    uint32_t r = tab[b & 0x7fffff];
    int re = (int)((r >> 23) & 0xff) - (e - 127);
    if (re <= 0) return sign;
    return sign | ((uint32_t)re << 23) | (r & 0x7fffff);
}
int main(int argc, char** argv)
{
    FILE* f = fopen(argv[1], "rb"); fread(tab, 4, 1u << 23, f); fclose(f);
    long bad = 0;
    for (int e = 0; e < 256; e++)
    {
        int shown = 0;
        for (uint32_t m = 0; m < (1u << 23); m += 13)
            for (int s = 0; s < 2; s++)
            {
                uint32_t x = ((uint32_t)s << 31) | ((uint32_t)e << 23) | m;
                uint32_t h = hw(x), md = model(x);
                if (h != md && !((h & 0x7fc00000u) == 0x7fc00000u && (md & 0x7fc00000u) == 0x7fc00000u))
                {
                    bad++;
                    if (shown++ < 2) printf("e=%d m=%06x s=%d hw=%08x model=%08x\n", e, m, s, h, md);
                }
            }
    }
    printf("mismatches: %ld\n", bad);
    return 0;
}
