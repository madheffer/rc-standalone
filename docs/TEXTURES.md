# Textures: resourcecompiler's pipeline, measured

How `resourcecompiler.exe` turns an image into a `.vtex_c`, settled from the DLLs (Ghidra
on `resourcecompiler_20260923.dll`, `tier0.dll`, calls into Valve's own `bc7e.dll` and
`ati_compress_wrapper.dll`) and checked value for value against fresh RC compiles of probe
textures (2026-10-13, `content/csgo_addons/vpkstuff_rc_probe/materials/s2ctex`: 256², 1024²,
300x200, 7x5, 4x4, opaque and alpha PNGs into BC7, DXT1, DXT5, ATI1N, ATI2N, RGBA8888).
Addresses are for that build.

## Mip chain (`CTextureFrame::GenerateMips_Box` 1801443f0)

Every value of every mip of the RGBA8888 probes (256², 300x200, 7x5) matches.

1. **Decode.** An 8-bit sRGB channel becomes linear through `tier0!g_Mathlib_GammaToLinearByteTable`,
   256 floats printed to six decimals. It is the 128/53 power curve (not 2.4) and is not
   reproduced by any formula (232 of 256 entries at best), so the table is carried verbatim.
   Alpha and linear channels are `v / 255.0f`.
2. **Downsample** (`FloatBitMap_t::QuarterSize` 180c43450), each level from the previous one in
   float, never re-quantized. The new size is `w > 3 ? w / 2 : 1` by `max(1, h / 2)`.
   - both sides even: 2x2 box, `((a + b) + (c + d)) * 0.25f` (a, b the top row).
   - a side is odd and the new size is `2n` or `2n + 1` of it (180c42690): a 3-tap filter per
     axis, taps `2x, 2x+1, 2x+2` (clamped) weighted `(n-x)/W, n/W, (x+1)/W` with `W` the source
     size. The weights sum to `(2n+1)/W`, so an even side next to an odd one is scaled by
     `(2n+1)/2n` (a 7x5 image's 1x1 alpha comes out 1.5). Summed middle row, top row, bottom row.
   - otherwise (a side of 1, 180c422a0): clamped 2x2 box, `((p(x+1,y) + p(x,y)) + p(x,y+1)) + p(x+1,y+1)`, times 0.25.
3. **Encode** (18126a7c0, SIMD): `x^(53/128)` built as
   `s1=sqrt(x) s2=sqrt(s1) s3=sqrt(s2) s4=sqrt(s3) m=s2*s3 s5=sqrt(s4) m*=s4 s6=sqrt(s5) m*=s6 s7=sqrt(s6) r=(1/(s5*s7))*m`,
   then `1.055f*r - 0.055f`, or `12.92f*x` at or below 0.0031308.
4. **Write** RGBA8888 (180c41d70): a row of a multiple of 4 pixels goes through SSE
   (`x*255`, clamp to 0..255, rounded to nearest even via `+2^23`); any other row is scalar
   `(int)(clamp(x,0,1)*255 + 0.5f)`.

Mip count: down to 1x1 for RGBA8888; for block formats the chain stops before a side would go
under 4 (256² BC7: 7 mips, 300x200: 6, 7x5: 1).

## Header

- Reflectivity is the 1x1 level in float, linear, alpha included (so 1.5 for the 7x5 probe).
- Extra data: `FALLBACK_BITS` then `COMPRESSED_MIP_SIZE`; no `METADATA`.
- Flag 0x8 (NO_LOD) comes from `m_bNoLod`, and is forced on a block-compressed texture whose
  sides are not powers of two.
- DXT5 asked for on a fully opaque image is written as DXT1.
- ATI1N and ATI2N reflectivity is linear.
- Format `RGBA8888` (not BGRA8888).

## Fallback (`FALLBACK_BITS`, 1024 bytes)

The smallest mip whose shorter side is still at least 32 (mip 0 when none is), resampled to
32x32 by `FloatBitMap_t` bilinear (181279940; wrap unless the texture clamps; sample centres at
`(i + 0.5) * src/32` accumulated in float) from the gamma-encoded floats, written as BGRA8
(rule 4), then ATI DXT5 with perceptual weights. 64 of 64 blocks on all three probes.

## Block encoders

| format | encoder | settings | measured |
|---|---|---|---|
| BC7 | `bc7e.dll` (bc7enc_rdo `bc7e.ispc`, Apache 2.0) | level from encode quality (0→1, 1→2, 2→3, 3/4→6); RC as a console app uses quality 3, so level 6 = `init_slowest`; perceptual for sRGB | 4096/4096 with current `bc7e.ispc`, `--opt=fast-math`, target `avx2-i32x4` |
| DXT1, DXT3, DXT5 | `ati_compress_wrapper.dll` (`ATI_TC_ConvertTexture`) | channel weighting on, weights 0.3086 / 0.6094 / 0.0820 when perceptual else 1, adaptive weighting on, speed normal; ATI formats 0xF / 0x10 / 0x11 | 4096/4096 |
| ATI1N, ATI2N | same | ATI formats 0x1F, 0x20, weights 1 | 4096/4096 |

The BC7 blocks are input as `ROUND(clamp(x)*255)` (nearest even), a partial block repeating its
last pixel. `bc7e.dll` dispatches on the CPU (sse2, sse4, avx, avx2 at 4 lanes): fast-math lets
ISPC use `rcpps`, whose estimate differs between AMD and Intel, and the avx2 path fuses
multiply-adds. Valve's own output therefore depends on the machine: the same build gives
4096/4096 on the Ryzen that compiled the references and 3984/4096 on an Intel i5-8600.

The "measured" column for DXT and ATI is Valve's own DLL called through ctypes with those options.
ATI_TC is closed source (ATI_Compress, the ETC/ATC era, statically linked into
`ati_compress_wrapper.dll`). Compressonator's open legacy codec (`compressonatorxcodec.cpp`,
`CompRGBBlock`, `CompBlock1X`, the adaptive weighting) built with the same options matches only
1213 (DXT1), 1070 (DXT5), 1246 (ATI1N) and 376 (ATI2N) of 4096 blocks. ATI1N endpoints follow the
red channel in both, so the input is right and the search differs: on a block spanning 0..12 ATI_TC
picks the 6-point ramp (3, 12) where Compressonator picks the 8-point one (11, 0). Next step is to
decompile the DLL's alpha and RGB block compressors (imported into the Ghidra project as
`/ati_compress_wrapper.dll`) and port those instead.

## What is left to ship texture parity

1. Port the mip chain, writers, reflectivity, fallback and header rules to C# from
   `tools/re/texture/mipmodel.py` (exact on every probe).
2. BC7: bc7e level 6, perceptual, as a native ISPC build (avx2-i32x4, fast-math); decide whether to
   match the AMD reference machine or emulate `rcpps` so the output does not depend on the CPU.
3. DXT1/3/5, ATI1N/2N: an exact port of ATI_TC (see above).
4. Validate every probe in `vpkstuff_rc_probe/materials/s2ctex` byte for byte, then the vpkstuff
   texture goldens.
