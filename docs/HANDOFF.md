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
- **What passing means where Valve varies (user, 2026-10-30).** Where Valve's
  own tool gives different bytes run to run (settle poses, animated maps'
  cubemaps, prop aggregate part order, `-oldvis`, `.vwrld`'s stamp and
  fingerprint), our output passes when it is within Valve's variance:
  - compile the map with Valve's tool at least twice, the same switches;
  - every byte the Valve runs agree on must be ours exactly;
  - where they disagree, ours must be a value Valve's own runs produce
    (for a pose or an order: equal to one of the runs; for a cubemap
    pixel: inside the range the runs span).
  Everything Valve reproduces stays bit for bit. Steam Audio's reverb seed
  is rdtsc in Valve's build, so its references are captured with the seed
  pinned (docs/STEAM_AUDIO.md) and ours must equal those.
- **VAC safety.** Never attach to or read cs2.exe. Game tests use `-insecure`,
  and only after asking the user.
- **What waits for CS2 closed is what VAC could see (user, 2026-11-02).**
  Frida on any process, cubehost and RenderDoc, anything touching cs2.exe,
  and the -insecure game launch wait for CS2 (and for cubehost Steam)
  closed. Jobs that are only heavy run with the game open at below-normal
  priority through `tools/re/heavy.py run --allow-cs2`: our own s2c
  builds, compiles and tests, and resourcecompiler run on its own
  (`compile_fresh.py --cs2-open`, `full_compile_test.py --cs2-open`).
  [CS2_CLOSED_QUEUE.md](CS2_CLOSED_QUEUE.md) sorts each item.
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

| module | doc | state (2026-10-28) |
|---|---|---|
| containers and packaging | [CONTAINERS.md](CONTAINERS.md), [RESOURCES.md](RESOURCES.md) | 98%: every container authored; KV3 v5, VPK and the map root (.vmap_c) byte for byte on all 40 Valve references; Hammer's Only Entities preset whole-package exact |
| geometry | [GEOMETRY.md](GEOMETRY.md) | 99%: probe01's -world package equals Valve's but a per-compile stamp; ze_hold_em_p world 100% |
| visibility | [VISIBILITY.md](VISIBILITY.md) | 100% of the deterministic build: byte-identical VXVS from the .vmap on five maps incl. atixref-sized s2c_prefabprobe4 and split hints (2026-10-23); `-oldvis` is not reproducible in Valve's own tool |
| physics | [PHYSICS.md](PHYSICS.md) | 98%: world_physics and entity models whole-file exact on every map with a compile, incl. cables and deformers placed through instances and prefabs (ledgers 128-133); ragdolls in the settle: joints, islands, the aggregate build, the whole stepped settle and the boneTransforms write-back exact on s2c_ragdollprobe (ledgers 135-168), next the corpus maps with ragdolls (PHYSICS.md roadmap); left besides: instances inside prefab maps for entity models, render meshes |
| entity lump | [ENTITIES.md](ENTITIES.md), [SETTLE.md](SETTLE.md) | 99.9%: every lump of every pinned map and all 15 prefab, instance and deformer probes exact; the 14 prefab corpus maps against fresh Valve references (2026-10-23) differ only in Valve's run-to-run settle poses; left: two orders read from output only (GROUND_TRUTH 46) |
| baked outputs | [BAKED.md](BAKED.md), [CUBEMAPS.md](CUBEMAPS.md), [NAV.md](NAV.md) | 85%: lightmaps, probes, far field, fog, nav (markups and round shapes, ledgers 142-143, 146), bomb damage and .vmap_c done; cubemaps: probe01's two cubes and whole array rendered with no RenderDoc dump, byte for byte (ledger 162); the world draw list and layer selection equal on probe01, s2c_l5probe and atixref; next the scene's inputs from the package |
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

1. **Cubemaps** ([CUBEMAPS.md](CUBEMAPS.md) "Whole cubes from our blocks", "A second map", "The draw list",
   "What the chained cube still takes from the dump", "A face with no dump", "Roadmap"; ledgers 117-121, 137-140,
   144, 147-157, 159, 162).
   Where it stands (2026-10-27 evening, ledger 162): **a face renders with no dump.** `CubemapFaceRenderer`
   (Source2.Compiler.Gpu) builds the whole frame from our data: the package, the shader and content packages, the
   blocks (`CubemapFaceBlocks`) and the draw list (`CubemapFaceDrawList` with memberships and `MaterialOrder`).
   Each binding is named by the program's own .vcs write sequence, the binner's dispatch sizes come from the code
   (scenesystem 1801b2850, rendersystemvulkan 180072850), and the passes and clears are the captures'.
   `CubemapFaceRendererTests.CubeWithNoDumpMakesTheGamesExr`: probe01's twelve faces equal the readbacks, both EXRs
   and the whole env_cubemap_array.vtex_c are the game's byte for byte (7 s a cube); every program loaded is a module
   of the capture. s2c_l5probe: five faces exact, -X differs by the pawn's viewmodel (not ported). The replay
   (`ChainedCubeMakesTheGamesExr`) stays as the test that compares each action and block. **Next, in order:**
   - Done later 10-27 (ledger 163): the scene from the package (`CubemapFaceBlocks.FromPackage`) and each draw's
     programs by its shader's render modes. probe01 still exact.
   - Done: `CubemapFaceProgramsTests` passes on all three captures (atixref 256 world draws; ledger 163).
   - Done: the instance records (`CubemapInstanceRecordsTests`: atixref's 401 instances bit for bit).
   - **The full to-do list is CUBEMAPS.md "What is left (2026-10-27 evening)"**: A, atixref face 0 against its
     readback (probe-lit inputs, the sky's D_FOG_TYPE writer, fog with a sky material, non-aggregate objects,
     shadowed lights' tiles, the binner with lights, then the render and texture streaming); B, s2c_l5probe's -X
     viewmodel and cube 1; C, moving the test helpers into the library and compile-map rendering the cubes; D,
     animated maps; E, the measured rules that still want their decompile.
   - Remaining from ledgers 157 / 159 (not needed by the three maps so far): 1800f3b10's later per-object
     conditions (culling box +0xc18, object size against the split, LOD fade), CPVS's octree walk (engine2
     180238170 -> 180237ce0 / 1802378f0) for the fragment's 0x20 bit on worlds with vis data, the cull's +0xc fade
     bit's writer, the layer +0x6dc branch.
   - s2c_l5probe's cube 1: CAPTURED 2026-10-30 (`%TEMP%/cubehost_run/s2c_l5probe_cube1b`, settle 20, 1.39 GB
     .rdc, 78 passes, summary.txt beside it); not yet compared against our render. decide the build state our compile renders at (scripted camera, spawned pawn and viewmodel:
     roadmap 0e, ledger 149).
   - Animated maps (roadmap 0a-0e, ledgers 144, 187): pinning is mostly done (CUBEMAPS.md D: time_pin.js takes
     two runs from 651,642 differing pixels to 135,760). Next: localise the last 135,760 (a pixel diff of
     cubehost_run/atixref_srt_1 against _srt_2), then port the pinned inputs. Scope is 1:1 there too.
   - Smaller: the binner's light and environment-map items, the viewmodel's draws, shadowed lights' views,
     non-aggregate scene objects (194 atixref calls), atixref draw 2305's one-ulp pipeline-library issue,
     probe_classes' engine crash on load.
   Recipes: CUBEMAPS.md (run the three cubes: "A second map"; capture a cube: `run_probe.py --renderdoc <n>
   --settle 20`). Frida agents in tools/cubehost (our own cubehost process, never cs2.exe; CS2 and Steam
   closed): layer_sort.js (sort keys, CMaterial2 creation), layer_members.js (layer tests, material stores,
   records; check with layer_members_check.py), material_store.js and watch_word.js (hardware watchpoints),
   caller_of.js (one-shot hardware breakpoint backtrace). Hardware watchpoints during map load can stop the map
   loading; atixref with an agent needs `--timeout 1500`. Layer objects are reused across views and frames, so
   pair logged records by address and fields.
2. **Ragdolls in the settle** ([PHYSICS.md](PHYSICS.md) "Aggregates in the settle", ledgers 135, 136,
   138, 158, 160, 161, 166): props whose model has several physics parts or joints (the CT agents of
   s2c_ragdollprobe) settle through their joints and write boneTransforms; the settle refuses them today.
   Done: both joint types, the joint stream, joints in the island manager, and the aggregate build with its
   pose (ledger 166, 2026-10-28: the reference pose with each bone's m_boneScaleParent; resourcecompiler
   never reads the animation graph's pose for it). AggregateBuildCaptureTests passes whole. RagdollLockstepTests
   (ledger 167): the whole settle, contacts included, equals Valve's on three captures in each capture's dynamic
   order (ledger 52). The write-back with boneTransforms is ported (ledger 168): s2c_ragdollprobe's whole lump
   equals Valve's, and so does s2c_ragdollprobe2's (ar_pool_day's mattresses, ledger 169). No other content map on
   disk has a ragdoll (AggregateScanTests). Left: meshes in an aggregate, the node's earlier pose entries (not stored
   in a .vmap), and PHYSICS step 5's other small refusals.
   Recipe and captures: PHYSICS.md step 6, tools/re/out/settle_notes/notes_joints.md.
3. **.vmap_c**: done (ledger 119). Every Valve root byte for byte (25 lit,
   15 unlit), and Hammer's Only Entities preset whole-package exact. Left:
   nothing: the context's +0x605 flag settled 11-03 (ledger 119, a bake without vis byte for byte).
   - The 10-23 "reopened by 1.41.9.2" was our tooling, not Valve
     (2026-10-25): compile_fresh.py added its `-world -fshallow` default to
     the Only Entities switches. Recompiled on 1.41.9.2 with the switches
     alone over the addon's lit packages, Valve's build has 8 children as
     on 10-22, and ours equals it whole-package on probe01 and cardtest
     (ParityReport with PARITY_BASE, 25/25 and 27/27 files);
     UnlitMapRootsEqualValves passes. %TEMP%/preset_entities holds the new
     references; the wrong ones are in preset_entities_1023_withworld.
4. **Nav** ([NAV.md](NAV.md), the inventory of every nav input and output the DLLs read and write, 2026-10-28; LIGHTING_ROADMAP L9): markups (L23), spheres and capsules (L22)
   and the analysis' overlap removal (L24) done 10-25 (ledgers 142, 143, 146); ladders and AddJumps' cylinder
   sweep done 10-28 (ledger 174: s2c_prefabprobe3's .nav Valve's up to the custom data); nav links done 10-28
   (ledger 175: s2c_navlink's whole .nav and its link entities Valve's). Left: the link's other .vmap keys
   (type, width, angles, movement) and links in instances or prefabs, a func_ladder in an instance or prefab, movable meshes, markups in instances or prefabs, the NONAV blocks
   collector (180f1f830: prop_deformable, block_base_nav_mesh), the CS analysis' casts and
   LadderConnect's rays against round shapes, bomb damage over hull, sphere or
   capsule worlds (L10).
5. **Own lighting:** OIDN for quality 1 and 2 end to end, more maps through
   `-s2cbake` (L16), the prefab world order (L20), AMD.
6. **Smaller baked outputs:** precomputed shadows, occlusion data (de_ maps;
   where it is made is not traced). Steam Audio is well along (docs/STEAM_AUDIO.md,
   ledgers 170-173): scene, materials, automatic probes, the inside-shapes filter,
   the pathing bake and the whole `.sapaths` are exact on s2c_saprobe; `.sareverb`
   is exact but for one Embree tie (probe 65). Left: the scene from instances,
   prefabs, entity models, cables and steamaudiogeometry faces; the other probe
   filters (the nav coverage appender's random stream is time-seeded in Valve's build; capture_randomstream.py --pin
   gives a pinned reference, ledger 186; a specimen where a random point survives is still needed);
   compile-map's `-sareverb` / `-sapaths` steps (still refused there); Embree's
   tie order; `-sacustomdata`.
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
11. **Map scripts (user, 2026-10-23), once visibility is 100%:** .js done 2026-11-02
    (ledger 200, ScriptAuthor); .ts settled 11-03 (ledger 200: a user's RC compiles no .ts, its
    typescript.js is not shipped; authors transpile to .js). A pre-update file left. Compile cs_script
    sources (.js/.ts) to `.vjs_c` ourselves; today resourcecompiler does it and the
    service only checks the file exists. The 1.41.9.2 update changed the compiler
    (new "Panorama Compiler Conditionals Fingerprint", every shipped `.vjs_c`
    rebuilt) and server.dll's script API; first get a pre-update `.vjs_c` to see
    what changed, and Valve's fresh `cs_script_demo` compile as the reference.
    1.41.9.3 (10-29) changed the Panorama preprocessor's quoted-string tokenizer
    (resourcecompiler 18011be50 in 20261029, same address in 20261023; "Reached end of
    stream without matching quote"): a new branch when the saved quote index is -1.
    It is the only real code change in that update's resourcecompiler.
12. **Speed, after parity (user, 2026-10-28):** nav and vis are the two big compile stages.
    Vis: the PVS scan is about 90 of the 113 min on s2c_prefabprobe4. Nav: our nav from
    s2c_prefabprobe3's .vmap (generation, ladders, the CS analysis) ran over an hour in a
    Debug test build; time a Release build and Valve's own step before sizing it. Any change
    that could alter bytes waits for the user's OK (parity before speed).

## Latest session (2026-11-04, CS2 and Steam closed: captures for ledgers 38 and 197, lighting checks)

- Ledger 38 closed (d2fe461): capture_subdivtangents.py showed the bake itself turns the flat meshes' -0 into +0;
  1813b82f0 writes lerp * weight + grid (1813c83f0's record). atixref: 103,887 of 103,887 corners.
- Lighting (ea32f68): tinted / lighting-only env_sky, uniform sky colour, QUAD / TUBE lights, lodlevel props.
  scene_info.kv3 byte for byte on six maps; -s2cbake lighting files Valve's on s2c_skytint, probe_classes and
  s2c_coloradjust (Valve's fresh compiles of the sky specimens and coloradjust are in %TEMP%/fresh_compile).
- Ledger 197 (68e77a7): capture_host.py on Valve's coloradjust job: vrad3 bakes csgo_environment's constant
  g_mTextureColorAdjust1 (the one the combo binds); RtxMaterials writes each field from its own VariableIndex.
- The default hit group opacity of translucent materials (8e5d8c3, 180098cd0): every atixref material block
  equals vrad3's captured one but for bindless indices (%TEMP%/hostcap_atixref).
- B.1: Valve's cube builds of coloradjust vary run to run on material seams; ours is inside the variance.
- compile-map --skip-unported skips bomb damage when it skipped the nav (45c2738); TJunctionFix's record walk
  indexed by x (byte-neutral, 4.6x on atixref's replay). atixref's -world stage still takes far longer than
  Valve's 24 minutes for the whole compile; the next profile should say where.
- Later (cc475d5): GPU-only hit groups for untranspiled rtx combos, combo reduction, SetByArtist defaults at the
  type's width (a real bug: float3 over defaulted scalars), VfxEval log / normalize. atixref bakes on the GPU but
  three of its four EXRs differ (OPEN.md section 6); probe01, cardtest, probe_classes, s2c_coloradjust and
  ze_hold_em_p re-baked byte for byte. Valve's atixref host capture: %TEMP%/hostcap_atixref.
- The three map-reference tests (MapRootFileTests, MapSubassetsTests, MapAssetsTests) fail at b14fb43 too: the
  s2c_dec_* / s2c_rtprefab* / s2c_sanavcover packages on disk, not today's code.
- Found, not ported: the particle snapshot (.vsnap) generator (OPEN.md 5.b10, addresses there); the missing-
  material rule 1.c5 still needs a capture design.

## Earlier session (2026-11-03, 1.41.9.5 patch check, mesh vertex streams, .vmap_c bake reads; CS2 open)

- 1.41.9.5: nothing to port (memory patch-check); cubehost knows the new client.dll stamp.
- Ledger 201: mesh paint streams and texcoord 1 from Valve's code (NodeMeshEntries.WithPaintStreams), and the
  vertex buffer meshopt rule (1802ed210 / 1802ed800 in rc 1103: the encoding kept only when smaller).
  s2c_coloradjust is 19 of 25. Open: italy_cobblestone_01's texcoord shift. materialsystem2 reads as "keep"
  (the attribute expressions are rewritten to indexed reads that see the declared defaults) and Valve's output
  says "shift"; our keep branch is used by only that corpus map. CS2_CLOSED_QUEUE A.7 captures it.
- Ledger 200: .ts settled (a user's resourcecompiler compiles none).
- Ledger 119: a bake without vis reads norte, dumptris and debugvisgeo (CWorldRendererBuilder_Build), and the
  bake decision reads bakelighting only in a partial build (Compile.Partial). Valve's probe01 with -world
  -bakelighting -fshallow (%TEMP%/bake_novis) equals ours byte for byte.
- Ledger 46: the datamodel's referrer list runs newest first (180cdf6f0), so 180f88460 visits placements in
  reverse order of when their referencing attribute was set. When that happens during the load is still open,
  so the lighting order keeps its fit.
- Read, not ported: nav collector 3 (NAV.md, needs 181352fb0 and 1818e0800 and a specimen) and Steam Audio's
  -sacustomdata (STEAM_AUDIO.md item 7). Valve's s2c_saprobe with -world -sacustomdata -fshallow wrote no
  .custom.occlusion (compile_fresh keeps no resourcecompiler output; why is not checked, perhaps the map's
  custom data rebake option): a specimen with that option on Auto comes first.

## Earlier session (2026-11-02 evening, the full compile's compile half, .vjs_c, the driver; CS2 open, below-normal)

- The user's rule: VAC, not load, is why the game closes, so heavy-only items ran now (Rules above).
- **Full compile of ze_hold_em_p** (`full_compile_test.py s2c_rc_probe ze_hold_em_p --cs2-open`, 12 min): against
  Valve's fresh compile on the same driver, 55 of 56 files byte for byte, 99.99%; only world.vwrld_c's compile
  stamp and per-compile fingerprint differ. The in-game half is CS2_CLOSED_QUEUE A.1.
- **The GPU driver moves the lightmaps.** NVIDIA 32.0.15.9579 to 32.0.16.1088 (2026-11-01 05:21): Valve's own
  irradiance moved by 70% of its blocks; ours equals Valve's on either driver (a 30192cf build gave the same
  counts, so no code regression). Lit references from before a driver update are stale: CS2_CLOSED_QUEUE part C;
  new-driver vrad3 references in %TEMP%/vrad3_refs/driver_32.0.16.1088. Tool: CpuBakeProbe.LightmapBlocksAgainstValve
  (PKGTEX=<valve vpk>|<our vpk>|<map>).
- **Ledger 200: .js to .vjs_c** (ScriptAuthor, `s2c compile <in.js>`): byte for byte with this install's RC on all
  seven cs_script_demo scripts. The shipped .vjs_c are Valve's build machine's (Panorama preprocessor present).
- Steam Audio reverb through the builder (ledger 199): only the Embree-tie probe 65 differs, as expected.

## Earlier session (2026-11-02 later, TextureAverageColor, Steam Audio's rebake rule, full builds from empty; CS2 open)

- Ledger 197: TextureAverageColor is the .vtex header's reflectivity (RenderDevice003 vf 0x2d8 reads the 16 bytes
  rendersystem copied from header +4). Both evaluators read it; ColorMatrices moved to Lighting/ and the bake's
  VfxEval (RtxMaterials) uses it too, so coloured tints and contrast other than 1 follow the disassembly in the
  bake as well (default materials unchanged word for word). 1,120 stock materials have a non-default colour
  adjustment (ColorMatrixTests.NonDefaultColorAdjust). Not measured yet: a capture of one of them.
- Ledger 198: Steam Audio's rebake options (CleanupBakedDataIfNeeded): Cleanup and Manual (deprecated, taken as
  Cleanup) drop the baked file and bake nothing; only Auto bakes. compile-map now does the same instead of refusing
  the three Steam Audio builders, and refuses only an Auto bake.
- compile-map: a full build (no builder named) starts from an empty package with -f / -fshallow2 or when there is
  no package yet; before, every build needed a package Valve had compiled.
- **The full compile test (needs CS2 closed: heavy, then the game).** Everything ze_hold_em_p needs is wired now:
  its Steam Audio is Manual (nothing to bake), lighting via -s2cbake, cubes via build-cubemaps.
  1. `s2c compile-map -i "<cs2>/content/csgo_addons/s2c_rc_probe/maps/ze_hold_em_p.vmap" -outroot <out> -s2cbake
     <out>/bake --gpu` (no builder named = a full build; vis included). Expect the 10-19 result: lit package 55 of
     56 files, 99.90%.
  2. `s2c build-cubemaps <out>/csgo_addons/s2c_rc_probe/maps/ze_hold_em_p.vpk --game "<cs2>/game"`.
  3. Score against Valve's: `PARITY=s2c_rc_probe|ze_hold_em_p|<cs2>/game/csgo_addons/s2c_cubeprobe/maps/ze_hold_em_p.vpk`
     ParityReport (or diff the two packages file by file).
  4. In game: copy our .vpk to game/csgo/maps before launch, `cs2.exe -tools -insecure -netconport 29000 -novid`,
     `tools/pipeline/map_test.py` (walk, triggers, look for missing lighting or magenta). Delete crash dumps after.
  Then the same on a second ZE map with props and doors (ze_atix: memory ze-focus).
- Later the same day (CS2 open): the Steam Audio Auto bake runs in compile-map (SteamAudio/SteamAudioBuild,
  ledger 199; s2c_saprobe's .sapaths through it byte for byte, the reverb run queued). **Everything that needs CS2
  closed is in [CS2_CLOSED_QUEUE.md](CS2_CLOSED_QUEUE.md)**, in order, with `tools/pipeline/full_compile_test.py`
  as item A.1 and the s2c_coloradjust specimen (tools/cubehost/coloradjust_specimen.py) made for A.3 / B.1.
- Ledger 46 partly read: the light gather runs light by light, each light's placements in the datamodel's
  referencing-element order (180f88460). **Next with CS2 open:** read that list's insertion order (g_pDataModel
  vf 0x3b8 / 0x3c0) and the +0x6b8 appender, then replace EntityLumpAuthor.Lighting's fitted order; then
  Steam Audio custom data (-sacustomdata, which also makes the .custom.occlusion of stock maps: 18013af00).

## Earlier session (2026-11-02, ze_hold_em_p's cubes nearly exact; CS2 and Steam closed)

- Ledger 195: texture residency is rendersystem's preload cap (the first mip with longer side <= 512), which the
  cube renderer now applies by default. The "probe01 keeps 2048 maps" premise was empty: probe01 and s2c_l5probe use
  no map over 512. Ruled out by cubehost runs: RenderDoc, the player's view (setpos onto the cube, 15 s),
  videocfg_texture_detail, the pool budget.
- Ledger 195: PerViewConstantBufferCsgo_t's bindless words named (+64 blue noise .. +104 fallback cube). +68, the
  BRDF lookup, is read unguarded by every csgo_ program; it is textures/dev/brdf_lut.vtex (the captured image byte
  for byte), now bound. ze cube 0 went from thousands of pixels over 1/256 on faces 0 and 1 to one.
- Ledger 196: materialsystem2's MatrixMultiply / MatrixColorTint2 / MatrixColorCorrect2 ported from the
  disassembly (Gpu/ColorMatrices.cs) and $Globals' 4 x 4 transposed. csgo_environment's $Globals are now the
  capture's word for word.
- **Where ze_hold_em_p stands:** against Valve's no-RenderDoc EXRs (cubehost_run/holdem_cvars/intermediates),
  faces 2 to 5 of all 8 cubes and the whole of cube 7 are byte for byte. Faces 0 and 1 of cubes 0 to 6 differ in 16
  to 34 pixels: the team intro's skinned agent models (CUBEMAPS.md F). probe01 still byte for byte, s2c_l5probe
  unchanged (-X viewmodel).
- Tools: tools/cubehost/exr_diff.py (per-face EXR diff), rdc_textures.py (every image's size and mips),
  rdc_texture_data.py (one image's bytes), spvmatch --expr (a variable's expression decompiled).
- Gotchas: holdem_rdc0's .rdc now fails to replay (VK_ERROR_INVALID_OPAQUE_CAPTURE_ADDRESS) though it replayed on
  11-01; the 11-02 probe01 capture replays. run_probe.py --renderdoc 0 makes no capture on probe01; use 1.
- **Next:** the agent models need the client's team intro scene at build time (which agents, their pose: roadmap 0e),
  a large item. Smaller and open: TextureAverageColor's source (render device vfunc 0x2d8) for a contrast other than
  1; a tinted or desaturated material capture for ColorMatrices; then another real ZE map through the same checks.

## Earlier session (2026-11-01, cubemaps on a lit ZE map: ze_hold_em_p; CS2 and Steam closed)

- CS2 1.41.9.4 (Oct 30) changed only client.dll and server.dll (patch report tools/re/patch_reports/20261101_1.41.9.4.md);
  every toolchain DLL and the shaders are the 10-29 builds. cubehost's ClientConvarGlobals has the new client
  (the RenderDoc convar global did not move). Valve's 10-27 builds of ze_hold_em_p's cubes 1 to 7 are still byte for
  byte what 1.41.9.4 builds.
- Picked as the weakest part: cubemaps (the largest file kind we cannot make for a real map: 100 of 132 workshop
  maps, 14% of their bytes). Target ze_hold_em_p, whose Valve build is reproducible (CUBEMAPS.md F).
- Ledger 192: the sunless and skyless scene, the light block's cull params and counts from the binner, the Csgo
  cube fog flag, and the csgo_ shaders' bindless lightmap indices (without them a sunless lit map renders black).
  ze face 4 now within 0.0072 of the game's readback; probe01 and s2c_l5probe unchanged.
- Ledger 193, the barn lights: records with baked shadow slots (218 captured pairs byte for byte), the lights'
  snapped bounds (37 boxes), the screen size (measured, 218) and fades (218); the sort, trim and binner item read.
  Then: each face's light list reproduced (all six), entries and records from the keys (every record the game's
  but shadow terms that reach no pixel), and the lights in the render (0db0f68): ze cube 0 pixels over 1/256 fell
  to 195,611 / 126,101 / 22,899 / 21,588 / 945 / 367,762. Later: the light items exact (6946e08), and the main
  remaining cause found: texture residency (ledger 194, the game's maps resident at 512 in this build; capped,
  faces 4 / 5 are within 1/256). Next: the residency rule (needs Steam closed for a probe01 re-capture), the
  Csgo block's bindless indices, entity models as scene objects (F.2), the AO proxies (F.3).
- **Resume here.** Captures in %TEMP%/cubehost_run: holdem_rdc0 (cube 0's .rdc, replay_face0 .. 5, readback/,
  spirv/, intermediates/ with the game's 8 EXRs), holdem_barn_io (218 barn light entry / record pairs),
  holdem_radius (the boxes 1800ce950 stores). The render against the readbacks, with W = holdem_rdc0 as a Windows
  path: `CUBENODUMP_VPK=W\game__csgo_addons__s2c_cubeprobe__maps__ze_hold_em_p.vpk CUBEREADBACK=W\readback
  CUBEREADBACK_EXR=W\intermediates\env_cubemap_1424340351.exr dotnet test --filter CubeWithNoDumpMakesTheGamesExr`
  (add S2C_CUBE_MAXTEXTURE=512 for the residency experiment, CUBENODUMP_DRAWS_OUT=<dir> for per-draw targets).
  Other checks: BarnLightFaceRecordsTests (CUBEBARNFACES), BinnerItemsProbe (CUBEITEMS), BarnLightFacesProbe
  (CUBELIGHTFACES_VPK), PackageBlocksAgainstTheCapture / PackageBlocksInTheCapturesDraws (CUBEBLOCKCMP). The
  regression pair: probe01 (cubehost_run/probe01, cube 0 byte for byte) and s2c_l5probe (CUBEREPLAY_SCRIPTEDCAMERA=0,
  five faces equal, -X the viewmodel). With CS2 open (cs2-open rules: decompiles and Idle offline tests only):
  read the residency rule in rendersystemvulkan 20261015 (r_texture_budget_dynamic, r_texture_budget_threshold,
  r_texture_stream_mip_bias) and client's videocfg_texture_detail, and map the Csgo block's +64 .. +128 indices to
  images from holdem_rdc0's dumps (the draws' bindless arrayElements). With CS2 and Steam closed: re-capture probe01
  (`run_probe.py --map probe01 --renderdoc 0 --settle 20 --keep-intermediates --out probe01_1104`) and check
  whether its 2048 maps are now resident at 512.

## Earlier session (2026-10-30 / 31, captures: Steam Audio's random stream, cube 1, atixref pinned; CS2 and Steam closed)

- HANDOFF "What passing means where Valve varies" (the user's rule) and Steam Audio's real state in HANDOFF and
  COVERAGE (it is well along on this branch, ledgers 170-173; it had been listed as not started).
- Steam Audio (ledger 186): tier0's global random stream is ran1 and time-seeded, so Valve's nav coverage
  appender varies run to run. tools/steamaudio/capture_randomstream.py logs every draw and `--pin SEED` reseeds
  the stream at the appender (two runs equal). tools/coverage/steamaudio_navcover_specimen.py makes s2c_sanavcover
  (a closed room plus a nav seed), which appends 2 probes, the same under any seed: the random points are still
  untested there.
- Cubemaps: s2c_l5probe's cube 1 captured (cubehost_run/s2c_l5probe_cube1b). cubehost.exe had to be rebuilt:
  the 10-29 client entry was in the source but not the exe (README note added).
- Cubemaps D (ledger 187, CUBEMAPS.md D): atixref's variance taken apart and mostly pinned by time_pin.js.
  RenderDoc captures and replay dumps of the pinned cube 37 are in cubehost_run/atixref_rdc37_1 and _2 (2 GB each).
- Tools: spvmatch `--glsl` (one combo decompiled with its blocks' members named, through VRF's
  ShaderSpirvReflection), ShaderPropsProbe, run_probe.py unloads the agent before quit.
- Crash dumps: every one this session was mine and deleted (D:/cs2dumps).

## Earlier session (2026-10-30 later, the barn light entry from the keys; CS2 open, static only)

- Cubemaps/BarnLightDescriptions.cs: a light_barn's 0x12c-byte description and transform from its entity keys
  (client 10-15 C_BarnLight vfunc 0x7d8, 180d81100), bit for bit on all 7 captured lights
  (BarnLightDescriptionsTests, CUBEBARN + CUBEBARN_VPK). The networked fields are the keys.
- client.dll's schema read statically (field entries {name, type, offset}); C_BarnLight's table in CUBEMAPS A.6.
- tools/re/token_names.py names attribute hashes; the entry's other fields are mapped (A.6); d (the fades' input)
  is the snapped bounds' screen size, checked numerically on all 64 entries.
- Ghidra: use /client_20261015.dll for the 10-15 build. /client.dll is another build: a script of mine rebuilt
  functions there by mistake around 180d8c0f0, then removed them and recreated the pdata-start functions.
- Captures queued for when CS2 and Steam are closed: none needed yet for the entry. Still open statically: the
  entry's shadow-pass fields (+0x174 .. +0x183, +0x1dc), +0x206 / +0x208, light_omni2's description.

## Earlier session (2026-10-30, the barn light record; CS2 and Steam closed)

Cubehost watch runs, each run's agent in tools/cubehost (CUBEMAPS.md A.4, A.6):
- base_records.js: the base scene object's instance record, all eight words (180079700).
- barn_records.js / barn_entries.js: why the first watch runs died (reading words under the agent's own
  read / write watchpoints; watchpoints left on destroyed lights, on a stack copy, on reallocated arrays) and the
  chain light -> gather 18019fe30 -> merge 1801a9410 -> upload 1801b2450 -> record 1801b14b0.
- barn_record_io.js: 64 (entry, record) pairs; Cubemaps/BarnLightRecords.cs reproduces all 64 bit for bit
  (BarnLightRecordsTests, CUBEBARN). Types 3 to 5, shadows and cookies are refused (none captured).
ReVa was down for the end of the session: the record's helpers were read with tools/re/disasm.py.
Next: the light entry from the package (18019fe30's fields, filled by client.dll from the entity).

## Earlier session (2026-10-29 night, atixref's gap mapped; CS2 open)

CUBEMAPS.md A.2, A.4, A.6 and the skybox view. Code: CubemapFaceReplayTests.PackageBlocksAgainstTheCapture (every
face block from the package against the capture, by role; BlockRoles fixed for atixref), MaterialProbe. Found:
- base objects draw through g_instanceBuffer like fragments (records decoded, w1 / w2 / w6 from 18007a540; the
  other words' writer needs a cubehost watchpoint, CS2 closed);
- atixref face 0 bins one barn light and environment maps (light block +308 .. +340, binner counts);
- the 3D skybox view comes from skybox_reference's map (game/csgo/maps/ze_atix_panic_new_skybox.vpk); its
  position rule is confirmed in client but the sky camera's runtime origin is not its lump origin;
- A.2: atixref's sky fog type 1 is the skybox map's env_sky fog_type (absent-key default 0).
Next with CS2 closed: watchpoint runs for the base object record and the barn light record.

## Earlier session (2026-10-29 night, A.4 inventory)

No code changed. CUBEMAPS.md A.4 "Inventory": the base-object draws atixref's faces carry (world node scene
objects, light blocker shadow meshes, prop models), the world node's 194 m_sceneObjects by type, and the entry
points for the base object's draw (scenesystem 1015 CBaseSceneObjectDesc vftable 1805e5d70, slot 8 18007d750 by
layout). Next: decompile that draw and its instance record.

## Earlier session (2026-10-29 night, Csgo indices; the device fault)

CUBEMAPS.md A.1 "Done: the three Csgo indices". The renderer writes our bindless indices at Csgo +72 / +76 / +80 and
binds a zero texture at element 0 of every bindless array: unbound element-0 reads had been losing the Vulkan
device on atixref non-deterministically. atixref now renders the same twice; the remaining gap starts with item A.4
(192 of 256 world draws on face 0). probe01 unchanged.

## Earlier session (2026-10-29 night, the probe record ported; atixref renders whole)

CUBEMAPS.md A.1, GROUND_TRUTH 185. Cubemaps/LightProbeVolumeRecords.cs ports client 1802819e0 for unbaked volumes:
all 400 of atixref face 0's records are the capture's but the indices (LightProbeVolumeRecordsTests). The renderer
binds the buffer, and atixref renders all six faces from no dump for the first time (not yet the game's: face 0
2.1 M of 4.2 M halves differ). probe01 unchanged. Next: the Csgo block's three bindless indices, then item A.4.

## Earlier session (2026-10-29 night, the probe record's writer found; CS2 and Steam closed)

cubehost runs on atixref with tools/cubehost/lpv_records.js (CUBEMAPS.md A.1): the probe volume record is built in
client.dll (1.41.9.3 1802819e0, the probe volume entity's update), not in scenesystem. The fields it writes are
listed from the disassembly; NOT PORTED yet. All 44 objects' records are dumped for the check. atixref's package in
the scratch addon was rewritten by the run and restored from a backup (cmp clean).

## Earlier session (2026-10-29 night, A.1 started; CS2 closed, Steam open)

No code changed; CUBEMAPS.md A.1 has what was read. csgo_lightmappedgeneric's probe-lit pixel program needs three
bindless indices in our Csgo block (+72 a cube, +76 a colour target, +80 depth) and the probe-volume buffer
(scenesystem 1801ba530: 400 records of 0x90, defaults from 1801ba3e0, one per scene object copied from its +0x130).
Where that per-object block is built is not found statically; a Frida probe on 1801baa00 in cubehost would settle it
(Steam must be closed for cubehost).

## Earlier session (2026-10-29 night, rasterizer and engine samplers; CS2 open)

CUBEMAPS.md A.0. A pixel program with no rasterizer descriptor takes D3D11's default (measured on six draws). The
engine's six bindless samplers are named in CubemapFaceSamplers.Engine at the game's own heap numbers, each confirmed
by tools/cubehost/cmp_globals.py: all 22 of our $Globals on atixref face 0 equal the capture's but the texture
indices. probe01 unchanged. atixref now stops at PerViewLightProbeVolumeConstantBuffer_t: item A.1, the probe-lit
inputs.

## Earlier session (2026-10-29 night, the bindless set; CS2 open)

CUBEMAPS.md A.0 "The bindless set is built". CubemapFaceRenderer binds set 4's bindless arrays with our own
indices, written where the programs read them ($Globals index variables). On atixref face 0 the foliage vertex
$Globals equal the capture's but the index itself, and the noise texture equals the capture's in all nine mips
(CubemapBindlessTests). probe01 unchanged. The render now stops on the alpha-tested foliage pixel program's missing
rasterizer state (pipeline state), then the wind's time (item D, needs CS2 closed).

## Earlier session (2026-10-29 night, last: atixref's cube from no dump; CS2 open)

No compile and no capture. CUBEMAPS.md A.0 has the detail, GROUND_TRUTH 184 the measured values.
- FromPackage reads an unbaked package (lightmap block 256 x 256, measured) and a light_environment without fade
  keys, which every Hammer-made CS2 map is (csgo.fgd remove_key; 0.1 / 0.05 measured). probe01 unchanged.
- atixref then stops on foliage in the cascades: csgo_foliage's vertex program samples a 3D noise texture through
  the bindless set (set 4 binding 29 sampler, binding 46 Texture3D), and the renderer has no bindless set. Its wind
  is time-driven, which ties A to D: pinning the time needs a capture with CS2 closed.
- Diagnostics: CubemapFaceReplayTests.BlocksOfTheCapture (blocks of a dump by binding), ShaderVariableProbe
  (a shader's variables with every field VRF parses).

## Earlier session (2026-10-29 night, later: AVOID decompiled, cubemaps C.1; CS2 open)

No compile and no capture.
- Nav collector 6 (AVOID) decompiled, not ported (NAV.md "AVOID from models"). The key is a MODEL's
  `nav_data.nav_attribute_avoid`, not a material's, and no CS2 FGD offers that block. The table the
  records come from, and its slot order, need a probe model and a capture.
- CUBEMAPS.md C.1 done: `Cubemaps/CubemapPackageAggregates.cs` holds the aggregate and layer-input readers
  the tests had. probe01's two cubes are still the game's from no dump. atixref's FlagsEqualTheCapture
  fails, and failed identically before the move (CUBEMAPS.md C.1 has the numbers).
- CUBEMAPS.md C.2 done on probe01: `s2c build-cubemaps <package>` (Gpu/CubemapBuild.cs) renders every cube and
  writes `maps/<map>/cubemaps/env_cubemap_array.vtex_c`. CubemapBuildTests: both EXRs, the .vtex and the array
  are the game's byte for byte (10-26 intermediates). Next: C.3, or A (atixref) so a second map builds.

## Earlier session (2026-10-29 night, the out-of-range normalise; CS2 open)

No compile and no capture: static disassembly of archived DLLs only.

- server 20261015 18146bed0 is rc's VectorNormalize_Slow (18125d000) instruction for instruction (constants 1.0
  and FLT_MAX read from the binary), so `VectorNormalizeSlow` serves both. Registered in make_manual.
- Lifted four refusals that only stood because that helper was unported: the nav analysis' Normalize
  (180398290 twice, 180391710's spot loop; vector only), the encounter path (180391a0c; the length is its
  return), ContainsXY's edge normal (1814c5cbe, on (ny, ex, 0)) and the ladder direction (rc 1818dc7cc; the
  length is its return). Each caller's use of the result was read from its disassembly.
- New: NavAnalysisTests.ContainsXYTakesTheSlowNormaliseOnATinyEdge. NavAnalysisTests (16) and the ladder
  tests pass. No reference map reaches the slow path, so it is checked by construction, not against a .nav.
- The suite was not run in full.

## Earlier session (2026-10-29 evening, the entity list's refusals)

Ledger 183; NAV.md "Order of work" 2. CS2 closed, Steam open (resourcecompiler captures only).

- capture_entitylist.py and capture_entitylist_trace.py hooked 10-23 addresses: the 1.41.9.3 build moved all of
  them by 0x40. Both now map them through rva_map.installed.
- tools/entities/entitylist_refusal_probes.py writes s2c_plainpf, s2c_aheadpf1, s2c_aheadpf2 (prefab maps) and
  s2c_hidepf, s2c_pfintarget, s2c_aheadprobe (captured with `--bake-only`; no packages built).
- Ported: a hidden prefab leaves alone; a prefab map's instance ahead of a target with fewer than two other
  referrers moves the target (181019590), its nodes joining right after the instance; a prefab inside an instance
  target collapses from the copy. MapEntityList.Item.Chain gives instances and prefabs in nesting order, and
  SettleWorld.Step / Placed / UnderDeformerStep and NavLinkFields.Copied take it (NavGenMapInput uses it).
  The three lists are Valve's whole.
- Probe layout lesson: a target group outside the world tree (as s2c_prefabprobe10 has it) never loads its
  prefabs' maps, so its copied prefabs collapse to nothing. Hammer keeps targets in the world; probe10's crash may
  come from that layout rather than from instances in the copied prefab's map (not checked).
- Not done: the entity lump of a prefab inside a target (EntityLumpSet's copy chains; needs a Valve package of
  s2c_pfintarget), a moved or cloned target holding instances or prefabs, the delete cascade's order.

## Earlier session (2026-10-29, the 1.41.9.3 update)

Ledger 182; tools/re/patch_reports/20261029_1.41.9.3.md. CS2 1.41.9.3 (Oct 28 build, SourceRevision 11181008)
rebuilt every toolchain DLL but tier0.

- build_diff against 1023: vphysics2, physicsbuilder, visbuilder, smartprops, vrad3, hammer, rendersystemdx11,
  scenesystem, materialsystem2, meshsystem, worldrenderer, navsystem, animationsystem and steamaudio are
  code-identical. Every tracked address resolves to the same code (ambiguous rows by neighbouring anchors,
  mid-function ones through their function, vftables slot by slot through RTTI); visbuilder's signatures
  165/165.
- resourcecompiler: one real code change, the Panorama preprocessor's quoted-string tokenizer (roadmap item 11).
  The rest of its 34 "real" diffs are inline switch tables and libraries (libtiff, OpenSSL, protobuf).
- Output change: the VData strip rule is weapon_lar now, fingerprint 55841844 (ledger 182). Valve references
  holding a .vdata_c or a .vdata child (bomb damage roots) from before 10-29 carry the old value.
- patch_check now checks every DLL with a name list (scenesystem, materialsystem2, meshsystem, worldrenderer,
  navsystem, animationsystem, steamaudio, phonon, bc7e, modtools and server were not checked before); a src
  citation already in a list is checked against that list's build. 17 script RVAs registered.
- Capture scripts with literal RVAs map them to the installed build (rva_map.rebase(agent, build) or installed());
  rva_map.installed hands zero-filled .bss to installed_data (capture_propmeshes' pointer had moved unseen).
- cubehost knows client 10-29's buildcubemaps_renderdoc_capture global (182382858).
- References refreshed on 1.41.9.3 (10-29 afternoon): every package holding the old fingerprint (B3B3D287) rebuilt
  as it was built: full (-f) deformerprobe, deformerprobe2, s2c_l5farfield128, s2c_l5probe, s2c_pinst, s2c_pinst7,
  s2c_prefabprobe4-9, 11, 12; `-world -phys -nav` the seven nav probes (navangles, navlink 1-4, navmarkup, navround);
  %TEMP%/vrad3_refs (cardtest and probe01 --full, navmarkup and navround -world -phys -nav), nolight_1023,
  preset_fast and preset_entities (--over) with MapSubassetsTests' switches. atixref back to world-only (553
  entries) from an empty slot: a -world build over the full package keeps its phys, vis and nav. Old packages in
  %TEMP%/refresh_1029b/old. Full suite 1148/1148 (7 m 39 s). Not refreshed: s2c_cubeprobe's probe01 and
  s2c_l5probe (cubemap builds; no test failed on them).
- The suite rebuilt atixref FULL on its own after the update: MapFixtures.CompileWithRc compiled every stale
  package with -f, and its 10-minute timeout left resourcecompiler running (it wrote the package 26 min later).
  CompileWithRc now builds atixref world-only from an empty slot and kills the compile at the timeout.

## Earlier session (2026-10-28 late and 10-29, walkable seeds and prefabs with instances)

Ledgers 179, 180; NAV.md collector 1 and "Order of work" 2. CS2 closed, Steam open (resourcecompiler captures only).

- NavGenMapInput.Seeds now takes the links' search (one shared `Found`: the baked entity list, wildcard classname,
  visibility, the same refusals) instead of the lump's walk; that closes its "unsettled" order note.
  NavSeedTests: Valve's navinput_seeds.bin on all 16 nav captures, bit for bit and in order.
- MapEntityList collapses a prefab map's instances and targets (each named within its own tree; a prefab map's
  hidden ids from its own document). The bake round's shape is decompiled (180f60740 / 180f86e70: instances
  collected once per round, children first, no reordering filter). MapEntityListTests compares the whole list with
  tools/re/capture_entitylist.py's (which now records origins): s2c_prefabprobe5 exact (145 entries).
- Ledger 180 (2026-10-29): settled. A prefab map's instance that joins the world ahead of its target names a
  clone of it (181019590), which leaves with that instance's collapse. MapEntityList models it; whole lists exact on
  s2c_prefabprobe3, 4, 5, 7, 9 and s2c_deformpath3, 4, and all 16 seed captures. tools/re/capture_entitylist_trace.py
  logs every list edit, the root list around each collapse, referrer counts and target sets (bake-only traces take
  seconds; `-f` in --switches when the package is up to date). Sweep of the 127 .vmaps on disk: every seed and
  link search runs but s2c_prefabprobe10's (Valve crashes on it); hidden instances or prefabs (mako, two c2m2
  variants) still refuse the list, unused there (no seeds or links).
- Ledger 181 (2026-10-29): hidden instances (deleted, then their target when unnamed; mako's whole list exact) and
  hiddenFlags: only & 0xb hides (MapEntities.HiddenNodes fixed; flag 4 is c2m3_coaster_d_d's visible prefabs, whose
  list is now exact too). `--bake-only` on capture_entitylist_trace.py ends at the bake for maps with no nav step.
  Left in MapEntityList: a hidden prefab (none on disk), a prefab inside a target, a target moved rather than
  cloned, the delete cascade's order, and s2c_prefabprobe10 (Valve crashes).
- Full suite (10-29, Release, after ledger 181): 1090 to 1091 passed, then the test host crashed while
  NavCompileTests.NavWithLaddersMatchesValve (s2c_prefabprobe3, the nav from the .vmap) was running, at 8 to 10
  minutes, three runs out of three. Alone it does not crash (75 min, about 460 to 640 MB, stopped by hand before it
  finished; the analyze=true case is the hours-long CS analysis). Not settled whether the crash is that test under
  the suite's parallel load or another test running beside it. Settled: no fault (no dump, no WER or .NET Runtime
  event); every other test is done about 9.5 min in and that case (analyze=true) runs for hours until a timeout kills
  the host. It now runs only with NAV_ANALYZE_BIG=1 (e78c28a). The one failure was
  MapRootFileTests.MapRootEqualsValves on s2c_navlink3: its reference package (10-28 21:11) predated
  s2c_navlinkpf.vmap (21:27), so the root's m_nFileCRC differed. Recompiled 10-29 with `-world -phys -nav`
  (compile_fresh.py; the old package is in %TEMP%/refresh_1029/old): the map root, link, nav and lump tests pass.
  Note: a Bash tool timeout kills the run at 10 min and looks like the same crash; run the suite detached.
- The suite was not run in full.

## Earlier session (2026-10-28 evening, nav links)

Ledgers 175 to 178; NAV.md collector 5 and "Order of work" 2. CS2 closed for two short captures, open otherwise.

- tools/coverage/nav_link_specimen.py builds s2c_navlink (probe01, two raised pads, three CMapNavLinks);
  Valve's compile is in the addon (copy in %TEMP%/navlink_ref), captures in %TEMP%/navgen_capture/s2c_navlink_in
  (and _camelkey, the run that showed `locatorOffset` is not the .vmap key).
- NavGenMapInput.Links (the collector), MapEntities.NavLinkFields and EntityLumpAuthor.NavLinkKeys (the lump);
  NavLinkTests and NavCompileTests' s2c_navlink cases pass. tools/re/tier0_wildcard_oracle.py measures the
  search's name compare.
- Ledger 176 (later): every other attribute. `nav_link_specimen.py --keys` builds s2c_navlink2 (six links, one
  type or export branch each); navlink_type is the MapNavLinkType_t name, case-blind (181067b60). Its records,
  029, link entities and whole .nav are Valve's; capture in %TEMP%/navgen_capture/s2c_navlink2_in.
- Ledger 177 (later): links in instances. tools/coverage/nav_link_nest_probe.py builds s2c_navlinkpf, s2c_navlink3
  (with a prefab) and s2c_navlink4 (without); tools/re/capture_entitylist.py logs every edit of the world's entity
  list. Maps/MapEntityList.cs simulates that list after the bake (the search's order); NavLinkFields.Copied places a
  copy as its locators do (CMapTransform's TransformBy). s2c_navlink4's records, 029, whole lump and whole .nav are
  Valve's. Ledger 175's search rule was wrong (classname only, visibility tested); corrected there.
- The seed collector (180f21760) searches the same list (180fc6f40, flags 3, make_manual); its order
  (NavGenMapInput.Seeds' "unsettled" note) can now come from MapEntityList (not done, not checked for markups
  or ladders).
- Ledger 178 (later): links in prefabs. The prefab's step moves the locators by CMapTransform's TransformBy as an
  instance does; s2c_navlink3's records, 029, lump and .nav are Valve's.
- Next for links: what MapEntityList refuses (hidden instances, prefabs with instances inside, prefabs in targets).
- The suite was not run in full.

## Earlier session (2026-10-28, the ragdoll pose and the first lockstep)

Ledger 166; PHYSICS.md steps 5a and 6. CS2 closed, Steam open (resourcecompiler captures only).

- animationsystem.dll 20261023 is now in the Ghidra project (alias-free: `revaq.py dec animationsystem_20261023.dll`).
- Probes (tools/settle/probes): settle_pose.js (rc's node pose build and 1817dd320's locals and output),
  settle_posewriter.js (which CAnimationHelper call changes its pose), settle_poseupdate_calls.js (Stalker list of
  the update's calls), settle_fine_steps.js (body dumps at every step 91..130).
- The aggregate's pose is node +0x310, the skeleton's reference pose with per-bone scales; our missing scale was the
  whole difference. AggregateBuildCaptureTests and the new ModelPoseCaptureTests pass.
- RagdollLockstepTests (new): the whole 2700-step settle exact on three captures (ledger 167), after a contact probe
  (settle_contacts.js) showed the chicken's placement and the run-dependent dynamic order.
- The write-back and boneTransforms (ledger 168): SettleWorld.Run settles ragdolls; s2c_ragdollprobe's lump equals
  Valve's whole. capture_settle.py --keep-package with probes/hold_to_exit.js keeps the package of the captured run.
- s2c_ragdollprobe2 (ledger 169): hulls in an aggregate and VPhysXBodyPart_t's defaults (drag 1); lump exact.
- The suite was not run in full.

## Earlier session (2026-10-27 evening, a cubemap face with no dump)

Ledger 162; CUBEMAPS.md "A face with no dump". Branch feat/cube-nodump (worktree source2-compiler-wt-cube, made because
the ragdoll session's files in the shared checkout did not build), cherry-picked onto research/map-authoring.

- `Gpu/CubemapFaceRenderer.cs`: the frame, bindings from the .vcs write sequences (constant buffers at the slot,
  textures and structured buffers at 30 + slot, UAVs at 158 + slot, samplers at 14 + slot), textures, samplers,
  programs, targets and clears. `Cubemaps/CubemapFaceBlocks.cs`: the per-face blocks, which the replay test now uses
  too. probe01's two cubes and its whole array exact with no dump read.
- The two inputs the 10-27 inventory left: set 3 binding 31 is g_BarnLights (unread in the cubemap view), and the
  binner's dispatch sizes are thread counts over the workgroup (decompiled).
- The suite was not run in full; the replay's ChainedCubeMakesTheGamesExr (probe01 cube 0) passes with the shared
  block builder.
- Later (ledger 163): the scene read from the package, programs by render mode (ValveProgram.LoadOrNone: no shader
  where the combo has none), and the atixref program check (72 / 189; what is missing is listed in CUBEMAPS.md).

## Same day, ragdolls in the settle (2026-10-27)

Ledgers 158, 160, 161; PHYSICS.md "Aggregates in the settle", steps 3 to 5 (done or nearly), 5a to 7 (left).
Commits 93db97e, 98a2455, ae55aad, c1f9e28.

- Joints solve in islands (Simulation/JointSolver.cs, oracle-exact), live in the island manager and SetType
  (IslandManagerOps, RnWorld.AddJoint/SetType; step-0 islands slot for slot), and a model with two parts or a joint
  builds as Valve's aggregate (Maps/SettleWorld.Aggregate.cs): every body field but the pose, every shape attribute
  and all 56 joints of s2c_ragdollprobe byte for byte.
- Next, in order (PHYSICS.md 5a to 7): port the model pose from the animation graph's default pose (rc 1817dd320;
  the skeleton composition is 1 to 4 ulps off in position), lift SettleWorld.Run's ragdoll refusal, lockstep the
  settle against the capture (also checks the island split with joints and sleep), then the boneTransforms
  write-back.
- Tests: RagdollIslandsCaptureTests and AggregateBuildCaptureTests run with RAGDOLL set to the capture's
  events.jsonl (a copy in %TEMP%/settle_capture/s2c_ragdollprobe; the recipe is in
  tools/re/out/settle_notes/notes_joints.md). AggregateBuildCaptureTests fails today, listing only the pose.
  JointStreamOracleTests needs the vphysics2 0924 build (installed or staged from tools/re/binaries).

## Earlier session (2026-10-27 later, layer selection and the material store)

Commits d5d67e9 .. a095d91 on research/map-authoring, pushed to box (ledgers 156, 157, 159; 158 and 160 are the
ragdoll session's, which shares this checkout).

- In-layer order (156): the layer sort ends on the CMaterial2 address, which follows creation order; the 957
  engine-created materials come first (`Resources/cubemap_engine_materials.txt`, `MaterialOrder`), the map's own
  follow async load completion (not data). Measured that it only matters on coplanar pixels of two materials.
- Layer selection (157, 159): scenesystem 1800f3b10's object pre-filter, 180041f30's draw-call test and
  180041a00's fragment test, with every input ported from code (`CubemapLayerFlags`): the object words from
  m_allFlags / m_anyFlags and meshsystem 180012150, the fragment flags from m_objectFlags, the draw call's flags
  from meshsystem 180027160 / 180027830 over the material's attributes. Checked on millions of logged game
  decisions; `DrawListEqualsTheCapture` passes on all three maps.
- The material attribute store (157): the merge of every render mode's program from the Vulkan shader files
  (`MaterialAttributes.OfModes`, `ShaderLibrary { Platform = "vulkan_50" }`); probe01's 16 stored keys equal ours.
  This also explains GROUND_TRUTH 40's AllowBackfaceCulling fallback (the Depth mode's csgo_depth_only).
- User rule, hard (memory "ground-truth-ledger"): no guessing, no fitted fallback presented as Valve's; every
  rule from decompile, Frida or RenderDoc, a match is only verification.
- Not done: the dump-free face frame (item 1 above). The full suite was not run in this session.

## Earlier session (2026-10-27, the world draw list from our data)

Ledger 156; CUBEMAPS.md "The draw list".

- `Cubemaps/CubemapFaceDrawList.cs`: views (moved from the culling test), one list per aggregate and layer, commands,
  instance blocks, viewports. `AggregateCullingCaptureTests.DrawListEqualsTheCapture` (CUBECULL) passes on probe01 and
  s2c_l5probe; atixref fails on layer membership (material draw flags, not read).
- `ChainedCubeMakesTheGamesExr` with `CUBEREPLAY_OURDRAWS=1,0`: probe01's two cubes and s2c_l5probe's cube 0 are the
  game's EXRs with our lists, order, arguments and viewports. In aggregate order (`=1`) four faces per cube differ.
- The layer sort (scenesystem 1800ed4c0 -> 18010f4e0 / 1801112a0) ends on heap pointers; `tools/cubehost/layer_sort.js`
  logs its keys (run_probe.py --frida, `--timeout 1500` for atixref). Two atixref runs order the same draws differently.

## Earlier session (2026-10-26 later, a second map and the draws' inputs from our data)

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
