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

### Section boundaries

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

**The middle arrays.** A search over every subset of the header counts crossed
with plausible strides found **no assignment that tiles both files exactly** after
a 64-byte header. So either the header is longer than 64 bytes, there are counts
not in the first nine words, or a section is variable length. Do not guess a
layout here; the earlier `lightmap_packing_geometry.dat` attempt published a
field order that ran off the end of all three specimens.

**Why the triangle count disagrees with the log.** Mako's header carries
`B = 279,064` while the compile prints `Convert RTE with 28728 triangles`. Those
differ by about 9.7x. Either the log counts a filtered subset that visibility
actually traces against, or `B` counts something other than triangles and the
`(1,1,1)` array is per-something-else. **Unresolved, and it matters**: it decides
what the arrays are indexed by.

## Next

1. Settle `B` against the log's 28,728 by finding a section whose element count
   is 28,728 rather than 279,064.
2. Widen the header search past 64 bytes, and look for counts stored after the
   kd-tree rather than in the header.
3. Validate any candidate layout by reconstructing a triangle soup and checking
   its bounds against the header's stated box, which is a cheap falsifier.
