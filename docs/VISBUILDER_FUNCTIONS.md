# visbuilder.dll functions recovered from assert strings

Valve ships this stripped, but every assert names the function it sits in,
its source file and its line. 6 functions were recovered this way.


## utils.cpp

| address | function | line |
|---|---|---|
| `180027f50` | `CBoxMerge::MergeBestCandidates` | 347 |

## vis3.cpp

| address | function | line |
|---|---|---|
| `18003ac70` | `CVoxelSampler3::AdaptivelySampleBorders` | 4718 |
| `180033fd0` | `CVoxelSampler3::MergeClusterSet` | 3206 |
| `180034ea0` | `CVoxelSampler3::MergeInsideRegions` | 3304 |

## vis_cluster.cpp

| address | function | line |
|---|---|---|
| `1800423b0` | `CVisClusterList::RecomputeClusterCostLists` | 488 |
| `180042650` | `CVisClusterList::RecomputeClusterCostListsForPartition` | 526 |

# Functions recovered from the log strings and the call graph

An assert names a function outright; a log string names what it DOES, and the
call graph places it. These are unnamed in the binary and identified by
behaviour, so they carry a description rather than a symbol.

| address | what it is | how it was identified |
|---|---|---|
| `180031f00` | the voxel stage driver | logs `Voxelize (%.f units) took...` and `Assigned %d clusters...`, and calls the four below in order |
| `18002e310` | voxelize | the call the Voxelize timer brackets; takes the voxel size as a float |
| `1800321f0` | **outside detection** | the only function referencing `Outside detection took %.2f seconds` |
| `18003c010` | the visibility driver | reads every `ResourceCompiler/VisBuilder/...` config key, then `180031f00` and `180036850` |
| `180031f00` | the voxel stage driver | voxelize, outside, COMPACT, work out the cluster target from the enclosed volume, then `MergeInsideRegions` |
| `180032670` | region compaction | runs BEFORE the printed region count and collapses each leaf's regions to at most three: enclosed, outside, solid, tagged 0, 1 and 2 |
| `18002beb0` | candidate boxes for a region | splits the box against the three hint lists; with no hints it emits the box itself and nothing else |
| `18002bcd0` | split a box against one hint, and recurse | calls `18002beb0` back |
| `18002f5c0` | the distance pre-merge | logs `Distance merged regions (%d merged to %d)` and `pre-merged to %d clusters` |
| `180034220` | the re-BUCKETING before each merge | a 2D grid of 512, 512, 2048, 2048 then 4096 units, budgets 6 to 3 times the target; only x and y are in the key and a cell's box spans all of z |
| `180033fd0` | `CVoxelSampler3::MergeClusterSet` | merges every bucket at the incoming limit with DOUBLE the budget, then again at the average cost that produced; returns that average, which is the next pass's limit |
| `18003dea0` / `18003de20` | the two merge job bodies | both call `1800337a0`, the first recording each bucket's cost |
| `180036850` | the PVS entry | branches on `DeterministicBuild`: `180018a30` normally, `1800177d0` on 0 or `-oldvis`, `180017020` on `-updateloshints` |
| `180018a30` | the deterministic generator set | cluster centre, boundary points, large cluster regions, then the `.los` reader |
| `1800177d0` | the ORIGINAL sampler | a larger generator set and its own loop; also what `-updateloshints` runs to write the `.los` cache |
| `18001d610` | `CNeighboringClustersList::Build` | per region, add its volume to its cluster and link the clusters of adjacent regions |
| `18001f170` | `NeighborsScan BuildTracePointsForClusters` | called on the boundary-points generator |
| `18001bb60` / `18001bcd0` / `18001bce0` / `18001bcf0` | the four generator NAME accessors | found by scanning .text for a RIP-relative displacement onto each string, which is what Ghidra could not follow |
| `18002ed60` | per-cluster bounding boxes | max cluster id over the regions, then union each region's box into its cluster's |
| `18002f250` | merge two (mask, leaf) lists | sorts both by leaf and ORs the masks of equal leaves, which is what makes the shipped region count finite |
| `180027400` | the 512-direction sphere | a golden spiral, `turn = (3 - sqrt(5)) * pi`, used once a merge set reaches 512 entries |
| `180032b80` | the region count the compile prints | fills a vector with the regions whose flag bits 0 and 1 are both clear, immediately before `MergeInsideRegions` logs the count |
| `18002e050` | classify one region as inside or outside | the SECOND pass, sequential and in region order; casts the seed's grid and marches every facing hit, then votes `inside > 2n` or `inside > n and outside < 5` |
| `18010c3f0` | cut a leaf's open voxels into regions | a greedy BOX decomposition, not connected components |
| `18002deb0` | cast one ray through the voxel octree | called per candidate ray by the classifier |
| `18003ddb0` | the cluster generation job body | the work pointer in the vtable MergeInsideRegions dispatches |
| `180032d80` | generate one region's clusters | the only thing that job calls; fills the 24-byte record whose first int the log sums |
| `18002beb0` | candidate boxes for a region | splits the box against the three hint lists and emits what survives |
| `18002bcd0` | split a box against one hint, and recurse | calls 18002beb0 back |
| `1800337a0` | merge a region's per-voxel clusters down by cost | called at the end of each candidate's assignment loop; merges until 32 clusters or the best cost exceeds a threshold |
| `180027f50` | `CBoxMerge::MergeBestCandidates` | named by an assert; the greedy box merge the above drives |
| `18010ad30` | add a box to a CBoxMerge pool, returning its handle | NOT a cost function, which is what it looks like at a glance |
| `180030df0` | set the merge up: sample every cluster's visibility, then build each one's candidate list | the only call between the pool fill and the merge loop |
| `1800301c0` | **the merge COST** | slot 1 of the merge controller vtable at `18017bf40`, called as `cost(a, b)` and stored beside the candidate's id |
| `180030a50` | the merge ACTION | slot 2; ORs the bit vectors, sums the voxel counts, unions the boxes, takes one off the controller's LIVE COUNT, strips the dead cluster from every candidate list, then calls `1800306e0` |
| `1800306e0` | rebuild the surviving cluster's candidates | clears its list and re-queries the box tree with its NEW box dilated by a unit, recomputing every cost; this is what keeps the candidate graph connected across a merge |
| `18010cc20` | one sub-cell's box from the node's box | `(i & 3, i >> 2 & 3, i >> 4 & 3)` times a quarter of the side, no epsilon |
| `18002d670` | build a leaf's 4x4x4 mask and push its regions | queries the kd tree per cell with the same size-dependent mask, splits the OPEN space into connected components, and pushes the solid voxels as one more region flagged out of the count |
| `180030d70` | break a tie between two candidates | slot 4; prefers the larger voxel count, then `180027e10` on the boxes |
| `180030190` | is this entry still live | slot 5; both flag bytes clear |
| `180031680` | pick the cheapest merge in the whole set | scans every live entry's candidate list, minimum cost, ties by summed voxel count |
| `18003ecf0` | the `SampleGridsJob` body | slot 5 of the functor vtable at `18017c190`; runs `180031a20` over a block of 64 entries |
| `180031a20` | **sample one cluster's visibility** | casts rays from the cluster's box centre and sets a bit per cluster each ray passes through |
| `18004a690` | gather the ray hits for one origin and direction set | called by the above; hit records are 0x20 bytes, `+0x1e & 1` meaning it hit |
| `18003d600` | walk a segment through the box tree, setting a bit per box it crosses | the only consumer of those hits |
| `180014ea0` | `dst = x & ~y` over a dword run | the two calls the cost pops the bits of |
| `18002fec0` | distance between two boxes, zero when they touch | the cost's last term |
| `180027e10` | order two boxes by longest side, then lexicographically | the tie-break, so the merge is deterministic |
| `18002b500` | read the HINT ENTITIES and fill the three lists | reads origin, box_mins, box_maxs and hintType off map objects; types 4, 5 and 6 are the x, y and z axis split hints |
| `18010be50` | a region's box from its leaf box and mask | shared by the driver and outside detection |
| `18003f1e0` | the `InitialRegionStatus` job body | the work pointer in the functor vtable the outside pass dispatches |
| `18004a2f0` | the SEED: inside, outside or undecided for one region box | the only thing the job body calls, and a threshold tree over the gather's counters |
| `18004b260` | gather over a box: a 5x5 grid of rays through each of its six faces, tallied four ways | called by the seed, the per-region classifier and one more site; the counters are facing hits, back hits, nodraw hits and escapes |
| `18010e8a0` | trace one ray against the kd tree | the hit record is 56 bytes: normal, triangle id at `+0x0c`, distance at `+0x10` |
| `180108d70` | distance from a box to a point | the gather's nearest-hit bookkeeping |

## The object layout these agree on

Every one of the functions above addresses the same sampler through the same
offsets, which is what makes the layout trustworthy rather than inferred from one
site:

| offset | what |
|---|---|
| `+0x28` / `+0x30` | node count / node array, 8 bytes each |
| `+0x40` / `+0x48` | region count / region array, **16 bytes** each in memory |
| `+0x78` | per-node bounds, 24 bytes each, indexed by `flags >> 2` |
| `+0x118` | per-region working array |
| `+0x120` | per-region status byte, the outside pass's output |
| `+0xe8` | the structure the candidate rays are gathered from |

A region in memory is `{ u32 cluster, u32 (nodeIndex << 2) | flags, u64 mask }`.
That `nodeIndex << 2` is the same leaf index the compiled file packs into its
24-bit field, seen from the other side.

The reasoning behind all of this, the design decisions it forced on our own
implementation and the wrong turns it cost, is in **`VISBUILDER_ANALYSIS.md`**.

## How the whole DLL was inventoried

Chasing a string answers one question. `InventoryDll.java` dumps every function,
every call edge and every string reference in one pass, and `DumpSubtree.java`
then dumps a whole STAGE as one readable unit (the visibility pipeline is 400
functions and 47,757 lines of C that way). Between them that is what the table
above was built from: **3,848 functions, 13,778 call edges and 3,363
string uses**. Of those, 121 functions log something, which is the behavioural map
of the builder, and the binary carries **205 RTTI type descriptors of which 80 are
Valve's own** despite the exports being stripped.

The classes worth knowing, since they name the machinery the stages are made of:

- the sampler and its owners: `CVoxelSampler3`, `CVisBuilder`, `CVisBuilderMgr`,
  `CResourceCompilerMapVisibility`
- the ray generators, which are the sampling strategies the PVS stage picks
  between: `CRayGenerator` and its `CAxial`, `CBoundaryPoints`, `CClusterCenter`,
  `CClusterView`, `CLOS`, `CLargeClusterRegions`, `CNearlyVisibleNeighbors` and
  `CRandom` subclasses, plus `CRayProcessJob`
- the queries and the merge: `CClusterQuery3`, `CClusterQuerySun`,
  `IClusterSpaceQuery`, `ILeafBoundsQuery`, `IClusterRemap`, `IMergeController`,
  `CMergeControllerGrid`
- the job plumbing: `CThreadedJob`, `CUtlMultiJobProcessor`, `IMultipleWorkerJob`

`CLargeClusterRegionsRayGenerator` is the one that burned 252 seconds and 608
million rays for 383 useful ones in the earlier profiling, so it now has a name
and a class to go with the measurement.

**Flag bit 0 means outside.** Outside detection sets it, the ray march stops on
it, and the region count excludes it. Bit 1 is set at region creation and also
excludes a region from the count; both are cleared for a region that counts.

## Functions no call graph reaches

The scene the sampler is given is a 16 byte stack adapter built in `180036850`,
`{ vtable 18017c1e8, owner = the voxel sampler }`. Its slots are thunks that
reload `this` from `+0x08` and tail-jump, so Ghidra leaves them undefined and a
subtree dump never sees them. These came out of reading the vtable from the
image, decoding the thunks, and running `DumpAt.java` on the targets.

| address | what it is | reached by |
|---|---|---|
| `18002c2a0` | fold a batch's segments into the PVS | vtable `+0x18` via `18002a970` |
| `18002d9e0` | walk a segment through the octree | `18002c2a0` |
| `18001b690` | OR the mutual visibility matrix, return words changed | `18002c2a0` |
| `18002d490` | a point to its region | vtable `+0x58` via `18002acf0` |
| `18002ad60` | a region to its cluster | vtable `+0x80` |
| `18010cb10` | 8 bit octant crossing mask | `18002d9e0` |
| `18010c6e0` | 64 bit sub cell crossing mask | `18002d9e0` |

The same blind spot hid the four ray generator names, which needed a scan of
`.text` for RIP-relative displacements onto the strings. Anything reached only
through a vtable slot or a 32 bit RVA needs one of those two techniques; a call
graph will not find it, and its absence from a dump is not evidence it is dead.

## The merge, which is also unreachable

| address | what it is | reached by |
|---|---|---|
| `1800337a0` | the five passes' merge loop | `DumpCallers` on `180030a50` |
| `180030a50` | absorb one cluster into another | `DumpCallers` on `18002f250` |
| `1800306e0` | build one cluster's candidate list | `1800337a0` |
| `180031680` | scan every slot for the cheapest pair | `1800337a0` |
| `180030df0` | build every cluster's candidates | `1800337a0` |
| `180033600` | drop the merged clusters and compact | `1800337a0` |

`CBoxMerge::MergeBestCandidates` (`180027f50`) and its driver `180028c70` are
NOT this merge. They belong to `18002f5c0`, the distance pre-merge, which is a
near no-op on every map we have.
