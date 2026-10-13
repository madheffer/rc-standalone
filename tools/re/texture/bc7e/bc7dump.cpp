// bc7dump <blocks> <out>: bc7e slowest + perceptual (RC's level 6) over 64-byte RGBA blocks.
// Build: python src/Source2.Compiler/Native/bc7e/prepare.py --out tools/re/texture/bc7e, then ispc + cl as in Native/bc7e/build.ps1.
#include <cstdio>
#include <cstdint>
#include <vector>
#include "bc7e_ispc.h"
int main(int argc, char** argv)
{
    FILE* f = fopen(argv[1], "rb"); fseek(f, 0, SEEK_END); long n = ftell(f) / 64; fseek(f, 0, SEEK_SET);
    std::vector<uint32_t> px(n * 16); fread(px.data(), 64, n, f); fclose(f);
    ispc::bc7e_compress_block_init();
    ispc::bc7e_compress_block_params p; ispc::bc7e_compress_block_params_init_slowest(&p, true);
    std::vector<uint64_t> out(n * 2);
    for (long i = 0; i < n; i += 64) ispc::bc7e_compress_blocks((uint32_t)(n - i < 64 ? n - i : 64), out.data() + i * 2, px.data() + i * 16, &p);
    FILE* o = fopen(argv[2], "wb"); fwrite(out.data(), 16, n, o); fclose(o);
    return 0;
}
