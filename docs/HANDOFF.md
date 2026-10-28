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
   - s2c_l5probe's cube 1 (needs a capture: `run_probe.py --renderdoc 1 --settle 20`, CS2 and Steam closed, ask
     the user); decide the build state our compile renders at (scripted camera, spawned pawn and viewmodel:
     roadmap 0e, ledger 149).
   - Animated maps (roadmap 0a-0e, ledger 144): atixref differs run to run in Valve's own build; pin its time in
     cubehost first. Scope is 1:1 there too.
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
   the context's +0x605 flag, unread.
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
12. **Speed, after parity (user, 2026-10-28):** nav and vis are the two big compile stages.
    Vis: the PVS scan is about 90 of the 113 min on s2c_prefabprobe4. Nav: our nav from
    s2c_prefabprobe3's .vmap (generation, ladders, the CS analysis) ran over an hour in a
    Debug test build; time a Release build and Valve's own step before sizing it. Any change
    that could alter bytes waits for the user's OK (parity before speed).

## Latest session (2026-10-28 late and 10-29, walkable seeds and prefabs with instances)

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
  the suite's parallel load or another test running beside it (a separate session is on it). The one failure was
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
