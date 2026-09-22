# The `.rte` ray trace environment

This is the file visibility consumes. `CVisBuilder::Build` writes it to
`%TEMP%\csgo_addons\<addon>\maps\<map>.rte` during the `-world` phase and the vis
entry reads it back:

```
Loading kd-trees
Successfully read C:\...\Temp\csgo_addons\s2c_big\maps\ze_ffvii_mako_reactor_v6_p.rte
Successfully unserialized ray tracing environment.
Convert RTE with 28728 triangles in 0.05s
```

**Why it matters more than its size suggests:** it survives the compile, and it is
the geometry input to visibility. Decoding it means a visibility builder can be
written and scored against real maps *before* a geometry pipeline exists, which
decouples the two largest remaining pieces of `PLAN.md`.

Two specimens, both produced by Valve's compiler here:

| map | bytes |
|---|---|
| ze_hold_em_p | 348,176 |
| ze_ffvii_mako_reactor_v6_p | 23,259,680 |

## What is established

### The header

```
u32  0   6            version (both files)
u32  1   3            (both files)
u32  2   0
u32  3   A            1,865      / 150,015
u32  4   B            4,548      / 279,064
u32  5   C            5,983      / 770,787
u32  6   B            repeated
u32  7   0
u32  8   B            repeated
vec3 0x24  min        (-13707, -608, 17)   / (-8192, -9088, -15360)
vec3 0x30  max        (7672, 398, 153)     / (7168, 2492.1, 512)
u32  15  small        4          / 6
```

The two `vec3` are the world bounding box; both match the map they came from. `B`
appears three times, which is the shape of a count used by several parallel
arrays.

### The trailing array is per-triangle, and it is reflectivity

The last `B * 12` bytes of the file are a `Vector3` array, and **every entry is
`(1, 1, 1)`** in both specimens. That is not geometry; it is the per-surface
albedo or reflectivity a bounce tracer needs, at its default of white. It lands
exactly on the end of the file in both files, which is what pins `B` as the
triangle count and 12 as its stride.

### The whole layout (SOLVED)

Both specimens tile **exactly**, a 348 KB map and a 23 MB one:

```
offset                size        section
0                     64          header
64                    A * 8       kd nodes: (float split, u32 packed child + axis)
64 + A*8              B * 48      triangles, cache-optimized form
                      (C-1) * 4   leaf triangle indices, u32 into the triangle array
                      B * 8       per-triangle, 8 bytes, purpose unknown
                      B * 12      per-triangle reflectivity, Vector3, all (1,1,1)
```

| section | ze_hold_em_p | mako |
|---|---|---|
| kd nodes | 1,865 | 150,015 |
| triangles | 4,548 | 279,064 |
| indices | 5,982 | 770,786 |
| total | 348,176 = file | 23,259,680 = file |

Every boundary was corroborated independently of the arithmetic: the kd region
opens at `0x40` with split positions inside the world box, the index region is
visibly ascending `u32` runs, the index-to-per-triangle boundary is sharp in a hex
dump (ascending indices, then a new repeating pattern, at exactly
`filesize - B*20`), and the reflectivity tail is uniformly `(1, 1, 1)`.

### The 48-byte triangle record

It holds no vertices. It is the **Badouel projected-plane** form a ray tracer uses
for intersection: the plane, plus two edge equations in the plane's dominant
projection.

```
float  0   normal component on axis u          float  1   normal component on axis v           | see the axis word at 40
float  2   plane distance, same scale as above  |
float  3   triangle id                          |
float  4   edge 0, a                            |
float  5   edge 0, b                            |
float  6   edge 0, c                            |
float  7   edge 1, a                            |
float  8   edge 1, b                            |
float  9   edge 1, c                            |
u32   10   bits 0..7   axis u  (0, 1 or 2)      |
           bits 8..15  axis v  (0, 1 or 2)      |
           bits 16..   flags and surface data   |
float 11   normal component on the dominant axis/
```

**Slot 3 is the triangle id**, and in both specimens it equals the record's own
index, exactly, for every triangle checked.

**Slot 10's two low bytes are the projection axes.** They only ever hold 0, 1 or 2
and always pair as (0,1), (1,2) or (2,0), so the third axis is implied and is the
one the plane is most perpendicular to. Everything above bit 16 varies per
surface and is undecoded; `0x0920` and `0x0800` are common.

**Slots 0, 1 and 11 are the normal under a FIXED mapping, `(slot11, slot0, slot1)`
as x, y, z.** It does not follow slot 10, which only names the two projection
axes. This is the one place the format invites a wrong answer: reading the normal
in coord_select order scores 100% on ze_hold_em_p, whose geometry is almost all
axis aligned, and quietly corrupts Mako. The mapping was settled by correlating
which slot holds the largest component against the third axis, which pairs only
ever as (slot1, axis2), (slot0, axis1) and (slot11, axis0).

Slot 2 is the distance in the same arbitrary scale as the normal, so normalise
the two together. The Badouel convention of scaling so the dominant component is
exactly 1 holds for 99.96% of ze_hold_em_p's triangles and only 61% of Mako's, so
divide rather than assume.

How this was settled, since a plausible field order is not evidence:

- placing the components by the axis word rather than in slot order raises the
  plane test on Mako from 86% to **99.9%**, which a wrong mapping would not do
- with the normal normalised, **99.2% of ze_hold_em_p's and 99.1% of Mako's
  planes cut the file's own stated world bounding box**
- the first records decode as ordinary map geometry: an axis-aligned
  `n=(0,-1,0) d=1504`, a 45 degree `n=(0,-0.707,-0.707)`, and a back-to-back pair
  sharing a distance, which is what a quad split into two triangles looks like

`tools/re/read_rte.py` implements this as `plane()`, `triangle_id()`,
`coord_select()` and `flags()`.

**The index array is `C - 1`, not `C`.** That off-by-one is consistent across both
files to the byte, so one entry is a root or sentinel rather than a leaf index.

The layout also passes a falsifier it had no reason to: read at this offset and
stride, **every one of ze_hold_em_p's 5,982 leaf indices is below the triangle
count**, none out of range. A section placed a few bytes wrong, or strided wrong,
would produce values scattered far past 4,548. `tools/re/read_rte.py` performs
that check.

### Section boundaries seen before the layout was solved

Classifying each 4-byte word as coordinate-like float, small integer or neither,
and run-length encoding the result, ze_hold_em_p splits cleanly:

| range | bytes | content |
|---|---|---|
| `0x40` .. `0x38fc0` | 232,832 | alternating float and u32 |
| `0x38fc0` .. `0x3ecc0` | 23,808 | small integers |
| `0x3eedc` .. `0x47ae0` | 35,844 | large u32 |
| `0x47ce0` .. EOF | 54,576 | the `B * 12` reflectivity array |

The first region is `(float, u32)` pairs, which matches `Loading kd-trees`: a
split position and a packed child-plus-axis word is the standard kd-node layout.

### Vertices come back out

Slots 4 to 6 and 7 to 9 are two barycentric edge equations over the plane's 2D
projection, so a vertex is where the barycentric pair reaches (0,0), (1,0) or
(0,1): a 2x2 solve for the two projected coordinates, then the third recovered
from the plane. `Rte.vertices()` does it.

**The check that settles the whole format.** Rebuild every triangle in a file and
take the bounding box of the result. It should be the box the file states in its
own header, and it is:

| | header says | rebuilt geometry |
|---|---|---|
| ze_hold_em_p | `(-13707.0, -608.0, 17.0) .. (7672.0, 398.0, 153.0)` | **identical** |
| Mako | `(-8192.0, -9088.0, -15360.0) .. (7168.0, 2492.1, 512.0)` | `(-8192.0, -9089.8, -15360.0) .. (7169.4, 2492.1, 512.0)` |

99.96% of ze_hold_em_p's triangles and 97.0% of Mako's land fully inside the
stated box, and the extremes reproduce it. Recovering vertices from an encoding
that discards them, and landing on the file's own bounds, is not something a
wrong decode does.

The output reads as ordinary brush geometry. ze_hold_em_p's first two triangles
are `(7672, -56, 64) (7672, -56, 17) (7160, -56, 64)` and
`(7672, -56, 17) (7160, -56, 17) (7160, -56, 64)`: one quad, a wall at y = -56
spanning x 7160 to 7672 and z 17 to 64, on whole-unit coordinates.

## Two corrections found by voxelizing

Both surfaced in `VisVoxelizer`, which consumes the decode far more harshly than
the bounding box check that settled it.

**A zero dominant component is implicit, not degenerate.** The Badouel form scales
the normal so its dominant component is 1, and some records leave that component
at zero. Treating those as undecodable throws away real geometry: ze_hold_em_p has
two, both walls at its own x extremes, and **Mako has 10,168 of them, 3.6% of the
file**. Read a zero dominant component as 1.

**The fixed slot mapping does not survive every map.** It was settled against
ze_hold_em_p and Mako, which are dominated by axis-aligned geometry. On the two
probe maps it is measurably wrong: **13 of cardtest's 92 traced triangles and 12 of
probe01's 80 rebuild OUTSIDE the box the file states for itself**, against 2 of
ze_hold_em_p's 4,536. One of them has an edge determinant of 2e-6 and lands 27
billion units away. `RayTraceEnvironment` rejects a reconstruction outside the
stated box for that reason, which is a guard and not a fix; the mapping itself is
still wrong for those records and that is open.

**The excluded-triangle flag is settled.** A triangle whose flag word is `0x0800`
is not converted into the trace scene, and that reproduces the compile's own count
on three maps: 4548 - 12 = 4536, 300 - 208 = 92, 288 - 208 = 80.

## What is NOT established

**The 8-byte per-triangle array.** Long constant runs, so probably a surface or
material id plus flags. On Mako only 320 of 279,064 entries match the first, so it
is not a single constant.

**Why the log says 28,728 triangles when the header says 279,064.** The literal
28,728 does occur in Mako's file exactly once, but as a VALUE inside the ascending
index array, not as a count, so it is coincidence. The header count is the array
length and it is confirmed by the file tiling exactly. The log's number is
therefore a filtered subset, presumably the opaque or vis-relevant triangles that
`Convert RTE` keeps. Not confirmed.

## Next

1. Decode the 48-byte triangle record, ideally by finding Valve's
   `CacheOptimizedTriangle` layout in the raytrace library, and validate by
   reconstructing vertices and checking them against the header's bounding box.
2. Identify the 8-byte per-triangle field.
3. Then a reader, scored by rebuilding a triangle soup whose bounds match the
   header's stated box.
