# Visibility: the structure, the tool, and where a replacement starts

Visibility is the expensive half of a CS2 map compile. On ze_ffvii_mako_reactor_v6_p
it is about 400 of 1,313 seconds, 30%, against 9.3 seconds for lighting. That
measurement is in `VRAD3.md`; this file is about the thing that costs the 400.

Everything below was read off Valve's own compiles or Valve's own binaries. Where
something is inferred rather than measured it says so.

## The output: world_visibility.vvis_c

One file per map, `RED2 DATA VXVS`, resource version 7 in all 112 installed maps.
It is 10.4% of all map bytes, third behind lightmaps and world nodes, and it runs
from 928 bytes on a skybox to 49 MB on ze_dystopia_p.

`VXVS` is six arrays laid end to end with no header of its own. The `DATA` block
is a small KV3 tree that indexes them, and its offsets are plain prefix sums of
the counts, so the layout is fully derivable from the arrays:

| array | stride | what it holds |
|---|---|---|
| `m_NodeBlock` | 8 | octree nodes |
| `m_RegionBlock` | 8 | cluster regions owned by leaves |
| `m_EnclosedClusterListBlock` | 8 | `(i32 offset, i32 count)` ranges |
| `m_EnclosedClustersBlock` | 2 | `u16` cluster ids |
| `m_MasksBlock` | 8 | 4x4x4 occupancy masks |
| `m_nVisBlocks` | 1 | the PVS bit matrix |

Checked on every installed map: the strides never vary, the six ranges tile VXVS
exactly with no padding or gap (112 of 112), and our derived index equals Valve's
own numbers (112 of 112).

### The two packed records

```
Node, two u32
  word 0   bit  0      isLeaf
           bits 1..31  offset: first child for a branch (a group of 8 addressed
                       by octant), first region for a leaf
  word 1   bits 0..7   regionCount
           bits 8..31  enclosedListIndex, 0xFFFFFF meaning none

Region, one u64
  bits  0..14  clusterId
  bit  15      intersectsGeometry
  bits 16..39  leafIndex     (written by the compiler, unused by the game)
  bits 40..63  maskIndex
```

Every bit of both records is claimed by a named field, which is why the round
trip in `VoxelVisibilityTests` is a real test of the layout: the codec rebuilds
the words from the fields rather than keeping them. It re-encodes **112 maps and
481 MB of VXVS byte for byte**.

### The PVS matrix

`m_nVisBlocks` is a bit matrix, one row per cluster, row-major, **least
significant bit first**. That bit order is settled rather than assumed: read LSB
first, 4,095 of ze_aztecnoob_p's 4,096 clusters see themselves; read the other
way, only 3,136 do.

Row stride is `roundUp4(ceil(clusters / 8))`, verified on all 112 maps (the
surplus over `ceil(clusters/8)` is only ever 0, 2 or 3, and the stride is always a
multiple of four). Sky and sun take rows of their own only when
`m_nSkyVisibilityCluster` / `m_nSunVisibilityCluster` are numbered past the base
clusters; otherwise they alias an existing row.

**Cluster 0 is the fallback and sees everything** - its row is all ones. And a
build can allocate a cluster id that nothing ends up occupying, whose row is then
entirely zero; such a cluster does not even see itself. Measured over 20 maps and
51,066 clusters, 13 were unused, essentially one per map.

### Leaves hold several clusters

A leaf is subdivided twice more into a 4x4x4 grid, and each of its regions carries
a 64-bit mask over those 64 cells (bit index `x + 4y + 16z`). Looking up a point
means descending the octree by octant, computing the one mask bit the point falls
in, and taking the first region whose mask contains it. `VoxelVisibility.Query`
implements that and agrees with ValveResourceFormat's independent implementation
on **84,673 sampled points across 12 maps**, with no disagreement.

## What the maps are actually shaped like

| map | clusters | mean visible |
|---|---|---|
| ze_raccoon_facility_p | 1,900 | 5.0% |
| ze_ocean_base_escape_p | 4,096 | 8.5% |
| ze_hidden_fortress_p | 4,096 | 14.0% |
| ze_boatescape101_p | 4,096 | 19.7% |
| ze_colorlicouspilar_p | 4,096 | 35.2% |

**Nearly every real map lands on exactly 4,096 clusters.** That is not a
coincidence of map size, it is a clamp: visbuilder logs
`Target %d clusters, clamped to %d, grid size %.1f`, and `MaxVisClusters` is a
configurable. So on a typical ZE map the merge stages are not finding a natural
number of clusters, they are grinding a much larger set down to a fixed ceiling.

"Mean visible" is the fraction of clusters visible from an average cluster, and it
is what vis is FOR. A faster builder that raises this number has not sped the map
up, it has moved the cost to the GPU at run time, so it is the quality axis any
replacement is scored on.

## The tool: visbuilder.dll

Vis runs inside `resourcecompiler`, not as a standalone executable. There is
`visbuilder.dll` (1.8 MB) but no `visbuilder.exe`, unlike `vrad3.exe`.

Source files, from assert strings: `visbuilder.cpp`, `vis3.cpp` (asserts at line
4718, so it is a 4,700+ line file), `vis_cluster.cpp`, `voxel_utils.cpp`,
`utils.cpp`. The classes worth knowing: `CVisBuilder`, `CVoxelSampler3`,
`CClusterQuery3`, `CClusterQuerySun`, `CMergeControllerGrid`,
`CResourceCompilerMapVisibility`, and a family of ray generators -
`CAxialRayGenerator`, `CBoundaryPointsRayGenerator`, `CClusterCenterRayGenerator`,
`CClusterViewRayGenerator`, `CLOSRayGenerator`, `CLargeClusterRegionsRayGenerator`,
`CNearlyVisibleNeighborsRayGenerator`, `CRandomRayGenerator`.

`VoxelVisBlockOffset_t` is Valve's own name for the (offset, count) pairs in the
DATA index.

### The pipeline, from its log strings in source order

1. `Convert RTE with %d triangles` - level geometry into the ray trace scene
2. `Voxelize (%.f units) took %.2f seconds (%s nodes)`
3. hints: `%d) %dx%dx%d voxel hint`, axis split hints, `Hint %.2f (%.2f regions)`
4. `Initial regions %d, collapsed to %d`
5. `Distance merged regions (%d merged to %d)`, `Pre-merged nodes %.2f seconds`
6. `Outside detection took %.2f seconds`
7. `Generated clusters for %d regions in %.2f seconds` **(228 s on Mako)**
8. `Merged to %d clusters in first pass` / `second pass`
9. `Merged cluster lists in %.2f seconds [%d clusters]` **(172 s on Mako)**
10. `Compacted to %d regions (%d clusters) in %.2f seconds [target %d clusters]`
11. `Adaptive border clusters (%.fs)`, `Assigned %d clusters in %.2f seconds`
12. `Sample vis for %d clusters` - the ray casting, `Traced %s rays in %.3f seconds`
13. `CNeighboringClustersList::Build()`, `NeighborsScan BuildTracePointsForClusters()`
14. `Raytraced sun visibility`, `Computed %d clusters visible to sky`
15. `Compute enclosed cluster lists` -> `%d cluster lists, %d elements`
16. `Collapsing resolution of %d nodes`, `Reduced node count from %d to %d`
17. `%d unique masks (of %s regions)`
18. `Wrote vis resource %s bytes`

Note where the time goes: the two merge stages, 8 and 9, are 400 of Mako's
seconds. The ray casting is not the headline cost, which is the same surprise
lighting produced.

### Three cache files

`visbuilder` reads and writes `<dir>/<map>/<map>.rte`, `.viscfg` and `.los`.

- **`.rte`** is the ray trace scene. `You must build with -world before you can
  build with -vis! Couldn't find raytrace.rte` proves vis is a separate phase over
  a cached intermediate.
- **`.los`** is a line-of-sight cache carried between compiles:
  `Loaded %d LOS hints from %s!`, `Wrote %d LOS hints for next time %s!`,
  `Found %u useful LOS in hint set.` There is a `-debuglos` switch, and a memory
  bound: `Note, removing %u lines of sight, too much memory!`
- **`.viscfg`** is per-map config; `Error: %s loading vis config!` on a bad one.

None of the three survives a compile here - `CMapBuilderContext` has
`ClearIntermediateDirectory`, `ClearIntermediateFilesExcludingFileType` and
`CleanupIntermediateCruft`, and a full-tree search finds no `.rte` on disk. Where
they live during a build is not yet established.

### Knobs

`resourcecompiler.dll` reads these from a `ResourceCompiler { VisBuilder { ... } }`
block in `gameinfo.gi` (the block is present but commented out in csgo's, and
`csgo_imported/gameinfo.gi` shows the shape):

```
ResourceCompiler/VisBuilder/BaseVoxelSize
ResourceCompiler/VisBuilder/MaxVisClusters
ResourceCompiler/VisBuilder/DeterministicBuild
ResourceCompiler/VisBuilder/PreMergeOpenSpaceDistanceThreshold
ResourceCompiler/VisBuilder/PreMergeOpenSpaceMaxDimension
ResourceCompiler/VisBuilder/PreMergeOpenSpaceMaxRatio
ResourceCompiler/VisBuilder/PreMergeSmallRegionsSizeThreshold
```

`MaxVisClusters` is the 4,096 clamp every real map sits on, and the pre-merge
thresholds feed the stages that cost the 400 seconds. These are untested so far;
that they exist and where they are read from is what is established.

### Running vis on its own, as an oracle

Valve's builder can be re-run per change instead of per full compile, which is
what makes it usable as a comparison oracle. The recipe, and every part of it was
needed:

```bash
rm "<game>/csgo_addons/<addon>/maps/<map>.vpk"
resourcecompiler.exe -nop4 -game <game/csgo> -i <content>/maps/<map>.vmap     -world -vis -fshallow
```

`tools/vis/rebuild_vis.py <addon> <map> --runs N` does this and prints the stage
timings and the hash of the resulting `world_visibility.vvis_c`.

Four things about it are not obvious, and each cost a wrong run to find:

- **Visibility is built by the `-world` phase, not `-vis`.** Run both and every
  vis stage prints under `Building 'world'` while `Building 'vis'` prints nothing
  at all.
- **`-vis -f` is a hard FAIL**, not the warning its message sounds like. The force
  flag has to be `-fshallow`.
- **The up-to-date check is on the source CRC, not its timestamp**, so touching
  the `.vmap` does not force anything. That is the same CRC a compiled resource
  records in its RED2. Removing the output VPK is what invalidates.
- **`-world` alone rebuilds visibility but never repacks the VPK**, so the result
  is invisible and the map's VPK is simply gone. Both phases have to be passed.

A skipped phase prints no stage lines and silently leaves the previous file in
place, which looks exactly like a fast deterministic rebuild. The harness says so
loudly rather than reporting a hash of yesterday's output.

On ze_hold_em_p a forced rebuild is **13.5 seconds, of which 8.2 is visibility**,
against 50 for a full compile.

### The builder is deterministic, its ray scan is not

Two forced rebuilds of ze_hold_em_p produce a **byte-identical** `vvis_c`
(sha256 `135ae01c…` both times) while the LOS scan reports a different number of
useful rays each run, 3,940 against 3,954. So the sampling is threaded and racy
but the result it feeds converges, and there is also a `DeterministicBuild`
setting that this default build evidently did not need.

That is the best possible baseline for an error margin: Valve against Valve is
**zero** difference, so any deviation a replacement shows is entirely its own and
none of it is noise in the oracle.

## Comparing two compiles

Two vis builds of the same map do not agree on cluster numbering or count, so
their PVS matrices cannot be diffed bit for bit. The comparable question is the
one the game asks: **can a viewer at point A see point B**. That is well defined in
any build, so a point-pair sample gives a confusion matrix:

- both visible, both hidden - agreement
- Valve visible, ours hidden - **holes**, the dangerous direction; geometry the
  player should see is culled away and the world has gaps in it
- Valve hidden, ours visible - **overdraw**, only a performance cost

A replacement is allowed to be looser than Valve's (more overdraw) and is not
allowed to be tighter in a way that hides geometry. Those two numbers plus the
mean-visible fraction are the error margin to hold a builder to.

```bash
s2c vis-diff <reference.vvis_c> -c <candidate.vvis_c> [--points 1500] [--seed N]
```

It exits non-zero when there is any hole, so it can gate a build.

### Sample the space, not the bounding box

The sampler draws from the space the REFERENCE says exists, by picking one of its
occupied 4x4x4 leaf cells with probability proportional to that cell's volume and
then a uniform point inside it. That is uniform over occupied space and every
draw lands in a cluster.

The first version sampled the bounding box uniformly and it was unusable: on
ze_eizures_b1_1 that placed 800 points in **139,636 draws**, a 0.57% hit rate,
because a map's bounding box is almost entirely solid rock and outside. It also
biased whatever survived toward large open volumes. Occupied sampling places
**100% of draws** on every map tried.

Every pair of placed points is compared, so 1,500 points is 1.1 million pairs.
Points the candidate declines to place are counted separately as placement
disagreement rather than folded into the visibility numbers: a build that
disagrees about where space IS is not being compared on visibility at all, and
above a few percent there the pair statistics are comparing two different maps.

The instrument is checked against differences constructed so the answer is known:
a map against itself gives exactly zero holes, zero overdraw and 100% agreement
over 319,600 pairs; clearing a seventh of the PVS gives holes and **no** overdraw;
an all-visible PVS gives overdraw and **no** holes.

### A real reading

ze_hold_em_p exists both as its published workshop build and as our own
resourcecompiler rebuild, so the two can be compared directly:

| | clusters | mean visible |
|---|---|---|
| workshop build | 92 | 95.8% |
| local rebuild | 260 | 64.3% |

With the workshop build as reference the local rebuild shows 1.18% holes and
**zero** overdraw; reversed, the workshop build shows 40.7% overdraw. So the local
build is strictly the tighter of the two, which is what the asymmetry is supposed
to show.

Two cautions come straight out of that run. The workshop build sees 95.8% of the
map from an average cluster, against 14% on a typical ZE map, so it is nearly no
visibility at all and a poor calibration subject. And placement disagreement was
2.75% one way and **34.4%** the other, which is far too high to attribute the
difference to settings: these are builds of two different `.vmap` sources, a port
against the author's original, not one source compiled twice. A clean calibration
still needs the same source compiled twice at different `MaxVisClusters` /
`BaseVoxelSize`.

## A shadow install, for changing settings safely

The VisBuilder knobs live in gameinfo.gi, so measuring them means editing it.
Editing the real one is not on while the game is running, and copying the install
is not on either: `game/csgo` alone is 60 GB against 92 GB free.

`tools/vis/new-shadow-cs2.ps1` builds one out of links instead. Directories become
junctions and files become hardlinks, both pointing at the same bytes on the same
volume, and the ONE file that has to differ is copied for real. 21 junctions, 529
hardlinks, and a 9 KB gameinfo.gi. Free space did not move.

Compile against it with `-game D:\cs2-shadow\game\csgo`. Verified equivalent:
`fsutil hardlink list` shows the shadow's gameinfo.gi alone in its own group while
`pak01_dir.vpk` lists both paths, and the same map compiled both ways produces
**byte-identical DATA and VXVS**.

One caveat that comes out of that check, and it corrects the determinism claim
above: the two containers are NOT byte-identical, because RED2's
`m_nFingerprint` differs (2356852877 against 1695646057). That field is compile
identity, not visibility. So `vis_digest()` hashes DATA and VXVS only, and
"identical" in this file means identical visibility.

## How far the knobs move it

ze_hold_em_p, each setting compiled in the shadow and scored against the stock
compile with `vis-diff` at 2,000 points:

| variant | voxel nodes | clusters | vis time | placement lost | holes | overdraw |
|---|---|---|---|---|---|---|
| baseline (8 units) | 81,625 | 258 | 9.73 s | | | |
| `BaseVoxelSize 16` | 20,585 | 156 | 4.07 s | 1.05% | 1.52% | 0% |
| `BaseVoxelSize 32` | 6,905 | 151 | **1.23 s** | **25.3%** | 0.49% | 33.0% |
| `MaxVisClusters 128` | 81,625 | 125 | 10.46 s | 0% | 1.99% | 0.44% |
| `MaxVisClusters 512` | 81,625 | 258 | 10.65 s | identical to baseline | | |

`MaxVisClusters 512` reproducing the baseline exactly is the control that proves
the block is read at all: the clamp moved from 1342 to 510 in the log, but this
map only makes 258 clusters so it never binds.

**`BaseVoxelSize` is the speed lever and it is not free.** 32 units is 8x faster
end to end, and it loses a QUARTER of the space the baseline places points in:
navigable volume the coarse voxelization never learns exists. That is a worse
failure than either holes or overdraw, because those at least presuppose the two
builds are describing the same world. 16 units costs 1% of placement for 2.4x.

**The calibration number:** a legitimate settings change moves point-pair
agreement by 1.5 to 2%. A replacement landing within a couple of percent of
Valve's is inside the range Valve's own settings span, and that is the scale our
builder's error should be read against.

**With the caveat that this map is a poor subject.** ze_hold_em_p sees 64.3% of
clusters from an average cluster, and volume-weighted about 97% of sampled pairs
are visible, so there is very little hiding for a variant to get wrong. A tightly
occluded map like ze_raccoon_facility_p at 5.0% mean visible would discriminate
far better, and this table should be redone on one.

## What is implemented

| piece | state |
|---|---|
| VXVS decode and encode | byte-exact on 112 maps, 481 MB |
| DATA index derivation | equals Valve's numbers on 112 maps |
| structural invariants | every index in range, one octree, no orphans |
| PVS row law and bit order | pinned on 112 maps |
| point and visibility queries | agree with an independent reader on 84,673 points |
| occupied-space sampler | 100% of draws placed, on every map tried |
| point-pair comparison (`s2c vis-diff`) | zero on identical input, correct sign on constructed differences |
| the builder | not started |

## Where a replacement starts

1. ~~Force Valve's builder to re-run~~ done, 13.5 seconds a round on a small map,
   and its output is byte-stable.
2. ~~Build the point-pair sampler~~ ~~and calibrate it on settings~~ done, see
   "How far the knobs move it" above. Owed: redo that calibration on a properly
   occluded map, because ze_hold_em_p is too open to discriminate.
3. Then voxelize and region generation, scored against Valve's own counts on the
   same map before any PVS is computed at all. ze_hold_em_p is the reference:
   81,625 voxel nodes, 10,554 regions in, 103,358 regions out, collapsed to 4,194,
   258 clusters against a target of 1,342, and 129 unique masks.
