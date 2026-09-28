> **Addresses in this file are for the visbuilder.dll of 2026-07-09**, which is
> the build they were read out of. CS2 rebuilt on 2026-09-23 and every one of
> them moved. Do not re-read them by hand and do not rewrite them here: section 9
> of [`REVERSING.md`](REVERSING.md) translates all 94 old to new, and
> `python tools/sigscan.py <visbuilder.dll>` prints them for whatever build is
> installed. `REVERSING.md` is also the procedure for the next update.
>
> **Starting cold?** [`HANDOFF_VIS.md`](HANDOFF_VIS.md) is the short version: the goal,
> the score per stage as it stands, how to run the oracle, and what is open.

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
octree holds. That is what makes it small: ze_hold_em_p's octree has 71,422 leaves
and 76,880 regions in total. Those leaves are not an estimate: the 81,625 nodes
the compile logs are 1 + 8 x 10,203 branches, and we reproduce both exactly, so
the leaf partition the count is taken over is Valve's own.

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

(2026-09-28: "handled separately" is the voxelizer; see "Visibility hints,
ported" at the end. The split lists are cut z first, then x, then y.)

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

## What is left of the stage, and what is known about each piece

The five stages above end at `93354 clusters generated`. `180031f00` and
`CVoxelSampler3__MergeInsideRegions` name everything after it, and the compile
prints a number for each, so none of it is guesswork about WHAT to build - only
about how.

### `18002f5c0`, the distance pre-merge

Gated on `sampler+0xf4`, which is set only when
`PreMergeOpenSpaceDistanceThreshold` is positive - and the shipped config sets it
to 128, so it runs on every map. It collects the regions that produced exactly
ONE cluster carrying the `+0x54` open-space flag, merges their boxes through
`180028c70` with the max dimension and max ratio from the config, and folds the
cluster lists together. Numbers to hit: `Distance merged regions (%d merged to
%d)` and `pre-merged to %d clusters`.

| | clusters generated | pre-merged to |
|---|---|---|
| ze_hold_em_p | 93,354 | 93,354, nothing merged |
| cardtest | 81,991 | 81,865 |
| probe01 | 81,707 | 81,576 |

### `180034220`, `MergeClusterSet`, run FIVE times - IMPLEMENTED

This is the expensive one - 400 of Mako's 1,088 seconds - and it takes
ze_hold_em_p from 93,354 clusters to 258. The signature is
`(sampler, clusters, cell, margin, costLimit, budget)` and the five calls are:

| pass | cell | margin | cost limit | budget |
|---|---|---|---|---|
| 1a | 512 | 0 | 20 | target x 6 |
| 1b | 512 | 256 | what 1a returned | target x 5.75 |
| 2a | 2,048 | 0 | what 1b returned | target x 4.5 |
| 2b | 2,048 | 1,024 | ... | target x 4.25 |
| final | 4,096 | 0 | ... | target x 3 |

It buckets clusters into a **2D grid** over the scene's own box - `floor` and
`ceil` of the bounds against the cell size, offset by half the margin - and hashes
`(cy % ny) * nx + (cx % nx)` through a 32-bit mixer into an open-addressed table.
Only x and y are in the key; z is not. The merging then happens within a bucket.

The budgets are not binding on the maps measured: ze_hold_em_p's first pass
budget is 8,052 and it lands at 258, so the cost limit is what stops it.

Each of those five is only the BUCKETING. The merging is
`CVoxelSampler3::MergeClusterSet` at `180033fd0`, and it is two passes over the
buckets rather than one:

```
costs[i] = 1800337a0(bucket[i], cellBox, costLimit, budget*2, padded=false)   in parallel
average  = mean(costs)
           1800337a0(bucket[i], cellBox, average,   budget,   padded=false)   in parallel
return average
```

and the average is what the next of the five passes takes as its cost limit. So
the whole chain is steered by one number, and the compile prints it:
`MergeClusterSet costs after first pass min:20.0 max:45960.3 avg:21940.0`.

Both jobs call `1800337a0`, which is the same greedy merge cluster generation
uses - with `padded = false`, which is what changes its character completely.
With the shell on, the live count can never reach the target and the cost only
orders the merges; with it off, the budget and the cost limit are exactly what
stop it.

**Where it lands.** `VisClusterSet` is the port and `VisClusterSetTests` scores
it, behind `MERGE=1` because it samples every cluster's visibility five times
over - two to three minutes a map:

| | generated | first pass | second pass | final | first cost |
|---|---|---|---|---|---|
| ze_hold_em_p | 95,978 | 301 vs 258 | 301 vs 258 | 301 vs 258 | 21,010.6 vs 21,940.0 |
| cardtest | 80,991 | 1,887 vs 1,998 | 1,685 vs 1,825 | 1,529 vs 1,626 | |
| probe01 | 80,676 | 1,946 vs 1,979 | 1,757 vs 1,815 | 1,589 vs 1,621 | 108,623.5 vs 110,760.1 |

**The cost is the number worth watching, not the counts.** It is the average over
every bucket of where a greedy merge stopped, so it folds in the cost function,
the sampled visibility, the candidate rule and the merge order all at once, and
it lands within **1.9% on probe01 and 4.2% on ze_hold_em_p**. The counts follow
from it and sit between -6% and +17%.

### The 512 direction switch, and why it is not an optimisation

`180030df0` samples a cluster's visibility by aiming at every OTHER cluster's
centre while the set is under 512 entries, and switches to a fixed sphere of 512
directions at or above it. The sphere is `180027400`'s golden spiral:

```
step = 2 / n;  turn = (3 - sqrt(5)) * pi
y = i*step - 1 + step/2;  r = sqrt(1 - y*y);  a = i*turn
dir = (cos(a)*r, y, sin(a)*r)
```

That switch changes which bits are set, so it changes every cost and the whole
merge order with it. A set of 511 and a set of 512 are sampled differently on
purpose, and a region's merge (at most 120 entries) never sees the sphere while
a grid cell's (thousands) never sees anything else.

### The PVS has three modes, and the shipped one is the small function

`180036850` reads `ResourceCompiler/VisBuilder/DeterministicBuild` and branches:

| mode | when | what it is |
|---|---|---|
| `180018a30`, 475 bytes | `DeterministicBuild != 0` and no `-oldvis` - **what CS2 ships** | the deterministic generator set |
| `1800177d0`, 4,703 bytes | `DeterministicBuild == 0`, or `-oldvis`, or under `-updateloshints` | the ORIGINAL sampler, a larger generator set with its own sampling loop |
| `180017020`, 1,611 bytes | `-updateloshints` | regenerates the `.los` hint cache by running the old sampler over an empty hint set |

**None of them is dead.** The old one is the legacy path AND the engine of the
tool mode that WRITES the `.los` cache, which is the format `PLAN.md` has had
open for months. But a stock compile runs neither, and that is not inferred from
the config - it is what the logs say. `180018a30` calls `18001d610`
(`CNeighboringClustersList::Build()`) and `18001f170` (`NeighborsScan
BuildTracePointsForClusters()`); `1800177d0` calls **neither**; and every compile
log we have prints the first of those. Reverse engineering the 4,703 byte
function first would have been days spent on code the shipped compiler does not
execute.

### The four ray generators, and how they were found

Their names are in the binary and **nothing in the code references them** as
Ghidra sees it, which is the thread `PLAN.md` records as needing an RVA-aware
walk. Scanning the .text section for a RIP-relative displacement that lands on
each string finds all four in one pass:

| accessor | name | vtable |
|---|---|---|
| `18001bb60` | `CLOSRayGenerator` | `18017b648` |
| `18001bcd0` | `ClusterCenterRayGenerator` | |
| `18001bce0` | `CBoundaryPointsRayGenerator` | `18017b688` |
| `18001bcf0` | `LargeClusterRegionsRayGenerator` | `18017b7c8` |

and `180018a30` builds exactly four objects in that order: the cluster-centre
one always, then - unless its caller passes the skip flag - boundary points,
large cluster regions (with 600, 128 and 256 written into it), and the `.los`
reader handed the hint path. That matches the compile logs, which name
`ClusterCenterRayGenerator` and `LargeClusterRegionsRayGenerator` and nothing
else.

### The region count the file ships with, and what it is NOT

`Compacted to 103358 regions (258 clusters)` is printed at the END of
`CVoxelSampler3::MergeInsideRegions` (`180034ea0`), after the five merge passes,
and it is **not** a total over the clusters. It
was implemented that way first, as the sum of every final cluster's (mask, leaf)
pairs, and scored at -63% on probe01 and -89% on ze_hold_em_p. The write-back
loop says why.

It is the length of the flat entry array that **cluster assignment** builds, and
the assignment is the three passes that close out `MergeInsideRegions`.

**Pass one** scatters. Every final cluster holds the `(mask, region)` pairs it
covers, so for each pair it appends a 16 byte record to that region's own list:
the running cluster index at `+0x00`, `region * 4` at `+0x04`, the 64 bit cell
mask at `+0x08`. The kind bits come out clear because a cluster is always open
space.

**Pass two** restores. The region compaction had already written blocking and
skipped records, which carry no cluster and pass one does not regenerate, so
every record of the old array with `packed & 3` set is re-added to its region.

**Pass three** concatenates, and only over regions whose flag bit 0 is still
set. Each kept region gets its flag word stamped `(flags & 1) | (offset << 1)`
and its entry count written beside it at `+0x04` as a `short`. The printed
number is the final running offset, so:

> **the length of the flat array, which is the total of every kept region's
> cluster records plus its retained blockers.**

The region's flag word is doing double duty from here on: bit 0 stays the kept
flag and everything above it is the offset where that region's records begin.
That is exactly how the PVS walk finds them.

### What it scores, and where the rest of the gap is

| map | ours | compile | off by | of which blockers |
|---|---|---|---|---|
| probe01 | 17,731 | 30,665 | -42.18% | 6,377 |
| cardtest | 17,705 | 30,817 | -42.55% | 6,407 |
| ze_hold_em_p | 40,688 | 103,358 | -60.63% | 29,100 |

The key in a cluster's `(mask, key)` pair is an **octree leaf**, not a region,
and getting that wrong is worth most of the error: scoring it against the region
count instead dropped 10,389 of ze_hold_em_p's 11,588 pairs as out of range and
read -95.21%.

What is left is not in this stage. Subtract the blockers and our final clusters
cover 11,588 leaf records on ze_hold_em_p against roughly 74,000, and 11,354
against roughly 24,000 on probe01. Our count is almost exactly the region count,
which means **every region ends up inside exactly one cluster**; Valve's works
out at about seven clusters per region. Since `18002f250` only ORs records that
share a leaf WITHIN one cluster, clusters are free to overlap, and Valve's
plainly do. Ours collapse each region's eight or so newborn clusters into a
single survivor instead of letting them join different neighbours.

So the remaining error is cluster coverage, which is the merge's ordering, and
it is the same root cause as ze_hold_em_p's 301 clusters against 258.

### The entry array is older than assignment, and `180032670` re-compacts it

`180032670` is not the region build. It walks the octree's leaves and collapses
each leaf's records down to **at most three**, one per kind, ORing every mask of
a kind together and throwing the cluster ids away:

| kind | bits | what it is |
|---|---|---|
| 0 | `region * 4` | open space |
| 1 | `region * 4 \| 1` | blocking, which stops a line |
| 2 | `region * 4 \| 2` | skipped, which the walk ignores |

So `+0x04` was never a flag word. It is `(region << 2) | kind`, and a record
carries the region it came from all the way through. That is what lets pass two
above put a retained blocker back in the right place.

### The PVS itself, end to end

`180036850` builds a 16 byte stack adapter, `{ vtable 18017c1e8, owner = the
voxel sampler }`, and hands THAT to the sampler as its scene. The adapter's
slots are one-line thunks that reload `this` from `+0x08`, so Ghidra leaves them
as `PTR_LAB_` and nothing in a call-graph dump reaches them. Reading the vtable
out of the image and decoding the thunks by hand gives the four that matter:

| slot | thunk | target | what it is |
|---|---|---|---|
| `+0x10` | `18002a960` | inline | `sampler->[0xe8]`, the ray trace environment |
| `+0x18` | `18002a970` | `18002c2a0` | fold a batch's segments into the PVS |
| `+0x58` | `18002acf0` | `18002d490` | a point to its region |
| `+0x80` | `18002ad60` | - | a region to its cluster |

The algorithm is one rule: **every cluster a clear line passes through can see
every other cluster that same line passes through.** `18002c2a0` runs it per
segment:

1. If the batch held no sight ray, step the origin one unit along the line
   first, because those segments start on the surface that produced them, and
   let a blocker abandon the line. If it did, start at the segment's own origin
   and walk straight through blockers.
2. `18002d9e0` walks the octree from the root cube, breadth first through a
   4,096 entry ring. At an internal node `18010cb10` gives an 8 bit mask of the
   octants the line crosses; at a leaf `18010c6e0` gives a 64 bit mask of its
   4x4x4 sub cells. Both are per axis slab min and max, so each is a product of
   three ranges rather than a walk. The octant table at `1801a6820` is the plain
   one, bit 0 x, bit 1 y, bit 2 z, each half the parent edge.
3. Every leaf entry whose own 64 bit cell mask intersects the line's contributes
   its cluster id, skipping repeats of the id just taken.
4. Sort and unique the ids, which is what lets the next step collapse them.
5. `18001b690` turns the sorted ids into `(word, bits)` runs, then ORs EVERY
   collected cluster's row with EVERY run, with a CAS because batches run on the
   pool. It returns how many words it actually changed.
6. A segment is **useful** when that count is above zero, and only then is it
   kept in the batch's vector at `+0x30`. That is what the per generator log
   means by "useful rays found", and why the number is not the ray count.

The matrix is a full square bit matrix, one row per cluster, allocated at
`clusters + 2` because sky and sun get one each (`m_nSkyVisibilityCluster`,
`m_nSunVisibilityCluster`). The write stage asks for it by name,
`MutualVisibilityMatrix`.

The entry array the walk reads at `this+0x48` is exactly the flat 16 byte array
the assignment write-back builds, and a region finds its own run through the
offset packed into its flag word. So the PVS cannot run until assignment has,
and the number that measures assignment is the `Compacted to N regions` line
above.

### The batch record, which the two halves agree on

| offset | what |
|---|---|
| `+0x00` | ray count |
| `+0x08` | rays, 32 bytes each |
| `+0x18` | landing count |
| `+0x20` | landings, 28 bytes each |
| `+0x30` | useful segment count, which the driver adds to the total |
| `+0x38` | useful segments, 24 bytes each, preallocated to 4,096 |

`180016c60` consumes only that last vector, and both of its halves are gated on
debug fields, so nothing it does reaches the PVS.

### The merge ordering, read in full

The five passes' merger is `1800337a0`, and it, its candidate builder
`1800306e0`, its scan `180031680` and its compaction `180033600` are all in
Ghidra's blind spot. They came out through `DumpCallers` on `180030a50`, which
is the only thing that reaches them.

**`1800306e0` builds one cluster's candidates**, and confirms what we had: the
box is grown by 1 unit each way, and while fewer than two neighbours come back
it is grown again by the cluster's voxel size, up to four times. The cost is
always read as `(this, neighbour)`, but the pair is **filed once, on the HIGHER
indexed of the two**, and an entry already there is rewritten in place.

That one-sided filing is load-bearing. Filing both directions instead, so every
cluster carries its own full neighbour list, takes probe01's first-pass cost
from -1.93% to **-52.97%**: the cost is not symmetric, and holding both
orientations lets the scan pick the cheaper one every time.

**`180031680` is a full scan, not a heap.** For each live slot it picks that
slot's own best candidate, keeping the incumbent unless a rival is cheaper by
`1e-4` (`DAT_18017f0cc`); inside that band it asks a deterministic tie break
instead. Then the slots are compared, and there is a second rule there we did
not have:

> when two slots' costs are within `1e-3` (`DAT_18017f0d0`), the pair with the
> **smaller combined voxel count** wins. A strictly lower cost still wins
> outright.

Early in a pass, when every cluster is a single voxel and the costs are all but
identical, that rule fires constantly, and it is the obvious candidate for why
Valve's clusters stay spread out instead of one of them absorbing its
neighbourhood.

Implemented on its own it does not deliver: probe01 holds its cost at -1.92%,
but the first pass goes from 1,946 clusters (-1.67%) to 2,197 (+11.02%). The
reason is that the rule inside the `1e-4` band was still missing, and the two
are coupled.

### The deterministic tie break, which is `180030d70`

It is reached only through the merge state's vtable at `18017bf40`, slot
`+0x20`. Given the incumbent candidate and a rival, it prefers the rival when:

1. the incumbent is dead or is one of the shell boxes, or
2. the rival has **fewer voxels**, or
3. their voxel counts are equal and the rival's box sorts first.

"Sorts first" is `180027e10`, and it reads only geometry, never an index, which
is what makes the build reproducible however the pool's threads interleave. It
compares, taking the first key that differs:

> longest side, then the x extent, then y, then z, then `mins.x`, `mins.y`,
> `mins.z`.

So both levels of the scan break a tie the same way, towards the smaller side.

### What the two rules together are worth

With both in, probe01's first two passes go from a percent or three out to
almost exact:

| | before | with both rules | compile |
|---|---|---|---|
| first pass | 1,946 (-1.67%) | **1,977 (-0.10%)** | 1,979 |
| second pass | 1,757 (-3.20%) | **1,826 (+0.61%)** | 1,815 |
| fifth pass | 1,589 (-1.97%) | 1,286 (**-20.67%**) | 1,621 |
| first cost | 108,623 (-1.92%) | 107,406 (-3.03%) | 110,760 |

That is strong evidence the ordering is now right: the first two passes are the
ones that read the merge most directly, and they land inside a percent. What it
exposes is the fifth pass, which over-merges by 20%.

The per-bucket budget is NOT the cause, and that was checked rather than
assumed. `CVoxelSampler3::MergeClusterSet` takes one budget for every bucket,
doubles it for its first parallel pass, averages the costs that produced and
runs a second pass at that average, which is what we do. `180034220` forms the
budget as `ceil(total / buckets)`, which is also what we do. Dividing instead by
the full `acrossX * acrossY` grid, on a reading of `FUN_18002f480` that turned
out to be wrong, takes the first pass to -9.75%.

So the rules are reverted, for now, on the arithmetic: mean absolute error over
the three pass counts goes from 2.28% to 7.13%. Re-applying them is worth doing
the moment the fifth pass is understood, and the rules above are exact enough to
re-enter from this page.

### ze_hold_em_p's +7.87% is entirely the SECOND pass, and the seed is exact

Outside detection is `1800321f0`, and it is two passes over the entries the
voxelizer left, running BEFORE the kind-collapse (`18002e310`, then
`1800321f0`, then `180032670`, read off the caller).

Pass one is the parallel `InitialRegionStatus` job, whose body `18003f1e0`
calls `FUN_18004a2f0(rte, regionBox)` per entry. Pass two walks the same list
and, only for entries that came back 0, calls `FUN_18002e050`. Between them
every entry that answered Outside gets `packed |= 1`, which is what later stops
a march.

Measuring the two separately is what settles the map:

| map | seed only | with the second pass | compile |
|---|---|---|---|
| probe01 | 7,312 (-0.05%) | 7,312 (-0.05%) | 7,316 |
| cardtest | 7,443 (+0.36%) | 7,443 (+0.36%) | 7,416 |
| ze_hold_em_p | **10,554 (0.00%)** | 11,385 (+7.87%) | 10,554 |

The seed alone lands on ze_hold_em_p's number **exactly**, and on the two probe
maps the second pass changes nothing at all. Every one of the 831 comes from
our second pass promoting a region the compile does not.

Their profile is distinctive. Of 4,673 undecided entries, 3,489 fail the bounds
check outright and 1,184 reach a vote; 831 of those are promoted, with a median
of 38 inside votes against a threshold of 25, none above 50, and **823 of them
with zero outside votes at all**. So they are all promoted by the second clause,
`inside > n && outside < 5`, and our march almost never meets an outside region
on these.

What has been checked against the image and matches: the decision
(`18004a2f0`) term for term including the 0.8 as a double; the counters and the
thresholds in `18002e050`, where `[RSP+0x90]` is `PerFace` once the prologue's
`MOV RAX,RSP` before the push and the `SUB RSP,0xd0` are accounted for; the
march `18002deb0` returning 1, 2 or **0**, where 0 is no vote at all; the back
off of 8 units and the 0.1 floor; and the order of the three passes. None of it
explains the 831.

### One march, walked leaf by leaf

`VisMarchWalk` behind `WALK=1` runs the detection twice: once to find the
promoted regions, then again tracing every leaf one chosen region's marches
enter. Region 41587 of ze_hold_em_p, 45 marches, 38 inside votes, 0 outside:

```
leaf 38645 level 0 at <1208, -108, 24> size 32   holds region 41587, Unknown
ray 0: stop 32.7 -> Inside (3 leaves)   met region 41586, status Inside
leaf 51749 level 1 at <1208, -172, 24> size 64   region 55754 open ffff.. Inside
leaf 51748 level 1 at <1272, -172, 24> size 64   region 55753 open ffff.. Inside
leaf 51675 level 1 at <1464, -364, 24> size 64   region 55680 open ffff.. Inside
```

Three things came out of it, and all three close off a line of enquiry.

**It is not a cascade.** Region 41586, which 41587's first ray votes on, is
itself an Unknown the second pass promoted. But running the second pass against
a SNAPSHOT of the seed statuses, so nothing it decides can feed the next
region, gives 830 promoted instead of 831. The marches are reaching regions the
SEED already called enclosed.

**It is not the ray reach.** The gather's rays run to the scene diagonal where
the compile runs them to `g_flConfigMaxCoord`, which was an open divergence.
Forcing 16,384 and then 65,536 leaves every number identical, so the diagonal
already reaches past everything that matters here.

**The seed counters say Unknown honestly.** For six promoted regions spread
through the set: facing 36 to 51, behind 5 to 19, insubstantial 0, and
**escaped 88 to 105 of 150**. Outside needs `n <= behind`, which is 25, and
none of them are close; `facing <= 3n` holds, so Unknown is what the thresholds
give. Nothing here is a porting slip.

What the walk DOES show is where the inside votes come from: the marches run
about 300 units through large level 1 leaves that are entirely open,
`ffffffffffffffff`, and seeded Inside. So the promoted regions sit in open space
that connects to big volumes our seed calls enclosed.

### Those big open leaves SHOULD be seeded Inside, and that settles it

Breaking every region's seed verdict down by leaf level and by whether its mask
is completely open:

| level | mask | regions | inside | outside | unknown |
|---|---|---|---|---|---|
| 0 | partial | 34,558 | 2,916 | 30,129 | 1,513 |
| 0 | full | 31,940 | 2,892 | 28,917 | 131 |
| 1 | full | 6,050 | **4,746** | 653 | 651 |
| 2 | full | 2,370 | 0 | 1,625 | 745 |
| 3 | full | 834 | 0 | 329 | 505 |
| 4 and up | full | 1,128 | 0 | 0 | 1,128 |

The level 1 band is the one the marches run through, and 4,746 of them are
seeded Inside, which is **45% of the whole 10,554**. Those three inside
columns add to 2,916 + 2,892 + 4,746 = 10,554 exactly.

So the hypothesis is refuted, and refuting it is what makes the rest
conclusive. If those volumes were Outside the seed total would fall far below
10,554 and stop matching the compile's own number, which it currently hits dead
on. They are enclosed, the seed is right about them, and our seed as a whole
agrees with the compile.

Which leaves exactly one place for the 831 to come from: `18002e050`. Our
marches return Inside for 38 of 44 rays where the compile's must return it for
25 or fewer, and the difference is not the reach, not a cascade, not the
counters and not the seed. It is in what a march meets on its way.

### Why level 2 can never be Inside

The counters answer it outright. Mean over every fully open leaf:

| level | edge | facing | behind | escaped |
|---|---|---|---|---|
| 0 | 32 | 12.7 | 47.7 | 89.6 |
| 1 | 64 | **114.0** | 5.4 | **30.6** |
| 2 | 128 | 1.1 | 36.9 | 112.0 |
| 3 | 256 | 0.0 | 20.1 | 129.9 |
| 4 | 512 | 0.1 | 11.2 | 138.7 |
| 8 | 8192 | 0.0 | 0.0 | 150.0 |

Inside is only reachable when the outer condition FAILS, and its first term is
`n <= escaped + behind` with n = 25 out of 150 rays. From level 2 up, escaped
alone is 112 or more, so that term is always true, the outer condition always
holds, and the function can only return Outside or Unknown. Level 2 then splits
on `n <= behind`: behind averages 36.9, so 1,625 of 2,370 are Outside and the
rest Unknown. Nothing is size-aware in the thresholds; it is entirely that big
empty cubes see nothing.

Level 1 is the opposite case and the reason the 831 exist: facing 114 of 150,
escaped only 30.6, so `facing <= 3n` and `facing + escaped <= 4.8n` both fail
and it lands on Inside. 4,746 of the 6,050 go that way.

The level 0 / level 1 inversion that looked wrong is not. A 32 cube escapes
89.6 rays against a 64 cube's 30.6 because the two bands are different
POPULATIONS, not different sizes of the same thing: level 0 is the shell the
voxelizer cuts around geometry, half of it in the void just past a wall, and
level 1 is what a sealed room interior collapses to. Splitting the counters by
seed verdict shows it directly, and the numbers below close the question.

### What the 831 actually are, and the whole chain checked against the binary

Every function between the seed and the printed count was re-decompiled and
compared line by line. All of them match what we ship: `1800321f0`, `18002e050`,
`18002deb0`, `18004b260`, `18004a2f0`, `18004b970`, `18010be50`, `18010c3f0`,
`180032670`, `180032b80` and the stage driver `180031f00`, which has no step
between them that we skip. Every float the stage reads was pulled straight out
of the PE rather than inferred: `18017f170` is 8.0, `18017f0e4` 0.1, `18017f0f0`
0.25, `18017f108` 0.5, `18017f138` 0.8. `18002e050`'s `local_48` is the vector's
`param_2[0xc]`, which `18004b260` writes as `grid * grid`, so the vote's n is 25.

Two measurements then say what the 831 are not:

- **The march is exact.** Replaying it beside a brute force scan of all 76,880
  region boxes against the same segment, ray by ray, it is a superset of the
  scan and misses nothing. The seed-Outside regions 12 units away sit BEHIND a
  wall, and the march stops 8 units short of the surface it hit, so meeting none
  of them is geometrically correct rather than a lookup failure.
- **The seed is not marginal.** Its 10,554 Inside regions average 142.2 facing
  rays of 150 and 0.0 behind, and their `facing + insubstantial` bottoms out at
  83. The 831 sit at 44. Nothing at all lies between 51 and 82, so the two are
  cleanly separated populations and there is no sub-band of the seed's answer to
  shave 831 off.

Asking the shipped file directly, by querying its octree at each of our region
centres, our seed's Inside set is **100% space the compile also holds a cluster
for, 10,554 of 10,554**, on all three maps (98.7% and 99.2% on the probes). The
second pass discriminates too: 77.3% of what it promotes is claimed against
2.5% of what it rejects. That test is only as fine as the answering leaf, which
on ze_hold_em_p is 256 units at the median because the shipped tree is the
COLLAPSED one, so it cannot settle the 831 on its own; it does settle that our
seed invents nothing.

The 831's own profile is what finally pointed at the answer: facing 44.0,
behind 9.6, escaped **96.4** of 150, against the seed-Inside band's escaped 7.7.
Two thirds of the rays cast from a region inside a closed corridor were hitting
nothing at all, and that is not something the map is.

### The kd traversal was clipping triangles to the leaf that holds them

`RayTraceEnvironment.Trace` tested each of a leaf's triangles only over the
leaf's own slice of the ray, `Meets(t, origin, direction, from, to)`, and
abandoned a node once it started beyond the best hit so far. Both are the
textbook kd shortcuts and both are wrong on this file.

Counting the file settles it: ze_hold_em_p's kd tree holds 4,548 triangles in
933 leaves using **5,983 index slots**, so a triangle is filed in about 1.3
leaves rather than in every leaf it overlaps. A large triangle is therefore
routinely met at a distance outside the slice belonging to the leaf that holds
it, the clamp threw that hit away, and the ray carried on to something far
behind. Against a scan of every triangle, on the seed's own rays, it disagreed
on **1,698 of 6,300**, every single one landing FARTHER than the truth, by up to
1,750 units. Testing over the whole ray instead, it agrees on all 6,300.

The way to be sure this is an implementation defect and not a misread of the
algorithm was to write the same descent twice: a replica reading the raw file
disagreed with the scan on exactly the same 1,698 rays, and stopped disagreeing
on exactly the same change. A traversal is correct precisely when it returns
what a scan returns, whichever way Valve's own SIMD packet walk gets there, so
`VisTraceAgainstBruteForce` holds it to that.

### What the fix did to the module

| stage | before | after |
|---|---|---|
| voxelize nodes, ze_hold_em_p | exact | exact |
| enclosed regions, ze_hold_em_p | +7.87% | **0.00%** |
| enclosed regions, cardtest | +0.36% | **0.00%** |
| enclosed regions, probe01 | -0.05% | **0.00%** |
| clusters generated, ze_hold_em_p | +2.81% | **0.00%** |
| clusters generated, probes | -1.22% / -1.26% | -0.06% |
| merge final, ze_hold_em_p | +35.66% | -5.81% |
| merge final, cardtest | +4.18% | -5.10% |
| merge final, probe01 | -2.10% | -5.68% |
| first pass cost, probe01 | -2.70% | -1.15% |

The 831 are gone: the second pass now promotes **nothing** on any of the three
maps, and all three enclosed counts are exact and pinned at zero. Outside
detection was never the defect. It was reading a ray trace that put two thirds
of its rays through solid geometry, and with honest distances the seed alone
answers every map.

ze_hold_em_p's cluster merge came back from +35.66% to -5.81%, and that is the
more useful part of the change: the three maps now under-merge by the same 5 to
6% instead of disagreeing in both directions by wildly different amounts. One
cause to find rather than three.

## Assignment was dropping two thirds of its own records

`180034ea0`'s tail builds one vector per octree node and then concatenates them,
and `*(param_1 + 0x40)` -- the length of what it concatenates -- is what the
compile prints as `Compacted to %d regions`. Two things go into those vectors:

1. every final cluster's (mask, leaf) pairs, scattered into the leaf each pair
   names and stamped `{running cluster index, leaf * 4, mask}`; and
2. **every record of the compaction whose `packed & 3` is non-zero, copied
   through verbatim.**

The second is most of it, and we were feeding it only a third. `180032670`
emits up to THREE records per leaf and we had only ever modelled one of them:
the enclosed union at kind 0, the **OUTSIDE union at kind 1**, and the solid
union at kind 2, each when its own mask is non-empty. Assignment drops the kind
0 records, because the scatter has just rebuilt those with real cluster ids on
them, and keeps the other two. We were synthesising a kind 1 record from each
leaf's SOLID mask and nothing at all for the outside union, so the stage carried
the solid half and lost the larger one.

Counting the leaves settles it without running the merge at all:

| map | leaves | enclosed union | outside union | solid union | non-open | pairs left for |
|---|---|---|---|---|---|---|
| ze_hold_em_p | 71,422 | 10,554 | **63,540** | 29,100 | 92,640 | 10,718 |
| cardtest | 15,177 | 7,416 | **12,693** | 6,407 | 19,100 | 11,717 |
| probe01 | 15,065 | 7,316 | **12,693** | 6,377 | 19,070 | 11,595 |

The last column is the compile's own total minus the non-open records, and
ze_hold_em_p's 10,718 is our merge's pair count **exactly**. So the merge was
never implicated here at all.

`VisRegions.Collapse` is the full three-union collapse now, `Compact` is its
kind 0 half, and assignment takes the whole array:

| map | before | after |
|---|---|---|
| ze_hold_em_p | -61.48% | **0.00%** |
| cardtest | -42.21% | -1.02% |
| probe01 | -42.53% | -1.14% |

The residual 1% on the probes is their merge's own 5%, diluted: pairs are only a
quarter of the records this stage emits, and the probes' merge under-merges by
about 5%, which is 350 pairs of 11,595. Fixing the merge closes it; nothing else
in assignment is outstanding.

## The merge, checked end to end, and the tree finally wired

The five passes are `180034220` five times from `180034ea0`, each returning the
cost the next one starts at, and everything they are made of has now been read
against the binary rather than inferred:

| | |
|---|---|
| `180034220` | one pass: re-bucket by cell and call MergeClusterSet. Ghidra types it `void`; the call sites read its XMM0, which is how the cost chains |
| `180033fd0` | merge every bucket at the incoming limit with DOUBLE the budget, average what that cost, merge again at the average with the single budget |
| `1800337a0` | one bucket's merge loop |
| `180030df0` | the initial candidate lists |
| `1800306e0` | one cluster's list, rebuilt |
| `180030a50` | absorb |
| `180031680` | the cheapest live pair |
| `1800301c0` | the cost |
| `18003dea0` / `18003de20` | the two job bodies, both passing padded = false |

Every constant came out of the PE rather than a guess: cells 512/512/2048/2048/4096,
margins 0/256/0/1024/0, budget multipliers 6, 5.75, 4.5, 4.25 and 3, the first
limit 20.0, `DAT_18017f1e8` = **-1.0** as the loop's seed cost, `DAT_18017f1dc` =
**-1e-4** as the gain floor, `DAT_18017f0cc` = 1e-4, `DAT_18017f0d0` = 1e-3, and
`_DAT_18017f260` = `0x7fffffff` as the abs mask. All of them already matched.

Two things did not, and both are fixed:

**The loop tests the limit twice, and the first test is on the LAST cost it
merged.** `while ((budget < live || best < limit) && (Cheapest(), budget < live
|| cost < limit))`. Absorbing rebuilds the survivor's candidates, so a fresh cost
can come out under a limit the run had already passed, and testing only the new
one keeps merging there. It is inert on all three specimens, because `live >
budget` dominates every bucket they have, but it is what the binary does.

**The candidate lists come from the AABB tree, and now they do here too.**
`1800337a0` gives every cluster a proxy with its own box, `180030a50` moves the
survivor and destroys the other, and `1800306e0` queries the tree, growing the
box by 1 unit and then by the compile's voxel size up to four more times while it
still finds nothing but the cluster itself. Standing that up in place of the
linear scan had failed twice, taking probe01's first pass from 2,466 clusters to
1,138; both attempts were reading a ray trace that landed 27% of its rays on the
wrong triangle. With that fixed the tree reproduces the scan's numbers **to the
cluster on all three maps**, which is what says the structure was never the
problem. It is kept rather than reverted because the order a query returns
candidates in decides which of two equally priced pairs a cluster holds, and only
the tree gives Valve's order.

### What is still 5% out

The counts did not move: -5.36% / -6.23% / -5.68% on probe01, -4.50% / -4.44% /
-5.10% on cardtest, -5.81% throughout on ze_hold_em_p. We merge about one pair in
twenty more than the compile does, and by the same margin on every map, which
still reads as one cause rather than three.

What is ruled out, by reading rather than by trying: the loop and its stopping
rule, the cost function and all of its penalties, the candidate build and rebuild,
the absorb, the cheapest-pair scan including both levels of its tie-break, the
bucketing and its cell boxes, the pass parameters, the budget arithmetic, and the
cost chain, whose value we land within 1.15% of on probe01. The tree is no longer
a candidate either.

### The sampler, which was the last input, and four things it had wrong

Every cost is a popcount over the visibility bitsets, so what a cluster can see
decides every merge. `180031a20` fills them and `18004a690`, `18004b970`,
`18004a420` and `18003d600` are what it is made of. Four things were ours rather
than the compile's, and all four are now the compile's:

- **The cast was restricted to the triangles overlapping the sampled box**, paid
  for once per set. Sound for a leaf; wrong for a merge pass, whose box is a 512
  unit grid cell spanning the full height of the map. Its diagonal is the reach,
  so a ray leaves the cell long before it runs out, and every triangle it would
  then have hit was missing from the list. `18004a690` casts against the whole
  scene.
- **The cast stops at `g_flConfigMaxCoord`, not at the box's diagonal.** A hit
  BEYOND the sampled box is still a hit, and the segment is then longer than the
  diagonal. The diagonal is only what a ray with no hit falls back to.
- **A BACK facing hit is not a hit.** `18004a420` clears the record's hit bit and
  puts FLT_MAX back in the distance, exactly as for a ray that met nothing, so
  the segment runs out to the diagonal instead of stopping on the surface.
- **The nodraw second look applies here too**, through `18004b970`.

The broad phase is now the real tree as well. `18003d600` walks the merge's OWN
`VisBoxTree` -- a leaf payload with no cluster behind it is a shell box, which
counts on its box alone -- and the narrow phase is `18010c6e0` per (mask, leaf)
pair, which is the per cell test corrected earlier in this session. Both were
approximated by an ad hoc index with a threshold of our choosing; neither is now.

**The cost the whole chain is steered by improved and the counts did not.**
probe01's first-pass average is -0.73% against the compile, from -1.15% and from
-2.70% earlier in the session. But probe01 finishes at -5.92%, cardtest at
-5.21%, and ze_hold_em_p went the WRONG way, from -5.81% to -9.69%, on the
full-scene cast alone.

That is recorded rather than reverted, for a reason worth stating: cluster
GENERATION runs on the same sampler, and it stayed exact through all four changes
(93,354 against 93,354 on ze_hold_em_p, -0.06% on the probes). A sampler that is
wrong would not hold that. So the four fixes are right and something they
interact with is still wrong, which is a better position than the one before,
where the sampler itself was a suspect.

## The distance pre-merge, built, and it was not a near no-op

`18002f5c0` runs between generation and the five passes and was the last unbuilt
stage. It collects the sets that produced exactly ONE cluster carrying the open
space flag, groups the ones that sit face to face, and folds each group into its
first member. The pieces:

| | |
|---|---|
| `18002f5c0` | collect, group, fold, and the two log lines |
| `180028c70` | compact the list, renumber, and round until nothing merges |
| `180027f50` | `CBoxMerge::MergeBestCandidates`: one round |
| `1800284a0` | one run's best partner |
| `1800294f0` | sorts the round's pairs, its comparator being `180027e10` |

Two things about it are worth writing down because neither is visible from the
code alone. The stage is gated on `sampler+0xf4`, which the setup sets only when
`PreMergeOpenSpaceDistanceThreshold` is positive, and the BINARY's default for
that key is `DAT_18017f1e8`, which is **-1**. Read the binary alone and the stage
never runs; the shipped config sets it to 128 and the compile prints its line on
every map. And the threshold is only the switch -- nothing inside reads it. What
does the work is `PreMergeOpenSpaceMaxDimension` and
`PreMergeOpenSpaceMaxRatio`, whose binary defaults ARE used: 1,024 and 4.

A pair may merge when their union's longest side is at most 1,024 and at most 4
times its shortest, and when the two boxes share a face -- lined up within
`DAT_18017f0dc` = **0.0125** on the two axes across and touching within
`DAT_18017f0f0` = **0.25** along the third -- or one contains the other. Of those
the partner whose union has the smallest volume wins, then the smallest aspect
ratio, then `180027e10`. A round merges every pair at the cheapest volume anyone
offered, each run taking part at most once, and rounds repeat until none is left.

### What it did

| | pre-merged to | compile | |
|---|---|---|---|
| probe01 | 81,598 | 81,576 | +0.03% |
| cardtest | 81,882 | 81,865 | +0.02% |
| ze_hold_em_p | 93,354 | 93,354 | **exact** |

ze_hold_em_p is exact for the right reason rather than by luck: with 93,354
clusters over 10,554 regions it has no set holding a single cluster at all, so
there is nothing for the stage to collect and it correctly merges nothing.

And downstream it is not a near no-op at all, which is what the old measurement
had suggested:

| | before | after |
|---|---|---|
| assignment, probe01 | -1.42% | **-0.11%** |
| assignment, cardtest | -1.29% | **-0.15%** |
| merge final, probe01 | -5.92% | +3.15% |
| merge final, cardtest | -5.29% | +3.81% |
| merge first, probe01 | -5.10% | +2.68% |
| merge second, probe01 | -6.56% | +1.49% |

The merge changed SIGN on both probe maps and roughly halved in size. Sixty
clusters removed before the five passes is worth about eight percent of the
final count, because each one it removes is a run of open space that the cost
driven merge would otherwise have spent its budget joining up.

### Why ze_hold_em_p is a different problem from the probes

Instrumenting where each bucket's loop STOPS separates them completely. A run
that hits its budget and one that runs out of pairs to make are the same number
on the way out and different problems:

```
ze_hold_em_p  pass 1:  82 buckets ended;  RAN OUT of pairs  82  stopped on cost   0   live     82
probe01       pass 1:  20 buckets ended;  RAN OUT of pairs   0  stopped on cost  20   live  2,541
                       the cheapest pair left averaged 122,463 against a limit of 109,571
```

probe01 is healthy: every bucket stops because the next merge costs more than it
is allowed, with the cheapest remaining pair 12% over the limit. ze_hold_em_p's
82 merging buckets each collapse to a SINGLE cluster and stop only because there
is nothing left to merge. Its other three buckets hold 151 clusters between them
and never merge at all, because a bucket smaller than twice the per cell budget
is returned untouched -- 233 is 82 + 151, and the compile's 258 is the same 151
plus 107, so where we finish 82 buckets at one cluster each the compile finishes
them at about 1.3.

So the gap is the LAST merge in about 25 buckets, and the cost that should have
stopped it. The cost function itself is not the suspect any more: `1800301c0`
has now been read to its end, including the tail we had inferred. The spread
penalty is x8 over a 4,096 unit footprint, the z span penalty x32 over 80 units
when at least one side is under it, the whole weighted term is scaled by 10, and
the distance added to it is `18002fec0`, which is the box GAP -- zero the moment
two boxes touch, so it can never be what stops a merge between neighbours. All
of that matches. So do the voxel sizes the x128 penalty keys on: a cluster is
born at `8 << level`, which is 8 through 2,048 across ze_hold_em_p's nine leaf
levels.

What the shape of the answer has to be: both penalties stop firing once a
cluster is big, because a big cluster's own footprint is over 4,096 and its own
z span is over 80. After that a pair of neighbours costs about 10 against a
limit of 21,000 and nothing can stop it. The compile stops anyway in 25 buckets,
so something keeps its cost up that does not keep ours up.

### The visibility difference was the suspect, and it is not a defect

Measuring what a cluster sees AS SAMPLED, before any merge, separates the two
maps as sharply as the halt reason did:

| map | pass 1, mean clusters seen | of | |
|---|---|---|---|
| ze_hold_em_p | 144.1 | 144 | **100.0%** |
| probe01 | 233.4 | 518 | 45.1% |

Every cluster in a ze_hold_em_p bucket sees every other one. So every difference
count is zero, every base cost is 1, and the whole cost is penalties times ten
plus the box gap. The merges that end its buckets cost 898 on average against a
limit of 21,000, the dearest reaching 20,800, and nothing ever crosses.

The obvious reading was that the sampler over-reports. It does not. Asking the
question independently of the merge -- bucket every cluster at 512 the way
`180034220` does, take the fullest cell, and trace centre to centre between the
clusters in it -- gives:

```
ze_hold_em_p  fullest 512 cell holds  2,128 clusters spanning <520, 112, 128>
              of 39,800 lines, 39,800 are CLEAR (100.0%) and 0 are stopped
probe01       fullest 512 cell holds  8,533 clusters spanning <512, 552, 296>
              of 39,800 lines, 22,082 are CLEAR ( 55.5%) and 17,718 are stopped
```

ze_hold_em_p's fullest merge cell is an OPEN BOX, 520 by 112 by 128, with not one
line in forty thousand stopped by geometry. The saturation is the map, not the
port. That closes the visibility difference as a suspect and leaves the error
where the numbers put it: the cost SCALE. The costs that end ze_hold_em_p's
buckets top out at 20,800 against a limit of 21,000, a one percent margin, and
its first pass average is 4.28% under the compile's where the probes are now
near one. Uniformly lower costs would not matter, because the limit is derived
from the costs -- but the limit comes from the first merge, which the budget
drives, and the stopping happens in the second, which the cost drives, so the
two populations do not have to scale together.

Three more pieces were read along the way and all match: `180020580` is the
vectorised OR the absorb folds one visibility vector into another with,
`18002fec0` is the box GAP and not a centre distance, and `180034220`, which
Ghidra types `void`, returns XMM6 -- reloaded from the average at `1800341b1` --
so the cost the chain carries forward IS the average, as modelled.

### The cost scale, taken apart

The number the whole chain is steered by is the mean over buckets of what each
one's FIRST merge returned, and a bucket too small to merge is handed straight
back returning the incoming limit. So how many buckets are too small moves the
average as much as the merges do:

| map | buckets | untouched | returning | merged | returning |
|---|---|---|---|---|---|
| ze_hold_em_p | 85 | **41** | 20.0 each | 44 | 40,550.2 |
| probe01 | 20 | 0 | -- | 20 | 109,571.5 |

ze_hold_em_p's 21,000.4 is arithmetic: `(44 x 40,550.2 + 41 x 20) / 85`. Its 41
untouched buckets hold 3,912 clusters between them, 4.2% of the map in 48% of
the cells, and each contributes 20 where a merging one contributes forty
thousand. To reach the compile's 21,940 from the same 40,550 you would need 46
merging buckets rather than 44.

Every constant the cost is built from has now been read out of the PE rather
than carried: the size mismatch is `DAT_18017f1a4` = 128, the tag and z span
penalties `DAT_18017f190` = 32, the spread `DAT_18017f170` = 8, the area limit
`DAT_18017f1cc` = 4,096, the z limit `DAT_18017f19c` = 80, the scale
`_DAT_18017f174` = 10 and the coarse weight `DAT_18017f128` = 0.25 as a double.
All seven match. So does the shape: both penalties are AVAILABLE on
ze_hold_em_p, with 44 of its 85 buckets holding more than one voxel size and
99.9% of its clusters spanning 80 units or less in z.

**And the limit is not the lever, which is the useful part.** Ours is 21,000
against the compile's 21,940 -- LOWER, so it should stop our merges EARLIER and
leave us with MORE clusters, and we have fewer. Raising it makes ze_hold_em_p
worse, not better.

### The bucketing is not it either

`180034220` was read line by line against the port and every piece matches: the
origin is `floor((mins - margin/2) / cell) * cell` per axis, the extents are the
matching `ceil` minus that over the cell, the key is
`(cyIndex % acrossY) * acrossX + (cxIndex % acrossX)` off the cluster's box
CENTRE, the number of DISTINCT keys is what `perCell = ceil(budget / buckets)`
divides by, and a cell's box is `key % acrossX` and `key / acrossX % acrossY`
with the doubled low z. ze_hold_em_p's grid is 42 by 3 over an RTE box of
(-13707, -608, 17) to (7672, 398, 153), which is 126 cells of which 85 hold
anything.

The bucket sizes kill the obvious hypothesis outright. To reach the compile's
average from our 40,550 per merging bucket you would need 46 of the 85 to merge
rather than 44, so two of the untouched ones would have to clear the doubled
budget of 190. They are nowhere near it:

```
72, then FORTY buckets of exactly 96, then 260, 364, 1628, 2070, 2128, 2128, ...
```

Not one bucket sits within a quarter of 190 on either side. The two extra would
have to DOUBLE, not drift. (The forty identical 96s are not a bug either: the
map is a long repetitive corridor and its edge cells all catch the same strip.)

### Where that leaves it

We have the compile's whole line for ze_hold_em_p, not just the average:

| | min | max | avg |
|---|---|---|---|
| compile | 20.0 | 45,960.3 | 21,940.0 |
| ours | 20.0 | **45,040.0** | 21,000.4 |
| | exact | -2.0% | -4.28% |

The max is the single dearest bucket and it is twice as close as the average,
which is what a difference spread thinly across the merging buckets looks like
rather than a few buckets landing on the wrong side of a threshold.

What each of those numbers IS: the cost of the merge that took a bucket from 191
clusters to 190, the first merge stopping the moment it reaches the doubled
budget. Ours is 40,550 where the compile's must be about 42,365. Not a
threshold, not a constant, not a bucket boundary -- the same point on a rising
cost curve, 4.5% low.

Everything that curve is built from has now been read: the seven cost constants,
the cost function to its end, `18002fec0`, `180020580`, the candidate rule, the
absorb, the tie-break, and finally the BIRTH values in `180032d80` --
`VoxelCount += popcount(mask)` and `VoxelSize = (int)((maxs.x - mins.x) * 0.25)`,
both exactly ours. The saturation that makes the late merges free is 31.9% at
the first merge and only reaches 100% after most of the merging is done, so it
is not masking the early curve either.

So the remaining ze_hold_em_p error is not a discrete defect anywhere in the
chain. It is a soft 4.5% offset in where a greedy merge of 2,000 clusters has
got to after 1,843 of them, which is the accumulated effect of merge ORDER among
near-equal costs rather than of any one rule being wrong.

## The tree was never balanced, and that IS the order

Chasing the order found the one thing in the chain that had been read wrong.
`18010a5d0`, the refit, does not simply walk to the root fixing boxes. It opens
every iteration with

```c
fVar4 = FUN_18010a6e0(param_1, param_2);   // and continues from what this RETURNS
```

and `18010a6e0` is a rotation: when a node's two children differ in height by
two, the taller child comes up, hands its own shorter grandchild down into the
rotated node's place, and the function returns the subtree's new root. It is the
standard single rotation of a dynamic AABB tree, down to the `F.height >
G.height` sub-case picking which grandchild stays up. Ours had none.

**Nothing in the test suite could see it**, and that is the lesson worth keeping.
A query tests BOXES, not shape, so an unbalanced tree answers every query
correctly -- the five existing tree tests, including the one that checks a query
against a brute force scan after every absorb, all pass either way. What the
balance changes is the ORDER a query returns candidates in, and
`180031680` takes a rival no dearer than `DAT_18017f0cc` over the one it holds,
so the order decides which of two equally priced pairs a cluster ends up
offering. `BoxesInsertedInALineStayShallow` pins it now through the one thing it
is observable through: 4,096 boxes inserted along a line come out 12 deep, where
without the rotation they are a list.

| | before | after |
|---|---|---|
| merge final, ze_hold_em_p | -9.69% | **-7.36%** |
| merge final, cardtest | +3.81% | +3.75% |
| merge final, probe01 | +3.15% | +3.15% |

ze_hold_em_p went from 233 clusters to 239 against the compile's 258 **without
its first pass cost changing by a digit** -- still 21,000.4 against 21,940.0.
That is exactly what a merge order difference looks like and nothing else does:
the same costs, spent in a different sequence, ending somewhere else. It also
made the pass about 20% faster, which is the ordinary reason to balance a tree
and the reason the omission was invisible until the numbers got this close.

### And the probes' +3%

Now an OVER-merge where it used to be an under-merge, so whatever is left there
is smaller than what the pre-merge was worth, and it is a cost-limit problem
rather than an exhaustion one.
2. Not `g_flConfigMaxCoord`, which was the obvious suspect and is now read
   rather than reasoned about. visbuilder imports the symbol from tier0.dll,
   whose export table puts it in `.data` holding `0x46800000`, so it is
   **16,384**. We had been casting to the scene box's diagonal instead, which on
   ze_hold_em_p is 21,403 -- the one map long enough for the difference to exist.
   It is the compile's constant and it is used now, and it changes nothing on any
   of the three: the probes are 4,096 units across, and ze_hold_em_p's clusters
   never have 16,384 units of corridor in front of them inside a 512 unit merge
   cell. Ruled out by measurement rather than left as a note.

### Three fidelity gaps closed on the way

**`18004b970`, a whole re-trace pass we did not have.** A ray that stops on a
surface which is nodraw and nothing else inside the `0x1030` mask is traced
again with nodraw ignored, and the second surface replaces the first when it is
ordinary and the centre faces it. So a nodraw pane is not what a region sees,
the geometry behind it is. It is implemented now, in the gather and in the
second pass, and it is inert on all three specimens: ze_hold_em_p's 4,548
triangles carry only `0x0000` and `0x0800`, and the probes have no
nodraw-only triangles either. It will matter on a map that has them.

**`18010cb10` and `18010c6e0` are per cell slab tests, not range products.**
This was read backwards twice and the correction is worth stating plainly.
`18010c6e0` builds a per axis enter and leave for each of the four slices of
each axis, then tests every one of the 64 cells on its own as
`max(enterX, enterY, enterZ, 0) <= min(leaveX, leaveY, leaveZ, 1)`, laying the
answer down as sixteen `movmskps` nibbles at `x + 4y + 16z`; `18010cb10` does
the same for 8 octants at `x + 2y + 4z`. Our `Crossed` took the bounding box of
the segment's passage and returned the product of three ranges, which lights up
every cell in that box: a diagonal through a leaf returned the block around the
line instead of the line. Fixed, it takes a march on ze_hold_em_p from 12.52
leaves and 13.36 regions to 11.25 and 12.02. It does not move the 831 by itself,
and it is also what `18002d9e0` walks the PVS with, where over-reporting cells
means over-reporting which clusters a line joins.

**Facing is measured from the landing point.** `18004b260` computes
`dot(n, centre) - dot(n, hit)` rather than comparing against the triangle's
stored plane distance. Ours now does the same in both passes. It changes no
number here, which means the two were already equal, but it is no longer an
assumption.

### The tree is ported, and its wiring is not finished

`VisBoxTree` is the real structure now, not a stand-in: the 48 byte node with
box, height, parent, two children and a payload; the pool of 32 with a free list
that doubles; `Create` copying the caller's box verbatim; the SAH sibling search
that descends while going down is cheaper than branching here; `Move` as a
remove and reinsert; `Destroy`; the refit walk-up from `18010a5d0`, which fixes
each ancestor's box and height and does NOT rotate; and the exact inclusive
query. Five tests hold it to a brute force scan, including a query after every
absorb and a run that grows the pool well past its first block.

Standing it up in place of the linear scan has now been attempted twice and is
still NOT done. It takes probe01's first pass from 2,466 clusters to 1,138. What
a second round of instrumentation established, so the next attempt does not
repeat it:

- An audit comparing every live cluster's box against the box the tree holds,
  run after the proxies are created and on both sides of every absorb, **never
  fires**. The tree is in step, which rules out the stale-box reading from the
  first attempt.
- Fixing the query's traversal order does not explain it either. `18010bbb0`
  writes child2 into the slot it just popped and child1 above it, so child1
  comes off next; ours pushed Left then Right and popped Right first. Corrected,
  probe01 moves only from 1,138 to 1,143.
- Walking the chain from a missing leaf up to the root shows the LEAF ITSELF
  failing the query test, correctly: a leaf at `<248, 916, 240>` against a query
  ending at x = 241. So the tree prunes it right and the cross-check's own
  linear scan is what disagrees, which cannot be true if the boxes match and the
  two tests are the same expression.

Those three cannot all hold at once, so one of the instrumentation assumptions
is wrong rather than the tree. The tree keeps its own tests, the linear scan is
what runs, and the next attempt should start by proving the cross-check itself
on a case it is known to get right.

### The candidate query IS a dynamic AABB tree, and it is exactly our test

`Touches` was a substitution, so all five of the tree's functions were
decompiled to settle whether it is a faithful one. It is, and here is the proof
rather than the assertion.

| address | what it is |
|---|---|
| `18010a520` | construct: 32 nodes of 0x30 bytes, root -1, free list |
| `18010ad30` | allocate a node, copy the box, then `18010af00` to insert |
| `18010af00` | insert a leaf, choosing a sibling and rebalancing |
| `18010bbb0` | query a box, returning node indices |
| `18010b510` | move a proxy: rewrite its box and reinsert |
| `18010ae00` | destroy a proxy |

The node is the familiar 48 byte one: box at `+0x00`, height `+0x18`, parent
`+0x1c`, children `+0x20` and `+0x24`, payload `+0x28`, with `child1 == -1`
marking a leaf. That is exactly the layout `18003d600` reads.

Three facts decide it. **No box is ever fattened**: `18010ad30` copies the
caller's 24 bytes verbatim, and so does `18010b510`, which is the only other
writer. **The query is an exact inclusive overlap**, `query.mins <= node.maxs`
and `node.mins <= query.maxs` on all three axes, with no margin. And **the tree
is kept live**: `180030a50` calls `18010b510` with the survivor's new box and
`18010ae00` on the one it absorbed, before rebuilding the survivor's candidates
through `1800306e0`.

An exact query over live boxes returns precisely the leaves whose boxes overlap,
which is what a linear scan with the same expansion returns. The tree is a
speed structure and nothing else, so porting it would add about three hundred
lines of sibling selection and rotation for a provably identical answer. That is
now established by decompilation rather than assumed, which was the thing that
needed fixing.

One detail worth keeping: the tree holds the shell boxes as well as the
clusters, and `1800306e0` filters them out by the slot's `+0x19` flag. Ours
never offers them as candidates in the first place, which is the same set.

### Where ze_hold_em_p's error actually comes from

Lining the three specimens up by their UPSTREAM error makes the merge look much
less guilty:

| map | enclosed regions | clusters generated | final pass |
|---|---|---|---|
| probe01 | -0.05% | -1.26% | -2.10% |
| cardtest | +0.36% | -1.22% | +4.18% |
| ze_hold_em_p | **+7.87%** | +2.81% | **+35.66%** |

The two maps whose region count is right land within a few percent. The one
whose region count is nearly eight percent high is the one that falls apart, and
its 831 extra sliver regions are a known open item in outside detection, not in
this stage. `Compact` already folds a leaf's boxes into one region per leaf, so
the seeding shape matches; what differs is WHICH leaves are called enclosed.

### How a leaf is actually seeded, and where the Tag comes from

`FUN_180032b80` is not the generator, it is a filter: it walks the entry array
and collects the INDEX of every entry whose kind is open. Those indices are the
work list for a parallel functor whose vtable is `18017c1d0` and whose body is
`18003ddb0`, and that body calls the real seeder once per open entry:

> `FUN_180032d80(sampler, outSet, entryIndex)`

It does three things. It takes the entry's leaf box and runs the open-space test
on the **first set bit's** box at quality 6, taking the one-cluster fast path
only when the nearest surface is at least `PreMergeOpenSpaceDistanceThreshold`
away and is not the no-hit sentinel. Otherwise it asks `FUN_18002beb0` for a
list of candidate BOXES over the leaf, and then, for each box in turn, walks the
64 bits and takes every voxel that falls inside that box, expanded by a small
epsilon. The LAST box takes whatever is left regardless. Each accepted voxel
becomes its own cluster record, stamped with `VoxelSize` at `+0x50` and, at
`+0x52`, **the box's tag**.

That is where `Tag` comes from, and it is the field
`VisMergeCost`'s `TagMismatchPenalty` multiplies the cost by 32 for.

### The candidate boxes are the mapper's visibility hints

`FUN_18002beb0` walks THREE lists of boxes held on the sampler and splits the
leaf box by every one that overlaps it, through `FUN_18002bcd0`:

| list | count | mode | what it is |
|---|---|---|---|
| `+0xa8` | `+0xa0` | 0 | x-axis split hints |
| `+0xc0` | `+0xb8` | 1 | y-axis split hints |
| `+0xd8` | `+0xd0` | 2 | z-axis split hints |

`FUN_18002b500` fills them from the map's `visibility_hints`, classified by
`hintType`: 4 is x, 5 is y, 6 is z, and the compile logs each one as
`%d) x-axis split hint %.2f - %.2f`. The tag is a single counter that starts at
1 and increments across all three lists, so it is the 1-based index of the
splitter that carved the piece out. When nothing is left to split, the remainder
box is appended with tag **0**.

So on a map with NO hints exactly one box comes out, the whole leaf with tag 0,
every cluster in that leaf is tagged 0, and the tag penalty never fires. That is
the case we implement, and it is why probe01 and cardtest now land within about
two percent while ze_hold_em_p does not.

(2026-09-28: the split path is now ported from the 09-23 build, and the order
above is wrong: `CandidateBoxes` cuts by the z list, then x, then y; see
"Visibility hints, ported" at the end.)

**What this would mean for ze_hold_em_p, stated as a hypothesis rather than a
result.** With hints, a leaf's voxels are split into tagged groups, merging runs
cheaply WITHIN a group and at 32 times the cost across one, so clusters grow
along the hint volumes, which span many leaves. That produces few clusters each
covering many leaves, and leaves shared between clusters, which is exactly
Valve's 258 clusters over about 74,000 pairs. Without hints everything is tag 0,
merging is purely local, and you get many small clusters that partition the
leaves, which is exactly our 350 over 11,558.

**And it is refuted. None of the three maps ships any hints.**

Chasing it down took three steps, because the first two were inconclusive rather
than negative. The compiled VPK carries `world.vwrld_c`, `default_ents.vents_c`
and a `.vmap_c`; walking every KV3 block of every resource in all three maps for
a key containing "hint" or "visib" returns only `m_nSkyVisibilityCluster` and
`m_nSunVisibilityCluster`, both from the vvis itself. `m_builderParams` turns out
to be lighting and compile metadata, and the `.vmap_c` is a stub holding only
RERL and RED2, so the compiled output could never have answered the question
either way.

The answer is in the SOURCE map, which is on disk under `content/csgo_addons`.
A `.vmap` is binary DMX with a plain string table, so a byte search works, and it
was run with a control: `CMapWorld`, `classname` and `CMapEntity` all appear in
each of the three, so the search is sound. On that basis:

| map | control | `hint` | `visibility` |
|---|---|---|---|
| ze_hold_em_p | ok | **0** | 4 |
| probe01 | ok | **0** | 0 |
| cardtest | ok | **0** | 0 |

ze_hold_em_p's four are `visibility_pathrange`, `visibility_threshold`,
`visibility_radius` and `visibility_samples`, which are light probe properties.
No hint entity, no `hintType`, nothing.

So Valve's build of ze_hold_em_p tags every cluster 0, exactly as ours does, and
`TagMismatchPenalty` never fires on any specimen we own. Hints are a real feature
we do not implement, and implementing them would not move a single number here.
Whatever makes Valve's clusters span 287 leaves and overlap does so with
**uniform tags**, which is a much tighter constraint on what is left to find.

### ze_hold_em_p's cluster count and the assignment gap are ONE defect

Our final clusters cover 11,558 (cluster, leaf) pairs on ze_hold_em_p, over
11,385 regions. That is a **partition**: essentially every leaf ends up inside
exactly one cluster. Valve's 258 clusters cover roughly 74,000 pairs over about
10,554 leaves, so each leaf sits in about seven of them and their clusters
**overlap**.

Each newborn cluster carries exactly one (mask, leaf) pair, and a leaf is born
with eight or nine of them, one per open voxel. `18002f250` ORs two records that
share a leaf when it merges two clusters, so a leaf's siblings collapse to a
single pair the moment they end up in the same cluster. Ours always do. Valve's
land in about seven different clusters and survive.

That single fact explains both open numbers:

- **350 clusters against 258.** Merging every leaf's siblings together exhausts
  the candidate graph early, and the merge stops at its connected components.
- **Assignment at -60%.** The flat array's length IS the pair count, so a
  partition can never reach a total that assumes overlap.

So these are not two defects to chase separately. Whatever keeps Valve's
same-leaf siblings apart is the one thing left in this stage, and it is worth
saying that neither the cost function, the ordering, the candidate query nor the
sampler explains it, because all four are now read from the image and match.

### The candidate query grows by BaseVoxelSize

`1800306e0` starts one unit out from the cluster's box and, while the query
still finds nothing but the cluster itself, grows by `sampler+0xf0` up to four
more times. That field is `ResourceCompiler/VisBuilder/BaseVoxelSize`, default
8, which the shipped gameinfo does not override; the constructor stores it at
`+0x1e` of an eight-byte-stride object, which is `+0xf0`. We were growing by the
cluster's OWN voxel size, which agrees at level 0 and not above it. Correct now,
though it moves no number on the three specimens.

### The sampling marked on the BOX, and the compile marks on the VOXELS

This is what the fifth pass was exposing, and it is in `18003d600`, the walk
that turns one traced segment into set bits.

At a leaf of its box hierarchy the payload is a slot index, and there are two
cases. A slot with **no cluster** is a shell box, and its bit is set on the box
test alone. A slot **with** a cluster is not: the walk takes the segment's
reciprocal direction and goes through that cluster's own (mask, leaf) pairs, and
for each one asks `FUN_18010c6e0` for the 64 bit mask of the leaf sub cells the
segment crosses. The bit is set only if `pair.mask & crossed` is non zero.

> Crossing a cluster's bounding box is only the broad phase. A cluster is seen
> when the segment passes through a cell it actually occupies.

We were stopping at the broad phase, so every vector was over-populated, every
`|A \ B|` came out too small, and the cost the merge is steered by was too low.
That is why the error grew with cluster size: it is worst exactly where boxes
are emptiest, which is the fifth pass.

`FUN_18010c6e0` is the same sub-cell mask the PVS walk uses, so it was already
ported as `VisVisibility.Crossed`. What it needed was a leaf index to cube
lookup threaded down to the sampler, which the compile reaches through the
sampler held at the merge state's `+0x20`.

### The two fixes only work together

Neither the voxel confirm nor the ordering tie break is any good on its own, and
both are needed for the fifth pass. probe01:

| | baseline | tie break only | voxels only | **both** | compile |
|---|---|---|---|---|---|
| first pass | 1,946 (-1.67%) | 1,977 (-0.10%) | 1,958 (-1.06%) | **1,954 (-1.26%)** | 1,979 |
| second pass | 1,757 (-3.20%) | 1,826 (+0.61%) | 1,792 (-1.27%) | **1,781 (-1.87%)** | 1,815 |
| fifth pass | 1,589 (-1.97%) | 1,286 (-20.67%) | 1,326 (-18.20%) | **1,587 (-2.10%)** | 1,621 |

cardtest moves the same way, from -5.56%/-7.67%/-5.97% to +4.15%/+4.60%/+4.18%.

ze_hold_em_p goes the other way, 301 clusters to 350 against 258, and that is
worth stating plainly rather than burying. It never reaches its budget or its
cost limit on any pass: its buckets run out of candidates and it stops at its
connected components. So its number measures connectivity, not the merge, and
both fixes being correct made a compensating error elsewhere visible. Its bound
in the test is wide on purpose and guards drift rather than asserting
correctness.

### Everything in the five-pass chain, verified against the image

The fifth pass over-merges once the ordering is right, so the whole chain was
checked component by component rather than reasoned about. All of this now
matches what we do, and none of it is the cause.

**The pass parameters.** Read out of the image rather than trusted: cells
512, 512, 2048, 2048, 4096; margins 0, 256, 0, 1024, 0; budgets 6, 5.75, 4.5,
4.25 and 3 times the target; and the first pass's incoming cost limit is
`DAT_18017f18c` = 20.0, which is the same 20 as the small-region threshold.

**The per-bucket budget.** `FUN_18002f480` resizes the set list to exactly n,
and `180034220` forms the budget as `ceil(total / n)` with that same n. The set
index is DENSE over the buckets that actually got a cluster, not the grid: the
box for slot `lVar4` is computed from the key, so the two are different numbers.
Dividing by the full `acrossX * acrossY` grid instead takes probe01's first pass
to -9.75%, and the reading behind that was simply wrong.

**The cost chain.** Ghidra types both `180034220` and
`CVoxelSampler3::MergeClusterSet` as `void`, which is wrong: the assembly ends
`MOVAPS XMM0, XMM6`, and XMM6 is reloaded from the slot written immediately
after the `DIVSS`. So MergeClusterSet returns the **average**, and `180034220`
passes it straight out. The chain is what we have.

**The cost function `1800301c0`**, term for term. `180014ea0` is
`dest = x & ~y`, so each side counts what the other can see and it cannot,
weighted by the other's voxel count, plus one. `18002fec0` is the axis-aligned
box gap, squared and rooted, added AFTER the scale. Every constant checked
against the image: coarse weight 0.25 above voxel size 8, size mismatch 128, tag
mismatch 32, spread 8, area limit 4096, z limit 80, scale 10.

**The sampler.** The switch to the 512 direction golden sphere is at
`0x1ff < count`, so 512 or more slots, which is what we use. Each slot's bit
vector is `ceil(n / 32)` words with one bit per slot. Sampling runs 64 slots to
a `SampleGridsJob`, and each slot goes through `180031a20`: below the sphere
threshold the directions are `normalize(gridPoint - centre)` per shell entry,
above it the 512 unit vectors are used as they are, and the reach is the
diagonal of the merge box.

### What the fifth pass is actually doing

Instrumenting the buckets settles the mechanism, whatever the cause:

| pass | cell | buckets | budget | perCell | biggest | clusters |
|---|---|---|---|---|---|---|
| 1 | 512 | 20 | 5,172 | 259 | 259 | 2,484 |
| 2 | 512 | 20 | 4,956 | 248 | 209 | 1,977 |
| 3 | 2,048 | 4 | 3,879 | 970 | 820 | 1,826 |
| 4 | 2,048 | 4 | 3,663 | 916 | 820 | 1,826 |
| 5 | 4,096 | 4 | 2,586 | 647 | 641 | 1,286 |

The fourth pass does nothing at all, because every bucket is already under its
budget. The fifth is driven **entirely by the per-bucket cap**: the cost limit
by then is about 107,000, which almost no individual merge reaches, so the loop
only stops on the budget. Valve's 1,621 is consistent with capping roughly one
bucket; ours merges 540 rather than the ~173 that would be.

So the fifth pass's divergence is not the ordering, the parameters, the budget,
the chain, the cost or the sampler's structure. What is left is the sampled
values themselves, and that is where the next look belongs.

### The pre-merge is not the answer either

`18002f5c0` runs `CBoxMerge` (`180028c70` driving
`CBoxMerge::MergeBestCandidates`), and that IS a round based Boruvka matching:
every cluster proposes its cheapest partner in parallel, the global minimum cost
is taken, and every pair at that cost merges at once with each cluster allowed
one merge per round. It looks like exactly the mechanism that would spread
clusters out.

It is not, because on our maps it barely runs: ze_hold_em_p goes 93,354 to
93,354, nothing merged, and cardtest 81,991 to 81,865. Whatever produces the
coverage gap happens in the five passes.

### The rest

| stage | what is known |
|---|---|
| `18002f5c0` | the distance pre-merge, which prints `pre-merged to %d clusters` |
| `18002ed60`, `180037840` | the OTHER assignment, which prints `Assigned %d clusters` |
| `CVoxelSampler3__AdaptivelySampleBorders` | prints `Adaptive border clusters` |
| `18003c010`'s volume gate | below 2^20 cubic units of enclosed space the whole PVS is disabled, with `Visibility cannot be determined in this map` |
| `18003c010`'s collapse | `Collapsing resolution`, `Reduced node count from %d to %d`, `%d unique masks` |
| `180048120` | the write, which our codec already does byte-exactly |

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

## Addresses do not survive a game update, so stop quoting them

On 2026-09-23 CS2 rebuilt and every address in this file moved at once.
`0x18017f128`, the coarse weight, went from holding `0.25` to holding the ASCII
of `idates()`. Doing that by hand once is research; doing it every patch is not,
so the addresses are derived now rather than quoted.

`docs/visbuilder.signatures.json` holds, per symbol, the bytes around it with the
ones the linker moves blanked out, and `tools/sigscan.py` resolves them against
any build. [`REVERSING.md`](REVERSING.md) is the whole procedure: what is
installed, the five steps of an update, how a signature is built, how to recover
one that broke, and where names come from. The short version of the result:

| | 2026-07-09 build | 2026-09-23 build |
|---|---|---|
| symbols in the manifest | 94 of 97 | **97 of 97** |
| known constants read back and matched | -- | **18 of 18** |

The manifest is signed against the 2026-09-23 build now and still reaches 94 of
its 97 symbols backwards into the older one, which is the real evidence that the
method is build agnostic rather than tuned to one image.

### What the rebuild actually changed

Three symbols needed recovering by hand, and what they turned out to be is worth
recording because two of them looked like behaviour changes and were not:

- **`Normalise` was inlined.** The old `180109570` was a whole normalise, an
  inline fast path guarded by a magnitude range with a double precision
  fallback. The new build hoisted the fast path into all 17 callers and left the
  fallback as `18010ac60`, 201 bytes against 391. Nothing about it behaves
  differently.
- **`SampleCluster` grew by 284 bytes and is otherwise identical.** An earlier
  note here said Valve had changed the visibility sampler, which would have
  mattered a great deal for the merge. **That was wrong.** Decompiling
  `180032fa0` against the old `180031a20` shows the same reach from the set box
  diagonal, the same `(flags & 1) == 0` choosing the reach over the hit
  distance, and the same walk; the extra bytes are the normalise fast path
  inlined into its direction loop. The signature broke because its BYTES
  changed, which is not evidence about what it does.
- **`AbsMask` had all four of its read sites inside functions that changed.**
  Found again at `180182500` by reading which `DAT_` the new `CheapestPair`
  takes as a mask, and confirming the bytes are `7fffffff` four times over.

**Every one of the constants the port depends on came back with the same value.**
Nothing Valve changed in this update touches what the vis compiler computes.

One fidelity item did fall out of it, and it is recorded here rather than fixed:
the compile's normalise has a magnitude guard with a double precision path below
and above it, and our `VisSeed.Directions` uses a plain `Vector3.Normalize`. For
direction vectors built from a box's half extents the fast path always applies,
so it is very unlikely to matter, but it has not been shown not to.

## Parity: every printed stage exact, and how (2026-09-23)

Everything from voxelize to assignment now lands on the compile's own numbers on
all three specimens, 0.00%, and `VisClusterSetTests` asserts it at 0.01% on our
own target. None of it came from reading harder. Every defect below was found by
capturing the compile's intermediate state from the live process with Frida and
replaying the SAME input through the port (HANDOFF_VIS.md section 4 has the tools).
Stated once and plainly: end-of-stage counts could not have found these, and
several were hidden by errors that cancelled.

### Earlier statements in this file that were wrong

- **"`18002fec0` is the box GAP, zero the moment two boxes touch."** The
  decompile stops at the square root; the instructions continue. The term is
  `0.5 * clamp(gap / 128, 0, 1)` plus a contact half, `0.5 * (1 - shared face
  area / face area)` over the widest and middle overlap axes, the face being the
  first box's. Half of all merge prices were off by up to 0.5.
- **"The candidate query is a speed structure and nothing else."** True of the
  merge's AABB tree, false of the tracer's kd tree: the voxelizer only ever asks
  about occupancy THROUGH that tree, and its walk is asymmetric (a box whose
  minimum lies on a split descends the upper side only). The tree is also not
  the file's: the loader rebuilds it (`RefineNode`, 65 nodes on probe01 against
  the file's 181).
- **"The pre-merge's binary defaults ARE used: 1,024 and 4."** The live
  `BestPartner` is handed 2,048 and 4.
- **The pre-merge "barely runs".** It ran wrongly: tree payloads were read as
  current run indices after compaction, so partners were looked up on the wrong
  runs.
- **"The merge sequence is order among near-equal costs."** It was concrete:
  survivors are handed on in swap-with-last order, and the next merge numbers
  its clusters by it.

### What the sampler needed, all of it now matching bit for bit

The tracer does not trace the file's triangles. `LoadRTEFromFile` rebuilds each
triangle's corners (`FUN_1800233f0`), a fresh environment re-derives normal,
plane, axes and edge equations from them (`FUN_180118e90`), and traces through
the batch tracer: direction renormalised with `rcpps` and one Newton step, the
hit accepted only for `0 < t` strictly and `|denom| > 1e-10`, dot products summed
z first. A cluster whose centre sits on a wall traced every ray to t = -0 before
this and saw almost nothing: that was all of probe01's sampler loss. The
sampler also aims at itself, normalises by reciprocal, reads the folded nodraw
flags and guards the walk's divisor as `18003ed60` does.

### What upstream needed

- Leaves in node pool order: a branch's eight children get consecutive slots
  when it splits, then it recurses. We had walked the tree in reverse, so no set
  was at the right position.
- The merge grid laid over the TRACER's bounds (the loader's rebuilt corners,
  floor -2.6e-5 on probe01, a whole 512 cell lower than the header's 0).
- The pass average as a float running sum; the pass target as the volume rule
  less two.
- Valve's own SAT (`FUN_18010b310`: normalised edges, its projection pairs,
  strict rejection) on the tracer's corners, through the rebuilt kd tree. That
  also closed the probes' long standing +0.28% voxelize gap.
- The pre-merge: swap-with-last compaction, the binary's tie-break on equal
  volume (a worse ratio can still win on box order), and MSVC's own
  `std::sort` for the pairs, because mutual partners make equal keys.

## After assignment: the rest of the build, exact (2026-09-23)

Everything the compile does after assignment is now ported, and the VXVS it
assembles from our own pipeline alone (our scan, no captured inputs) is byte
identical to the shipped one on all three specimens: probe01 239,244 bytes,
cardtest 243,546 and ze_hold_em_p 129,978, 0 differing on each. Each stage
was measured the same way as the first half: `tools/vis/capture_pvs.py` records
its inputs and outputs from the live compile and `VisPvsReplay` replays them.
The stages, in the order the compile runs them:

| stage | port | what decided it |
|---|---|---|
| PVS scan | `VisPvs.Scan` | three generators on one pass runner, see below |
| volume gate and target | `VisClusterList.Volume`, `Target` | the pre-merge's group volume and count, capped at 2^19 a group |
| vis-cluster merge | `VisClusterList.Run` | `CVisClusterList`, see below |
| border sampling | `VisBorders.Sample`, `Rewrite` | 294 traced points per border record, see below |
| `AssignClusters2` | `VisBorders.Consolidate` | same cluster and kind within a leaf fold together |
| sky | `VisSky.Visible` | triangles flagged 0x1000 |
| sun | `VisSun.Visible` | two traces and a walk cut to open cells |
| collapse | `VisCollapse.Run` | up to eight breadth-first rebuilds |
| output | `VisOutput.Build` | unique masks, enclosed lists, PVS rows |

### Earlier statements in this file that were wrong

- **"The boundary-points generator does not run on ze_hold_em_p, so it is
  triggered by scale."** It is triggered by `pvstype`, worldspawn's key, read
  from the map's `.viscfg`: `SampleVisForClusters` takes `pvstype == 1` and
  then builds only the cluster-centre generator. ze_hold_em_p has 1, the probes
  10.
- **`0x1000` is "coarse occupancy only".** It is what the voxelizer does with
  it, but the sky stage reads the same bit as the sky: its triangles are what a
  cluster must reach to see the sky. ze_hold_em_p has none, so it ships no sky
  or sun rows.

### The scan

- Begin-pass's set-bit scan stops at the address of each row's LAST word, so a
  candidate in the final word is never found from below (kept: it decides which
  rays are cast).
- The fill hands a pair to the generator lower id first. The boundary generator
  alternates which end casts by index, so the order matters.
- Boundary points: per cluster side, the corners of every voxel face on that
  side, keyed by the two coordinates across it; a key keeps the deepest corner,
  and a key reached exactly once becomes a point, moved 0.01 toward the centre
  of the record that first made it. A pair casts from the sides along which the
  other box reaches past this one by more than half the largest such reach.
- Large cluster regions: boxes at least 256 apart, the first's half diagonal at
  most 128, the second's at least 600. No pair qualifies on the three
  specimens; Mako produces 248,647 (see below).
- CLOS: see "CLOS, and the big-map paths" below.

### The vis-cluster merge

- Clusters 0 and 1 are ordinary here; the finaliser renumbers survivors from 2,
  giving row 0 all ones and row 1 nothing, and leaves stale words past the new
  width.
- The records' `+0x54` and `+0x56` shorts are constructed as 0 and 8 and never
  overwritten, so every pair costs `costA + costB`. The per-cluster voxel size
  (the pre-merge record's `+0x50`) only halves a cluster's weight when above 8.
- The distance weight is `(double)(8 - t * 7.5) * weight`, truncated. Ghidra
  shows it in float.
- The neighbour lists are rebuilt from each record's box grown by 8, without
  the scan's touching test, and small components are bridged to what lies
  within a box padded toward 128 units.

### The border stage

- Border records carry cluster 0. The rewrite searches a leaf for the ORIGINAL
  record's cluster, not the claim's, so every claim is appended and only
  `AssignClusters2` folds them.
- The traced points are a 7 by 7 grid per face with the edges included.
- The distinct-normal count that decides whether only the nearest claim is
  kept accumulates across all of a record's neighbours.

### The sun, and a value read from the stack

The sun's first trace runs under mask 0x5811. The second trace's mask field is
never written; measured at runtime it reads 0. Both batches run in mode 0. These
were measured with a one-off Frida probe on the two flush entry points, not
read from the decompile, which would have suggested mode 2.

### The output

A node whose enclosed list repeats one already stored in node order gets no
list at all, and lists of 1,024 clusters or more are not stored.

## CLOS, and the big-map paths (2026-09-24)

### CLOS, settled from the binary and by measurement

- The compile builds two `.los` paths. `los` is the content path with
  `content\` replaced by `game\`, loaded and verified against the scene.
  `los_errors` is `content/csgo_addons/<addon>/maps/<map>.los`, loaded
  unverified. Both were read off a live compile.
- The deterministic setup hands CLOS the `los_errors` set. Its loader format is
  a u32 count, then that many 24 byte segments (start, end); a count the file
  cannot hold loads nothing.
- Nothing in visbuilder writes `los_errors`. The writer writes `los`, but the
  deterministic run clears the driver's recording byte before the generators
  run, so the useful segments the fold collects are never kept and the writer
  is always handed an empty set. Measured: recording 0 for all four generators,
  0 hints written.
- CLOS casts one ray per hint, type 0, flagged only if the loader verified it,
  which it never does for `los_errors`. `BatchTracer` emits for a miss only from
  a type 2 ray and for a hit only when the ray is flagged (or the face is
  nodraw and the ray type 2). A type 0 hit queues a continuation ray one unit
  past the surface, but the driver frees each ray list, continuations
  included, without reading them.
- So CLOS emits no segment in the deterministic build, whatever the file
  holds. Measured: probe01 compiled with 2,000 `los_errors` segments had a
  matrix after CLOS, and every output after it, byte identical to the run
  without. `VisLos` ports it whole (loader, generator, tracer rules, the fold's
  non-sight branch) and replays that run exactly.

### What a big map exercises, on Mako

ze_ffvii_mako_reactor_v6_p enters the scan with 25,471 clusters (the first
merge caps at `MaxVisClusters * 4`, and CS2's gameinfo sets `MaxVisClusters`
to 4,096). Its capture needed blobs sent in parts: one matrix is 81 MB and
Frida drops any message over 128 MiB.

- The boundary generator's first pass stops at 8,000,672 pairs: the 8,000,000
  limit is checked after each cluster. Exact.
- The vis-cluster merge steps down by 8,192 before the final pass: 25,471 to
  24,576 to 16,384 to 8,192 to 4,096. Above 10,240 records it is partitioned
  (9 partitions here): each outer round recomputes everything, takes the
  cheapest candidate's partition, and works that partition alone for up to 31
  more rounds, recomputing only its records and listing its candidates with
  no running limit. After every step the binary resets its map to the
  identity over the survivors and hands the sampler that step's map. All
  21,377 merges match.
- The steps clamp at `MaxVisClusters` read from the same key, so 4,096 and not
  the binary's default of 2,048.

### Tracing as the batch tracer traces, and every Mako stage exact

With a brute-force nearest hit, Mako's cluster centres ended 18 rows off and
258 of 1,176,083 border claims differed. Every mismatch was a knife edge: two
triangles met at the same distance (a wall and a ceiling meeting in a corner,
one facing the ray and one not), or a segment crossing exactly where two
triangles' edges leave a 0.0002 crack. Which answer wins there is decided by
the order Valve's tracer tests triangles and which it tests at all, so the
tracer is now ported whole.

- `BatchRay` files each segment by the signs of its delta and traces a bucket
  as a packet of four once it is full; `FlushBatch` traces the rest, padding a
  short packet with copies of its first ray.
- The packet walk (batch mode 0) normalises with `rcpps` and one Newton step,
  clips to the tracer's box, and walks the rebuilt kd tree near side first by
  the packet's octant, pushing the far side when a live lane reaches it. A
  leaf's triangles are tested for all four lanes, each slot once per packet (a
  256 entry mailbox on the slot's low byte), keeping 0 &lt; t &lt; best with best
  starting at 1e23. After a leaf with triangles the walk stops once no lane's
  range reaches past its best hit; an empty leaf never stops it.
- The scan's generators fill ray lists of 4,096 (`FillRays`): a pair's rays
  go into a pending buffer and move into the list from the END of that buffer,
  as many as fit. `BatchTracer` traces each list, so the packets are fixed by
  the pair order. The border stage traces one batch per neighbour, the 294
  points in order.
- The kd build's plane move had one wrong rule. When a candidate leaves the
  lower side empty, the plane goes to the float just below the triangles'
  lowest edge: `movd`, `inc` or `dec` on the bits, one step away from zero for
  a negative edge. We had rounded to the integer beside it. It changed 230 of
  Mako's nodes and none of probe01's.

`capture_pvs.py` now dumps the tracer the compile rebuilt (nodes, leaf lists,
every slot's record, the box). `TheTracerTree` holds ours to it: Mako's 11,167
splits and 11,168 leaves and all 28,728 records identical, probe01's too.

On Mako every stage replayed from the same compile's capture is now exact:

| stage | Mako |
|---|---|
| neighbour list | 25,471 / 25,471 |
| cluster centres | 10 passes in order, 25,473 / 25,473 rows |
| boundary points | 7 passes in order (3.92 billion rays, 49 minutes), 25,473 / 25,473 rows |
| large cluster regions | 2 passes in order (608 million rays, 13 minutes), 25,473 / 25,473 rows |
| vis-cluster merge | 21,377 / 21,377 merges |
| borders | 1,176,083 / 1,176,083 claims; rewrite and AssignClusters2 identical |
| `FlatVisClusterVector`, `MutualVisibilityMatrix` | 4,096 / 4,096 each |
| sky, sun, 8 collapse iterations | identical |
| VXVS | 6,561,250 bytes, 0 differing |

probe01, cardtest and ze_hold_em_p stay exact end to end. A replay must read
the `.rte` of the compile it was captured from, since slot order follows the
file's triangle order and the file is not byte-stable between compiles;
captures now keep a copy beside them.

## What vis hands the world renderer (2026-09-24)

Besides the vvis, vis fills two blocks the map build keeps.
`CVisBuilder::Build` asks the map builder context for two containers it owns
and registers them in the vis compile's context as `FlatVisClusterVector` and
`MutualVisibilityMatrix`. The names exist only in visbuilder; resourcecompiler
reads the same containers through two getters of its own context.

The reader is the world renderer's visibility-guided mesh clustering
(`CVisibilityMeshMerger`, `visdrivenclustering.cpp`), switched on by CS2's
`game/csgo_core/gameinfo.gi`:

| key under `ResourceCompiler/WorldRendererBuilder` | binary default | CS2 |
|---|---|---|
| `VisibilityGuidedMeshClustering` | 0 | 1 |
| `MinimumTrianglesPerClusteredMesh` | 64 | 2,048 |
| `MinimumVerticesPerClusteredMesh` | 32 | 2,048 |
| `MinimumVolumePerClusteredMesh` | 216 | 1,800 |
| `MaxPrecomputedVisClusterMembership` | 16 | 16 |

It needs at least three clusters, and otherwise builds its own list. The
merger takes each cluster's bounds from its box list.

- **`FlatVisClusterVector`**, filled by the border stage before its rewrite:
  per cluster, the `SplitOpenSpace` boxes of every open record of every leaf
  in node order (no range check on the cluster), then every border claim whose
  cluster is in range, its box grown by 0.1f on every side.
  `SplitOpenSpace` grows a box from the lowest uncovered cell along x, y, then
  z while it meets no empty cell, keeps what is new, and repeats.
- **`MutualVisibilityMatrix`**, exported after the output is built, from the
  real clusters' PVS rows (sky and sun excluded). `count[k]` is how many see k
  and `pair[j][k]` how many see both, both u16 and divided as signed shorts:
  `M[j][k] = pair[min][max] / count[max]`, written above the diagonal and then
  mirrored below.

`VisOutput.FlatClusterBoxes` and `VisOutput.MutualVisibility` port both;
`capture_pvs.py` records them and `VisPvsReplay.TheNamedBlocks` compares.

One earlier measurement now has its reason. The sun's first trace ran in batch
mode 0 although the code computes `(DisableCullingForShadows ^ 1) * 2`:
CS2's gameinfo sets `BakedLighting { DisableCullingForShadows 1 }`.

### What the world renderer does with them

Reversed from the 2026-09-23 resourcecompiler; the Valve outputs below were
compiled with that build (probe01, ze_hold_em_p, ze_doom_p2, Mako; c2m2 is
older and agrees).

Only two parts of the world renderer ask the context for the blocks: the mesh
clustering, which reads both, and `PrecomputeLightVisMembership`, which reads
the box list.

**Mesh clustering** (`CVisibilityMeshMerger`) runs three times per world node,
once per mesh list, and each run logs "Splitting geometry using visibility".

- Each cluster's bounds are the union of its boxes. A cluster with no boxes
  never matches.
- A mesh's candidates are the clusters whose bounds overlap the mesh bounds
  (touching counts) and one of whose boxes does too.
- A triangle's membership: for each candidate in id order, a box-triangle
  separating-axis test with 0.001 tolerance against the cluster bounds, then
  against its boxes; the first box that overlaps adds the cluster. A triangle
  that would need more than `MaxPrecomputedVisClusterMembership` (16) clusters
  fails.
- Triangles with the same membership list become one mesh (grouped by a hash
  of the ids, seed 0x3501a674). Triangles in no cluster make one leftover mesh
  and failed triangles another.
- Small meshes are then merged. The minimums from gameinfo are scaled per pass:
  x4 (volume x8) with growth limit 1.1 and membership slack 0, then x1 with
  limit 1000 and slack 16, the pair repeated while the second merges; then x2
  with 1.25 and slack 1; then x16 with 1.1 and slack 0, repeated while it
  merges. A pair is rejected when the union box volume over the larger box
  volume passes the limit, or when the merged list outgrows both lists by more
  than the slack.
- Partners are meshes that share a cluster inside the mesh bounds grown by 240,
  with at most 65,535 vertices and 16 clusters merged. The score multiplies
  `M[a][b]` over every cluster `a` the merge adds and every cluster `b` the
  partner has, rounded to twentieths; an empty matrix scores 1.

The lists steer only how geometry is cut into meshes. Every node checked writes
`m_visClusterMembership = [  ]` on the node and on each aggregate, every
`m_nVisClusterMemberOffset` and `m_nVisClusterMemberCount` as 0, and no scene
object carries `OBJECT_TYPE_PRECOMPUTED_VISMEMBERS` (0x4000, the flag that
would add `m_VisClusterMemberBits`).

**Light membership** would give `light_barn`, `light_rect` and `light_omni2`
a sorted `precomputed_vis_clusters` key. It needs a context flag,
`direct_light_shadows` and an in-process lighting bake; the flag's only store
in the binary is the constructor's zero, and none of the 679 such lights in
Mako, ze_hold_em_p, ze_doom_p2 and c2m2 carries the key.

So a matching world node needs from vis the vvis and these two blocks exactly,
because they decide the mesh split, and nothing more. The merger itself belongs
to the world renderer port.

## Visibility hints, ported (2026-09-28)

Read from the 09-23 visbuilder and checked on Mako, the one specimen with
hints. `VisBuild` reads the `.viscfg`'s `visibility_hints` array (the map's
`visibility_hint` entities as the export writes them) and hands it to
`Hints_Load` (`18002c970`). Each hint's box is `origin + box_mins` to
`origin + box_maxs`, angles ignored. Types 4, 5 and 6 go to the x, y and z
split lists; every other type is a VOXEL hint (`Hints_AddVoxelHint`,
`18002ced0`):

| hintType | FGD label | voxel | region |
|---|---|---|---|
| 0 | High Resolution (8 unit grid) | 8 | 32 |
| 2 | Medium Resolution (32) | 32 | 64 |
| 3 | Low Resolution (64) | 64 | 128 |
| 7, 8 | Lower Resolution (64, fewer initial clusters) | 64 | 256 |
| 9 | Lowest Resolution (256, fewest initial clusters) | 64 | 512 |
| 10 | Reduced Resolution (16) | 16 | 64 |
| any other (1 included) | Normal | base voxel | 4 x base |

A voxel hint's box side at or past the scene's bounds is moved out to the root
cube's; the voxel size is clamped to [4, 256]; the region size is the
requested one held between the unclamped voxel size and four times it. The
list is sorted by voxel size, then region size (`18003f940`, an insertion sort
under 33 entries, so equal ones keep entity order).

**Voxel hints change the octree** (`Voxelize`, `18002f890`, called with the
base voxel size, not the leaf size). For each node wider than the base voxel,
the first hint in sorted order whose box touches the node's decides: if the
hint's box holds the node whole, the node stops splitting once
`(int)(region / base) >= (int)(side / base)` (4 without a hint, the smallest
leaf of 32 units), becoming a leaf of that size with its own 4x4x4 mask; and
while `voxel <= side * 0.25`, every child is descended, the empty ones too.
Mako's seven hints are all voxel hints (four of type 3, one of 9, two of 8).
Without them our octree has 3,197,641 nodes; with them 2,643,577, the node
count of Valve's compile captured at scan entry, exactly. The `.viscfg`'s
hints are rebuilt from the `.vmap` bit for bit (`SettingsFromTheMap`).

**Split hints** (`CandidateBoxes`, `18002d320`, and `CandidateBoxes_SplitOne`,
`18002d140`) are ported as read and not measured: no specimen has one. A leaf
box is cut by the z list, then x, then y, a tag counting from 1 over every hint
of the three lists in that order. The box being cut is given up once any side
is under 1.1. A hint that touches it cuts it along its axis unless it reaches
less than 1.1 into it; the piece inside the hint is pushed with the hint's tag;
with pieces left on both sides the lower one is cut again from the start and
the upper one carries on, otherwise the one piece left carries on. What
remains is pushed with tag 0. `GenerateRegionClusters` (`180034420`) then deals
the region's voxels out box by box: a voxel not yet taken goes to a box when
its sub-cell lies inside the box grown by 0.1 (the last box takes the rest),
the box's clusters carry its tag, and each box's clusters are merged on their
own before they are appended.
