# vrad3

vrad3 is the lighting baker. resourcecompiler writes its inputs and a script
into `game/csgo_addons/<addon>/_vrad3/`, then runs `vrad3.exe`, a small
launcher for `vrad3.dll`. This file records what the DLL does, read from the
20261001 build. Addresses are in [ADDRESSES.md](ADDRESSES.md) (module
`s2c:baked/vrad3`). The port plan is in [BAKED.md](BAKED.md).

## Method

The DLL is 3.2 MB: about 890 functions of vrad3's own code (0x180001000 to
0x180090000), and statically linked libraries:
- tier1, datamodel (DMX) and a .vmap reader;
- floatbitmap with bc7e, ISPC texture compression and etc2comp;
- tinyexr, libpng, libtiff, zlib and a PSD reader;
- the MSVC runtime and the Concurrency runtime.

The tools, all offline:
- `tools/re/vtables.py` lists every RTTI class and its vtable slots.
- `tools/re/xrefs.py` indexes each function's calls, data references and
  strings, with callers and referers.
- `tools/re/vrad3_structure.py` writes the rows the structure alone gives:
  each command's Run and Register, and each lambda job's body.
- `tools/re/batch_decompile.py` dumps every decompile through ReVa.
- `tools/re/harvest_names.py` and `dump_asserts.py` name from asserts. vrad3
  has only 18 asserts, so most names come from structure and reading.

## Startup

`Vrad3_LauncherMain` sets up the command line and minidumps, then
`Vrad3_Main`:
1. Prints the banner and build string ("pc64 Sep 30 2026", "SIMD: 4x fp32").
2. Connects the app systems: scenesystem (SceneSystem_002, SceneUtils_001)
   and vrad3 (Vrad3_001).
3. Mounts `VRAD3_IN_PATH` and `VRAD3_OUT_PATH` and starts the thread pool.
4. Makes every `-name=value` on the command line a script variable `$name`.
5. Runs the file given by `-script` through `Vrad3_ExecuteScript`, then prints
   "Total Time".

Command-line switches are read by hash: MurmurHash2 of the lowercase name with
seed 0x31415926, the same as Hammer. For example, `-script` is 0x254f4cad and
`-gpuraytracing` is 0xd25b7cdf.

## The script

A script is a list of commands, one per line, with `//` comments. Labels
(`name:` ... `done`) are routines, which `run <label>` calls. `$name`
substitutes a variable; an unknown variable or routine exits with code 1.
`Vrad3_RunCommand` looks up the first token in the command table and calls
`Run(argc, argv)`, where argv[0] is the keyword. An unknown keyword lists the
known commands and exits with code 1.

The command table is a `CUtlHashtable<CUtlString, CVrad3Command*>`, keyed by
FNV-1a 32 of the name and then h = (h << 17 ^ h) + (h >> 21). Each command is
a static `CVrad3Command_<keyword>` object. Its constructor
(`CVrad3Command(const char *name)`, run from a dynamic initializer) inserts it
into the table. Its vtable has a single slot: `void Run(int argc, const char
**argv)`.

There are 104 commands. resourcecompiler's script (an example is
`csgo_addons/s2c_rc_probe/_vrad3/script-gpu.vrad3`) runs:
1. `gpu_init`, `lightmap_type ahd`, `sceneinfo_gpu scene_info.kv3`;
2. `lightmap_image_gpu 4096 4096 1024`, `lightmap_load_packing_geometry_gpu`;
3. per block: `lightmap_load_block_gpu lightmap_info.dat N` and
   `lightmap_compute_block_gpu 16 16 0.200`;
4. the post-process: `lightmap_image_consolidate_small_charts 1`,
   `lightmap_image_remove_fireflies 4.0`, `lightmap_image_filter_median 7`,
   `lightmap_image_dilate_over_invalid_jfa`, `lightmap_image_encode_ahd`,
   `lightmap_image_fill_gutters 1`, `uber_weld lightmap_seams.dat`, then the
   writes;
5. per light probe volume: `lpv_cells`, `lpv_box`, `lpv_local_to_world`,
   `lpv_init`, `lpv_image_gpu 96 96 96`, `lpv_voxelize_gpu`, `lpv_flood_fill`,
   `lpv_compute_block_gpu 0 0 0 X Y Z 64 64 0.200`,
   `lpv_image_ambient_occlusion`, `lpv_image_remove_fireflies 3`,
   `lpv_image_resolve`, `lpv_image_median_filter 3`, then the writes;
6. `exit 0`, which skips the cleanup.

## Commands read so far

- `gpu_init`: needs a render device (`-vulkan`) with ray tracing (device vf
  0x620; `-gpuraytracing`). It loads the resource manifests `VRAD3` and
  `ToolsStartup_RayTracing`. It runs once.
- `lightmap_type sh2|ahd`: sh2 is 1, ahd is 0 (DAT_1803ab578).
- `lightmap_image_gpu <w> <h> <block>`: initialises the global
  `CVrad3LightmapImage` (DAT_180399e70) to w x h, sets the block size, and
  creates the block-sized render target `lightmap_rasterize_rt_depth`.
- `sceneinfo_gpu <scene_info.kv3>`: loads the scene description from
  `VRAD3_IN_PATH`. It reads `mesh_file`, then that file's `meshes`;
  `instances` (mesh, material, flags); `ibl` (path_trace_light_flags,
  transform, scale, material, sky_bounce, sky_bounce_scale,
  sun_light_min_brightness); `lights` (barn and luminaire records,
  path_trace_light_flags, baked_shadow_index, baked_shadow_light_id); and
  `cameras` (angles). It then builds the scenesystem world
  `vrad3_raytrace_scene_world`.
- `lightmap_load_packing_geometry_gpu <file>`: `PackingGeometry_t::Unserialize`
  reads the file into the global packing geometry. Each array is a u32 count
  followed by its data, read in chunks of at most 16 MB. The layout parses
  probe01's file exactly to its end:

  | field | content |
  |---|---|
  | u32 n, n x u32 | an array, empty on probe01 |
  | u32 stride, u32 count, count x stride | vertices: position, normal, tangent and sign (10 floats, stride 40) |
  | count x 8 | each vertex's atlas uv (float2, 0 to 1) |
  | u32 C, C x u32 | triangle indices |
  | u32 D, D x 28 | charts |
  | C / 3 x u32 | each triangle's chart |
  | 6 x u32 | vertex attribute offsets: position (0), unread, normal (12), tangent (24), two unread (0, 0) |

  A chart record is seven u32s: ARGB debug colour, index, x, y, w, h and a
  flags word. The command uploads a ChartInfo buffer of (record +0,
  flags & 0x0fffffff) per chart, and the vertex layout
  `PackingGeometryVertexLayout`.
- `lightmap_load_block_gpu <lightmap_info.dat> <block>`: block b covers
  luxels (b mod bx, b div bx) x blocksize. The block's chart IDs (one u32 per
  luxel, 0xFFFFFFFF for none) sit in `lightmap_info.dat` at
  ((bx_index * bs) + by_index * W) * bs * 4. They go into the image's 12-byte
  luxel records: the ID, then a flags byte (bit 0 a chart covers the luxel,
  bit 2 the chart's flags bit 28, bit 1 its bit 29), and into the GPU
  texture `LuxelChartIDs.vtex`. The triangles whose atlas-uv bounds meet the
  block are clipped to it on the CPU and uploaded as
  `PackingGeometryVertices`: a 24-byte vertex of position, atlas uv and
  `PackTangentFrame`'s u32. They are uploaded with `TriangleArea` per
  triangle and `ChartIDs`.
- `lightmap_compute_block_gpu <minPasses> <passes> [tolerance]`: `passes`
  defaults to 256 and the tolerance to 0.02. resourcecompiler passes
  `16 16 0.200`. Passes run in command lists of 16. In pass p:
  - when p mod 4 is 0, the samples are made again. Small charts get a
    compute pass that samples triangles by area. Large charts get a raster
    pass whose `g_vWindow` is jittered by a random offset in [-0.5, 0.5)
    luxels. A ray-traced validation pass fixes samples inside geometry.
  - when p >= minPasses, the first pass of a command list runs the
    convergence test against the tolerance;
  - every pass path-traces once per luxel, accumulating into `g_Luxels`.

  The five stages are timed and logged as Sampling Small Charts, Sampling
  Large Charts, Sample Validation and Fixup, Convergence Testing, and Path
  Tracing. Afterwards `g_Luxels` is read back into the image's channels
  (`CVrad3LightmapImage::SetPixelRGB` / `RGBA`).

## GPU programs

The bake's GPU work runs through materials in core's pak01
(`materials/dev/vrad3/*.vmat`). Their shaders are in
`core/shaders_vulkan_dir.vpk`: compute and raster ones under their own
names, the ray tracing ones as `rtx_vrad3_*_vulkan_60_rtx.vcs`. The SPIR-V
decompiles back to GLSL with Valve's names intact (they were written in
Slang). `ShaderDumpProbe` (`SHADERDUMP=<shader vpk>|<name part>|<out dir>`)
writes each one's variables and every static combo's shader files.

| stage | material | shader |
|---|---|---|
| Sampling Small Charts | lightmap_generate_small_chart_samples | compute |
| Sampling Large Charts | lightmap_rasterize | vertex, geometry, pixel |
| Sample Validation and Fixup | lightmap_sample_validation | rtx_vrad3_lightmap_sample_validation |
| Convergence Testing | lightmap_test_converged | compute |
| Path Tracing | lightmap_raytrace | rtx_vrad3_lightmap (ray generation, 4,500 lines) |

The probes, far field and vertex lighting have their own:
`rtx_vrad3_lpv`, `lpv_test_converged`, `rtx_vrad3_mark_occupied`,
`rtx_vrad3_farfield` and `_metrics`, `rtx_vrad3_vertexlight`,
`lightmap_vertexlight_reduce`. `rtx_pathtracer` is the editor path tracer.

The host binds shader parameters by name hash, the same MurmurHash2 as the
command line. Every hash in the block compute is cracked from the shaders'
names: g_Vertices, g_nVertexStride (24), g_nLightmapUVOffsetInBytes (12),
g_nPackedNormalTangentOffsetInBytes (20), g_TriangleListForThreadGroup,
g_TriangleArea, g_nRNGOffsetX/Y, g_nLightmapBlockRectX/Y/Width/Height,
g_nLightmapWidth/Height, g_LightmapSamples, g_LuxelChartIDs, g_ChartInfo,
g_vWindow, g_ChartIDs, g_nSampleStride, D_PASS, g_nBlockWidth/Height,
g_flErrorTolerance, g_BlockConverged and g_Luxels. Not cracked: 0x932bfc39,
a raster combo set per pass; 0xdee533d5.

The data the ray generation shader works on:
- `Luxel_t`, the per-luxel accumulator (224 bytes): irradiance and sample
  count; SH r, g, b; quadratic r, g, b and final rgb; baked shadow, baked
  shadow unoccluded and its light IDs; tangent; normal; ambient occlusion.
- `LightmapSample_t`: position, shading normal, interpolated normal, face
  normal (with the transmissive colour in their w), tangent, lightmap uv,
  instance flags and sample flags.
- `SceneGlobalInfoGPU_t`: sky colour, sky ambient bounce, sun irradiance,
  local light bounds, the light bin grid and light count, the IBL transform,
  scale, texture, cubemap and format, sky light flags, the BRDF LUTs and the
  scene flags.
- `PathTraceBarnLightGPU_t`: one light, from scene_info.kv3's barn records.
- `PackedRtxInstanceData_t` and `MaterialDataMinimal_t`: per instance and
  per hit material.
- The push constants: the pass and dimension count, the block's offset and
  the lightmap size, and the cordon box.

## Our CPU bake

`Lighting/Bake/` runs the block compute on the CPU from the same inputs vrad3
reads (scene_info.kv3, scene_meshes.kv3, lightmap_packing_geometry.dat,
lightmap_info.dat), the shaders ported from their GLSL and the host from the
decompile; the post-process is the existing exact port (LightmapPost,
LightmapAhd, LightmapWeld).

- **Scene** (`BakeScene`, `RayScene`, `BakeMaterial`, `Light`): scenesystem's
  loader (1800912d0): instance masks and packed flags from the material
  attributes (180098660), the lights as PathTraceBarnLightGPU_t, the IBL
  cubemap times exp2 of the sky's exposure bias, the globals. A binned-SAH
  BVH with watertight intersection stands in for the hardware: a triangle is
  front facing counter-clockwise from the origin (the sample validation's
  backface count needs that), masks and cull flags as in Vulkan. Shadow
  rays cull nothing: csgo's gameinfo.gi sets
  BakedLighting/DisableCullingForShadows, which clears scene flag 1 (with
  it, probe01's sun leaked through single-sided walls).
- **Block geometry** (`BlockGeometry`): lightmap_load_block_gpu's clip
  (18002d530): cordon-masked triangles overlapping the block, large charts
  first (std::partition on chart flags 0x30000000), small-chart polygons cut
  on the 8 x 8 tile lines with vrad3's epsilon, snapping and frame repacking
  (PackTangentFrame is resourcecompiler's CompressTangentFrame), sorted by
  tile, fanned, with world areas and the per-tile triangle lists.
- **Samples** (`LightmapBaker.Sample`): every fourth pass, two SFMT draws
  (seed 0xdee533d5 per block) offset the small-chart sampler's TEA, two more
  jitter the raster window by half a luxel. The small-chart pass is the
  compute shader exactly: per tile 16 rounds of an area reservoir over the
  tile's triangles, the 64 shared samples matched to luxels by truncated
  uv, a uniform reservoir over all rounds. The raster pass draws the whole
  clipped buffer, depth TEA(primitive id), other charts discarded. Then
  rtx_vrad3_lightmap_sample_validation (two face-normal probes, a short
  step out, eight Hammersley rays counting backfaces).
- **Path tracing** (`PathTracer`): rtx_vrad3_lightmap's ray generation per
  luxel and pass (described in `PathTracer`'s summary) on the scrambled
  Halton volume (`HaltonSamples`, the PBRT sampler of 180187920 with the
  shader's digit groups, `tools/re/halton_table.py`).
- **Since 2026-10-18** (ledger 77): materials without an rtx program run the
  transpiled rtx_default_hitgroup with 180098cd0's block, and the samples go
  through the transpiled rtx_vrad3_lightmap_sample_validation.
- **Not ported yet** (as of 2026-10-17): the csgo material hit shaders (every material uses
  rtx_default_hitgroup: colour texture times tint, no emission, no
  normal maps), cookies, subsurface, the payload's half-float material
  fields, GPU float rounding. The bake cannot be byte for byte (GPU
  transcendentals and traversal order); the target is Valve's statistics.

### Valve's shaders, run as C#

Since 2026-10-17 the bake runs vrad3's own GLSL instead of hand ports:
`tools/re/glsl_to_cs.py` transpiles the SPIR-V-Cross output statement for
statement into C# (`Lighting/Bake/Glsl`: `GlslVectors.g.cs` from
`tools/re/glsl_types_gen.py`, `GlslRuntime.cs` the built-ins with GPU
semantics, min/max/clamp preferring the operand that is not NaN).

- **Ray generation**: `Glsl/Raygen/RtxVrad3LightmapS0F0.g.cs` is
  rtx_vrad3_lightmap's AHD combo; `Vrad3Raygen` hosts it: g_SceneGlobalInfo,
  g_Lights (PathTraceBarnLightGPU_t), one light bin holding every light
  (vrad3's binning is conservative), g_instanceBuffer, the samples, the
  luxels, the Halton volume, and traceRayEXT on our BVH (payload 0 the
  closest hit, payload 1 the shadow visibility). On probe01 and
  ze_hold_em_p it gives the hand port's (PathTracer, still there with
  `S2C_HANDPORT=1`) numbers to three decimals, so the hand port is right and
  the raygen is now Valve's code. About 2.5 times slower.
- **Hit groups**: each material's rtx static combo (ShaderDataProvider over
  its F_ parameters on shaders_vulkan_dir.vpk's _vulkan_60_rtx and
  _features programs) picks a transpiled closest hit
  (`Glsl/Shaders/*.g.cs`, `HitShaders.All`); `RtxScene` builds what they
  read: GeometryInfo_t per mesh (scene_meshes.kv3's float fields, R32G32 to
  R32G32B32A32_FLOAT, offsets times 4, 32-bit indices; inferred from
  scenesystem 180093f10 and the shaders, not captured), the vertex and
  index buffers, PackedRtxInstanceData_t, the bindless textures (every mip,
  sRGB views where the VCS variable has SrgbRead) and samplers, and each
  material's data block: every variable the static combo writes, at its
  write sequence's RegisterOffset (4-byte words), its value the artist's,
  the VCS default or its VfxEval expression (`VfxEval`, with
  materialsystem2's SrgbGammaToLinear and MatrixMultiply; MatrixColorTint2
  and MatrixColorCorrect2 identity at their defaults, other values counted
  as unported). Transpiled so far: rtx_default_hitgroup 0 and 1,
  csgo_environment 0, csgo_complex 2560 and 5120, csgo_simple_2way_blend 0,
  csgo_lightmappedgeneric 32 (every combo ze_hold_em_p and probe01 use;
  `CpuBakeProbe.RtxCombos` lists a scene's).
- **Open (ze_hold_em_p)**: with Valve's raygen and hit groups the bounce
  irradiance stays 1.16 to 1.85 times Valve's (single bounce already 1.3,
  the ratio smooth over each block, shape mask and reservoir ruled out by
  experiment, light records and their reader 1804a5570 checked against
  scenesystem). Next: capture vrad3's own pre-post-process image
  (lightmap_image_save, CS2 closed) for this map and compare luxel by luxel.
  2026-10-18, more ruled out:
  - Emission: baking with every hit's emission zeroed gives the same ratios
    to four digits.
  - The light conversion: 18008c110, the area 1804a3a60 and the record copy
    18008beb0 were re-read against Light.cs and match.
  - The flags: path_trace_light_flags is 15 on both maps.
  - Cookie textures: none, and the raygen samples them only behind
    m_nCookie != -1.
  - All 37 lights are the same type (DISK, no cookie).

  The map's eight light probe volumes give the same picture.
  - Exact: the solid masks equal Valve's and the shadow images are
    near-identical (12154 of 12160 texels).
  - Too bright: the ambient cubes, which are bounce light only here, are
    1.12 to 1.22 times Valve's.

  So the excess sits in the bounce shading both bakes share, i.e. the hit
  groups' material values, not the lightmap sampler, the raygen or the light
  data.

  2026-10-18, found with vrad3's own raw image (CS2 closed; deterministic,
  material swaps in scene_info): the excess is all in
  concrete_slabs_wet_1 (csgo_simple_2way_blend). Its blend weights come
  from texcoord slot 4, a field scene_meshes names VertexPaintBlendParams,
  and RtxScene skipped it. Mapping the field through the material's input
  signature, as scenesystem 180093f10 does for semantic index -1, gives
  0.987 of Valve on block 5. The fix is behind `RtxScene.MapBySignature`
  until the -1 rule is read; see docs/LIGHTING_ROADMAP.md (M1).

`CpuBakeTests.Bake` (`CPUBAKE=<vrad3 folder>`, `CPUBAKE_ADDON`,
`CPUBAKE_BLOCK` for one block into a block-sized image, `CPUBAKE_FILTER=1`
for the script's firefly and median filters on it, `CPUBAKE_STRIDE`)
compares each EXR channel with Valve's on the traced luxels, per luxel and
over 16 x 16 bins; `tools/vrad3/bake_summary.py` tabulates the logs. A
1024 x 1024 block takes about 15 s on six cores.

Parity, 2026-10-17 (each block baked alone with the script's firefly and
median filters, against Valve's EXRs after the full post-process):

- **probe01**, all 16 blocks: irradiance mean ratio 0.983 to 1.004 per
  block and channel, 16 x 16 bin correlation 0.93 to 1.00 (per luxel 0.68
  to 1.00, the rest is the two bakes' noise); directional irradiance within
  1% in the mean; direct light shadows (the sun) 0.97 to 1.00. The R
  channel of direct_light_shadows.exr is written inverted (1 shadowed).
- **ze_hold_em_p**, all 16 blocks (37 shadowed DISK barns, no sky): the
  baked shadow slots match (ratio 1.000, correlation 0.98 to 1.00); the
  irradiance (bounce light only, every light has a shadow slot) has the
  right shape (bin correlation 0.88 to 1.00, per luxel 0.47 to 0.99) but is
  1.16 to 1.85 times Valve's, bluer than it. The cause is the material: the
  world is csgo_environment, whose own hit group
  (csgo_environment_vulkan_60_rtx) takes layer 1's colour through
  g_mTextureAdjust1, a luminance-keeping tint, and darkens it by the height
  texture's green channel; our default hit group uses the raw colour.

## The lightmap post-process

The image (`CVrad3LightmapImage`, global at DAT_180399e70) is w x h with
up to 20 channels (`Vrad3LightmapChannel_t`). Each channel is a float bitmap
(0x9d8 bytes) with a component mask, and is live when its width is above
zero. Beside the channels the image keeps:
- per luxel, a 12-byte record: chart ID; sample count in the low 24 bits of
  +4; flags at +8 (bit 0 valid, bits 1 and 2 the chart's flags bits 29 and
  28);
- per luxel, a 20-byte baked shadow record: four u8 light indices (0xFF for
  none), then four floats.

Charts whose flags have bit 28 or 29 set are small in x or y. Charts with
both set are left alone by the fireflies and median passes.

- `lightmap_image_consolidate_small_charts <mip>`: on each axis on which a
  chart is small, the chart's rect is cut into cells of 2^(mip - 1) luxels.
  Each cell takes two passes over the chart's own luxels:
  1. A running weighted mean of every live channel component over the
     luxels with samples. With n the luxel's sample count and N the running
     total, t = n / N and mean = mean * (1 - t) + value * t. The shadow
     record blends the same way per slot: the first light index met fixes
     the slot, and only luxels with that index blend into it.
  2. The mean, the shadow record and N are written to every luxel of the
     chart in the cell, and each is marked valid.

  Ported: LightmapPost.ConsolidateSmallCharts.
- `lightmap_image_remove_fireflies <threshold> [debug.exr]` (Run 18004b530,
  rows 18004ba20): the threshold goes through `V_atoi`, so "4.0" is 4 and
  fractions are lost (default 4). Two passes over the rows. The luxels taken
  are valid, of a chart not small on both axes. Neighbours are the 8 around
  it of the same chart, valid, with Rec.709 luminance
  (g 0.7152 + r 0.2126) + b 0.0722 of at least 0.0005; in pass 1 they must
  also not be flagged. Their irradiance is averaged, and each one's SH
  evaluated at its own normal (180054c70; L2 constants 0.282095, 0.325735,
  0.273137, 0.078848, 0.136569; each colour clamped at 0) is averaged too.
  The means multiply by 1/n; with no neighbours they stay 0.
  - Pass 0 flags the luxel (a bitset, and the count in the log) unless,
    per colour, its RGB is at most threshold x the neighbour mean, its
    |SH at its own normal| is at most threshold x the neighbours' mean, and
    at most threshold x its own RGB.
  - Pass 1 replaces each flagged luxel's irradiance with the mean of its
    unflagged neighbours when it has any.

  The debug EXR is (0, 0, 0, 1), with (0, 0, 1) where no chart covers the
  luxel and (1, 1, 1) on fireflies. Ported: LightmapPost.RemoveFireflies.
- `lightmap_image_filter_median <size>`: radius r = (size - 1) / 2, at
  least 1. For each valid luxel, it gathers each component separately (RGB,
  channel 8, the SH coefficients) from luxels that are in the window
  clamped to the chart's rect and within the disc dx^2 + dy^2 <= r^2. They
  must also be of the same chart, valid, and with normal dot >= cos 15
  degrees. Only luxels of charts not small on both axes are filtered. The
  lists are the irradiance (masked), AO (channel 8 x) and SH coefficients 0
  to 3 of each colour (180043e30 reads the SH: per colour, channel 1 + c holds
  coefficients 0 to 3, channel 4 + c holds 4 to 7, channel 7's component c
  holds 8). With more than 2 values it takes the median through
  `std::nth_element` (18005cf40) at n/2. For even n it calls nth_element
  again at n/2 + 1 and averages the first value with whatever then sits at
  n/2 - 1. MSVC's nth_element is an insertion sort up to 32 values
  (ISORT_MAX), so for sizes up to 7 (29 luxels at most) that is the textbook
  median; past 32 it partitions (ninther above 40) and the quirk applies.
  The results go to a zeroed 128-byte record per luxel, and Run writes all 27
  SH floats back (1800443d0): a filtered luxel's SH coefficients 4 to 8
  become 0. Ported: LightmapPost.FilterMedian, MsvcSort.NthElement.
- `lightmap_image_dilate_over_invalid_jfa`: a jump flood over the luxel
  grid with two index buffers. lambda_1 seeds both with each luxel's own
  index where it is valid or no chart covers it (-1 elsewhere), so luxels
  outside the charts are seeds and never get filled. A pass runs per step
  size s, from next_pow2(max(w, h)) / 2 halving down to 1, and the current
  buffer flips before every pass after the first. In a pass, each luxel
  whose current entry is not itself looks at the 3x3 neighbours at offsets
  {-s, 0, s} of the same chart. It takes the neighbour's entry from the
  other buffer when it has none or the new seed is nearer (integer squared
  distance), writing the current buffer in place; that buffer is not first
  copied from the other. The fill (lambda_4) copies, for each luxel with a
  seed not itself, every live channel and the first 16 of the shadow
  record's 20 bytes from the seed, then marks it valid. Dilation never
  crosses charts. Ported: LightmapPost.DilateOverInvalidJfa.
- `lightmap_image_encode_ahd`: per valid luxel, the irradiance RGB and the
  9 SH coefficients per colour are rotated into the luxel's tangent frame
  (18005d2e0). A Rec.709 luminance SH is formed. The highlight direction d
  is the normalised L1 direction (-c3, -c1, c2). If it points under the
  surface and its xy is under 0.0001, it takes the left neighbour's
  direction when that luxel is the same chart, else flips z and zeroes the
  colour. It is then clamped to z >= 0.0872 (5 degrees), with xy rescaled to
  cos 5 degrees. Over 256 fixed directions (the table at DAT_1802e99e4) a
  one-unknown least-squares system is set up. Each row is
  (1 - max(0, dir.d) / d.z) x luminance against
  max(0, SH(dir)) - max(0, dir.d) / d.z x luminance. Its solution splits the
  light into the ambient and the directional part along d.
  - The tangent frame (180044bc0) has columns t = normalised channel 11 xyz,
    cross(n, t) normalised times channel 11 w, and n = normalised channel 9;
    its inverse (1801f4250) is the transpose, rescaled by 1 / |t|^2 when that
    is off 1 by 0.001 or more. 18005d2e0 rotates each colour's 9
    coefficients as z, x +90, z, x -90, z rotations (180062a70), negating
    coefficients 1, 4 and 5 when the frame is mirrored.
  - The luminance SH is g 0.7152 + (r 0.2126 + b 0.0722) per coefficient.
    Below 1e-5 at L0 the irradiance becomes the colours' L0 and channel 1
    (0, 0, 1, 1). The left neighbour's direction is its channel 1 xyz as
    already written this pass; the clamp rescales xy to sqrt(0.992396176).
  - The rows: c = max(w.d, 0) / d.z and L = (g 0.7152 + r 0.2126) + b 0.0722,
    A = (1 - c) L, b = max(SH(w), 0) - c L. The solve (18005aed0 then
    18005e7f0) is conjugate gradient on A^T A x = A^T b from x = 1, stopping
    at a squared residual under 1e-20 or after 100 steps; sums run in row
    order. x is clamped to 0..1 and channel 1 = (d, x); the irradiance is
    written back (zeroed in the flipped case).
  - Run then frees channels 2 to 7, moves channel 0 to 14, makes channel 15
    (mask 1) from channel 1's w and channel 13 (mask 7) from its xyz, and
    frees channel 1. Ported: LightmapAhd.Encode (directions embedded as
    AhdDirections, read from DAT_1802e99e0).
- `lightmap_image_fill_gutters <mip>`: cells of 2^(mip - 1) luxels, and a
  gutter width from gameinfo `ResourceCompiler/BakedLighting/LightmapGutterSize`
  (default 1). lambda_1 (18005cc10) marks the luxels any chart covers; the
  mark is never updated, so filled luxels are not sources and the repeats
  change nothing. Pass 0 covers ordinary charts, repeated gutter-width times;
  pass 1 covers charts small on one axis, once (both: skipped). Each
  unmarked luxel inside a chart's rect, walked in cells (not clipped), is
  filled (180049830) from the first square of radius 1 up to the cell size
  that holds marked luxels of the same chart. It takes the mean of every
  live channel there, and per shadow slot the first light index met with
  the sum of the floats of luxels with that index (an unset slot sums the
  luxels with none) over the count. The luxel takes the chart id, valid
  when any source was, and flag bits 1 and 2 from the chart; its sample
  count stays. Ported: LightmapPost.FillGutters.
- `uber_weld <lightmap_seams.dat>` (Run 180050360): a sparse least-squares
  weld. The file is 32-byte records of four atlas-uv points (probe01: 30):
  edge A from P0 to P1, edge B from P2 to P3, each to texels as
  uv x size - 0.5.
  - Seam rows: n = max(3 x the longer edge, 1), t from 0.5/n in steps of
    1/n (accumulated) while t <= 1. A sample is skipped when both
    |A.x - B.x| < 1/width and |A.y - B.y| < 1/height (texel units against
    1/size, as written). Otherwise 18004ed10 adds A's four bilinear taps at
    weight +4 and B's at -4: weights (1-fy)(1-fx)W, (fx W)(1-fy),
    ((1-fx)W)fy, (fx W)fy on texels clamped to the image. Within one side a
    nonzero tap absorbs later taps on its texel and zero taps go, each
    removal moving the last entry into the hole. The row ends with
    (-1, -1) at weight 0, its right-hand side.
  - Data rows: the texels named, in first-seen order (TexelCoord_t hash),
    each at weight n (times named) or 0.01 when its luxel has no samples,
    right-hand side its value times that weight.
  - Channels 0-3, 8 and 13-15 that exist are solved one at a time
    (18004f6d0): the masked components as one system (row r n + c, column
    texel n + c), each row's triplets sorted by (row, column) with MSVC's
    sort, zeros dropped. CG on the normal equations (18005b2b0, 180060210)
    from the current values, tolerance 1e-20, 100 steps: row sums in entry
    order, the transpose in row order, dot products (1801f6660) in chunks of
    32768 four-lane groups reduced (l2 + l3) + (l0 + l1), chunks summed in
    order, the 0-3 leftover elements last.
  - Before: channel 13 to world space by the tangent frame (lambda_2,
    each component (w m3 + y m1) + (z m2 + x m0)), channels 0 and 14 (and
    1) to YCoCg (180042730). After: back from YCoCg clamped at 0 (1800427b0)
    in the reverse order, channel 13 by the inverse frame. TransformRGB
    writes a pixel only when a component compares different.

  Ported: LightmapWeld.
- `lightmap_image_write_ahd <colour.exr> <direction.exr>`: the colour file
  is channel 14 as it stands (FloatBitMap_t::WriteToFile, 18009b280). The
  direction file is built per luxel (18005b9f0): R = dir.x x 0.5 + 0.5,
  G = dir.y x 0.5 + 0.5 (channel 13), B = the 1-component AHD channel
  (channel 15), A = channel 8 when it exists (the bitmap is RGBA only then,
  mask 7 + 8). Ported: LightmapPost.AhdDirectionExr.
- `lightmap_image_write_direct_light_shadows <file> [-indexlights N]
  [-gamma20] [-noinvert]`: N defaults to 4, and inverting is on by default.
  Per luxel (18005c100), each of the N slots starts at 1 when inverting,
  else 0. A slot whose light index is set takes clamp(shadow, 0, 1), its
  square root under -gamma20, then 1 - v unless -noinvert. The bitmap is
  RGBA (mask 0xF) of the first four slots. Ported:
  LightmapPost.DirectLightShadowsExr.
- `lightmap_image_write_chart_color <file> <minspp> <maxspp>`: each chart
  in its debug colour (1801f6400 converts the ARGB), with the sample counts
  scaled between minspp and maxspp.

## Light probe volumes

resourcecompiler's script runs one routine per volume: `lpv_type`,
`lpv_cells`, `lpv_box`, `lpv_local_to_world`, `lpv_init`,
`lpv_image_gpu 96 96 96`, `lpv_voxelize_gpu`, `lpv_flood_fill`,
`lpv_compute_block_gpu 0 0 0 X Y Z 64 64 0.200`,
`lpv_image_ambient_occlusion`, `lpv_image_write_ambientcube <noisy>`,
`lpv_image_remove_fireflies 3 <debug>`, `lpv_image_resolve`,
`lpv_image_median_filter 3`, then the writes.

- `lpv_type sh2|cube|ambient_cube`: sh2 is 1, the cube forms 0
  (DAT_1803ab5f0).
- `lpv_cells X Y Z`, `lpv_box min max`, `lpv_local_to_world m00..m23`
  store the volume's cells, local box and 3x4 matrix. Floats are parsed
  through `V_StringToFloat64`, then narrowed.
- `lpv_init` copies them into the global volume (DAT_1802ed208: box, cells)
  and clears the occupancy bitset (one bit per cell).
- `lpv_image_gpu W H D`: the grid gets a one-cell border on every side
  (cells + 2), and its box grows by one cell size each way. It frees the
  previous images and makes new ones:
  - the ambient cube image, (X x Z) by (Y x 7); confirmed on probe01's
    volumes (32x42x6 is 192 x 294, 26x10x6 is 156 x 70), as is the
    shadow image's (X x Z) by Y. The readback fills the six face bands;
    what fills the seventh is still to read;
  - the direct light shadow image, (X x Z) by (Y x 2);
  - three SH images, (X x Z) by (Y x 2) each;
  - for fog, one image.

  It also sizes the per-cell shadow records (20 bytes: four light indices,
  four floats) and the GPU compute image, W x H x D.
- `lpv_voxelize_gpu` (180032850): 32 passes of rtx_vrad3_lpv file 0 over
  the bordered grid, pass i with m_nEstimate i, three SFMT-19937 draws (seed
  0xea213b00; offset x the third, z the first, each times 2^-32), the
  bordered box and the local-to-world matrix. Each launch traces 32 rays from
  its jittered cell position; more than 12.8 back faces mark it solid, and a
  solid cell stays solid. 18002a320 reads bit 0 back. The generator is
  SFMT, not MT19937 (1801ff9a0 / 1801ff440: the reference parameters).
- `lpv_flood_fill` (1800323f0): the bordered cell holding the local origin
  (floor((0 - min) / cell size), clamped). 180217b50 walks occupied cells
  breadth first (+x, +y, +z, -x, -y, -z, Manhattan distance up to 8) to the
  first empty neighbour; 180217110 starts from everything occupied and clears
  the empty cells reachable from there. The result is uploaded as the solid
  mask (0 / 1 per bordered cell).
- `lpv_compute_block_gpu sx sy sz ex ey ez [minspp] [maxspp] [err]` (180035e80):
  minspp defaults to 512, maxspp to 1024 and err to 0.02. Passes run in
  command lists of 16, up to maxspp, each with the pass's Halton volume and a
  second SFMT (same seed). Push constants: m_nEstimate the pass, dimension
  count 4, filter radius 1, the fog flag, the region start with the grid's
  counts packed 16:16, the offset, and the region's box (start x cell + min,
  end x cell + min). The convergence test (lightmap_test_converged, the LPV
  combo: 3 x 3 x 3 cells a group; no valid cell is converged, two or fewer is
  not; else each face's luminance standard deviation within mean x err) runs
  before pass 0 (err 0, writing 0), then at the start of each 16-pass
  command list that begins at or past minspp (180036258; not between the
  passes of a list).
  Each cell reads back as a 192-byte LpvLuxel:
  - six float4 ambient cube faces, RGB clamped at 0, written to the ambient
    cube image at column z x X + x, row face x Y + y;
  - three float4s copied to the SH images as they are;
  - baked shadows as four (sum, weight, light index) triples, stored as
    sum / weight in the cell's shadow record when the weight is positive.

  With fog, the cell's first three floats go to the fog image instead.
  Ported (LightProbeVolumeBaker, Valve's raygen transpiled): probe01's two
  volumes reach Valve's statistics, the solid mask is identical (357 and 279
  solid cells, the open band texel for texel), the noisy and final ambient
  cubes are within 0.2% in the mean (1% on the small volume) at correlation
  0.98 to 0.998, and the shadow image is 8051 of 8064 and 1560 of 1560
  texels identical (2026-10-17, dev box, 4.5 s for both).
- `lpv_image_ambient_occlusion` (180082fc0, 180083580): per open cell and
  face, the 26 neighbour directions d = normalize(dx, dy, dz) weighted
  w = ((n.d 0.5 + 0.5) - max(n.d, 0)) 0.33 + max(n.d, 0). Two steps along d,
  w halving each step, add w to the total and, from the first solid cell on,
  to the occluded sum. The factor 1 - weight (occluded / total)^2, with
  weights 0.7, 0.7, 0.85, 0.7, 0.7, 0.9 for +X, +Y, +Z, -X, -Y, -Z, scales the
  face's four channels when it is under 1.
- `lpv_image_remove_fireflies [ratio] [debug]`: the ratio is
  (float)V_atoi(argv[1]), default 4.0; Ghidra's decompile drops this float
  argument, the disassembly shows it. It runs the SH variant (180083e40) or
  the ambient cube one (180084c50, body 1800851b0): per face, a detect pass
  flags an open cell whose luminance (0.2126, 0.7152, 0.0722) is at least
  5e-5 and whose R, G or B exceeds ratio times its open 26 neighbours' mean;
  a fix pass gives each flagged cell the mean of its open, unflagged
  neighbours.
- `lpv_image_resolve [-gamma20]`:
  1. 180081560 dilates the shadow records: two rounds (not until nothing
     changes). A cell with an empty slot that has a neighbour holding that
     slot is ready; each ready cell takes, per empty slot, the mean of its
     non-ready neighbours' values, marked 254, and queues its neighbours with
     empty slots for the next round.
  2. It writes the direct light shadow image ((X x Z) by Y, four slots per
     cell): an empty slot is 1, else 1 - clamp(v, 0, 1), or 1 - sqrt(v) with
     -gamma20.
  3. For the cube type, 18007f720 dilates the ambient cube into the solid
     cells: per face, sweeps fill each cell whose valid 3 x 3 x 3 neighbours
     weigh more than the threshold (0.2308, 0.4615, 0.3077 by Manhattan
     distance, halved off the face direction), alpha 1. The threshold starts
     at 0.5 and drops by 0.1 while above 0.
  4. 1800812a0 writes band 6: RGB 0.3 in open cells, 0 in solid ones, alpha 1.
- `lpv_image_median_filter <size>` (1800860b0): radius (size - 1) / 2, at
  least 1. Size 0 skips it. For the cube type, per face and open cell, each
  of R, G and B becomes the median of the open cells reached through open
  face neighbours within the radius, the cell itself excluded (the mean of
  the middle two for an even count; one or none leaves it). The values
  change after the whole face ("LPV Median Filter Indirect").
- `lpv_image_write_ambientcube <file>` writes the ambient cube image as it
  stands. `lpv_image_write_direct_light_shadows <file>` writes the shadow
  image, or an all-ones image when there are no baked shadows.

resourcecompiler then builds the atlases (LPVAtlas 1). The places come from
LightProbe_PackAtlas, ported as Maps/ProbeAtlas. The atlas size is the
allocator's dims of the last fit, each rounded up to a multiple of 4
(1801f5fce, 1801f6080). With two or more volumes that can exceed the packed
boxes' extent.
- 1801f64f0 copies each volume's ambient cube over a range from
  (place - 1) & ~3 to (place + size + 4) & ~3, clamping the source texel.
- 1801f69c0 copies the shadows over place - 1 to place + size.

Both are ported in Lighting/LightProbeAtlasImages, and are identical to
probe01's lpv_atlas_*.exr when built from vrad3's own per-volume images.
The SH and fog paths (`lpv_image_encode_sh2`, `lpv_image_write_sh2`,
`_fog`, the OIDN filters) are still to read.

## Far field probe volumes

Each `env_farfield_probe_volume` gets a routine in the script.
resourcecompiler's writer (1801eea20 in the 10-06 build, lightbaker.cpp)
emits:

```
run compute_farfield_<n>
compute_farfield_<n>:
    farfield_box <min> <max>
    farfield_local_to_world <m00..m23>
    farfield_cells X Y Z
    farfield_compute_bricks farfield_<n>_bricks.kv3
    farfield_voxelize_bricks
    farfield_compute_lighting 1024
    //farfield_write_atlas ff/farfield_<n> -suffix _raw
    farfield_median_filter 1
    farfield_dilate 3
    //farfield_write_atlas ff/farfield_<n> -suffix _dilated
    farfield_filter_fireflies 4.0
    farfield_resample_borders
    farfield_write_atlas ff/farfield_<n>
    done
```

resourcecompiler then reads `farfield_<n>_bricks.kv3` and the atlas
slices. It builds `lightmaps/env_farfield_<n>_indirection.vtex` and
`env_farfield_<n>_sh2_dc` / `_sh2_l1r` / `_l1g` / `_l1b.vtex` (EntityLump
export 180240a90).

- `farfield_box`, `farfield_local_to_world`, `farfield_cells`: as the LPV
  ones. `farfield_max_memory <MB>` sets a float budget (DAT_1802ecdc8).
- `farfield_compute_bricks <file.kv3>`: marks the occupied cells with the
  LPV's occupancy builder (180032b60), dilated by 2 cells. It builds sparse
  bricks over them (18001fa00), sorts them, and puts brick 0 first, a
  level-7 sentinel covering the whole box. It writes the KV3:
  - `bricks[]`: `level` (a brick covers 2^level cells per axis), `origin`
    (cell coordinates), `min` and `max` (world);
  - `cell_count`, `cells_x`, `cells_y`, `cells_z`;
  - `cell_descriptors`: the per-cell indirection words.
- `farfield_voxelize_bricks`: the samples inside each brick are tested for
  solid (180035580) and the share is logged.
- `farfield_compute_lighting [spp] [bricks_per_batch]`: defaults 128 and
  512. The atlas holds at most 32 x 62 = 1984 bricks; past that it exits.
  It is four components (dc, sh1_r, sh1_g, sh1_b), each 33 z-slices of
  1056 x (33 x rows) float images (ceil(bricks / 32) rows). A brick is
  33^3 samples: 32^3 inside plus a border plane. The GPU trace is
  180035580 / 1800358b0 (`rtx_vrad3_farfield`). It logs the luxels, the
  black ones and the mean and max DC.
- `farfield_median_filter <passes>` and `farfield_dilate <passes>`: the
  argument counts passes, not a window size. They run per brick on the
  thread pool (180023510 for the median).
- `farfield_filter_fireflies <ratio>`: the ratio is parsed as a float
  (`V_StringToFloat64`), unlike the lightmap's integer. It clamps the
  samples above the ratio per brick (180023dc0) and logs how many.
- `farfield_resample_borders`: per brick (180024400), the border plane is
  filled from the neighbouring bricks, so the atlas filters across them.
- `farfield_write_atlas <path> [-suffix s]`: writes
  `<path>_{dc, sh1_r, sh1_g, sh1_b}_z00..z32<s>.exr`, 132 files in all,
  making the folder first.
