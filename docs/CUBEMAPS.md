# Cubemaps

`maps/<map>/cubemaps/env_cubemap_array.vtex_c` is in 147 of the 185 workshop
maps surveyed on 2026-10-21 (14% of their bytes). It is the largest output the
port does not write. It is made in two halves:

1. **The game renders** each env_cubemap / env_cubemap_box (client.dll,
   `CBuildCubemapsGameSystem`, the `buildcubemaps` command or `-buildcubemaps`).
   It writes `<addon>/_buildcubemaps/env_cubemap_<handshake>.exr` per cubemap
   ("Building %d cubemaps, rendering: %dx%d, storage: %dx%d"), an empty
   `env_cubemap_<handshake>.txt` beside each, and the array's source
   `_buildcubemaps/env_cubemap_array.vtex`, then compiles it.
2. **resourcecompiler compiles the array** as an ordinary texture. That half
   is CPU code plus one small GPU pass, and is what this file settles.

Read from resourcecompiler 0923 (addresses in ADDRESSES.md, BAKE rows); the
1015 build has the same code at +0x40 (the table and layer) and +0x180
(GenerateMips_GGXCubeMapBlur).

## The game half (client.dll, 10-15; read from the disassembly)

- Settings from csgo/gameinfo.gi's WorldRenderer section (client 180218990 and its caller):
  EnvironmentMaps 1, EnvironmentMapBlurType GGX, EnvironmentMapFaceSize 256 (the
  storage size; default 128), EnvironmentMapRenderSize 1024 (default twice the
  face size), EnvironmentMapFormat BC6H, EnvironmentMapColorSpace linear,
  EnvironmentMapMipProcessor GGXCubeMapBlur, EnvironmentMapUseCubeArray 1. These
  fill the template's blanks.
- Each face renders at the render size and is reduced to the face size in float
  bitmaps on the CPU (client 18020e4c1 on: 0x9d8-byte frames from the render size
  halving down to the storage size), then composited into a storage x 6 by storage
  strip (4 x 3 cross with a flag) that becomes the EXR. A texel failing the
  validity check logs "Cubemap %d Face %d Invalid Pixel".
- The render itself is the game's scene renderer drawing the map from the probe
  (objects flagged rendertocubemaps, barn lights' bakespeculartocubemaps,
  r_render_to_cubemap_* convars). Not ported; this is the large remaining piece.

## The array's source

client.dll's template (file offset 0x1ad0c70 in the 10-15 client.dll), DMX
keyvalues2:

```
<!-- dmx encoding keyvalues2_noids 4 format vtex 1 -->
"CDmeVtex"
{
	"m_inputTextureArray" "element_array"
	[
		"CDmeInputTexture"
		{
			"m_name" "string" "inputTexture%i"
			"m_fileName" "string" "%s"
			"m_colorSpace" "string" "linear"
			"m_typeString" "string" "CUBE"
			"m_imageProcessorArray" "element_array"
			[
			]
		}%s                       (one per cubemap, comma separated)
	]
	"m_outputTypeString" "string" "%s"
	"m_outputFormat" "string" "%s"
	"m_textureOutputChannelArray" "element_array"
	[
		"CDmeTextureOutputChannel"
		{
			"m_srcChannels" "string" "rgba"
			"m_dstChannels" "string" "rgba"
			"m_mipAlgorithm" "CDmeImageProcessor"
			{
				"m_algorithm" "string" "%s"
			}
			"m_outputColorSpace" "string" "%s"
			"m_inputTextureArray" "string_array"
			[
				"inputTexture%i"%s
			]
		}
	]
	"m_bHiddenAssetFlag" "bool" "1"
}
```

The blanks, from Valve's output: output type `CUBEARRAY` (rc 18010a530's
name for type 0x16 / 0x2f), format BC6H, algorithm `GGXCubeMapBlur`. The
output colour space is not read yet (linear expected).

## The compiled file (ported: `Cubemaps/CubemapArrayVtex.cs`)

Current form, from Valve's 10-15 maps (de_dust2, de_mirage, cs_italy, de_anubis,
ar_shoots): 256 x 256 cubes, 7 mips (down to 4 x 4), extras COMPRESSED_MIP_SIZE
(method 0: BC6H does not pay under CompressMinRatio 95) and CUBEMAP_RADIANCE_SH.
DATA layout: header 40, two entries at 40 and 52, the mip size block at 64 with
the sizes at 76, the SH block at 76 + 4 mips and its floats 8 on. RED2: per cube
its EXR (CRC32) and the optional absent .txt beside it, then the .vtex; the
arguments ___OverrideInputData___, ImageArg and BinaryBlobArg per EXR, fast,
final, hueShiftFixup, lightmapMaxResolution, maxmipsize (1048576), minmipsize,
ordered by name as V_stricmp does (so `_` before letters); Texture Encode
Quality user data 3. `CubemapArrayVtexTests`: RED2 authored from de_dust2's own
paths and CRCs plus its DATA serialize to its container byte for byte, and DATA
rebuilt from its values is its DATA (de_dust2 predates the 10-15 KV3 writer, so
the test writes unsigned 0 and 1 as the old signed types, ledger 61).

Older workshop maps (ze_boatescape101_p, ze_fast_escape_p, ze_por_island_p):

- DATA: version 1, flags 0x50 (CUBE_TEXTURE | TEXTURE_ARRAY), 128 x 128,
  depth = the cubemap count, format 19 (BC6H), 6 mips (128 down to 4), the
  reflectivity, one extra block: type 5 (CUBEMAP_RADIANCE_SH), 8 bytes
  (offset 8, float count 27 x cubes), then the floats.
- The payload is the raw BC6H chain, exactly 6 x 21,840 bytes a cube.
- RED2: special dependencies Texture Compiler Version 11, Texture Compiler
  Version Mip GGXCubeMapBlur 2, Texture Encode Quality 1; argument
  dependencies ___OverrideInputData___, per EXR an ImageArg and a
  BinaryBlobArg, then etc, fast, final, hueShiftFixup (IntArg),
  maxcompiledtextureres, maxmipsize (fingerprint 0x49800000), minmipsize
  (FloatArg); input dependencies every EXR (its CRC), every .txt (CRC 0) and
  the .vtex; HiddenAssetFlag 1.

## GGXCubeMapBlur (ported: `Cubemaps/GgxCubeMapBlur.cs`)

`CTextureFrame::GenerateMips_GGXCubeMapBlur` (rc 180150130), per input cube:

1. The six faces, as RGBA32F (image format 0x13), go up into a cube
   `GGXCubeMapBlur_SourceCubemap.vtex` with a full mip chain; the render
   context then builds the mips (vf 0x2a0). On Vulkan that is a blit chain:
   rendersystemvulkan's `r_mipgen_compute_shader` defaults to 0 (its
   initialiser at rsv 1015 180001db0). The DX11 default compile's mips come
   from the driver.
2. The destination `GGXCubeMapBlur_DestCubemapArray.vtex` and the target
   `GGXMipBlurRT.vtex` are made at the same size.
3. The layer (rc 1800caf90) copies mip 0 face by face, then per mip from 1
   draws each face with `materials/dev/convolve_environment_map.vmat`
   (core's `convolve_environment_map` shader, one static and one dynamic
   combo) into the target and copies it into the destination's mip and face.
4. Mips from 1 are read back as RGBA32F into the frame's levels; mip 0 stays
   the input as loaded.

What each draw gets:
- **Quad:** four 0x30-byte vertices drawn as a strip: position (x, y, 0) at
  0, direction (1, y, -x) at 20. The vertex shader flips y and turns the
  direction by the face's rows `g_vMatRow0..2` (rc 1827685f0).
- **Taps:** a 64 KB constant buffer `FilterTapConstantBuffer_t`, 2048 float4
  (direction, source lod) then 2048 float4 (weight in x), and
  `g_nSampleCount` 2048. The pixel shader builds a basis around the
  normalised direction (up (1, 0, 0), or (0, 0, 1) when |z| < 0.999) and
  sums `max(textureLod(cube, basis * tap.xyz, tap.w) * weight, 0)` over the
  taps, sampler `Filter_MinMagLinearMipPoint`.
- **Schedule** (flags 3 from GenerateMips): count is the destination's full
  chain down to 1 x 1 (rc 180c49440), not the mips the texture keeps. The
  last three mips take the cosine table; the others the GGX table at
  roughness (mip / (count - 4))^2. A 128 cube (8 mips, 6 shipped): mips 1 to 4
  at 1/16, 1/4, 9/16 and 1, mips 5 to 7 cosine.
- **GGX table** (rc 1800caa20): cleared; a binary search over the
  Hammersley count n in (0x800, 0x4000) keeps the largest n whose taps with
  2c^2 - 1 > 0 number at most 0x800; each kept tap is the reflected
  direction (2c sin(theta) cos(phi), 2c sin(theta) sin(phi), 2c^2 - 1), lod
  0.5 log2((2pi / (pdf 2048)) / (4pi / (6 size^2))) + 1 clamped at 0, pdf =
  a^2 c / (pi d^2) with d = c^2 (a^2 - 1) + 1, weight 2c^2 - 1, the weights
  then scaled by one over their sum. a is the roughness squared.
- **Cosine table** (inline, rc 1800cb526): 2048 taps (sqrt(u) cos(phi),
  sqrt(u) sin(phi), sqrt(1 - u)), phi = i / 2048 times 2pi, lod as above
  with pdf c / pi, weight 1 / 2048.

Checked by `GgxCubeMapBlurTests` against `tools/re/emu_cubeblur_tables.py`:
the installed resourcecompiler.dll's own code under unicorn, tier0's
V_sincosf and V_log2f answered by tier0.dll through ctypes (needs no CS2).
Sizes 16 to 1024, every roughness mip / d and (mip / d)^2 for d up to 15:
all 1,001 tables byte for byte. The LOD needs tier0's own log2f
(`CrtMath.Log2`, the CRT's float log, not correctly rounded; equal to tier0 on
1,166,310 inputs, `tools/re/tier0_log2_oracle.py`).

## Radiance SH (ported: `Cubemaps/CubemapRadianceSh.cs`)

CompileResourceInternal (rc 1801378c0 .. 180138100), per cube of the frame,
from mip 0: each texel's direction from its face (jump table rc 180139788)
and centre ((row + 0.5) / size, (col + 0.5) / size), normalised (rc
18013e340), weight (m k)(m m) with m the largest absolute component and
k = 4 / size^2; each channel times the weight times the nine L2 basis values
(rc 18125d4d0) adds to its sums, which end scaled by 4pi over the summed
weights. An Inf or NaN texel fails the compile. Not yet checked against an
oracle; on shipped arrays (their BC6H mip 0 as the input) it is within 0.2 to
0.4% of the shipped floats, so the face order and directions are right.

## The GPU pass (`Source2.Compiler.Gpu/GpuCubemapBlur.cs`)

Valve's convolve_environment_map programs on our Vulkan host (ProgramRunner,
now with a strip topology): the source cube uploaded and blitted down, a
64 KB tap buffer per mip, the face rows per draw, RGBA32F targets read back.
The SPIR-V keeps no names, so the blocks are told apart by size (the tap
block is exactly 64 KB). `GpuCubemapBlurProbe` (`CUBEPROBE=<vtex_c>[|cubes]`)
runs it on a shipped array's decoded mip 0: ze_fast_escape_p's first three
cubes come within 1 to 5% mean relative error on mips 1 to 5, which is
BC6H's noise on both ends. Not yet compared bit for bit.

## The CS2-closed run

`python tools/re/cubemap_oracle.py` (refuses while CS2 runs; scratch addon
s2c_cubeprobe; output %TEMP%/cubemap_oracle), then
`CUBEORACLE=%TEMP%/cubemap_oracle` and run `CubemapOracleTests`. It reports
which EXR layout RC takes and RC's face order, the filtered RGBA32F mips against
GpuCubemapBlur float for float (DX11 and -vulkan), the SH and reflectivity, and
the whole BC6H file against CubemapArrayVtex.Build. `CUBEBUILD=<dir>` runs the
build alone (2 cubes of 256 in 2 s on the RTX 4070; VRF reads the result).

## Oracle results (2026-10-21, CS2 closed)

`tools/re/cubemap_oracle.py` then `CubemapOracleTests` (`CUBEORACLE=%TEMP%/cubemap_oracle`):
- The CUBE input is a 6 x 1 strip (`FacesFromStrip` equal to RC's cut).
- GpuCubemapBlur equals RC `-vulkan`'s filtered floats on all 1,572,840; RC's
  default DX11 compile is 1 ulp off on 1.1% of them.
- Radiance SH 81 of 81 floats; reflectivity rule settled (`Reflectivity`).
- `CubemapArrayVtex.Build`'s file equals RC's `-vulkan` BC6H file byte for byte,
  and RC's DX11 and `-vulkan` BC6H files are identical on this input.

## Open

- DX11 float parity for the GPU pass (only matters where a 1-ulp difference
  survives BC6H; none did on the oracle's input).
- The EXR the game writes: pixel type (half assumed) and header; needs one
  game-side buildcubemaps capture.
- The BC6H encoder is ispc_texcomp.dll from the game's bin folder (as for the
  lightmaps); building it from Intel's source, as bc7e was, removes that
  dependency.
- The game half: rendering the probes with the game's renderer. Not started.
