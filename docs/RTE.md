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

**Slots 0, 1 and 11 are the normal, in the axis order slot 10 gives**, and slot 2
is the distance in the SAME arbitrary scale. Normalise the two together to get a
unit plane. The scale is usually chosen so the dominant component is exactly 1,
which is the Badouel convention, but not always: that holds for 99.96% of
ze_hold_em_p's triangles and only 61% of Mako's, so divide rather than assume.

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

## What is NOT established

**The two edge equations, slots 4 to 9.** Grouped as (a, b, c) triples by their
magnitudes, two small coefficients and one large, which is the shape of a 2D line
equation in the projected plane. Not verified, because verifying them needs the
vertices they were derived from and those are exactly what the format discards.

**Reconstructing vertices.** The Badouel form is lossy in the sense that vertices
come back only by intersecting the plane with the two edge equations and a third
implied one. Worth doing, because a triangle soup whose bounds match the header's
box would close this out completely.

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
