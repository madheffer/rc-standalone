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
