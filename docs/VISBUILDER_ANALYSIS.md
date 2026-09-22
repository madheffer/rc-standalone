# Reading Valve's visibility builder, and writing one from it

This is the analysis behind `VisVoxelizer`, `VisRegions` and `VisOutside`: what
`visbuilder.dll` is, how it was opened up, what its design actually is, and which
of its decisions we had to adopt rather than invent. `VISBUILDER_FUNCTIONS.md` is
the address table; this is the reasoning.

It is written for someone who has to extend the work, so it records the wrong
turns as well. Three of them cost more than any of the right answers, and all
three were the same mistake: a rule that fit the data in front of it.

---

## 1. What we did NOT do, and why the method changed

The first pass at this binary chased one string, `Outside detection took %.2f
seconds`, found the one function referencing it, and read outwards. That answered
the question asked and left the other 3,847 functions unknown, which meant every
following question started from zero and, worse, that a wrong reading had nothing
to contradict it.

The second pass inventoried the whole DLL first. `InventoryDll.java` walks every
function, every call edge and every string reference in one pass:

| | |
|---|---|
| functions | 3,848 |
| call edges | 13,778 |
| string uses | 3,363 |
| functions that log something | 121 |
| RTTI type descriptors | 205, of which **80 are Valve's own** |
| functions named outright by an assert | **6** |

The ratio in the last two rows is the whole argument for the change of method.
Assert strings name a function exactly, and there are only seven of them in the
binary; they were exhausted immediately. Log strings name what a function DOES,
there are 121, and the call graph then places everything around them. RTTI names
the types even though the exports are stripped.

**The practical rule: find the logging functions, then walk the call graph out
from them.** A stage of a compiler is a subtree, and `DumpSubtree.java` dumps a
subtree at a time. The visibility pipeline is 400 functions and 47,757 lines of C
that way, which is a morning's reading rather than a research project.

### The four tools, and what each is for

| script | what it answers |
|---|---|
| `InventoryDll.java` | what is in here at all: functions, edges, strings |
| `DumpSubtree.java` | what does this STAGE do, as one readable unit |
| `DumpByString.java` | which function implements the thing this message describes |
| `DumpPointers.java` | what does this vtable dispatch to |

`DumpPointers` is the small one that matters more than it looks. Valve dispatches
its parallel work through functor vtables, so the body of a thread pool job is a
pointer in a data blob and nothing about it is a string or a call edge. Both the
`InitialRegionStatus` job and the cluster generation job were found that way and
by nothing else.

### What is not in this repository

The decompiled C itself. It is derived from Valve's binary, it is 48,000 lines,
and it regenerates in minutes from the scripts above, which ARE here under
`tools/vrad3/`. What is written down instead is the analysis: addresses,
structures, rules, and the numbers that confirmed them.

---

## 2. The object model

Every function in the stage addresses one object, `CVoxelSampler3`, through the
same offsets. Agreement across a dozen independent call sites is what makes this
trustworthy rather than a guess from one place.

| offset | what |
|---|---|
| `+0x28` / `+0x30` | node count / node array, 8 bytes each |
| `+0x40` / `+0x48` | region count / region array, **16 bytes each in memory** |
| `+0x78` | per-node bounds, 24 bytes each, indexed by `flags >> 2` |
| `+0xa0` / `+0xa8` | x-axis split hints, 6 floats each |
| `+0xb8` / `+0xc0` | y-axis split hints |
| `+0xd0` / `+0xd8` | z-axis split hints |
| `+0xe8` | the structure the seed and the ray gather both query |
| `+0xf4` / `+0xf8` | a mode flag and its threshold, which pick the cluster path |
| `+0x118` | per-region working array |
| `+0x120` | per-region status byte, the outside pass's output |

A region in memory is

```
struct Region {            // 16 bytes
    uint32 cluster;
    uint32 node_and_flags; // (nodeIndex << 2) | flags
    uint64 mask;           // 4x4x4 occupancy over the leaf
};
```

and the compiled file packs the same thing into 8 bytes as
`cluster(15) | intersectsGeometry(1) | leafIndex(24) | maskIndex(24)`. The
`nodeIndex << 2` in memory IS the file's 24-bit leaf index, seen from the writing
side. That correspondence is why the in-memory reading can be trusted against
shipped files at all.

**Flag bit 0 means outside**, set by the outside pass, stopped on by the ray
march, and excluded from the region count. Bit 1 is set when the region is
created and also excludes it. The count the compile prints is the regions with
neither.

---

## 3. The pipeline

`180048690` is the entry, and it logs `Visibility complete in %.2fs`. Below it the
stages run in the order the log prints them, which is NOT the order the source
strings sit in the binary:

```
180049e00   Convert RTE with %d triangles
180031f00   the voxel stage driver
  18002e310   Voxelize (%.f units) took ... (%s nodes)
  1800321f0   Outside detection took ...
    18003f1e0   the InitialRegionStatus job body
      18004a2f0   the SEED: inside / outside / undecided for one region
    18002e050   classify one undecided region
      18002deb0   march one ray through the voxel octree
  180032670   region compaction
  180032b80   collect the regions with flags 0 and 1 clear  <- the printed count
180034ea0   CVoxelSampler3::MergeInsideRegions
  18003ddb0   the cluster generation job body
    180032d80   generate one region's clusters
      18002beb0   candidate boxes, split against the hint lists
        18002bcd0   split against one hint, recurse
      1800337a0   merge the per-voxel clusters down by cost
  18002f5c0   Distance merged regions / Pre-merged nodes
  180034220   MergeClusterSet, the expensive pass
18003c010   Collapsing resolution / Reduced node count
180048120   Wrote vis resource %s bytes
```

The two merge stages under `180034220` are 400 of Mako's 1,088 seconds. The ray
casting, which everyone assumes is the cost, is not.

---

## 4. The design decisions we had to adopt

These are the places where the obvious implementation is wrong, and where reading
the binary was the only way to find out.

### The octree stops four voxels early

A leaf is not a base voxel. It is **four base voxels a side**, and the last two
levels of resolution live in the region's 64-bit mask instead of in nodes.
`18010be50` states it from Valve's side: the sub cell is
`(box.maxX - box.minX) * 0.25`, and bit `i` is the cell at `x = i & 3`,
`y = (i >> 2) & 3`, `z = (i >> 4) & 3`.

Taking the leaf as the base voxel gives **1.2 million nodes** against a target of
81,625. It is the single biggest thing to get right in stage 2.

### The root cube is per map

32,768 units on ze_hold_em_p over a 4,096 voxel side, 4,096 units on cardtest over
512. The compiled file states it, so it is read and never assumed; reading it as a
constant costs three levels of depth on a small map.

### Outside detection is a ray vote, and insideness is EARNED

This is the one that cost the most. The natural reading of "outside detection" is
a flood fill from the world box, and it returns nothing, because **a ray trace
scene is under no obligation to be closed**. ze_hold_em_p's has a wall along one
side and end caps, and no ceiling and no far wall: voxelize it onto a dense grid,
pad it, flood from the padding, and all 6,875,040 open voxels are reached.

Valve instead classifies each region on its own and lets the answer spread along
rays. `18002deb0` returns outside only when a ray reaches a region whose flag is
ALREADY set, never when it leaves the world, and the vote in `18002e050` makes a
region inside only when enough rays came back having touched a region already
known inside. **Outside is the default; inside is earned.**

Implemented with the polarity the other way round, every region on an unsealed map
comes out outside. That is the same -100% the flood fill produced, which is not a
coincidence: they are the same assumption.

### Clusters are born one per voxel and then merged by cost

`180032d80` walks a region's 64 mask voxels and appends **one cluster record per
voxel**, each holding a list of `(mask, leafNode)` pairs so a cluster can later
span leaves. Then `1800337a0` merges them greedily: it builds a `CBoxMerge` over
the boxes, and merges the cheapest pair repeatedly until the count reaches its
limit of 32 **or** the best remaining merge costs more than a threshold.

That merge is where the 8.85 clusters per region on ze_hold_em_p comes from, out
of a mean of 55.8 voxels. Every attempt to find a closed-form rule for that number
failed, and it failed because there is no rule: it is the output of a greedy cost
loop.

### Hints are authored by the mapper

The three splitter lists that drive cluster subdivision are filled by `18002b500`,
and it fills them by reading ENTITIES: `origin`, `box_mins`, `box_maxs` and
`hintType`, with type 4, 5 and 6 being the x, y and z axis splits and anything
else a voxel hint.

So **visibility clustering is steerable from the map**, and a compiler that
ignores hint entities will cluster a hinted map differently from Valve's even when
everything else agrees. It also means the entity walk and the visibility builder
are not independent subsystems, which they look like right up until this point.

---

## 5. What it cost to get each number

| stage | ours | compile | off by |
|---|---|---|---|
| voxelize, ze_hold_em_p | 81,281 nodes | 81,625 | **-0.42%** |
| voxelize, shipped branches reproduced | 1,175 of 1,179 | | **99.66%** |
| outside detection, ze_hold_em_p | 10,281 enclosed | 10,554 | **-2.59%** |
| cluster generation | not implemented | 93,354 | |

The two probe maps sit 41% and 28% wide of stage 2 and stage 3, and that is not
the stages: **13 of cardtest's 92 traced triangles and 12 of probe01's 80 rebuild
outside the box the file states for itself**, against 2 of ze_hold_em_p's 4,536.
The `.rte` decode is what is wrong on those maps, and it is recorded as open in
`RTE.md`.

Two corrections to that decode came out of this work, and both were found by
voxelizing rather than by reading the format, because a voxelizer is a far harsher
consumer than a bounding box check:

- a zero dominant normal component is IMPLICIT, not degenerate, which Mako has
  10,168 of, 3.6% of the file
- a reconstruction outside the file's own box is a decode failure: one cardtest
  record with an edge determinant of 2e-6 lands 27 billion units away and smeared
  occupancy across the whole root cube

---

## 6. The method lessons

**A rule that fits one map is not a rule.** Every wrong answer here fit the data
in front of it. The flood fill was obviously right until a second map had no
ceiling. The column rule for cluster counts fits cardtest and probe01 to within
2.5% and misses ze_hold_em_p by 54%, and adopting it would have looked like
progress. The `.rte` normal mapping was settled against two maps that are both
dominated by axis-aligned geometry and is measurably wrong on a third.

**Make the knobs prove they are not carrying the answer.** The ray reach in
`VisOutside` is ours, not Valve's, so `VisOutsideTests` asserts that changing it by
a factor of 32 moves the result by under 2%. It moves it by 0.14%. Without that
test the -2.59% would be indistinguishable from a fit.

**Prefer a structural check to a tolerance.** A node count can be hit by accident.
"Every branch of the shipped octree is also a branch of ours" cannot, because the
shipped tree is the same tree collapsed and a collapse only removes nodes.

**Read the binary before inferring from counts.** Three candidate rules for the
region count were measured and rejected at -29%, -100% and +323% before anyone
opened `visbuilder.dll`, and the answer was a two-line read once the right
function was in front of us.
