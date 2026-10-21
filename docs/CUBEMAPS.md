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
- Per cubemap and face (client 18020e130, decompiled from the 10-15 build in
  Ghidra, alias `client`): the face's angles from a six-entry table (client
  181ad10d0, 0x28 bytes each), the probe origin (record floats 0..2), FOV 90,
  near / far from record floats 6 and 7, aspect 1; a view "BuildCubemaps" on
  the "Game" rendering pipeline into `scratchcolor` (render format 0xf, read
  back as RGBA16F halves) and `scratchdepth` (0x2a), render size square. With
  r_draw3dskybox, a second view from the sky camera (client 180acb130: origin
  +0x13d4, scale +0x13d0; position = origin + probe / scale) goes in as
  render attribute 0xaa7a6a35. View attributes: 0xd7f4028 and 0x7052a413 the
  targets, 0x7de7125 = 1, 0xae401481 = 0, 0x8cd2032 / 0x987b4595 the two
  skybox convars, 0x654ec82e / 0xf820ab6c the record's byte 0x24 == 0,
  0x64a9813a the sky camera position, 0x1e961ef / 0x821e2402 a list from the
  sky camera's index. A half with exponent 0x7c00 logs "Invalid Pixel" (and
  exits with a command-line switch). The halves go into a float frame (client
  1813db210, format 0xf) and 1802091d0 reduces, composites and writes.
- After the render (ported: `Cubemaps/CubemapFaceExr.cs`): QuarterSize from
  the render size to the face size (client 1813ddd00: the same code as RC's
  180c43450 instruction for instruction, callees included, but for the
  allocator and job launcher, so RcMipChain.QuarterSize), a vertical flip
  (1813dc290: rows y and h - 1 - y swapped), the face copied into the strip at
  x = face * size (181756620; the strip starts as (0, 0, 0, 1)), and
  1813dede0(strip, path, 0, 1): compression 0, half pixels, tinyexr's fast
  writer, the same code as vrad3's FloatBitMap_t::WriteToFile (so Vrad3Exr with
  ties-away rounding). To confirm on a game-written EXR: the channel set (R, G,
  B, A assumed) and 1813db210's half to float.
- The "Game" pipeline the view runs (client strings, CRenderingPipelineCsgo):
  GPU culling (two pass, compute), depth prepasses, the light binner, the CS2
  shadow pipeline, SSAO and AO proxies, volumetric fog, static and dynamic
  opaque forward, decals, water refraction, MBOIT translucency, the 3D skybox
  views, MSAA resolves, bloom and post-processing. Which of these the
  BuildCubemaps view enables (its render attributes) is not read yet.

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

## Against the game's own build (2026-10-21)

tools/cubehost runs the game's engine and Workshop Tools in our own process
with no Steam (tools/cubehost/README.md). `run_probe.py --keep-intermediates`
loads probe01 over netcon (`map probe01 nomapvalidation=true`, then
`buildcubemaps`) and copies the game's `_buildcubemaps` files before it deletes
them: two EXRs, the .vtex and the compiled array ("Building 2 cubemaps,
rendering: 1024x1024, storage: 256x256"). `GameCubemapTests`
(`CUBEGAME=<intermediates>`):
- `CubemapArrayVtex.Source` equals the game's .vtex (CRLF line ends);
- each game EXR (1536 x 256, channels B, G, R: no alpha) re-written from its
  own values through Vrad3Exr is the same file;
- `CubemapArrayVtex.Build` on the game's EXRs equals the game's
  env_cubemap_array.vtex_c byte for byte (945,886 bytes).

So everything after the render is exact against the game. What remains is the
render of each face itself.

## The render, captured (2026-10-21)

The game's own RenderDoc hook, inside cubehost only (Steam closed, no Steam
code; never cs2.exe): `run_probe.py --renderdoc <cube>` runs the engine with
`-renderdoc` (rendersystemvulkan 1015 0d7ca0 loads
`C:\Program Files\RenderDoc\renderdoc.dll`), and BuildCubemaps starts a
capture (render device vfunc 0x638) when the cube counter equals
`buildcubemaps_renderdoc_capture`, ending it (0x640) after that cube's six
faces. The convar is development-only (client 1800538d8 registers it with
flags 2): release builds hide it from the console and the command line, so
cubehost writes it in its own process (`--renderdoc-capture`, the cvar data
at client's global 18237eab0, name checked, int value at +0x58). The engine
writes `%TEMP%/RenderDoc/cubehost_<date>_capture.rdc` and opens it in
qrenderdoc, which the runner closes. `tools/cubehost/rdc_summary.py` (run in
RenderDoc's Python: `qrenderdoc --python`) lists the actions.

probe01 cube 0 (1.75 GB capture, no debug markers in a release build):
138 draws and 12 dispatches, the same sequence for each of the six faces:

1. clear the sun's shadow atlas (4352 x 5248 D16), the light binner's two
   dispatches (not culling: see below), four cascade passes into it;
2. clear the face targets (1024 x 1024 RGBA16F colour, D24S8 depth);
3. depth prepass (opaque meshes: indirect draws of 1 + 3 + 2);
4. a full-screen triangle-strip draw on a 1280 x 720 D24S8 target (the main
   view's size), then a cleared target;
5. forward opaque pass into the RGBA16F face (1 + 3 indirect draws), then a
   36-index cube (the sky);
6. stencil clear, two image copies, a 256 x 256 D24S8 draw;
7. `vkCmdCopyImageToBuffer`: the readback that `CubemapFaceExr` starts from.

This is the per-pass ground truth for porting the face render: each pass's
shaders, constants and outputs can be read from the capture.

### The views, checked against the game (2026-10-22, ledger 118)

Each face's per-view constants are one 1024-byte block in a coherent upload
buffer (11 identical copies per face, the probe position at +560, the
`env_cubemap_fog` origin and keys at +512 and +432). The 4 x 4 at +192 is the
world-to-projection matrix, and `tools/re/emu_cube_views.py` reproduces it by
running client.dll's own face setup under unicorn (tier0's V_sincosf,
V_atan2f and V_tanf through ctypes): the face angles go through a
quaternion and back (`89.99999237`, not 90), then a view object from the
client's static template (FOV 90, aspect 1) with the record's origin and
near 1 / far 10000 (constants the buildcubemaps command writes into each record, client
1801ea0a0; ledger 118). The view's matrix at +0x288, transposed, is the upload
bit for bit on faces 1 to 5; face 0 differs only in one signed zero. Depth
is 0..1, not reversed. The six faces of the capture are the table's order:
+X, -X, +Y, -Y, +Z, -Z.

### What the face shaders read (2026-10-22)

`tools/cubehost/spv_usage.py` lists each module's uniform and storage blocks
and the members it reads. generic's vertex shader reads set 0 binding 1 at
+0 and +384 and binding 3 at +560 only; the pixel shader reads 17 members of
its _Globals_ (set 1 binding 0), five of binding 1 (+296 .. +416), 19 of
binding 3 and 28 of a large set 3 block (lights and the binner's output, up to
+31808); csgo_depth_only reads binding 1 (+0, +384) and the transforms.

Binding 1 is a 640-byte per-view block (one per layer of the face, 49 per
face in the upload): camera-relative rendering. +0 (and again at +560) is the
world-to-projection with the translation taken out, applied to
`position + (-camera)` from +384; +64 is the view object's +0x2c8 matrix
transposed, +128 its world-to-view (+0x1d8, 3 x 4) transposed, +192 its
projection (+0x208); +320 the viewport (1024 x 1024 and 1 / 1024); +368
(0, 0.95, 1, 10000); +400 the face angles in radians; +416 the camera; +464
(10000, 9999, -10000); +528 (1 / 1280, 1 / 720, 0.8, 1.4222), from the
1280 x 720 main window, not the face. The camera-relative matrix is the
projection times the world-to-view without its translation, transposed:
client.dll's own 4 x 4 multiply (181746490, under unicorn) gives it bit for
bit on faces 1 to 5. On face 0 one element's four terms are all -0.0: the
upload has -0.0 (a plain four-term sum), 181746490 gives +0.0 (it starts
from +0), so the game builds the upload with another multiply (presumably
scenesystem's); every other word agrees.

### Read without RenderDoc (2026-10-22)

`tools/cubehost/rdc_stream.py` reads the .rdc itself (RenderDoc 1.44 layout:
one frame-capture section, LZ4 in 1 MB pages that each refer back into the
previous one, chunks padded to 64 bytes), one page at a time, so it runs in
a few MB with no GPU. `rdc_actions.py` lists the frame in execution order
(each command buffer's recording where a vkQueueSubmit lists it), resolves
every draw's pipeline through its pipeline libraries (the game links
VK_EXT_graphics_pipeline_library pipelines from one-stage libraries) to the
shader modules, and prints the engine's annotations: the game calls
RenderDoc's SetCommandAnnotation for every batch (`batchlist.descriptor`,
`batchlist.flags_str`, `aggregate.model_name`, `aggregate.material_name`,
`materialsystem.mode_evaluated`...), which stand in for the debug markers a
release build lacks.

    python tools/cubehost/rdc_spirv.py <capture.rdc> <dir>      # each module as <resource id>.spv
    dotnet run -c Release --project tools/cubehost/spvmatch -- <dir> 3000000 <game>/core/shaders_vulkan_dir.vpk <game>/csgo/shaders_vulkan_dir.vpk
    python tools/cubehost/rdc_actions.py <capture.rdc> [modules.txt] > actions.txt

(modules.txt: `<resource id hex> <name>` lines from spvmatch's output.)

The whole frame holds 10 shader modules. Each one's SPIR-V is byte for byte
a file of Valve's .vcs (`shaders_vulkan_dir.vpk`, core and csgo; matched by
hash over every static combo's shader files):

| module | program | static combo | dynamic combo |
| --- | --- | --- | --- |
| 4dd | generic_vulkan_50_vs (also csgo_glass_vs 17/19) | 4: S_MODE_TOOLS_VIS=1 | D_BAKED_LIGHTING_FROM_LIGHTMAP=1, D_COMPRESSED_NORMALS_AND_TANGENTS=1 |
| 4f3 | generic_vulkan_50_ps | 4: S_MODE_TOOLS_VIS=1, all else 0 | 4: D_BAKED_LIGHTING_FROM_LIGHTMAP=1, no static specular cube, no MBOIT |
| 4ea | csgo_depth_only_vulkan_50_vs | 0 | 0 and 1 (D_COMPRESSED_NORMALS_AND_TANGENTS) |
| 6f7 | compute_depthbin_cullbits_vulkan_50_cs | 0 | 0 |
| 6f9 | compute_tile_cullbits_vulkan_50_cs | 0 | D_SUBGROUP_SIZE_32=1 |
| 577d | downsample_depth_vulkan_50_vs (shared by copytexture and others) | 0 | 0 |
| 5806 | downsample_depth_vulkan_50_ps | 0 | 17: D_MINMAX_CHECKER_2X2=1, D_NON_MSAA_DEPTH=1 |
| 5808 | downsample_depth_vulkan_50_ps | 0 | 20: D_MAX=1, D_NON_MSAA_DEPTH=1 |
| 6860 | sky_vulkan_50_vs | 0 | 0 |
| 6861 | sky_vulkan_50_ps | 0/2/4/6 (S_TEXTURE_FORMAT, same code) | 0: D_FOG_TYPE=0 |

So the face renders with Valve's own shader binaries, which the port can run
as the GPU bake already runs vrad3's. Per face (submit), from the list:

1. light binner: `compute_depthbin_cullbits` 256 x 1 x 1, then
   `compute_tile_cullbits` 16 x 32 x 1 (the "two culling dispatches" above);
2. sun shadow, four cascades: `csgo_depth_only` (mode Depth) over the world
   node aggregates, flags `USES_SHADOW_FAST_PATH`; every batch names
   `materials/dev/reflectivity_90.vmat` as its material here, whatever its
   own (to explain);
3. `CLightProbeVolumeObjectDescGpu` and `CDirectionalLightObjectDescGpu`
   batches (an empty rendering pass);
4. depth prepass: `csgo_depth_only` (mode Depth), the aggregates
   `n0_lr0_agg_merge_reflectivity_30_0` and `..._dev_measuregeneric01_0`;
5. `downsample_depth` dynamic 17 (checkerboard min/max) as a 4-vertex
   strip: the 1280 x 720 draw above;
6. colour: `generic` in material mode **ToolsVis** (not Forward) for both
   aggregates, lit from the lightmap; then the sky
   (`toolscene_lighting_sky_dust.vmat`, `CSkyBoxObjectDesc`, mode Forward,
   36 indices); `CWorldTextSceneObjectDesc` batches are empty;
7. two image copies, `downsample_depth` dynamic 20 (max), the readback.

probe01's materials are core dev materials on generic.vfx; why the view
evaluates ToolsVis (the engine runs with -tools, as Hammer's build does) and
which mode a csgo_environment material takes are not read yet. The prop
(trashclusters01, csgo_vertexlitgeneric) draws nowhere in cube 0.

## Open

- DX11 float parity for the GPU pass (only matters where a 1-ulp difference
  survives BC6H; none did on the oracle's input).
- The BC6H encoder is ispc_texcomp.dll from the game's bin folder (as for the
  lightmaps); building it from Intel's source, as bc7e was, removes that
  dependency.
- The face render (the "Game" pipeline for the BuildCubemaps view): captured
  pass by pass and every shader identified (above); the port is not started.
  Next, with RenderDoc replay (CS2 closed): each draw's constant buffers,
  descriptor bindings, indirect arguments and render state, and the view
  constants against client 18020e130's view setup.
