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
| `18004b260` | gather the candidate rays for a box | queries the structure at `this+0xe8` |

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

**Flag bit 0 means outside.** Outside detection sets it, the ray march stops on
it, and the region count excludes it. Bit 1 is set at region creation and also
excludes a region from the count; both are cleared for a region that counts.
