/* rcpdump <out>: rcpps of every float in [1, 2) (2^23 results, uint32 each), plus a check that
   the exponent and sign factor out: rcpps(s * 2^e * m) == s * 2^-e * rcpps(m) away from the edges. */
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <immintrin.h>

static uint32_t rcp_bits(uint32_t x)
{
    float f; memcpy(&f, &x, 4);
    float r = _mm_cvtss_f32(_mm_rcp_ps(_mm_set1_ps(f)));
    uint32_t b; memcpy(&b, &r, 4); return b;
}

int main(int argc, char** argv)
{
    static uint32_t out[1u << 23];
    for (uint32_t m = 0; m < (1u << 23); m++) out[m] = rcp_bits(0x3f800000u | m);
    FILE* f = fopen(argv[1], "wb"); fwrite(out, 4, 1u << 23, f); fclose(f);
    long bad = 0;
    for (uint32_t m = 0; m < (1u << 23); m += 97)
        for (int e = -100; e <= 100; e += 7)
            for (int s = 0; s < 2; s++)
            {
                uint32_t x = ((uint32_t)s << 31) | ((uint32_t)(127 + e) << 23) | m;
                uint32_t r = rcp_bits(x);
                uint32_t base = out[m];
                /* base = 2^-1 * 1.xxx or exactly 1.0; scale its exponent by -e */
                uint32_t want = ((uint32_t)s << 31) | ((((base >> 23) & 0xff) - e) << 23) | (base & 0x7fffff);
                if (r != want) bad++;
            }
    printf("exponent/sign scaling mismatches: %ld\n", bad);
    return 0;
}
