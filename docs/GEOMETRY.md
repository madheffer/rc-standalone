# Geometry

The .vmap's meshes into what the compile builds from them:
- the mesh export (shared with physics);
- the ray trace scene (`.rte`) that visibility reads;
- the world nodes and render meshes the map ships.

Only the first two are ported.

## Mesh export

Valve's side is `CMapMesh_ConvertMeshForBuilder` and its steps:
`HammerMesh_CopyFrom`, `HammerMesh_TransformToWorld`, texcoord shifting,
`CMesh_WriteDmeMesh`, then `DmeMeshToCMesh`. Ours:
- `Maps/MapMeshCorners.cs`: the per-corner export (PHYSICS.md has the weld).
- `Maps/MapMeshes.cs`: the node walk (instances, prefabs, hidden nodes).
- `Physics/HalfEdgeMesh.cs` and `SubdivisionBake.cs`: subdivision.

Placement:
- **A node's world matrix** is the instance path times its own
  `AngleMatrix(angles, origin)`, in the binary's float order
  (ConcatTransforms sums z, y, x, then the translation).
- **Points** go through tier0's SIMD transform, each row summed
  `(x + z) + (y + t)`.
- **An instance copy** is placed where the collapse leaves it
  (`SettleWorld.Baked`). The node's origin and angles move through each
  instance's matrix against its target's inverse, both built in double, then
  go through MatrixAngles and SetAngles. Multiplying the path matrix in
  instead is one ulp out under rotated instances.
- **A prefab** (`CMapPrefab`) is placed the way instance collapse moves a node
  (`SettleWorld.PrefabPlacement`).

Open:
- the stream layout per material shader (render meshes need it);
- faces re-projected by about 1e-5;
- `ApplyDeformer` (deformer meshes);
- displacement, which sits in the subdivision code.

## The trace scene (`.rte`)

`CVisBuilder::Build` writes `%TEMP%\csgo_addons\<addon>\maps\<map>.rte`
during `-world`, and the vis entry reads it back through
`CVisibilityMesh::LoadRTEFromFile`, which settles the layout.

```
offset          size     section
0               60       header: nine u32 (6, 3, 0, A, B, C, B, 0, B), world box min/max, u32
60              A * 8    kd nodes: float split, u32 packed child + axis
60 + A*8        B * 48   triangles
                C * 4    leaf triangle indices
                B * 8    per-triangle id: the face material's resource id (nothing reads it)
                B * 12   per-triangle reflectivity, all (1,1,1)
```

The 48-byte triangle record:

| slot | holds |
|---|---|
| floats 0-2 | unit normal |
| float 3 | plane distance |
| float 4 | triangle id (equals the record's index) |
| floats 5-7 | edge equation 0 (a, b, c) |
| floats 8-10 | edge equation 1 (a, b, c) |
| bytes 0x2c, 0x2d | projection axes u, v |
| u16 0x2e | flags |

The edge equations are `E(u, v) = a u + b v + c` over the projection. A
vertex is rebuilt by solving `E0, E1 = (0,1), (0,0), (1,0)`, then recovering
the third axis from the plane. That is `RayTraceEnvironment`, and the loader
rejects only non-finite corners.

Flags:

| bit | meaning |
|---|---|
| 0x0001 with 0x0800 | skipped by the loader |
| 0x0010 | nodraw in the file, stored as 0x0020 |
| 0x0800 | excluded from the trace |
| 0x1000 | occupancy only for voxel boxes wider than 256; the sky for the sky stage |

If nodraw is over 80% of the total area, the loader clears the mark from
every triangle.

The file is not byte-stable between compiles. The triangle order changes
(and with it the ids and the kd tree), so compare by content and never by
hash.

### Built from the .vmap (`TraceScene`, `MapGeometry`)

`TraceSceneTests` finds each of our triangles' records bit for bit in the
same compile's `.rte`. Every specimen is exact both ways:

| map | triangles |
|---|---|
| probe01 | 288 |
| cardtest | 300 |
| ze_hold_em_p | 4,548 |
| atixref | 32,854 |
| Mako | 279,064 |

The rules:
- **Which meshes:** those under the world or a group. Under an entity only
  when its class renders as world (FGD `render_as_world_but_physics_as_entity`,
  which in CS2 is only `func_water`).
- **Dropped faces:** a face whose material has `mapbuilder.nodraw` or
  `mapbuilder.occluder`, and none of `visblocker`, `acceptvis` or `sky`, is
  dropped. The attributes come from the compiled `vmat_c`.
- **No props:** the collector's prop pass is off in every compile measured.
- **Hidden:** a mesh hidden by `CVisibilityMgr`, under a hidden node or
  through a hidden instance is not compiled.
- **Slivers:** `WRB_EmitRteTriangles` computes the three edge lengths in
  float, sorts them, and keeps a triangle only when the longest is at least
  0.0001 and `longest * 1.0001 <= sum of the other two`.
- **Subdivided meshes** go through `SubdivisionBake` (PHYSICS.md).

- **The flag word** (`WRB_CollectRteMeshes`, then the material's bits in
  `WRB_EmitRteTriangles`): 0x800 for a visexclude mesh; 0xa000 for shadow
  mode 1 and 0x2000 for mode 2, the mode being the mesh's `disableShadows`,
  forced to 1 by the material's DoNotCastShadows (its own attribute,
  `F_DO_NOT_CAST_SHADOWS`, or csgo_water_fancy, which declares it on every
  combo); 0x2 for `F_RENDER_BACKFACES`. Every triangle's word and id match
  on all five maps. Modes 2 and 3 are read, not measured.

Open:
- **Skip boxes:** the emitter also skips triangles inside the builder's
  oriented boxes (`WRB_RteSkipBoxes`). What places them is unknown, and no
  specimen has any.
- **Triangle order:** ours is walk order. The file groups each mesh by
  material, and the rest follows the world renderer's mesh split.
- **Bad meshes:** a mesh whose bounds exceed `2 sqrt(3) g_flConfigMaxCoord`
  is skipped whole; no specimen has one.
- The entry's 0x80 (object flag 0x80): no specimen's world meshes carry it.

## World nodes and render meshes

In progress. What is settled:
- **Buffers** (`Meshopt/MeshoptEncoder.cs`): meshoptimizer's vertex codec
  version 1 at level 3 and index codec version 1. Every meshopt MVTX and
  MIDX block of probe01, cardtest and atixref's node models re-encodes
  byte for byte from its decoded data (`MeshoptEncoderTests`).
- **The model container** (`Containers/WorldNodeModelAuthor.cs`): the
  buffer blocks in block index order, then MDAT, CTRL, RERL, RED2, DATA.
  All 401 node models of those three maps re-author with the same
  container facts, decoded trees, buffers and references
  (`WorldNodeModelAuthorTests`). Resource layout (`ValveLayout`): blocks
  zero-padded to 16 bytes, RERL to 4 with its entries 8-aligned.
- **The index layer** (`.vwrld`, `.vwnod`) already round-trips through
  `AuthorKv3Tree` (`MapResourceAuthoringTests`).

A node model's vertex (probe01): float3 position, float2 texcoord
(`LowPrecisionUv`, R32G32 float), and a packed u32 tangent frame
(`CompressTangentFrame`: octahedral normal 10 + 10 bits, the tangent's
angle about a basis from the normal in 11 bits, the bitangent sign).
Aggregate models add a `color` stream at index 1 and meshlets.

The compile, as read from resourcecompiler 0923 (ADDRESSES.md, WN):
- **Entry headers** (ported: `MeshEntryFlags`, `MaterialAttributes`,
  `NodeEntryHeader`). `WRB_MeshEntryFlags` reads 35 material attributes
  (each "found and true"), resolved by murmur from shader and material
  attribute names (`tools/re/emu_material_queries.py` lists the keys), and
  the builder record; the port matches the binary run under unicorn on 400
  random cases (`MeshEntryFlagsEmulated`). A material's attributes are its
  vmat_c's own, then those its shader declares for the static combo its
  F_ parameters select (vs, ps, psrs; dynamic expressions over FEAT[i]
  evaluated); the size is the RepresentativeTexture binding's own vtex_c
  size (-1 without one). The world record: lighting mode disableShadows,
  bit 2 disablemerging; a prop's: disableshadows, donotcollapse or
  disablemerging. Every compared field (tint, id, bounds centre, fades,
  boost, object flags, +0x1a0 bytes, flags, size) is exact on all world
  entries of probe01, cardtest and atixref (598) and all 102 atixref prop
  entries (`NodeEntryHeaderReplay`), with two measured rules open
  (GROUND_TRUTH 40, 41).
- **Entries:** `WRB_CollectMeshEntries` turns each map mesh piece into a
  0x238-byte node mesh entry: +0x1b0 attribute flags (64-bit, from the
  material's attributes in `WRB_MeshEntryFlags`; 0x40000000 is
  `SupportsAggregateInstancing`), +0xbc object flags (0x80, 0x200, 0x400
  render to cubemaps, 0x100000), +0xa0 cubemap, +0xa4 light probe volume,
  +0xac overlay order, +0xb4 fade max, +0x1c8 a 3x4 matrix. The entry is
  filled from the 0xf0-byte mesh record Hammer's `ConvertMeshForBuilder`
  writes (the one `tools/hulls/dump_meshbuf.py` dumps): its bytes +0x24 to
  +0x27 over 255 are the tint (entry +0x28, compared by `CanMerge`), +0x37
  sets attribute bit 2 (never merge), +0x34, +0x38 and +0x39 set object
  flags 0x80, 0x200 and 0x400, +0x3a with +0x40 == 1 sets 0x100000, +0x44
  of 1 or 2 sets attribute bit 37 or 38, and +0x28, +0x2c, +0x30, +0x35,
  +0x36, +0x3c, +0x48 to +0x54 land in the entry's other fields (read in
  18023f6d0, not mapped one by one yet). `CanMerge` is ported
  (`WrbMeshEntry`) and checked by the merger replay.
- **Settings** come from csgo_core/gameinfo.gi (csgo/gameinfo.gi holds
  none of them): WorldRendererBuilder sets FixTJunctionEdgeCracks,
  VisibilityGuidedMeshClustering, UseAggregateInstances,
  AggregateInstancingMeshlets, UseStaticEnvMapForObjectsWithLightingOrigin
  and BakePropsWithNonUniformScale to 1, clustered meshes at minimum 2048
  triangles, 2048 vertices and volume 1800, MaxPrecomputedVisClusterMembership
  16; MeshCompiler sets PerDrawCullingData, UseMikkTSpace and
  SplitDepthStream to 1, the vertex codec at version 1 level 3, and
  MeshletConeWeight 0.15.
- **The entries BuildNode starts from** (`NodeMeshEntries`, checked by
  `tools/vis/capture_nodeentries.py` and `NodeEntriesFromVmap`, which
  snapshot a node's entries before and after every BuildNode step): each
  visible world mesh's face sets in walk order, one vertex per corner, with
  position, texcoord, normal, tangent and, when the mesh has it,
  PerVertexLighting. Positions go to the world by the node's matrix, normals
  and tangents rotated by it and renormalised (squares summed z, y, x, times
  the reciprocal of the root; measured, the function not read). Exact on all
  18 of probe01's entries, bar PerVertexLighting: that stream is baked
  lighting Hammer applies from a previous bake (180f0ea90), an input from
  the baked halves. What BuildNode then does on probe01: Step257b50 takes
  out the entries with attribute bit 10 (toolslightmapres; their positions
  go to a list at node +0x160) and drops those with bit 36; the T-junction
  fix adds 14 vertices and 14 triangles; the closing 1/32 weld takes 878
  corners to 444 vertices. The T-junction fix: per triangle edge without a
  neighbour in its mesh (adjacency from 1812d91c0), two 32-byte records
  (a point, the unit direction along the edge, the entry's +0x40, the
  vertex), sorted and deduplicated, then a CVertexKDTree over them, and a
  job that inserts other meshes' points lying on each open edge and cuts
  the face again (18025b410; not ported yet).
- **Subdivided meshes in the entries** (`MapMeshCorners`, baked path):
  ConvertMeshForBuilder bakes subdivision first (1810c6520 ->
  BakeSubdivisionForFaces), so a mesh with subdivided faces is exported
  from the baked half-edge mesh. Every face-vertex stream rides the bake:
  split edges lerp their corners (1813a54d0), each cell corner takes the
  patch grid of every stream (1813c94f0), from subdivisionData's own grid
  where it stores one (texcoord, VertexPaintBlendParams,
  VertexPaintTintColor) and from the corner, side midpoints and mean
  otherwise. The bake marks its new edges soft (AssignSmoothingModeToEdges
  mode 2: flags & ~1 | 2; pieces split from them stay new) and 1810cddd0
  recomputes every corner normal at the baked faces' vertices
  (1813850e0): corners turn about the vertex to the twin of their next,
  the fan runs between hard edges (open edges, flag bit 0, or face
  normals whose dot is not above cos(smoothingAngle) + 1e-5; flag bit 1
  soft), and the normal is the normalised sum of the fan's Newell face
  normals. The texcoord island shift then runs on the baked mesh. On
  atixref all 44 subdivided entries match in corner order, positions,
  texcoords, paint, tint and normals (after the export's renormalisation);
  tangents are open (GROUND_TRUTH 38). The second texcoord and
  VertexPaintBlendParams are added only when some material of the mesh
  reads LowPrecisionUv1 (538 of 560 atixref entries built, 512 exact).
- **Static props in the entries** (`WRBNode_AddStaticProps`, read; not
  ported): a prop_static is baked into the node's entries, unless it is a
  clutter instance, when it sets baketoworld, has a material override or a
  deformer, or (with csgo_core's BakePropsWithNonUniformScale) its |scales|
  spread by more than 0.0001, or carries extra vertex streams. atixref: 82
  non-uniformly scaled props give 102 entries, after the world's, ids past
  the map's node ids for copies inside instances. Each entry is one draw
  call of the model's LOD: its vertices in buffer order from the call's
  lowest index, the call's indices rebased; streams position, normal,
  tangent, texcoord, then VertexPaintTintColor (COLOR0 / 255) or a zero
  "color", then a zero texcoord. The loader's floats come from meshsystem's
  unpack (`PackedNormals`; SNORM16 texcoords max(v / 32767, -1), half
  floats as they are). `CMesh_TransformByMatrix` (`PropTransform`) then
  places them: the prop matrix is Concat(AngleMatrix(angles),
  diag(scales)) with the origin (the collapsed placement inside an
  instance); positions by it, normals by its 4x4 inverse transposed,
  tangents by it, both renormalised; u (not negative) scaled about 0 and
  1 - v about 1 by the column length the material's
  TexCoordScaleByModelU/V name (g_nScaleTexCoord*ByModelScaleAxis - 1), so
  even unscaled texcoords pass through 1 - (0 + ((1 - v) - 1) * 1 + 1).
  `NodePropEntries` builds them from the .vmap: props with baketoworld or
  a non-uniform scale; ordered by model path, a model's props last to first
  in the walk, a prop's draw calls in order; the id the prop's node id or,
  for an instance copy, the copy's; the skin key remapping materials
  through the model's material groups. atixref: all 102 entries (50037
  vertices) exact from the .vmap in order, id, material, every stream and
  the indices (`PropEntriesProbe.BuiltFromVmap`). The materialoverride key
  does not bake a prop (14 atixref props carry one, none baked), so the
  record's +0x40 list is something else. Not ported: the per-entry fields
  (`WRBNode_PropEntry`), the clutter, +0x40, deformer, extra-stream and
  +0x17c triggers, and csgo_foliage's NeedsLocalSpaceVertices rule (only
  with foliage animation and no vertex animation; such a prop stays in
  model space, as csgo_simple_liquid's always does).
- **BuildNode:** bounds, static props, entries with +0x1a5 or attribute
  0x400000000 dropped, removal of triangles inside, T-junction cracks
  (on), optional baking, the merge (`WRBNode_MergeMeshes`, only without
  render clusters; CS2 clusters by visibility, so it does not run), then
  `CMesh_Weld` at 1/32.
- **The merge:** for each entry without attribute bit 0x2, later entries
  that `WRBMeshEntry_CanMerge` accepts join it while the sum stays under
  0x200000 vertices and 0x400000 indices; a group becomes one entry with
  the first's fields. `WRBMeshEntry_CanMerge` asks for equal attribute
  flags, material, overlay order, object flags, lighting mode, stream
  layout, cubemap, light probe, fade, the +0x1c8 matrix within 1e-5, and
  more. With visibility-guided clustering the merging is
  `CVisibilityMeshMerger::MergeMeshes` instead (visdrivenclustering.cpp),
  per mesh list: groups are formed by taking the last remaining entry as
  seed and sweeping the list from the front for entries `CanMerge` accepts
  (up to 300,000 indices); an "init bounds" pass gives each member the vis
  clusters it touches; a member in one cluster goes whole into the bucket
  keyed by that cluster set, a member in several is split triangle by
  triangle into the buckets of each triangle's set; the buckets (a hash map
  keyed by the set) are merged into entries by a thread pool job. This is
  where triangle order, merges and clusters come from. Ported as
  `VisibilityMeshMerger` (the CUtlHashTable whose slot order is the output
  order, the membership tests, TriBoxOverlap, the merge passes,
  ChooseTarget with MSVC's std::sort). Exact on every captured call of
  probe01, cardtest, atixref and ze_hold_em_p (12 calls, 559 entries, 553
  buckets; the
  capture is `tools/vis/capture_meshmerge.py`, the replay
  `VisibilityMeshMergerReplay`), and the ported `CanMerge` agrees with
  Valve's on all 141,068 pairs. Two facts only the capture showed: every
  table the merger makes has a minimum size of 32 (+0x18 = 0x20), and the
  output entries keep their incoming +0x218: the bucket key is written into
  the bucket's own entries after they are copied out. Not exercised yet:
  triangles no cluster sees or more than 16 see (the unclustered output).
- **The merger, as read** (addresses in ADDRESSES.md, "CVisibilityMeshMerger"):
  - inputs: vis's FlatVisClusterVector (per-cluster box lists,
    `VisOutput.FlatClusterBoxes`, set into the builder context at +0x5c0) and
    MutualVisibilityMatrix (`VisOutput.MutualVisibility`, +0x5c8); each
    cluster's bound is the union of its boxes;
  - a mesh's clusters: those whose bound and one of whose boxes overlap its
    bounds; a triangle's: of the mesh's clusters, those whose bound and then
    one box pass TriBoxOverlap at 0.001; more than 16 gives no membership;
  - a bucket holds entries; a mesh joins the first entry CanMerge accepts
    (appended, triangles in order) or starts a new one;
  - merge passes per group, minimums times 4/1/2/16 with factors 1.1, 1000,
    1.25: buckets in hash slot order, entries last first; an entry under
    the minimum volume, triangles or vertices moves to the bucket
    `ChooseTarget` picks (bounds grown by 240, sets joined under the cap,
    CanMerge and under 0xffff vertices, scored by mutual visibility). On probe01 meshes 101, 100, 102 and 107
  end up in one c2 model while the other nine dev meshes are aggregated;
  the vis merger joining those four (so the merged entry no longer
  aggregates) is the likely reason, not read yet.
- **Mesh lists** (ported: `MeshLists`): the twelve lists' want/exclude
  masks read from CompileNode's constructors, CMeshList::Accepts and
  CAggregateMeshList::Accepts with `WRBMeshEntry_CanAggregate`; the merger
  runs on the aggregate, overlay and base lists in that order. From the
  .vmap with the ported entry flags (`VisibilityMeshMergerReplay.ListsFromVmap`
  against the merger capture): probe01's three lists and atixref's 105-entry
  aggregate list are exact in order; open are entries that reach the base
  list with flags 0 (GROUND_TRUTH 42) and the overlay entries, not yet
  fed to the test.
- **CompileNode:** each entry goes to the first mesh list whose flag test
  accepts it, in this order: CSkyboxBlockLightMeshList (0x48),
  CSkyboxMeshList (0x40), CBlockLightMeshList (8),
  CNoSplitOverlayMeshList, an overlay list, CWorldSpaceTextureMeshList
  (0x8000), CSkyboxMaterialMeshList (0x80), CBakedPropLODMeshList (0x2000,
  `agg_prop`), CNoSplitMeshList (4, `agg_inst`), an instanced list,
  CAggregateMeshList (0x40000000, `agg_merge`, also needs
  `WRBMeshEntry_CanAggregate`), CBaseMeshList (the rest). Then render
  clusters: with visibility-guided clustering, meshes grouped by vis
  cluster membership, and "meshes with no vis membership" split by
  `Step_BuildingRenderClusters` (`RenderClusters`: the centroid split and
  the triangle assignment, ported from FUN_180282bf0 and FUN_181366f30 but
  not yet checked, since the vis-guided half comes first). Each list
  compiles once or per cluster.
- **Names:** `<node>_lr<layer>[_c<cluster>]{_s|_d}[_cb][_dl][_b][_kv][_nv][_bl][_rtem]_<name>`
  from the object flags (0x200 `_d`, 0x400 `_cb`, 0x80 `_dl`, 0x20000 `_b`,
  0x10000 `_nv`, 0x10 `_bl`, 0x100000 `_rtem`); a list's groups are runs of
  equal cubemap, probe and flags, named `%s_cm%02d_lp%02d`.
- **Overlays** (`NodeOverlays`, `OverlayProjector`, `MeshTangents`;
  checked by tools/vis/capture_overlays.py and `OverlayCaptureProbe`):
  Hammer's static overlays are `CMapStaticOverlay` nodes, projected at
  compile in BuildNode after the bake (`WRBNode_GenerateOverlayMeshes`).
  - **Descriptors**, one per material of each visible overlay in walk
    order: the face polygon in world space from its first half-edge,
    snapped to 1/8 (HammerMesh_SnapVertices), texcoords whose box leaves
    [0, 1] less the box centre truncated toward zero; projectionMode,
    projectionFar, renderOrder, projectOnBackFaces, backFacingAngle, the
    target node ids, and MaterialAdjustmentParamsStruct packed into one
    int (nibbles of (int)(x * 15) plus two flag bits). Exact on all 297
    atixref overlays.
  - **Targets**: the node's entries in order whose attributes miss 0x1209,
    with more than two vertices and indices, whose centre-extents world box
    meets the projector box (the corners and the corners pushed back by far,
    grown by 1), and that the mode accepts (1 and 2: kind, entry +0x1a0 ? 2
    : 1; 3: listed node ids).
  - **Projection** (`ProjectPolygonOntoTriangles`): direction the
    normalised sum of face normals, target triangles facing it within the
    back-face angle, clipped by the overlay plane, the far plane and a plane
    per side (Sutherland-Hodgman, a distance whose square is under the
    longest side's square times 1e-8 counts as zero), re-cut by the polygon
    triangulator; every point carries its target triangle and weights.
  - **Mesh** (181361000): the target's streams weighted per point; position,
    the overlay texcoord (point dropped onto the plane; bilinear solve for
    a convex quad, near-parallelogram and general quadratic cases, else the
    face triangle whose area weights sum to 1), the normal normalised and
    turned, VertexGenericIntegerData (the packed int) and
    OverlayProjectionDirection (dir * 0.5 + 0.5, w from the normal's dot).
    Tangents then come from CMesh_ComputeTangents with UseMikkTSpace: the
    binary's own MikkTSpace-style generator (`MeshTangents`), not the
    reference mikktspace.c.
  - atixref: 297 overlays, 1,777 projections, 356 meshes; the pass from
    the .vmap gives all 356 bit for bit in order. Not yet: the entries'
    fields (180252ae0), tint (applied when the descriptor tint is not
    white), multi-face overlays (faces project in thread order), the
    node's name table filter, prop targets (node +0x190).
  The entries are compiled one model each by list vf 0x30 (18026e9c0),
  named `vism%i_mt_<material base>` from the entry's +0x230.
- **Aggregates** are re-split into fragments, one draw call each, sorted
  by triangle count; each has a meshlet (packed AABB, culling cone) and
  draw bounds. probe01's reflectivity aggregate holds 100 triangles from 72
  source triangles (FixTJunctionEdgeCracks is on; the pass is not read).

- **Draw buffers** (`CResourceCompilerMesh::AddDrawDescriptors`): each
  draw's vertices are renumbered by first use in its incoming index
  buffer. Then comes stock meshopt `optimizeVertexCache` (Valve's score
  table at 18243aab0 has meshopt's values), then stock `optimizeOverdraw`
  at 1.03 (the 11-bit radix version). The vertex and index codecs, the
  tangent frame and UV density are exact (MeshoptEncoderTests,
  TangentFrameTests, UvDensityTests).
- **Triangle order** into those steps is not ours yet. The builder's input
  DMX lists faces in vmap order within each face set (MeshBufferVsVmap,
  MESHBUF_ORDER=1: atixref 456 meshes ascending, the rest grouped by
  set). But on probe01's c2 model, 60 of 64 triangles come out right only
  when each mesh's ridges (face pairs) go in reversed, faces in order
  within a pair. Mesh 100's third ridge also needs face 5 before face 4.
  So the order is data dependent and set between the DMX and the draw
  (map mesh to CMesh, split, triangles inside, weld). Not found yet.

- **Node model trees** (`WorldNodeModelTrees` from a `WorldNodeModel`):
  MDAT, CTRL, RED2 and DATA match field for field, value types included, on
  all 401 node models of probe01, cardtest and atixref, and the container
  facts too (WorldNodeModelTreesTests). The description's bounds, draw
  bounds, vertex ends, counts and compile arguments are measured from
  content; material, tint, layout and the meshlet partition are inputs.
  Rules: buffer blocks per draw set (its vertex buffers, then its index
  buffer); `m_nVertexCount` is one past the highest vertex the draw reads;
  the searchable vertex count sums each draw set's first vertex buffer;
  every model sets `embedded_map_mesh` and `preserve_tangents`, an
  aggregate also `embedded_aggregate_mesh`, `generate_draw_bounds` and
  `generate_meshlets`, and only aggregates carry draw bounds and meshlets.
- **Meshlets** (`CMeshletBuilder_*`): after the vertex cache and overdraw
  passes, meshopt 1.x `buildMeshlets` at 255 vertices, 48 triangles, cone
  weight 0.15 (csgo_core's MeshletConeWeight; the code's default is 0.05)
  per draw; each meshlet through `optimizeMeshletLevel` at level
  4, its triangles appended to a new index buffer (an odd count padded with
  a degenerate triangle unless SceneSystem/RenderMeshlets), then the
  vertices refetched by first use. A meshlet's vertex offset is the draw's
  applied offset, its triangle offset absolute, its box packed 10 bits an
  axis in the draw bounds (`MeshletBounds.Pack`), its cone meshopt's
  `computeMeshletBounds` with the binary's float order
  (`MeshletBounds.Cone`), zero unless the material has AllowBackfaceCulling
  and is not DoubleSided. The cone is taken before the index buffer is
  encoded, and meshopt's index codec keeps triangle order but may rotate a
  triangle's corners (`rotateTriangle`, the edge FIFO); the float sums
  follow corner order, so a cone recomputed from the decoded buffer can
  differ. In atixref 18 static prop aggregate meshlets do, and each is
  exactly the cone of the captured call's un-rotated triangles
  (`tools/vis/capture_cones.py`, `WorldNodePropConeProbe`). Our own compile
  computes the cone before encoding, as Valve's does; every other meshlet
  matches from its decoded index range.
- **Vertex layouts** differ by material and data: texcoords are float32,
  float16 or 16-bit snorm, and float32 appears where every value would fit
  snorm, so the choice is not a value range alone (not settled). Measured
  (WorldNodeLayoutProbe, WNLAYOUT=1): of 210 float32 texcoord buffers, 162
  hold a value half precision cannot represent, but 24 are half-exact and
  still float32 (and TEXCOORD1 is often snorm-exact yet float32), so it is
  not "lossless only" either. Read since: the precision is a per mesh
  flag. Hammer's `ConvertMeshForBuilder` writes `forceHighPrecisionTexcoords`
  on the mesh DMX when `Hammer/ForceHighPrecisionTexcoords` is set or any
  texcoord is still outside +-1.03125 after the island shift
  (`HammerMesh_TexcoordsOutOfRange`); `DmeMeshToCMesh`'s stream layout
  (180d473d0) marks each texcoord stream high precision (stream byte +0x1d)
  when the mesh has that flag or any |u| or |v| exceeds 16. `CanMerge`
  compares that byte, so a draw never mixes the two. High precision is
  float32. Low precision is snorm16 or float16: every snorm buffer measured
  lies within +-1 and every float16 buffer within +-16, but 6 float16
  buffers lie within +-1, so the snorm test is not per buffer (per source
  mesh, before the split, is the likely reading). The code that turns the
  flag into a format (the layout builder behind `CMesh::CreatePackedVB`,
  181365a40) is not found yet. The lowprecisionuv scan (1810d5f40) is
  editor UI.

Open, in order: the triangle order above (it also decides the meshlet
partition), why four probe01 meshes were not aggregated, the texcoord
format rule, static prop aggregates' cones, the fragment split, the
T-junction pass, then the node and world files.
