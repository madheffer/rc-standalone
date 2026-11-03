# Steam Audio

What resourcecompiler's `-sareverb`, `-sapaths` and `-sacustomdata` write, how Valve's bake runs, and how far
the port has got. Ledger: [GROUND_TRUTH.md](GROUND_TRUTH.md) 170 to 172. Started 2026-10-28.

## The outputs

| file | when | format |
|---|---|---|
| `maps/<map>.sareverb` | `-sareverb`, worldspawn `steamaudio_reverb_rebake_option` Auto (2) | binary KV3 v5, uncompressed |
| `maps/<map>.sapaths` | `-sapaths`, `steamaudio_pathing_rebake_option` Auto | binary KV3 v5, LZ4 |

Both sit in the map's .vpk beside the `.vmap_c`. The current build (1023) writes KV3; ar_pool_day, baked long
ago, ships the older `SABD` binary files. Kv3V5Writer re-encodes both of Valve's KV3 files byte for byte.

The `.sareverb` root (CSteamAudioBakedReverbData::SetKV3FromProbeBatch): `m_nBands` (3), `m_scene`, `m_grid`
(bounds, spacing, counts, line segments, probes: all empty for an Automatic bake), `m_reverbSettings` (rays,
bounces, IR duration, ambisonics order, export scene), `m_reverbClusteringSettings`,
`m_reverbCompressionSettings` (off, quality 0.95), `m_vecClusterForProbe`, `m_compressedData`,
`m_compressedClusteredData`, `m_movables`, and `m_pProbeBatch`: phonon's own serialized `ProbeBatch`
(FlatBuffers, `probe_batch.fbs` in the Steam Audio source). For s2c_saprobe that blob is 354,388 bytes, almost
all of it energy fields: per probe 4 channels (order 1) x 3 bands x 100 bins (1 s at 10 ms).

The `.sapaths` root: `m_nBands`, `m_probes.m_pProbeBatch` (the pathing layer: visibility graph and paths),
`m_movables`.

## Valve's bake

resourcecompiler hands the build to steamaudio.dll (CSteamAudioScene::BakeReverb, BakePathData), which calls
phonon.dll: Steam Audio core 4.9.0, a Valve build with APIs the public source (4.8.1) does not have
(iplPartitioner, iplProbeClusterMap, iplProbeArrayResize, ...). The exports are thin vtable thunks, folded so
that several names share one address; a capture names a call by the steamaudio.dll import slot it goes through.

The reverb bake, as captured on s2c_saprobe (tools/steamaudio/capture_phonon_api.py):

1. The context (steamaudio 180063f8f): version 0x40900, SIMD level 3, and the variables
   enable_dc_correction_for_phase_interpolation, enable_iir_order_switching, override_reconstruction_type
   (true), precached_ambisonics_order (3), enable_optix_verbose_logging (true); an Embree device (settings: one
   reserved byte).
2. The probe scene (180084d90 -> 1800852a7): an Embree scene (type 1) with one static mesh. For s2c_saprobe:
   206 vertices in metres (Source inches x 0.0254, axes moved), 280 triangles with the winding reversed from the
   source list, every triangle on material 0, and the whole 164-entry material table (absorption x3,
   scattering, transmission x3). The same scene is built again for each bake.
3. Probes: iplProbeArrayGenerateProbes (UNIFORMFLOOR, spacing 6, height 1.5, a box transform from the map's
   bounds) gave 73 probes; steamaudio filters them ("inside shapes", exclusion volumes, nav mesh) and puts
   the survivors in a fresh probe array, which goes into a new probe batch.
4. The bake (180087e90): iplReflectionsBakerBake with a 0x80-byte block: scene, batch, scene type (1, or 4
   with a GPU device), identifier (reflections, reverb, no sphere), flags 3, rays (worldspawn, 32768), diffuse
   samples (2048), bounces (32), simulated and saved duration (IR duration, 1), order (1), threads
   (`-sareverb_threads`), ray batch 1, irradiance min distance (a convar, 1.0), bake batch 1, 0.01 and 1 at
   0x68. ground_reflection_min_angle 0 and disable_specular_shadow_rays false are set around it.
5. iplProbeBatchSave into a serialized object, whose bytes become `m_pProbeBatch`.

The pathing bake builds the scene again, adds the probes one by one (iplProbeBatchAddProbe) and calls
iplPathBakerBake: identifier (pathing, dynamic), 1 visibility sample, radius 0, threshold 0.1, vis range
infinite, path range 100, 8 threads.

### Valve's reverb is not reproducible

Three Valve compiles of the same map give three different `.sareverb` files (values off by up to ~10%); the
`.sapaths` is the same every time. The reverb rays come from phonon's Embree simulator, whose ISPC jobs seed an
Embree RandomSampler from `clock()`, i.e. `rdtsc` (phonon 0923: 16 sites in 1801f5e48..180249a08, two job
kernels per ISPC target; the low 32 bits go straight into a murmur mix). `capture_sareverb.py` pins them:

- pinned, `-sareverb_threads 1`: two compiles byte-identical;
- pinned, 8 threads: still different (each thread sums its own energy fields; mostly last-bit differences,
  up to ~4% in a few values), so the multithreaded bake depends on scheduling as well.

So parity for the reverb means: one thread and the same seed. Valve's ordinary output (a fresh rdtsc per job,
several threads) is not data any compile can repeat, Valve's own included.

## The port so far

The user's call (2026-10-28): port all of it, phonon's simulator included, so the code is ours and runs on
Windows and Linux; re-read each CS2 update. Nothing here calls phonon.dll; the replay below is only the oracle.

Ported and exact:

- `SteamAudio/FlatBufferBuilder.cs`: the FlatBuffers C++ builder's layout rules (back to front, PreAlign,
  vtable dedup oldest first, defaults left out, Finish's alignment).
- `SteamAudio/ProbeBatch.cs`: the serialized probe batch, read and written in phonon's serialize order and the
  generated Create functions' field order. SteamAudioProbeBatchTests: every Valve `.sareverb` and `.sapaths` blob
  in the refs (14 files: reverb with 73 energy fields and reverb times, pathing with 73 visibility lists, 51 sound
  paths, 2628 refs) re-written byte for byte.
- `SteamAudio/SteamAudioFiles.cs`: the two KV3 roots (schema tables 180325d60 and the ones it nests; the blob by
  180071240) and the reverb settings (18006bc60: code defaults, gameinfo ReverbDefaults, worldspawn). Both
  whole files equal Valve's given the batch.
- Embree's ray tests: Embree 4.4.0 (phonon's static copy prints "Embree v4.04.00"). For phonon's scenes
  (`rtcSetSceneBuildQuality HIGH`, `RTC_SCENE_FLAG_DYNAMIC`, not robust) Scene::createTriangleAccel picks
  BVH8Triangle4 with the Möller intersectors; Triangle4 keeps v0, e1 = v0 - v1, e2 = v2 - v0 and Ng =
  cross(e2, e1), dot and cross are FMA chains (vec3.h), rcp is rcpps plus one FMA Newton step. On the AVX2 path
  the packet test (MoellerTrumboreIntersectorK: W = absDen - U - V) and the single-ray one (U + V <= absDen)
  both reproduce every captured lane: SteamAudioRayProbe over 20000 rtcIntersect8 and 20000 rtcOccluded8 calls
  (tools/steamaudio/capture_rays.py on the replay), 75,874 hit lanes with t, u, v, Ng and primID bit for bit, and
  55,854 occlusion lanes, by brute force over the mesh, no exact ties. The BVH only matters when two triangles give
  the same t; such a tie is where the traversal order would decide, so the port must count and report them.
- The simulator tables (`SteamAudio/Sampling.cs`): generateSphereSamples (180a58220) and the hemisphere ones,
  with phonon's Math::kPi = 3.141592f (2 kPi = 0x40c90fd8) and the CRT cosf / sinf it calls (182683c40,
  182685260, ported on their FMA path in `CrtMath.cs`: sinf's cosine branch fuses 1 - x^2/2, cosf's does not),
  and the order-1 SH coefficients (Google's hardcoded terms in double on (-z, -x, y)). Equal to phonon's tables
  (SteamAudioSimulatorTests, tools/steamaudio/capture_tables.py: 98,304 + 6,144 + 131,072 values).
- The reverb kernel (`SteamAudio/ReflectionSimulator.cs`): the AVX2 simulateEnergyField (180201a50, chosen by
  the ISPC dispatcher 1801f50a0 at ISA level 4; SSE2 / SSE4 / AVX CPUs run other kernels and other numbers),
  ported from its disassembly: 8-lane gangs, the gang-uniform RandomSampler, ISPC's rsqrt and pow, every FMA
  order, and 4.9's changes from the public source (listener-radius cut on bounce 0 only, no sphere tests after
  it, no irradiance-distance cut, a per-field bin duration and sub-bin count). The job lambda (180afeff0) sums the
  threads' fields after the last job. SteamAudioReverbBakeTests: 72 of s2c_saprobe's 73 probes give Valve's
  energy field bit for bit. Probe 65 differs in 60 of 1200 values because of one exact tie: a ray through the
  edge shared by triangles 166 and 169 (equal t), where Embree's BVH traversal order picks the triangle. Ties are
  counted; resolving them needs Embree's BVH8 FastSpatialSAH build and its hybrid packet traversal.
- The reverb times (`SteamAudio/ReverbEstimator.cs`): reverbTime (180a84df0, no FMA) with the CRT expf and
  log10f FMA paths (1827a2450, 1827a2700, table driven; tables copied from the DLL into `CrtMath.cs`), the default
  air absorption (0.0002, 0.0017, 0.0182, captured) and the bake's maxss with 0.1 (180a8c936). SteamAudioReverbTimeTests:
  all 219 times of every Valve .sareverb in the refs from Valve's energy fields.
- The automatic probes (`SteamAudio/ProbeGeneration.cs`): resourcecompiler's box from the acoustic geometry's
  bounds (180100e40), steamaudio's box matrix (18006b2d0) and unit conversions (1800635d0 / 1800636b0 axes,
  18006b260 and 180063600 scales), phonon's transposed matrix (180ab3a20) and inlined uniform-floor generator
  (180a86090) with its single-ray closest hit (180afba80). SteamAudioProbeGenerationTests: Valve's 73 bake probes
  bit for bit from the captured generation scene and box.
- The material table (`SteamAudio/AcousticMaterials.cs`): CSurfacePropDataTable's parse (180128710: defaults
  from 180084a40, a base's entry copied in list order, then the txt entry by name via 180084720) and the scene
  build's reduction (180084d90: octave bands 0, 7, 9 from the indices set at 180063adc; scattering summed in order
  over 11). SteamAudioMaterialTests: all 164 entries of Valve's scene. The surface order is surfaceproperties.vsurf's
  list order (a base later in the list is copied while still a default); that order is confirmed by the match,
  not yet read from the surface property system's code.
- The acoustic geometry (`SteamAudio/AcousticScene.cs`, ledger 171): resourcecompiler's collector walks the map
  depth first (180f53f40) and takes each map mesh's **physics shapes** (PhysMesh_Shapes, the settle's port, through
  `SettleWorld.MeshShapes`), not its faces: hulls fanned (1819589f0), meshes as their vertices and triangles
  (1819cea20), grouped by (surface index, InteractsAs, collision group), moved by the node's (translation, longest
  column, quaternion) matrix (1801feb70, 1812651a0), appended under a layer mask (180f510d0): 0x1400 (blocksound,
  window) for the bake's scene, 0x3400 (and passbullets) for the probe generation's. Owner rules (solid 0,
  steamaudio_geometry_mode, solidity 2) as 180f53780 has them. steamaudio's build (180084d90) converts to metres
  and reverses each triangle; nothing welds. SteamAudioSceneTests: s2c_saprobe's scene equals Valve's four captured
  scenes bit for bit, and the probe box and the 73 probes follow from it with no capture input.
- The pathing bake (`SteamAudio/PathBaker.cs`, ledger 172): phonon 4.9's BakedPathData (180a978e0) from its
  disassembly: the visibility graph (one rtcOccluded1 per probe pair, `TriangleScene.Occluded1`), Dijkstra per
  probe on MSVC's heap with 4.9's straight-distance range test, the sorted unique paths, the refs, and the CRT's
  atan2f for the turning angles (`CrtMath.Atan2`, equal to phonon's on 200,000 oracle pairs,
  tools/steamaudio/oracle_atan2.py). SteamAudioSceneTests: s2c_saprobe's whole .sapaths from the .vmap alone equals
  Valve's, byte for byte.
- End to end (`SteamAudioSceneTests.SaprobeReverbFromTheMap`, SAREVERB_FULL=1): the whole s2c_saprobe.sareverb
  from the .vmap alone equals Valve's seed-0, one-thread file except probe 65's tie (swapped in, the file is then
  byte for byte Valve's).
- The inside-shapes filter (`SteamAudio/ProbeFilters.cs`, ledger 173): a radius-0 query per probe on the compile's
  physics world (the nav world), vphysics2's broadphase walk, collision filter, body frame and per-shape tests
  from their disassembly. s2c_sainside (floating mesh, hull and closed-mesh boxes,
  tools/coverage/steamaudio_inside_specimen.py): the 73 probes left of 75 equal Valve's bit for bit.
- rcpps is the CPU's own approximation: Intel and AMD differ, so the bake is reproducible per CPU family (as
  Valve's is).

Tools: `tools/coverage/steamaudio_specimen.py`, `tools/steamaudio/capture_sareverb.py` (Valve's compile, seeds
pinned), `capture_phonon_api.py` (every phonon call with inputs), `replay_phonon.py` (the oracle: Valve's bake in
our process, seed 0, equal to Valve's pinned file), `capture_rays.py` (Embree's packet answers), `capture_tables.py`
(the kernel's tables and structs at its first call).

Valve references: `%TEMP%/steamaudio_refs` (`s2c_saprobe_api0`: seed 0, one thread, with the API capture;
`rays_saprobe.bin`; `s2c_saprobe_t1_seed1`; the 8-thread and unpinned runs; ar_pool_day's SABD files). phonon
0923 and steamaudio 1023 are in the Ghidra project (revaq aliases `ph`, `sa`).

## Status (2026-10-28, end of day)

From the .vmap alone, on the specimens, with no Valve input but the game content:

| piece | state | checked by |
|---|---|---|
| acoustic scene (both masks) | exact | SteamAudioSceneTests.SaprobeSceneMatchesValve (s2c_saprobe, s2c_sainside) |
| material table | exact | SteamAudioMaterialTests |
| automatic probes + inside-shapes filter | exact | SteamAudioSceneTests.ProbesFromTheMap (73 of 73; 73 of 75) |
| reverb kernel, reverb times | 72 of 73 probes exact (probe 65: Embree tie) | SteamAudioReverbBakeTests, SaprobeReverbFromTheMap (SAREVERB_FULL=1, 90 s) |
| whole `.sareverb` | exact with probe 65 swapped in | SaprobeReverbFromTheMap |
| pathing bake, whole `.sapaths` | exact | SaprobePathsFromTheMap |
| CRT atan2f | exact on 200,000 oracle pairs | SteamAudioAtan2Tests |
| KV3 files, probe batch | exact (14 Valve files round-trip) | SteamAudioProbeBatchTests |

Refs in `%TEMP%/steamaudio_refs`: `s2c_saprobe_api0`, `s2c_sainside_api0` (seed 0, one thread, with pathing;
recapture with `tools/steamaudio/capture_phonon_api.py s2c_rc_probe <map> <dir> --seed 0 --paths`, CS2 closed),
`tables_saprobe`, `atan2f_oracle.bin` (`tools/steamaudio/oracle_atan2.py`). Specimens: `steamaudio_specimen.py`
(s2c_saprobe from probe01), `steamaudio_inside_specimen.py` (s2c_sainside).

Nothing runs in compile-map yet: `-sareverb` / `-sapaths` are still refused there. Recommended order for what is
left: the nav coverage appender (item 2: the stream's state is captured and pinnable, ledger 186),
the entity-driven filters and scene refusals (items 1, 2), compile-map (item 4), then Embree's tie order (item 5).

## What is left

In order of the plan (the user's call, 10-28: the rest of the pipeline first, Embree's tie order after):

1. **The rest of the scene from the .vmap** (map meshes in the world are done, ledger 171): instances and
   prefabs (180f88460's instance paths), entity models and smart props (180f52220), paths and cables (180f519d0's
   CMapPath branch, the settle's FromCable), faces with tools.steamaudiogeometry (180f54020: 18139f930's 3 points
   per triangle, unwelded, grouped by surface), a mesh's own steamaudioGeometryMode (where the .vmap stores the
   embedded property), hidden nodes, the physics interface's layers for a default-group shape with no
   InteractsAs; then more maps, one with a passbullets-only shape to tell the two masks apart.
2. **The probe filters** (resourcecompiler 180100e40, in this order): nav mesh (180fbea90, when
   `steamaudio_reverb_filter_navmesh`), ladders (180fbc630), missing nav areas (180fbcb10), exclusion volumes
   (180fbde60, when `..._filter_volumes`), citadel ziplines (180fbba50), inside shapes (180fbe450, **ported**,
   ledger 173), probes placed in the map (180fbc2d0), snd_opvar_set_point probes (180fbd800). The missing nav areas
   appender runs on every compile with a nav mesh (its convar registers on) and samples big areas with tier0's
   global RandomFloat. That stream is time-seeded, so Valve's appender varies run to run (ledger 186);
   capture_randomstream.py --pin SEED reseeds it at the appender's entry and gives a reproducible reference.
   s2c_sanavcover appends 2 (the same under any seed); a specimen where a random point survives is still needed. Also the other generation types (probe generation volumes, manual) and
   the height-layer variant of 180087940 (the convar at 18035cd30), the max-probes limits and clustering.
3. **Pathing's remaining cases**: sampled visibility (numSamples > 1 with a radius; the sphere volume samples), the
   4.9 flag at +0x40, coincident probes, and the KV3 conversion of worldspawn choices keys (generation type, rebake option). The
   pathing settings reader (18006c1b0) is ported (`PathingSettings.From`, gameinfo PathingDefaults then worldspawn).
4. **compile-map**: the rebake options are read (ledger 198: Cleanup and Manual drop the baked file, Manual
   logged as deprecated; only Auto bakes), and at Auto `SteamAudioBuild` bakes the reverb and pathing (ledger
   199; s2c_saprobe's .sapaths byte for byte through it). Left: the reverb run through it against the pinned
   reference (SaprobeReverbFromTheBuilder, SAREVERB_FULL=1), custom data, `-sareverb_threads` (more than one
   thread is not reproducible in Valve's build either: refuse or document), the seed (Valve's is rdtsc; ours
   is fixed), packing both files into the map .vpk; then a whole-package parity check against a Valve compile
   with seeds pinned (capture_sareverb.py).
5. **Embree's tie order**: its BVH8 FastSpatialSAH build and hybrid packet traversal (`BVH8Triangle4Intersector8
   HybridMoeller`), for rays that hit two triangles at exactly the same t (s2c_saprobe probe 65, 60 of 1200
   values; SteamAudioReverbBakeTests reports such probes instead of failing).
6. **Smaller open items**: the hemisphere sample generator's own code (its table matches, the function is not
   located); the surface property system's order (the vsurf list order matches, not read from code); the
   non-FMA CRT paths and the SSE2 / SSE4 / AVX ISPC kernels (refused; Valve's numbers differ on those CPUs);
   ambisonics orders above 1; the KV3 conversions between value types for worldspawn keys; directivity callbacks;
   long-range CRT argument reduction; NaN / denormal paths.
7. **`-sacustomdata`** (occlusion, dimensions, materials), reverb compression and clustering, `-steamaudio_gpu`.
   Read 2026-11-03 (rc 0923): BuildSACustomOcclusion (180103b10, compilemap.cpp) names the output
   `<map>.custom.occlusion` (steamaudio vf 0x148 / 0x150), applies the rebake option as the other builders do
   (Manual logged as deprecated, taken as Cleanup), reads `sacustomdata_probes_spacing` and
   `sacustomdata_probes_height` (int, defaults from its argument) and `sacustombake_threads`, gathers probes
   through the shared probe helper (180100e40 with probe type 2, as CleanupBakedDataIfNeeded's "custom data"),
   then has steamaudio.dll make a baker (vf 0x70; "Could not create scene") and bake "%d x %d probe pairs" of
   occlusion through its vf 0x38 with a progress callback (180101da0), `sabakestrictmode`, and the result's size
   logged before it is written (vf 0x1a8). Not read: steamaudio.dll's occlusion baker (vf 0x38 of the object vf
   0x70 returns) and the file's layout. A Valve reference needs only a standalone resourcecompiler run
   (`compile_fresh.py ... -- -world -sacustomdata`), which may run with CS2 open.
