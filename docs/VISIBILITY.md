# Visibility

`world_visibility.vvis_c` from the trace scene, ported from `visbuilder.dll`
stage by stage. Function addresses are in [`ADDRESSES.md`](ADDRESSES.md)
(visbuilder, build 20260923); the ports cite them in their comments.

## Status

Every stage is exact, on Valve's captured inputs and end to end:
- **From Valve's .rte:** probe01, cardtest and ze_hold_em_p give a
  byte-identical VXVS (239,244, 243,546 and 129,978 bytes). Mako's captured
  run is exact in every stage, VXVS 6,561,250 bytes.
- **From our own trace scene (built from the .vmap):** probe01, cardtest and
  ze_hold_em_p give a byte-identical VXVS. atixref and Mako have not been run
  end to end; atixref's cluster generation alone takes about an hour.
- **Visibility hints** come from the .vmap: Mako's `.viscfg` hints are
  rebuilt bit for bit, and its octree node count matches Valve's (2,643,577).

Open:
- The whole chain is not yet called by `compile-map`; `TheWholeBuild` is the
  reference order.
- Split hints (types 4 to 6) are ported but untested: no specimen has one.
- `NormaliseSlowPath` (lengths under 1e-17 or over 1e17) is approximated by a
  double normalise in three places; no specimen reaches it.
- `-updateloshints` and the legacy sampler are not ported. These are the
  non-deterministic paths, and a stock compile uses `DeterministicBuild 1`.

## The output: VXVS

`RED2 DATA VXVS`, resource version 7. VXVS is six arrays end to end with no
header; DATA indexes them by prefix sums of the counts.

| array | stride | holds |
|---|---|---|
| `m_NodeBlock` | 8 | octree nodes |
| `m_RegionBlock` | 8 | cluster regions owned by leaves |
| `m_EnclosedClusterListBlock` | 8 | `(i32 offset, i32 count)` |
| `m_EnclosedClustersBlock` | 2 | `u16` cluster ids |
| `m_MasksBlock` | 8 | 4x4x4 occupancy masks |
| `m_nVisBlocks` | 1 | PVS bit matrix |

The node and region records:
```
Node, two u32
  word 0   bit 0 isLeaf; bits 1..31 first child (branch, 8 by octant) or first region (leaf)
  word 1   bits 0..7 regionCount; bits 8..31 enclosedListIndex (0xFFFFFF none)
Region, one u64
  bits 0..14 clusterId; bit 15 intersectsGeometry; bits 16..39 leafIndex; bits 40..63 maskIndex
```

The PVS matrix:
- It is one row per cluster, bits least significant first, row stride
  `roundUp4(ceil(clusters / 8))`.
- Cluster 0 sees everything. Sky and sun get rows of their own only when
  numbered past the base clusters.
- A leaf is four base voxels a side; its regions carry masks over the
  4x4x4 cells, with bit `x + 4y + 16z`.

The codec (`VoxelVisibility`) re-encodes 112 maps (481 MB) byte for byte, and
its point queries agree with ValveResourceFormat on 84,673 points.

## The pipeline and its ports

Visibility runs inside resourcecompiler's `-world` phase (not `-vis`). It reads
three files from `%TEMP%\csgo_addons\<addon>\maps\`: `<map>.rte`,
`<map>.viscfg` and `.los`.

| stage | port | notes |
|---|---|---|
| load the .rte | `RayTraceEnvironment`, `TracerKd` | the loader rebuilds each triangle's corners and its own kd tree (`RefineNode`); GEOMETRY.md has the format |
| voxelize | `VisVoxelizer` | top-down from the root cube; mask `0x811` above 256 units, `0x1811` at or below (a `0x1000` triangle stops counting); a node splits on size alone once its parent found geometry; voxel hints stop or force splits |
| outside detection | `VisOutside`, `VisSeed` | a per-region ray vote; outside is the default, inside is earned; propagates along rays, so an unsealed scene works; the seed traces the .rte's OWN kd tree (the rebuilt tracer tree gives other verdicts, GROUND_TRUTH 30) |
| regions | `VisRegions` | a region is a greedy box, not a connected component; the coarse-only retry runs at every depth; the printed count is after compaction |
| cluster generation | `VisClusters` | one cluster per open voxel, then a greedy merge by `VisMergeCost` down to 32 or a cost threshold; 56 shell boxes pad the tree and never merge |
| distance pre-merge | `VisPreMerge` | swap-with-last compaction, the binary's volume tie-break, MSVC `std::sort` (`MsvcSort`) |
| five merge passes | `VisClusterSet`, `VisMerge`, `VisBoxTree` | dynamic AABB tree with its rotation: balance decides the order candidates return in, and so the merge |
| sampler | `VisSampler`, `VisClusterSample` | rays from the box centre, at every other cluster (under 512) or a fixed 512-direction sphere |
| assignment | `VisAssign` | |
| PVS scan | `VisPvs`, `VisLos` | cluster-centre, boundary-points and large-regions generators; `pvstype` 1 builds only cluster-centre; CLOS contributes nothing in a deterministic build |
| vis-cluster merge | `VisClusterList` | caps at `MaxVisClusters * 4`, then steps down by 8,192; partitioned above 10,240 records |
| borders | `VisBorders` | 7x7 traced points per face; `AssignClusters2` folds claims |
| sky and sun | `VisSky`, `VisSun` | sky is triangles flagged `0x1000`; sun trace mask `0x5811`, batch mode 0 |
| collapse | `VisCollapse` | up to eight breadth-first rebuilds |
| output | `VisOutput` | VXVS, plus `FlatVisClusterVector` and `MutualVisibilityMatrix` for the world renderer |

The cost the cluster merge minimises (every constant read back by
`VisMergeCostTests`):
```
volA = a.voxels * (a.size > 8 ? 0.25 : 1), volB likewise
n = popcount(a.vis & ~b.vis) * volB + popcount(b.vis & ~a.vis) * volA + 1
n *= 128 if sizes differ and either is under 9;  n *= 32 if tags differ
if either size < 9: n *= 8 if both footprints <= 4096 and the union's > 4096
                    n *= 32 if the union's z span > 80 and either height <= 80
cost = boxGap(a, b) + n * 10
boxGap = 0.5 * clamp(gap / 128, 0, 1) + 0.5 * (1 - shared face area / face area)
```

Ghidra's decompile drops the second term of `boxGap`, after the square root.
Read the disassembly for anything surprising.

### Tracing as Valve traces

Ties decide results on real maps (a wall and a ceiling hit at the same
distance, or a 0.0002 crack between edges). The batch tracer is therefore
ported whole:
- **Filing:** `BatchRay` files each segment by the signs of its delta and
  traces packets of four, padding a short packet with copies of its first
  ray.
- **Hits:** each direction is renormalised with `rcpps` plus one Newton step.
  A hit is kept only for `0 < t < best` (best starts at 1e23) with
  `|denom| > 1e-10`, and dot products are summed z first.
- **The kd walk:** near side first by the packet's octant, with a 256-entry
  mailbox on the slot's low byte.
- **The kd build:** when a candidate plane leaves the lower side empty, the
  plane moves to the float just below the lowest edge (bit increment or
  decrement, one step away from zero), not to the neighbouring integer.
- **Ray lists:** they hold 4,096 rays. A pair's rays move in from the end of
  the pending buffer, so the pair order fixes the packets.

### Settings

- `gameinfo.gi` `ResourceCompiler/VisBuilder` supplies `BaseVoxelSize`,
  `MaxVisClusters` (CS2: 4,096; the binary default is 2,048),
  `DeterministicBuild` and the `PreMerge*` thresholds. The live pre-merge gets
  2,048 and 4, not the binary's 1,024 and 4.
- The `.viscfg` holds `pvstype` (worldspawn), `vDirToSun`, and the map's
  `visibility_hint` entities.
- `VisConfig` rebuilds all three from the .vmap (`SettingsFromTheMap`).

### Visibility hints

A hint's box is `origin + box_mins` to `origin + box_maxs`, and angles are
ignored. Types 4, 5 and 6 are x, y and z split hints; any other type is a
voxel hint:

| hintType | voxel | region |
|---|---|---|
| 0 | 8 | 32 |
| 2 | 32 | 64 |
| 3 | 64 | 128 |
| 7, 8 | 64 | 256 |
| 9 | 64 | 512 |
| 10 | 16 | 64 |
| other (1 too) | base | 4 x base |

Voxel hints:
- A side at or past the scene bounds moves out to the root cube.
- The voxel size is clamped to [4, 256].
- The list is sorted by voxel size, then region size; under 33 entries this
  is an insertion sort, so equal hints keep entity order.
- For each node wider than the base voxel, the first hint touching it
  decides:
  - The node stops splitting once `(int)(region / base) >= (int)(side / base)`.
  - While `voxel <= side * 0.25`, every child is descended.

Split hints (`CandidateBoxes`):
- A leaf box is cut by the z list, then x, then y. Each hint's tag counts
  from 1 over all three lists.
- A box is given up once any side is under 1.1.
- `GenerateRegionClusters` deals voxels out box by box: a voxel goes to a box
  when its sub-cell lies inside the box grown by 0.1.

### What vis hands the world renderer

`FlatVisClusterVector` (per-cluster boxes) and `MutualVisibilityMatrix`
(`M[j][k] = pair[min][max] / count[max]`, as u16 read as signed shorts) steer
`CVisibilityMeshMerger`, which splits world meshes by cluster. The CS2
settings, under `WorldRendererBuilder`, are:
- `VisibilityGuidedMeshClustering 1`;
- the per-mesh minimums, 2,048 triangles, 2,048 vertices and volume 1,800;
- `MaxPrecomputedVisClusterMembership 16`.

The merger belongs to the world-renderer port; its rules as read are in git
history (the old `VIS.md`, section "What the world renderer does with them").
Nodes ship an empty `m_visClusterMembership`, and no CS2 light carries
`precomputed_vis_clusters`.

## Running and scoring

- **Valve as an oracle:**
  ```bash
  python tools/vis/rebuild_vis.py <addon> <map> --runs N
  ```
  This rebuilds with `-world -vis -fshallow` after deleting the map vpk.
  - Valve against Valve is byte-identical, so any difference is ours.
  - `-vis` without `-world` guts the vpk.
  - `-vis -f` fails.
  - The up-to-date check uses the source CRC, so delete the output to force a
    rebuild.
- **Capture and replay** is how parity was reached. End-of-stage counts
  cannot localise a greedy-merge defect.
  - Capture scripts: `tools/vis/capture_merge.py`, `capture_rays.py` and
    `capture_pvs.py` (Frida, CS2 closed).
  - Replay tests: `REPLAY=<map>` (`VisMergeReplay`), `RAYS=<map>`, `KD=<map>`,
    `PVS=<map>` (`VisPvsReplay`), and `BIGPVS=<map>` (`VisBigReplay`, Mako).
  - A replay must read the `.rte` of the compile it was captured from, because
    the file's triangle order varies between compiles.
- **From the .vmap:** `VisBuildTests.FromTheMap` runs the whole chain on our
  own trace scene. `VISBUILD_PROGRESS=<file>` logs stage progress.
- **Diagnostics** are env-gated: `GEOM`, `DIFF`, `WALK`, `VALVE`, `SHAPE`,
  `MARGIN`, `COMPACT`, `HALT`, `OPEN`, `SCALE`, `MERGE`, `MAKO`.
- **Comparing two builds:** `s2c vis-diff <ref> -c <candidate>`. It samples
  point pairs over occupied space and counts holes (fail) and overdraw. A
  legitimate settings change moves agreement by 1.5 to 2%.

## Measured costs

On Mako's 1,299 s compile, visibility takes 1,088.7 s (83.8%) and lighting
16 s:
- LOS ray scan: 568 s, of which boundary points take 309 s and large cluster
  regions take 252 s. That generator casts 608 million rays for 383 useful
  ones.
- Cluster generation: 231.5 s.
- Merged lists: 173 s.
- Voxelize: 20.5 s.

`BaseVoxelSize 32` is 8 times faster and loses a quarter of the navigable
space. Changing visibility changes 11 of 72 files in a map vpk (vvis, world
node meshes, the node index, the world, the lump, a probe octree), so
visibility cannot be swapped alone.
