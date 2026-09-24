# Convex hulls: where Valve cooks them

Every brush entity's physics (a trigger's single hull, a breakable's hulls) ships
as `RnHull_t` inside the entity model's `vphys` block. This is where those bytes
are made, read out of the 2026-09-24 binaries. Addresses live in the tools, not
here.

## Who does it

Not `physicsbuilder.dll`. It has a quickhull and uses it on the ModelDoc side
(the 256 vertex, edge and face validation, the simplification modes and the
preview mesh), but the hull that ships is cooked in `vphysics2.dll`, which
exports it as `RnHullCreate`. `CRegionSVM` appears only in `resourcecompiler.dll`
and `vphysics2.dll`.

`resourcecompiler.dll` converts each physics shape. For a hull shape it:

1. Copies the shape's points.
2. If the shape's scale is not 1, moves each point into place before cooking.
   The point is rotated by the shape's quaternion, scaled, then translated.
3. Calls `RnHullCreate(count, points, hull, options)` through
   `VPhysics2_Interface_001`.
4. If the scale was 1, it transforms the cooked hull afterwards instead. The
   matrix is built from the same quaternion and translation.
5. If the hull has no region SVM and the node's `disable_region_svm` is off,
   builds the SVM itself (below).

The compile passes these options: a float 0.0, a float 0.0, then 256, 256 and
256, then 0, a float 0.0, and flags true, true, false. `RnHullCreate` appends a
point scale of 1.0 and 0.01. With no options its first float is 5.0.

## `RnHullCreate`

- **Box shortcut.** With 8 to 36 points, take the AABB. If every point lies
  within 1/32 of a box corner on all three axes, and all eight corners are hit,
  the result is `RnHullCreateBox(half extents, centre)`.
- **Normalise.**
  - The centre is c = (s·min + s·max)·0.5, where s is the point scale.
  - e is the `frexp` exponent of the largest |s·max − c| over the three axes.
  - k = 2^max(e − 6, 0).
  - Each point becomes (s·p − c)·(1/k).
  - The tolerance is divided by k unless the options say not to.
- **Quickhull.** vphysics2 carries its own quickhull (a 0x170 byte object).
  It builds at tolerance max(1.1920929e-5, options tolerance), and the compile
  gets 1.1920929e-5.
- **Fallbacks and limits.** If the hull is invalid, an extrusion retry follows.
  Then a pass enforces the vertex, edge and face limits, and a final check runs.
  After that the hull goes back to c; one step between those is not read yet.
- **Convert to `RnHull_t`** (0xf8 bytes):

| offset | field | how |
|---|---|---|
| 0x00 | centroid | mean of the vertices, sum × (1/n) in float |
| 0x0c | max angular radius | largest \|v − centroid\| |
| 0x10 | min centroid radius | last pass, from the planes |
| 0x14 | bounds | min then max of the vertices |
| 0x2c | orthographic areas | 0.25 each, then the area pass |
| 0x38 | mass properties | mass pass |
| 0x68 | volume | mass pass |
| 0x6c | surface area | area pass |
| 0x70 | vertex positions | 12 bytes each |
| 0x88 | face planes | normal and offset, one per face |
| 0xa8 | region SVM | pointer, filled by the compile |
| 0xb0 | vertices | one byte each: an outgoing half-edge |
| 0xc8 | half-edges | four bytes: next, twin, origin, face |
| 0xe0 | faces | one byte each: a half-edge |

Half-edges are reordered so each twin sits at the next index. Indices are single
bytes, which is where the 256 limits come from.

## The region SVM

`resourcecompiler.dll` builds it from the cooked hull. The builder makes a tree
of 40 byte nodes over the hull's faces, then flattens it:

- A leaf stores its value.
- An inner node packs its child offset and its split into one 32-bit word.
- An empty node warns "Invalid hull tree".

The builder works relative to the hull's centroid. Each plane is copied out and
its offset gets n·centroid added back.

## What it means for the port

Matching a hull byte for byte means porting vphysics2's quickhull, its
extrusion and limit passes, the conversion above and the SVM builder. Every
step is float arithmetic whose order matters. Next: the quickhull build itself,
then the three finishing passes (mass, area, centroid radius).
