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
   binary_auto, but some call sites pass plain "binary" or "binary_bc".
   Map each writer to the resource types it serves. The binary_auto
   encoder itself is settled (2026-10-07, tier0 1800c7220 and its node,
   typed array, string and LZ4/zstd helpers; Kv3V5Writer): every one of
   the 2,598 v5 blocks of the four corpus maps, and every sampled game
   model, material, particle and soundevent block, re-encodes from its
   tree to Valve's bytes with the compression chosen automatically, so
   no block measured so far uses another encoding. World physics
   containers re-authored from Valve's trees: 79 of 80 corpus files byte
   for byte (zero padding between blocks, not VRF's "S2V"; zstd workers
   from 1 MB of buffers and blobs; blob frames through lz4hc's
   LZ4_compress_HC_continue on tier0's 0x14000 ring). Open: s2port_doom's
   fix3_geo_all, one byte longer in its 33 LZ4 blob frames (likely an
   lz4hc version difference; which frame is not found yet).
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
    2026-10-07: they are what keeps ze_hold_em_p's 7 end_breakable entity
    models from byte parity: corner texcoord 0.47777778 (3EF49F4A, nodes
    244/245) comes out of Valve's island shift as BF05B05C, one ulp from
    any shift of the stored value (BF05B05B), so Valve shifts a value it
    recomputed. The faces carry textureScale, textureAxisU and textureAxisV
    face streams (dataStateFlags 0); the code that reads them is reached
    through the stream name tables at 183014f58/183015018/183015020.
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

56. **prop_ragdoll pre-settle pose** (from ledger 45). The compile's
    pre-settle of BasePhysicsSimulated entities (180f1e800, keys
    skipPreSettle and spawn_motion_disabled) poses ragdolls through
    18105b6c0 into CMapEntity +0x438, and ExportToLump writes it as
    boneTransforms ("name: px py pz qx qy qz qw;" per bone, 181004390,
    serializer 18174aeb0, gated by class metadata PosableSkeleton). Not
    ported. probe_classes' two prop_ragdolls have no model, so no pose.
    Valve specimen: ar_pool_day (5 lounge_chair_mattress ragdolls whose
    bones settle through their joints). Our probe: s2c_ragdollprobe
    (tools/physics/ragdoll_probe_map.py on s2c_rounds: chickens and a CT
    agent dropped and turned, plus skipPreSettle, Motion Disabled and Start
    asleep), waiting on a Valve compile with CS2 closed. Porting it needs
    the settle's joints (ragdoll constraints), which are not ported.

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
    The lists are a CUtlMap keyed by model at node +0x198 (walked in order
    by 1800e00b0), one vector of props each. atixref's standalone props
    (ledger 55) come out of the same lists in walk order.

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
    them, not RayScene_AddTriangles). Named 2026-10-06 (capture_rayscene.py
    now records backtraces): it is the prefab map's own world scene, built
    while its target world loads (rc 180f99af0, where
    prefab_has_runtime_entity_by_default is read) and instanced into the
    main scene at identity with owner flags 0, so a moved prefab's contents
    also sit at their prefab-local place. Whether the light trace sees it
    is open; Mako has no prefabs, so its 125 lights are something else.
    Mako's scene at the first light ray (capture_rayscene.py dumps every
    live instance there, kind 8; the add/move history alone misleads, as
    instance handles are reused): its map meshes are ours but for 16 meshes
    the visibility manager hides (Valve's scene drops them, ours keeps them
    under owner flag 0x20, which the light mask skips, so no effect), one
    cable_dynamic (20788, 600 triangles; cables are not in ours) and 4
    stray triangles. Prop triangles are not compared: meshsystem adds them
    (ModelRayScene_Build), not RayScene_AddTriangles. With meshsystem's own
    copy of RayScene_AddTriangles hooked too and the light mask (c00060b1)
    applied to object and triangle flags on both sides (the object's word is
    its scene's first dword, 181c10ea0): every triangle ours traces Valve
    traces, and Valve traces 491,083 more. Their scenes are built under the
    smart props' procedural update (rc 1810a0490 reads smartPropPartSet;
    180f71020 "UpdateDirtyProceduralObjects"), three containers of 12 to 23
    parts (82k to 178k triangles) and repeated models, plus a few through
    MapDoc_BakeInstances. So Mako's light scene lacks its smart props'
    geometry. Added (EditorTraceScene.SmartPropInstances: one container per
    smart prop at the node's transform, flags 0, its parts' model triangles
    moved into the node's space at the identity): 490,812 smart prop
    triangles now match, a few hundred a 1/64 key apart. Mako's lights
    differing went from 125 to 105, none worse; the rest are not the scene
    (only one cable and rounding remain there): shared triangles carry the
    same flag words, and only 15 back-to-back ones differ in winding. So the
    105 come from the trace or the sampling, not the scene. Next: capture
    one light's rays and hits in Valve's pass (light_omni2 385: Valve's box
    reaches x -5779, ours -5248, ours blocks more).
    2026-10-08 (dev box, current build): 127 lights differ in 475 keys; 19
    by under 0.001, the rest by a median of 48 units, and wherever a light
    is lopsided ours blocks more (18 of 18). Two traced: light_omni2 1099's
    top rays stop for us on mesh 8522 (an opaque hood, copies through
    instances 8510-8513 in world layer 31739) at z -2240; Valve's box tops
    out at -1280, a hit higher up, not an unblocked ray. light_rect 32719
    (world layer 31647) stops for us on meshes 8880 and 18293 of its own
    layer. All three are in Valve's live scene at our transforms with owner
    flags 0xb0000, their materials opaque (csgo_simple, flags 0), their
    triangles facing the rays as the face loops do; neither the lights'
    shadow keys nor the instances' transform updates set them apart, and
    leaving world-layer content out makes 14,116 keys differ. So Valve's
    rays pass geometry ours hits: the capture of one light's rays is next.
    2026-10-09 (capture_lightrays.py on the 1009 build, per thread: the
    light pass traces on several threads, and a shared hook state had paired
    one thread's rays with another's results; LightRayProbe.AgainstCapture):
    our rays are Valve's (every start bit for bit, every direction), and
    Valve's hits are ours: light 1099, 166,103 of 172,032 rays hit at the
    same distance and 5,888 pass on both; light 32719, 146,748 and 707.
    What differs is 41 and 1 rays where ours hits nearer: a triangle Valve
    holds (same flags, same place, EditorSceneCaptureTests RAYSCENE_OWNERS),
    hit from the front, within about 1% of one of its edges (one grazing ray
    aside), and behind it the very triangle Valve reports. Bounds come from
    extreme rays, so these few are the lights' differences ("ours blocks
    more"). The cause is the traversal: Valve's ray scenes walk their own kd
    trees, which crack at node boundaries, where our padded BVH never
    misses. Next: port RayScene's kd tree build and traversal (rc
    RayScene_AddTriangles and the 181c0a000 caller), then capture one
    mesh scene's tree to check the build.
    2026-10-09, ported (EditorTraceScene.Packets): the light pass traces in
    packets of four. RayBatch_Add (181c1bf10) files a segment by its delta's
    sign octant (dx < 0 bit 0, dy bit 1, dz bit 2) and traces four at once;
    RayBatch_Flush (181c1c530) pads a short packet with its first ray. The
    instance tree (built by 181c1ae90, one instance per leaf, boxes the
    TransformAABB of each scene's box) passes the whole packet when any lane
    meets a node (181c15d00: exact 1/d, start < far, near <= far, near <
    range). In an instance's space, lanes whose direction sign bits (so -0
    is negative) differ are traced octant group by group (181c02a20), each
    sub-packet keeping every lane's origin and range and filling the other
    lanes' directions with its first lane's; a scene of scenes then runs its
    own instance pass with the whole packet. Mode 2 (181c0a000) is the
    visbuilder's kd walk (TracerKd, now IKdTriangles): every lane tested
    against each visited leaf (t < best, best starting at 1e23, no range
    check), a 256-entry mailbox, back faces culled unless flag 2. A lone ray
    is a packet of four copies. Result: light 1099 differs on 1 ray of
    172,032, 32719 on none; Mako's lump from 105 lights (349 keys) to 9 (25);
    atixref (6 to 0), s2c_prefabprobe3 (5 to 0) and 4 (8 to 0) exact. The kd
    walk alone (one ray at a time) fixed nothing (104 lights) and a per-ray
    box test made it worse (110): the packets are the rule. Then
    Light_AtInfinityTest (181296a50) ported: SampleVolume (180f19f20) skips
    a light only when its w (+0x9c) is 0 and its clip box (181292560)
    reaches nexttowardf(-262144, 0) and nexttowardf(262144, 0) on every
    axis; an orthographic barn (size z 0, w 0, finite box) is traced
    (light_barn 9479 exact). Open: Mako's 8 (light_rect 8385/8386 differ
    widely; the rest are OBB ties and 1-2 ray bounds), and ray 4:215 of
    1099 (we hit smartprop 33582's model, Valve passes; 1099's keys are
    exact regardless). Then the kd leaf size (capture_scenekd.py: Valve's
    tree of mesh 9437's scene, 9 nodes against our 27): RefineNode
    (181c16b90) splits a list past 10 triangles, or past 32 when the scene's
    own flag word holds 0x10000. That word is not the owner flags we mask
    with: a mesh's scene starts at 0x30000 (MapMeshGeometry_BuildRayScene
    1813ce820) and reads 0xb0000 plus the owner bits in the light pass; a
    static prop's mesh scenes carry the prop's 0x80b0000 word (survey of
    all 9,948 Mako scenes). Then the smart props (capture_scenekd.py --bounds
    survey records every AddInstance and SetInstanceTransform, and hooks
    meshsystem.dll's copy of the setup): the container sits at the node's
    path times its AngleMatrix, no scale (sin/cos rounding: -4.37e-8, 1);
    each placed part is an instance by the part's transform (scales
    included) of a part scene (word 0, no triangles) that holds the model's
    own scene at the identity, built by meshsystem.dll (word 0x90000, so
    leaves of 32), its triangles in the model's space. Result (2026-10-09):
    Mako's lump exact (105 lights to 0), the seven captured lights equal
    Valve's on every ray bit for bit, atixref, prefabprobe3/4, doom and
    propover exact. Left: light 1099 has 130 of 172,032 hits off in the
    last bits on map meshes 25406 and 25395 (no key changes). Their world
    matrices are Valve's bit for bit; the cause is corner order: in those
    meshes (and 27237) every triangle of Valve's starts one corner later
    than ours, (B, C, A) for our (A, B, C), and the record's rounding follows
    the order (mesh 9437's start where ours do). Where RayScene_AddFace
    (1813ccd20) picks a face's first corner is not yet read. meshsystem's
    model-space corners also differ from our render triangles by an ulp in
    some triangles (CacheOptimizedTriangle_FromCorners is
    instruction-identical in both DLLs), which moved no captured ray.
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
    2026-10-05): the octree's 2,643,577 nodes and their boxes exact (each
    node's cluster words differ with the clusters); clusters 24,960
    against Valve's 25,471, 23,083 boxes shared. About 1,450 of the 1,861
    boxes only ours have their centre in x -2048 to 6144, y -10240 to
    -4096, z -2048 to 0, mostly outside the voxel hints (which act through
    the octree only, see Resolved). Valve's extra boxes are often strips 24
    thick in y. Of Valve's 2,374 boxes not ours, 1,587 lie inside a bigger
    box of ours (331 of them differing on one face only, a merge step
    more) and 787 inside none, so we mostly merge further and sometimes
    cut otherwise. The merge's choices part, not a missing stage (the
    octree and its node boxes are exact going in). Next: a merge-pass capture
    on Mako (capture_merge.py --passes), then VISCHAIN_MERGE.
    2026-10-06: with the seed reading folded flags, generation's 751,270
    sets match but 25 (VisPreMergeProbe on mako.pm), one strip where a solid
    voxel row splits a leaf. capture_merge.py --gen-box --vis on two of them:
    sampler and all 226 prices exact; Valve stops at two clusters because
    MergeLoop (180034e40) also puts the 56 shell boxes in the candidate tree
    (payload n + i, no cluster), so a cluster beside the shell finds
    something and never widens its query. Ported (VisMerge.Selection);
    both leaves exact. 2026-10-07 (VisChainTests, VISCHAIN_MERGE=mako.p on
    that capture's own scene): generation, the pre-merge and all five merge
    passes now equal Valve's set for set and cluster for cluster, pass 0
    751,270 sets / 1,928,875 clusters to pass 5 13 sets / 25,471 clusters.
    Open: assignment, the scan and the VXVS, which need a capture of one
    compile end to end (capture_pvs.py).
    2026-10-09 (capture_pvs.py on the 10-06 build, VisChainTests on the dev
    box from Valve's .rte, 5.4 hours): assignment (3,586,348 entries,
    2,643,577 nodes and boxes, 25,471 cluster boxes), the scan (25,473
    matrix rows), the vis-cluster merge (4,096 clusters, entries and boxes)
    and the borders (1,176,083 entries, consolidated entries) all equal
    Valve's. The VXVS too (VisChainTests.SavedAgainstShipped on that run's
    <map>.ours.vxvs): 6,561,250 bytes, byte-equal to the shipped vvis_c of
    both full compiles in %TEMP%/gt. Mako's vis is exact end to end.

52. **The settle's dynamic order** (settled 2026-10-05: Valve's own output
    varies). MapSettle_Run (rc 180f1e490) takes its nodes from PhysDoc_GetNodes
    (180f4c360), an open-addressing hash table walked in bucket order (0x28-byte
    entries, hash +0, node +8; a 64-bit mix of the node pointer), and bodies
    turn dynamic in that order. Heap addresses differ run to run: two settle
    captures of c2m3_coaster_d_d share no pointer and settle 3:950 to 3:952
    differently (the corpus lump matches the second). So a stack of props
    that depends on the order has no single right answer and no compiler
    can match a given run from the map. Where our settle world is exact body
    for body and Valve's captured order settles exact, those props are a
    documented gap (EntityLumpAgainstValveTests.UnstableSettle): coaster
    3:950 to 3:952 (3,041 bodies exact), dkr_m4_ferris 3:2251 (7,887 bodies
    exact, 260 of 260 in Valve's order), dkr_m2_carnival's 12 (7,655 bodies
    exact, 195 of 195 in Valve's order) and dkr_m5_stadium 3:1704 (our order
    settles all 110 as a fresh compile did; the corpus compile differs).
    capture_settle.py --settle-only stops at the settle (25 s a capture).

53. **dkr_m3_tunneloflove's settle** (settled 2026-10-06 by decompile and
    capture). A model with a bind pose for each part (counts at +0x58 and
    +0x78 equal) takes the settle's matrix path whatever its scale (rc
    FUN_18105dbd0, 18105e476 to 18105e4d0): Invert(B[0]) Concat (Scale(s)
    Concat B[part]), the shapes' points through it and cooked again. For
    coaster_car's single part that is the identity rounded, which moved its
    bounds' max z one ulp and its inertia off-diagonals. The world is now
    exact body for body (8,005), and Valve's captured order settles all 95
    exact; 3:2151 to 3:2153 are order-sensitive (ledger 52).
54. **World node back end from the .vmap** (2026-10-07, read: CompileNode
    180277ab0, BuildAggregate 180268e80, Step_SplittingMeshWith 180260710,
    the overlay list 180271160 / 180271110 / 180271780). Settled and
    exact on probe01 (all 3 node models' trees, both index buffers, the
    .vwnod and .vwrld): an aggregate's fragments are its CMeshes welded
    (CMesh_Weld with 0), sorted by CMesh +0x58 (0 for map meshes), vertex
    count descending, then memcmp of the vertices; one draw each. A
    meshlet's m_nTriangleCount leaves out its padding triangle. A material
    whose INSG has m_depth_elems splits the draw set into a depth buffer
    (the streams those name) and the rest; otherwise one buffer and depth
    index 255. Streams lay out in mesh order: read by the material's
    signature they take its D3D and shader semantics, else their own name.
    The world node's bounds are BuildNode's output. Overlay lists group
    entries per call by render order, flags & 0x80 and attribute bit 23
    (bit 1 makes a closed group); aggregatable groups (UseAggregateInstances,
    order 0, bit 23 clear, every entry CanAggregate) go through
    GroupAggregates as agg_overlay; each group with entries left becomes
    mesh_[ns]overlay<group index>. Open: the instanced list's
    vf 0x38 (18026ed20) groups by entry +0x40 (node id) and +0xa8 (not
    modelled) through a hash map whose slot order numbers the nomerge%d
    objects; .vwrld m_nCompileFingerprint changes per compile.
    Brush entity models (2026-10-07, atixref 56 of 58 render models and
    86 of 86 physics-only exact, ze_hold_em_p 8 and 24): one embedded mesh
    per drawn material piece (hammer_mesh_<piece>, numbered on when taken),
    in entity space, tangents recomputed (preserve_tangents 0), Hammer's
    texcoord precision flag over every face of the mesh, nodraw included.
    FUN_1802f9cc0 reads embedded_map_mesh and create_pooled_vb: the depth
    split needs a map mesh that is not pooled. SNORM texcoords truncate
    (6863, not 6864); an index buffer stays raw when the codec output is
    not smaller. The OptionallyCompressedTangentFrame normals once
    listed as open here are not a rule: atixref's reference was compiled
    on 09-24, before the 10-01 to 10-06 updates recompiled materials, and
    no current material, shader or DLL carries that semantic (only
    assetpreview.dll, as a baked 32-byte name). atixref needs a fresh
    reference before its node models can be scored.
    The whole -world package (compile-map, WorldNodeFiles; 2026-10-07):
    probe01 and cardtest matched Valve's in every tree and container fact
    but the manifest hashes and the per-vertex lighting; both are settled
    in ledger 55 (the vvis's fingerprint is still uncaptured). Settled on
    the way: a full build's lump lists its resource_name values, sorted,
    as RED2 weak references with UsesBuilderEntityKeys 1; a map's child
    resources list RERL by path; a node model whose material the game
    lacks has compile_warnings 1. Prop aggregate fragments' flags come
    from BuildAggregate: +0x20 with attribute bit 17, +0x40000 with bit 19
    or with 16 or 18 while 17 is clear, +0x800 for a LOD'd mesh. The
    depth split (FUN_1802f5680, FUN_181364720, AddDrawDescriptors
    1802ed7f0): with SplitDepthStream, an embedded map mesh that is not
    pooled and a material whose INSG has m_depth_elems, the fields its
    depth list reads (matched by semantic, per-vertex lighting as color 1)
    go first and the rest of the main list's second, in mesh order; one
    buffer when either side is empty. There is no Dummy condition (an
    earlier reading fitted the stale atixref reference). The field order
    puts texcoords before normal and tangent (baked props stream P N Tg T0
    and lay out P T0 N on ze_hold_em_p; the code that orders them is not
    read). Settled after: the
    Linux build parted from Windows for two reasons. Float sin/cos went
    to the host libm (now Simulation.CrtMath, tier0's FMA3 sinf/cosf,
    exact against the Windows CRT on 3.68M arguments). And rcpps/rsqrtps
    estimates differ by CPU vendor, so Valve's own vis is the compiling
    CPU's: the references come from a Zen 4, and S2C_ESTIMATE=zen4
    (CpuEstimate, tables exact over every float) lets an Intel box
    reproduce them. With both, probe01 -world on the Intel dev box is
    byte-equal to the Ryzen's.
55. **Partial builds: the world step without vis or a bake** (settled
    2026-10-07 from the 0923 decompile, checked on the fresh 10-07
    references). Open: whether a no-vis -fshallow build drops a
    world_visibility left by an earlier vis build (compile-map keeps it).
    - Builders: CompileMap's flag table (1801f7840) lives at context +0x609;
      its return, whether any builder was named, at +0x607.
      CWorldRendererBuilder::Build reads vis through vf29 (1801f9ac0,
      +0x609 + 2). Without it there is no RTE unless baking, no vis
      builder, no world_visibility.vvis_c.
    - The bake: context +0x605, set in 1800fd550 when gameinfo's
      DefaultMapBuilders/bakedlighting is on (CS2: on). Its value is
      (not partial, or bakelighting) and not nobakedlighting. Hammer
      writes -bakelighting from its Baked Lighting box, so -world alone
      (Fast Compile: -world -phys -nav) does not bake.
    - Hammer's dialog (hammer.dll 1800d6760 and its siblings): Build world
      writes -entities when Entities Only is ticked, else -world; then
      -deformables, -nosettle (unticked), -tileMeshBaseGeometry,
      -debugvisgeo, and -skipauxfiles or -rebake_surfacegraph. Physics,
      vis and nav write -phys, -vis and -nav.
    - CompileNode without vis (180278555): with
      VisibilityGuidedMeshClustering it clusters by the vis builder's flat
      clusters when there are more than two. Otherwise it uses the node's
      box grid (180283520, RenderClusters.Grid), one box per cluster with
      an empty mutual-visibility list, and merges when the grid has more
      than two boxes. The same grid makes the render boxes when nothing
      else does (180278b57).
    - A render object's meshes are one draw per material and vertex
      layout. Draws whose input layout matches share buffers: one vertex
      and index buffer set each, blocks interleaved (probe01's cb_mesh:
      without and with PerVertexLighting).
    - Per-vertex lighting is not the stream's (1, 1, 1, 0). The .vmap keeps
      the last bake's values in a CDmeNodeInstanceData per mesh instance,
      named by the node path (instance ids then the mesh's, joined by
      '.'). Its vertexLightingData holds one RGBA8 per face vertex. The
      map load puts it into PerVertexLighting as byte / 255 (180f0ea90),
      so every build ships the previous bake's values. This replaces
      ledger 54's "applied by Hammer" reading.
    - RED2 hashes (capture_overrides.py on probe01 and ze_hold_em_p):
      n0.vrman's m_nFileCRC is the CRC32 of its saved text with
      backslashed, escaped paths and CRLF. world.vrman's
      ___OverrideInputData___ fingerprint is the CRC32 of the same text
      with LF, listing world.vwrld, world_physics.vrman and
      world_visibility.vvis (even when no vis is built), written as
      uint32.
    - Result: probe01's -world package is Valve's byte for byte bar
      .vwrld's timestamp and fingerprint. cardtest differs only in ledger
      42's teleport02. ze_hold_em_p differs in three light aggregates' MDAT
      and the end_breakable models' texcoords (ledger 10).
    - Open: atixref -world against its fresh 10-07 reference has 256 of 553
      files identical. Aggregate membership and numbering differ (_0/_1
      swapped), and blocklight objects come out with more buffer sets than
      Valve's. So either the grid merge or the per-layout set rule is not
      Valve's on a big map.
    - Settled 2026-10-08 (atixref, dev box): the static props left out of
      aggregates and not baked ship as scene objects of their own model
      after the cluster objects, 65 on atixref, model by model in ordinal
      path order and, within a model, in walk order: the prop's matrix,
      render colour / 255 as tint, skin (default when unset), fades, LOD
      override, flags 8 | 0x400 (no baked bit) and the light probe / cubemap
      handshake of the volume holding its bounds' centre. A prop whose model
      does not load still ships, with handshakes 0 (atixref's 14 curb
      corners and nuke_machinery_05). All 65 records equal Valve's, in
      order. The node bounds (WRBNode_ComputeBounds, map meshes then every
      prop with bounds, once at the start of BuildNode) are Valve's too
      (.vwrld node bounds equal). What remains is the clustering after the
      grid: we make 148 cluster objects to Valve's 129 and split materials
      into more aggregates (hr_metal_trim_002: 14 against 3).
    - Settled 2026-10-08 (decompile, with the 10-03 BuildNode and render
      cluster captures): atixref -world now has Valve's 553 files, every
      name equal, 344 byte-identical (from 256); the other three corpus
      maps unchanged.
      - Render boxes: with the merge leaving none, CompileNode builds them
        from the layer's entries in BuildNode's order: world and prop
        entries, then the overlay meshes, every one whichever list takes
        it (toolsskybox, toolsnodraw). Our 88,857 centroids equal the
        captured call's in order and the 20 boxes are Valve's.
      - PrepareForMerge (1802b52d0) runs only in the vis merger branch, on
        the merged lists. Other lists reach GroupAggregates with the
        CMesh's unfiltered streams (VertexPaintTintColor, a second
        texcoord, VertexPaintBlendParams).
      - GroupAggregates (18026f590): a stable sort (18028f2c0) by the
        instance-stream kind, bits 0x6000000000 and 0x100000, the CMesh
        name (the material path, V_stricmp), then the stream layout hash
        (1802b6130) as a signed int. Then each same-name group is promoted
        (texcoord 180258310: one flagged or past 16 flags all; tangent
        180258450). Runs compare name, bits, kind and every stream field
        (+0, +8, +0x10, +0x14, +0x18, +0x1c, +0x1d). A lone run aggregates
        when its CMesh has more than 0x360 indices (+0x24), not vertices.
      - The instanced, no-split and baked prop lists have an empty vf 0x28
        (1800d3020): they stash only whole (node, +0xa8) groups (vf 0x38),
        so a group with one mesh that cannot aggregate (atixref's node
        6142, atix_har without bit 30) is one object.
      - The node lists its aggregates in model name order (all 264 of
        atixref's, ze_hold_em_p's 7). Measured; where Valve sorts them is
        not found.
      - Open: 95 agg_prop models differ in the NORMAL word's tangent angle
        on a few vertices each: Valve encodes 180 degrees (the NaN path:
        the dot product past 1) where ours gives 0. The encoder's order
        matches (182067e80), so the source tangent differs in its last
        bit. Also open: some agg_nomerge MDAT and cluster objects' buffers.

57. **Whole-package parity and Valve's own determinism** (2026-10-12,
    ParityReport: compile-map into an empty package, scored file by file
    against Valve's package; S2C_WORLD_STAMP gives the .vwrld Valve's
    compile time and fingerprint). Two full compiles of probe01 on the 1009
    build differ only in the world stamp and the .nav (9,580 against 9,590
    bytes, from byte 5,421 on: the AI data), so the lightmaps are
    deterministic on one machine and the nav is not; the scorer reports the
    nav apart. In a full build lightmaps are about 95% of probe01's bytes;
    the CS2 bomb damage file (sacustomdata, sampled from the nav mesh) is 55%
    of a no-lighting build of a map with bombsites. Without lighting, on
    the 1012 references: probe01 and cardtest exact but for the bomb damage,
    the nav and the .vmap_c; ze_hold_em_p 86.9% (entity model texcoords and
    the .vmap_c left).

58. **Draw call UV density inputs** (settled 2026-10-12 by capture,
    capture_uvdensity.py on ze_hold_em_p): 1812d9920 reads the first
    "position" field of 3 floats and the first "tex" field of 2 from the
    float vertices and indices AddDrawDescriptors hands it; those are ours
    vertex for vertex, but the triangles are the welded list before the
    meshlet build, which starts about 5% of an aggregate's triangles a corner
    later. The density is taken there (NodeDraw.Result.DensityIndices): all
    of ze_hold_em_p's world nodes exact, 16 more of atixref's.

59. **Vis-merged render objects** (settled 2026-10-12 by decompile,
    18026e9c0): the merger's object copies its entry's +0xb0, +0xb4 (fades)
    and +0x80 (the node's origin) into the render object's +0x38, +0x3c and
    +0x68, the scene object's m_flFadeStartDistance, m_flFadeEndDistance and
    m_vLightingOrigin; the cluster compile leaves 0, 0 and FLT_MAX.
    cardtest's no-lighting world nodes exact.

60. **Open, 2026-10-12:**
    - Brush entity model texcoords (ze_hold_em_p's end_breakable models,
      12.5% of its bytes): some V values are 1 or 2 ulps from ours in both
      directions. Valve's ConvertMeshForBuilder output is ours bit for bit
      (capture_texshift.py, after HammerMesh_ShiftTexcoordIslands), so the
      change is in the entity model compile; not a re-projection from the
      face's texture axes nor an average of merged corners (tested).
    - Overlay tangents (atixref, about 30 small models): the projection is
      exact (OverlayCaptureProbe.WholePass, 356 of 356), but our packed
      NORMAL word holds a default tangent (angle 512, sign set) where
      Valve's varies; the computed tangent is lost between projection and
      packing (skipping the stream filter for overlays makes it worse).
    - The .vmap_c's m_SubassetReferences order: neither the lump order, the
      walk, node ids, document order, FGD order nor a plain hash (Murmur2,
      CRC32, FNV-1a by bucket) of the class name. The entity walker is
      180f25de0 (vf 8 "entity", vf 0x20 "soundevent").
    - Prop aggregate draw order (atixref, 1.75 MB): ledger 43.
61. **KV3 unsigned zero and one** (settled 2026-10-16 by decompile and
    Valve's own output; tier0 20261015, CS2 1.41.9.1). The binary reader
    1800c4120 now takes types up to 27: 26 and 27 need format 5 or later and
    make an unsigned 0 and 1 (1800be890, basic type 4 with subtype 0x17, as
    UINT32 = 12 does); 15 and 16 stay signed. The writer's node type chooser
    1800c5f60 gives a uint 12 when it fits 32 bits, else 4, and with its
    compact flag (its one caller, 1800c6ef3, passes 1) a uint 0 or 1 is 26 or
    27; the 20261006 build wrote it as 15 or 16. Packages keep their sizes
    and differ by a few type bytes in every file. Kv3V5Writer with the rule
    re-encodes all 348 v5 blocks of Valve's 10-15 probe01, cardtest and
    ze_hold_em_p compiles (103 with the old one); a tree read from an older
    file holds those zeros as Int64 and keeps its bytes. Our authors had
    written unsigned 0 and 1 as Int64 to get the old bytes; they now author
    every unsigned field as UInt32 (RED2 fingerprints and user data at any
    value, physics flags and indices, draw call buffers, meshlets, aggregate
    offsets, node and world lighting info, entity spawnflags), which is what
    every one of Valve's 10-15 trees decodes them as. Against the 10-15
    references (%TEMP%/nolight_1016, no lighting): probe01 and cardtest exact
    in every file we write, ze_hold_em_p 86.8% (ledger 60's texcoords).
    Not seen: a typed array of unsigned zeros, whose element type could
    follow the new rule (none of the 348 blocks needed it to differ).
62. **Lightmap charting of a mesh** (settled 2026-10-17 by decompile and
    capture; 0923 18022d920 with 18022c740, 18022d6e0, 18022d1e0,
    18022f610; tools/vis/capture_charts.py, LightmapChartsTests with
    CHARTCAP). Charts grow from each unvisited triangle in order through a
    max-heap of edges keyed by the two normals' dot product (equal keys
    rise); a neighbour joins when its scale is within [2/3, 3/2] of the
    chart's first, both uv areas are non-zero and it does not fold over a
    chart neighbour across their edge (mesh uvs, UnwrapUsingUvsFromMesh
    default true). Each chart splits its vertices by (vertex bytes, chart
    uv), and its uvs become (uv - min) * scale * sqrt(world area / uv area)
    * the chart's max scale; the triangles come out chart by chart.
    Lighting/LightmapCharts.cs gives Valve's output bit for bit on all 80
    chart meshes of probe01, cardtest and ze_hold_em_p (48,368 triangles,
    2,225 charts). Not ported: the box projection path (no mesh uvs), the
    aspect away from 1 (no specimen).
63. **Lightmap atlas packing** (settled 2026-10-17 by decompile and
    capture; 0923 NUVChartPacker::PackCharts 1802272d0, InitializeChartUVs
    1802235d0, TryPack 180226370, the group order 180225020 with TinyBVH
    181ed77b0 / 181ed64a0; tools/vis/capture_packer.py, LightmapPackerTests
    with PACKCAP). Debug colours come from SFMT-19937 seeded 0x777f17e, its
    state words read untempered, hue to sRGB through tier0's powf. Each
    chart's rotation is the longest run of near collinear boundary edges
    (dot >= 0.99) in CUtlHashtable slot order, then -theta or pi - theta.
    The charts are rotated, scaled, padded to 4 and sorted (unflagged first,
    big first, by luxels, then index); TryPack rasterises each into
    per-offset 8x8 block masks, dilates by the gutter and places first fit,
    single luxel charts and lines last. A group of more than 16 single luxel
    or 4 line charts takes the primitive order of a TinyBVH built over the
    charts' 3D bounds: binned SAH with 16 bins, c_trav the group limit,
    c_int 1, in-place partition. Lighting/LightmapPacker.cs and
    Lighting/TinyBvh.cs give Valve's setup, every uv pass, every attempt's
    placements, the chart records and the atlas uvs bit for bit on probe01
    (71 charts) and ze_hold_em_p (2,083 charts, attempts 1 0 1 0).
    Not seen: TinyBVH's threaded build (50,000 primitives and up), its
    256-entry stack overflowing.
64. **The lightmap's chart meshes from the .vmap** (settled 2026-10-17 by
    decompile and capture; 0923 BakeLightMaps 18025db60, BakeLighting
    180217740, GenerateCharts 180210fa0, the chart job 180212850,
    PrepareChartMesh 180210730 with 18020f910, 18020e6a0, 18020efc0,
    PackCharts 180214960; tools/vis/capture_nodeentries.py --full and
    capture_importance.py, ChartMeshesTests). In a baking build every
    world entry's +0x98 is set unless the mesh says bakelighting false, and
    the record's +0x98 then clears NeedsLightProbe (attribute bit 20).
    After the T-junction fix (BuildNode's 1/32 weld comes after the bake)
    BakeLightMaps strips PerVertexLighting from every entry and takes those
    with no 0x2000000909, bit 29 and +0x98; BakeLighting sorts them with
    std::sort, more vertices first, then memcmp of the vertex floats. The
    importance volumes are the toolslightmapres entries' vertex positions
    (180257b50, before they leave the node). Each mesh loses LightmapUV, is
    welded at 1/32 unless lightmapNoWeld (each welded vertex keeping the
    last input vertex that became it), flips away-facing triangles of a
    RenderBackfaces material, takes per-triangle importance scales
    (transition region 120 in CS2), and gets its corner neighbours from
    vertex ids keyed by position bytes, normal within cos 1 degree (V_cosf,
    0x3f7ff605) and the texcoord within 2^-11, normals split since CS2's
    LightmapChannels has no directional_irradiance_sh2_dc. PackCharts
    rotates and renormalises normals and tangents (SSE Matrix3x4_Rotate),
    scales each vertex's uvs once, and flags the chart records. The atlas
    is lightmapMaxResolution (4096) at mip 1 (MinSpecLightmapSize 4096)
    with 4 passes (lightmapVRadQuality 0). Lighting/ChartMeshes.cs,
    ChartMeshPrep.cs, PackingAssembly.cs and LightmapSettings.cs write
    lightmap_packing_geometry.dat byte for byte as Valve's on probe01 and
    cardtest (15 chart meshes, 434 vertices, 71 charts) and ze_hold_em_p
    (50 chart meshes, 40,662 vertices, 2,083 charts); the vertex ids and
    corners match all 80 captured meshes of the three maps.
    A baked prop's entry takes +0x98 from the prop record's +0x17c
    (180245770): bakelighting 1 or 0 decides, -1 (the default) means the
    model's prop_data bakelighting (true when absent) and any of its
    materials with SupportsLightmapping; its +0x9c is V_powf(2,
    lightmapscalebias). A map mesh's CMesh +0x184 is V_powf(2, the face
    set's lightmapScaleBias) (DmeMeshToCMesh 180d47b90), and the chart
    scale is +0x184 times +0x9c (ze_hold_em_p's two bias-1 meshes chart at
    2). Its props are unique entries (no +0xc0 bit 4), in world space.
    Not ported: GenerateCharts' shared-mesh and instance path (1802191e0,
    1802104c0, 18020f690), which no measured map takes (ChartMeshes.Build
    throws); the tier scale for a CMesh's +0x58 bits (182a8cc50, none
    set); GJK's bits for a scale strictly between 0.01 and 1
    (ConvexDistance is exact geometry; none on these maps); where the
    builder's requested size (+0x2f8) and the map's lightmap settings come
    from (absent on these maps: the defaults).
65. **lightmap_info.dat** (settled 2026-10-17 by decompile; 0923
    RasterizeAndSetupTexels 1801f32f0, the triangle rasteriser 1801f5020,
    RunVrad3's writer in 1801f2240; ChartMeshesTests.LightmapInfoIsValves
    with LMINFO). Triangles in index order, atlas uvs times the packing size
    (the atlas over 2^(mip - 1)); a chart small in both axes (flags bits 28
    and 29) takes the luxel at its first corner, any other is rasterised
    conservatively: counter-clockwise, luxel centres tested against the
    three edge functions, each pushed half a luxel outwards along both
    axes, in Valve's operation order. Each packing luxel covers a
    2^(mip - 1) square, and each luxel keeps the first chart to reach it
    (0xFFFFFFFF none). The file is the ids in 1024-luxel blocks (2048 with
    vrad3LargeBlockSize or off a customer machine; at most the smaller
    side), row-major over blocks and within each. Lighting/LightmapTexels.cs
    writes it byte for byte as Valve's on probe01 and ze_hold_em_p (from the
    .vmap, through ChartMeshes.Build). Not seen: mips above 1, an atlas
    smaller than a block, the 2048 block.
66. **lightmap_seams.dat** (settled 2026-10-17 by decompile; 0923
    WriteLightmapEdgeSeamsForVrad3 180213290, the cross-mesh weld 1802129d0,
    BakeLighting's uv snap in 180217740; ChartMeshesTests.
    LightmapSeamsAreValves with LMSEAMS). After PackCharts every chart
    entry's uvs are snapped to steps of 1 / (2 side floor(32767 / side)).
    The vertices of all charted meshes are welded across meshes: position,
    normal and tier byte records through CVertexKDTree, each vertex
    joining the first earlier one the box query returns whose cluster's
    first vertex has the same tier, lies within 1/32 and has a normal dot
    of at least 0.99. Corner adjacency over all triangles then comes from
    those ids (18020efc0). Each neighbouring pair, the lower triangle first,
    where both triangles' two edges from the first corner are not within
    0.999 of parallel, writes its shared edge's four snapped uvs (32 bytes),
    unless it is the same edge of one mesh and chart or the uvs agree
    exactly. Lighting/LightmapSeams.cs writes it byte for byte as Valve's
    on probe01 (30 records) and ze_hold_em_p (7,786). Not seen: an entry
    matrix other than identity, a non-zero tier byte.
67. **The path-trace scene: scene_meshes.kv3 and scene_info.kv3** (settled
    2026-10-17 by decompile; 0923 WRB_BuildPathTraceSceneInfo 180285eb0, its
    static models 180284aa0, WRB_LoadPropMeshes 1801d30d0, CMesh's KV3
    writer 1812e4550, the record EntityLump_StaticModelRecord 180245770,
    meshsystem's geometry info 18002af80 and its semantic lookup 180036aa0,
    the light info 180eece60, the light-shape writer 181297030, the sky light
    181113f20; SceneInfoTests with SCENEINFO / SCENEMESHES). The meshes are
    binary KV3 v5, generic format, uncompressed; the info is tier0's KV3 text
    (CRLF, no final newline, arrays of fewer than five scalars inline,
    V_FloatToString_NoExponent floats). Instances, in order: the world entries
    BuildNode receives (attributes 0x2000000001 left out), with a linear tint
    and flags from attribute bits 16, 17 and 19, emissive and +0x99; then the
    builder's prop map (+0x198) model by model in ordinal order, its records
    from every prop_static and every brush entity with lightmapstatic set:
    per draw call of the model's LOD 0, one shared mesh and an instance per
    prop (matrix, the skin's material, rendercolor and renderamt over 255,
    emissive_lighting_boost, flags from disableshadows, lightmapstatic,
    emissive (default 1) with emissive_mode and bakelightdoublesided). A
    prop's CMesh has the 18 standard floats and, after them, every vertex
    field meshsystem does not consume, named by its shader semantic: a COLOR
    field is the vertex colour only without a shader semantic (or one that
    looks up as color), so a brush entity model's COLOR0, tagged
    VertexPaintTintColor, is an extra stream and its "color" stays zero. A
    float texcoord 0 or 1 marks the texcoord streams uncompressed. Lights:
    each light_barn's or light_environment's 300-byte LightShape with its
    light info (path_trace_light_flags, baked shadow ids, the precomputed
    box); the ibl from the first light_environment and its env_sky (null
    without one). Lighting/PathTraceScene.cs, PathTraceProps.cs,
    MeshGeometry.cs and SceneLights.cs write both files byte for byte as
    Valve's on probe01 (15 meshes; 3,625 characters) and ze_hold_em_p (75
    meshes, 159 instances, 37 barns; 107,650 characters). Not seen: per-prop
    vertex data (+0x100), material overrides, deformers, lodlevel 0 or
    more, smart props, other light classes, a sky colour without an env_sky,
    the 180240a60 brush-entity tests beyond lightmapstatic.

68. **vrad3's block geometry and lightmap samples** (settled 2026-10-17 by
    decompile and shader; vrad3 18002d530 lightmap_load_block_gpu, the cordon
    mask 180045400, the partition 18003d8e0, PackTangentFrame 1801f5970 with
    its asin 1801fa6e0 and basis 1801f4060, UnpackTangentFrame 1801f5680;
    lightmap_generate_small_chart_samples and lightmap_rasterize). The block's
    triangles are polygons of position, uv and packed frame; large charts
    stay whole, small-chart polygons are cut on the 8 x 8 tile lines (epsilon
    2^-16, the cut coordinate snapped, and v also snapped to the x line's
    value when a y cut was due too, as vrad3 writes it), tiled by uv centroid,
    sorted and fanned. PackTangentFrame is resourcecompiler's
    CompressTangentFrame (the same code). The small-chart sampler and the
    raster pass are ported as the shaders run (Lighting/Bake/BlockGeometry.cs,
    LightmapBaker.Sample). Not exact: the order of triangles inside a tile
    (vrad3's std::sort is unstable), so which random number picks which
    triangle can differ.

69. **Shadow-ray culling in CS2's bake** (settled 2026-10-17 by decompile
    and a bake comparison; scenesystem 180095180 sets
    m_nSceneGlobalInfoFlags (globals +0xB8) bit 1 only when
    ResourceCompiler/BakedLighting/DisableCullingForShadows is 0;
    rtx_vrad3_lightmap maps bit 1 to ray flag 32, bit 2 to 16). csgo's
    gameinfo.gi sets DisableCullingForShadows 1, so CS2's shadow rays cull
    nothing. The ray scene's facing stays counter-clockwise-front: the
    sample validation's backface count (a hit behind the face normal that a
    cull-back-faces retrace no longer finds) only ever counts with it. With
    culling, probe01's sunlit blocks kept 15% of Valve's shadow; without it,
    as Valve, they match (CpuBakeTests).

## Resolved

- **Prop bone overrides** (ledger 45, settled 2026-10-08 by decompile).
  The pose physics applies per body (entity +0x268, copied from CMapEntity
  +0x438, applied by name in 180200af0 through 181749d20) has no .vmap
  storage. Every writer of +0x438 in the installed resourcecompiler: the
  constructor (180f2ac50), clear (180f8606d), CopyFrom (180f777c0, through
  181749ef0) and SetSkeleton (180f93820/180f93880), the last called only
  from 18105ce30. That runs from 18105b6c0, reached in the editor through
  CMapDoc::FrameUpdate and in a compile only through CompileMap
  (1800fd550) -> 180f1dae0 -> the ragdoll pre-settle (180f1e800) ->
  180f4d6f0. So in a compile only ragdolls carry a pose, a prop_static's
  world physics never gets a bone override, and our physics output needs
  no change. The ragdoll side is ledger 56.

- **Bodies leaving the world** (ported 2026-10-06 from the decompile of
  vphysics2 0924; exact in lockstep with Valve's world). After the
  continuous solve, CRnWorld_ClampToWorldBounds (1801faf80) takes each
  dynamic body with a proxy outside +-(h - 1): velocities zeroed unless
  static, its box (1801b1a30, shapes' boxes unioned into a box that starts
  at zero) grown by its origin, moved by the least that brings the box
  inside +-(h - 6) per axis (1801b9c20), and put to sleep (1801be010); then
  the pair query and new contacts. dc_port's two dkr_m1_motel maps now
  build and are within the documented gaps. The oracle tests load the
  archived 0924 vphysics2 beside its tier0 when the installed build has
  moved on (they had returned early since the 10-01 update).

- **Prefab and instance collapse order** (settled 2026-10-05 by capture,
  capture_collapse.py on c2m3_coaster_d_d). Prefabs and the map's own
  instances share the first round in tree order: coaster's prefabs 2, 3,
  5, 6 and 7 (hidden ones too) then instance 2118, roots 2119, 2259, 4629,
  4698, 4754 and 4755. A prefab's map nodes take its block's start plus
  their preorder slot (MapInstances.PrefabSlots); the settle builds them at
  those ids, and coaster's settle world matches Valve's body for body.
- **editorOnly nodes** (settled 2026-10-05). CMapNode +0x365 is the
  "Editor Only" property (serialized editorOnly; getter rc 180f8e990 also
  true when vfn 0x440 says so). The compile's node filter (180f77240)
  rejects such a node, and none ships: the corpus has ten, all sky_cameras
  in prefabs (coaster, the five dkr_remix maps, dkr_onelevel's five), and
  no Valve lump carries one, while probe_classes' two plain sky_cameras
  ship. MapEntities marks them hidden; one with children throws (not
  measured).
- **An empty path's pathNodes** is "[  ]", as any empty KV3 array prints
  (dkr_m4_ferris's path_particle_rope 1849).
- **Where a prefab's entities are walked** (settled 2026-10-05 by
  comparison). A prefab's map is walked where its parent's children end,
  after its siblings, as an instance's copies are written:
  c2m2_fairgrounds_csgo_multi numbers its own entities 2119 to 2123 (last
  in the tree) 1 to 4 and its environment prefab (first) from 5. The
  collapse order is still tree order. 1,153 differences to 0; every other
  prefab map unchanged.
- **A source key named hammerUniqueId** (settled 2026-10-05 by
  comparison, ze_doom_p2). Source 1 ports carry old ids in that key; the
  compile writes its own id path into that slot and adds none after scales.
- **Brush entity hull input** (settled 2026-10-06 by decompile and
  capture, cs_script_demo, HullBuildCaptureProbe). BuildPhysicsPieces
  (18020b230) welds the piece CMesh at 1/32, moves it twice and converts it
  (1813319d0: one half-edge vertex per welded vertex, triangles repeating an
  index skipped), then calls HalfEdge_JoinCoincident (18131e190) with mode 3
  and 1e-7 (0x33d6bf95). That builds tier0's CVertexKDTree over the
  positions and, vertex by vertex, merges each into the first box hit that
  is earlier, not merged away and within 1e-7, when both are open (no edges
  or an edge held by exactly one face side) and share no face. A vertex at
  a triangle with two corners on one point therefore stays apart, and a
  later vertex at that point joins whichever the tree lists first (neither
  oldest nor newest, which is why simple rules failed). Ported as
  BrushHulls.HalfEdgeJoin and Maps.VertexKdTree: the queen's main mesh
  gives Valve's 20,341 vertices and every index, its 89 hull inputs are
  Valve's, and cs_script_demo's 37 brush entity models are exact.

- **A class's applied material** (settled 2026-10-06 by decompile and
  comparison, ze_doom_p2_c). auto_apply_material is set as the mesh's
  whole-mesh override (rc 18108d780, SetWholeMeshOverrideMaterial) and
  BakeWholeMeshOverrideMaterial (1810c68f0, "AssignMaterialToFaces") gives
  every face that material, so the mesh is one physics piece and its own
  materials need not exist: doom's 38 triggers on Source 1 textures with
  no .vmat ship models, now all 309 exact.

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
  leaves undecided ends outside. On Mako (2026-10-06, all 2,660,132
  verdicts exact) one more: the seed reads the scene's flag word, which
  LoadRTEFromFile folded (0x10 into 0x20) before adding the triangle, not
  the file's. A file word 0x130 is 0x120 in the scene, nodraw only, so
  GatherRays' NoDrawSecondLook looks past it (capture_outside.py --rays:
  101 of 150 rays of one box turned to facing).
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
