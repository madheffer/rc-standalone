# Plan: a self-authored CS2 map compiler

The goal is a tool of ours that turns a `.vmap` into a map CS2 loads and renders
the way Valve's compile would. Not byte-identical, which is unattainable for the
baked halves and not what matters; **engine-equivalent**, measured.

What "as Valve would" means concretely, and each of these is already measurable:

- containers decode to the same tree, the same references, the same resource
  version (`s2c reauthor`)
- visibility never hides geometry Valve shows, and stays inside the band a
  legitimate settings change spans, 1.5 to 2% (`s2c vis-diff`)
- the finished VPK differs from Valve's only where we intend (`s2c map-diff`)
- it loads and plays

Read `CAVEATS.md` before touching any of this.

---

## Where the dependencies actually run

This ordering is not a preference, it is what resourcecompiler does, and it is
why the phases below cannot be reordered:

```
.vmap (binary DMX)
   -> world geometry            brush solids, displacements, prop placement
   -> ray trace environment     the .rte, written to %TEMP%
   -> VISIBILITY                voxelize, regions, clusters, PVS
   -> world nodes               geometry SPLIT ALONG cluster boundaries
   -> entity lump               carries cluster assignment
   -> lighting (vrad3)          needs geometry and visibility
   -> cubemaps, nav, audio
   -> pack
```

The hard consequence, measured and not assumed: changing visibility alone changes
**11 of 72 files** in a map VPK. Visibility, world node meshes, the world node
index, the world, the entity lump and a light probe octree are one coupled unit.
A phase that produces any of them must produce all of them consistently.

---

## Phase 0: formats and instruments (DONE)

| piece | state |
|---|---|
| container authoring, RERL ids, KV3 v5 + LZ4 | shipping |
| index layer: `.vrman` `.vwrld` `.vwnod` `.vents` `.vmap` | authored, equivalent to Valve's |
| VXVS codec | byte-exact re-encode on 112 maps, 481 MB |
| visibility queries | agree with an independent reader on 84,673 points |
| `s2c vis-diff` | holes vs overdraw, zero on identical input |
| `s2c map-diff` | per-file and per-block VPK comparison |
| `s2c reauthor` | bytes vs decoded tree vs references |
| Valve as an oracle | 13.5 s per vis rebuild, byte-stable |
| shadow install | settings varied without touching the real game |
| binary symbols | assert-derived names, persisted in the Ghidra project |

---

## Phase 1: prove our containers in game (DONE, loaded and played)

Everything we author is currently judged by our own comparison. It has never been
loaded by CS2.

`-novpk` was the intended route and it hangs (see `CAVEATS.md`), so we write the
VPK instead. `tools/pipeline/splice_map.py` rebuilds a map VPK with named entries
replaced, and it round trips Valve's own package byte for byte across all 72
entries, with ValvePak reading the result.

Done so far:

1. Re-authored `world.vrman_c`, `world.vwrld_c`, `n0.vwnod_c` and
   `default_ents.vents_c` from the stock compile through our writer. All four
   report *equivalent*: identical decoded tree, identical references, identical
   resource version, differing only in KV3 encoding and the RED2 fingerprint.
2. Spliced them into Valve's compiled map. `map-diff` confirms **exactly those 4
   of 72 files differ** and the other 68 are byte identical.
3. Installed at `game/csgo_addons/s2c_lighting/maps/ze_hold_em_p.vpk`, with the
   stock build kept beside it for an A/B.

4. **Loaded it in CS2 and played it.** The map runs: geometry, spawns, round
   start, and the map's own entity logic firing its door countdown, which is our
   re-authored entity lump doing its job.

**CS2 accepts our containers.** Scored against Valve's own build through the same
load path, our build reports the **same 8 missing resources and not one more**:
three addon materials, a postprocessing profile, a cubemap array and three
panorama images, all of which live in the addon and are not mounted when a map is
loaded out of `csgo/maps`. Valve's stock build fails on exactly the same eight,
which is what makes the comparison worth anything.

That missing addon content is why the map renders magenta on this load path. It
is not a defect in what we authored, and the control is what establishes that
rather than an assumption.

`tools/pipeline/cs2_console.py` drives the game over `-netconport`, so this test
is now a command rather than a person.

**Why first:** it is a day's work, it needs no new format knowledge, and it
converts "our writer produces something that looks right" into "CS2 accepts what
our writer produces". Every later phase rests on that and none of them should be
built on an unproven container layer.

**Done when:** a map whose entire index layer is ours loads and renders
indistinguishably.

---

## Phase 2: visibility from Valve's geometry

Normally visibility needs our own geometry pipeline first. It does not have to:
**the `.rte` survives in `%TEMP%`**, and it is the ray trace scene visibility
consumes, `Convert RTE with 28728 triangles`. Decoding it lets the visibility
builder be written and scored now, against a real map, without the geometry
pipeline existing.

1. Decode the `.rte`. **DONE, exactly**, read out of
   `CVisibilityMesh::LoadRTEFromFile` (`180049e00`) and the unserializer under it
   rather than inferred: the header is 60 bytes and the index array is `C`, and
   the two errors in the old reading cancelled in the file-size arithmetic, which
   is why "both specimens tile exactly" passed for a layout that was one float out
   of phase. Every traced triangle of all three measured maps rebuilds, all their
   normals are already unit length, and the rebuilt bounding box is the header's
   own to the unit. `RayTraceEnvironment`, docs/RTE.md.
2. Voxelize. **DONE, EXACT on ze_hold_em_p** (81,625 nodes against the compile's
   81,625), with **100% of the shipped octree's branches reproduced on all three
   maps**. It is `18002e310`'s top-down build and not a marking pass, because the
   set of triangles that counts depends on the box: above 256 units everything
   counts and at or below it a `0x1000` triangle does not, and a node becomes a
   branch on size alone once its parent found geometry in it. The two probe maps
   are +0.28%, six branches each. `VisVoxelizer`, scored by `VisVoxelizerTests`.
   The rest of the scoring targets stand: 10,554 regions in, 103,358 out,
   collapsed to 4,194, 258 clusters against a target of 1,342, 129 unique masks.
3. Regions, outside detection and cluster generation. **All three EXACT on
   ze_hold_em_p**: 10,554 enclosed regions against 10,554, and 93,354 clusters
   generated against 93,354. Outside detection is `1800321f0`, a per-region vote
   over rays marched through the voxel octree, propagated along rays rather than
   through voxel adjacency, which is why it works on geometry that does not seal.
   Cluster generation is `180032d80`: one cluster per open voxel, then
   `1800337a0` merges the cheapest pairs by `VisMergeCost` (which is
   `1800301c0`, slot 1 of the merge controller vtable, with every constant read
   out of the DLL and pinned by a test that reads them back). `VisOutside`,
   `VisClusters`, `VisMergeCost`.

   The seed is `18004b260` and `18004a2f0` ported outright, a 5 by 5 grid of rays
   through each face of the region's box tallied four ways, run over the `.rte`'s
   own kd tree by `RayTraceEnvironment.Trace`. Approximating it cost -49% on the
   probe maps while ze_hold_em_p stayed exact at any threshold, which is what
   proved it had to be ported rather than tuned.

   Open here: the cluster merge assumes every voxel of a region sees alike, which
   is exact on ze_hold_em_p and about 2.5% short on the probe maps, where a region
   really does merge to 1.75 clusters rather than one. That needs the per-cluster
   ray sample `180031a20` takes.
4. PVS. Score with `vis-diff`: **zero holes is the requirement**, overdraw is
   negotiable, and the settings band says a couple of percent is normal
   variation.
5. Write it with the existing codec, which is already byte-exact.

**Done when:** our `.vvis_c` for a real map scores zero holes and overdraw inside
the settings band, and the game loads it beside Valve's geometry.

The last point is the honest caveat: because of the coupling, our visibility
beside Valve's geometry is only valid while our clustering stays close enough to
Valve's that its geometry split remains sensible. A large divergence needs
Phase 3.

---

## Phase 3: the coupled outputs

Once visibility is ours, the five resources that depend on it must be too:
splitting geometry along our cluster boundaries into world nodes, the entity
lump's cluster assignment, and light probe placement. `map-diff` is the check:
after this phase a full rebuild should differ from Valve's in these files and
**no others**.

---

## Phase 4: geometry from source

The real foundation, and the largest piece: `.vmap` brush solids, displacements
and prop placement into meshes. Deliberately last among the correctness phases
because Phases 1 to 3 can all be done against Valve's geometry, and each one
narrows what can go wrong here.

Scored against Valve's world nodes on the same map: mesh counts, triangle counts,
bounds, material assignment.

---

## Phase 5: the baked halves

Lighting and cubemaps stay Valve's for now, and that is a measured decision, not
a concession: lighting is **16 seconds of a 1,299 second compile**. `vrad3.exe`
runs standalone in 56 seconds on Mako and we already drive it. Replacing it buys
nothing until everything above is done.

---

## The optimisation thread, separate from correctness

Visibility is 83.8% of a compile and the ray scan is 52% of that. These are
speedups, not correctness work, and they can run in parallel:

| target | measured | opportunity |
|---|---|---|
| LOS ray scan | 568 s, 4,567,126,453 rays, 11,923,366 useful | embarrassingly parallel, the workload a rented GPU is for |
| `CLargeClusterRegionsRayGenerator` | 252 s, 608,325,057 rays, **383** useful | 19% of the compile at six hits per ten million; mechanism not yet understood |
| cluster generation + merge | 404 s | ordinary CPU graph work over 751,270 regions |
| `.los` cache | never written | the loader works, so authoring the format seeds it |

The original premise of this whole effort was that lighting made compiles take a
day and a big GPU would fix it. Lighting was the wrong target; the ray scan is
the right one, and it is the same shape of answer.

---

## Open reverse-engineering threads

- **The `.rte` format.** Unlocks Phase 2. Highest value.
- **The `.los` format.** Unlocks the cache the shipped tool cannot use itself.
- **`CLargeClusterRegionsRayGenerator`.** Reached only through a base pointer,
  its name string has no code references, and Ghidra cannot follow x64 MSVC RTTI
  because those links are 32-bit RVAs. Needs an RVA-aware vtable walk.
- **`lightmap_packing_geometry.dat`.** 28-byte stride, earlier hypothesis
  disproven.

## Deliberately not doing

- Matching Valve's clustering. The metric permits a looser answer, and "no holes,
  more overdraw, faster" is a far more achievable target than reproducing their
  merge heuristics.
- Byte-identical baked output. Not attainable and not what the engine needs.
- Replacing vrad3. Measured at 1.2% of a compile.
