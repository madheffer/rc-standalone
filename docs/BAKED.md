# Baked halves

What a full compile bakes: lightmaps, light probes, cubemaps, precomputed
shadows, nav, Steam Audio, bomb damage. The port is planned, not started;
vrad3 is driven as a stopgap. What is ported so far is the entity-side keys
the bake depends on (ENTITIES.md: lighting keys, probe atlas, baked shadow
slots).

## vrad3

resourcecompiler shells out to `game/bin/win64/vrad3.exe`. Its working
directory `game/csgo_addons/<addon>/_vrad3/` and the generated script
survive the compile, so the lighting stage re-runs on its own:
```bash
cd "<CS2>/game/csgo_addons/<addon>/_vrad3"
"<CS2>/game/bin/win64/vrad3.exe" -map maps/<name>.vmap -script script-gpu.vrad3 -vulkan -gpuraytracing -allthreads -unbufferedio
```

**The script is the pipeline:**
- `gpu_init`, `lightmap_type ahd`, `sceneinfo_gpu scene_info.kv3`;
- `lightmap_image_gpu 4096 4096 1024`;
- the packing geometry, then 16 of `load_block N` plus
  `lightmap_compute_block_gpu 16 16 0.200`;
- consolidate, fireflies, median 7, JFA dilate, AHD encode, gutters, the
  seam weld, then the writes.

There are 86 commands (`CVrad3Command_<name>` in RTTI), including Valve's own
checks: `run_furnace_test_gpu` and `determinism_check_record` / `_replay`.

**Stack:** Vulkan ray tracing (a DX11 path exists), Intel Open Image Denoise,
Embree 3 (in resourcecompiler), tinyexr. Use RT-core GPUs (L40S, RTX
4090-5090), not H200s.

**I/O:**
- **In:** `scene_info.kv3` and `scene_meshes.kv3` (KV3 text),
  `lightmap_info.dat`, `lightmap_packing_geometry.dat`, `lightmap_seams.dat`.
- **Out:**
  - `irradiance.exr` and `directional_irradiance.exr`;
  - `direct_light_shadows.exr`, `fireflies.exr` and
    `lightmap_chart_color.exr`;
  - per probe volume: `lpv_<id>_ambientcube.exr` (plus `_noisy`, `_dlshd`,
    `_fireflies` and `_octree.dat`).
- **EXR format:** uncompressed scanline half floats, so writing needs no EXR
  library and comparing is an array diff. The `_noisy` files allow comparing
  before denoising.
- **`lightmap_info.dat`:** one u32 chart id per luxel (`0xFFFFFFFF` for
  none), stored block by block in block-row-major order. A valid id indexes
  a 28-byte chart record from the packing geometry; its layout is not read
  yet.

## Measured

Lighting is not what costs a compile. On Mako, lightmapping is 9.3 s of
1,313 s; visibility is most of it (VISIBILITY.md).

The vrad3 run on an RTX 4070 across the test maps:
- the median filter is a fixed ~8 s (7x7 over the whole 4096 atlas);
- probe volumes grow with their count (124 on Mako: 11.3 s);
- the GPU trace is 0.9 to 5.9 s;
- Mako's whole vrad3 run is 56 s.

The trace is linear in the sample count, at 0.344 s per sample. X is clamped
by Y. The third argument (0.200) is probably a convergence threshold;
unconfirmed.

## Compiling a community port

- **Missing materials are fatal**, but only found late: Mako spent 22
  minutes before failing on one.
  - `tools/vrad3/check_references.py <addon> <map> [--stub]` reads the
    .vmap and lists what cannot resolve, including tool materials in
    `game/core`.
  - Stubs must be compiled before the map.
- **A missing model is not fatal.** The prop is simply absent from the
  scene, which makes lighting measurements lower bounds. Extract the
  compiled models from the workshop vpk.

## Where the port starts

1. The scene resourcecompiler hands vrad3 (`Vrad3_Init`,
   `WRB_BuildPathTraceSceneInfo`), the chart packing and the seams.
2. A tracer behind the same I/O contract, scored per luxel against Valve's
   EXRs: direct light, then bounce, then the AHD encode.
3. Light probe volumes, cubemaps, then nav (`CNavMesh`) and Steam Audio.

## Lightmap charts in resourcecompiler

Step 1 of the port is the input side: what resourcecompiler writes into
`_vrad3/` before vrad3 runs. Read from the 20260923 build (2026-10-16; names
in make_manual's BAKE list, ADDRESSES.md):
- `CStaticLightingProcessor::GenerateCharts` (staticlightingprocessor.cpp)
  makes one 0xb8 "chart mesh" per mesh (shared meshes, instances, unique
  meshes, each through a thread pool job), from the LightmapUV, texcoord and
  position streams. Gameinfo `ResourceCompiler/BakedLighting/
  ImportanceVolumeTransitionRegion` (72 by default, 120 in CS2's gameinfo)
  takes part. The charting core (18022d920) builds a 200-byte record per
  triangle (18022c740): its unit normal (VectorNormalize, slow path below
  1e-17) and its chart uvs. `UnwrapUsingUvsFromMesh` is read with default
  true (CS2's gameinfo leaves it unset), so the mesh's own uvs are used: the
  texcoord set the mesh entry picks (uv2 with use_uv2_for_charts), v scaled
  by the chart's aspect. Without it, a box projection: the normal's dominant
  axis picks one of six bases (+-X, +-Y, +-Z; codes 0-5, tables at
  182a8d3b0 / 182a8d400).
- The chart mesh prep (180210730) drops the mesh's LightmapUV stream, welds
  it (CMesh_Weld, ported) when asked, copies vertices and indices, flips the
  winding of triangles whose material has bit 2 of 18020d860's flags and
  that face away (normal sum test), and with importance volumes gives each
  triangle a resolution scale (18020f910, ImportanceVolumeTransitionRegion).
- `CStaticLightingProcessor::PackCharts` concatenates them into
  `PackingGeometry_t`, the layout `Lighting/PackingGeometry.cs` already
  round-trips byte for byte: stride 0x28 vertices with attribute offsets 0,
  0xc and 0x18; indices; a chart per triangle (-1 for none); 0x1c chart
  records whose flags take the mesh's bit 30 and its low 28 bits.
- `NUVChartPacker` (uvchartpacker.cpp), ported in Lighting/LightmapPacker.cs
  (ledger 63): `PackCharts` seeds SFMT-19937 (0x777f17e) for the debug
  colours, runs `FindBestRotation` per chart, then for each scale pass
  `InitializeChartUVs` (gutter `LightmapGutterSize`, 1: rotate, scale, pad
  to 4, sort) and `TryPack` (block-mask raster, gutter dilation, first fit;
  single luxel and line charts last, big groups of them in a TinyBVH's
  order, Lighting/TinyBvh.cs), and writes x, y, w, h into the chart records
  and the atlas uvs. Exact on probe01 and ze_hold_em_p.
- `NLightBaker::RasterizeAndSetupTexels` / `SetupTexels` give each luxel its
  chart (lightmap_info.dat); `WriteLightmapEdgeSeamsForVrad3` writes
  lightmap_seams.dat; `WRB_BuildPathTraceSceneInfo` scene_info.kv3 and its
  mesh file.

Ground truth per map: `compile_fresh.py --full` keeps the addon's `_vrad3`
folder beside the package (`<out>.vrad3`), since the next bake overwrites it.
The first milestone is our lightmap_packing_geometry.dat equal to Valve's on
probe01; it also gives the world nodes their lightmap uvs (GEOMETRY.md).

## AMD GPUs (future plan)

**Report (2026-10-05, from a user, not yet reproduced):** on AMD GPUs,
compiling lighting, or previewing it in Hammer, is not possible or is
bugged. Which of the two fails, and how (vrad3 refusing to start, a crash,
or wrong lightmaps), is not known yet.

**Goal:** our own bake compiles and previews lighting on AMD as it does on
NVIDIA. This is a requirement for the lighting port, not an extra.

**What we know that bears on it:**
- Valve's run is `-vulkan -gpuraytracing` (Vulkan ray tracing); a DX11
  path exists. Every measurement here is from an RTX 4070.
- Embree 3 sits in resourcecompiler, so a CPU trace path exists somewhere
  in Valve's tools; whether vrad3 can use it is not read.

**Plan:**
1. Reproduce on AMD hardware: the vrad3 command line above, once with
   `-vulkan -gpuraytracing` and once with the DX11 path. Capture the log and
   the failing command. Then do the same for Hammer's lighting preview.
2. Read why it fails in vrad3 (a device or extension check, a
   vendor-specific shader path, or a driver bug).
3. Keep our tracer vendor-neutral: standard Vulkan ray tracing extensions
   only (AMD RDNA2 and later support them), a compute-shader fallback for
   GPUs without ray tracing hardware, and a CPU path that produces the
   same result. Score each path per luxel against Valve's EXRs, as in
   "Where the port starts".
4. Get an AMD GPU into the test matrix before calling the bake done.
