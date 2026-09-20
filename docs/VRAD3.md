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

### lightmap_info.dat is one CHART ID per luxel

Settled from the binary, not inferred. `lightmap_load_block_gpu`'s own log line is
`"Copy block chart IDs to lightmap image... "`, and the loader's seek arithmetic
decompiles to:

```c
blocksAcross = ceil(atlasWidth / blockSize);
seek = (blockX * blockSize + blockY * atlasWidth) * blockSize * 4;   // bytes
// then read blockWidth * blockHeight u32s, clipped at the atlas edges
```

So the file is the atlas stored BLOCK by block in block-row-major order, each
block `blockSize * blockSize` u32s. For the shipping configuration - a 4096 atlas
in 1024 blocks - that is 4 MiB per block and 64 MiB total, and block 1 starts at
4 MiB while block 4 starts at 16 MiB. The file's size matches exactly.

Per luxel, the value is a chart id, or `0xFFFFFFFF` for a luxel that belongs to no
chart (71.1% of the probe map's atlas is covered). A valid id indexes a **28-byte
chart record** - the table that comes out of `lightmap_packing_geometry.dat` - and
the loader copies two flag bits out of bits 28 and 29 of that record's dword at
+0x18 into the per-luxel state, which is 12 bytes wide with a flags byte at +8
whose bit 0 means "has a chart".

The chart record's own layout is the next thing to pin down, and the header of a
packing geometry file reads `0, 40, 434, 896.0f, 992.0f` - which is not yet a
consistent story with a 28-byte stride, so it wants the decompiler rather than
another guess.

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

## Measured: where the time actually goes

Three jobs on the same machine (RTX 4070, 15 threads), all of them a single
4096x4096 atlas in 16 blocks, profiled with `tools/vrad3/profile_vrad3.py`:

| stage | probe01 (15 meshes) | ze_hold_em_p (87) | ze_doom_p2 (3,289) |
|---|---|---|---|
| **median filter 7x7** | **7.73 s** | **7.86 s** | **8.24 s** |
| light probe volumes | 0.08 s (2) | 0.73 s (8) | **4.36 s (39)** |
| GPU block compute | 0.87 s (16) | 1.22 s (16) | 2.52 s (16) |
| seam weld | 0.04 s | 0.55 s | 0.99 s |
| JFA dilate | 0.31 s | 0.35 s | 0.42 s |
| measured total | 9.58 s | 11.48 s | 17.60 s |
| wall clock | 18.3 s | 21.2 s | 36.8 s |

Three things fall out of that, and none of them was the expectation:

**The median filter is a FIXED cost.** 7.73 to 8.24 seconds across a 200x range of
scene complexity, because it is a 7x7 pass over the whole atlas and the atlas is
4096x4096 either way. On the small maps it is 70-80% of the measured time. It is
CPU work on an image, which is embarrassingly parallel and a poor fit for where it
currently runs.

**Light probe volumes are the fastest-growing term.** 0.08 to 0.73 to 4.36 seconds
for 2, 8 and 39 volumes - roughly linear in volume count, and already 23% of
ze_doom_p2. A big map with hundreds of them is the first place to look for a
compile that runs for hours.

**The path trace is not the bottleneck at this scale.** 0.87 to 2.52 seconds for
the actual GPU tracing. Building the ray trace scene for 3,289 meshes took 0.31 s.

Also: wall clock is about double the sum of the timed stages, so roughly half of
vrad3's runtime is in things it does not time - startup, Vulkan init, and writing
five ~134 MB uncompressed EXRs.

**What this does NOT establish** is why a big ZE map is reported to take most of a
day. Every map here fits one atlas page and none took a minute. The two candidates
visible in the numbers are atlas pages (each multiplying both the trace and the
fixed CPU passes) and probe volume count. Settling it needs a big map profiled the
same way, which is now a 20 second measurement once the map builds.

## Compiling a community port at all

`resourcecompiler` treats a missing material as fatal
(`content_consider_missing_materials_fatal`), and a ported map names materials
whose sources nobody has.

**Check the references BEFORE compiling.** resourcecompiler validates materials
late: on ze_ffvii_mako_reactor_v6_p it spent **22 minutes** preprocessing lights,
voxelising and building visibility, and only then stopped at the first mesh whose
material was missing. Every stub round pays that 22 minutes again, and there were
232 materials to find. `tools/vrad3/check_references.py` reads the `.vmap`
directly and answers the same question in seconds:

```bash
python check_references.py <addon> <map>          # what cannot resolve
python check_references.py <addon> <map> --stub   # and write the placeholders
```

A reference resolves if it is a source under the addon's content, a compiled
resource under the addon's game directory, or a compiled resource in an archive
the game mounts - and that last part means EVERY archive, not just `game/csgo`:
the tool materials a map is full of (`toolsnodraw`, `toolstrigger`) live in
`game/core`, and reading only csgo reports them missing on every map ever made.

`stub_missing_materials.py` does the same job from a compile log, for when a
compile has already failed and the log is what you have.

The other trap: the stub must be COMPILED before the map. A stub sitting in the
content tree is invisible to the map build, which resolves materials as compiled
resources - the error stays "referencing missing material" and looks like the stub
did not work. Compile the materials first, then the map:

```bash
resourcecompiler -nop4 -f -game <game/csgo> -i "<addon content>/materials/*.vmat" -r
resourcecompiler -nop4 -f -game <game/csgo> -i "<addon content>/maps/<name>.vmap"
```

ze_hold_em_p took three stubs and then compiled in 50 seconds.

**A missing MODEL is not fatal, and that is worse in one way**: the compile
proceeds and the prop is simply absent from the ray trace scene. Mako Reactor
names 282 models nobody has the sources for - custom FFVII content that exists
only inside its published workshop VPK - so any lighting or timing measured from
that compile is a lower bound, with occluders and bounce surfaces missing. The fix
is to extract the compiled models from the workshop VPK into the addon's game
directory, where resourcecompiler resolves them like any other compiled resource.

## Where a replacement starts

1. Drive vrad3 ourselves and record per-stage timings on a REAL ZE map, so the
   optimisation target is measured rather than assumed. At probe scale the median
   filter already costs more than the trace.
2. Write the EXR differ, and check determinism with Valve's own
   `determinism_check_record` / `_replay`.
3. Then a tracer behind the same I/O contract, scored per luxel against Valve's
   own EXRs - direct light first, then bounce, then the AHD encode.
