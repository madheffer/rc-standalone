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

Not ported: simplify algorithms 1 and 2 and the region SVM. Algorithm 0, the
one both the compile and the map builder ask for, is below.

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
   a vertex's handle is simply that number. Corners are joined by position, not
   by .vmap vertex: two .vmap vertices at exactly the same point are one vertex
   (measured: the physics input Valve hulls, captured from a compile).
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

`HullFromVmap` builds every brush entity's hulls from the `.vmap` and
compares them with the same map's compile. It checks positions, order, floats
and topology. It depends on two more findings.

- **The triangulator.** The mesh the map builder hulls from is already
  triangulated, so vertices are numbered in the order the triangles' corners
  meet them. A quad keeps the (0,2) diagonal unless that diagonal is the worse
  fit or more than 1.01 times the other one squared. Then it becomes (0,1,3)
  (1,2,3) and meets its corners 0,1,3,2. Larger faces go through an ear
  clipper: best ear by 1/area + 2(1 - largest corner cosine), strict, first
  wins. `Physics/FaceTriangulator.cs` ports both.
- **World space first, through CTransforms.** A vertex goes to world space
  through the mesh's matrix, then into the entity's space through the entity's
  inverse. Both come from CTransforms (`Maps/CTransform.cs`): angles to a
  quaternion, the entity's inverted (conjugated, renormalised), each turned
  into a matrix. They differ from angle matrices in the last bits, which on
  Mako moved 17 rotated pieces by up to 0.001. Checked against 2782 matrices
  and 174,330 transformed coordinates captured from a compile, all exact. The
  world-space rounding is visible too: a .vmap y of -31.999998 under a mesh at
  y = 448 comes out as exactly -32.

| map | exact | order only | near miss | other |
|---|---|---|---|---|
| ze_ffvii_mako_reactor_v6_p (rotated entities included) | 1235 | 2 | 1 | 3 hull counts |
| ze_hold_em_p | 163 | 0 | 0 | 6 doors: a toolsclip mesh ships no hull (the .vmap is older than the compile) |
| atixref (recompiled 2026-09-24) | 277 | 0 | 0 | |

A near miss is a vertex off by a rounding step or a sharpen-sized nudge.
Every hull that goes through the simplifier matches exactly: 3 on Mako and 6
on atixref.

### What is left

- **Face fan order around a vertex.** It feeds the neighbour set, and
  triangle index order stands in. Reversing it changes nothing on Mako,
  because it only matters when two faces share a home slot.
- **Triangle and face order.** 7 of 1024 Mako pieces have Valve's points in
  another order and 3 more other triangles: two faces or two triangles of a
  face swapped, or an n-gon cut differently. That is the per-corner mesh the
  map builder builds from the .vmap (FUN_1812d66f0) and the half-edge mesh it
  rebuilds from it (FUN_1813319d0), neither ported yet.
- **The weld in the pipeline.** `Physics/MeshWeld.cs` is the map builder's
  1/32 weld, replayed bit for bit on 1404 of 1405 captured Mako welds, but the
  hull path does not yet rebuild the per-corner mesh (texcoords, normals) it
  runs on. On Mako it moves the physics input of one mesh only.
- **Hull order across pieces.** It differs from mesh order, so the test
  matches hulls by their vertex sets.
- **The region SVM.**

## The weld, and measuring the physics input

The map builder copies each per-material piece of a map mesh into a triangle
mesh with one vertex per corner (position, texcoords, normal, vertex paint)
and welds it (`Physics/MeshWeld.cs`):

- Every float has a tolerance: 1/32 by default, 1/2048 for texcoords, exact
  for lightmap coordinates and integer streams.
- Vertices are visited in order. Each joins the cluster of the first earlier
  vertex, in the order tier0's `CVertexKDTree` box query returns them, whose
  cluster's first vertex is within every tolerance.
- Triangles that lose a corner are dropped.

Because the normals differ, a box's corners stay apart through the weld; the
faces join later, by position.

`tools/hulls/capture_weld.py` runs a compile under Frida and records every
weld (in and out), every physics piece's triangle mesh, and every transform
with its matrix. `WeldReplay` (`WELD=<capture>`) replays the welds, and
`HullFromVmap` with `HULL_PHYS=<capture>` compares each of our pieces with
the one Valve hulled: 1000 of 1024 Mako pieces are exact.

## The simplifier

The limits pass runs Iterations + 1 times. Each time a hull breaks a vertex,
edge or face limit, or two neighbouring faces lie within the merge angle, it
simplifies. If that fails ("could not simplify hull") the hull is kept as it
is. The map builder's 5 degree angle is what sends a brush entity's hull
here. `Physics/HullSimplifier.cs` ports algorithm 0, "Quadric Error Metric",
which has three stages.

1. **Quadric edge collapse.**
   - The hull becomes a triangle mesh: vertices in list order, each face
     fanned from its first half-edge.
   - Every edge is a pair. Each vertex carries the sum of its triangles'
     plane quadrics, weighted by half their area.
   - A pair costs the cheaper of its endpoints. The quadric's minimum wins
     instead when it is cheaper still and lies nearer either endpoint than
     the endpoints lie to each other. The kept vertex moves along the edge
     by the minimum's projection.
   - Pairs come off a min-heap. One is skipped when its ends share three or
     more neighbours or when the move would flip a triangle.
   - Collapsing continues while the error is under the tolerance squared, or
     there are more vertices than the limit, or more than 85 triangles. It
     always stops at 4 vertices or 4 triangles.
   - With the map builder's options, only hulls of more than 85 triangles
     lose anything. For the others this stage only renumbers the vertices
     (first use, triangles taken from their lowest corner), and that order
     is what gets hulled again (tolerance 1e-6, relative).
2. **`CDualHullAgglomerator`.**
   - Each face of that hull is a leaf cluster: its plane relative to the
     vertex centroid, times its area.
   - Neighbouring clusters merge bottom up until one is left. Each merge
     takes the cheapest pair, and a pair costs
     log(sum over both sides of area x tangent to the merged normal x
     sharpness), plus 90 when either side turns past the angle.
   - Sharpness starts at 1. It grows when a cluster borders one whose normal
     is more than 90 degrees away.
   - Cutting the tree: first every merge whose two children are within the
     angle (loosened by their sharpness) is taken, in tree order, until 4
     clusters are left. Then, if there are still more planes than the face
     limit, plain tree order is used.
3. **The hull from those planes.**
   - Each plane's dual point n / d is hulled at tolerance 0, with a tolerance
     scale of 1 rather than the constructor's 50.
   - Each dual face gives a vertex, and those vertices are hulled.
   - If that breaks a limit, the plane budget drops by a sixteenth and the
     planes are cut again.

The agglomerator's dual mesh (clusters as vertices, the rings of faces round
each hull vertex as faces) lives in Valve's generic half-edge library. Only
its adjacency reaches the result, through sharpness. The port keeps it as
rings of clusters and refuses a merge the way the library's edge collapse
does: when a shared neighbour is not the tip of a triangle on the common
edge.

Ghidra's printer drops parentheses in chains of `*` and `+`, so `a * b * c`
in the decompile can be `a * (b * c)` in the binary. The simplifier's
arithmetic was checked against the disassembly (`tools/re/symsse.py` traces the
real association). The same goes for `minss` and `maxss`, whose NaN
behaviour the decompile does not show.

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
