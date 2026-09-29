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

| module | doc | state |
|---|---|---|
| containers and packaging | [CONTAINERS.md](CONTAINERS.md), [RESOURCES.md](RESOURCES.md) | every container authored; VPK byte for byte; `s2c compile-map` updates a package |
| geometry | [GEOMETRY.md](GEOMETRY.md) | the trace scene from the .vmap matches every `.rte` triangle on five maps; world nodes: container, codecs, tangent frame and UV density exact, lists and merge rules read, trees from content not started |
| visibility | [VISIBILITY.md](VISIBILITY.md) | every stage exact; byte-identical VXVS from the .vmap on three maps |
| physics | [PHYSICS.md](PHYSICS.md) | world_physics and entity models whole-file exact on every map with a compile |
| entity lump | [ENTITIES.md](ENTITIES.md), [SETTLE.md](SETTLE.md) | every lump exact outside the light keys; the settle exact, sphere and capsule props included |
| baked halves | [BAKED.md](BAKED.md) | vrad3 driven; port not started |

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

## Open, in the order worth taking it

1. **Light keys** ([ENTITIES.md](ENTITIES.md)):
   - re-read `EditorTraceScene` and the `LightTrace` second pass beside the
     decompile; they were written by an agent and are checked only by their
     tests;
   - atixref's omni2 lights 7306, 7318 and 7348, one cube face each, single
     rays at prop edges (ledger 31; `LightRayProbe`);
   - `precomputed_vis_clusters` (needs visibility from the .vmap);
   - then drop the light classes from the lump test's gap.
2. **The trace-scene flag word** (GROUND_TRUTH 25). Then run atixref and Mako
   end to end from the .vmap (`VISBUILD_BIG=1`), and wire visibility into
   `compile-map`.
3. **Physics leftovers:** prop bone overrides, the lattice deformer,
   instances in prefabs, the bake's vertex merge and smoothing.
4. **compile-map:**
   - what an entities-only build clears;
   - `-entities` without a package;
   - compiling a map's addon content.
5. **Geometry:** world nodes and render meshes, the largest remaining
   module. Start from GEOMETRY.md's open list: the incoming triangle
   order (GROUND_TRUTH 34), then aggregation on probe01 (35).
6. **Baked halves:** the scene vrad3 gets, then a tracer scored per luxel.

Pre-cleanup notes (the old VIS.md lab notebook, RE_NOTES, MAP_RESOURCES and
the rest) are in git history at `c2dc317`.
