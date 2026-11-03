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
   - both sides even: 2x2 box (1812764c0, run per channel by 18127a9c0). When the output width
     is a multiple of 8 (18127a9c0 tests `w > 7 && w % 8 == 0`) the SSE loop adds the column
     pairs, `((TL + BL) + (TR + BR)) * 0.25f`; otherwise the scalar loop adds
     `(((TL + BL) + TR) + BR) * 0.25f`. Multiple of 8, not 4: a 24x40 image's 12-wide level is
     scalar (odd_png's reflectivity, one ulp, was the only fixture that tells them apart).
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

DXT5 is what the engine expects too. The texture-streaming fill that binds fallbacks
(`rendersystemdx11_20261029.dll` 180048e30, logging `TEXTURESTREAMING: Filled %d fallback
textures`) passes image format 0x17 to the format-info lookup (180090d10) and uploads 32x32 from
the 1024 bytes for every texture, whatever its own format. The engine's format-name table puts
`DXT5` at index 23. `rendersystemvulkan_20261029.dll` does the same (1800bd290,
`mov ecx, 0x17`). A fallback in any other format is drawn as DXT5 garbage until the mips stream in.

Valve's stock fallbacks keep only the channels the texture outputs. Out of 200 pak01 textures
sampled on 2026-11-02: every ATI1N fallback is R alone (G, B and alpha 0), every ATI2N fallback
has alpha 0, and 38 of 40 DXT1 fallbacks have alpha 0. That follows the `.vtex` channel mapping,
which is not ported (see below), so the in-house builder's fallback carries the image's RGBA.

### The in-house builder (`ResourceBuilder.BuildTexture`)

It makes its fallback with the same steps (`ResourceBuilder.FallbackBits`). The pick is the last
level of its own chain whose shorter side is at least 32 (level 0 when none is). When that is the
chain's last level, RC's box downsample continues it below, since RC picks from its whole chain.
The bytes load as `v / 255` (they are already the stored, gamma-encoded values RC resamples).
Then come RC's resampler and RC's DXT5, perceptual when `TextureDef.Srgb` (default sRGB, linear
for BC4/BC5). Given RC's own chain (`MipChainBgra`), the fallback is RC's byte for byte
(`BuildTextureFallback_GivenRcChain_MatchesResourceCompiler`: DXT5, DXT1, BC7, ATI2N, ATI1N).
Against the BC7 fallback it replaced, measured on 254 textures (200 stock textures rebuilt,
probes, synthetic cases) with an exact area average of the input as the reference:
- Before, what CS2 drew scored 8 dB median. Now it scores 35 dB, and no case got worse.
- It beats the old resize encoded as DXT5 in 243 of 254 cases.
- The cases it loses by more than 3 dB are all textures of 8x8 and smaller. There RC's wrapping
  resampler pulls the opposite edge in, which is RC's measured rule.

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
  `CompressMinRatio 95`); the method word is 1 when any mip is compressed, 0 otherwise
  (rgba_flat512: mips 0-2 compressed, 128x128 and smaller raw; dxt1_flat512: mip 0
  compressed, the 32 KB mip 1 raw since it cannot shrink by 32 KB; both byte-identical).
- BC7 goes through `Native/bc7e` (`bc7e_s2c_{amd,intel}` in `prebuilt/`, built by `build.ps1` / `build.sh`
  from `prepare.py` with ISPC 1.24, prebuilt for win-x64 and linux-x64 and shipped under
  `runtimes/<rid>/native`). `VtexCompileSettings.Bc7Vendor` picks the table, AMD by default.
  Block input: `ROUND(clamp(x)*255)` to nearest even, sRGB channels encoded first, a block past
  the edge repeating the last row and column. It needs an x64 CPU with AVX2 and FMA.
- Several inputs, image processors and output channel groups compile through the same code as a
  material's children (`RcTextureChannels`, docs/MATERIALS.md "Child textures"): each group runs
  its mip algorithm on its inputs' whole images (`CTextureFrame::GenerateMips` 180142990, called
  from 180133da0 before the channel copy 181277690), a channel names its inputs and reads the
  first input with that name, and the RED2 lists each mip algorithm's and processor's special
  dependency. The four `algorithms/hemi_*` probes and `aw_color64` (NormalizeNormals, Inverse,
  HemiOctIsoRoughness_RG_B, ArtistDefinedOrAlphaWeighted beside a Box alpha) compile to RC's whole
  files.
- DXT5 becomes DXT1 when every stored mip's alpha is exactly 1 (180133da0 tests plane 3 of the
  mips it keeps; a 7x5 image stores one level, though its odd-side children carry alpha 1.5).
- Anything not measured is refused with an error naming the setting: other `.vtex` attributes,
  mip algorithms and image processors not ported, processor arguments, an sRGB group moving alpha
  to a colour channel or back, colour-space conversion, cube/3D, and an image `.txt` sibling.

## Mip algorithms (`CTextureFrame::GenerateMips` 180142990)

The dispatcher matches the channel group's algorithm name and calls one generator; each takes
the group's whole input image(s) as loaded (all four channels; the channel copy picks the source
channels afterwards) and registers its special dependency. Which algorithms CS2's shaders use
(their `m_mipProcessingCommand` over all 103 programs of `shaders_pc_dir.vpk`): ArtistDefined,
Box, Box3, HemiOctIsoRoughness_RG_B, ArtistDefinedOrAlphaWeighted, PreserveCoverage, None,
BoxInverse, HemiOctAnisoRoughness, AnisoRoughness_RG, MipNormal, AnisoNormal (generic) and RGBM
(sky). Ported and checked whole-file against RC's 11-03 compiles of `RcReference/textures/algorithms`
(`VtexSource_WithChannelGroupsAndAlgorithms_CompilesToResourceCompilersBytes`):

| algorithm | generator | what it does | special dependency |
|---|---|---|---|
| `None` | 180144200 | level 0 only; a texture whose groups are all None stores one mip (180133da0 leaves its output loop at a group's mip count of 1) | Mip None 1 |
| `BoxInverse` | 1801447c0 | level 0 is `v * -1 + 1` per channel, then the box chain; power of two only | Mip Box Inverse 1 |
| `MipNormal` | 1801443f0 | the Box chain under its own name | Mip Normals 1, Mip Box 1 |
| `PreserveCoverage` | 18014fa90 | below | Mip PreserveCoverage 5 |
| `HemiOctAnisoRoughness` | 180148350 | the hemi-octahedral chain unshuffled (lambda 180149d20): 0 the X roughness, 1 `(x - y)/2 + 1/2`, 2 the Y roughness, 3 `(x + y)/2 + 1/2` | Mip HemiOctAnisoRoughness 3 |
| `AnisoRoughness_RG` | 180148350 then the shuffle in 180142990 | channel 1 takes channel 2, channels 2 and 3 become 1 | Mip AnisoRoughness_RG 1, Mip HemiOctAnisoRoughness 3 |
| `HemiOctIsoRoughness_RG_B` | as above | R = 3, G = 1, B = `(ch2 + ch0)/2`, A = 1 (docs/MATERIALS.md) | Mip HemiOctAnisoRoughness 3, Mip HemiOctIsoRoughness_RG_B 1 |

`PreserveCoverage` keeps an alpha-tested texture's coverage down the chain. The coverage of
channel 0 at a scale (18014f380): every 2x2 cell's corners scaled and clamped to [0, 1]
(`maxss`, `minss`), sixteen bilinear samples per cell with RC's weight table (carried as bit
patterns), a sample counted at `v >= 0.5`, over `16 (w-1)(h-1)` (0/0 at a side of 1). Each level
is the box of the level before; unless the input's coverage at 1 is not above 0 or the level is
1x1, a bisection on [0, 8] (mid `(hi + lo) * 0.5`, `lo = mid` while the level's coverage there is
below the target, until `hi - lo` is at most 0.01) gives the scale `(hi + lo) * 0.5` for channels
0..2, clamped to [0, 1] (180142710); then, in place and row by row, a pixel whose clamped 3x3
neighbourhood of channel 0 is wholly at least 0.5 or wholly under it takes its unscaled values
back (18014f840). Probes: `pc_rgba` (the mask alone) and `pc_alpha_rgba` / `pc_alpha_bc7`
(alpha weighting beside a PreserveCoverage alpha, csgo_complex's alpha-test packing).

## Image processors (`CTextureFrame::ApplyImageProcessor` 18013f300)

An input's processor runs on its whole loaded image before the group's mip algorithm. CS2's
shaders use NormalizeNormals, Inverse (docs/MATERIALS.md), Mod2XCenter (csgo_complex,
lightmappedgeneric, 4wayblend and vertexlitgeneric detail textures) and ConvertToYCoCg (sky).
`Mod2XCenter` (18013fee0, registering "Image Mod2X" 1): per channel in the bitmap's mask (all
four for an RGBA8888 load) the sum of every pixel in row order, then each pixel
`(0.5 - sum / (w*h)) + v`. `mod2x_rgba`, `mod2x_srgb_rgba` and `detail_bc7` (csgo_complex's
g_tDetail packing) compile to RC's files. Not ported: ConvertToYCoCg (180141720),
HeightToNormal, FillToPowerOfTwo, FillToMultipleOfFour, ScaleToPowerOfTwo,
DilateColorInTransparentPixels, EncodeRGBM, UniformRandom (no CS2 shader names them).

## A mip chain made elsewhere

`VtexCompileSettings.MipChainBgra` (and `TextureDef.MipChainBgra` on the in-house path) takes a
finished chain: 8-bit BGRA, level 0 at the image's size, each level half the one before. It is
checked level by level and encoded as given (no resampling, no colour-space conversion), which
makes the output that chain's texture rather than resourcecompiler's. vpkstuff uses it to ship the
mips CS2 builds for a composite at runtime (a linear-light box chain). An 8-bit level survives the
float load and the sRGB write exactly, so an RGBA8888 texture stores the given bytes (MipChainTests).

## Decoding: `Textures.CompiledTexture`

`CompiledTexture.Read(bytes)` opens a `.vtex_c` without VRF. `GenerateBitmap(depth, face, mip, codec)`
returns what VRF's `Texture.GenerateBitmap` returns, as an `SKBitmap`: BGRA8888, or RgbaF32 for a
high dynamic range format unless `TextureCodec.ForceLDR` is passed. Every pak01 texture (71,966 of them,
12 formats) decodes to VRF's pixels, byte for byte, at mip 0 with the codec read from the edit info,
with no codec, and forced to 8 bits. A sample of 60 per format also matches at every mip, face and slice.
The checks ran on 2026-10-14 against vpkstuff's VRF, through the scratch `vrfparity texture` harness.

- **Layout.** The DATA block holds a 40-byte header and extra data entries; METADATA gives the display
  rect, COMPRESSED_MIP_SIZE the per-mip stored size (LZ4 when its first field is 1), CUBEMAP_RADIANCE_SH
  the coefficients. Mips follow the DATA block, smallest first. A mip's stored size is
  `min(uncompressed, listed)`. A block format pads to 4x4, and a 2- or 3-slice block texture reads as
  4 slices, which is VRF's rule. `ReadMip` returns one mip's decompressed bytes, `RawImageData` the
  embedded PNG/JPEG/WebP.
- **Block decoders** (`Textures.Decoding.BlockDecoders`), written from the D3D11 specification:
  - BC1 interpolates on the stored 5- and 6-bit values and rounds once to 8 bits (`round(sum/n *
    255/max)`), so a third of the way from 1 to 0 in 5 bits is 5, not 6. This is what TinyBCSharp
    (VRF's decoder) does; it was found by probing it with synthetic blocks.
  - BC1 inside BC3 always uses four colours.
  - BC4 and BC5 interpolate the 8-bit endpoints with rounding (`+3)/7`, `+2)/5`).
  - BC4 decodes to grey and BC5 to red/green with blue 0.
  - BC7 and BC6H follow the specification's tables. The partition and anchor tables are generated
    from `Native/bc7enc/bc7enc.c`.
  - BC6H decodes to half floats and goes to 8 bits by clamping.
- **Other formats** (`PixelDecoders`), per VRF:
  - 16-bit unsigned channels are `/256` in LDR and `/65535` in HDR.
  - RGBA16161616(F) to LDR is a log-average luminance tonemap with gamma 2.25.
  - Float formats clamp.
- **Codec** (`CodecFromEditInfo`) comes from the `CompileTexture` special dependencies:
  - YCoCg, NormalizeNormals and HemiOct.
  - YCoCg on a cube is sRGB.
  - DXT5 with NormalizeNormals is DXT5nm (red and alpha swapped).
  - HDR formats are linear.
- **Not decoded:** the ETC2/EAC family, which throws `NotSupportedException`; no CS2 texture uses it. Format 33
  (17 pak01 tint-ID masks, 1 byte a pixel, unknown to VRF too) has no name until the texture compiler
  gives one. `ReadMip` returns its bytes. RGBM is flagged but not decoded, as in VRF.
- **One deliberate difference.** Uncompressed rows are read at the stored width, so a display rect
  narrower than the texture crops correctly. VRF reads them as if packed at the rect's width. No pak01
  texture has such a rect on an uncompressed format.

`SpriteSheet.Read` is the SHEET reader beside `Write`: a frame may carry several images (multi-layer
particles store four), and stock particle sheets read and write back to their own bytes.

## What is left

1. The settings refused above, each measured against RC before it is accepted.
2. The vpkstuff texture goldens: every one moves, so re-bless win-x64 and linux-x64 with the
   cause and check in game.
3. The DXT5 fallback in game. No setting holds a texture on its fallback, so the only sign is
   the frames between a texture's creation and its first streamed mip. Read in
   `rendersystemvulkan_20261015.dll` on 2026-11-03:
   - every `r_texture_stream_*` / `r_texture_budget_*` convar registers with flags 0, and the
     convar setup (180237010) adds devonly (`| 0x100000002`) to such a convar unless the command
     line has the parameter whose MurmurHash2 (seed 0x31415926) is 0x0bc4f1a2, `-tools`. Retail
     CS2 refuses them; Workshop Tools accepts them;
   - `r_texture_stream_max_resolution` (callback 1800f49e0 -> 180102dd0) rounds to a power of two
     and clamps to at least 64;
   - `r_texture_stream_mip_bias` is applied in the per-texture pass (180104b80) as
     `mips - bias`, clamped to at least 1, so one real mip always stays wanted;
   - `r_texture_stream_throttle_count` / `_amount` (180106150) stop a frame's streaming only after
     a texture has streamed, so even 0 streams one texture per frame, and loading is unthrottled.
   Helm (11-03): the compiler's texture loads and draws in GFL's hitmarker particle (red `+`).
