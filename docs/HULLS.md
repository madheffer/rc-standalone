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
  After that the hull is scaled back by k and moved back to c.
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

## The quickhull build

vphysics2 carries Gregorius-style quickhull. `Physics/QuickHull.cs` ports it
operation for operation:

1. **Centre.** Subtract the points' mean (a plain running sum divided by n).
2. **Weld.** A later point within the tolerance of an earlier one on all three
   axes (strict) is replaced by the last point. The tolerance is relative to the
   AABB extents when the options say so, and the compile's options do.
3. **Tolerance.** From the welded points' AABB:
   - m is the largest |coordinate| on any axis, and the axes' largest |values|
     are also summed;
   - v = min(sum, m·√3)·3·1.01 + m, floored at 1;
   - tol[0] = v·50·FLT_EPSILON, tol[1] = 4·tol[0], tol[2] = 2·tol[1].
4. **Simplex.** The axis of largest extent gives the first two points. The third
   is the point farthest from their line, the fourth the point farthest from
   that plane, each by a strict "better than 100·tol[0]". Four faces are built.
   Every other point goes to the face it is farthest above, if that beats
   tol[2].
5. **Grow.** Repeatedly take the conflict point farthest above its face (over
   tol[2]):
   - a depth-first horizon, where a neighbour is visible when the point is
     more than tol[1] above it;
   - a fan of new faces;
   - three merge passes: faces whose plane has the interior point in front, then
     non-convex edges, larger face first, then flagged faces;
   - orphan points re-filed or dropped;
   - dead faces freed.
6. **Finish.** Mark the vertices still on faces, drop the rest, then add the
   mean back to the vertices and plane offsets.

Two details make the order Valve's rather than any correct hull's:

- **List insertion.** A node goes in just before the first node ever inserted
  into that list, which is not either end.
- **Pool order.** Vertices, half-edges and faces come from fixed pools with
  index free lists. The later sharpen pass visits each edge pair once by
  comparing the two half-edges' addresses, so pool order changes where
  vertices end up.

## What is ported, and what matches

`Physics/RnHullBuilder.cs` covers the rest:

- `RnHullCreate`, with the box shortcut and `RnHullCreateBox`;
- normalisation, the limit and sharpen passes, and the inner-margin check;
- the conversion and the mass, area and centroid-radius passes.

Not ported: the simplifiers (the compile's options only reach them past 256;
the map builder's 5 degree angle reaches them for nearly coplanar neighbours)
and the region SVM.

`HullReplay` feeds every shipped brush-entity hull's own vertices back in:

| map | box hulls | other hulls |
|---|---|---|
| ze_hold_em_p | 156 of 156 bit-exact | 1 differs by ulps and in order |
| ze_ffvii_mako_reactor_v6_p | 606 of 611 bit-exact | 4 fully exact; 245 match in every float and differ only in order; 490 differ by ulps and in order |

A general hull fed its own output keeps its floats but not its order, and the
order changes the sums by an ulp.

## What the map builder asks for

A map mesh carries `physicsType` (`HammerMeshPhysicsType_t`): none, default,
convex_single, convex_multi, mesh. The map builder resolves "default" by where
the mesh sits:

| where | resolves to |
|---|---|
| world geometry | mesh |
| entity with `PhysicsTypeOverride_Mesh` | mesh |
| entity with `PhysicsTypeOverride_SingleConvex` | convex_single |
| any other entity | convex_multi |

`base.fgd` defines the three override base classes. In the stock FGDs only
`func_shatterglass` uses one (Mesh). The editor describes "default" as "single
convex if part of an entity", but the compile resolves it to convex_multi.

## From a brush mesh to its shipped hulls

`Physics/BrushHulls.cs` follows the whole path.

1. **Pieces.** Each material of a mesh is its own piece. Clip materials make
   hulls too.
2. **Triangle mesh.** A piece's faces are triangulated into a fresh mesh. Its
   vertices are numbered in first-appearance order over the faces' corners, and
   a vertex's handle is simply that number.
3. **Groups.** convex_single hulls every vertex in that order. convex_multi
   first splits the triangles into groups that touch at a vertex:
   - every triangle goes into a hash set;
   - each group grows breadth first from the first occupied slot;
   - neighbours come out of another set, in slot order.
4. **Group points.** A group's vertices come out of a third set in slot order.
   The set is murmur3's fmix32 on the handle with chained open addressing, and
   `Physics/ValveHashSet.cs` ports it. Its size is the next power of two at or
   above faces x 32 / 3, and at least 32.
5. **The map builder's hull.** The same builder `RnHullCreate` uses, with the
   angle at 5 degrees and a minimum thickness of 1. A flat set, such as a
   single face, is pushed out by 1 along +x, +y and +z and hulled again.
6. **The model compile's node check.** The node's points are hulled raw
   (tolerance 0, with no normalisation or sharpening) and the shape keeps that
   hull's vertex list.
7. **`RnHullCreate`** on those points.
8. **The shape's transform.** The shape's matrix is applied (resourcecompiler).
   For a map builder hull that is the identity, which still turns every -0
   into +0.

`HullFromVmap` builds every unrotated brush entity's hulls from the `.vmap`
and compares them with the same map's compile. It checks positions, order,
floats and topology.

| map | exact | order only | near miss | other |
|---|---|---|---|---|
| ze_hold_em_p | 163 | 0 | 0 | 6 doors: a toolsclip mesh ships no hull (the .vmap is older than the compile) |
| ze_ffvii_mako_reactor_v6_p | 1120 | 31 | 10 | 4 hull counts, 3 need the simplifier, 13 rotated entities skipped |
| atixref | 198 | 1 | 8 | 3 hull counts, 6 need the simplifier |

A near miss is a sharpen-sized nudge (0.01 or less) that follows from a
different order.

### What is left

- **The ear clipper.** It triangulates faces with four or more corners; a fan
  stands in. The triangulation only changes a hash insertion order, which
  matters when two keys share a home slot, and that happens more often in
  bigger hulls.
- **Face fan order around a vertex.** It feeds the neighbour set; a triangle
  index order stands in.
- **The simplifier** that a 5 degree angle between neighbouring faces asks
  for.
- **Entity-local points for rotated entities and meshes.** Valve's
  mesh-to-entity matrix is not built here yet.
- **Hull order across pieces.** It differs from mesh order, so the test
  matches hulls by their vertex sets.
- **The region SVM.**

## Where the hull points come from

The true input is a vertex list, not the mesh. physicsbuilder builds a hull
shape by hulling the mesh points with the same quickhull. The options come from
the `CModelDocPhysicsHullFile` node:

- face-merge angle, clamped to 5..80;
- max hull vertices, 4..256;
- import mode;
- optimization algorithm (3 turns the angle off).

The hull's vertex list, in list order, becomes `hull_vertices`, which
`RnHullCreate` hulls again. Next: the node and attributes the map builder
gives a brush entity, then that first stage, then the SVM.
