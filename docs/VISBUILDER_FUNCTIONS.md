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
| `180032670` | region compaction | rewrites each leaf's regions to a merged pair, writing `(node << 2) | bits` |
| `180032b80` | the region count the compile prints | fills a vector with the regions whose flag bits 0 and 1 are both clear, immediately before `MergeInsideRegions` logs the count |
| `18002e050` | classify one region as inside or outside | called per region by outside detection |
| `18002deb0` | cast one ray through the voxel octree | called per candidate ray by the classifier |
| `18010be50` | a region's box from its leaf box and mask | shared by the driver and outside detection |
| `18003f1e0` | the `InitialRegionStatus` job body | the work pointer in the functor vtable the outside pass dispatches |
| `18004a2f0` | the SEED: inside, outside or undecided for one region box | the only thing the job body calls, and a threshold tree over the gather's counters |
| `18004b260` | gather over a box, returning four counters and a ray list | called by the seed, the per-region classifier and one more site; the structure it queries is `this+0xe8` and its record type is NOT decoded |

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

## How the whole DLL was inventoried

Chasing a string answers one question. `InventoryDll.java` dumps every function,
every call edge and every string reference in one pass, which is what the table
above was actually built from: **3,848 functions, 13,778 call edges and 3,363
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
