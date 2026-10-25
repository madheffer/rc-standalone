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

| module | doc | state (2026-10-22) |
|---|---|---|
| containers and packaging | [CONTAINERS.md](CONTAINERS.md), [RESOURCES.md](RESOURCES.md) | 98%: every container authored; KV3 v5, VPK and the map root (.vmap_c) byte for byte on all 40 Valve references; Hammer's Only Entities preset whole-package exact |
| geometry | [GEOMETRY.md](GEOMETRY.md) | 99%: probe01's -world package equals Valve's but a per-compile stamp; ze_hold_em_p world 100% |
| visibility | [VISIBILITY.md](VISIBILITY.md) | 100% of the deterministic build: byte-identical VXVS from the .vmap on five maps incl. atixref-sized s2c_prefabprobe4 and split hints (2026-10-23); `-oldvis` is not reproducible in Valve's own tool |
| physics | [PHYSICS.md](PHYSICS.md) | 98%: world_physics and entity models whole-file exact on every map with a compile, incl. cables and deformers placed through instances and prefabs (ledgers 128-133); ragdolls in the settle under way: both joint types' solver functions oracle-exact, every motor mode (ledgers 135, 136, 138), next the joint wrapper, islands and the aggregate build (PHYSICS.md roadmap); left besides: instances inside prefab maps for entity models, render meshes |
| entity lump | [ENTITIES.md](ENTITIES.md), [SETTLE.md](SETTLE.md) | 99.9%: every lump of every pinned map and all 15 prefab, instance and deformer probes exact; the 14 prefab corpus maps against fresh Valve references (2026-10-23) differ only in Valve's run-to-run settle poses; left: two orders read from output only (GROUND_TRUTH 46) |
| baked outputs | [BAKED.md](BAKED.md), [CUBEMAPS.md](CUBEMAPS.md) | 80%: lightmaps, probes, far field, fog, nav (markups and round shapes included, ledgers 142-143), bomb damage and .vmap_c done; cubemaps in progress |
| own lighting (vrad3 port) | [VRAD3.md](VRAD3.md), [LIGHTING_ROADMAP.md](LIGHTING_ROADMAP.md) | 94%: `compile-map -s2cbake` bakes with no vrad3; ze_hold_em_p 99.90% of bytes |

The tracker artifact (https://claude.ai/artifact/4whctxfaTdHEiE4d3oAVHA)
carries the same state per item.

## Running things

- **The suite:** `dotnet test tests/Source2.Compiler.Tests` takes several
  minutes. Long or diagnostic cases are env-gated, for example
  `VISBUILD_BIG=1` and `PVS=<map>`. Game-dependent tests skip without CS2.
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

1. **Cubemaps, the face render** ([CUBEMAPS.md](CUBEMAPS.md), ledgers 117-121, 137-140, 144; the
   ordered plan to the array from the .vmap is CUBEMAPS.md "Roadmap"):
   - The state at the build (ledger 144, CUBEMAPS.md roadmap 0a-0e): the
     game's own build is not reproducible as is. probe01 is with a settle
     (`run_probe.py --settle 20`; an unsettled warm run samples finer mips);
     atixref differs on every cube run to run (foliage and moving entities
     follow time; pausing does not help). Scope is 1:1 with Valve for animated
     maps too, never static-only: find what the build's state depends on, pin
     it in cubehost so a Valve build repeats, port the time-dependent inputs
     and the texture residency, and render our compile at the time that
     equals Valve's build.
   - atixref draw 2305: one half-float ulp on 38 pixels. Build the pipeline
     from one-stage libraries (VK_EXT_graphics_pipeline_library) as the game
     does; the first attempt crashed with an access violation in our code.
   - The per-view culling: the face views are done (ledger 139, every
     face-view draw list of probe01, s2c_l5probe and atixref), the sun
     cascades too (ledger 140, every cascade list), and the shadowed light's
     views of atixref given their captured frusta (ledger 144, 96 lists).
     Left: those lights' view setup from their sub-frusta, LOD cross-fades, the per-view PVS test,
     and the generic culling of non-aggregate scene objects (42 atixref
     draws).
   - The light binner's inputs from our own lights, then all six faces of a
     cube. The readback, QuarterSize, the EXR strip and the array are exact
     against the game from the captured readbacks (ledger 137).
   - atixref cube 0's readbacks miss the game's EXR on one mesh's material
     (ledger 137): run atixref's buildcubemaps twice without RenderDoc (CS2
     and Steam closed) and compare the EXRs to see whether Valve's own output
     varies there.
   - probe_classes crashes the engine on load (its package is missing entity
     models): recompile it, then capture its rect lights.
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
4. **Nav** (LIGHTING_ROADMAP L9, L24): markups (L23) and spheres and
   capsules (L22) done 10-25 (ledgers 142, 143). Left: nav links, func_ladder
   ladders, movable meshes, markups in instances or prefabs, the NONAV blocks
   collector (180f1f830: prop_deformable, block_base_nav_mesh), L24 (one area
   connection s2c_navround's saved mesh drops), the CS analysis' casts and
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

## Latest session (2026-10-25, nav markups and round shapes)

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





## Earlier session (2026-10-22/23, ended for a CS2 update)

- First after the update: `tools/re/patch_check.py` then `build_diff.py`; the
  10-15 addresses in tools/re/capture_argdeps.py (WRITER_RVA, RECORDER_RVA)
  and the vis captures' hooks may move.
- .vmap_c: argument rows in read order then MSVC std::sort (ledger 119); all 40
  Valve roots exact; Hammer's Only Entities preset whole-package exact (ParityReport
  PARITY_BASE). Tool: tools/re/capture_argdeps.py (Frida on resourcecompiler).
- Entity lumps: the test allows no light or baked-key difference; the prefab probes
  run again from the addon's packages; precomputed_vis_clusters only on lights still
  directlight 3 after the shadow slot step (ledger 122); LUMP_FLATBOXES=<capture>
  checks lights against Valve's own cluster boxes.
- Visibility (2026-10-23, later): the five merge passes exact from Valve's input, then
  s2c_prefabprobe4 byte-identical end to end from Valve's .rte and from the .vmap (113 min
  on the PC); split hints exact on the new s2c_vishints; -oldvis measured non-deterministic
  in Valve's tool, -updateloshints inert on a stock build (ledger 127).
- Machines: run on this PC (Ryzen); the dev box only when the user says the PC is busy.
- Full suite last run 984/984 before the lump-test tightening; the lump, vis and
  .vmap_c tests touched since pass (33/33 and the prefab probes).

## Previous session (2026-10-22)

- Cascade inputs traced to the binaries and settled by a Frida capture in
  cubehost (`cs2_closed_session.py --cascades`): 560 is
  csm_max_shadow_dist_override from the video shadow-quality preset, 75 the
  render pipeline's cascade-0 attribute (ledger 121).
- Cubemap face 0 runs end to end on our own constant blocks from cleared
  targets, bit for bit on the RTX 4070 (`ChainedFaceEqualsTheCapture`).
- Two more cubemap captures: s2c_l5probe (face 0 bit for bit, lights and push
  constants) and atixref (335 of 336 actions; three replay defects found by
  comparing draws in RenderDoc on both sides, ledger 121).
- .vmap_c: Hammer's Fast and Only Entities presets byte for byte against
  Valve compiles (ledger 119).
- The aggregate culling located in scenesystem (decompiles in
  tools/re/out/culling).
- Full suite on the PC: 984 of 984 (after the .vmap_c row order and Only Entities fixes).
- New tools: tools/cubehost/cascade_sources.js and cascade_summary.py,
  run_probe.py --frida, compile_fresh.py --over, rdc_postvs.py,
  rdc_debugpixel.py, rdc_pixelhistory.py, rdc_bindings.py, rdc_events.py,
  rdc_targets_before.py, rdc_pipeline_create.py; tests RenderDocInApp
  (CUBEREPLAY_RENDERDOC) and SpvGlslProbe; CubemapFaceReplayTests switches
  CUBEREPLAY_START, _SAVE, _LOOSE, _WHITE, _ARGS, _PATCHCB, _ALSO.
- Captures and dumps (not in git): %TEMP%/cubehost_run/{probe01,
  s2c_l5probe, atixref}, %TEMP%/preset_fast, %TEMP%/preset_entities.

Pre-cleanup notes (the old VIS.md lab notebook, RE_NOTES, MAP_RESOURCES and
the rest) are in git history at `c2dc317`.
