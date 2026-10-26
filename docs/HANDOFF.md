# Handoff: the map compile port

Start here, cold. The goal is Valve's CS2 map compile (resourcecompiler and
its builders) ported to C#, with output matching Valve's bit for bit. Zombie
escape maps are the target. Work happens on branch `research/map-authoring`,
remote `box`.

## Rules

- **Authoritative is law.** Only decompiled or measured facts. Anything
  inferred, assumed or fitted to output goes into
  [GROUND_TRUTH.md](GROUND_TRUTH.md) and is settled from the DLLs
  ([REVERSING.md](REVERSING.md)). Never write an unread rule as fact.
- **Full coverage.** Every entity class Hammer offers and every compile
  option Hammer or resourcecompiler offers is in scope
  ([COVERAGE.md](COVERAGE.md)).
- **VAC safety.** Never attach to or read cs2.exe. Game tests use `-insecure`,
  and only after asking the user.
- **Compiles and captures only while CS2 is closed.**
  - Check `tasklist` and run the watchdog, which kills resourcecompiler if
    cs2.exe appears.
  - Back up any package a compile overwrites, restore it after and `cmp`
    it.
  - Delete crash dumps after reading them, and keep 10 GB free on D:.
- **Agents and processes.** No subagents. Kill every process you start once
  it is not needed: dotnet build servers, testhosts, ReVa, python.
- **Style.** Commits end with the co-author line; push to `box` only when
  asked. No em dashes. Comment blocks are at most 20 lines.
- **Addresses live in `tools/re/names/make_manual.py`**, from which
  [ADDRESSES.md](ADDRESSES.md) is generated. Run `tools/re/patch_check.py`
  after every CS2 patch.
- **After a big task**, re-read each ported function beside Valve's
  decompile.

## The compile, and where each part is

The `-world` phase (`CWorldRendererBuilder::Build`), in order:
1. The output folders and the `default_ents` lump.
2. The scene build, then the entity template lumps.
3. The static prop models.
4. The ray trace environment (`.rte`), then visibility (visbuilder.dll).
5. Light vis membership and precomputed shadows.
6. World nodes (`CompileAndSaveNodes`: render clusters and mesh splits by
   vis cluster).
7. The vrad3 scene and the LPV atlas.
8. The entity lumps, with the physics settle before them.

A full compile (no `-fshallow`) then builds world physics (physicsbuilder),
baked lighting, nav and cubemaps.

```
map loading ─┬─ mesh export ─┬─ trace scene ── visibility ─┬─ world nodes ── lighting, probes, cubemaps
             │               ├─ brush hulls ───────────────┤
             │               └─ world collision ───────────┼─ settle ── entity lump
             └─ static props ─┘                            └─ nav
```

| module | doc | state (2026-10-26) |
|---|---|---|
| containers and packaging | [CONTAINERS.md](CONTAINERS.md), [RESOURCES.md](RESOURCES.md) | 98%: every container authored; KV3 v5, VPK and the map root (.vmap_c) byte for byte on all 40 Valve references; Hammer's Only Entities preset whole-package exact |
| geometry | [GEOMETRY.md](GEOMETRY.md) | 99%: probe01's -world package equals Valve's but a per-compile stamp; ze_hold_em_p world 100% |
| visibility | [VISIBILITY.md](VISIBILITY.md) | 100% of the deterministic build: byte-identical VXVS from the .vmap on five maps incl. atixref-sized s2c_prefabprobe4 and split hints (2026-10-23); `-oldvis` is not reproducible in Valve's own tool |
| physics | [PHYSICS.md](PHYSICS.md) | 98%: world_physics and entity models whole-file exact on every map with a compile, incl. cables and deformers placed through instances and prefabs (ledgers 128-133); ragdolls in the settle under way: both joint types' solver functions oracle-exact, every motor mode (ledgers 135, 136, 138), next the joint wrapper, islands and the aggregate build (PHYSICS.md roadmap); left besides: instances inside prefab maps for entity models, render meshes |
| entity lump | [ENTITIES.md](ENTITIES.md), [SETTLE.md](SETTLE.md) | 99.9%: every lump of every pinned map and all 15 prefab, instance and deformer probes exact; the 14 prefab corpus maps against fresh Valve references (2026-10-23) differ only in Valve's run-to-run settle poses; left: two orders read from output only (GROUND_TRUTH 46) |
| baked outputs | [BAKED.md](BAKED.md), [CUBEMAPS.md](CUBEMAPS.md) | 82%: lightmaps, probes, far field, fog, nav (markups and round shapes, ledgers 142-143, 146), bomb damage and .vmap_c done; cubemaps: three cubes of two maps (probe01, s2c_l5probe) byte for byte with every block, buffer, texture, sampler, layout and pipeline state ours (ledgers 147-154); the draw list and its arguments still from the capture |
| own lighting (vrad3 port) | [VRAD3.md](VRAD3.md), [LIGHTING_ROADMAP.md](LIGHTING_ROADMAP.md) | 94%: `compile-map -s2cbake` bakes with no vrad3; ze_hold_em_p 99.90% of bytes |

The tracker artifact (https://claude.ai/artifact/4whctxfaTdHEiE4d3oAVHA)
carries the same state per item.

## Running things

- **The suite:** `dotnet test tests/Source2.Compiler.Tests` takes about 40 minutes now. Last full run:
  1055 of 1055 on 2026-10-26 (37 minutes, after the map-root/subasset blocker, ledger 155). Long or
  diagnostic cases are env-gated, for example `VISBUILD_BIG=1` and `PVS=<map>`. Game-dependent tests skip
  without CS2.
- **Test data:**
  - Valve compiles of test maps: `MapFixtures.RcCompiledLump(s)` compiles
    into the scratch addon `s2c_rc_probe`.
  - Probe maps live in `content/csgo_addons/s2probe/maps/`.
  - Full-compile ground truths (Mako's takes about 21 minutes) are kept
    under `%TEMP%/gt/`.
- **Per module:** each doc's "Tests and tools" or "Running and scoring"
  section.
- **Decompiling:** start ReVa (`tools/re/reva_serve.py`), then run
  `python tools/re/dec.py <dll> <addr>`.
- **In game:** `tools/pipeline/map_test.py`, with the workshop tools,
  `-insecure` and netcon.

## Roadmap: what is left, in the order worth taking it

**The 10-26 blocker is closed (ledger 155).** `MapRootFileTests.MapRootEqualsValves` and
`MapSubassetsTests.ReferencesEqualValves` pass on all 54 s2c_rc_probe maps. There were three causes. The test read
the capture tools' partial builds as full ones (it now takes the builders from the package's outputs). A
force_hidden node and its subtree are dropped from the subasset table, which is now ported (MapEntities). Five
references were stale against their .vmap and were recompiled (old packages in `%TEMP%/refresh_1026/old`). A
capture tool restores the package it moved aside, so **after regenerating a probe .vmap, recompile its
reference** (`tools/pipeline/compile_fresh.py` with the switches it was built with, then copy the result into
the addon).

1. **Cubemaps** ([CUBEMAPS.md](CUBEMAPS.md) "Whole cubes from our blocks", "A second map", "The pipelines",
   "The textures", "The buffers", "The binner's items" and "Roadmap", ledgers 117-121, 137-140, 144, 147-154).
   Where it stands: three cubes from two maps (probe01's two, s2c_l5probe's cube 0) give the game's EXRs byte
   for byte, and probe01's whole env_cubemap_array.vtex_c is the game's (`ChainedCubeMakesTheGamesExr`). What
   comes from our data in that render: every constant block (generic's per-material `_Globals_` included),
   the binner's item records and edges, every texture, vertex and index buffer, sampler, vertex layout and
   fixed-function state of the world, sky and downsample draws. Still from the RenderDoc dumps: the list
   of draws and dispatches (which passes and draws, their order), the draw arguments (our culling gives the
   same commands, ledgers 139, 140), viewport and scissor rectangles, dispatch sizes and push constants, and
   the spawned pawn's viewmodel and AO box on s2c_l5probe. Next, in order:
   - the draw list from our data: the passes and their order, the layer and primitive order within a pass
     (CUBEMAPS.md roadmap 4; 16-bit sort key from scenesystem 180041a00's +0x20), our culling's commands as
     the arguments, viewports from the views, then probe01 rendered with no dump at all;
   - s2c_l5probe's cube 1 (needs a capture: `run_probe.py --renderdoc 1 --settle 20` for s2c_l5probe, CS2
     and Steam closed, ask the user) for its whole array; decide which build state our compile renders at
     (scripted camera or not, a spawned pawn and its viewmodel or not: roadmap 0e, ledger 149);
   - animated maps (CUBEMAPS.md roadmap 0a-0e, ledger 144): atixref differs run to run in Valve's own build,
     so pin its time in cubehost first; scope is 1:1 there too, never static-only;
   - smaller: the binner's light and environment-map items (none binned in the captured views), the
     viewmodel's draws, shadowed lights' own view setup (ledger 144), LOD cross-fades, the per-view PVS test,
     non-aggregate scene objects (42 atixref draws), atixref draw 2305's one-ulp pipeline-library issue,
     probe_classes' engine crash on load.
   Recipes: CUBEMAPS.md (dump a face: rdc_replay.py with RDC_DUMP=1 per face range from the readback eids;
   the passes: rdc_passes.py into the capture dir; run all three cubes: see "A second map"; capture a cube:
   `run_probe.py --renderdoc <n> --settle 20`, CS2 and Steam closed, ask the user).
2. **Ragdolls in the settle** ([PHYSICS.md](PHYSICS.md) "Aggregates in the settle", ledgers 135, 136,
   138): props whose model has several physics parts or joints (the CT agents of s2c_ragdollprobe)
   settle through their joints and write boneTransforms; the settle refuses them today. Done: both
   joint types' solver functions, oracle-exact. Left, in order: the joint wrapper and joints in the
   island solver, joints in the island manager, the aggregate build from the model, lockstep against
   the capture, the boneTransforms write-back. Valve's truth capture recipe is in PHYSICS.md and
   tools/re/out/settle_notes/notes_joints.md.
3. **.vmap_c**: done (ledger 119). Every Valve root byte for byte (25 lit,
   15 unlit), and Hammer's Only Entities preset whole-package exact. Left:
   the context's +0x605 flag, unread.
   - The 10-23 "reopened by 1.41.9.2" was our tooling, not Valve
     (2026-10-25): compile_fresh.py added its `-world -fshallow` default to
     the Only Entities switches. Recompiled on 1.41.9.2 with the switches
     alone over the addon's lit packages, Valve's build has 8 children as
     on 10-22, and ours equals it whole-package on probe01 and cardtest
     (ParityReport with PARITY_BASE, 25/25 and 27/27 files);
     UnlitMapRootsEqualValves passes. %TEMP%/preset_entities holds the new
     references; the wrong ones are in preset_entities_1023_withworld.
4. **Nav** (LIGHTING_ROADMAP L9): markups (L23), spheres and capsules (L22)
   and the analysis' overlap removal (L24) done 10-25 (ledgers 142, 143, 146). Left: nav links, func_ladder
   ladders, movable meshes, markups in instances or prefabs, the NONAV blocks
   collector (180f1f830: prop_deformable, block_base_nav_mesh), the CS analysis' casts and
   LadderConnect's rays against round shapes, bomb damage over hull, sphere or
   capsule worlds (L10).
5. **Own lighting:** OIDN for quality 1 and 2 end to end, more maps through
   `-s2cbake` (L16), the prefab world order (L20), AMD.
6. **Smaller baked outputs:** precomputed shadows, occlusion data (de_ maps;
   where it is made is not traced), Steam Audio (no workshop map has it).
7. **Geometry:** heap-ordered prop aggregate parts (Valve varies run to run),
   subdivided entries' tangents, overlay tangents, detail grass.
8. **Visibility:** done for parity (2026-10-23, ledger 127): s2c_prefabprobe4 byte-identical
   from the .vmap end to end, split hints exact on s2c_vishints, `-updateloshints` a no-op on
   a stock build. Left is speed (the PVS scan is ~90 of the 113 min on prefabprobe4), which
   waits for the user's OK for anything that could change bytes.
9. **Entities:** done as measured (2026-10-23): prefab layouts (probes 7 to 12, the instance rounds
   read in MapDoc_BakeInstances and simulated) and the trace scene's inverse (translation summed as
   18125ade0 does), which closed the last light keys on c2m2 and coaster. Against fresh Valve
   references (%TEMP%/fresh_corpus) only the settle poses Valve itself varies remain. Left: reading
   the prefab document order in the light gather and the node copy behind the key flip (GROUND_TRUTH 46).
10. **Containers and resources:** compiling the addon content a map
   references, a package made only of our outputs; glTF export, model
   internals, shaders.
11. **Map scripts (user, 2026-10-23), once visibility is 100%:** compile cs_script
    sources (.js/.ts) to `.vjs_c` ourselves; today resourcecompiler does it and the
    service only checks the file exists. The 1.41.9.2 update changed the compiler
    (new "Panorama Compiler Conditionals Fingerprint", every shipped `.vjs_c`
    rebuilt) and server.dll's script API; first get a pre-update `.vjs_c` to see
    what changed, and Valve's fresh `cs_script_demo` compile as the reference.

## Latest session (2026-10-26 later, a second map and the draws' inputs from our data)

Commits 8c48703..b83223d (ledgers 149-154), every step checked on probe01's two cubes and s2c_l5probe's
cube 0 (all six readbacks and the game's EXR byte for byte each time; probe01's whole array too).

- s2c_l5probe faces 1-5 dumped; its cube matches with: no scripted camera in that build (cascade 0 is the plain
  split's; 180cb2100 answers from UI state), render pass clears from the capture (tools/cubehost/rdc_passes.py
  writes passes.jsonl), blending and the AO target's UNORM view in the replay, the viewmodel's own blocks kept.
- Decompiled and ported: the visible light probe volumes' order (priority = indoor_outdoor_level descending,
  box volume ascending), the binner's item records and edge lines (Cubemaps.LightBinnerItems, scenesystem
  1801aac90 and helpers; 10-23's .rdata moved by 0x30, so read data tables through the 10-23 code).
- From our data in the render: generic's `_Globals_` per material, every texture (lightmaps, materials, 1 x 1
  defaults; cookie and env-map arrays unread), every vertex and index buffer (package models, scenesystem's sky
  cube, the downsample quad), the samplers (CubemapFaceSamplers), the vertex layouts (CubemapFaceVertexLayout)
  and the fixed-function state (CubemapFacePipelines).
- Run all three cubes: CUBEFACES / CUBEREPLAY_SPIRV / CUBEREADBACK / CUBEREADBACK_EXR / CUBEREPLAY_CUBE per
  capture, CUBEREPLAY_SCRIPTEDCAMERA=0 for s2c_l5probe; about 2 to 3 minutes per cube on the RTX 4070.

## Earlier session (2026-10-26, whole cubemaps from our blocks)

probe01's cubemap array from our render, byte for byte (commits 3df5182, 30915c7).

- CUBEMAPS.md "Whole cubes from our blocks": probe01's cubes rendered on our device, all six faces chained from
  cleared targets, with every constant block and buffer from our data (blocks found by role, not by face 0's
  hashes). Each face equals the game's readback, and the six give the game's EXR byte for byte
  (`ChainedCubeMakesTheGamesExr`); our two EXRs build the game's whole env_cubemap_array.vtex_c. Cube 1 was
  captured 10-26 (Steam closed by the user). Each face's light block lists the probe volumes in its frustum
  (GROUND_TRUTH 147: measured on all twelve faces, the gather not read yet).
- The 10-23 update left the settled probe01 build unchanged (both EXRs equal 10-21's); the compiled array is 48
  bytes smaller and `CubemapArrayVtex.Build` already writes it. cubehost picks its RenderDoc convar address by
  client.dll's PE time stamp (10-23: 1823806d8), and its csproj no longer compiles spvmatch's sources.
- Captures and dumps: %TEMP%/cubehost_run/probe01 (cube 0, replay_face0..5) and probe01_cube1.
- Later 10-26 (ledger 148): the light binner's CullParams_t (scenesystem 1801b2850 / 1801a8150), the sky's
  g_matSkyWsToOs (180155117) and pixel $Globals, and the depth downsamples' ratios (client HalfResDepth /
  QuarterResDepth layers) from our data, each equal to the capture word for word on all twelve faces.

## Earlier session (2026-10-25, the nav analysis' overlapping connections)

- L24 closed (ledger 146, Maps/NavGen/NavMeshOverlaps.cs): CSNavMesh::Analyze's shared step (server.dll
  1814cfaa0) removes overlapping connections per edge (1814b86f0 over 1814ada50's intervals) before the AI
  data; NavWriter.Build runs it, so the .nav and NavAnalysis see Valve's mesh. s2c_navround's .nav is Valve's
  up to the custom data from both capture runs (NAVWRITER_SUFFIX=_full is the run its package came from), and
  NavGenPipelineTests no longer excuses it. One-way connections only go on the unsaved incoming lists.

## Earlier session (2026-10-25, nav markups and round shapes)

- Lightmaps: BakeLighting's atlas side caps at worldspawn's max_lightmap_resolution (the builder's
  +0x2f8, not a fixed 4096) and the world node's uv snap takes the halved side (ledger 141); review
  F12 was already fixed for the query data on 10-18.
- Nav markups (L23, ledger 142, Maps/NavGen/NavGenMarkups.cs): collector, NONAV extraction filters,
  stages 003 / 009 / 019 and WALKABLESEED; s2c_navmarkup Valve's in its whole .nav, custom data and
  bomb damage, probe_classes through 029 (it was refused).
- Spheres and capsules in the nav world (L22, ledger 143): tessellation and box casts;
  s2c_navround Valve's through 029, all 525 round-shape casts bit for bit. Its .nav drops one
  connection Valve's 029 blocks hold (L24, open).
- Captures: capture_navgen.py picks the resourcecompiler offsets by the installed build (20261015 and
  20261023; navsystem and vphysics2 code are unchanged in 10-23) and dumps the markup tag table and the
  NONAV stage arguments; capture_navcast.py dumps sphere and capsule data and holds the process exit
  until its records are flushed. A map whose package is up to date makes resourcecompiler skip the
  nav: delete the .vpk first.
- COVERAGE.md's compile options and class rows refreshed (they predated the bake and vis work).
- Node angles (ledger 145): every .vmap read now holds its nodes' angles as resourcecompiler's load
  setter leaves them (|a| < 0.001 to 0, a negative yaw plus 360); s2c_navangles' markups and lump are
  Valve's.

## Earlier session (2026-10-25)

- .vmap_c: the 10-23 "Only Entities reopened by 1.41.9.2" was compile_fresh.py adding
  `-world -fshallow` to the switches (fixed); fresh references in %TEMP%/preset_entities match
  whole-package on probe01 and cardtest.
- Ragdolls: CRnRevoluteJoint ported (ledger 136; ctm_sas's knees are type 3, flags 0, listed by
  ModelPhysicsProbe), the conical spring motor and plasticity ported (ledger 138); the conical
  joint's shared pieces live in Simulation/JointRows.cs. RevoluteJointOracleTests and
  JointSolverOracleTests: 11 tests, 20,000 random cases each, against vphysics2 0924.
- Lessons: Ghidra misprints float association and constants in these SSE functions (the hinge
  rotate, the position k sums, a copysign of 2.0 shown as 1); read clamps and singular tests
  (comiss/jbe, maxss/minss) from the disassembly, and let the oracle decide.
- A cubemap session shares this checkout (ledger 137 is theirs); check the ledger number before
  adding one.

Older session logs (10-22 and before) are in git history; their facts live in the module docs.
Pre-cleanup notes (the old VIS.md lab notebook, RE_NOTES, MAP_RESOURCES and
the rest) are in git history at `c2dc317`.
