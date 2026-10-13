/* rsqrtdump <out.txt>: this CPU's rsqrtps as a table.
   For x = 2^e * 1.m the estimate is checked to depend only on e's parity and the top K bits of
   m (smallest K that holds), and to scale exactly by 2^(-e/2) across every normal exponent;
   then writes 2 * 2^K lines "<parity> <index> <bits of rsqrt(x) for e = 0 or 1, lowest m of the bucket>". */
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <immintrin.h>

static uint32_t rsq(uint32_t bits)
{
    float f; memcpy(&f, &bits, 4);
    float r = _mm_cvtss_f32(_mm_rsqrt_ps(_mm_set1_ps(f)));
    uint32_t o; memcpy(&o, &r, 4); return o;
}

int main(int argc, char** argv)
{
    int K;
    for (K = 8; K <= 23; K++)
    {
        int ok = 1;
        for (int p = 0; p < 2 && ok; p++)
            for (uint32_t m = 0; m < (1u << 23) && ok; m++)
            {
                uint32_t base = ((127u + p) << 23) | (m & ~((1u << (23 - K)) - 1));
                if (rsq(((127u + p) << 23) | m) != rsq(base)) ok = 0;
            }
        if (ok) break;
    }
    printf("depends on parity and top %d mantissa bits\n", K);
    /* exponent scaling: every normal exponent with its parity's table, scaled */
    long bad = 0;
    for (uint32_t e = 1; e < 255; e++)
        for (uint32_t m = 0; m < (1u << 23); m += 997)
        {
            uint32_t x = (e << 23) | m;
            uint32_t ref = rsq(((127u + ((e - 127) & 1)) << 23) | m);
            int shift = ((int)e - 127 - (int)((e - 127) & 1)) / 2;  /* x = ref input * 4^shift */
            int re = (int)(ref >> 23) - shift;
            uint32_t want = re > 0 && re < 255 ? (ref & 0x807FFFFF) | ((uint32_t)re << 23) : 0xFFFFFFFF;
            if (want != 0xFFFFFFFF && rsq(x) != want) bad++;
        }
    printf("exponent scaling mismatches: %ld\n", bad);
    FILE* f = fopen(argv[1], "w");
    for (int p = 0; p < 2; p++)
        for (uint32_t i = 0; i < (1u << K); i++)
            fprintf(f, "%d %u %08x\n", p, i, rsq(((127u + p) << 23) | (i << (23 - K))));
    fclose(f);
    return 0;
}
