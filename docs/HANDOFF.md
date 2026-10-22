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
| containers and packaging | [CONTAINERS.md](CONTAINERS.md), [RESOURCES.md](RESOURCES.md) | 97%: every container authored; KV3 v5, VPK and the map root (.vmap_c) byte for byte, Hammer's Fast and Only Entities presets included |
| geometry | [GEOMETRY.md](GEOMETRY.md) | 99%: probe01's -world package equals Valve's but a per-compile stamp; ze_hold_em_p world 100% |
| visibility | [VISIBILITY.md](VISIBILITY.md) | 97%: every stage exact; byte-identical VXVS from the .vmap on three maps |
| physics | [PHYSICS.md](PHYSICS.md) | 97%: world_physics and entity models whole-file exact on every map with a compile |
| entity lump | [ENTITIES.md](ENTITIES.md), [SETTLE.md](SETTLE.md) | 99%: every lump exact outside a few light keys |
| baked outputs | [BAKED.md](BAKED.md), [CUBEMAPS.md](CUBEMAPS.md) | 80%: lightmaps, probes, far field, fog, nav, bomb damage and .vmap_c done; cubemaps in progress |
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

1. **Cubemaps, the face render** ([CUBEMAPS.md](CUBEMAPS.md), ledgers 117-121):
   - atixref draw 2305: one half-float ulp on 38 pixels. Build the pipeline
     from one-stage libraries (VK_EXT_graphics_pipeline_library) as the game
     does; the first attempt crashed with an access violation in our code.
   - The per-view culling that makes the draw arguments: port scenesystem
     18003c5a0 (fragment BVH against the frustum, instance records, merging
     of adjacent fragments) and 18003d380 (per fragment), checked against the
     draw arguments of probe01, s2c_l5probe and atixref.
   - The light binner's inputs from our own lights, then all six faces of a
     cube, then the readback, QuarterSize, the EXR strip and the array (the
     part after the render is already exact).
   - probe_classes crashes the engine on load (its package is missing entity
     models): recompile it, then capture its rect lights.
2. **.vmap_c leftovers** (ledger 119): force_up_to_date_materials' reader
   in Only Entities builds; MapCompile knowing a missing material. (The l5
   row order is settled: read order plus MSVC std::sort.)
3. **Nav** (LIGHTING_ROADMAP L9, L22, L23): markups, links, ladders, movable
   meshes, spheres and capsules in the casts.
4. **Own lighting:** OIDN for quality 1 and 2 end to end, more maps through
   `-s2cbake` (L16), the prefab world order (L20), AMD.
5. **Smaller baked outputs:** precomputed shadows, occlusion data (de_ maps;
   where it is made is not traced), Steam Audio (no workshop map has it).
6. **Geometry:** heap-ordered prop aggregate parts (Valve varies run to run),
   subdivided entries' tangents, overlay tangents, detail grass.
7. **Visibility:** Mako end to end, and wiring vis into a world build.
8. **Entities:** atixref's one-face omni2 lights, Mako's light pass.
9. **Containers and resources:** compiling the addon content a map
   references, a package made only of our outputs; glTF export, model
   internals, shaders.

## Latest session (2026-10-22)

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
- Full suite on the PC: 982 of 983 (the known l5 row order).
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
