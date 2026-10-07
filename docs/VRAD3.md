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
