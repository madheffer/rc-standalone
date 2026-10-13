# Textures: resourcecompiler's pipeline, measured

How `resourcecompiler.exe` turns an image into a `.vtex_c`, settled from the DLLs (Ghidra
on `resourcecompiler_20260923.dll`, `tier0.dll`, calls into Valve's own `bc7e.dll` and
`ati_compress_wrapper.dll`) and checked value for value against fresh RC compiles of probe
textures (2026-10-13, `content/csgo_addons/vpkstuff_rc_probe/materials/s2ctex`: 256², 1024²,
300x200, 7x5, 4x4, opaque and alpha PNGs into BC7, DXT1, DXT5, ATI1N, ATI2N, RGBA8888).
Addresses are for that build.

## Mip chain (`CTextureFrame::GenerateMips_Box` 1801443f0)

Every level is float for float what RC holds: `capture_mips.py` hooks `QuarterSize` and the
per-channel half under Frida during an RC compile, and the 256², 300x200, 7x5 (sRGB) and ATI1N
(linear) chains match bit for bit at every level, so every mip byte and the reflectivity follow.

1. **Decode** (`FloatBitMap_t::LoadFromBuffer`, RGBA8888). An 8-bit sRGB channel becomes linear
   through `tier0!g_Mathlib_GammaToLinearByteTable` (180c34560), 256 floats printed to six
   decimals. It is the 128/53 power curve (not 2.4) and no formula reproduces it (232 of 256
   entries at best), so the table is carried verbatim; alpha is `v / 255f`. A linear image
   (180c32920) goes through SSE as `v * (1/255f)` when its width is a multiple of 4, else scalar
   `v / 255f`, all four channels. The two differ in the last bit, which flips ties when a box
   average lands on .5: that was all that kept ATI1N/ATI2N's lower mips apart.
2. **Downsample** (`FloatBitMap_t::QuarterSize` 180c43450), each level from the previous one in
   float, never re-quantized. The new size is `w > 3 ? w / 2 : 1` by `max(1, h / 2)`.
   - both sides even: 2x2 box (1812764c0). When the output width is a multiple of 4 and at least
     8 the SSE loop adds the column pairs, `((TL + BL) + (TR + BR)) * 0.25f`; otherwise the scalar
     loop adds `(((TL + BL) + TR) + BR) * 0.25f`.
   - a side is odd and the new size is `2n` or `2n + 1` of it (180c42690): a 3-tap filter per
     axis, taps `2x, 2x+1, 2x+2` (clamped) weighted `w0 = (n-x)/W, w1 = n/W, w2 = (x+1)/W` with `W`
     the source size. The weights sum to `(2n+1)/W`, so an even side next to an odd one is scaled
     by `(2n+1)/2n` (a 7x5 image's 1x1 alpha comes out 1.5). Each row is
     `(w0*p0 + w1*p1) + w2*p2`; the rows combine as `(top*ft + mid*fm) + bot*fb` with each factor
     a single product, `ft = (m-y)*ih, fm = m*ih, fb = (y+1)*ih`.
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

- Reflectivity is the 1x1 level in float, linear, alpha included (so 1.5 for the 7x5 probe),
  bit for bit on every fixture.
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
(rule 4), then ATI DXT5, perceptual weights when the texture is sRGB and 1 when it is linear.
64 of 64 blocks on every fixture.

## Block encoders

| format | encoder | settings | measured |
|---|---|---|---|
| BC7 | `bc7e.dll` (bc7enc_rdo `bc7e.ispc` c4a839c, Apache 2.0) | level from encode quality (0→1, 1→2, 2→3, 3/4→6); RC as a console app uses quality 3, so level 6 = `init_slowest`; perceptual for sRGB | 4096/4096 with ispc 1.13 to 1.24, `--opt=fast-math`, target `avx2-i32x4` |
| DXT1, DXT3, DXT5 | `ati_compress_wrapper.dll` (`ATI_TC_ConvertTexture`) = Compressonator 2.3's SSE2 codec | channel weighting and adaptive weighting on, weights 0.3086 / 0.6094 / 0.0820 when perceptual else 1, speed normal; ATI formats 0xF / 0x10 / 0x11 | every block of every mip, all probes |
| ATI1N, ATI2N | same | ATI formats 0x1F, 0x20, weights 1 | every block of every mip |

The BC7 blocks are input as `ROUND(clamp(x)*255)` (nearest even), a partial block repeating its
last pixel. `bc7e.dll` dispatches on the CPU (sse2, sse4, avx, avx2 at 4 lanes): fast-math lets
ISPC use `rcpps`, whose estimate differs between AMD and Intel, and the avx2 path fuses
multiply-adds. Valve's own output therefore depends on the machine: the same build gives
4096/4096 on the Ryzen that compiled the references and 3984/4096 on an Intel i5-8600.

## BC7 without the CPU

`rcpps` is the only instruction in `bc7e.dll` whose result depends on the vendor (FMA and sqrt
are exact IEEE). For `x = s * 2^e * 1.m` both vendors return `s * 2^-e * T[m]`, every exponent
checked (`tools/re/texture/bc7e/rcpedge.c`), and `T` depends only on the top 12 bits of `m` on
AMD (Ryzen 7 7800X3D) and the top 11 on Intel (Core i5-8600); `rcpss` and 256-bit `vrcpps` agree
with `rcpps`. The two tables differ on half of all mantissas. Under fast-math ISPC emits `a / b`
as `iv = rcpps(b); a * (iv * (2 - b*iv))`, so `src/Source2.Compiler/Native/bc7e/prepare.py` rewrites each variable division
as that expression with `iv` from `rcp_amd.txt` or `rcp_intel.txt` (`-DBC7E_RCP_INTEL`).
`inv_n = 1.0f / ((float)N - 1)` stays a division: `N` is a constant at its call sites and ISPC
folds it exactly. On 101,632 blocks (the 1024² probe, noise, gradients, flat and near-flat):
the AMD table on the Intel CPU equals AMD hardware, the Intel table on the AMD CPU equals Intel
hardware, and the AMD build still matches resourcecompiler's BC7.

Which CPU to follow: it does not matter to the game (any valid BC7 decodes), only to byte
parity against a given reference. Valve's stock files come from Valve's build machines, which we
cannot see; the references we compile come from the AMD machine. So both tables ship and the
build picks one: AMD by default (it is what our RC references hold), Intel when a reference
compiled on Intel is the target, and the output is the same on whatever CPU runs the encoder.

## DXT and ATI: ATI_Compress, reproduced

`FloatBitMap_t::WriteToBufferDXT_atitc` (180c48210) writes each mip with the RGBA8888 writer
(rule 4, format 2) and hands it to `ATI_TC_ConvertTexture` as ARGB8888, pitch `w*4`, with the
options above (doubles `0x3fd3c01a36e2eb1c`, `0x3fe380346dc5d639`, `0x3fb4fdf3b645a1cb` when the
perceptual flag, bit 1 of the format flags, is set). The DLL is ATI_Compress of the ETC/ATC era.
Its block code is Compressonator 2.3 (GPUOpen-Tools/compressonator tag `V2.3.2953`, MIT,
`CompressonatorXCodec.cpp`) built with `USE_SSE`, the last release that still has the SSE2 paths;
current Compressonator stripped them and matches only a third of the blocks. Three details make
it exact (`tools/re/texture/ati`: `prepare.py` fetches and patches 2.3, `compare_dll.py` checks
against the DLL):

1. **Weights are squared.** `SetParameter` stores `w * w`, so the perceptual base weights reach
   the codec as 0.3086^2, 0.6094^2, 0.0820^2.
2. **Adaptive weighting reads bytes as pixels.** `CalculateColourWeightings(CMP_BYTE block[])`
   takes `block[k] & 0xff0000` of single bytes: red and green medians are 0 and the blue median
   is the sum of the first 16 bytes (all four channels of the first four pixels) / 16. Valve's
   DLL has the same code (1800318f0).
3. **The alpha refine sums two lanes.** Where `Refine1` inlines `RmpSrch1SSE2`, the compiled
   horizontal add reads a register it has just zeroed, so only lanes 0 and 1 of the error count
   (18003be80). The global search sums all four, as `(a1+a3)+(a0+a2)`.

A partial block repeats its own valid pixels (`x % validW`, `y % validH`). A DXT5 request on an
image whose alpha is 255 everywhere is written as DXT1 ("Found DXT5 texture with solid 255 alpha!
Saving as a DXT1!"). Checked against fresh RC output: DXT1 every mip of 256x256, 300x200
(scalar-writer widths included), opaque and 7x5; DXT5 every mip of 256x256; the 1024x1024 and
4x4 probes against the DLL.

ATI1N/ATI2N are exact at every mip once the linear loader rule (decode, above) is followed.

## In C#

`src/Source2.Compiler/Texture/Rc`: `RcMipChain` (decode, `QuarterSize`, encode, the RGBA8888
writer) and `AtiCompress` (the DXT/ATI port, scalar code that keeps the SSE2 order and rounding).
`RcTextureParityTests` checks them against RC's files in `tests/.../RcReference/textures`: every
mip of RGBA8888 (256x256, 300x200, 7x5), DXT1 (256x256, 300x200, opaque), DXT5 (256x256) and the
opaque 7x5 written as DXT1, ATI1N and ATI2N, byte for byte, with every fallback and
reflectivity. Against Valve's DLL directly
(ctypes) the port is exact on every block of the probes and of six synthetic 256x256 images
(noise, gradients, a six-colour palette, near-black with mixed alpha, one channel, flat blocks).

The C++ harness in `tools/re/texture/ati` is not a reference for every block: 2.3's
`RefineSSE2` reads uninitialised stack for the padding colours of the last SIMD group, and the
harness's stack once differed from the DLL's on the first block of an image. The C# port zeroes
them, which is what the DLL's output agrees with.

## `.vtex` sources: `RcTexture.VtexCompiler`

`ResourceCompiler` compiles a `.vtex` (KeyValues2 `CDmeVtex`) to resourcecompiler's bytes:
every fixture in `tests/.../RcReference/textures` (RGBA8888, DXT1, DXT5, ATI1N, ATI2N and BC7 on
256x256, 300x200, 7x5, 4x4, opaque and no-LOD probes) and the 1024x1024 BC7 probe are identical.

- Mips stop before the shorter side drops under 4 when block compressed; RGBA8888 goes to 1x1.
- `NO_LOD` (0x8) when `m_bNoLod`, or when block compressed and either side is not a power of two.
- DXT5 with every alpha 255 is written as DXT1 (RC: "Found DXT5 texture with solid 255 alpha!").
- `COMPRESSED_MIP_SIZE` only when the largest side is at least 33; each mip is LZ4 HC level 10
  and kept compressed only when `c != 0 && raw*95 > c*100 && c < raw - 0x8000` (gameinfo
  `CompressMinRatio 95`). No fixture compresses, so the method word of that case is unmeasured
  and the compiler warns when it happens.
- BC7 goes through `Native/bc7e` (`bc7e_s2c_{amd,intel}` in `prebuilt/`, built by `build.ps1` / `build.sh`
  from `prepare.py` with ISPC 1.24, prebuilt for win-x64 and linux-x64 and shipped under
  `runtimes/<rid>/native`). `VtexCompileSettings.Bc7Vendor` picks the table, AMD by default.
  Block input: `ROUND(clamp(x)*255)` to nearest even, sRGB channels encoded first, a block past
  the edge repeating the last row and column. It needs an x64 CPU with AVX2 and FMA.
- Anything not measured is refused with an error naming the setting: other `.vtex` attributes,
  several inputs or channels, channel remapping, mip algorithms other than Box, colour-space
  conversion, cube/3D, and an image `.txt` sibling.

## What is left

1. The compressed-mip method word: a compressible probe (a mip at least 32 KB smaller after LZ4).
2. The settings refused above, each measured against RC before it is accepted.
3. The vpkstuff texture goldens: every one moves, so re-bless win-x64 and linux-x64 with the
   cause and check in game.
