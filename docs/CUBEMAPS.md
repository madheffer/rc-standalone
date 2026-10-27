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

Cascade inputs (ledger 121's open 75): `python tools/pipeline/cs2_closed_session.py
--skip-captures --cascades` (Steam closed too), or directly `run_probe.py --frida
tools/cubehost/cascade_sources.js` then `cascade_summary.py`. The agent logs every
cascade attribute set with its caller, 180cb2100's result for 1808898d0, and what
the cascade setup reads (both scenesystem's and the client's copy); its byte checks
and hooks were tried on the DLLs mapped without initialisation (10 hooks).

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

The step before the EXR is checked too (2026-10-25, ledger 137):
`tools/cubehost/rdc_readback.py` writes the image each `vkCmdCopyImageToBuffer`
of a capture reads back (the 1024 x 1024 RGBA16F colour target, one per face),
and `GameCubemapReadbackTests` (`CUBEREADBACK=<dir>`,
`CUBEREADBACK_EXR=<the game's EXR of that cube from the same run>`) runs the
six through `CubemapFaceExr.Build`. probe01 cube 0 and s2c_l5probe cube 0
equal the game's EXR byte for byte. atixref cube 0 does not: three compact
regions, all last written by one mesh's material (a 12,840-index draw), differ
between the live run and RenderDoc's replay of it, presumably a texture the
live game was still streaming. Two runs without RenderDoc settled it: the
game's own EXRs of atixref vary from run to run everywhere (below).

    RDC_IN=<capture.rdc> RDC_OUT=<dir> qrenderdoc.exe --python tools/cubehost/rdc_readback.py

(The first run of the session sat idle with the capture loaded and never
reached the first copy; killed and started again it finished in about a
minute.)

### The game's build, run to run (2026-10-25, ledger 144)

Repeated `buildcubemaps` runs of one package in cubehost, no RenderDoc, CS2
and Steam closed (`run_probe.py --out <name>` keeps each run apart;
`--from s2c_cubeprobe` reruns the package already in the scratch addon):

- **probe01**: the EXRs depend on texture streaming at the moment of the
  build. Of four runs started the moment the map loaded, the earlier capture's
  run, a first (cold, 104 s) run and both 20 s settled runs (`--settle 20`)
  are bit-identical; one warm run (42 s) is sharper: 6,366 and 1,558 pixels
  differ, only on the edges of high-contrast texture features (the stripes),
  up to 315 half ulps, the signature of a finer mip being resident. The
  settled state is the reproducible one and equals what the captures (and so
  the replays and readback tests) hold. s2c_l5probe behaves the same.
- **atixref**: no two runs agree on any of its 42 cubes, settled or not, and
  pausing the server before the build (`--before "sv_pausable 1" --before
  setpause`) does not help (0 of 42 equal). From 185 to 243,548 pixels per
  cube differ, far beyond rounding: in env_cubemap_245678519 every tree and every surface
  that receives their shadows (foliage moving with time), in env_cubemap_245678535 an object
  at the top of faces 4 and 5 at another place. The scene is time-dependent,
  so a single game build of atixref is no byte reference: its render depends
  on the time and entity state at the build, which no compile from the .vmap
  can know.

Scope (user, 2026-10-25): parity is 1:1 with Valve in every aspect, animated
scenes included; reducing it to static maps is not 1:1 and is not the goal.
So the time-dependent state is part of the port: what the game renders at the
moment of the build (its clocks, entity simulation, animated materials,
texture residency) has to be known and reproduced, and a Valve build has to be
made reproducible (its time pinned) to serve as the byte reference. The
roadmap's items 0a-0e below carry this. After a settled run the engine
sometimes crashes reloading the map (access violation; the EXRs are written by
then).

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

### Replayed on our own device (2026-10-22, ledger 120)

RenderDoc's replay (CS2 closed; 16 s for the whole frame) gives every draw's
state; `rdc_replay.py` writes it as JSON lines, and with `RDC_DUMP=1` also the
contents each action reads and its targets (or written buffers) after it:

    RDC_IN=<capture.rdc> RDC_OUT=<dir> RDC_DUMP=1 RDC_FIRST=140 RDC_LAST=581 qrenderdoc.exe --python tools/cubehost/rdc_replay.py
    python tools/cubehost/rdc_spirv.py <capture.rdc> <spirv dir>
    CUBEREPLAY=<dir> CUBEREPLAY_SPIRV=<spirv dir> dotnet test --filter CubemapFaceReplayTests

(Depth-stencil dumps are a 4-byte depth plane then a 1-byte stencil plane;
3D textures come back a whole mip at a time.) `CubemapFaceReplayTests` runs
each action of face 0 through `SceneDrawRunner` (Source2.Compiler.Gpu): Valve's
SPIR-V, descriptor sets laid out from its reflection, the captured samplers,
vertex layout, raster, depth and stencil state, from the captured targets
before the action. All 30 actions (the binner's two dispatches, four sun
cascades, depth prepass, depth downsample, the colour draws, sky, max
downsample) equal the capture bit for bit. The render state they use is in
ledger 120.

`CUBEREPLAY_EIDS=<eid,...>` runs a subset; `CUBEREPLAY_BLANK=<resource;...>`
uploads those resources as zeros, which shows whether the face depends on
them at all. `CubemapFaceInputsProbe` (with `CUBEREPLAY_VPK`, the captured
map's package, and `CUBEREPLAY_MATERIAL`) traces the captured inputs back to
our data: the world streams to the world node models, the lightmaps and
material textures to their .vtex_c, generic's `_Globals_` to
`ProgramGlobals.Fill` (equal word for word with g_bRenderingToCubemaps = 1
and gameinfo's ShaderIDColors). What is settled and what is left is in
ledger 120.

## The per-view culling (2026-10-25, ledger 139)

Ported in `Cubemaps/AggregateCulling.cs`; `AggregateCullingCaptureTests`
(`CUBECULL=<capture dir>`, with `indirect.jsonl` from
`tools/cubehost/rdc_indirect.py` and `replay/` from rdc_replay.py) checks it
against every face-view draw list of probe01, s2c_l5probe and atixref, all six
faces: 25, 25 and 335 lists, every command equal. How the game gets from a
world node aggregate to a view's indirect draws, all CPU in these captures:

1. **Frustum** (client): the view's world-to-projection (+0x288) gives six
   planes (1817465f0, Gribb-Hartmann, normalised), moved to the camera and
   packed as two SoA blocks of four with sign masks (1817435e0); scenesystem's
   view copies them (+0x430 camera, +0x460 planes).
2. **Fragments** (worldrenderer 18001e920 -> CAggregateSceneObject::AddFragment
   1800380a0): one per AggregateMeshInfo_t; bounds are the draw call's bounds
   through the fragment's transform (18049bfb0); a transform slot is shared
   with the previous fragment while transform (1e-5), packed tint and base
   vertex match. Above 128 fragments the setup (180038650) builds tinybvh
   (c_trav 16, c_int 1) and reorders the fragments into leaf order.
3. **Cull** (18003c5a0 / 18003d380): fragments in (BVH) order, the
   camera-relative box against the eight planes; fragments with a LOD setup
   keep only the LOD 1800450f0 picks (size metric, 50 / (size x 1024), the
   switch distances). The visible order is the instance order.
4. **Layer lists** (180041a00): each layer's list of (fragment, position) in
   visible order. The depth prepass is split by the size metric (18004c390,
   rsqrtss): "Large" keeps size >= 0.6, "Small" min(size, aggregate size) <
   0.6 (r_csgo_depth_prepass_cull_threshold 60 / 100, client 181201670).
5. **Draws** (DrawArray 180042330): per list, a command per entry extended
   over following entries while the transform slot is the same and the first
   index continues the range; aggregates whose fragments all have their own
   transform instead draw consecutive positions of one draw call instanced.
   First instance = the view's instance block + position; blocks are handed
   out per (view, aggregate) by an atomic counter, so their order follows
   thread timing and differs between views of one frame (the test compares up
   to that offset; the instance records' content is the same either way).

The 42 atixref draws that are not aggregate draws (individual scene objects)
go through scenesystem's generic object culling, not this port.

**The sun cascades** (ledger 140) are ported too: every cascade list of the
three captures equals ours (39, 39 and 225 lists; atixref's other atlas tiles
are its shadowed lights' own views, below). Per fit
(scenesystem 180494b40: the three view cascades, then the viewmodel cascade
on its own):

- Each cascade's shadow view (1804a7a60): planes with the zero-to-one near
  plane from projection x (view x translate(camera)), already camera
  relative; the camera is the inverted view's translation (180498c70, the
  client's Gauss-Jordan). That camera also fixed the cascade per-view
  block's camera words (`CubemapCascadeTests` passes now).
- Each view cascade after the first: the previous cascade's box pulled 15%
  in from each x and y side, as six planes with d negated; 18003d380's first
  culling-box set skips a fragment inside all six.
- Every cascade of a fit: the fit's caster volume (180495df0) with d
  negated, the second culling-box set (outside any plane culls): the main
  view's frustum corners (its inverse world-to-projection) and the corners
  pushed toward the sun by the fit's reach; the planes facing away from the
  sun, a cap along the sun at 1.125 x the smallest dot, and a plane along
  the sun through each silhouette edge.
- The sun direction is the lump's light_environment angles as they are
  (SunDirections' ToSun is already the fit's normalised vector; normalising
  again moves its bits).

**Shadowed lights' views** (atixref's one shadowed light_omni2, castshadows 2
with six precomputed sub-frusta, camera at its origin 295 -1746.13 14307):
six atlas tiles below the cascades, (0, 1536), (384, 1536), (1024, 1536),
(1344, 1536), (1664, 1536) and (0, 1280), drawn on faces 0 and 3. Culled with
the frustum of each tile's captured per-view block (camera-relative
world-to-projection, zero-to-one near plane) through the same aggregate cull,
all 96 lists equal the capture: no culling box beyond the frustum. What is not
ported is the view setup itself (the light's six projections from its
sub-frusta, the tile sizes and places in the atlas) and their constant blocks.

## Whole cubes from our blocks (2026-10-26)

`CubemapFaceReplayTests.ChainedCubeMakesTheGamesExr` renders every face of a
cube on our device and checks the result at three levels. Each face's dump is
chained from cleared targets. Every constant block and buffer the face reads
from our data is ours: the world and sky per-view blocks,
PerViewConstantBufferCsgo_t, BakedLightingConstantBuffer_t, the transforms and
instances, the four sun cascades' per-view blocks and the light block. The
checks: every action's output equals the capture, the colour target after the
last draw equals the game's readback, and the six faces through
`CubemapFaceExr` equal the game's EXR byte for byte. The test finds the
blocks by role (`BlockRoles`: binding, the per-view block's depth range, the
shadow-atlas tile), not by face 0's hashes. probe01's +Z face sees no world
and draws the sky alone.

    RDC_IN=<capture.rdc> RDC_OUT=<dir>/replay_face<f> RDC_DUMP=1 RDC_FIRST=<eid> RDC_LAST=<eid> qrenderdoc.exe --python tools/cubehost/rdc_replay.py
    CUBEFACES=<dir> CUBEREPLAY_SPIRV=<spirv> CUBEREADBACK=<readback dir> CUBEREADBACK_EXR=<game EXR> [CUBEREPLAY_CUBE=1] [CUBEREPLAY_EXR_OUT=<dir>] dotnet test --filter ChainedCubeMakesTheGamesExr

(A face's events run from the event after the previous face's
`vkCmdCopyImageToBuffer` to its own, from rdc_summary.py's list. A face takes
700 to 900 MB of dumps. The six faces replay in about 70 s on the RTX 4070.)

- **probe01, both cubes** (cube 0 from the 10-21 capture, faces 1 to 5
  dumped 10-26; cube 1 captured 10-26 with `run_probe.py --renderdoc 1
  --settle 20`): all twelve faces equal their readbacks, both EXRs are the
  game's byte for byte, and `CubemapArrayVtex.Build` on our two EXRs gives
  the game's whole `env_cubemap_array.vtex_c` (945,838 bytes; GameCubemapTests
  on a folder of our EXRs with the game's .vtex, `CUBEREPLAY_EXR_OUT`).
- The light block lists each face's visible light probe volumes
  (g_VisibleLPV, 96 bytes each; g_nNumLPVs at +31960): those whose box meets
  the face's frustum, volume 1 before volume 0, and each entry's last int is
  the volume's array_index. The test checks these words against the capture,
  since an unreached volume changes no pixel. Measured, not read
  (GROUND_TRUTH 147).
- Still the capture's: the pipelines and draw arguments (our culling gives
  the same index ranges, ledger 139), the geometry, lightmap and material
  contents (equal to the package's, ledger 120). Since 10-26 (ledger 149)
  generic's `_Globals_` of every world colour draw is ours: the draw's vertex
  stream is a world node model's buffer, its first index names the draw call
  and so the material, the material's generic colour program is the draw's
  SPIR-V module, and ProgramGlobals.Fill gives the block word for word on all
  three cubes. Materials whose blocks are the same bytes share one captured
  block.
- The light binner's CullParams_t, the sky's g_matSkyWsToOs and pixel
  $Globals, and both depth downsamples' $Globals are ours too since 10-26
  (ledger 148), checked word for word against every face's capture: the
  binner from lb_tile_pixels / lb_bin_slices and the visible item counts, the
  sky from env_sky's rotation and material, the downsamples from the
  HalfResDepth (r_csgo_mixed_resolution_particles_scale, 2) and
  QuarterResDepth (4) layers' g_nDownsampleRatio through the programs' own
  expressions.

### A second map: s2c_l5probe (2026-10-26, ledger 149)

s2c_l5probe's cube 0 (the 10-22 capture, faces 1 to 5 dumped 10-26) gives
all six readbacks and the game's EXR byte for byte through the same test, with
every block ours, and probe01's two cubes still do. What it took:

- **No scripted camera.** Cascade 0's 75 comes only while 180cb2100 finds a
  scripted camera (Panorama's StartCamera, end of match or team select), and
  that is UI state, not the map. In that build the player had spawned, so the
  cascades are the plain split's: `CUBEREPLAY_SCRIPTEDCAMERA=0`,
  `CubemapShadowCascades.GameDistances(..., scriptedCamera: false)`.
- **Pass clears.** `tools/cubehost/rdc_passes.py` writes each capture's
  render passes (`passes.jsonl` beside the face dumps: attachments, load ops,
  clear values, and each view's image and format). The chained replay clears
  each CLEAR attachment over the pass's render area when the pass begins.
  The AO target is cleared to white over 512 x 512.
- **The AO target's UNORM view.** The pass renders the sRGB AO target through
  an R8G8B8A8_UNORM view, and the colour pass samples it that way. The pawn's
  AO box multiplies into it, which reproduces only through that view. The
  replay now models colour blending (SceneDrawState.Blend) and mutable 8-bit
  views (SceneDrawRunner.ViewAs).
- **The viewmodel.** The spawned pawn's first-person viewmodel is drawn in the
  prepass and colour pass with its own per-view block (depth range 0 .. 0.1),
  scene blocks and light block (the viewmodel cascade alone). BlockRoles keeps
  the blocks only those draws read from the capture. Its models, skinning and
  blocks are not ported.
- **Visible volumes' order** from the decompile
  (`CubemapFaceConstants.VisibleVolumes`): priority (indoor_outdoor_level)
  descending, then the local box's volume ascending.
- The barn and omni lights are not binned in the cubemap view (the binner block
  with no lights equals the capture on every face).

Not done for this map: its cube 1 has no capture, so its whole array is not
checked; that needs `run_probe.py --renderdoc 1` with CS2 and Steam closed.

    RDC_IN=<capture.rdc> RDC_OUT=<capture dir>/passes.jsonl qrenderdoc.exe --python tools/cubehost/rdc_passes.py

### The buffers (2026-10-26, ledger 152)

The world, sky and downsample draws now read our buffers. The world draws use
the package models' vertex and index buffers, the latter in the pool at the
capture's offsets. The instance id ramp and the zero stream are ours. The sky
cube's mesh is scenesystem's (`CubemapFaceMeshes`), and the downsample quad's
positions are ours. Each range a draw reads is checked against the capture.

### The binner's items (2026-10-26, ledger 151)

The light binner's cull dispatches read two buffers the CPU uploads for each
view: one 80-byte record per item, and the edge lines of each item's screen
footprint. `Cubemaps.LightBinnerItems` builds both for light probe volumes
from scenesystem's code: the box's eye-outside mask, its silhouette projected
or clipped, the half-tile Minkowski sum, edges by rsqrtss. Every word equals
the capture on all eighteen faces (`LightBinnerItemsTests`, `CUBEBIN=<dir>@<cube>;...`).
The whole-cube test now uploads ours for both buffers, with zeros past them.

### The pipelines (2026-10-26, ledger 150)

`CubemapFacePipelines.Apply` gives a draw's fixed-function state from its
pass and its pixel program's .vcs render state (`ValveProgram.RenderState`),
plus the engine's part. That part is depth bias on with front faces
counter-clockwise, and the depth-only passes' state. The sun cascades get the
2.13 slope bias of `sc_shadow_slopescale_depth_bias`. The colour pass gets its
stencil test, and the quarter downsample its stencil replace. The viewport
depth ranges come from the pass. The whole-cube test draws every world, sky
and downsample draw with that state and checks each field against the
capture. Since ledger 153 the samplers are ours too (`CubemapFaceSamplers`:
each slot's state variables from the write sequence, set 1 binding 14 +
slot), and since ledger 154 the vertex layouts (`CubemapFaceVertexLayout`:
each input's semantic from the shader file's attribute map, fed from the
mesh, the instance ids or the zero stream). Still taken from the capture: the
viewport and scissor rectangles, the draw list and its arguments, and the
state of the draws whose programs are not ported (the viewmodel, the pawn's
AO box).

### The textures (2026-10-26, ledger 150)

Every texture the world colour and sky draws read now comes from our data.
Each binding's variable is looked up in the draw's program: set 1, binding 30
plus its slot in the combo's write sequence.
- The lightmap attributes (direct_light_shadows, irradiance,
  directional_irradiance, debug_chart_color) are the package's
  `lightmaps/*.vtex_c`.
- g_tColor and g_tNormal come from the material's .vtex_c, and the sky's
  g_tSkyTexture from its material.
- SSAOOcclusionFactor and ParticleShadowBuffer are a 1 x 1 white.
- CubemapFogTexture is a 1 x 1 cube of zeros.
- LightCookieTexture and EnvironmentMap are bound as zeros. No light or
  environment map is binned in the cubemap view, so no pixel reads them
  (s2c_l5probe's capture holds stale contents there and its face still
  matches).

Every subresource we know is checked against the capture's. The shadow atlas
and the AO targets are the face's own live targets. probe01's faces now draw
every draw with our state and textures.

The 10-23 update (1.41.9.2) left the settled build unchanged. probe01's two
EXRs from the 10-26 run equal the 10-21 run's bit for bit. The compiled array
shrank from 945,886 to 945,838 bytes, and `CubemapArrayVtex.Build` already
gives the new file byte for byte (`GameCubemapTests` on the 10-26
intermediates). cubehost's RenderDoc convar moved to client 1823806d8 in that
build; cubehost now picks the address by client.dll's PE time stamp.

## The draw list (2026-10-27, ledger 156)

`Cubemaps/CubemapFaceDrawList.cs` gives a face's world aggregate draws from our
data: the views (the face and its four cascades, `Views`, moved out of the
culling test), one list per aggregate and layer (Shadow per cascade, then the
face's PrepassLarge, PrepassSmall and Colour), each list's commands from
`AggregateCulling`, the instance blocks and each view's viewport and scissor
(the face's 1024 square, the cascade's atlas tile). The cascade tiles at shadow
quality 2 are measured (`CascadeTiles`); the atlas allocator is not read.

- `AggregateCullingCaptureTests.DrawListEqualsTheCapture` (`CUBECULL=<capture
  dir>`): every captured aggregate call is one of our lists and every list of
  ours is a captured call, in our pass order with our viewports. All six faces
  of probe01 and s2c_l5probe pass. atixref does not yet: its non-shadow-casting
  materials (the grass blends, aggregates 7, 54, 81, 84) get Shadow lists the
  game does not draw, the alpha-tested cypress (110) is drawn in its own prepass
  layer after Small, and aggregates 89 and 91 have no prepass. Which layers a
  draw joins is 180041a00's test of the draw's flags (+0x14 of the 0x50-byte
  record at the object's +0x168) against the layer's (+0x480, +0x488, +0x498);
  the draw flags come from the material and are not read yet.
- `ChainedCubeMakesTheGamesExr` with `CUBEREPLAY_OURDRAWS=1|reverse|<k,...>`
  replaces the capture's aggregate draws by ours: each pass's group of
  aggregate draws becomes our lists in our order, each command with our first
  instance, viewport and scissor; a captured draw of the same aggregate and
  command only lends its bindings. Every captured draw must be a command of
  ours and the reverse.
- With the aggregates in reverse order (reflectivity_30 before
  dev_measuregeneric01, as the game drew them) probe01's two cubes and
  s2c_l5probe's cube 0 give all eighteen readbacks and the game's three EXRs
  byte for byte. In aggregate order four faces of each cube differ (34 to 324
  halves): coplanar surfaces of the two materials both pass the colour pass's
  depth test and the later draw wins. The shadow and prepass depth converge
  either way.
- **The order within a layer** (GROUND_TRUTH 156): 1800ed4c0 sorts a layer's
  CMeshDrawPrimitive_t (0x70 bytes): the full sort (18010f4e0, layer flag 1)
  compares +0x58 first, the batch sort (1801112a0) does not; then both compare
  the object's +0x18, two +0x62 bits, the draw's flag 6, the per-object
  material (+0x28), +0x62 & 0x1344, +0x48, the material (+0x20), then
  +0x62 & 0xecbb, the draw's +100 and +0x58. `tools/cubehost/layer_sort.js`
  (run_probe.py --frida) logs every sorted layer's keys. In probe01's colour
  layer the two aggregates tie on every key up to the material's address
  (0x27e56302800 before 0x27f09fec000), so Valve's order is the materials'
  heap order. Two atixref runs (`--out atixref_sort1`, `atixref_sort2`) sort
  the same draws in different orders in most sorts: the order is not
  reproducible in Valve's own build, and on coplanar pixels of two materials
  neither is Valve's face. On the two small maps it holds: eight more runs
  (`probe01_order1..5`, `l5probe_order1..3`) all draw reflectivity_30 first.
  The cause (GROUND_TRUTH 156): the address follows the CMaterial2's creation
  order (95 to 96%), and reflectivity_30 is one of the 957 materials the engine
  creates before any map's own (`Resources/cubemap_engine_materials.txt`, made
  by `tools/cubehost/material_order.py` from the agent's "material" records),
  which sort before all others. The map's own are created as their loads
  complete, which is not data. `CubemapFaceDrawList.MaterialOrder` ports
  that: engine materials first in creation order, then the map's own by path.
  All three cubes are the game's with it (`CUBEREPLAY_OURDRAWS=materials`).
  (A run with the agent on atixref takes over 600 s: pass `--timeout 1500`.)
- **Does the order reach the output** (measured): `DrawOrderChangesTheFace`
  (`CUBEREPLAY=<face dump>`, `CUBEREPLAY_ORDERTEST=1`) replays a face with
  each run of indirect calls reversed. atixref face 0: no depth or shadow
  texel changes, 346 of 4,194,304 colour words do. Two Valve builds of
  ze_hold_em_p order the same draws differently and give all 8 EXRs byte for
  byte equal. Only coplanar surfaces of two materials depend on it.

### What the chained cube still takes from the dump (2026-10-27)

`CUBEREPLAY_INVENTORY=1` makes `ChainedCubeMakesTheGamesExr` list every input it binds from the dump's blobs
instead of our data (buffers, image subresources, push constants, samplers, dispatch sizes). On probe01's cube 0
(with `CUBEREPLAY_OURDRAWS=materials`, all six faces still the game's EXR) only two remain per face:

- the colour pass's storage buffer at set 3 binding 31 (153,600 bytes). Since 10-27 (ledger 162) generic's write
  sequence names it: g_BarnLights (slot 1; g_CullBits is slot 0, binding 30). No barn light is binned in the
  cubemap view, so no pixel reads it;
- the light binner's two dispatch sizes (256 x 1 x 1 and 16 x 32 x 1). Since 10-27 from the code (ledger 162):
  1801b2850 dispatches thread counts and rendersystemvulkan 180072850 divides them by the program's workgroup.

Beyond these inputs the replay still uses the dump as scaffolding: each action's record (which program, which
resource name at each set and binding), `passes.jsonl` for the passes and their clears, and `resources.json` for
the targets' formats and sizes. Rendering with no dump means building that frame from our data: the action list
(CubemapFaceDrawList plus the binner, sky and downsample passes), each program's bindings from its SPIR-V
reflection and the programs' variable tables (as the textures and samplers already are), and the targets.

## A face with no dump (2026-10-27, ledger 162)

`Source2.Compiler.Gpu/CubemapFaceRenderer.cs` renders a face from our data alone: the map's package (world nodes,
models, lightmaps), the game's shader and content packages, the face's blocks (`Cubemaps/CubemapFaceBlocks.cs`, the
roles the replay found, moved out of the test) and the draw list (`CubemapFaceDrawList.Face` with the layer
memberships and `MaterialOrder`). No file of a RenderDoc dump is read.

- **The frame.** As every face of the three captures runs it: the atlas cleared to depth 1, the binner's two
  dispatches, the four cascades' lists into their tiles, the face cleared (colour 0, depth 1, stencil 0), the
  prepass, the half depth (cleared over 512 x 512, then downsample_depth's checkerboard min/max), the AO target
  cleared to white over 512 x 512, the colour lists, the sky, the face's stencil cleared, the quarter depth. The
  two image copies after the sky do not reach the readback and are not modelled.
- **Bindings from Valve's tables.** Each program's SPIR-V bindings are named by its combo's write sequence: a
  constant buffer at its slot, a texture or structured buffer at 30 + slot, a UAV at 158 + slot, a sampler at
  14 + slot, $Globals at binding 0 of the stage's set. Each name then picks one of our inputs
  (PerViewConstantBuffer_t, the per-pass view block; TransformBuffer, InstanceBuffer, CullBits, BarnLights,
  CullItems, CullPlanes, OutputCullBits; the texture attributes as in "The textures"). An unnamed binding throws.
  `CubemapFaceBindingsProbe` (`CUBEBINDINGS=1`) lists each program's bindings beside its sequence.
- **The binner's dispatches** from the code: thread counts (bins, mask words, 1) and (tiles x, tiles y, mask
  words), divided by the program's workgroup (32 x 1 x 1, 8 x 4 x 1).
- **Programs.** generic's colour combo per material (S_MODE_TOOLS_VIS, lightmapped, the material's features),
  csgo_depth_only, sky, downsample_depth and the two binner programs; D_COMPRESSED_NORMALS_AND_TANGENTS by the draw
  call's m_bUseCompressedNormalTangent. With `CUBEREPLAY_SPIRV` the test checks every program is a module of the
  capture.
- **Result.** probe01's two cubes: twelve faces equal the readbacks, both EXRs and the whole
  env_cubemap_array.vtex_c are the game's byte for byte, about 7 s a cube on the RTX 4070. s2c_l5probe's cube 0:
  five faces exact, -X differs where the pawn's viewmodel draws (not ported).

    CUBENODUMP_VPK=<package> CUBENODUMP_CUBE=<n> CUBEREADBACK=<readback dir> CUBEREADBACK_EXR=<game EXR> [CUBEREPLAY_SPIRV=<modules>] [CUBEREPLAY_EXR_OUT=<dir>] [CUBEREPLAY_SCRIPTEDCAMERA=0] dotnet test --filter CubeWithNoDumpMakesTheGamesExr

The readbacks, EXRs and modules are only the comparison.

Later 10-27 (ledger 163): the scene comes from the package (`CubemapFaceBlocks.FromPackage`: the cube records in
client 1801ea0a0's order with their handshakes, the volumes, fog, sun, sky, lightmap uv scale and gameinfo's
MinSpecLightmapSize), and each world draw's programs from its shader's render modes (`CubemapFaceRenderer.Programs`:
Depth for the cascades and prepass, ToolsVis for colour, no pixel program where the combo has no shader). probe01
stays exact. `CubemapFaceProgramsTests` (`CUBEPROGS=<capture dir>`) checks the choice on every world draw of a
capture: atixref face 0, 72 of 189 equal; what the rest need is ledger 163's three rules (the depth vertex stream
and its compressed-normals combo, probe lighting for prop aggregates, foliage's alpha depth combo).

Left for atixref, in order: those three; prop aggregates' per-mesh transforms, tints and light probe volume in the
instance records (m_bHasTransform, m_vTintColor, m_nLightProbeVolumePrecomputedHandshake; the captured records give
the layout); two-stream vertex layouts; the probe-lit colour programs' inputs (the probe atlas textures); the sky with
D_FOG_TYPE 1; non-aggregate scene objects (about 50 direct draws in face 0, among them error-shader draws in the
atlas); the viewmodel (s2c_l5probe's -X). Diagnosis probes: `CubemapSceneKeysProbe` (`CUBEKEYS`),
`CubemapModesProbe` (`CUBEMODES=1`), `CubemapDrawCallKeysProbe` (`CUBEDCKEYS`).

## Roadmap to env_cubemap_array.vtex_c from the .vmap (2026-10-25)

The goal is 1:1 with Valve in every aspect: every map, static or animated,
every compile option, byte for byte against the game's own build of the same
package. Nothing is limited to static scenes.

Done: the array's compile (ledger 117), readback -> EXR (137), face views,
constant blocks and the chained face 0 (118-121), the per-view culling of
world aggregates for faces and sun cascades (139, 140), the shadowed light's
views given their frusta (144), and the finding that the game's build varies
run to run (144). In order:

**0. The state at the moment of the build** (ledger 144: streaming and time
change the game's output). Without this no animated map has a reference.

- **0a. What decides the state.** From client.dll's BuildCubemaps
  (1801ea0a0 and its callers) and the engine's frame loop: whether the build
  stops or advances simulation and render time between faces and cubes, which
  clocks the render reads (the per-view block's time words, the client's
  curtime and frametime, the material system's time, the server tick), and
  what changes with them: moving brush entities (func_door, func_movelinear,
  func_rotating, func_tracktrain and the rest), animated models and their
  sequences, foliage wind and other vertex animation in the shaders, texture
  scroll and animated textures, light styles (atixref's eight fast_strobe
  lights), particles, sky and fog animation, physics props settling (Valve's
  own settle varies by heap order, docs/PHYSICS.md). Check: each candidate's
  words in the captured per-view and material blocks against two runs'
  captures; a Frida trace of the clock reads inside cubehost (function
  entries only, as the 10-22 lesson says).
- **0b. A reproducible Valve build.** Pin what 0a finds, in cubehost only: a
  fixed time base for the build (a hook of the time source, or the engine's
  own convars if one freezes it), a fixed point of entity simulation, the
  settle for streaming. Check: two runs of atixref bit-identical, all 42
  cubes. That pinned run is the byte reference for animated maps; probe01's
  settled run already is one for static maps.
- **0c. Texture residency.** Which mips a settled build has resident per
  texture (the streaming rule: the 512 preload rule seen for the blend
  sampler, r_texture_stream_max_resolution and the screen-size request), and
  what an unsettled build has (the warm run's finer mips). Check: our
  residency gives the settled EXRs of probe01, s2c_l5probe and atixref.
- **0d. The time-dependent inputs ported.** Each thing 0a lists, computed for
  a given time as the game computes it: entity transforms at that time (from
  the entity lump and our entity and physics ports), material time constants
  and vertex animation inputs, light style values, animated model poses.
  Check: the pinned run's per-view, material and instance blocks word for
  word; then its EXRs.
- **0e. The time our compile renders at.** What Hammer's and the game's build
  have at the build (map load to buildcubemaps), chosen so our output equals
  a pinned Valve build of the same package; and for Valve's shipped maps,
  whether their arrays' time can be recovered (render at candidate times and
  compare) so they serve as references too.

**1. Shadowed lights' own shadow views** (atixref's six tiles, 96 draws per
capture): the culling is settled (the frustum alone, all 96 lists equal);
left are the view setup from the light's precomputed sub-frusta, the atlas
allocator (tile sizes and places, priority, shadowmapsize -1), their constant
blocks, and spot, barn and rect lights' shadow views (none captured yet).
Check: `AggregateCullingCaptureTests` with our frusta instead of the captured
blocks; the face's replay with our blocks.

**2. Non-aggregate scene objects** (atixref's 98 draws: static props kept as
their own objects, entity models, the sky): scenesystem's generic object
culling, their instance records and draw lists, animated models' bones.
Check: the same test with those draws matched to package models.

**3. The remaining cull gates**: LOD cross-fades and forced LODs (1800450f0's
other paths), the per-view PVS test (FUN_180036840), the small-object fade
(view +0x5724 / +0x572c), culling boxes on face views. Each ported or shown
inactive for the BuildCubemaps view (a Frida read of the view fields, CS2 and
Steam closed).

**4. Layer and primitive order**: which layer lists a material lands in, and
the order of the primitives within a pass (the 16-bit sort key from
180041a00's +0x20, the pipeline sort). Check: the captured action order of
every pass, all three maps.

**5. The light binner from our own lights** (s2c_l5probe's omni and barn
lights; dynamic lights and light styles) and the bindless material set
(atixref): descriptor heap and texture slots from our materials. Check: the
replay with nothing taken from the capture but geometry buffers.

**6. atixref draw 2305**: one half-float ulp on 38 pixels; build pipelines
from stage libraries (VK_EXT_graphics_pipeline_library) as the game does.

**7. All six faces chained**, every cube of probe01, s2c_l5probe and atixref,
from cleared targets on our data. Check: each face against
`tools/cubehost/rdc_readback.py`'s readbacks bit for bit (GPU on the 4070;
the dev box GTX 1080 drifts on two draws). Needs face 1..5 dumps
(rdc_replay.py per face range, CS2 closed), of a pinned run (0b) for atixref.

**8. The cube set and the strip**: cubemap records as client 1801ea0a0
gathers them (class order, near 1, far 10000, handshakes, cube array index),
each cube rendered, reduced and written (`CubemapFaceExr`), then
`CubemapArrayVtex.Build`. Check: the whole env_cubemap_array.vtex_c against
the game's build of the same package (settled, and pinned where animated),
all three maps.

**9. compile-map and the service**: render the cubemaps in our own Vulkan
device after the lit compile (Hammer's "build cubemaps" equivalent) at 0e's
time, and write the array and the .vtex into the package. Check:
ParityReport's cubemaps row at 100% on the parity matrix maps, every preset
and switch.

**10. Every pass and map feature**: a ze map and a workshop map with props,
decals, overlays, water and translucency in view (passes the three captures
do not exercise: MBOIT, water refraction, decals, sky variants, fog volumes,
env_cubemap_box and parallax volumes, env_cubemap_fog), each with a pinned
capture; video config dependence (shadow quality presets, ledger 121) as the
build sees it; the CPU-vendor dependent rsqrtss in the size metric and an AMD
GPU reference.

**11. Independence**: the BC6H encoder from Intel's source instead of the
game's ispc_texcomp.dll; DX11 float parity of the blur where a reference
needs it.

## Open

- DX11 float parity for the GPU pass (only matters where a 1-ulp difference
  survives BC6H; none did on the oracle's input).
- The BC6H encoder is ispc_texcomp.dll from the game's bin folder (as for the
  lightmaps); building it from Intel's source, as bc7e was, removes that
  dependency.
- The face render (the "Game" pipeline for the BuildCubemaps view): every
  action of face 0 reproduced on our device, and every constant block and
  buffer it reads built from our data (CubemapFaceViews, CubemapFaceConstants,
  CubemapShadowCascades; ledgers 120, 121) with the face unchanged, and the
  face chained from cleared targets on our blocks
  (`ChainedFaceEqualsTheCapture`: bit for bit against the capture on the
  RTX 4070, 10-22). The vertex, index, lightmap and
  material inputs already equal the package's data (ledger 120). The
  readback after the colour pass makes the game's EXR byte for byte (ledger
  137). The face views' draw arguments come from our culling now (ledger
  139), and the sun cascades' (ledger 140). All faces of probe01's two cubes
  are chained and reduced to the game's EXRs and array (2026-10-26, "Whole
  cubes from our blocks"). Next: the draws without the capture.
- A second map, s2c_l5probe (omni2 and barn lights; capture and dumps in
  %TEMP%/cubehost_run/s2c_l5probe), replays face 0 bit for bit (38 actions,
  push constants for the light binner). Its frame also draws 16 animatable
  models per face (presumably the spawned player): none reaches face 0's
  pixels, and neither cube's game EXR shows any model (10-22).
- atixref (42 cubes, world-only build) is captured too (%TEMP%/cubehost_run/atixref,
  face 0 dumped, 7 GB): 335 of its face 0's 336 actions replay bit for bit; draw
  2305 is one half-float ulp off on 38 pixels, presumably the pipeline build
  (the game links stage libraries; ledger 121). The RenderDoc tools for
  comparing a draw on both sides are in tools/cubehost (rdc_postvs.py,
  rdc_debugpixel.py, rdc_pixelhistory.py, rdc_bindings.py, rdc_events.py).
- The per-view culling: done for the face views and the sun cascades
  (above, ledgers 139 and 140); shadowed lights' own views are not compared.
- The cascade inputs depend on the game's video settings, not the map: the
  shadow-quality preset (the run detects level 2) sets
  csm_max_shadow_dist_override 560, 3 cascades and 1280 tiles
  (`CubemapShadowCascades.Inputs`, ledger 121). Cascade 0's 75 is set by the
  render pipeline on every game view, the cubemap view included (captured 10-22).
