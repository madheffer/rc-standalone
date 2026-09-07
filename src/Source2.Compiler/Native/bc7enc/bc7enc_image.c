// bc7enc_image.c — whole-image BC7 wrapper around bc7enc.c (Richard Geldreich's
// single-file BC7 encoder). Exposes a C ABI the .NET backend P/Invokes
// (Models/Bc7Native.cs): one call encodes an arbitrary-size image region, so
// the managed side never pays a per-block interop cost. Parallelism is done
// in C# by slicing the image into horizontal strips and calling this once per
// strip — BC7 blocks are independent, so a strip boundary on a 4-pixel row is
// a clean cut.
//
// Input pixels are BGRA8888 (the layout ResourceBuilder already holds, from
// SkiaSharp's Bgra8888 bitmaps); they are swizzled to RGBA per-block before
// handing to bc7enc, which expects R-first.

#include "bc7enc.h"
#include <stdint.h>

#if defined(_WIN32)
  #define BC7ENC_API __declspec(dllexport)
#else
  #define BC7ENC_API __attribute__((visibility("default")))
#endif

static int g_inited = 0;

// Builds bc7enc's internal tables. MUST run once before any encode call.
// Idempotent; the .NET side calls it once at startup.
BC7ENC_API void bc7enc_image_init(void)
{
    if (!g_inited)
    {
        bc7enc_compress_block_init();
        g_inited = 1;
    }
}

// Encode a BGRA8888 image region to a BC7 block stream.
//   src             BGRA8888 pixels, row-major, width*height*4 bytes.
//   width, height   region dimensions (need not be multiples of 4 — the
//                   bottom/right edge is clamped to fill the last block).
//   uber_level      bc7enc quality 0..4 (4 = best).
//   max_partitions  bc7enc mode-1 partition search 0..64 (64 = best).
//   perceptual      0 = linear RGB error (use for packed/data textures),
//                   1 = YCbCr-weighted error.
//   out             receives ((width+3)/4)*((height+3)/4)*16 bytes.
BC7ENC_API void bc7enc_encode_bgra(
    const uint8_t *src, int width, int height,
    int uber_level, int max_partitions, int perceptual,
    uint8_t *out)
{
    // Set the params struct directly rather than via bc7enc.h's inline
    // helpers — a C `inline` function called across translation units has no
    // guaranteed external definition, so this stays correct at -O0 too.
    bc7enc_compress_block_params p;
    p.m_max_partitions_mode = (uint32_t)max_partitions;
    p.m_uber_level = (uint32_t)uber_level;
    p.m_try_least_squares = BC7ENC_TRUE;
    p.m_mode_partition_estimation_filterbank = BC7ENC_TRUE;
    p.m_use_mode5_for_alpha = BC7ENC_TRUE;
    p.m_use_mode7_for_alpha = BC7ENC_TRUE;
    if (perceptual)
    {
        p.m_perceptual = BC7ENC_TRUE;
        p.m_weights[0] = 128; p.m_weights[1] = 64;
        p.m_weights[2] = 16;  p.m_weights[3] = 32;
    }
    else
    {
        p.m_perceptual = BC7ENC_FALSE;
        p.m_weights[0] = 1; p.m_weights[1] = 1;
        p.m_weights[2] = 1; p.m_weights[3] = 1;
    }

    int bw = (width + 3) / 4;
    int bh = (height + 3) / 4;
    for (int by = 0; by < bh; ++by)
    {
        for (int bx = 0; bx < bw; ++bx)
        {
            uint8_t block[64];   // 16 RGBA pixels, raster order within the block
            for (int py = 0; py < 4; ++py)
            {
                int sy = by * 4 + py;
                if (sy >= height) sy = height - 1;          // clamp bottom edge
                const uint8_t *row = src + (size_t)sy * width * 4;
                for (int px = 0; px < 4; ++px)
                {
                    int sx = bx * 4 + px;
                    if (sx >= width) sx = width - 1;        // clamp right edge
                    const uint8_t *s = row + (size_t)sx * 4;     // BGRA
                    uint8_t *d = block + (py * 4 + px) * 4;      // RGBA
                    d[0] = s[2]; d[1] = s[1]; d[2] = s[0]; d[3] = s[3];
                }
            }
            bc7enc_compress_block(out + (size_t)(by * bw + bx) * 16, block, &p);
        }
    }
}
