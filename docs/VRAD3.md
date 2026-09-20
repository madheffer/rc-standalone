# vrad3, the lighting compiler: what it is and how to work on it

Lighting is the expensive half of a map compile - big ZE maps are reported at
around a day - so it is the part worth replacing first. This is the measured
ground truth for doing that, taken from the shipping CS2 tools on 2026-09-20.

The headline: **vrad3 is a standalone, scriptable tool, and resourcecompiler
writes its script in plain text.** Nothing here had to be guessed.

## It runs on its own, in seconds

`resourcecompiler` shells out to `game/bin/win64/vrad3.exe` with a working
directory of `game/csgo_addons/<addon>/_vrad3/` and a generated script. Both
survive the compile, so the lighting stage can be re-run on its own:

```bash
cd "<CS2>/game/csgo_addons/<addon>/_vrad3"
"<CS2>/game/bin/win64/vrad3.exe" -map maps/<name>.vmap -script script-gpu.vrad3 \
    -vulkan -gpuraytracing -allthreads -unbufferedio
```

Measured on an RTX 4070 for a small probe map: **18.3 s wall, of which
"Lightmapping took 3.24 seconds"**. The CPU post-stages cost more than the trace
at that size - the 7x7 median filter alone was 7.86 s. So the iteration loop for
lighting work is seconds, and the tool narrates every stage with timings, which
makes it its own profiler.

## The script IS the pipeline

`script-gpu.vrad3`, verbatim from a real compile, minus the repeated block loop:

```
// vrad3.exe -map maps/probe01.vmap -script script-gpu.vrad3 -vulkan -gpuraytracing -allthreads
gpu_init
lightmap_type ahd
sceneinfo_gpu scene_info.kv3
lightmap_image_gpu 4096 4096 1024
lightmap_load_packing_geometry_gpu lightmap_packing_geometry.dat
run compute_all_lightmap_blocks          // 16x: load_block N; lightmap_compute_block_gpu 16 16 0.200
lightmap_image_consolidate_small_charts 1
lightmap_image_remove_fireflies 4.0 fireflies.exr
lightmap_image_filter_median 7
lightmap_image_dilate_over_invalid_jfa
lightmap_image_encode_ahd
lightmap_image_fill_gutters 1
uber_weld lightmap_seams.dat
lightmap_image_write_ahd irradiance.exr directional_irradiance.exr
lightmap_image_write_direct_light_shadows direct_light_shadows.exr -indexlights 4
lightmap_image_write_chart_color lightmap_chart_color.exr 16 16
```

**86 commands exist**, recoverable from the binary's RTTI as
`CVrad3Command_<name>`: `path_trace_triangles_gpu`, `lightmap_image_encode_sh2`,
`lpv_voxelize_gpu`, `lightmap_image_filter_oidn`, `lpv_write_light_octree` and so
on. Valve also ships their own validation commands - `run_furnace_test_gpu`
(energy conservation), `lightmap_image_write_convergence`,
`lightmap_analyze_image_error`, `determinism_check_record` / `_replay`.

## The stack

| Piece | What |
|---|---|
| ray tracing | **Vulkan** RT (`-vulkan -gpuraytracing`; a `-dx11` path exists) |
| denoising | **Intel Open Image Denoise**, with CPU / CUDA / HIP / SYCL device DLLs |
| scene prep | **Embree 3**, referenced by `resourcecompiler.dll` rather than vrad3 |
| EXR io | tinyexr (`src/bitmap/tinyexr_impl.cpp` leaks in the strings) |
| banner | "VRAD3 - Distributed Lighting Tool", build pc64 Aug 24 2026 |

Note for anyone renting GPUs to run this or a replacement: this is a ray tracing
workload, so **RT cores matter and an H200 does not have any**. L40S / RTX
4090-5090 / RTX 6000 Ada are the right shape.

## The I/O contract

Everything passes through the `_vrad3` directory, and half of it is text.

**In:** `scene_info.kv3` and `scene_meshes.kv3` (plain KV3 - instances, material
per instance, flags), `lightmap_info.dat`, `lightmap_packing_geometry.dat`,
`lightmap_seams.dat`.

**Out:** `irradiance.exr`, `directional_irradiance.exr`,
`direct_light_shadows.exr`, `fireflies.exr`, `lightmap_chart_color.exr`, and per
light probe volume `lpv_<id>_ambientcube.exr` (plus `_noisy`, `_dlshd`,
`_fireflies` and `_octree.dat`).

The `_noisy` files sit beside the denoised ones, which means a replacement can be
compared **before** denoising - transport and denoise are separable.

### The EXR output needs no EXR library

Headers read straight off the files:

| file | size | channels | compression |
|---|---|---|---|
| `irradiance.exr` | 4096x4096 | B, G, R half | **none** |
| `directional_irradiance.exr` | 4096x4096 | A, B, G, R half | none |
| `direct_light_shadows.exr` | 4096x4096 | A, B, G, R half | none |

Uncompressed scanline half float: 4096 x 4096 x 3 x 2 = 100,729,145 bytes, which
is the file size to the byte. Writing these is a header and raw scanlines, and
comparing ours against Valve's is an array diff.

### lightmap_info.dat is one u32 per luxel

Decoded by inspection, no decompiler needed. The file is **exactly 64 MiB =
4096 x 4096 x 4**, and the script's `lightmap_image_gpu 4096 4096 1024` plus its
16 `lightmap_load_block_gpu` calls say why: the atlas is 4096 square, a block is
1024 square, 4 x 4 = 16 blocks of 4 MiB each.

On the probe map, **71.1% of slots are valid**, `0xFFFFFFFF` marks the rest, and
the valid values are small dense ids (0 to 70, 71 distinct). So this is the
per-luxel "which surface am I" map the tracer shoots rays from. Exactly what the
id indexes - chart, instance or triangle - is the next thing to pin down, against
`lightmap_packing_geometry.dat` (whose header reads `0, 40, 434, 896.0f, 992.0f`:
a count, a stride, and rects).

## Reading the binary

vrad3.dll is **stripped** - no function names - but it keeps RTTI type
descriptors and every script command's literal, so a string reference lands on
the function that implements it.

Ghidra 12.1.3 lives in `D:\tools\ghidra_12.1.3_PUBLIC` with two scripts in
`D:\tools\ghidra_scripts`:

```bash
# one-off: import and analyze (83 s for vrad3.dll)
analyzeHeadless D:\tools\ghidra_projects cs2 -import "<CS2>\game\bin\win64\vrad3.dll"

# decompile every function that references a string
analyzeHeadless D:\tools\ghidra_projects cs2 -process vrad3.dll -noanalysis \
  -scriptPath D:\tools\ghidra_scripts -postScript DumpByString.java \
  "lightmap_load_block_gpu" out.c
```

`DumpDecompiled.java` does the same by function-name regex, which is useful on
binaries that kept their symbols. On a stripped one it finds nothing, which is how
this pair came to exist.

The ReVa MCP server is installed for interactive use, but only in **assistant
mode**: start Ghidra's GUI and it serves MCP on `localhost:8080`. Its headless
mode is broken upstream in 7.3.1 - `mcp-reva` imports a `reva` module that ships
in neither the extension nor the PyPI package, and it pins an `mcp` SDK old enough
that `streamablehttp_client` has since been renamed.

## Where a replacement starts

1. Drive vrad3 ourselves and record per-stage timings on a REAL ZE map, so the
   optimisation target is measured rather than assumed. At probe scale the median
   filter already costs more than the trace.
2. Write the EXR differ, and check determinism with Valve's own
   `determinism_check_record` / `_replay`.
3. Then a tracer behind the same I/O contract, scored per luxel against Valve's
   own EXRs - direct light first, then bounce, then the AHD encode.
