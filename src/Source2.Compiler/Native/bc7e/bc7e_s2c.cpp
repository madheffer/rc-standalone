// bc7e_s2c: the C entry point Source2.Compiler calls (Bc7e.cs). Links one build of
// bc7e_rcp.ispc (prepare.py), which takes rcpps from one CPU vendor's table, so the blocks
// are the same on every AVX2 + FMA machine. Level 6 (init_slowest), as resourcecompiler.
#include <cstdint>
#include <mutex>
#include "bc7e_ispc.h"

#if defined(_WIN32)
#define S2C_EXPORT extern "C" __declspec(dllexport)
#else
#define S2C_EXPORT extern "C" __attribute__((visibility("default")))
#endif

static std::once_flag g_init;

// pixels: 16 RGBA8 pixels per block (R, G, B, A in memory); out: 16 bytes per block.
S2C_EXPORT void s2c_bc7e_encode(const uint32_t* pixels, uint64_t* out, uint32_t blocks, int perceptual)
{
    std::call_once(g_init, [] { ispc::bc7e_compress_block_init(); });
    ispc::bc7e_compress_block_params params;
    ispc::bc7e_compress_block_params_init_slowest(&params, perceptual != 0);
    for (uint32_t i = 0; i < blocks; i += 64)
    {
        uint32_t n = blocks - i < 64 ? blocks - i : 64;
        ispc::bc7e_compress_blocks(n, out + (size_t)i * 2, pixels + (size_t)i * 16, &params);
    }
}
