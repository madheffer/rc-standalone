# Physics and collision

Everything that ships collision:
- `world_physics.vmdl_c` and its manifest;
- the brush entity models;
- the hulls, meshes and prop shapes inside both.

The settle is in [SETTLE.md](SETTLE.md). Builds, unless a note says otherwise:
resourcecompiler (rc) 20260923, and physicsbuilder (pb) and vphysics2 (vp)
20260924. [ADDRESSES.md](ADDRESSES.md) indexes every address.

## Status

world_physics is whole-file exact on every map with a current compile:
the 34 maps tools/physics/corpus.py compiles on 1.41.9.2 (atixref, cardtest,
cs_script_demo, ze_hold_em_p, c2m2's prefab, the probe maps), Mako, and the
Hammer-option probes below. "Whole-file" means the decoded trees and
container facts; compressed bytes are encoder defined.

Hammer's per-mesh physics options are covered, each on a probe map Valve
compiled (2026-10-23):
- **Physics type** none, default, mesh, convex_single, convex_multi.
- **Physics simplification** (override and error): world pieces, painted
  layers, tessellated meshes, brush entity hulls and meshes, and the
  pre-settle world (s2c_simplify, s2c_simplify_types; ledger 124).
- **Collision property, group, interacts as, with and exclude** (s2c_meshcoll,
  s2c_meshcoll_hold; ledger 125). Detail layers are refused: CS2 defines none.
- **Cables** (s2c_cablecoll, 17 collision cables over every cable setting;
  ledger 126): cable_static's tube in the world, cable_dynamic's model
  physics, and both in the pre-settle world.

Brush entity models:
- **Physics-only models** are whole-file exact: atixref 86, ze_hold_em_p 24,
  Mako 324, cardtest 5.
- **The physics block of models with render meshes** matches too: 58/58,
  8/8 and 237/237.

In game, ze_hold_em_p and atixref with our physics load and collide as
Valve's do.

The settle world under deformers is exact on s2c_deformplace (147 bodies,
140 shapes, step 0 and the settled prop; ledger 130): deformed props become
deformed meshes, cables stay straight, and copies of deformed meshes deform
at their own placement.

CMapDeformerPath (`Maps/PathDeformer.cs`, ledgers 131 to 133) is exact on
s2c_deformpath (four path deformers, plain and turned, with start, end and
maxError set or left at the factory's), s2c_deformpath2 (close start/end
pairs, closed and hidden targets, instance and prefab copies), s2c_deformnest
and s2c_deformnest2 (deformers inside deformers, deformers holding their own
target, in instances) and s2c_deformpath3/4 (two prefabs of one map, a nested
prefab, both with and without the host holding the prefabs' ids): every deformer struct
PropDeformer_Transform is handed, the world physics and the entity models.
The fit itself is bit for bit against resourcecompiler's own functions on
20,000 random paths (`PathDeformerOracleTests`):
- **Target:** targetPath, an embedded CMapNodeReference, names a path
  (a cable is one) by its referenceID; the path's world control points
  (1810b7ca0, mode 1) make the Bezier polygon.
- **Recentre** (18117aeb0, each setter): the node's origin moves to the
  path's point at (end + start) 0.5 of its length, and the nodes below it
  move along (181076850 with the old origin as pivot). Its pivot mode is 1
  (vf10), so the lattice sits half the turned size back from the origin
  (181020fa0) and the section is centred on the curve.
- **Fit** (18128dc90): the polygon resampled at 100 points a segment, framed
  from +z, cut to [start, end] and refitted as 2 x the spanned segments (up to
  16) of Schneider cubics within maxError per cent of the length; the cut
  curve sampled at i / 99 and framed from the cut's normal; each corner of
  the 1 x 1 section fitted per segment, giving the control points and handles.
- **Settings:** start clamps to [0, end - 0.01], end to [start + 0.01, 1],
  start first: the load applies embedded properties in the order the class
  registers them (targetPath, start, end, maxError), not the file's order
  (ledger 132).
- **Closed and hidden targets:** a closed loop repeats its first node when
  the class has supports_loop (the FGD, as for the entity lump). A hidden
  target is recentred like any other, because resourcecompiler never sets the
  visibility state the recentre checks.
- **Copies** (`Maps/PathDeformer.Copies.cs`, ledger 133): each instance copy
  and prefab map is simulated as the bake runs it. An instance copy is
  recentred where its source stands when its reference is remapped, moved by
  the step, then recentred in tree order on every path the step moved (outer
  deformers before inner ones). A prefab's map is placed and every path
  deformer recentred once; then, as its nodes enter the host world, each
  reference resolves by its old referenceID to the first holder of that id
  (the host's own node, an earlier prefab's copy, or its own), and the remap
  brings it back to its own copy. A deformer holding its own target is
  recentred k + 3 times (k path nodes) when a reference change starts it.
  Copies inside a prefab's map start from the state the prefab left.
- **Moves:** a deformer and its subtree move by pivot o step o pivot^-1 with
  the identity as pivot (each compose renormalises), and TransformBy reads a
  node's angles as the angle setter keeps them (-20 held as 340).
- A mesh straight under any deformer is world geometry, as under a group.

Open:
- **Aggregates in the settle (ragdolls):** a prop whose model has several
  physics parts or a joint (ledger 135). Valve's truth is captured
  (s2c_ragdollprobe); CRnConicalJoint's and CRnRevoluteJoint's solves are
  ported and oracle-exact. The settle still refuses such a map. Roadmap, in
  order, each step checked against the capture before the next:
  1. Done (2026-10-25, ledger 136): CRnRevoluteJoint (VPhysX type 3 with
     flags bit 1 clear, as ctm_sas's two knees): prepare (every motor mode,
     the plasticity, the limit, the two hinge rows, the point block, the
     warm start), velocity iteration, impulse store and scaling, position
     iteration; Simulation/RevoluteJoint.cs, RevoluteJointOracleTests. Left
     for step 5: the ctor defaults and desc setters as the aggregate build
     calls them (friction torque 180204d70, elasticity 180204520), and
     CRnRevoluteBlockJoint (flags bit 1, 180213270, record 6), which no
     corpus model is known to use.
  2. The conical joint's spring and velocity motor (modes 0 and 2,
     180205bd0 and the motor block of 180229870), oracle-tested.
  3. The joint wrapper 18030f0b0 (priority and infinite-mass gating, record
     0x12) and joints in the island solver: prepared first, iterated before
     the contacts, position-solved first, impulses stored on the last
     velocity iteration; vt 0x38 break test.
  4. Joints in the island manager (notes_joints.md "Islands"): activate,
     deactivate, merge (joints first), split flood fill (joints first), the
     colouring's joint buckets; SetType's leave-and-return with
     ContinuousSolve/WorldSolver's JoinedToDynamic walk of body +0x70.
  5. The aggregate build from the model (180042ba0: bodies, part shapes,
     mass, damping, drag, joints with the degrees round trip and friction
     torque), rc's attribute pass and bone poses (18105dbd0), then
     made dynamic body by body; check the step-0 world, joints and islands
     against the capture byte for byte.
  6. Step in lockstep with the capture's bodies and joints (SettleReplay
     with joints), sleep with joints.
  7. The write-back: bone 0 through bind pose 0 to the entity, the other
     bones local into boneTransforms (18105b350, 18105cac0, 181004390,
     serializer 18174aeb0); s2c_ragdollprobe's lump 0 differences.
- **An edge between two coincident baked vertices:** MergeVertices
  (1813a8840) takes CollapseEdge (181383600 -> 181382170, with the
  attribute snapshots 1813a3d50 and 1813a3f40) on Hammer's 22-bit handle
  tables; not ported, no specimen. The rest of the bake wrapper (1810c65c0:
  merge at 1e-6, AssignSmoothingModeToEdges soft, normals) is.
- **Instances inside a prefab's map:** their copies' brush entity and cable
  models are built by the lump's rules (ledger 128), with no Valve probe yet.
- **ze_hold_em_p's six doors:** a toolsclip mesh ships no hull, because the
  .vmap is older than the compile.
- **Render meshes** of entity models, and the cable models' render half (so
  the compile writes no cable model yet; their physics is ported). These
  are the model compile's render side, not physics.
- **Model compiles only:** RnHullCreate's simplify algorithms 1 and 2, its
  minimum radius check and RnMeshCreate's simplifier take option values only
  a model compile sets (the map passes the MapBuilder, Compile and mesh hull
  blocks). The agglomerator's split into separate clusters has no specimen.
- **Ledger items** in GROUND_TRUTH.md.

## The container

- **Blocks:** `PHYS CTRL RED2 DATA`, all KV3 v5 generic, resource version 1.
  CTRL is raw.
- **Block compression** follows tier0's `binary_auto`: raw below 0x100
  bytes, LZ4 when buffer plus blobs are below 0x80001, Zstd otherwise.
- **Decoded types:** scalar 0/1 come back Int64, typed arrays stay UInt32,
  and floats are doubles.
- **DATA** is the model name only.
- **RED2:**
  - the ModelDoc compiler (version 3) and three argument dependencies;
  - the shape counts, with each kind listed only when present, in
    alphabetical key order;
  - the named surfaces in first-named order under canonical names, each with
    a count of entries sharing the name;
  - `has_default_surface_property` only for a named "default".
- **PHYS:**
  - one part: spheres, capsules, hulls, then the soups as meshes;
  - the attribute index per shape, omitted when all are 0;
  - the surface hashes;
  - the collision attributes.
- **Attributes** keep the strings of the shape that registered them, tags
  distinct and alphabetical:
  - A world material keeps its own spelling (`conditionallysolid`).
  - A prop's attribute is rebuilt through vphysics2's intersection
    dictionary (pb 180153d40), so it takes vphysics2's spelling
    (`ConditionallySolid`; `Physics/CollisionNames.cs`).
- **The surface table** is keyed by the name a shape carries, as spelled:
  prop "Wood" and material "wood" are two entries.
- **Hull orthographic areas** sum `(row1 + row0) + row2` (rc 181a09e20).

The manifest:
- `world_physics.vrman_c` is compiled from a file RC saves first (AddManifest
  rc 1801f9550, SaveKV3 text generic, CRLF, no final newline). RED2 carries
  that file's CRC32 under `csgo_addons/<addon>`.
- `world.vrman` is built in memory and names no file.
- A child RED2 is struct-typed and writes `m_SpecialInputDependencies`
  empty.

Packages (`Io/VpkWriter.cs`, byte for byte on local compiles):
- A single-file VPK v2, with data in ordinal path order.
- The tree lists extensions, folders and names in reverse order of first
  appearance.
- The chunk section has one entry per MiB (archive 0x7FFF, hash type 1 =
  BLAKE3 cut to 16 bytes; older packages use type 0x8000, MD5).
- Then the MD5s, and an empty signature section.
- `s2c map-physics <addon> <map> --into <vpk> -o <out>` splices world
  physics and the physics-only entity models into a compiled package.

## World collision (`Physics/WorldCollision.cs`, `WorldPhysics.cs`)

Only a full compile (no `-fshallow`) ships world_physics.

1. **Walk.** `CPhysicsBuilder::Build` walks nodes depth first in stored
   order: at each node, its meshes, then its entities, then its children.
   - A collapsed instance copy is appended to its parent's children.
   - `CMapWorldLayer` meshes count as world.
   - A hidden node and its subtree are skipped.
   - A prefab's map is walked in place.
2. **Pieces.** Each world mesh is split by material, and each piece becomes
   a ModelDoc node. Meshes move by their node's own `AngleMatrix`; brush
   entity meshes move by CTransforms. A world mesh's faces are cut on world
   positions, and its weld never joins two .vmap vertices.
3. **Material physics** comes from the material reader (pb 1800132f0), with
   tag lists split and matched the way tier0 does it (`Io/Tier0Strings.cs`):
   - `mapbuilder.nonsolid` drops the piece.
   - The sky, clip, ladder, water and los/sound attributes give
     `conditionallysolid` plus an interact-as tag.
   - Water forces the piece solid.
   - `translucent` gives `window` for world pieces only. Entity models
     ignore shader translucency.
   - `PhysicsSurfaceProperties` names the surface.
   - A material with no group or tags leaves the attribute unset; the shape
     then takes attribute 0.
4. **Convex world meshes** (`convex_single`, `convex_multi`) are hulled in
   world space. The input is the per-material CMesh vertex buffer, joined by
   .vmap vertex. A blend material takes `PhysicsSurfaceProperties1`, and a
   subdivided mesh is hulled from its tessellation. convex_multi hulls
   physicsbuilder's groups (`BrushHulls.BufferGroups`, 18001ac20): connected
   by triangle corners over the vertex buffer, in order of each group's
   lowest vertex, vertices in buffer order. A painted layer is grouped the
   same way.
   - A simplified piece hulls what the simplifier left, in its buffer
     order (see "Mesh simplification").
5. **Painted blend layers** (`BlendSplit`). The layer count comes from the
   compiled shader's attributes (`ShaderAttributeProbe`). Each triangle
   averages its corners' paint:
   - Puddle paint above 0.5 picks the wet surface; else the first channel at
     0.5 or more picks layer 1.
   - Each surface gets a shape `<name> [<surface>]` even when empty, with
     tool hash 0.
   - A plain piece's paint is the first corner met in its own face set.
   - A subdivided face takes subdivisionData's per-grid-point paint.
6. **The material sampler** (pb `CMaterialSampler`). For a two-surface
   csgo_environment_blend material it overrides the 0.5 rule:
   - It renders sample points through the material's ToolsVis programs
     (`g_nToolsVisMode` 80, `MaterialSamplerMode` 0).
   - It reads back RGB, with weights `(c - min) / 255`.
   - Points vote per triangle; ties go to the lower layer.
   - Without `F_USE_NEW_BLENDING` the weights are constant, so layer 0,
     ported without rendering.
   - With it, `GpuMaterialSampler` (Vulkan, Valve's own programs) is bit for
     bit against a `-vulkan` compile. The default compile uses DX11, and 23
     of 3,816 points differ by one step between the two APIs.
   - Textures load at the preload cap: the first mip with
     `max(w, h) <= 512`.
7. **The mesh's own settings** (180016cf0): physicsCollisionProperty, when
   scripts/collision_properties.txt has it (case folded), replaces the
   material's group and interact lists; physicsGroup then replaces the
   group, and physicsInteractsAs, With and Exclude are appended after a
   space (`WorldCollision.WithMeshSettings`).
8. **Order.** Each shape is appended and the list re-sorted by type with
   tier0 `V_qsort`, the MSVC CRT qsort (`Maps/CrtQsort.cs`, unstable on ties
   of 8 or fewer).
9. **Soups.** One per collision attribute, in first-appearance order. A
   shape starts a new soup when it would need a per-triangle surface index
   above 255.
   - Name: the members' non-empty names joined with "; ". Past 50
     characters the next name adds "; ..." and seals it (rc 180c27e30).
   - Tool hash: the one member hash with triangles, else 0 (rc 180c28690).
     The hash is `FixupResourceName` plus lowercase MurmurHash2, seed
     0x31415926.

The part's flags (ShapeBuilder_BuildPart, rc 180c28230; `WorldPhysics.PartFlags`)
are 2 when a mesh shape's attribute names no detail layer, or one vphysics2's
intersection dictionary lacks (empty in resourcecompiler, capture_layers.py),
and 2 when there are shapes and every one's attribute names a detail layer.
CS2 defines no detail layers, so the flags are 2 exactly with a triangle
mesh: a world of hulls alone ships 0, as a hull-only entity model does
(interact-as tags do not count: atixref's func_breakables carry them).

Every triangle-mesh shape lists its vertices by first use: physicsbuilder
makes its CMesh from the export's DmeMesh corner by corner (pb 180085e80),
so a simplified piece, whose simplifier keeps its input order, is renumbered
(`WorldCollision.FirstUse`); the other pieces are in that order already.

### Cables (`Physics/CableCollision.cs`)

A cable with collisionEnabled (on by default) ships a triangle mesh. The
geometry, rc 0923, replayed bit for bit on 38 captured calls and 20 DmeMesh
exports (CableReplay, capture_cable.py):
- **Control points** (CMapPath_IsLoop 1810b7ca0): per node its position
  relative to the path and its in and out tangents (FUN_181110ba0). The
  path's origin is its first node's (CMapPath::vf274, 1810b66e0); a closed
  loop of a supports_loop class repeats the first node at the end and its
  ends neighbour each other (1810be5b0, 1810be2f0). interpolationType
  starts at 1 (the factories).
- **Samples** (1812802d0): each node pair a cubic Bezier, sampled at
  max(4, ceilf(length / tessellationSpacing)) parameters summed by
  1 / (n - 1); the length is a polyline doubled from 2 to 128 points
  (18110fc80). Each sample's radius and height scale is lerped over its pair
  (1810b8270).
- **Frames** (1812820e0): rotation minimising from +z, turned by
  `V_acosd(dot) * 57.29577951308232` degrees about the tangents' cross
  product. The quaternion rotate sums `(q x t) + (w t + v)`, not Ghidra's
  left-to-right.
- **Tube** (1810b8ae0, rings 1810bbc40): cos/sin of a summed
  `6.2831855f / sides` step; point `((sin s) r + (cos N) h) + centre` with
  `s = normalise(N x T)`; sides + 1 points a ring, two triangles a side.
- **Collision tube** (vf277, 18114d850): the side count reduced (up to 8
  kept, 8 below 18, half below 32, else 16) and the radius times 1.025f;
  with physicsSimplificationError above 0, welded at 1/32 and simplified
  with the error squared (181156860, rc's copy of CMesh_Simplify).

Where it goes:
- **cable_static** (`has_static_mesh`; the physics document's filter,
  rc 180f4d220): the collision tube moved by the cable's node matrix joins
  the world's soups (`WorldCollision.CablePieces`) in walk order.
  physicsbuilder does not simplify it again: the cable's vf216 is 1801ae2c0,
  which returns 0.
- **cable_dynamic:** its own model (`EntityPhysicsModels.CableModel`). The
  physics is the render tube (181151b30: the cable's own side count, the
  radius as it is), read back corner by corner, moved to the world and back
  into the cable's space, its seam joined by the half-edge join, and
  simplified by the map builder with the error unsquared (the export
  record's +0x90). Only with collisionEnabled (CollisionType 4).
- **The pre-settle world** (PhysMesh_Shapes' path branch, 181057020): every
  collision cable, dynamic or static, as one triangle mesh at its node
  matrix, the record from its material with its surface_properties key.

Inside instances and prefabs (s2c_cableinst, s2c_cableinst2; ledger 128):
- **Placement:** every path node is a node of its own, so the collapse
  (an instance's, or a prefab's group setter, 181019d00 -> 180fc1bf0)
  moves each stored node origin by the step matrix, and a path node's
  TransformBy (18110eae0) turns its stored tangents by the step's rotation;
  the path's origin follows its moved first node and its angles turn with
  the step (`SettleWorld.PlacedPath`). Under a deformer the deformer's own
  move places them instead (ledger 129). Moving the finished tube by one
  matrix is off by ulps.
- **Settings:** an instance's copy is a new node from the factory given
  CMapCable::CopyFrom (rc 18114b270), which clamps numSides to 3..1000 and
  the spacing and radius to 0.01f..1e5f but does not copy
  physicsSimplificationError: the copy simplifies with the factory's 2
  (`CableCollision.Copied`). A prefab's nodes are not cloned and keep theirs
  (captured: every vf277 call of the prefab copy sees the source's error).
- **Order:** the bake collapses instances and prefabs alike in tree order,
  each copy appended after its siblings, so in the world soup a prefab's
  map comes after its siblings too (`MapMeshes.ReadWithCables`).

A cable under a deformer: the world export is not deformed; the pre-settle
world runs the tube through the deformer with the cable's node matrix
(`CableCollision.Deformed`). Not ported: the water_tube shader's branches
(CS2 ships no such shader) and the cable model's render half.

### Mesh simplification (`Physics/MeshSimplifier.cs`)

Hammer's physicsSimplificationOverride and physicsSimplificationError. The
error passed is the stored one squared (CMapMesh vf216); a vmap older than
28 stored it squared, and the loader takes the square root (rc 180d7f9c0).
CS2's gameinfo default is 0.0, so only the override simplifies.

The simplifier (pb 18060xxxx, rc's copy 181bfxxxx) is a Garland-Heckbert
edge collapse in double:
- **Quadrics:** each face's plane weighted by its area; each boundary edge's
  plane, perpendicular to its first face, weighted 1000 x its length squared.
- **Edges:** one heap entry each. An edge is rejected (cost DBL_MAX) when it
  has no faces or more than two, joins two boundary vertices without being
  one, touches a vertex whose two-face edge has both faces one way, or shares
  more than two neighbours. A boundary end may only move along its boundary.
- **Target:** the quadric's minimum by the 3x3 inverse, or with a determinant
  not above 2.2e-305 (or NaN) the cheaper end, ties to the higher vertex.
- **Cost:** the quadric at the target over the summed area, plus 0.05 x error
  for each face that gets worse (circumradius over inradius) and for each
  edge of valence above 6. A face that would fold (normal under 5e-5 long or
  turned past 60 degrees) rejects the edge.
- **Loop:** collapse the heap top while its cost is not above the error, at
  most vertices - 4 times. The output keeps live vertices and faces in their
  original order.

Where it runs:
- **World pieces** (PhysicsBuilder_MeshPieces): a copy welded at 1/32 over
  all its streams, then CMesh_Simplify (needs 4 vertices, 6 indices). The
  CMesh carries eight streams when the mesh has vertex paint (position, two
  texcoords, normal, tangent, PerVertexLighting, tint, paint), else
  positions only. A painted piece is split into layers first, each layer
  simplified.
- **Brush entity models** (MapBuilder_MeshFromMapMesh): the piece's triangle
  mesh goes through the collapse as it stands, no weld, before the half-edge
  mesh is built.
- **The pre-settle world** (18105bb20): each soup welded and simplified.
- **Triangle-mesh shapes** made from a simplified piece list their vertices
  by first use.

`MeshSimplifierOracleTests` runs Valve's own functions in the archived
physicsbuilder 0924 against the port.

### `RnMeshCreate` (`Physics/RnMeshBuilder.cs`)

Bit for bit on every captured call (`RnMeshReplay`):
1. **Weld** at 1/32 (`MeshWeld`), clusters in formation order.
2. **Drop** triangles with a repeated corner or a cross product with
   `|n|^2 <= 1e-10`. Each triangle's box is grown by 1/32.
3. **BVH:** 4 or fewer triangles make a leaf. Otherwise 32-bin SAH on
   centroids over axes spanning at least 1/16, taking the strictly cheapest
   cut. With no cut, 8 or fewer make a leaf, and more are halved by count on
   the widest axis. Nodes are stored depth first.
4. **Output:** vertices in order with unused ones dropped. Triangles follow
   the leaves, each rotated by its edge lengths, and materials follow their
   triangles.
5. **Orthographic areas**, capped at 1. **Flags:** closed when every
   half-edge has exactly one twin and nothing self-touches within 1.19e-7;
   inverted when the signed volume is negative.

The self-touch test is vphysics2's own triangle GJK (`Physics/MeshGjk.cs`,
18037a1c0, ledger 133), each pair both ways round. The optional simplifier
is not ported.

### Static props (`StaticPropHulls`, `WorldCollision.PropPieces`)

- **Which props:** a `prop_static` whose `solid` key (6 when absent) is 6.
  The overrides are `collision_override` and `surface_property_override`.
  Each body of the model's compiled PHYS is placed by `AngleMatrix` with
  columns scaled, times the bind pose (pb 1800174e0).
- **Order and attributes:** spheres, capsules, hulls, then meshes, each
  shape with its body's attribute and surface. Hull surfaces register
  first.
- **Hulls** are quickhulled at tolerance 0 (at most 256 each), cooked, given
  the SVM, then moved by the node transform composed with the parent's
  inverse. That composition turns quaternion -0 into +0, which only the SVM
  planes show.
- **Meshes** are split by surface index. They come back from the half-edge
  round trip numbered by first use.
- **Spheres and capsules:**
  - Centres are moved by the prop matrix times the bind pose, and the radius
    is multiplied by the largest column length.
  - rc then moves them by the node transform (the identity, which turns -0
    into +0) and drops a radius at or below 0.
  - Addon models are read before the game's.
- **Instanced props** are placed by `BakedPlacement`; prefab props by
  `PrefabPlacement`.

### Smart props (`Maps/SmartPropEvaluator.cs`)

- The .vmap stores only each element's seed and locator deltas, keyed by
  element path.
- The evaluation starts from the node's WORLD transform (rc 181230360 calls
  `SmartPropsSystem_001` +0x88). Each record is then rebased by the inverse
  and placed again; that round trip is the ~1e-4 the props carry.
- Each element's random stream is tier0's uniform stream, seeded from its
  path's stored seed. An unstored seed draws from the unseeded master
  stream, which the port refuses.
- Ported: groups, models, ModifyState, PickOne (FIRST, SPECIFIC, stored
  RANDOM), FitOnLine (random and largest-first), the sizer, locators,
  Translate, Scale and the variable filter. Class defaults are read from
  their constructors.
- Not ported: instanced or scaled nodes, stretch, ALL_IN_ORDER, orient along
  line, root modifiers, IsValid criteria, detail objects, surface override,
  stored `m_DeltaTransform`, lattice.

### Subdivision (`Maps/HalfEdgeMesh.cs`, `SubdivisionBake.cs`)

The map builder bakes subdivided faces on Valve's half-edge mesh
(BakeSubdivisionForFaces, rc 1813baa40), and the port keeps it as Valve
does:
- **Containers:** dense arrays plus handle tables. A new element takes the
  handle at the head of a FIFO free list; a removal swaps the last element
  in. The export walks the dense face array, so these rules fix the
  triangle order.
- **Splits:** faces are split level by level, lower first (rc 1813ca560).
  Each side's midpoint is found by arc length over already-split edges (a
  local t under 0.01 or over 0.99 reuses the end vertex). Children recurse
  in corner order 0, 1, 3, 2 and keep their parent's corner order.
- **Grid positions:** each face's grid points take its grid's positions, and
  the last write wins.
- **Export:** a coarser face beside a finer one keeps the extra points.
  Faces are cut on world positions, and baked vertices are never welded
  together.

## Brush entity models (`Physics/EntityPhysicsModels.cs`)

`maps/<map>/entities/<name>_<node>.vmdl_c`, in the same container as world
physics.

Which models, and what they hold:
- **Entities:** the world walk, minus instance groups and hidden nodes.
  - Instance copies are built from their baked placements.
  - Entities inside a prefab are named by id path (`<name>_108_3`) and
    placed through the prefab.
- **No file:** a model whose material the game cannot find fails to
  compile, so no file is written, although the lump still names it.
- **Physics-only models:**
  - They are the classes with FGD `physics_only_model` or
    `render_as_world_but_physics_as_entity`, or meshes whose every face is
    `mapbuilder.nodraw`.
  - With no shapes left, the file is RED2 and DATA alone.
- **Auto-applied materials:** `auto_apply_material` (the triggers'
  toolstrigger) names that material in every slot.
- **Mesh settings:** as for world pieces (`WorldCollision.WithMeshSettings`),
  but never a mesh's physicsInteractsExclude: ConvertMeshForBuilder's record
  for the map builder has no exclude list.
- **Simplification:** see "Mesh simplification".
- **Physics type:** "default" resolves to mesh for world geometry and for
  `PhysicsTypeOverride_Mesh` classes, to convex_single for
  `PhysicsTypeOverride_SingleConvex`, and otherwise to convex_multi.
  - Mesh-type pieces are cut and welded like the world's, in entity space.
- **Flags and RED2:**
  - The part flags as the world's (`WorldPhysics.PartFlags`): 0, or 2 with
    meshes.
  - `keep_vertices` is an IntArg.
  - `mapbuilder_entity_classname` is fingerprinted by its string token.

### Hulls, from a brush mesh to the shipped `RnHull_t`

The path is `Physics/BrushHulls.cs`, bit for bit on Mako (1,266), atixref
(277) and ze_hold_em_p (163 of 169):
1. **Pieces:** each material is a piece, clip materials included. A piece
   that cannot be hulled adds nothing.
2. **The per-corner mesh.** Hammer's `ConvertMeshForBuilder` writes each
   mesh as a DmeMesh and the builder reads it back (`MapMeshCorners`):
   - A world mesh moves to world space; normals are turned by the same
     matrix without renormalising. An entity mesh stays in its own space.
   - Texcoords shift by whole numbers per UV island, only when some texcoord
     is outside +-1.03125.
   - The second texcoord set and the paint streams survive only for shaders
     that read them.
3. **Weld** (`MeshWeld`): tolerance 1/32, 1/2048 for texcoords. Each vertex
   joins the first earlier cluster within tolerance, in `CVertexKDTree`
   query order.
4. **Triangulation** (`PolygonTriangulator`):
   - A quad keeps the (0,2) diagonal unless it is the worse fit by more than
     1.01 squared.
   - Larger faces are ear-clipped by `1/area + 2(1 - largest corner
     cosine)`: strict, and the first ear wins.
   - Vertices are numbered in first-appearance order and joined by
     position.
5. **Groups:** convex_multi splits triangles into vertex-connected groups
   through Valve's hash sets (`ValveHashSet`: fmix32, chained, sized to the
   next power of two of `faces * 32 / 3`, at least 32).
6. **The map builder's hull:** a 5 degree angle and minimum thickness 1. A
   flat set is pushed out by 1 along +x, +y and +z.
7. **The model compile:** it quickhulls the points raw (tolerance 0), then
   calls `RnHullCreate`, then applies the shape transform (the identity,
   which turns -0 into +0).
8. **Placement:** vertices go to world space through the mesh's CTransform,
   then into the entity's space through its inverse (`Maps/CTransform.cs`).

### `RnHullCreate` (vp export, `Physics/RnHullBuilder.cs`)

- **Options passed by the compile:** 0, 0, then 256, 256, 256, then 0, 0,
  then true, true, false. The function appends a point scale of 1 and 0.01.
- **Box shortcut:** with 8 to 36 points all within 1/32 of the AABB
  corners, and all 8 corners hit, the result is `RnHullCreateBox`.
- **Normalise:** `c = (s min + s max) / 2`, and `k = 2^max(e - 6, 0)` for the
  `frexp` exponent e of the largest extent. Points become `(s p - c) / k`.
- **Quickhull** (`Physics/QuickHull.cs`, Gregorius style):
  - The tolerance is `v = min(sum, m sqrt 3) * 3 * 1.01 + m`, floored at 1.
    Then `tol0 = v * 50 * FLT_EPSILON`, `tol1 = 4 tol0` and
    `tol2 = 2 tol1`.
  - A node is inserted just before the first node ever inserted into its
    list.
  - Vertices, half-edges and faces come from fixed pools with index free
    lists. Pool order is visible through the sharpen pass.
- **Afterwards:** an extrusion retry, the limits and sharpen passes, and the
  inner-margin check. The result is scaled back.
- **Conversion to `RnHull_t` (0xf8 bytes):**
  - centroid, max angular radius, min centroid radius, bounds;
  - orthographic areas, mass properties, volume, surface area;
  - vertex positions and face planes;
  - SVM pointer;
  - byte-indexed vertices, half-edges (next, twin, origin, face) and faces.
  - Twins sit at adjacent indices.
- **Simplifier** (`HullSimplifier`, algorithm 0, QEM):
  - quadric edge collapse (it only renumbers unless there are more than 85
    triangles);
  - then `CDualHullAgglomerator` clusters face planes bottom up (log cost,
    plus 90 past the angle);
  - then the hull from the planes' dual points.
  - Algorithms 1 and 2 are not ported.

### Region SVM (`Physics/RegionSvm.cs`, rc)

- **Built** from the cooked hull before the shape transform. The transform
  then moves its planes along with the faces.
- **Regions:** one per feature, in order: inside, vertices, edges (the even
  half-edge), faces. Each is a convex cell in homogeneous points.
- **Splitting**, breadth first:
  - A region is across a plane when it reaches more than 1e-4 past it on
    both sides.
  - Planes bounding regions on both sides are tried first.
  - The score is regions in front times regions behind; ties keep the lower
    plane.
  - With no split, a pairwise separating-plane fallback runs (59 of Mako's
    hulls need it).
- **Words:**

  | node | word |
  |---|---|
  | inside | `0x00000000` |
  | vertex | `0x20000000`, then its first outgoing half-edge shifted 8, then the vertex |
  | edge | `0x40000000` and the half-edge |
  | face | `0x60000000` and the face |
  | split | `0x8000` plus the plane, and the front child's offset |

  Each plane's offset gets `n . centroid` added back.

## Tools and tests

- **World physics:**
  - `WPBUILD=<addon>|<map>|<compiled vpk>` on `WorldPhysicsAuthorTests`
    builds from the .vmap and diffs the trees.
  - `WPAUTHOR` re-authors Valve's own trees.
  - `ENTBUILD=` does the same for entity models.
- **Simplification:** `MeshSimplifierOracleTests` (Valve's DLL, random
  meshes), `SIMPLIFY_CAPTURE=` and `SIMPLIFY_INPUTS=` (world pieces,
  `tools/physics/capture_simplify.py`), `MAPSIMPLIFY=`,
  `MAPSIMPLIFY_INPUTS=` and `SETTLESIMPLIFY=` (entity pieces and the
  pre-settle world, `tools/physics/capture_mapsimplify.py`). Probe maps:
  `tools/physics/simplify_probe_map.py`, `mesh_collision_probe_map.py`.
- **Captures** (Frida on resourcecompiler, CS2 closed):
  - `tools/physics/capture_physshapes.py` (`--full --rnmesh --vulkan`);
  - `capture_rnmesh.py`, `capture_convex.py` and `capture_bake.py`;
  - `tools/hulls/capture_weld.py` and `dump_meshbuf.py`.
- **Replays:**
  - `RNMESH=`, `WELD=`, `HULL=` and `HULL_PHYS=`;
  - `PROPHULLS=`, `WORLDCOL=` (`WorldCollisionInput`), `BLENDREPLAY` and
    `GPUREPLAY`.
- **Ghidra's float order:** the printer drops parentheses in `*` and `+`
  chains, and hides `minss` and `maxss` NaN behaviour. Check the
  association with `tools/re/symsse.py` or the disassembly.
