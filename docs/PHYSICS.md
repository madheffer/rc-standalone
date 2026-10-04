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
atixref, cardtest, s2c_rounds, the four ze_hold_em builds, c2m2's prefab,
Mako, and the prop-override probe. "Whole-file" means the decoded trees and
container facts; compressed bytes are encoder defined.

Brush entity models:
- **Physics-only models** are whole-file exact: atixref 86, ze_hold_em_p 24,
  Mako 324, cardtest 5.
- **The physics block of models with render meshes** matches too: 58/58,
  8/8 and 237/237.

In game, ze_hold_em_p and atixref with our physics load and collide as
Valve's do.

Open:
- **Props:** bone overrides (PosableSkeleton), the lattice deformer, and
  instances inside prefabs.
- **The bake wrapper's steps:** MergeVertices and AssignSmoothingModeToEdges;
  no map needs them yet.
- **ze_hold_em_p's six doors:** a toolsclip mesh ships no hull, because the
  .vmap is older than the compile.
- **Render meshes** of entity models, and the cable model (geometry).
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
   - A mesh whose physicsSimplificationOverride is set with a non-zero
     error would be simplified by physicsbuilder (not ported); the build
     stops on it (`BrushHulls.RefuseSimplification`).
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
7. **Order.** Each shape is appended and the list re-sorted by type with
   tier0 `V_qsort`, the MSVC CRT qsort (`Maps/CrtQsort.cs`, unstable on ties
   of 8 or fewer).
8. **Soups.** One per collision attribute, in first-appearance order. A
   shape starts a new soup when it would need a per-triangle surface index
   above 255.
   - Name: the members' non-empty names joined with "; ". Past 50
     characters the next name adds "; ..." and seals it (rc 180c27e30).
   - Tool hash: the one member hash with triangles, else 0 (rc 180c28690).
     The hash is `FixupResourceName` plus lowercase MurmurHash2, seed
     0x31415926.

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

The self-touch test is an exact distance, not vphysics2's GJK. The optional
simplifier is not ported.

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
- **Physics type:** "default" resolves to mesh for world geometry and for
  `PhysicsTypeOverride_Mesh` classes, to convex_single for
  `PhysicsTypeOverride_SingleConvex`, and otherwise to convex_multi.
  - Mesh-type pieces are cut and welded like the world's, in entity space.
- **Flags and RED2:**
  - The part flags are 0, or 2 with meshes.
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
