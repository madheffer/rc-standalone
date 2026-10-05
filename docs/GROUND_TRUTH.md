# Ground truth ledger

Every rule the port follows must come from Valve's code: a decompile, a
capture of the compiler running, or Valve's code called in place by a test.
A rule fitted to Valve's output is a lead, not a fact. This ledger lists the
rules that were inferred, assumed or only measured, and how each is being
settled. When one is settled, it moves to the resolved list with its source.

Tools for settling:
- **Decompile** (Ghidra through ReVa) of resourcecompiler, physicsbuilder,
  vphysics2, tier0 and the other tool DLLs.
- **In-process oracle**: a test loads the installed DLL and calls the function
  itself on many inputs (ResourceCompilerOracle, Vphysics2Oracle). Used
  whenever the function is pure enough to call.
- **Capture**: Frida on resourcecompiler during a compile of a small test map,
  dumping a function's inputs and outputs (the vis capture harness).

The game itself is not attached to. Nothing here needs it; a question only the
game can answer is raised with the user first, and runs with -insecure.

## Open

### World physics
3. **KV3 encodings per resource type.** Most block writers save with
   binary_auto (settled below), but some call sites pass plain "binary" or
   "binary_bc". Map each writer to the resource types it serves.
4. **Face order around a vertex** in the brush hull neighbour set: triangle
   order stands in (it only matters on a hash collision).
5. **Instance path for brush entity meshes**: applied after the node's own
   move, or composed first. Both place atixref the same; not read.
6. **Subdivision bake remainder**: the face collapse is modelled from the
   edge collapses (confirmed by exact output, not read line by line). The
   wrapper's vertex merge is ported (see Resolved) for the two paths a
   specimen takes; an edge between two coincident vertices (the edge
   collapse, 181382170) stops the build.
7. **Collision names registered by the game** (csgo_*) keep the model's
   spelling. Read (2026-10-04): physicsbuilder's attribute strings
   (180153d40) come from vphysics2's CNameIndex by index, and the index
   finds names by token hash (vf5 180299ea0), so a name the pool does not
   hold takes the spelling it was first registered with in the process.
   Open: the order models register their names in, which decides only when
   two models spell one game name differently (no specimen does).
10. **Hammer's re-projected texcoords and normals** (about 1e-5 off on some
    faces). They do not change a hull, but the source is not read.
12. **VPK v2 layout** (tree order, chunk hashes, signature section): byte
    exact on four packages, the packer not read.

21. **Case folding** outside the compile's own code: Maps/ and Physics/
    now fold A-Z only (Tier0Strings: IgnoreCase, EqualsAscii and friends,
    2026-10-04). The resource authoring, KV3/DMX text and file-path code
    (Containers/, Kv3/, Dmx/, Io/, Texture/, Building/) keep .NET's
    folding; each is to be audited against the Valve call it mirrors.

22. **Coverage.** Every entity class the FGD offers (point_servercommand
    and the other server, logic and point entities included) and every
    compile option Hammer's build dialog and resourcecompiler's command line
    offer must be handled, not only what the test maps use. Both lists are
    enumerated in docs/COVERAGE.md (354 offered classes, every build
    switch), and every offered class is measured through the probe map.
    Open there: the light and probe bake keys, particle snapshot files, the
    vmap upgrade table, the landmark/camera/cull-triangle world lists, and
    every switch the port does not yet take.
24. **What an entities-only build clears**: the world step clears the
    entities folder and flammables; whether the entity models survive an
    entities-only build is not settled (compile-map keeps them).
26. **Prefab placement**: a node in a prefab's map is placed as instance
    collapse moves a node (SettleWorld.PrefabPlacement: the prefab's own
    placement as the move, the node's origin through it and its angles
    rebuilt); a quaternion or plain matrix path each leave spheres a few
    ulps off. Measured whole-file exact on s2c_prefabprobe; the prefab
    loader (Hammer's map document, +0x6b8 docs) is not read.

45. **Prop bone overrides** (a prop_static's pose, entity +0x268 from
    CMapEntity +0x438, applied per body by name in 180200af0 through
    181749d20): read on the physics side, but the .vmap storage of the pose
    is not found (the DMX attribute list of CMapEntity names no pose; the
    lump's boneTransforms key is written from it by ExportToLump). No map in
    the installed content uses it.
46. **Instances inside a prefab's map: entity ids** (settled for the
    lump 2026-10-04 by capture, tools/entities/capture_collapse.py on
    s2c_prefabprobe3 and 4; MapInstances.Expand with prefabs). The bake
    collapses the CMapPrefab like an instance first: its root takes the
    next id and its block is its map's copy count plus the nodes its map's
    loader made (smart prop locators) plus one. The copy count is every
    node in preorder, and for an instance whose target group comes later
    in the walk, that group's subtree once more (copied for the instance
    before the walk reaches it): c2m2's environment prefab's instance 4928
    precedes its target 4888, a group of one entity, which is the "two
    slots more" seen before. The instances inside then collapse in tree
    order from the end of the prefab's block; one inside a target group
    waits for its group's copies, past every first-round block, as in a
    map of its own. Their copies ship with plain ids. All eleven prefab
    probes' lumps are within their documented gaps. Not captured: a prefab
    beside instances of the map's own, and a prefab inside a prefab (both
    throw). c2m3_coaster_d_d is the first: its prefab 3 sits beside
    instances of its own, so its lump and settle wait on that capture. The
    settle takes its copy ids from this same expansion (prefabs on).

47. **Order of baked props within a model.** NodePropEntries orders a
    model's baked props last to first in the walk (measured on atixref's
    102 entries). WRBNode_AddStaticProps (180255b90) takes them from the
    node's per-model lists (+0x198) last to first, but what fills those
    lists is not read (the full order on deformerprobe2, two bend groups
    2144-2159 and 2162-2177 of one model: 2144, 2153-2159, 2162, 2163,
    2145, 2164-2173, 2146, 2174-2177, 2147-2152, which interleaves the
    groups like a spatial query's traversal): on deformerprobe2 the 32 bend-deformed props come
    in another order (2144, 2153 to 2163, 2145, 2164 to 2173, ...), while
    all 65 entries are exact in content (PropEntriesProbe with
    PROPENTRIES_BYID=1).

48. **The targetname of a cubemap or probe volume record** (EnvVolumes):
    the late export (180240a60) reads targetname from the exported keys;
    we take it from the entity's key table before any instance name fixup.
    It only matters to an entry with a lighting origin name, which no
    specimen has.

### Map resources
15. What picks Zstd or LZ4 for map output.

### Other modules
16. **Settled 2026-10-03.** The slow normalise (lengths outside 1e-17 to
    1e17): visbuilder's NormaliseSlowPath is resourcecompiler's
    VectorNormalize_Slow (18125d000), read in both: (y y + x x) + z z in
    double, components times the reciprocal. Ported once
    (VectorNormalizeSlow) and used by every normalise that reaches it
    (visibility, trace scene, lights, tangent frames, deformers,
    triangulator, node entries), where three approximations and six throws
    stood before.

29. **Settled 2026-10-04 (RTE triangle order).** The emitter writes a
    mesh's triangles as the exported mesh holds them: one face set per
    (lightmap scale bias, material), biases ascending, materials in the
    mesh's materials array order, face order kept within a set (as
    MapMeshCorners cuts the pieces). Our trace scene now puts every triangle
    at the .rte's own index on probe01, cardtest, ze_hold_em_p (all) and
    atixref (32,854 of 32,854). The order matters: the loader rebuilds the
    tracer kd tree from the file order and the voxelizer walks it; with
    triangles in walk order, atixref's octree differed in 45 leaf masks and
    its VXVS from the .vmap was off (VisStageDiffProbe, VISDIFF=...).

27. RTE: what places the emitter's oriented skip boxes (builder +0x238,
    1802597d0, 180259130); no specimen has one.
28. Visibility split hints (types 4, 5, 6): ported from the decompile, no
    specimen map has one.
31. **The editor trace scene's traversal**: EditorTraceScene finds the
    nearest hit through a padded bounding-volume tree of its own, ties by
    triangle index; Valve's instance scenes use their own kd trees and walk
    order, which decide ties and cracks (as they did for visibility on
    Mako). Suspected for atixref's omni2 lights 7306, 7318 and 7348 (one
    cube face each, 0.01 to 0.03): on 7306 the ray that decides lands on a
    prop box's corner edge, and hiding any one prop does not give Valve's
    box (LightRayProbe, LIGHTRAY_SWEEP). The same kind on the prefab
    probes: s2c_prefabprobe4's 7849, 7861, 7891 (atixref's three as
    copies) and 8106; s2c_prefabprobe3's 1581 (small) and 2385, whose face
    3 is far off (extent 545 against 702; ours blocks more). Settled for
    2385 (2026-10-04): the scene's materials are not it, the per-material
    flag words captured from Valve's map meshes (capture_matflags.py) are
    ours, a missing material's 0 included; the outer map plays no part;
    no single prop, triangle or triangle pair of mesh 5503 hidden gives
    Valve's box (LIGHTRAY_TRISWEEP). Mako is the large case: 125 omni2,
    rect and barn lights differ, by a median of 75 units on some face.
    The scene itself, captured (tools/entities/capture_rayscene.py, with
    scene generations; EditorSceneCaptureTests, RAYSCENE=): on
    s2c_prefabprobe3 all 817 map-mesh scenes of Valve's world scene are
    ours triangle for triangle (32,881). Valve's world scene also holds
    901 single-child scenes (models; we place 585 static props, the rest
    are presumably dynamic and masked by the owner flags) and one scene
    at identity with 581 mesh scenes and 1,108 model instances whose
    meshes match neither our collapsed nor our prefab-local meshes (many
    small boxes near (0, 0, 24): template groups in their own
    coordinates?). Model triangles are not captured (meshsystem builds
    them, not RayScene_AddTriangles). Next: name that scene (its owner and
    flags), capture the model path, then diff Mako the same way.
32. **Instance copies' undeclared keys** ship in reverse template order:
    measured on atixref's 186 copied lights; the key list's head insertion
    (FUN_180ce08f0) is read, the copy loop that walks the template is not.
33. **Static props' ray triangles**: the light scene takes a prop mesh's
    trace data (meshsystem CMeshRayTrace, mesh +0x200); we build it from
    the render vertex and index buffers, draw calls then indices, which
    makes every atixref barn light exact. The code that fills the trace
    data (TraceDataForDraw_t, TraceVertex_t) is not read.
34. **World node triangle order** (settled 2026-10-03 for plain draws):
    the merger's output entry goes through the model compiler's
    CMesh_Weld at 1e-7 (FUN_18033f200 into 1803447d0; the node model is
    compiled as an in-memory ModelDoc), then AddDrawDescriptors' first-use
    renumbering, meshopt optimizeVertexCacheTable and optimizeOverdraw at
    1.03 (MeshoptOptimizers, ported from 1812b8c10 and 1812c4f30): every
    plain draw whose triangles come from one merger entry is exact in
    triangle order on probe01 (2), cardtest (3) and atixref (53)
    (DrawOrderProbe). Aggregate draws add meshopt 1.0's buildMeshletsFlex
    (255 vertices, 48 triangles, cone weight 0.15 when the material has
    AllowBackfaceCulling and is not DoubleSided, else 0) and
    optimizeMeshletLevel at 4, an odd meshlet padded with (last, last,
    last) (MeshoptMeshlets): all 44 aggregate draws from the merger exact
    on probe01, cardtest, atixref and ze_hold_em_p. Draws from lists the
    merger does not see take BuildNode's output entries, a per-cluster
    draw the subset of the entry's triangles in that cluster, in entry
    order (several entries appended in entry order): atixref 99 of 99
    plain and 383 of 384 aggregate such draws exact, 30 of 32 multi-entry
    plain draws, with the subset read from the shipped draw. Meshlet padding
    triangles are no entry's, so agg_nomerge draws (world space, no fragment
    transform) are BuildNode-sourced like the rest: 571 of 583 such
    aggregate draws exact. Prop aggregates (agg_prop, model space) run the
    same steps on the prop model's draw as meshsystem unpacks it, TEXCOORD1
    included (the weld keeps vertices that differ only there): all 446 of
    atixref's exact (PropAggregateOrderProbe), skins, materialoverride and
    smart prop material variables included. A per-cluster draw's subset is
    read from its real triangles (padding aside), and an overlay's triangles,
    which share their target's positions, belong to the entry with the
    draw's material. In all, 1,268 of atixref's 1,270 draws are exact. The vertices of every one of those 1,270 draws
    sit in first-use order of its final index list (optimizeVertexFetch,
    VertexOrderProbe); NodeDraw builds a draw that way. Open: two
    BuildNode aggregates (agg_nomerge inferno_trim_wood01_painted_blend_02,
    hr_concrete_wall_painted_001) whose first meshlets agree and later ones
    do not, on Valve's own entries. The render clusters themselves are
    settled (RenderClusters.Contributes, boxes and assignment exact on
    atixref's 2,031 block-light triangles).
35. **Aggregation on probe01**: four dev meshes (100, 101, 102, 107) share
    the other nine's material flags but miss `agg_merge`; the likely cause
    is the visibility mesh merger joining them first (GEOMETRY.md).
18. vrad3 trace cost law (performance only; not output).
19. Legacy-GUID particles in game (needs the user's go-ahead for a game run).
20. The vmat_c field order note ("likely NTRO positional"): the order is
    measured; the loader's reason is not read.

37. **Signed zeros in node entry normals** (settled 2026-10-03): a normal
    or tangent goes through two turns. HammerMesh_TransformToWorld rotates
    it by the node's matrix (Matrix3x4_Rotate, not renormalised: captured,
    178,068 vectors bit for bit, capture_meshturn.py); the builder DMX keeps
    the result; DmeMeshToCMesh's copy (180d46590) then rotates it by the
    identity it is handed, every zero +0 (captured, capture_vertexcopy.py),
    which gives a -0 component the sign of the other products, and
    renormalises (a zero length gives +0s). NodeMeshEntries.Turn: probe01 18,
    cardtest 20 and atixref 550 of 560 entries exact, the rest subdivided
    tangents (38). The old "zero times the translation" term was a stand-in.

38. **Tangents of subdivided node entries** (MapMeshCorners' baked path):
    positions, texcoords, paint, tint and normals of atixref's subdivided
    entries are exact, tangents only where the faces are flat. On displaced
    node 1370 even an original control corner's tangent is neither its
    stored tangent, nor that orthogonalised against the recomputed normal,
    nor rotated with it, nor a texcoord-gradient or MikkTSpace-style
    tangent of the baked triangles; on walls the plain carried tangent is
    right on one mesh (6778) and Gram-Schmidt helps another (1367). The
    patch tangent grid 1813c2090 is only reached from Hammer's viewport and
    sampling paths (18102f230, 1810c0d60). Nor is it CMesh_ComputeTangents
    (`MeshTangents`, exact on overlays): on Valve's own entry data it gives
    0 to 1,305 of each mesh's corners. 24 of 538 atixref entries differ by
    tangents alone. Next: a capture of the half-edge mesh's tangent
    stream after BakeSubdivisionForFaces and after ConvertMeshForBuilder.

39. **The vertex unpack and transform of baked props** (settled
    2026-10-01): WRB_LoadPropMeshes' floats come from meshsystem's unpack
    (180032730 for R32_UINT frames; R8G8B8A8 frames decoded and re-encoded
    to it at load), captured exact on all 819 atixref loader meshes
    (capture_propmeshes.py). CMesh_TransformByMatrix (1802b1ff0) then
    places them (PropTransform): all 102 atixref prop entries exact in every
    stream; the second texcoord is TEXCOORD1 where the model has one. The
    texcoord axes are shader attributes
    (TexCoordScaleByModelU/V = g_nScaleTexCoord*ByModelScaleAxis - 1).
    Open: the foliage static combo rule for NeedsLocalSpaceVertices.
40. **AllowBackfaceCulling with no declaration** (entry attribute bit 32):
    CMaterial2::GetBoolAttribute (materialsystem2 18000ba80) searches the
    material's attribute blocks and then a parent set (+0x60) not yet
    identified. generic, csgo_lightmappedgeneric, csgo_vertexlitgeneric
    and csgo_black_unlit declare no AllowBackfaceCulling in any vs, ps or
    psrs combo, yet answer true; csgo_water_fancy answers false. Measured
    on atixref, probe01 and cardtest (MaterialAttributes' NoCullDefault).
    Next: find what fills the parent set (materialsystem2's material load).
41. **renderwithdynamic on instance copies** (object flag 0x200): the 15
    atixref copies of renderwithdynamic meshes carry no 0x200, though the
    property has a setter (1810e9960) and the originals carry it.
    Measured (NodeEntryHeader.World's copy argument), not read; next: the
    collapse's node copy (FUN_180f60740) and which properties it carries.
42. **cardtest's teleport02 entry at the merger**: 0x160100000 at
    BuildNode:out (aggregatable, and our flags agree), but the merger's
    base-list input carries +0x1b0 = 0. atixref's eight zero-flag base
    entries are explained (overlays whose materials the content lacks: an
    empty attribute set over the overlay record's zero flags); this one
    is not. Next: CompileNode's code before the list loop (FUN_18026cac0,
    FUN_18027add0). The material is missing from the content: at collect
    time Valve gives it 0x160100000 and a 256x256 representative texture
    (so a light probe, 2026-10-04), which is none of core's error.vmat,
    editor/error_indicator.vmat or models/dev/materialforerrormodel.vmat;
    ours gives it the empty attribute set.
43. **Prop aggregate grouping and order** (WRBNode_BuildPropAggregates,
    180272ce0, read): models in CDefStringLess order, one entry per prop
    and mesh, buckets by entry +0x20 (one agg_prop model per material),
    Morton order of the prop's placed LOD bounds centre against BuildNode's
    closing bounds minimum, then a stable sort by mesh pointer. Measured
    on atixref (PropAggregateOrderProbe.Grouping): every fragment transform
    is our PropTransform matrix (4,446 of 4,450 bitwise), and 216 of 358
    multi-instance draws hold their instances in that Morton order. The
    rest (web joists, signs, wires, fences) come in several ascending runs:
    likely props with their own mesh copy (prop +0x100, 1802b1e30), whose
    order is the copies' heap addresses, as is the draw order itself and
    the bucket order (a hashed pointer; two compiles swap metal_door_001's
    _0 and _1). Not reproducible from data; ours must pick an order.
    The candidate test is WRBNode_PropCanAggregate (18026d7a0, read); a
    model with several LODs puts every LOD's meshes in, a fragment per prop
    and LOD with a LOD setup per prop; a smart prop's models come in with
    the element's material group as skin. NodePropAggregates builds all 171
    of atixref's aggregates from the .vmap: 446 draws exact in triangle and
    vertex order, every fragment set (PropAggregatesReplay); draw order
    138 of 171, fragment order 218 of 361. The origin search puts the best
    Morton origin at BuildNode's bounds minimum. Open: the 863 threshold's
    unit (CMesh +0x24), extra vertex streams, the texcoord stream test
    after the runs (180273xxx, V_stricmp "texcoord").

44. **Texcoord formats of node models** (settled 2026-10-03; read:
    1810dff20, 1810e7020, 180d473d0, 180d4a120, 180258310, 1802f6840,
    1802fc990). A map mesh is high precision (forceHighPrecisionTexcoords)
    when any texcoord of the mesh, after the island shift, lies outside
    +-1.03125, or Hammer/ForceHighPrecisionTexcoords is on; its streams then
    carry +0x1d (NodeMeshEntries.PreciseTexcoords: atixref 560 of 560
    entries against a capture of the flag). A prop's come from its model's
    geometry flag (float32 texcoords). An aggregate run (all of its render
    cluster models) with one flagged stream or one value past 16 is all
    flagged. A flagged stream is R32G32_FLOAT; the unflagged ones of a draw
    set (one vertex buffer) are R16G16_SNORM when all lie in [-1, 1], else
    R16G16_FLOAT (NodeDraw.TexcoordFormat). atixref: all 824 world draws
    (DrawOrderProbe, DRAWORDER_RUNS=1) and 472 agg_prop streams.

51. **Mako's vis clusters at assignment** (VisChainTests on mako3,
    2026-10-05): octree nodes and node boxes exact; clusters 24,960
    against Valve's 25,471, 23,083 boxes shared. About 1,450 of the 1,861
    boxes only ours have their centre in x -2048 to 6144, y -10240 to
    -4096, z -2048 to 0, mostly outside the voxel hints (which act through
    the octree only, see Resolved). Valve's extra boxes are often strips 24
    thick in y. Of Valve's 2,374 boxes not ours, 1,587 lie inside a bigger
    box of ours (331 of them differing on one face only, a merge step
    more) and 787 inside none, so we mostly merge further and sometimes
    cut otherwise. The merge's choices part, not a missing stage (nodes
    and node boxes are exact going in). Next: a merge-pass capture
    on Mako (capture_merge.py --passes), then VISCHAIN_MERGE.

## Resolved

- **Faces of instanced meshes in world physics** (ledger 49, settled
  2026-10-05 by capture). CMapMesh_ConvertMeshForBuilder runs RemoveBadFaces
  (rc 1810d9ca0): a face the polygon triangulator cannot cut whole on the
  mesh's own positions (1813a5ec0) is removed, triangles included
  (capture_heremovals.py: 6,111 = 873 error-model copies x 7 slivers on
  c2m2_fairgrounds_csgo_multi), and removal is swap-last (1813995a0), so the
  export walks the faces in that changed order. With csgo_unlitgeneric and
  csgo_static_overlay translucent only on blend modes 1 and 3 to 6, and
  folder-less materials read from the addon root for the collision side
  (physicsbuilder's surface read still logs them missing), world physics is
  whole-file exact on all 17 corpus maps, multi, prenodraw, preskybox and
  c2m3_coaster_d_d included.
- **A prefab the visibility manager hides** (2026-10-05, c2m3_coaster_d_d
  against Valve's compile): its contents still compile. Valve's lump carries
  hidden prefab 2's entities (hammerUniqueId 2:123 and on) and their 16
  brush models; the earlier rule that a hidden prefab encloses its contents
  was never measured. The entity walk, the mesh walk, the settle, the
  instance expansion and the entity models now all compile them: coaster's
  97 entity models exact (was 81). A hidden INSTANCE still places nothing
  (measured on Mako).

- **The merge sampler's rays** (settled 2026-10-05 by capture,
  capture_merge.py --vis-n --rays on atixref, VisMergeReplay and
  VisRayReplay RAYS_MERGE): CastRayGrid (18004a690) files a cluster's rays
  into the batch tracer together, so coplanar back-to-back faces tie as the
  packets decide; the scalar trace picked the other face, and TallyRays'
  facing test (18004bae0) then kept a hit Valve drops. With the batch
  (VisClusterSample.Rays, sharing the seed's NoDrawSecondLook) all five merge
  passes on atixref are exact (pass 5: 30,185 clusters).
- **Prefab name fixup** (2026-10-05, dkr_m2_carnival_d against Valve's
  lump): a prefab with fixupEntityNames gives its contents' names and name
  references "[PR#]<prefab node id>_" where the map-level fixup gives
  "[PR#]" (the prefab has no targetName and useTargetNameAsPrefix off; the
  targetName prefix and nested fixup prefabs are not measured and throw).
  Carnival's lump: 0 unexplained (light keys only); its 44 entity models exact.

- **RED2's surface_prop list** (ledger 50, settled 2026-10-05 by capture,
  tools/physics/capture_surfaceprops.py on dkr_m2_carnival_d). The list is
  CompilePhysics' symbol table at +0x458, filled at 18032ebd0 from each
  part's shapes' surface string (+0xf0) in part order, which is our piece
  order. "default" is named outright by every piece of a vertex-painted
  mesh with no surface of its own (toolsnodraw, unblended and missing
  materials alike): physicsbuilder's BlendSurfaces (180015080) seeds
  "default" for them. Unpainted meshes name nothing for such pieces, which
  is why listing missing materials broke fairgrounds. World physics exact on
  carnival_d, carnival_OLLLD and the three motel variants. Two side
  findings: the folder-less material rule belongs to physicsbuilder's world
  read only (ModelDoc finds addon-root files for entity models: c2m2's
  prefab func_breakables), and an entity model's file name goes through
  V_FixupPathCharToUnderscore for '.' and '+' after a leading "[PR#]" is
  stripped (180240a60; cs_script_demo's chess.q is chess_q_95).
- **Voxel word order in merged clusters** (settled 2026-10-05,
  VisPreMergeProbe on atixref.p2): FoldVoxelPairs (1800307d0) sorts both
  lists by leaf, ORs matching leaves and APPENDS the rest of the second
  list unsorted; the pre-merge's and AbsorbPair's results keep that order.
  Merge pass 0's entry is now identical on atixref (155,350 sets).

- **Outside detection on atixref** (settled 2026-10-04 by capture,
  tools/vis/capture_outside.py and OutsideCaptureTests, all 301,499 regions
  and verdicts exact). Three defects: regions are pushed in the build's
  depth-first order (a branch recursed into at its octant, before the
  octants after it), not slot order; the seed is GatherRays on the batch
  tracer (RayTraceEnvironment.Segments: packets per octant through the
  rebuilt kd tree), with Valve's direction and facing arithmetic, which
  settles ledger 30 (the scalar trace on the file's tree only agreed on
  maps without coplanar back-to-back triangles); and ClassifyRegion marches
  with MarchRay, whose OctantMask is called with the node's minimum as both
  box arguments, so the march meets nothing and every region the seed
  leaves undecided ends outside.
- **Subdivision vertex merge** (ledger 6, ported 2026-10-04,
  BakeMergeCaptureTests on c2m2_fairgrounds_csgo: all 71 baked meshes
  exact after the merge, positions bit for bit, vertex identities and
  half-edge counts). HalfEdge_MergeVertices: baked faces' vertices listed
  once in face order, clusters within 1e-6 ((dx dx + dy dy) + dz dz),
  pairs merged into the first (one boundary half-edge leaving each, inner
  faces different; boundary loops spliced, or two boundary edges zipped
  when one's far end reaches the other), sum times 1/count, up to ten
  passes. It matters to physics: the world mesh weld keeps vertices apart
  by id, so an unmerged pair is an extra vertex (fairgrounds' world physics
  is now whole-file exact).

- **Painted convex_multi pieces and world convex_multi groups** (ledger 2,
  settled 2026-10-04 by decompile). physicsbuilder's PhysicsBuilder_MeshPieces
  (180016230) hands each layer's mesh and an unsplit piece to the same
  node callback, so a layer is grouped as a piece is. A world mesh's groups
  are physicsbuilder's own (PhysicsBuilder_ConvexMulti 18001ac20 through
  1800d33e0 and 1800d39a0), not the map builder's brush entity grouping:
  a union-find over the CMesh vertex buffer joined by each triangle's
  corners, groups in order of their lowest vertex, each group's vertices
  in buffer order, a vertex no triangle uses a group of its own
  (BrushHulls.BufferGroups). Hull options equal convex_single's.
- **Physics simplification** (settled 2026-10-04 by decompile).
  ConvertMeshForBuilder hands physicsbuilder physicsSimplificationError
  from CMapMesh vf216: with physicsSimplificationOverride set, the error
  squared; else 0 when a material carries attribute 0x184fc5; else
  gameinfo's PhysicsBuilder/DefaultHammerMeshSimplification squared (CS2:
  0.0). Above 0 the piece is welded at 1/32 and simplified (1800c5b90,
  not ported; BrushHulls.RefuseSimplification stops such a build). None of
  the 227 installed maps sets the override.

- **Texcoord transform defaults** (ledger 9, settled 2026-10-04 from the
  compiled shaders' variable defaults, ShaderDefaultsProbe): a parameter a
  material leaves out takes the default its shader declares,
  g_nUVSet1 to 3 = 1 (not 0 as assumed), g_nColorOverlayUVSet = 2,
  g_vTexCoordScale* and g_vOverlayTexCoordScale = (1, 1), rotations 0, in
  csgo_environment and csgo_environment_blend (MapMeshCorners.KeepsTexcoords).
- **Static prop aggregates' meshlet cones** (ledger 36): the cone is
  computed before meshopt encodes the index buffer, and the codec may rotate
  a triangle's corners, which changes the cone's float sums. A capture of
  computeMeshletBounds (tools/vis/capture_cones.py) shows all 18 differing
  atixref meshlets are the buffer's triangles in order with some corners
  rotated, and MeshletBounds.Cone on the captured order gives Valve's cone.

- **World-layer instance copies' angles** (ledger 13): placed by the
  collapse's own matrix path (SettleWorld.BakedPlacement) since 09-28; the
  Mako lump test allows no angle difference and passes.

- **The .rte flag word and per-triangle id** (ledger 25 and 17): the id is
  the face material's resource id; the word's shadow bits come from the
  mesh's disableShadows or the material's DoNotCastShadows, 0x2 from
  F_RENDER_BACKFACES (GEOMETRY.md). All 316,054 triangles of probe01,
  cardtest, ze_hold_em_p, atixref and Mako match (TraceSceneTests).
- **Node model trees and meshlet descriptors**: WorldNodeModelTreesTests, all
  401 node models (GEOMETRY.md); static prop aggregates' cones settled since (above).

- **Subdivided meshes in the light scene**, settled 2026-09-30 from the
  decompile (`RayScene_AddFace` 1813ccd20, `MeshTessellation_PatchIndices`
  1813c3b60, `LightBuild_CapsuleLuminaire` 181296730,
  `LightSampler_CapsulePoint` 181299bc0).
  - A face is a polygon with its neighbours' edge points, or one unstitched
    displaced patch grid per corner (ENTITIES.md).
  - The capsule luminaire (omni2 shape 1, or 2 with caps) is ported; its
    sampler never scales by the radius, as the binary does not. Its area term
    groups (h + h) * (r * 2 pi), which the decompile's parentheses hide.
  - Measured: atixref from 22 lights off to 3 (6844, the capsule, exact);
    ze_hold_em_p's lights 133 and 136 exact.

- **The settle for sphere and capsule shapes** (was 23), settled 2026-09-29.
  - The narrowphase cores are oracle-exact.
  - The mesh contact, dispatch and time of impact were read from vphysics2.
  - The round shapes' placement was read from resourcecompiler
    (`PhysPart_AddSphere`, whose sphere centre skips the uniform scale, was
    confirmed in the disassembly).
  - `CMapEntity_SetStartAsleep` sets a key the source lacks in its class
    default's place.
  - s2c_settleround's lump is exact.

- **The .rte's geometry from the .vmap** (2026-09-28, decompile of
  resourcecompiler 0923 and measurement on five maps). An instanced mesh is
  placed as the bake's collapse leaves its copy (SettleWorld.Baked), a mesh the
  visibility manager hides or reached through a hidden node is left out, and
  WRB_EmitRteTriangles (1802821f0) drops a sliver: sorted float edge lengths,
  longest under 0.0001 or times 1.0001 over the other two. Every file triangle
  of probe01, cardtest, ze_hold_em_p, atixref (32,854) and Mako (279,064) is
  produced bit for bit, and nothing else.
- **Visibility hints** (2026-09-28, decompile of visbuilder 0923, checked on
  Mako). The .viscfg's visibility_hint entities: types 4, 5, 6 split lists,
  any other a voxel hint whose box, voxel and region sizes stop or force the
  octree's split (Voxelize 18002f890). Mako's octree with its seven hints has
  Valve's node count, 2,643,577. VISIBILITY.md, "Visibility hints".
  The voxel hint list (sampler +0x88 count, +0x90 entries of 0x20) is
  read only by Hints_AddVoxelHint, Voxelize and the sampler's reset
  (18003e040) among the functions from 180016000 to 18005b000 that take
  the sampler as their first argument (capstone scan, 2026-10-05). A
  reader reaching the sampler through another pointer is not ruled out,
  but nothing points to a hint acting past the octree.
- **World physics surface and attribute tables** (2026-09-28, decompile of
  resourcecompiler 0923 and physicsbuilder 0924). Surfaces register by name,
  case-sensitive, spheres, capsules, hulls, then meshes; a shape with no
  surface takes the part's "default". The RED2 surface_prop list takes each
  shape's own non-empty spelling once, merges spellings by their A-Z folded
  hash and writes the first spelling met, the count being the spellings.
  Attribute tags split on whitespace, "," and "|", sort by V_stricmp_fast
  (A-Z lowered, signed chars) and keep the first spelling of a repeat; a
  shape whose group is empty and whose lists parse to no tag is unset and
  takes attribute 0. Per-triangle materials: the low byte of each member's
  surface index, the array only once a member differs. Open: the two
  detail-layer lists in attribute matching.
- **Static prop overrides** (2026-09-28, decompile of physicsbuilder
  180153d40): collision_override names an entry of
  scripts/collision_properties.txt whose group and three lists (","-joined as
  authored) replace the body attribute's; surface_property_override replaces
  the surface by name. Measured on the probe map s2c_propover
  (tools/physics/prop_override_map.py, compiled by resourcecompiler): whole
  file exact, and "CSGO_Railing" finds csgo_railing, so the lookup folds case.

- **Map compile switches** (2026-09-28, decompile of hammer.dll and
  resourcecompiler). Hammer's assembler writes the fixed switches, then each
  builder widget's; resourcecompiler's parser keeps its own switches and types
  every other "-name value" float, int, then string (a bare one is 1).
  CompileMap reads ten builder names; -all or none selects every builder the
  game allows; -entities without -world is entities-only; nosettle skips the
  settle; a -fshallow build keeps the package and replaces the builders'
  outputs. -nolightmaps and -vpkincr change nothing.
- **Visibility root cube** (2026-09-28, decompile). The voxel sampler snaps
  the traced scene box to the grid, rounds the widest extent up to a voxel,
  takes the next power of two and pads each axis by half its shortfall; the
  shipped bounds of four maps follow.
- **cable_dynamic rendercolor** (2026-09-28, decompile and a probe map): the
  cable node writes its tint's red, green and blue as the string "%i %i %i".
- **Entity key defaults, masks and name fixup** (2026-09-28, decompile and
  the probe map). A flags key's default is the OR of its default-on choices
  ("%i"); tag_list and tag_list_dynamic default to their default-on tags
  joined with ","; a flags key redeclared at the same type merges the
  inherited choices after its own. spawnflags is masked to the declared
  bits at load. The name fixup applies to types target_destination,
  target_name_or_class, npcclass, filterclass and pointentityclass, and to
  targetname, skipping values starting "!*?@" and FGD class names (case
  blind). A key spelled "name" gets no default. snapshot_mesh sets
  snapshot_file from the found node's id. All 353 offered classes, bare and
  keyed, match Valve's lumps outside the light and probe bake keys.
- **Split pieces carry no tool material** (2026-09-28, decompile). The
  blend split builds new meshes with positions only and never names their
  material; physicsbuilder sets each node's tool material from its mesh's
  material name, so a split piece's is empty and its hash is 0.
- **Tool material hash** (2026-09-28, decompile and oracle). The node's
  tool material goes through FixupResourceName for "vmat" (absolute paths,
  a leading '/' and other extensions fail and hash as 0; a missing extension
  is added; separators, "./" and ".." are folded; A-Z lowered) and then a
  lowercase MurmurHash2 with seed 0x31415926. Checked against
  resourcecompiler's own functions on 4,048 names, and the tier0 path helpers
  against tier0's exports on the same names.
- **Soup tool hash and name** (2026-09-28, decompile). Members without
  triangles are skipped outright. The tool hash is kept when exactly one
  hash has triangles. The name joins non-empty names with "; ", is long past
  50 characters, and the next name seals it with "; ...".
- **KV3 block compression** (2026-09-28, decompile). resourcecompiler saves
  resource blocks with tier0's binary_auto encoding (the RED2 writer among
  them). tier0 sums the buffer (its trailer included) and the blobs: under
  256 bytes the block is raw, under 0x80001 it is LZ4, otherwise Zstd. This
  replaces the two bracketed cuts (raw at or below 256 on the buffer alone;
  Zstd above 512 KiB for PHYS only): a block of exactly 256 bytes is now
  compressed, blobs count, and any block past 0x80000 is Zstd.
- **Material list splitting and the water rule** (2026-09-28, decompile and
  oracle). The two collision lists are split with V_SplitString on "," with
  empty pieces dropped and nothing trimmed. A material whose tags contain
  "water" (A-Z folded) loses every ", window" with case sensitivity off: the
  argument is 0 in the binary, where the decompile had shown it as true. The
  tier0 helpers are checked against tier0's exports on 4,054 texts.
- **Surface table** (2026-09-28, decompile). The part's surface table is a
  list of name strings in first-registration order, matched with a
  case-sensitive compare (an empty name equals an empty one); a shape with no
  surface of its own inherits the enclosing one.
- **has_default_surface_property** (2026-09-28, decompile). Set when a
  physics shape node's own surface string equals "default", A-Z folded.
- **Painted convex_single pieces** (2026-09-28, decompile). Each layer's
  triangles become a positions-only mesh welded at 1/32, and every vertex of
  it is hulled on its own, named " [surface]" with no tool material.
- **Convex world hull input** (2026-09-28, capture and decompile).
  physicsbuilder hulls every vertex of the per-material mesh in its buffer
  order: a Frida capture of Mako's full compile shows the quickhull input
  equal to the mesh's vertex buffer on all 124 convex_single calls, and the
  port's input (corners joined by .vmap vertex) equal to it on all 106 it
  could place. A subdivided mesh arrives tessellated and positions-only:
  Mako's three pipes are subdivision surfaces (492 vertices each against 126
  .vmap vertices), which the port now tessellates as the mesh path does.
- **Stitched subdivided faces** (2026-09-28, decompile). The bake runs on a
  port of Valve's half-edge mesh (containers, edge split, add edge, arc-length
  split, grid write-back); c2m2's prefab and Mako are whole-file exact.
