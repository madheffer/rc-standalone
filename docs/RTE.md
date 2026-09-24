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

## The layout, read out of Valve's own reader

`CVisibilityMesh::LoadRTEFromFile` is `180049e00` in visbuilder.dll (the source
path is in the binary: `utils/visbuilder/voxel_utils.cpp`), and `18011a670` is the
unserializer under it. Reading those settled the format outright, and corrected
two things that had been inferred from arithmetic and were both wrong.

```
offset                size        section
0                     60          header: nine u32 counts, then the world box
60                    A * 8       kd nodes: (float split, u32 packed child + axis)
60 + A*8              B * 48      triangles
                      C * 4       leaf triangle indices, u32 into the triangle array
                      B * 8       per-triangle, 8 bytes, purpose unknown
                      B * 12      per-triangle reflectivity, Vector3, all (1,1,1)
```

**The header is 60 bytes, not 64**, and **the index array is C, not C - 1**. The
two errors cancelled in the file-size arithmetic, which is why the old layout
appeared to tile exactly; it tiled four bytes late, and every triangle was read
one float out of phase. Both layouts sum to the file size on both specimens, so
tiling alone could never have told them apart.

| section | ze_hold_em_p | cardtest | mako |
|---|---|---|---|
| kd nodes A | 1,865 | 183 | 150,015 |
| triangles B | 4,548 | 300 | 279,064 |
| indices C | 5,983 | 807 | 770,787 |
| total | 348,176 = file | 25,152 = file | 23,259,680 = file |

### The 48-byte triangle record

```
float  0   normal x
float  1   normal y
float  2   normal z
float  3   plane distance, as dot(normal, point) == distance
float  4   triangle id, which equals the record's own index in every file checked
float  5   edge 0 a          the two edge equations are over the 2D projection
float  6   edge 0 b          named by the axis bytes below:
float  7   edge 0 c          E(u, v) = a*u + b*v + c
float  8   edge 1 a
float  9   edge 1 b
float 10   edge 1 c
byte  0x2c axis u (0, 1 or 2)
byte  0x2d axis v (0, 1 or 2); the third axis is (v + 1) % 3
u16   0x2e flags
```

The normal is plain xyz in slots 0 to 2 and is ALREADY UNIT LENGTH: 100% of the
traced triangles of all three maps measured, to within 1e-3. Nothing needs
normalising, no component is implicit, and the axis bytes name only the
projection.

### Rebuilding a vertex, which is `180022030` verbatim

```
det  = 1 / (e1.b * e0.a - e0.b * e1.a)
v0   = solve(E0 = 0, E1 = 1)
v1   = solve(E0 = 0, E1 = 0)
v2   = solve(E0 = 1, E1 = 0)
p[u], p[v] from the 2x2 solve, p[w] = 0, then
p[w] -= (dot(normal, p) - distance) / normal[w]
```

and the record is rejected only when a component comes out non-finite. That
function is inlined a second time inside `18004bf20`, the kd box query, which is
an independent corroboration of the whole layout.

### What it scores

| | ids that equal the index | traced | unit normals | rebuilt | box |
|---|---|---|---|---|---|
| ze_hold_em_p | 4,548 / 4,548 | 4,536 | 100% | **4,536** | exact |
| cardtest | 300 / 300 | 92 | 100% | **92** | exact |
| probe01 | 288 / 288 | 80 | 100% | **80** | exact |

Nothing fails, and the rebuilt bounding box is the header's own to the unit on
all three. The previous decode lost 14% of the probe maps' triangles and put one
of cardtest's 27 billion units away; that was the phase error, not the maths.

### The flags, and what nodraw does

```
0x0001  excluded, with 0x0800: LoadRTEFromFile skips these outright
0x0010  nodraw in the file, rewritten to 0x0020 before the triangle is stored
0x0020  nodraw
0x0100  seen on ten of cardtest's triangles, undecoded
0x0800  excluded from the trace scene
0x1000  counts as occupancy only for a voxel box WIDER than 256 units
```

**Nodraw seals and does not draw, and the compiler treats it exactly that way.**
A nodraw triangle is converted, traced, and voxelized like any other; the flag
only feeds a statistic. `LoadRTEFromFile` sums the area of every converted
triangle and of the nodraw ones, and if nodraw is over 80% of the total it logs
`Vis geometry appears to be mostly nodraw (%.2f%%), reconfiguring...` and clears
the mark off every triangle. So the flag can change the map's own accounting and
never removes a surface from visibility.

`0x1000` is the one flag that changes geometry, and it is size dependent.
`18002e310` queries the kd tree with mask `0x811` while a box is wider than 256
units and `0x1811` at or below it, and the mask is "reject a triangle with any of
these bits". The other three bits are inert by then, so the whole of the rule is:
**below 256 units a `0x1000` triangle stops being occupancy**. ze_hold_em_p has
none, and the two probe maps have eight each, which was the entire difference
between a voxelize that was exact and one that was 43% high.

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

**The index array was read as `C - 1` before the header was known to be 60 bytes.**
It is `C`; the off-by-one was the header, not a sentinel.

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

## What the old decode got wrong, and why it looked right

The layout above replaces one that had been inferred from file arithmetic, and
the way it survived is worth recording because the same trap is everywhere in
this work.

The old reading put the header at 64 bytes and the index array at `C - 1`. Those
two errors cancel: `64 + A*8 + B*48 + (C-1)*4 + B*20` and
`60 + A*8 + B*48 + C*4 + B*20` are the same number. So the check that was
supposed to settle the layout, "both specimens tile exactly", passed for both,
and could never have separated them.

Every triangle was therefore read one float out of phase. That shifted the normal
out of slots 0 to 2, which is why the normal appeared to need a "fixed slot
mapping" of `(slot11, slot0, slot1)`, why 3.6% of Mako's records appeared to have
an implicit dominant component, why 14% of the probe maps' triangles rebuilt
outside the file's own box, and why one cardtest record with an edge determinant
of 2e-6 landed 27 billion units away. None of those were properties of the
format. All of them are gone.

The guard that rejected a reconstruction outside the stated box is gone with
them. It was compensating for the phase error, and with the phase right nothing
needs rejecting: Valve's own reader rejects only a non-finite component, and on
these three maps it never fires.

**The lesson is the one the analysis doc already states and this is the sharpest
case of it: a check that a wrong answer also passes is not a check.** The thing
that actually settled the format was reading Valve's reader.

## Where the map's triangles come from

`MapGeometry.RteTriangles` rebuilds the map-mesh part of the file, and
`MapGeometryReplay` finds each triangle's 48-byte record bit for bit in the
`.rte` of the same compile. probe01 (288) and cardtest (300) are exact both
ways; on atixref 20,610 of our 20,958 are found and the file's other 12,244
are props, which we do not produce yet.

**Which meshes.** A mesh under the world or a group counts. One under an
entity counts only when the class renders as world (FGD metadata
`render_as_world_but_physics_as_entity`, only `func_water` in CS2). A mesh
whose shadow mode is "none" (`disableShadows` 3) gets the flag the collector
drops. A face whose material has `mapbuilder.nodraw` or `mapbuilder.occluder`,
and none of `visblocker`, `acceptvis` or `sky`, is dropped; the attributes come
from the compiled `vmat_c` (tools materials live in `core`).

**Where they go.** A node's world matrix is the instance path times its own
`AngleMatrix(angles, origin)`, both in the binary's float order
(ConcatTransforms sums z, y, x, then the translation). A point goes through as
each row dotted with (x, y, z, 1) the way a SIMD horizontal add pairs it,
`(x + z) + (y + t)`: a roll of -89.999985 degrees (mesh 6819 on atixref) and
instanced copies of a yaw-180.5 mesh tell every other order apart. Faces are
triangulated in world space.

**Instances.** A group that a `CMapInstance` targets is not compiled where it
stands. Each instance compiles the group's contents again, and each instance on
the path contributes the instance's matrix times the inverse of its target's,
outermost first. Unrotated instances are exact.

**Still open.**

- 240 atixref triangles, all in copies of one mesh (a yaw-180.5 light) under
  instances rotated 90, 180.00002 and 270 degrees, are off by one ulp in y at
  some corners. No summation order, matrix built another way (angles round trip,
  quaternion, double) or perturbation of the six x/y matrix entries by up to two
  ulps rebuilds them all. A capture of the compile's matrices for one of these
  copies should settle it (not taken: CS2 was running).
- Faces with subdivision levels (`subdivisionData`) are tessellated on
  ze_hold_em_p: 35 painted-blend slab faces at level 3 become 8x8 grids (128
  triangles each, 4,482 in all). On atixref, 21 meshes carry subdivision levels
  and still arrive as plain faces. The map compile's `ExpandSubdivision` and
  meshutils' subdivision remapping decide which and how; not yet ported.
- About 110 scattered atixref triangles in ordinary unrotated world meshes
  (1109, 861 and others) are not found. Coverage by coplanar faces of other
  meshes does not predict it.

## What is NOT established

**The 8-byte per-triangle array is an id, and it groups by surface.** It takes
three distinct values across the whole of ze_hold_em_p (4,482 triangles share the
first) and five across cardtest, and on cardtest every distinct FLAG word maps to
exactly one of them: `0x0800` to one id, `0x0120` to another, `0x1120` to a third.
So the flags are a property of whatever this id names, which is what a material or
surface would be. It is not a pure function of the flags, though: on ze_hold_em_p
one id carries both the twelve `0x0800` triangles and thirty-eight with no flags
at all. What it actually hashes is still open, and visibility does not read it.

**Why the log says 28,728 triangles when the header says 279,064: settled.**
It is the number the compile's tracer holds. Dumped from a live Mako compile
(2026-09-24), the rebuilt tracer has 28,728 slots, and `TracerOrder` (the
triangles not flagged `0x0801` whose loader corners and conversion are finite)
also comes to 28,728; every slot's 48 byte record matches ours bit for bit.
The other quarter of a million are excluded from tracing by their flags.

**The file is not byte-stable between compiles.** Three probe01 compiles on
2026-09-24 (two with the same binary) gave three different `.rte` files: the
same header, planes and surface ids, but a different triangle order (so the id
field and the kd tree differ) and, in each pair, one triangle whose edge floats
differ. The vis captures from all three were byte identical, so visibility does
not see the difference. Why the order varies is open; compare `.rte` files by
content, never by hash. A replay must still read the `.rte` of the compile it
was captured from: slot order follows file order, and ties between triangles
met at the same distance go to the one the kd walk tests first.

## Next

1. The rotated-instance residual, from a capture of the matrices.
2. Subdivision: when a face is tessellated, and the exact grid and diagonals.
3. Props: the collector's second path over the map's models, baked into world
   space by the world builder.
4. Identify the 8-byte per-triangle field.
