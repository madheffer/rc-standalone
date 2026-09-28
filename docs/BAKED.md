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
