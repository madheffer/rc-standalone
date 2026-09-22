# Visibility: the structure, the tool, and where a replacement starts

Visibility is not the expensive half of a CS2 map compile. It is **almost all of
it**. resourcecompiler times the phase itself and says so:

    Visibility complete in 1088.67s.

on a 1,299 second compile of ze_ffvii_mako_reactor_v6_p. **83.8%.** Lighting is
16.02 seconds, 1.2%.

An earlier note in this file put visibility at "about 400 seconds, 30%". That was
wrong, and wrong in an instructive way: it came from summing the stages we had
already recognised and it missed the LOS ray scan, 568 seconds, entirely. The
number to quote is the one the tool prints about itself.

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
6. `Outside detection took %.2f seconds` - at RUNTIME this comes immediately
   after voxelize, before anything below it, which is what makes stage 7's region
   count a count of ENCLOSED regions
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

## Where the 1,088 seconds go

resourcecompiler times these itself, so they survive output buffering:

| stage | seconds | share of vis | what it did |
|---|---|---|---|
| **LOS ray scan** | **568** | **52%** | 4,567,126,453 rays cast, 11,923,366 useful (0.26%) |
| Generated clusters | 231.5 | 21% | 751,270 regions |
| Merged cluster lists | 173.0 | 16% | down to 25,471 clusters |
| Voxelize (8 units) | 20.5 | 2% | 2,643,577 nodes |
| Outside detection | 6.9 | 0.6% | |

The ray scan splits across generators, and one of them is remarkable:

| generator | seconds | rays | useful |
|---|---|---|---|
| `CBoundaryPointsRayGenerator` | 309.0 | 3,923,657,587 | 1,633,010 |
| `CLargeClusterRegionsRayGenerator` | 252.0 | 608,325,057 | **383** |
| `CClusterCenterRayGenerator` | 7.6 | 35,143,809 | 10,289,973 |

**`CLargeClusterRegionsRayGenerator` spends 252 seconds casting 608 million rays
to find 383 useful ones.** That is 19% of the whole compile for a hit rate of six
per ten million. Whatever it is for, it is the most obviously attackable number in
the build.

Two conclusions follow. Ray casting IS the dominant cost of a map compile, which
is the opposite of what the lighting investigation found for lighting, and it is
embarrassingly parallel: 4.6 billion independent rays is exactly the workload a
rented GPU is for. And the clustering stages behind it, 404 seconds between them,
are ordinary CPU graph work on 751,270 regions.

## Visibility is not a file you can swap

Compiling ze_hold_em_p twice, identically except `BaseVoxelSize 32`, and diffing
the two map VPKs with `s2c map-diff`: **11 of 72 files differ**, not one.

| what changed | files |
|---|---|
| `world_visibility.vvis_c` | 1 |
| world node render geometry (`.vmdl_c`, MVTX/MIDX/MDAT all differ) | 6 |
| `n0.vwnod_c`, the world node index | 1 |
| `world.vwrld_c` | 1 |
| `default_ents.vents_c`, the entity lump | 1 |
| a light probe volume octree `.dat` | 1 |

The compile stage `Splitting geometry using visibility...` is why: render meshes
are cut along cluster boundaries, so a different clustering produces different
geometry, a different world node, different entity data and different light probe
placement.

**So a replacement cannot be dropped in as one file.** Anything that changes
clustering changes five other kinds of resource downstream, and shipping a new
`.vvis_c` beside the old geometry means shipping an inconsistent map. This is the
most likely shape of the failure the cs2map project hit, where geometry, props and
collision misbehaved after iterating the compile.

## Where the intermediates actually live

Not in the game tree, which is why an earlier search found nothing:

    %TEMP%\csgo_addons\<addon>\maps\<map>.rte        23 MB on Mako
    %TEMP%\csgo_addons\<addon>\maps\<map>.viscfg     1,308 bytes

They survive the compile. The `.los` line-of-sight cache does **not** get written
at all, so the 568 second ray scan is paid in full on every compile. That is not a
missing flag, and the next section is why.

### The LOS cache cannot be turned on

`CVisBuilder::Build` (visbuilder.cpp:106) sets up all three paths correctly. It
reads `%TEMP%`, formats `<temp>/<...>/<map>.rte` and `.viscfg`, and builds the
`.los` path from the map's content path with `content\` replaced by `game\`.
It puts all of them in a KeyValues named `vvis` as the keys `rte`, `viscfg`, `los`
and `los_errors`, which the vis entry then reads back. The observed load path
matches exactly, and the load happens: `Loaded 0 LOS hints from ...`.

The writer is `FUN_180049370`, and its call site is **unconditional** - it runs on
every vis build. It returns immediately:

```c
uVar14 = *(uint *)(param_2 + 4);      // hints in the set
if (uVar14 == 0) goto LAB_180049775;  // return, writing nothing
```

The hint set is empty, so nothing is written, and neither
`Wrote %d LOS hints for next time` nor `Failed to write %d LOS hints` is ever
printed. Nothing in the shipped path populates the set: the two messages that
would say so, `Updating LOS hints (%u given) in hint set` and
`Found %u useful LOS in hint set`, do not appear either. The 11,923,366 "useful
LOS rays" the scan reports are not hints in that set.

`-debuglos` does not change this. It is parsed, and it only raises a cap inside
the writer, from `0x200000` to `0x2000000` hints. Confirmed by running a compile
with it: the log prints `Increased LOS limit for debugging!` and still no `.los`
appears, on a map whose scan found 3,949 useful rays, far under even the low cap.

**So the cache is inert in the shipped build and cannot be enabled from outside.**
The one opening left is that the LOADER works. A `.los` we write ourselves would
be read, which makes the cache reachable by authoring its format rather than by
finding a switch. That format is not decoded yet.

### CLargeClusterRegionsRayGenerator: measured, not yet explained

It is the worst line in the compile: **252 seconds, 608,325,057 rays, 383 useful
results**, run in 2 passes, 19% of the whole build for six hits per ten million
rays. For contrast `CClusterCenterRayGenerator` finds 10,289,973 useful results
from 35,143,809 rays in 7.6 seconds.

It does not run at all on ze_hold_em_p, so it is triggered by scale, presumably
cluster or region size. What it samples and why it misses so completely is NOT
established: the class is only ever invoked through a base pointer, its name
string has no code references, and walking its RTTI to a vtable did not resolve
(x64 MSVC stores those links as 32 bit RVAs and the scan for the descriptor's RVA
found no complete object locator). Measurements above are real; the mechanism is
still open.

`.viscfg` is ordinary binary KV3 and decompiles with the KV3 reader this project
already has. It holds `pvstype`, `vDirToSun`, and the map's `visibility_hint`
entities with their `hintType` and bounding boxes. All of it derives from the map
source, so it is authorable with what is already built.

### Two traps

`-vis` WITHOUT `-world` repacks the VPK without the world content: it turned
Mako's 74 MB map VPK into 84 KB. It does not rebuild visibility even when the
`.rte` is present and the output has been deleted, so it buys nothing and costs
the map.

And when resourcecompiler's stdout is a pipe it block buffers, so arrival timing
is not stage timing. 5,733 Mako lines arrive in a handful of bursts, which
produced a convincing but fictional "798 second" stage on the first profiling run.
`profile_compile.py` now prints the tool's self-timed numbers first and marks any
gap that ends in a burst as a buffered flush.

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

## Stage 2: voxelize, and what it is scored against

The compile prints a number for this stage, so it can be scored without a geometry
pipeline existing:

```
Convert RTE with 4536 triangles in 0.01s
Voxelize (8 units) took 0.49 seconds (81,625 nodes)
```

`VisVoxelizer` is that stage and `VisVoxelizerTests` is the score.

| map | traced triangles | compile | ours | off by |
|---|---|---|---|---|
| ze_hold_em_p | 4,536 | 81,625 | **81,625** | **exact** |
| atixref | | 311,305 | 311,745 | +0.14% |
| cardtest | 92 | 17,297 | 17,345 | +0.28% |
| probe01 | 80 | 17,169 | 17,217 | +0.28% |

### The shape of the tree, read off Valve's own output

**Nodes are one root plus eight per branch.** 81,625 is 1 + 8 x 10,203 to the
unit, and 17,297 is 1 + 8 x 2,162, so every branch owns exactly eight children and
the count is a count of BRANCHES in disguise.

**The root is a cube, and its size is per map.** The compiled file states it:
ze_hold_em_p is 32,768 units over a 4,096 voxel side, cardtest 4,096 over 512.
Reading it as a constant is wrong and costs three levels of depth on a small map.

**A leaf is four base voxels, not one.** A leaf carries a 4x4x4 occupancy mask, so
the octree stops at 32 units and the 8-unit resolution lives in the mask. Taking
the leaf as the base voxel gives 1.2 million nodes against a target of 81,625,
which is the single biggest thing to get right here.

### The tree is built DOWNWARDS, and that is not an implementation detail

`18002e310` starts at the root and asks the kd tree whether any triangle reaches
into each child's box, recursing into the ones that do. A marking pass that
voxelizes each triangle and then derives the tree bottom-up gets close, and it
cannot be made exact, for two reasons:

**Which triangles count depends on the box.** The query mask is `0x811` while a
box is wider than 256 units and `0x1811` at or below it, and the mask rejects a
triangle carrying any of its bits. By the time the trace scene exists the only
live bit is `0x1000`, so the rule is: below 256 units a `0x1000` triangle stops
being occupancy. ze_hold_em_p has none of them; the two probe maps have eight
each, and that alone was the difference between exact and 43% high.

**A node becomes a branch on SIZE alone.** Once its parent has found geometry in
it, a node subdivides if it is wider than four voxels, whether or not any of its
own eight children still see anything. That is how a `0x1000` triangle carries two
coarse levels of branches on its own, and a bottom-up pass cannot represent it
because there is no occupied leaf underneath.

### The check that does not depend on a tolerance

A node count can be hit by accident. The stronger test is that **every branch of
the SHIPPED octree is a branch of ours**: the shipped tree is this one collapsed,
and a collapse only removes nodes, so anything it still subdivides we must
subdivide too. That is now **100% on all three maps**, and `VisVoxelizerTests`
asserts nothing less.

### What the probe maps are still 0.28% wide on

Six branches each, identically on both. They are no longer a decode problem: with
the `.rte` phase corrected every traced triangle of both maps rebuilds and the
rebuilt box is the header's own to the unit. Six branches out of 2,152 is open
and recorded as such.

## Stage 3: regions, and how outside detection actually works

The stage order is not what the source strings suggested. The compile runs outside
detection IMMEDIATELY after voxelize and counts regions only afterwards:

```
Voxelize (8 units) took 0.41 seconds (81,625 nodes)
Outside detection took 0.10 seconds
Generated clusters for 10554 regions in 4.34 seconds
93354 clusters generated
```

So 10,554 is a count of the regions the map ENCLOSES, not of the regions the
octree holds. That is what makes it small: ze_hold_em_p's octree has 71,121 leaves
and 73,659 regions in total.

### What a region is, and what is implemented

A region is one connected run of open voxels inside one leaf, which is exactly
what the compiled file's 4x4x4 masks hold. `VisRegions` enumerates the octree's
leaves and cuts each one up, and `VisRegionsTests` pins the properties that must
hold whatever the stages above do with them: the leaves account for the whole
tree, two regions of a leaf never claim the same voxel, a leaf's regions cover its
open voxels exactly, and no leaf above the smallest holds geometry.

| map | leaves | regions | leaves holding geometry |
|---|---|---|---|
| ze_hold_em_p | 71,121 | 73,659 | 28,782 |
| cardtest | 21,309 | 28,464 | 8,609 |
| probe01 | 21,218 | 28,367 | 8,584 |

### The `.rte` does not seal the map, so outside detection is not a flood fill

This was measured before it was confirmed in the binary, and the section after
next has the confirmation. Voxelize ze_hold_em_p's geometry onto a dense 8-unit grid over its own
bounding box, pad it, and flood from the padding: **every one of its 6,875,040
open voxels is reached**. Nothing is enclosed. cardtest is the same, all 2,230,159
of them.

The reason is visible in the faces of the map's own box, as the fraction of each
one that voxelizes solid:

| face | ze_hold_em_p | cardtest |
|---|---|---|
| x lo / x hi | 50.0% / 54.2% | 53.4% / 2.7% |
| y lo / y hi | 93.3% / **0.2%** | 63.7% / **5.0%** |
| z lo / z hi | 51.2% / **1.5%** | 85.2% / **1.1%** |

ze_hold_em_p has a wall along y lo, end caps on x, a partial floor, and **no
ceiling and no far wall at all**. The ray trace scene is the geometry a LOS ray
needs to hit, and it is under no obligation to be closed. So whatever "outside" is
in this compiler, it is not "cannot reach the world box".

### Three candidates measured and rejected

None of these is close enough to be a near miss, which is why none was adopted:

| candidate | ze_hold_em_p | cardtest | probe01 |
|---|---|---|---|
| every leaf's regions | -28.7% | -7.6% | -7.5% |
| flood fill from the world box | -100% | -100% | -100% |
| leaves meeting the geometry's own box | +323% | +149% | +151% |

The flood fill is not a coding error: it returns zero because zero open voxels are
enclosed, which the dense grid proves independently of the octree.

### Outside detection, from visbuilder.dll

It is not a flood fill, which is what the measurements already said and what the
code now confirms. `1800321f0` is the only function referencing `Outside
detection took %.2f seconds`, and the stage driver `180031f00` calls it directly
after voxelize, so the order in the log is the order in the source.

**It classifies each region on its own and lets the answer propagate along rays.**

1. Collect every region whose flag bit 1 is clear.
2. A thread pool job named `InitialRegionStatus` (`18003f1e0`) gives each one a
   status byte, by building its box and handing it to `18004a2f0`. That is the
   SEED, and it is a sampling test: `18004b260` gathers over the box and returns
   four counters, and a threshold tree over them returns inside, outside or
   undecided. An earlier note here called the seed a bounds test; that is the
   early-out inside the per-region classifier below, not the seed.
3. Any region at status 2 gets flag bit 0 set, which means OUTSIDE.
4. Every region still at status 0 is then classified by `18002e050`, and any that
   comes back 2 gets bit 0 as well.

`18002e050` takes the region's box, built from its leaf's box and its 4x4x4 mask
by `18010be50`, and:

- returns **2 immediately if the box's centre lies outside a bounds box** held at
  `this+0xe8`. This is an early-out, not the seed.
- otherwise gathers candidate rays for the box, casts each one with `18002deb0`,
  and counts the answers. It returns 1 when the inside count beats a threshold
  held beside the gather, and 2 otherwise. So it is a VOTE, not a single test.

`18002deb0` is the part that makes this work on geometry that does not seal. It
marches one ray through the voxel octree:

- at a branch it takes the octant children the ray enters
- at a leaf it computes a 64-bit mask of which of that leaf's 4x4x4 voxels the ray
  crosses, and intersects it with each of the leaf's region masks
- it returns **2 the moment it reaches a region whose bit 0 is already set**, and
  1 if it finishes having touched a region already known inside

So outsideness spreads from the bounds seed along rays through the voxel grid,
one region at a time, rather than through voxel adjacency. A region deep in a
corridor stays inside because its rays terminate on nearby geometry before they
reach anything already flagged, and that holds whether or not the map has a
ceiling. This is why a corridor with an open top still produces inside regions,
and why the flood fill could not.

### Outside detection, implemented and scored

`VisOutside` is the port, and the count it produces is scored against the
compile's own:

| map | traced triangles | compile | ours | off by |
|---|---|---|---|---|
| ze_hold_em_p | 4,536 | 10,554 | **10,554** | **exact** |
| cardtest | 92 | 7,416 | 7,395 | **-0.28%** |
| probe01 | 80 | 7,316 | 7,261 | **-0.75%** |

ze_hold_em_p landed on the compile's own count the moment the voxelization under
it became exact, which is the strongest evidence the propagation is right:
nothing in it changed to get there.

### The seed had to be ported, and the way that was proved is the point

The two probe maps sat at -49% while ze_hold_em_p was exact, with everything
upstream of them measured and right. What settled it was sweeping the one part of
the pass that was OURS rather than Valve's - a 26 direction "is it blocked" vote
with two ratios - over twenty threshold pairs on both maps:

| | 70% | 80% | 83.3% | 90% | 96% |
|---|---|---|---|---|---|
| ze_hold_em_p, target 10,554 | 10,554 | 10,554 | 10,554 | 10,554 | 10,554 |
| probe01, target 7,316 | 6,646 | 6,580 | **3,756** | 2,837 | 2,809 |

ze_hold_em_p does not move at all; probe01 moves by 137%, with a cliff between
requiring 20 of 26 rays blocked and requiring 21. So the approximation was
deciding nothing on one map and half the answer on the other, and no amount of
tuning it would have been anything but a fit.

**The polarity is the thing to get right, and it is the opposite of the obvious
one.** `18002deb0` returns outside only when a ray reaches a region whose flag bit
is ALREADY set, never when it leaves the world, and the vote in `18002e050` makes
a region inside only when enough rays came back having touched a region already
known inside. So outside is the default and inside is earned. Implemented the
other way round, every region on an unsealed map comes out outside, which is the
-100% the flood fill produced and is not a coincidence: both are the same mistake.

**Both of the knobs here are ours, and NEITHER is carrying the answer.** That
matters more now than it did at -2.59%, because a number we chose landing on
Valve's to the unit is either the answer or a fit, and moving the knob is how to
tell them apart:

- the ray reach, over a factor of 32 from 64 units to 2,048, moves the count by
  **0.14%**
- the seed's two thresholds, over twenty pairs from 70%/10% to 96%/45%, move it
  by **0.00%**: every one of the twenty gives 10,554

The second is the stronger of the two. The seed is the part of the pass that is
least like Valve's, whose thresholds are a tree over four undecoded counters, and
it turns out not to decide anything: the propagation does, and the propagation is
ported. `VisOutsideTests` pins both, so a future change that starts depending on
either fails rather than passing quietly.

### The propagation, and a case where porting it made the number worse

`1800321f0` is exactly two passes and does NOT iterate to a fixed point. The seed
runs over every region in a thread pool; a region it called outside has its flag
bit set; then a SEQUENTIAL pass classifies whatever is left, in region order,
each one seeing the verdicts of the ones before it.

`18002e050` is that second pass. It casts the same 5x5-a-face grid the seed does
and marches every ray that landed on a surface FACING it, stopping 8 units short
of what it hit. `18002deb0` walks the octree along that segment and answers:

- **outside** the moment it touches a region whose FLAG is set, which wins outright
- **inside** at the end, if any region it crossed is currently marked inside
- nothing at all otherwise

and the vote is two integer thresholds over `n`, the rays a face carries:

```
inside if (inside > 2n) or (inside > n and outside < 5)
```

That condition was checked against the INSTRUCTIONS rather than the decompiler's
parenthesisation, because a three-term test with two short circuits is exactly
where a reading can go wrong. `MOV ECX,[RSP+0x90]; LEA EAX,[RCX+RCX*1];
CMP EDI,EAX; JG inside; CMP EDI,ECX; JLE outside; CMP ESI,5; JL inside`, and
`[RSP+0x90]` is the gather struct's `+0x30`, which `18004b260` sets to `n*n`.

**It is ported and it made ze_hold_em_p worse, and that is recorded rather than
reverted.** The seed ALONE gives that map exactly 10,554 enclosed leaves; the
second pass adds 831 more, which the compile does not count:

| | regions | clusters | target |
|---|---|---|---|
| ze_hold_em_p | 11,385 (+7.87%) | 95,978 (+2.81%) | **1,344, exact** |
| cardtest | 7,443 (+0.36%) | 80,991 (-1.22%) | **864, exact** |
| probe01 | 7,312 (-0.05%) | 80,678 (-1.26%) | **864, exact** |

Three things are known about those 831. They carry almost no VOLUME - the target
cluster count is unchanged and still exact - so they are single-voxel slivers.
They are in leaves the shipped file also has live, and the leaf-by-leaf agreement
IMPROVES with the pass on (1,506 to 1,628 matched, 1,660 to 1,538 missed). And the
old approximation that scored exact here was a 26-direction vote iterated to a
fixed point, which is not what the binary does at all.

So the honest reading is that the slivers should not be regions in the first
place, which puts the remaining error in the leaf masks rather than in this pass.
Reverting to the approximation would buy the headline number back and lose the
one thing that makes the number worth anything.

### What the seed actually is

`18004b260` casts a **5 by 5 grid of rays through each of the region box's six
faces**, 150 in all, from the box centre, and tallies four counters:

| counter | what it counts |
|---|---|
| A | landed on an ordinary surface, with the centre on its front side |
| B | landed on the BACK of a surface |
| C | landed on a nodraw or coarse-only one (`flags & 0x1030`), facing |
| D | hit nothing at all |

`18004a2f0` then runs a threshold tree over them, with `n` = 25 rays a face and
`rays` = 150:

```
if ((n <= D + B || (A <= rays/3 && C + A <= rays/2)) && (B > 4 || C + A <= 2n)):
    if (n <= B):                                    return OUTSIDE
    if (A <= rays/2 or A + D <= rays * 0.8):        return UNDECIDED
return INSIDE
```

Reading it out is one thing; being able to RUN it is another, and that needed a
real ray tracer rather than the voxel march. The `.rte` carries its own kd tree,
which decodes cleanly: a node is 8 bytes, `axis = w0 & 3` with 3 meaning leaf and
`w0 >> 2` the first child or the first triangle index, then a float split or a
`u32` count. Walking it, **every split of both specimens lands inside the file's
own world box, every leaf index is in range, and the leaf triangle counts sum to
exactly `C`** - 5,983 on ze_hold_em_p and 788 on probe01. That is the same
structural confirmation the tiling gave for the sections, and it is what makes
the 150 rays a region affordable: ze_hold_em_p's 4,548 triangles sit in 933
leaves of at most ten each.

`VisSeed` is the port and `RayTraceEnvironment.Trace` the tracer. The reach the
PROPAGATION gives a ray is still ours, and the spread test above still holds it.
The seed's grid is Valve's own 5, passed as a literal at the call site, and it is
swept anyway:

| rays a face | 3x3 | 4x4 | 5x5 | 6x6 | 8x8 |
|---|---|---|---|---|---|
| ze_hold_em_p, target 10,554 | 10,888 | 10,554 | **10,554** | 10,554 | 10,554 |

Four and up are identical, so the sample count is not producing the answer
either; three is too coarse to resolve a 32 unit leaf and says so.

### Where the fix landed, leaf by leaf

`VisLeafDiffTests` puts our leaves against the shipped ones rather than comparing
totals, which is what a count that is half wrong cannot tell you:

| probe01 | agree live | agree dead | only shipped | only ours |
|---|---|---|---|---|
| the 26-ray vote | 1,819 | 3,915 | 2,814 | 0 |
| Valve's seed | **4,459** | 3,913 | **174** | 2 |

The asymmetry is what the test asserts. The shipped tree is collapsed and its
regions compacted, so a leaf it keeps live that we do not is expected; a leaf WE
call enclosed that the compile dropped entirely is a claim on space that is not
there, and there are two of those on probe01 and none on ze_hold_em_p. The 174
that remain sit in one corner of the map, around x 1,120 to 1,280 at z 272, which
is where its geometry box ends.

### Cluster generation: the birth rule is implemented, the merge is not

The reasoning behind the whole visibility port, and Valve's design decisions that
forced ours, is in `VISBUILDER_ANALYSIS.md`.

The next number after the region count is `93354 clusters generated`, and the
path to it is known end to end. `VisClusters.Born` implements the birth rule and
`VisMergeCost` is the cost that brings the count down; what is missing between
them is the ray sample that gives each cluster its visibility bits.

`CVoxelSampler3::MergeInsideRegions` (`180034ea0`) gathers the enclosed regions,
sizes a vector of 24-byte records one per region, and dispatches a thread pool job
(`18003ddb0`) that calls `180032d80` for each. **The logged count is a sum**: the
log walks that vector at stride 24 and adds the first int of every record. So
93,354 is 10,554 per-region counts added up, a mean of 8.85, and cardtest's 81,991
is 7,416 of them at a mean of 11.05.

Per region, `180032d80` asks `18002beb0` for candidate boxes and then walks the
region's 64 mask voxels, assigning each to a candidate. `18002beb0` is recursive
subdivision: it holds the region's box against three lists of splitter boxes on
the sampler and, for each that contains it, calls `18002bcd0`, which splits and
recurses back in. A box that survives with no splitter left is emitted.

### The three splitter lists are HINTS, and hints are authored by the mapper

`18002b500` fills all three, and it fills them by reading ENTITIES. It walks the
map's objects and pulls four keys off each: `origin`, `box_mins`, `box_maxs` and
`hintType`. The type picks the list:

| hintType | what it is | list |
|---|---|---|
| 4 | x-axis split hint | `this+0xa0` |
| 5 | y-axis split hint | `this+0xb8` |
| 6 | z-axis split hint | `this+0xd0` |
| other | voxel hint | handled separately |

### What the birth rule turns out to be, and why it needs the cost

`180032d80` walks the region's 64 mask voxels and appends **one cluster record per
voxel**, so the count before any merging is simply the sum of the popcounts of the
enclosed regions' masks:

| map | enclosed regions | born | compile | mean voxels a region |
|---|---|---|---|---|
| ze_hold_em_p | 10,554 | 585,920 | 93,354 | 55.5 |
| cardtest | 3,772 | 123,038 | 81,835 | 32.6 |
| probe01 | 3,756 | 122,782 | 81,707 | 32.7 |

So the merge is not a rounding correction on ze_hold_em_p; it removes six out of
every seven clusters. `1800337a0` is what does it, and the loop condition is an OR
that is easy to misread:

```
while (32 < count || bestCost < threshold)
```

It does not stop at 32. It merges the cheapest pair while there are more than 32
OR while the cheapest merge still costs under 20, so a region whose voxels all see
the same things collapses far below 32. The compile prints
`MergeClusterSet costs after first pass min:20.0`, and that 20.0 is the threshold
constant exactly: the loop stops when nothing cheaper is left.

With `VisMergeCost`'s floor of `distance + 10`, two clusters that see identically
merge while their boxes are within ten units of each other. A region IS one
connected run of open voxels, so if its voxels all see alike the whole of it
collapses to a single cluster.

### And that reproduces the count exactly

Split ze_hold_em_p's enclosed regions by whether the merge runs on them at all:

| | regions | clusters |
|---|---|---|
| 32 open voxels or fewer, never merged | 2,672 | 85,472 |
| more than 32, merged | 7,882 | **7,882** |
| | | **93,354** |

7,882 regions producing 7,882 clusters is one each, and 85,472 + 7,882 is the
compile's 93,354 to the unit.

### WHY it is one each, which is not what it first looked like

The obvious reading is that every voxel of a region sees the same things, so the
cost is at its floor and everything merges. That is wrong, and running the real
sampler is what proved it: the bit vectors genuinely differ, and with the cost's
floor of `distance + 10` one differing bit already costs 20 and stops a merge at
the target.

The count is one per region for two structural reasons instead.

**The live count the loop tests includes the 56 shell boxes.** `1800337a0` sets
it to `clusters + padding` and `180030a50` takes one off per merge, so a region
of at most 64 clusters can never bring 120 down to 32. `32 < live` stays true for
the whole run, and the loop ends when `180031680` finds no valid pair rather than
when the count reaches the target. The cost therefore decides the ORDER of the
merges and not where they stop.

**The candidate graph stays connected.** `180030a50` finishes by calling
`1800306e0` on the surviving cluster, which clears its candidate list and rebuilds
it from its NEW box dilated by a unit, recomputing every cost against its merged
bit vector. So a merge never strands a neighbour, and a region - which is one
connected run of open voxels by construction - collapses whole.

Implemented without either of those, the same sampler gives 180,773 clusters on
ze_hold_em_p against 93,354, and with only the first it gives 146,481.

`VisClusters.Merge` is the real thing and `VisClusters.Uniform` is the shortcut
that follows from it, and `VisClustersTests` asserts they are equal rather than
trusting the shortcut.

The probe maps are scored too now that their regions are right, and they show
where the model gives out:

| map | compile | ours | off by |
|---|---|---|---|
| ze_hold_em_p | 93,354 | **93,354** | **exact** |
| cardtest | 81,835 | 79,847 | -2.43% |
| probe01 | 81,707 | 79,529 | -2.67% |

**The residual was not in the merge.** The sampled merge and the shortcut agree
to the unit on all three maps, so it had to be upstream of both, and it was: two
things about the REGIONS, both read out of the binary.

### A region is a greedy BOX, not a connected component

`18010c3f0` is not a flood fill. It takes the lowest open voxel nothing has
claimed, grows the box as far as it will go in x, then in y, then in z, each step
requiring the whole new slab to be open, emits it, and starts again:

```
taken = solid
while an open voxel is unclaimed:
    m = that voxel
    while x < 3 and (solid & (m*2  | m)) == 0: m |= m*2
    while y < 3 and (solid & (m<<4 | m)) == 0: m |= m<<4
    while z < 3 and (solid & (m<<16| m)) == 0: m |= m<<16
    emit m & ~taken;  taken |= m
```

The growth doubles the accumulated mask rather than tracking a corner, so `m*2`
is one step in x, `m << 4` one in y and `m << 16` one in z, each bounded by the
starting cell's own coordinate so the mask cannot wrap into the next row. An
L-shaped run of open space is ONE component and several boxes.

ze_hold_em_p does not notice: its leaves are almost all either half open or fully
open, where a component and a box are the same thing, which is why a flood fill
scored exact there and was still wrong.

### The coarse-only retry happens at every depth

`18002e310` queries the KD TREE for the retry, not a filtered list, so a box that
holds nothing but `CoarseOccupancyOnly` geometry becomes a leaf wherever it turns
up. Our descent was dropping those triangles from the candidate list the moment a
box fell to 256 units, so the retry could only ever fire one level down. Carried
separately they fire at every level.

### What the two are worth

| | regions | clusters |
|---|---|---|
| flood fill, coarse-only leaves at one level | 7,403 (-0.18%) | 80,031 (-2.20%) |
| greedy boxes | 7,590 (+2.35%) | 80,808 (-1.26%) |
| **and the retry at every depth** | **7,589 (+2.33%)** | **82,259 (+0.52%)** |

probe01 lands at **+0.19%** the same way. ze_hold_em_p is unmoved and exact at
every step, which is the test that mattered: neither change is a fit, because
neither has anywhere to hide on the map that was already right.

### The printed region count is taken AFTER compaction

`180032670` runs between outside detection and the cluster stage, and it
collapses every leaf's regions to at most three: the union of its enclosed
masks, the union of its outside ones, and its solid voxels, tagged 0, 1 and 2 in
the region's own low bits. Only the first is counted or clustered.

So "Generated clusters for N regions" is **the number of leaves holding any
enclosed space at all**, not the number of enclosed boxes. The order in
`180031f00` is unambiguous - voxelize, `1800321f0`, `180032670`, then
`MergeInsideRegions` which prints the count - and reading it in the wrong order
is what left the probe maps 2.3% high on a number that is not the one the
compile prints:

| enclosed regions | before | after compaction |
|---|---|---|
| ze_hold_em_p | 10,554 (exact) | **10,554 (exact)** |
| cardtest | 7,589 (+2.33%) | **7,445 (+0.39%)** |
| probe01 | 7,445 (+1.76%) | **7,314 (-0.03%)** |

### The target cluster count, which is the cheapest strong check in the stage

`180031f00` works out what the whole merge will aim at before any of it runs,
from the enclosed VOLUME alone:

```
volume = sum of the box volumes of the compacted enclosed regions
t      = (int)(volume / 2^20)
target = t < 1 ? 32 : max(32, (t + 33) & ~31)
target = min(target, MaxVisClusters * 4)        // MaxVisClusters is 2048
```

and the compile prints it, as `Target 1344 clusters, clamped to 1342`. It costs
nothing to evaluate, there is no ray or cost function in it, and it lands
**exactly** on all three maps:

| | ze_hold_em_p | cardtest | probe01 |
|---|---|---|---|
| ours | **1,344** | **864** | **864** |
| compile | 1,344 | 864 | 864 |

That is a single number that only comes out right if the octree, the leaf masks,
the outside pass and the compaction are all right together, so it is now the
first thing to check when any of them changes.

### The settings are not the binary's defaults, and that is five of six keys

`18003c010` reads six `ResourceCompiler/VisBuilder/...` keys out of the game's
KeyValues before anything runs, and CS2 ships a block that overrides nearly all
of them in **`game/csgo_core/gameinfo.gi`**:

| key | binary default | what CS2 actually ships |
|---|---|---|
| `MaxVisClusters` | 2,048 | **4,096** |
| `PreMergeOpenSpaceDistanceThreshold` | -1, i.e. OFF | **128.0** |
| `PreMergeOpenSpaceMaxDimension` | 1,024 | **2,048.0** |
| `PreMergeOpenSpaceMaxRatio` | 4 | **8.0** |
| `PreMergeSmallRegionsSizeThreshold` | -1, i.e. OFF | **20.0** |
| `BaseVoxelSize` | 8 | not overridden, so 8 |
| `DeterministicBuild` | - | **1** |

Two of those turn a whole stage on. `18002f5c0` is wrapped in
`if (sampler+0xf4 != 0)`, which is set only when the distance threshold is
positive, so reading the binary alone says the pre-merge never runs - and the
compile prints `Distance merged regions` on every map. The binary's defaults are
what the tool would do with no game attached; they are not what the tool does.

It also means `LeafMasks` is keyed by (level, cell) rather than by cell, because
a mask is 4x4x4 over the node's OWN box whatever size that is.

which is where the `%d) x-axis split hint %.2f - %.2f` and
`%d) %dx%dx%d voxel hint` log lines come from. The boxes are the entity's own,
offset by its origin.

That is worth knowing beyond this stage: **visibility clustering is steerable from
the map**, and a compiler that ignores hint entities will cluster a hinted map
differently from Valve's even when everything else matches.

### Why the count is still not reproducible

ze_hold_em_p logs no hints at all, so its three lists are empty and every region
yields exactly one candidate. Its 93,354 therefore does not come from splitting;
it comes from the per-voxel assignment inside `180032d80`, whose inner test is not
decoded.

Two measurements, recorded because they bound the answer and because the second
one is the reason nothing was adopted:

- a per-region count of voxel COLUMNS, the distinct rows of a 4x4x4 mask along one
  axis, lands within **0.9% to 2.5%** of the target on cardtest and probe01
- the same rule is **+54% on ze_hold_em_p**, on every axis, and the difference
  tracks composition: 47% of ze_hold_em_p's enclosed regions sit in leaves ABOVE
  the smallest, against 3% on the probe maps

So a rule fitted on the two simple maps does not survive a map with mixed leaf
sizes, and adopting it would have looked like progress while being wrong.

**The assignment loop has since been read, and there is no closed-form rule to
find.** `180032d80` appends one cluster record per voxel of the region, each
holding a list of `(mask, leafNode)` pairs so a cluster can later span leaves, and
then `1800337a0` merges them: it builds a `CBoxMerge` over their boxes and merges
the cheapest pair repeatedly until the count reaches its limit of 32 OR the best
remaining merge costs more than a threshold. The 8.85 clusters per region is the
output of that greedy loop over a mean of 55.8 voxels, which is why every attempt
to fit it failed. Implementing it needs the cost function, which is the quality
axis of the whole build.

### What the printed region count actually counts

`180032b80` fills a vector with the regions whose **flag bits 0 and 1 are both
clear**, and it runs immediately before `CVoxelSampler3::MergeInsideRegions` logs
`Generated clusters for %d regions`. The function's own name, recovered earlier
from an assert, says the same thing: these are the INSIDE regions.

So 10,554 is neither the octree's region count nor a connectivity result. It is
the count after two independent exclusions: bit 1, set when the region is created,
and bit 0, set by the pass above.

### What this confirms about the mask layout

`18010be50` builds a region's box from its leaf's box and mask, and it is an
independent statement of the format the compiled file uses:

- the sub-cell size is `(box.maxX - box.minX) * 0.25`, so a leaf is **four base
  voxels a side**
- bit `i` of the mask is the sub-cell at `x = i & 3`, `y = (i >> 2) & 3`,
  `z = (i >> 4) & 3`

Both match what `VisVoxelizer` already implements, which is worth having from the
compiler's own code rather than from inference over shipped files.

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
