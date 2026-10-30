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
    Settled 2026-10-19 for the brush entity models (ledger 87): the model
    compile's V flip round trip, 1 - (1 - v). Earlier notes:
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
    probes' lumps are within their documented gaps. The settle takes its
    copy ids from this same expansion (prefabs on).
    Settled 2026-10-23 (read, then checked on Valve compiles of
    s2c_prefabprobe7 to 12, tools/entities/prefab_nest_probes.py): the
    rounds are MapDoc_BakeInstances (180f60740): until nothing collapses,
    every instance-typed node (prefabs too) in tree preorder, each copy
    added as the last child of its parent (CMapInstance_Collapse
    181017820, vf 0x1c8); MapInstances.Expand simulates that tree. A
    prefab map placed again reuses its compile_source_ids; fixup prefixes
    nest outer first, a targetName prefix only with useTargetNameAsPrefix.
    Still from output only (two open points): the order of the +0x6b8
    prefab documents in MapDoc_GatherLights (documents and placements
    breadth first, the first placement's lights last to first: probes 9,
    11 and 12 tell these apart and fit nothing else tried), and the
    node copy that flips a repeated placement's keys (ExportToLump writes
    the table as stored, 181004020). A prefab inside an instanced group
    crashes Valve's compiler (s2c_prefabprobe10).

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
    Settled 2026-10-27 by decompile (ledger 165): strcmp keys, "%s_%d" of a
    counter for deformed, overridden or +0x1e8 records; the deformer part is
    ported, the capture check waits for CS2 closed.

48. **The targetname of a cubemap or probe volume record** (EnvVolumes):
    the late export (180240a60) reads targetname from the exported keys;
    we take it from the entity's key table before any instance name fixup.
    Settled 2026-10-27 by decompile (ledger 164): the exported keys carry the
    lump's [PR#] fixup and the record keeps it; ported.
    It only matters to an entry with a lighting origin name, which no
    specimen has.

### Map resources
15. What picks Zstd or LZ4 for map output. Settled by ledger 3's decompile of binary_auto (tier0 1800c7220:
    LZ4 under 0x80001 bytes of buffer and blobs, zstd from there, kept only if it saves a twentieth); what is
    left is ledger 3's map of writers to encodings (ledger 164).

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
    Settled 2026-10-27 by decompile (ledger 164): CMapMesh's copy (181081be0)
    does not carry renderwithdynamic, nor fademindist, fademaxdist or
    keep_vertices; a copy keeps the constructor's values.
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
    2026-10-22: s2c_prefabprobe4 (atixref as a prefab) 2140:476 and 2140:477, last-bit origin and yaw;
    Valve's 10-20 and 10-22 compiles with the same resourcecompiler disagree, and our whole lump equals
    the 10-22 one (0 differences).

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
    - (Settled 2026-10-19, ledger 87: 1 - (1 - v).) Brush entity model texcoords (ze_hold_em_p's end_breakable models,
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

70. **Material data for vrad3's hit groups** (settled 2026-10-17 by the
    compiled shaders; rtx programs in csgo/shaders_vulkan_dir.vpk,
    materialsystem2 20261015's expression function table at 1800fa000). A
    material's block in g_MaterialData holds every variable its static combo
    writes, at the write sequence's RegisterOffset in 4-byte words
    (csgo_environment combo 0: g_vColorTint 52 = byte 208, g_tColor1 178 =
    712, as its GLSL struct reads them); values are the artist's, the VCS
    default, or the variable's VfxEval expression; texture variables are
    bindless indices, read through sRGB views when the variable's
    TypeSpecificBits bit 0 (SrgbRead) is set. Inferred, not captured: the
    GeometryInfo_t field formats for scene_meshes.kv3's float streams and
    the missing-texture fallbacks (white, flat normal).

71. **GPU min, max and clamp** (settled 2026-10-17 by a bake): the raygen
    divides a path's radiance by its largest component and clamps the
    result; for a zero path that is clamp(0 / 0, -1, 1) * 0, which vrad3
    bakes as 0. The transpiled shaders' min, max and clamp therefore take
    the operand that is not NaN (IEEE minNum / maxNum), as GPUs do.

72. **Lightmap texture encoding** (settled 2026-10-17 by decompile and byte
    comparison). The format per channel, slicing and header rules are in
    SerializeLightMap (1801f3f40) and 1801eee20 (docs/BAKED.md "Lightmap
    textures"). The encoders, profiles and weights were confirmed by
    rebuilding probe01's six lightmap textures, identical byte for byte:
    - BC6H: ispc slow;
    - DXT1, ATI1N: ATI weights 1 from the 8-bit writer;
    - BC7: bc7e.
    The reflectivity was confirmed the same way: the four-lane mean, bit for
    bit. RED2's hashes (2026-10-18, decompile; all five probe01 values
    reproduce):
    - the override CRC is CRC32 of the descriptor's KV3 text (181e97100);
    - the ImageArg fingerprint is CRC32 of the bitmap's dimensions and planes
      (180174ce0).

73. **The light probe volume bake** (settled 2026-10-17 by decompile,
    disassembly and comparison with vrad3's images on probe01). Voxelize,
    flood fill, compute and readback are in docs/VRAD3.md "Light probe
    volumes", as are AO, fireflies, resolve and median. The random offsets
    are SFMT-19937 (checked against its reference sequence). The AO factor
    comes from the disassembly (Ghidra drops its float accumulation). The
    shadow dilation runs two rounds. The solid masks and the open band
    equal Valve's. The atlas copy equals resourcecompiler's on both atlases.

74. **Lightmap query data** (settled 2026-10-18 by decompile and byte
    comparison). StaticLighting_WriteLightmapQueryData (1802163b0) quantises
    the atlas uvs with the caller's scale, welds at 1/32, and simplifies with
    meshoptimizer's simplifyEdge. The meshoptimizer version was identified
    from the decompile: v1.1 to v1.2, with 15 arguments, RegularizeLight
    tested and no PreserveFolds or ErrorClamped. The output is KV3 v5.
    probe01's and cardtest's files are rebuilt byte for byte. The resolution
    volume cull (180216090) is ported as read but unexercised: probe01
    culls nothing.

75. **The ray trace scene's vertex layout** (settled 2026-10-18 by
    decompile and bake). vrad3 sets no layout itself: its scene_meshes
    reader (1801fd4e0) keeps each attribute as read, and scenesystem's
    loader (1800912d0) builds the vertex buffer.
    - The material comes from the mesh's own material_name.
    - CMesh_CreateInputLayout (1804aaf20) gives every attribute a format:
      the attribute table (18066d400) by name and index, or by size when
      the attribute is no_compression. A compressed normal is R32_UINT,
      the packed tangent frame, and its tangent is dropped.
    - The material's input signature then names each field (1804aa730).
      Each element's Semantic picks a field, which takes the element's
      D3D semantic name and index. Fields no element takes are removed.
      The rest are packed in CMesh order.
    - The vertices are converted per format (1804abf60 / 1804ac280).
    - The -1 semantic index the 10-18 experiment assumed is not on this
      path: 180093f10 handles it, but no field reaches it here.
    - So csgo_simple_2way_blend's TEXCOORD 4 is VertexPaintBlendParams
      (R8G8B8A8_UNORM), and props carry half texcoords and a packed
      tangent frame.
    - Result (SceneVertexLayout.cs): ze_hold_em_p's 16 blocks went from
      1.16 to 1.85 times Valve's raw image to 0.98 to 0.997, and its lit
      probe volumes to 0.94 to 0.997. The only_c12, only_c9 and
      only_slabs scenes give 0.99.
    - A second, independent port (RtxVertexLayout) gave identical bytes on
      all 90 meshes of probe01 and ze_hold_em_p. It was then folded into
      SceneVertexLayout: every float op and constant from the
      disassembly, cvttss2si semantics (0x80000000 for a NaN or out of
      range) where .NET 10's casts saturate, aa730's dedupe on name,
      index, format, offset, slot and instanced, the -1 lookup and slot
      check of 180093f10, and the writer's running offset. aa730's second
      loop matches the field name against the table's Semantic (+0) and
      its own index (+0x14), so a PerVertexLighting attribute stays as
      color 1 (R16G16B16A16_FLOAT, 8 bytes): no hit shader reads it, but
      it is in the stride. scenesystem's 180567b00 / 18015e140 equal the
      resourcecompiler ports TangentFrame.Compress (2M random and edge
      frames) and WorldNodeModels.ToHalf (all 2^32 floats).

76. **Light probe volume bake details** (settled 2026-10-18 by
    disassembly; review findings F35, F36, F41 to F45).
    - F35: both LPV passes scale the SFMT words by the float at
      0x1802a7e88, 0x2f7ffffd (loaded 1800328aa and 1800361e2), three
      ulps under 2^-32, then add +0. Offset x is the third draw, y the
      second, z the first.
    - F36: lpv_compute_block_gpu (180035e80) tests convergence at the
      top of each command list (180036258: pass 0, or a list starting at
      or past minspp); a list runs up to 16 passes (180036830 to
      1800369c3) with no test in between.
    - F41: the flood fill's origin cell (1800323f0) is floorf, then
      cvttss2si, then negative to 0, else min(n - 1).
    - F42: the AO factor's (occluded / total)^2 is tier0's V_powf
      (1800839bd, exponent from +0x320).
    - F43: the firefly detect skips a cell only when 5e-5 > luminance
      (comiss / ja at 18008550c): a NaN luminance is processed.
    - F44: SPIR-V Cross leaves two kinds of half arithmetic unconverted
      in vrad3's shaders: compound assignments to half values (emissive
      times fEmissiveBoost in the raygens, albedo times the vertex colour
      in rtx_default_hitgroup) and mix() of half arguments (the
      reflectance blend). glsl_to_cs.py now rounds their results to half.
      How the GPU evaluates the half mix inside (FMA, intermediate
      rounding) is not known; only the result is rounded.
    - F45: a GLSL array declared from another array copies it (Clone).
    - F40 (stale luxels past X*Y*Z in the convergence test) is left: it
      needs err > 0, and resourcecompiler's script bakes 64 / 64, which
      tests only at pass 0 with err 0.
    - Measured (CpuBakeTests CPUBAKE_LPV=all): probe01's two volumes at
      0.998 to 1.000 of Valve per band, 39 to 56% of noisy texels
      bit-identical and 84 to 90% within 1%; ze_hold_em_p's eight
      volumes after the post at 0.997 to 1.000, 14 to 93% bit-identical.
      The rest is most likely the GPU's float evaluation (FMA
      contraction, hardware rcp / rsqrt) feeding the path tracer; not yet
      isolated. If so, vrad3's own output depends on the GPU it ran on
      (Valve's bake is deterministic on one GPU; a second GPU is not
      tested), and byte parity of baked lighting can only be defined
      against that GPU.

77. **The default hit group and the sample validation** (settled
    2026-10-18 by decompile and bake).
    - A material whose shader has no _vulkan_60_rtx program is drawn by
      rtx_default_hitgroup: combo 0 (materials/dev/rtx/
      hitgroup_default_opaque.vmat) or 1 (hitgroup_default_transparent.vmat,
      F_TRANSLUCENT) by material flag 2. scenesystem 180098cd0 writes its
      0x20-byte block: the albedo from the attribute LightSim_Albedo
      (murmur 0x6239512c), each component clamped to 0..0.99, (0, 0, 0)
      without it; opacity 1 at +12 (a translucent material: texture
      0xfa77b522, not named, else LightSim_Opacity 0xe2945d83); when there
      is no LightSim_Albedo, the bindless index of LightSim_DiffuseAlbedoTexture
      (0x0effc516), else RepresentativeTexture (0x5d700411), at +16 (-1
      without); -1 at +20; the material flags (180098660's 1, 2, 5) at +24.
      The attributes are the material shader's (generic.vfx binds
      LightSim_DiffuseAlbedoTexture to g_tColor; no material tint). It
      reads the packed vertex buffer, so its normals carry the 10-bit
      octahedral quantisation: probe01's floor normal is (0.00098,
      0.00098, 1) in Valve's raw image, which our hand-ported default (float
      normals, times g_vColorTint) missed.
    - The samples go through vrad3's own rtx_vrad3_lightmap_sample_validation
      (transpiled, SampleValidationRaygen). The hand port kept ze_hold_em_p's
      chart 2074 (a wall at x = 7672, 28,998 luxels of block 5) that Valve
      invalidates.
    - Measured on ze_hold_em_p block 5 against Valve's raw image: the luxel
      normals equal Valve's (correlation 1.000, mean |diff| 0); AO
      correlation 0.866 to 0.967, ratio 0.990 to 0.998. Irradiance per luxel
      is still uncorrelated (0.31 to 0.39) at the same mean (0.989 to
      0.991): the light bins are the next suspect (RaygenHost puts every
      light in one bin; vrad3 bins them with rtx_binlights). Read since:
      the raygen evaluates every light of the (wave-ORed) bin mask with a
      clip-space cull per light, so the bins cannot decorrelate the noise;
      see 78.

78. **Where the lightmap noise still parts from Valve's** (measured
    2026-10-18 on ze_hold_em_p block 5 against Valve's raw images of the
    scene variants; open).
    - All materials reflectivity_30 (refl30all): irradiance correlation per
      luxel 0.972, AO 0.993. With the real materials: 0.31 to 0.39. Per
      material (only_<name>, the rest reflectivity_30): tintdark
      (csgo_lightmappedgeneric) 0.931, slabs (csgo_simple_2way_blend, the
      floor) 0.667. So the divergence is in the material hit shading, and
      the means still agree (0.989 to 0.996).
    - Our own bake against itself with every hit albedo times 1.003: 0.957.
      The material outputs therefore differ by much more than a rounding.
    - Ruled out on the slabs: bilinear weights quantised to 1/256 (as
      NVIDIA filters) leave the correlation unchanged (0.625 to 0.624); the
      block's texture, sampler and constant writes (CPUBAKE_MATDUMP=1) match
      the vmat; the normal maps decode as BC7 with roughness in B (0.29 to
      1.0 and about 0.10), the mask as BC4; no hit shader uses a random
      number.
    - Not yet checked: everything every textureLod reads at mip 0 in
      Valve's GPU run. That needs GPU-side ground truth (a Frida or
      RenderDoc capture of one pass, or a probe shader on the PC's RTX 4070,
      which made the captures). Done since: see 79.
    - Our own bake with full-resolution against 512-capped textures
      correlates at 0.938, and forcing the blend to layer A or B gives
      means of 1.33 and 0.67: the blend is right on average, the per-hit
      material differs by more than a mip level would.

79. **The hit shaders on the GPU: the texture unit** (measured 2026-10-18
    on the PC's RTX 4070 with HitShaderOracleTests; open).
    - Oracle: tools/re/hit_to_compute.py rewrites a closest hit's GLSL as
      compute (ray built-ins and payload from buffers), ComputeOracle
      (Source2.Compiler.Gpu, shaderc) runs it with the scene's own buffers
      and the textures uploaded as their stored blocks. On the blend
      floor's hits every geometric field agrees to 1 ulp; albedo differs by
      up to 4%, roughness 4.5%, the material normal beyond rounding.
    - Texture residency: rendersystem's 512 preload cap (as settled for
      physicsbuilder) also caps the raytracer's textures (scenesystem's
      rtx_texture_resolution, 1800058f0, default 512, range 64 to 2048):
      RtxScene.LoadTexture now starts at that mip (TextureCap). Small
      measured gain (slabs 0.625 to 0.634).
    - BC7: texelFetch equals VRF's decode bit for bit.
    - BC4 UNORM: r = (r0 (257 - w) + r1 w) / 65535 with w = 36, 72, 113,
      144, 185, 221 (r0 > r1) or 48, 96, 161, 209 (r0 <= r1), G = B = 0,
      A = 1; VRF returns 8 bits in every channel.
    - sRGB 8-bit to linear: a fixed 256-entry table of half floats (up to
      0.00375 off the exact curve), measured from an R8G8B8A8_SRGB texture.
    - BC1: every palette entry is an 8-bit code p (UNORM p / 255, sRGB the
      table above at p); endpoints are the usual 565 expansion. Red and blue
      (5 bits a, b): four-colour mode p = ((2a + b) 22) >> 3 (and with a, b
      swapped for index 3), three-colour mode p = ((a + b) 33) >> 3, index 3
      black; exact on all 2,048 cases each. Green (6 bits) fits no
      linear integer formula on the raw or expanded values (11/16 weights
      on the expanded value miss 368 of 8,192 by one): tabulate it (64 x 64,
      measured by Bc1Decode, HITORACLE_BC1=1, data in %TEMP%/hitoracle_tex).
    - Bilinear: with BC7 texels equal, 65,536 filtered samples still differ
      by up to 0.0011 (mean 3e-5) from exact float weights; 1/256 weights
      (rounded: mean 2.2e-5) or coarser coordinates, half or 16-bit output
      rounding do not make it exact, and the error does not grow with the
      coordinate. The filter arithmetic is still to find.
    - Payload half fields come back unrounded from the compute rewrite
      (reflectance 0.0400000028): whether vrad3's real ray tracing payload
      rounds them is not known; ours does.
    - Next: fit BC1's interpolation and the bilinear filter, then decode
      BC1/BC4 and sRGB as measured in RtxScene.LoadTexture and filter as the
      GPU does in HitContext, and re-measure only_slabs.

79. **Lightmap textures at every compile option** (settled 2026-10-18 by
    decompile and byte comparison with Valve's probe01 builds at
    -lightmapMaxResolution 8192, -lightmapVRadQuality 2 and
    -lightmapCompressionDisabled; tools/re/out/captures/probe01_options).
    - SerializeLightMap (1801f3f40) gets the mip count BakeLighting chose
      (lightmapMipCount, then halvings to MinSpecLightmapSize).
      - The output's minimum dimension is the atlas' shorter side >> (mips
        - 1).
      - A channel MaxResolution halves while the shorter side is over
        max(that, MaxResolution), one mip off each halving.
      - Every channel but debug_chart_color gets mip algorithm Box<n>
        while n > 1.
      - Shadows scan the unhalved bitmap.
    - The texture compiler (180133da0):
      - Box mips are QuarterSize levels.
      - The special dependency names the mip algorithm ("... Mip Box").
      - "Texture Encode Quality" carries the quality as user data.
      - The header's reflectivity is the smallest mip's.
      - Mip sizes are listed from mip 0, and the data is stored smallest
        first.
    - The lightmap query data's uv scale uses the halved side
      (180218096).
    - The RED2 arguments:
      - `lightmapMaxResolution` and `fast` come from the compile's command
        line (a float records its bits as a FloatArg).
      - `final` = 182a88554[lightmapVRadQuality] = {0, 0, 1} as an IntArg.
      - `hueShiftFixup` is IntArg 1 on BC6H when final.
    - Quality 2 (final) raises the encode quality to 4: LZ4 HC level 12
      instead of 10, BC6H veryslow instead of slow. The DXT1 and ATI
      encoders take no quality, and bc7e uses level 6 at 3 and 4.
    - The BC6H row fill (180c3ef20) truncates to half with max(0, x),
      clears the sign and writes alpha 1. This is review finding F25: it is
      exact on EXR halves, so it showed only on mip 1.
    - lightmapCompressionDisabled gives RGBA16161616F (truncating halves)
      for every channel but shadows, and RGBA8888 for shadows.
    - Result: maxres8192, nocompress and the default build are byte-exact
      in all seven files.
    - At quality 1 and 2 (182a88550 = {0, 1, 1}, unless noRepack,
      gameinfo NoRepack or disableLightingCalculations), vrad3 writes
      chart_repacking.dat (18020e230):
      - the content: magic 0x0531771e, the chart count, the width and
        height, a texel grid of original chart ids, and per chart
        {x, y, flags, size};
      - BakeLighting moves every lightmap uv through it (18020e4e0): with
        flags bit 1 to the centre of a size x size square, otherwise by
        the chart's offset (swapped with bit 0);
      - the query data is built from the moved uvs
        (LightmapChartRepacking).
    - The light probe atlas size is LightProbe_PackAtlas's output: the
      allocator dims of the last fit, rounded up to 4 (1801f5fce,
      1801f6080; review finding F15), not the boxes' extent. ze_hold_em_p's
      eight volumes give the same size either way; its outputs are
      byte-exact apart from 2 BC7 blocks of the dlshd atlas.
    - Still open at quality 2:
      - the hue-shift fixup encoder (180c46dd0): 5 of 1,048,576
        irradiance blocks; fixed, ledger 111;
      - 5 BC7 blocks of the dlshd atlas: fixed, ledger 110 (bc7e's 8-wide
        target).

80. **The GPU bake host and Valve's host buffers** (measured 2026-10-18 on
    ze_hold_em_p block 5, RTX 4070; open).
    - RtxBakeHost (Source2.Compiler.Gpu) runs Valve's own SPIR-V in a
      Vulkan ray tracing pipeline: rtx_vrad3_lightmap (static 0, dynamic
      D_SH_LIGHTMAPS 0) and rtx_vrad3_lightmap_sample_validation as raygens;
      per material shader one module with entries ClosestHitN / AnyHitN for
      ray type N (rtx_default_hitgroup static 1 has AnyHit0 and AnyHit1);
      18009b330 names the SBT's misses Miss0 / Miss1, which no vrad3 program
      has, so they are zero records. Descriptor sets 3 to 6 as the programs
      declare them; one BLAS per mesh, the TLAS over the instances.
    - Nothing may be forced opaque: shadow rays start at visibility 1 and
      only the hit groups' AnyHit1 lowers it (opaque materials included).
      With FORCE_OPAQUE every shadow was 1.
    - vrad3's own bake is deterministic: block 5 baked alone
      (tools/vrad3/capture_host.py) equals block 5 of the full-map bake in
      every channel, bit for bit.
    - Block 5 on the GPU: 6.7 s (vrad3 0.11 s for its block compute) against
      171 s for the transpiled C# on 14 threads. GPU against CPU port:
      irradiance correlation 0.95, shadows 99.9% equal. GPU against Valve:
      irradiance 0.61, the same as the CPU port, AO equal on 73% of the
      occluded luxels, shadows 0.99, normals exact. So the remaining gap is
      in the host inputs both share, not in the shaders.
    - The raygen sums lights over subgroupOr of the luxel's light bin masks
      and picks among them by reservoir sampling: the bins (rtx_binlights
      over the grid of SceneGlobalInfo) change the noise, not only which
      lights reach a point. One all-lights bin is not equivalent.
    - capture_host.py hooks scenesystem 180099c00 (structured buffer upload:
      data, stride, count) during a vrad3 run. CpuBakeTests
      (CPUBAKE_HOSTCAP=<capture>) compares ours. Differences found:
      - SceneGlobalInfoGPU_t: local light bounds (-13707, -608, 17) to
        (7672, 398, 144) and a 3114 x 147 x 18 bin grid (ours a 1-cell
        placeholder); m_nSkyLightFlags 1 with no ibl table (the loader's
        default; only ibl.path_trace_light_flags overrides it; ours 0, and
        the raygen drops the sky when bit 0 is clear); m_nBrdfLut and
        m_nBrdfMultiscatterLut set (textures/dev/brdf_lut and
        brdf_multiscatter_lut, 1800065c0); _unused0 -1.
      - PathTraceBarnLightGPU_t: min / max bounds (+0x150, +0x160: the light's
        clip box through FromClipSpace, 1804a2b70, cut by the illumination
        volume's box), the illumination bounds centre, orientation and
        extent (+0x170 to +0x19c), m_nPreviewBakedShadowIndex 0 (ours -1),
        and m_mBarnIlluminationFromWorld 1 ulp off in 31 words.
      - GeometryInfo_t: one record per instance (159), not per mesh (75);
        unused streams are -1.
      - PackedRtxInstanceData_t: nTriangleGeometry, nMaterialData and
        nInstanceFlags differ (the shaders read only nSrgbColor,
        nMaterialData, fEmissiveBoost and nMaterialFlags_nInstanceFlags).
      - Material data: 3,728 bytes against our 3,360 (block order and
        bindless indices differ, which is harmless if consistent), and a
        colour matrix of 1.00000024 and 1e-8 terms where ours is identity.
    - The bin grid: FUN_1804c8280 bisects a cell size between 0 and the
      largest extent until the step is 0.01, counts rounded with cvtss2si
      (at least 1), keeping the smaller size while cells x 4 x masks fits
      0x4000000 bytes.
    - lightmap_compute_block_gpu (vrad3 180030100): push constants
      (pass, 4, 0, 0), (block x, y, lightmap w, h), the cordon from
      DAT_180347f98 (not yet read); new samples on pass mod 4 = 0; the
      convergence test only at pass 0 with 16 / 16.
    - Settled since (all against the capture):
      - PathTraceBarnLightGPU_t is byte-identical for all 37 lights:
        PathTraceBounds ports 1804a2b70 (the clip box through FromClipSpace,
        reciprocal multiply), 18049d6d0 (quaternion rows), 18049c3a0 (point
        transform: (r3 + y r1) + (z r2 + x r0)), 18049e080 (the box AABB),
        the loader's barn matrix (inverse quaternion over dpps, rotated
        negative centre, rows times 1 / extent, which explains the old 1 ulp
        differences: we divided) and the bounds cut (maxss / minss). The
        preview baked shadow index is 0.
      - SceneGlobalInfoGPU_t is byte-identical apart from the two BRDF LUT
        bindless slots, which no vrad3 shader reads: scene bounds from each
        instance's mesh AABB through 18049bfb0 (centre and absolute
        extents), the light union clipped to them, the grid from 1804c8280
        (3114 x 147 x 18 on ze_hold_em_p), sky light flags 1 without an
        ibl table, _unused0 -1. rtx_binlights now runs on the GPU over that
        grid.
      - The Halton volume: HaltonSamples equals Valve's own
        rtx_generate_scrambled_halton_samples (run on the GPU with
        180187920's push constants (31, 5, 4, 1377), (1216, 2592, 0, 0),
        (frame, 0, 0, 0) over 32 x 32 x 128 threads) on every word of
        frames 0 to 3 (CPUBAKE_HALTONCHECK). The host now generates it
        with Valve's shader.
      - A shader's RayTrace mode can name another shader: csgo_simple's is
        csgo_complex.vfx (its features file's mode list). Its materials take
        csgo_complex's rtx program with their own parameters (Valve's block
        for flourescent_light_housing3 is a csgo_complex combo 0 block).
      - Material blocks end at the next multiple of 16 past the last write
        ((end + 16) & ~15: 348 gives 352, 432 gives 448).
      - Sampler states: the rtx program's sampler descriptions are
        RegisterType SamplerState variables (RegisterOffset = the state's
        number, not a block word); a SamplerIndex variable names its state
        in TypeSpecificBits. Filter is the D3D11 enum (0x55 anisotropic,
        0x15 trilinear, 0x14 bilinear, 0 point, 255 the default:
        trilinear), MaxAniso only with 0x55, unset addresses wrap.
        tools/vrad3/capture_samplers.py logged vrad3's bindless table (set
        4 binding 29): every VkSampler has LOD 0..15, no bias, transparent
        black border; g_sAniso and the user sampler are 8x anisotropic,
        which csgo_environment's textureGrad uses. Our slots now hold the
        same states (the indices differ, consistently).
      - Not yet exact in the material data: g_mTextureColorAdjust1 (the
        environment shader's colour matrix: Valve's MatrixColorCorrect2
        gives 1.00000024 and 1e-7 terms at its defaults; ours identity,
        materialsystem2 180081260 not ported).
    - Why the luxels parted (0.65 at one sample): the raygen picks its
      origin sample from the 5 x 5 neighbourhood by reservoir sampling,
      weighted by exp(-2 |uv - centre|^2) of each neighbour's
      m_vLightmapUv, so the samples' positions and uvs must be exact, not
      only their normals.
    - The large-chart samples now come from Valve's lightmap_rasterize
      (vulkan 50 vs / gs / ps) on the GPU (RtxBakeRaster): D_PASS 0 depth
      LessEqual with writes, D_PASS 1 Equal without (the PS render states),
      no culling, a depth-only target cleared to 1, g_vWindow the block's
      uv rect plus the jitter. D_REVERSE_DEPTH stays 0 (not read from
      rendersystem; it only decides overlaps within one chart).
    - vrad3 starts every sample generation from an empty buffer: keeping
      the previous generation's samples where the new jitter covers nothing
      parted 1,382 luxels at 9 passes; clearing leaves 73.
    - The raw image's ambient occlusion is 1 on luxels no chart covers.
    - Result, ze_hold_em_p block 5 against Valve's raw images (vrad3 run
      alone on the block at 1, 4, 5, 9, 13 samples and the map's 16):
      irradiance and SH bit-identical on 99.99% of luxels at every count
      (16 samples: 54 of 826,931 differ), AO and normals 100%.
    - Those 54 were the image writer, not the bake: lightmap_image_save
      rounds float to half to nearest with ties away from zero (all 54
      irradiance and 235 SH ties; .NET's cast rounds them to even).
      LightmapImage.ToHalfAway does it now. With it, block 5 is
      bit-identical to vrad3's raw image in all 13 channels (irradiance, SH,
      quadratics, AO, normal and tangent with their spreads).
    - The colour matrices are exact too (tools/vrad3/capture_colormatrix.py
      logged materialsystem2's results): MatrixColorCorrect2 (180081260:
      ((((((D^-1 R^T) S) R) D) B) C), R the rotation 1800828c0 of the
      normalised luminance axis to z, every product 180081100's dpps
      order), MatrixColorTint2 at white equals it word for word (coloured
      tints not ported), MatrixMultiply sums in dpps order, and a Float4x4
      goes into a material block transposed. The material data is now
      byte-identical apart from bindless indices.
    - The luxel records match too once they start as vrad3's: the sample
      count word over 0.5f, the flags word over 1.0f (only their low bits
      are written), the four baked shadow values 1.0f. lightmap_data.dat
      is then identical for the block.
    - The NVIDIA driver compiles a ray tracing pipeline into one of two
      code variants whose lighting sums part by 1 to 3 ulps; which one
      depends on how the pipeline is created. A second creation in the
      process happened to give vrad3's; that workaround is gone. RtxBakeHost
      now creates them as vrad3 does (tools/vrad3/capture_pipelines.py):
      - a deferred operation joined by worker threads (max concurrency 17);
      - an empty VkPipelineCache, flags 0, recursion depth 1, no dynamic
        state;
      - one stage per group: the raygen, every hit program's ray 0 stage,
        then every ray 1 stage; the two rtx_default_hitgroup programs first;
      - the three pipelines in vrad3's order: rtx_binlights (with every hit
        group), rtx_vrad3_lightmap, rtx_vrad3_lightmap_sample_validation.
    - The lightmap raygen is rtx_vrad3_lightmap's S_EXECUTION_REORDERING 1
      combo (md5 of the module vrad3 creates: 183491771659...), since
      rendersystem enables VK_NV_ray_tracing_invocation_reorder (feature
      1000490000 on) wherever the device has it. GpuDevice enables it too.
      Running combo 0 left 6 AO values in 4 ze_hold_em_p blocks one AO ray
      off: a ray meeting a plane at t = 16.00015 against tMax 16, flipped by
      a one-ulp origin.
    - The other hit program order, after the two defaults, is not read yet
      (vrad3: complex 2560, complex 0, complex 5120, 2way blend; ours goes
      by first use). The output does not depend on it on ze_hold_em_p.
    - Acceleration structures (tools/vrad3/capture_accel.py):
      - one BLAS per mesh, flags PREFER_FAST_TRACE | ALLOW_COMPACTION;
      - geometry flag NO_DUPLICATE_ANY_HIT_INVOCATION, R32G32B32 positions
        straight from each mesh's vertex buffer (its stride), uint32
        indices, no transform;
      - all sized and created first, then each built with a compacted-size
        query, then all compacted with COMPACT copies;
      - the TLAS: PREFER_FAST_TRACE, instance flags 0, masks 0x11 / 0x1f;
        custom index and SBT offset (2 x instance) per instance, with
        GeometryInfo_t per instance (copies of the mesh's).
      Ours keeps per-mesh custom indices and per-program SBT records, which
      read the same data.
    - GeometryInfo_t's m_nVertexAndIndexBuffers is (mesh, -1, -1, mesh) in
      every capture.
    - Set 3's samplers are immutable in the descriptor set layout
      (capture_samplers.py, vkCreateDescriptorSetLayout):
      - g_Sampler: linear, nearest mip, wrap;
      - g_CookieSampler: linear, nearest mip, clamp to a transparent black
        border.
      Clamping g_Sampler darkened probe01's bounce light to 70% through
      rtx_default_hitgroup's textureLod on tiled dev textures.
    - A scene without an ibl block gets sky light flags 0. A light without
      an illumination volume gets the identity orientation (0, 0, 0, 1).
    - The scene's two BRDF LUT indices (248, 250) are read by no shader.
    - Readback: 180030100 normalises the normal with VectorNormalize
      (18003d0c0: length sqrt((y y + z z) + x x), times 1 / length) and takes
      the tangent's length as x x + (y y + z z).
    - std::sort's order for BlockGeometry's tiles: MsvcSort ports MSVC's
      introsort move for move. MsvcSortTests checks it against hashes from
      tools/re/msvc_sort_reference.cpp, built with MSVC.
    - Result, ze_hold_em_p: all 16 blocks baked on the RTX 4070 are
      identical to vrad3's raw image in all 13 channels and in
      lightmap_data.dat. vrad3 itself repeats bit for bit (two runs of the
      whole map compared).
    - Result, probe01 with its ibl block stripped from both sides
      (vrad3_variant.py --strip-key ibl): all 16 blocks identical. The IBL
      cube map is not uploaded to the GPU yet.
    - Small charts: Valve's lightmap_generate_small_chart_samples now runs
      on the GPU (RtxBakeSmallCharts). On cs_script_demo block 12 its inputs
      equal vrad3's word for word (tools/vrad3/capture_smallcharts.py):
      vertices, tile starts, areas and $Globals with its hole at 0.
    - g_nRNGOffsetX / Y are the generation's next two SFMT words, but
      rendersystem writes int attributes clamped to the variable
      description's default range [-999999999, 999999999]
      (rendersystemvulkan 180254550; the VCS files declare +-1e9). The
      second generation's words 2014350870 and -1313486101 reached the
      shader as 999999999 and -999999999. With LightmapBaker.RenderInt
      clamping the same way, block 12 is identical at 16 samples.
    - ze_hold_em_p's 346 and probe01's 3 small charts are all zero-area
      triangles, which the block clip drops, so neither map exercises this
      pass.
    - Still open: coloured tints, the IBL cube map, and the post-process
      EXR writers' tie rounding (not yet checked on a tie).

81. **The nav of a map with no walkable seeds** (settled 2026-10-18 by
    decompile and by every compiled package in the addons, CS2 1.41.9.1).
    - The nav step is resourcecompiler 1800fc370. Its settings come from
      the map's CMapNavData node (180fafae0, the node Hammer's navigation
      tools add). Without one it logs "Nav settings not found, using
      default settings."
    - The seeds are every point_nav_walkable's world origin, through prefab
      instances (180f21760).
    - With no seeds, navsystem.dll logs ">>NAVGEN: Skipped... no walkable
      seeds present" and writes a mesh with no areas.
    - Across the 26 compiled maps, every map without a point_nav_walkable
      also lacked a CMapNavData. They all ship one identical 834-byte .nav
      (md5 7b91f546...): ze_hold_em_p, ze_doom_p2 (both addons),
      ze_doom_p2_c_gameplay, c2m2's gameplay and environment prefab, and
      six probes. Every map with seeds got a generated nav.
    - The file:
      - magic 0xFEEDFACE, version 36, then 1, 1;
      - an empty KV3 block, 23 zero bytes, another empty block;
      - 119 bytes of default generation parameters (not decoded);
      - NavGenParams { HullParams [ { Name "player", FlowMap_Enabled
        false, FlowMap_NodeMaxRadius 400.0 } ] }, then 5 zero bytes;
      - { arealist [] } in its own KV3 format, 29f00073-008e-45f9-...
      Every KV3 block is uncompressed v5, and Kv3V5Writer writes all four
      as Valve does.
    - NavFile.EmptyDefault builds it; compile-map's nav step writes it and
      refuses a map that needs the generator. It is byte-identical on
      ze_hold_em_p, ze_doom_p2_c_gameplay and s2c_rounds (NavFileTests,
      MapCompileArgsTests).
    - Open: generating a real mesh (Recast plus 31 polygon stages in
      navsystem), and a map with CMapNavData but no seeds (its parameters
      would differ; not seen).

82. **A baked compile's world file and lights** (settled 2026-10-19 against
    Valve's lit packages: ze_hold_em_p, probe01, cardtest, cs_script_demo).
    - world.vwrld:
      - m_bBuildBakedLighting is set, and m_builderParams.m_bakedLightingInfo
        stays the empty one;
      - m_worldLightingInfo holds lightmap version 8, game version 2,
        m_bHasLightmaps, compression (!lightmapCompressionDisabled),
        m_nLPVEncoding and m_nLightmapEncoding 0, the packer's passes, and
        the vrad quality;
      - m_vLightmapUvScale is 65535 / R per axis, where R = floor(32767 /
        side) × side × 2 on the side left after the mip and MinSpec halvings:
        1.14284 at 4096 and 1.032242 at 1024 (180217740 writes it as
        65535 / R);
      - m_lightMaps lists direct_light_shadows, irradiance,
        debug_chart_color, directional_irradiance as resources, in that
        order on every map;
      - m_bakedShadows lists, in the lump's export order, each light the
        shadow slot step gave a channel: light_path_uniqueid,
        light_map_uniqueid (both as uint) and bakedshadowindex
        (ze_hold_em_p: all 37). A light left at -1 is not listed; no
        capture has one yet.
    - The entity lump: each light's precomputed_vis_clusters needs vis's
      flat clusters, so compile-map now builds vis before the lumps and the
      world node step reuses it.
    - Result: both files are identical on ze_hold_em_p and probe01.

83. **BakeLightMaps' write-back into the world node** (settled 2026-10-19 by
    decompile, 18025db60, 180217740 and 1802171b0, and by
    tools/vis/capture_nodeentries.py --full on ze_hold_em_p).
    - The node keeps the entries that do not bake in their order. The 50
      baked ones follow, in BakeLighting's sorted order (std::sort,
      18021b530).
    - Each baked entry is now its chart mesh: the 1/32 weld of
      StaticLighting_PrepareChartMesh, without PerVertexLighting, with a
      LightmapUV stream (two floats, type 0x2b) appended.
    - The closing steps change none of the 93 entries.
    - The uvs are the packed atlas uvs, snapped after PackCharts (180217740):
      u = floorf(u / (1 / R) + 0.5) × (1 / R), u by the height's R and v by
      the width's, then stored times R / 65535. All 81,324 values match bit
      for bit, ties included. Multiplying by R instead of dividing by 1 / R
      parts on 0.1%.
    - The models:
      - LightmapUV lays out as texcoord 3 in R16G16_UNORM, truncated, among
        the texcoords and after them;
      - those draws are m_bHasBakedLightingFromLightMap and not unmatched;
      - lightmapped meshes' probe handshake becomes 0 in the .vwnod, which
        follows from the headers' bake fields.
    - LightmapNodeBake implements it, and a -vrad3output compile runs it.
    - Result:
      - ze_hold_em_p: all nine world node files identical; the lit package
        is 47 of 56 files and 99.09% of bytes.
      - probe01 lit: 22 of 25 files. Missing are the generated nav, the
        bomb damage file and the .vmap_c.
    - Open: the 7 end_breakable entity models (texcoord ulps, a geometry
      gap), 2 BC7 blocks of ze's probe atlas (fixed, ledger 110), the .vmap_c (see
      LIGHTING_ROADMAP "vmap_c").

84. **The whole-map post-process and its EXR writers** (settled 2026-10-19 by
    oracle and disassembly, vrad3 2026-10-01, ze_hold_em_p).
    - Our GPU bake of all 16 blocks equals vrad3's raw image (pre_post) in
      all 13 channels and in lightmap_data.dat. lightmap_image_save writes
      halves, so this proves the raw image to half precision only.
    - The oracle: vrad3 runs the script's post steps on that saved image
      (lightmap_image_load, steps 1..k, lightmap_image_save), and our port
      runs the same steps on the same file (CpuBakeTests.PostFromValvesRaw,
      POSTFULL / POSTFULL_STEPS / POSTFULL_FIRST). Consolidate, fireflies,
      median, JFA, gutters and the weld were exact at full scale.
    - The AHD encode (18004cfc0) normalises the direction with the squared
      length dx dx + (dz dz + dy dy) (18004d64e). Ghidra prints it without
      the grouping. With (dx dx + dz dz) + dy dy, 350 direction and 479
      ambient texels differed by one ulp. Fixed, the encode is exact.
    - lightmap_image_write_ahd and lightmap_image_write_direct_light_shadows
      convert float to half rounding ties away from zero, as
      lightmap_image_save does. Every remaining difference was a tie:
      Valve's value was one ulp larger in magnitude, ours was even.
    - Result: our own bake, from scene_info.kv3 to the three EXRs,
      reproduces ze_hold_em_p's irradiance.exr, directional_irradiance.exr
      and direct_light_shadows.exr byte for byte. vrad3 itself does not run.
    - Rule: Ghidra may print a + (b + c) as a + b + c. Read the
      disassembly wherever float association matters.

85. **The .vmap_c's m_SubassetReferences** (settled 2026-10-19 by decompile
    and by all 24 of s2c_rc_probe's compiles, MapSubassetsTests).
    - The collector (vtable 182353d80, add at 180fc0ac0) keeps the pairs
      in a tier1 CUtlHashtable. Each entry is 0x20 bytes: flags and hash,
      type, name, count. The minimum size is 32 slots, the table grows past
      3 used per 4, a new key goes at the head of its bucket's run, and
      FLAG_LAST marks a run's last entry (shift 180fbfe10, resize
      180fc0110).
    - The hash (180fbfc90) is MurmurHash2 with seed 0x3501a674 of the type
      and of the name, each folded with h ^ h >> 15, then xored.
    - 1800fee50 copies the table into a fresh one (180fbf9f0, from the last
      slot back) and reports the copy slot by slot. The RED2 groups by
      type, entity first. The .vmap's map_subasset_reference_* attributes
      (180f71a50) are the same pairs sorted, a different list.
    - The walk is the main lump's: instance copies are spliced in where
      their parent's subtree ends and the templates are dropped. A path's
      node entities come before the path. A hidden entity, worldspawn and
      a class the FGD lacks add nothing.
    - Each entity adds ("entity", class), then each soundevent-typed key
      (FGD type sound, 0xd) in key-table order, class defaults included.
    - MapSubassets implements it. Earlier tries failed for three reasons:
      sounds added after all entities, the document order, and game
      keys left out.

86. **The .vmap_c** (settled 2026-10-19 against s2c_rc_probe's 23 current
    compiles, MapAssetsTests and MapRootFileTests; atixref is an older
    compile with another argument table).
    - Blocks: RERL, RED2, an empty DATA; resource version 1.
    - The RERL lists the children and the map's direct asset references,
      sorted ordinal:
      - every material on the faces of a visible mesh (prefab and instance
        contents included). A mesh under a class with
        auto_apply_material gives that material instead;
      - every material an overlay's meshData lists;
      - each walked entity's resource-typed keys from its key table, class
        defaults included. A value without a '/' is skipped (c2m2's bare
        particle names);
      - instance_file keys (a skybox map) and script keys, as
        scripts/vscripts/<value>, with .lua added when there is no
        extension;
      - the class's auto_apply_material;
      - bakeresource keys. The lightprobetexture ones are left out (the
        atlas is a child);
      - a CMapPath's or CMapCable's materialName;
      - a CMapSmartProp's smartPropFilename.
      Missing assets are listed too.
    - m_InputDependencies, sorted, in csgo_addons/<addon>:
      - lightmaptexturearg.txt when baked;
      - lightprobevolumetexturearg.txt when baked with probe volumes
        (both optional and absent);
      - each prefab's .vmap and the map, with their CRCs.
    - m_ArgumentDependencies: ___OverrideInputData___, then the arguments
      the steps read, sorted case-insensitively:
      - 21 for an unbaked compile;
      - 11 more when baked (lightmapMaxResolution twice, the second row
        0);
      - final and lightProbeVolumeTextureArg with probe volumes.
      FloatArg fingerprints are the value's float bits; the defaults are
      4, 4096, 1, 1048576 and -1.
    - m_SpecialDependencies: the identities of the child kinds, sorted. A
      .vdata brings KV3 Compiler Version 2 and VData Compiler Version 1,
      both CompileVData; a .vsnap brings SNAP Compiler Version 1.
    - The child list names entity models that failed to compile (a missing
      material, ze_hold_em_p's 8 doors), though the package has no file
      for them.
    - m_SearchableUserData: IsChildResource 0, UsesBuilderEntityKeys 1,
      WorldModelDocAll 1. m_SubassetReferences: ledger 85.

87. **A lit compile with no vrad3** (settled 2026-10-19 on ze_hold_em_p by
    bytes, compile-map -s2cbake).
    - Brush entity model texcoords: the model compile takes the mesh
      through DMX, which flips V there and back: 1 - (1 - v), rounded.
      All 7 end_breakable models are now exact; the round trip reproduces
      every texcoord of every model, which closes ledger 60's open item.
      It also makes scene_meshes.kv3 equal Valve's.
    - Vrad3Job writes the job folder from the compile's own state. Every
      input vrad3 reads equals resourcecompiler's: scene_info.kv3,
      scene_meshes.kv3, lightmap_info.dat, lightmap_packing_geometry.dat
      and lightmap_seams.dat. The script carries RC's quality tables
      (182a88550..885c8).
    - Light probe volumes on the GPU (RtxBakeHost.BakeLpv) with
      rtx_vrad3_lpv. D_LPV_PASS 0 voxelizes and 1 computes, with
      S_EXECUTION_REORDERING as the lightmap uses it. The push constants
      are 128 bytes: the row-major mat4x3 is three 16-byte rows.
    - The raygen indexes g_Luxels and g_LpvBlockConverged by the launch
      (LaunchSize.x (z LaunchSize.y + y) + x). Each block places its cells
      in its own box over the launch size. So each lpv_compute_block_gpu
      (at most 96 cells per axis) is its own launch: region-sized luxels,
      the block's box, its own convergence image, its random draws
      reseeded. Only g_SolidMask reads the bordered grid, at the packed
      start (low 16 bits) with the packed counts (high 16).
    - Result: all 16 probe volume EXRs, the four lightmap EXRs and the
      packaged lighting are byte for byte, except the dlshd atlas's 2 BC7
      blocks (bc7e, LIGHTING_ROADMAP L3; fixed by ledger 110). The package
      was 55 of 56 files and 99.90%, the same as with vrad3's outputs; the
      compile takes 278 s.

88. **The weld solves the baked shadow slots** (settled 2026-10-19 on
    probe01 and ze_hold_em_p by bytes, vrad3 run on the same raw image).
    - After channels 0-3, 8 and 13-15, uber_weld solves the four baked
      shadow slots as one RGBA float bitmap, with the same rows and the same
      conjugate gradient. lambda_1 (18005ca30) fills a slot's value, 0 where
      its light id is 0xff; lambda_10 (18005c840) writes back only the slots
      whose id is not 0xff.
    - The step runs when DAT_180347d70 is set. lightmap_image_gpu and
      lpv_image_gpu set it from vtable +0x58 of the scenesystem world
      "vrad3_raytrace_scene_world". The standalone scripts that load a
      saved image have no world, so the flag is off there. That is why the
      earlier step bisect showed equal shadows.
    - probe01's sun slot differed in 4,124 R texels along its seams; it now
      matches. ze_hold_em_p (37 baked-shadow lights) stays exact with the step.
      The port runs it always: with no baked-shadow light every slot is 0xff,
      the system is zero and nothing is written back.

89. **Shadow slot shapes of omni2 and rect lights** (settled 2026-10-19,
    decompile; atixref's full build, 33 of 33 keys).
    - BuildRecord (180f45900) reads the light's record with count 1 and
      flags 0, the whole record.
    - Shape (0xa8) above 0.05 gives the barn frustum hull. Otherwise:
      - FalloffRange (181293b30) is -0xc8 / 0xcc when 0x9c and 0xcc are
        nonzero, else FLT_MAX.
      - At FLT_MAX the point hull shape (180f48520) applies; it is not
        ported and the compile stops with a message.
      - Otherwise the sphere shape (180f48c50) applies, with the cone
        ConeRatio (1812933b0): 0xe0 / 0xe4, or -1.1 / 1.1 when 0xe4 is 0.
        It is forced to 0 when 0xe8 > 0 and the ratio is >= 0.
    - The sphere shape:
      - centre: the light's position; radius: the range;
      - 3x4: the light's with its rotation columns times the range;
      - hull: the assigner's icosphere. The cone table is walked in cosine
        order while cos <= the cone, so the last taken is the tightest
        enclosing cone.
    - The icosphere (180f3fbf0): the icosahedron (0.5257311, 0.8506508,
      faces at 182343bc0) split once.
      - Each edge's midpoint is normalised and kept unless it lies within
        1e-6 squared of a point already kept.
      - Every point is scaled by 1 / |centroid of the first triangle|
        (1.0592947).
    - The cone table: one entry per angle of i * 0.017453292 (i = 3, 6 ...
      180), plus 1.5707964, sorted by cosine.
      - Each entry (180f417f0) is keyed by cos t and holds rings at polar
        angle min(i s, t), with s = min(0.3 t, 10 degrees).
      - A ring has 24 points (cos p, cos a sin p, sin a sin p); an exact
        repeat of a kept point is dropped. The rings run until the polar
        angle reaches t - 1.1920929e-07, then the apex (0, 0, 0) is added.

90. **The path-trace scene's lights beyond barns** (settled 2026-10-19 on
    atixref's full build: 234 of its 243 light entries byte for byte; ze's
    lights unchanged).
    - Brightness: units 0 is tier0's V_exp2f(brightness) times the scale.
      That is the CRT's float exp2f (CrtMath.Exp2):
      - n = trunc(x), f = x - n, moved into [-0.5, 0.5];
      - Cody-Waite expf of f * 0.6931472f (1802bdb54).
      .NET's float.Exp2 differs by an ULP on fractional brightness, which
      put 84 colours off.
    - Luminaire tables (181297030):
      - QUAD: m_Positions (4 x 3 from 0xf0) and m_vNormal (0x120), 1812909e0;
      - SPHERE: m_vPosition (0xf0) and m_flRadius (0xfc), 181290c70;
      - TUBE / TUBE_WITH_END_CAPS: m_Endpoints (0xf0, 0xfc), m_Tangents
        (0x108, 0x114), m_flRadius (0x120) and m_flEndcapAreaPercentage
        (0x124), 181296d40.
    - Bounce scale other than 1 (181112e80): a separate entry first.
      - It is the record with its colour times bouncescale, flags 1 (9
        when it casts shadows), baked_shadow_index -1 and the light's
        illumination volume.
      - The light's own entry then has no bounce flag.
    - A point_template's lump (entitylumpname) holds its entities in the
      template's space. The scene places them by the template:
      disco_light_template at (-147, -3551, 281) moves its 8 lights.
    - Open: the order (LIGHTING_ROADMAP L17).

91. **The CS2 bomb damage bake** (settled 2026-10-19 by decompile of csgo's
    modtools.dll (20261015) and bytes. Fed Valve's own nav and world
    collision, probe01's and cardtest's baked_bomb_damage.vdata_c are byte
    for byte: positions, all 67,764 damage words, the text and the container.
    Maps/BombDamage.cs.)
    - Who: the map compile modifier CS2BombDamage. Bombsites are the
      func_bomb_target entities with bomb_damage_power above 0, in entity
      order.
    - Grid: cells of 10. Each nav area, in the mesh's order, and each
      triangle of its fan, from (v0, v[n-1], v[n-2]) down to
      (v0, v[2], v[1]), visits the cells of the area's bounds.
      - The bounds are snapped as v - fmodf(v, 10) - 5 and
        v - fmodf(v, 10) + 10 + 5; y, then x, then z, each from the top.
      - A cell is added when a box of half 10 meets the triangle (an
        Akenine-Moller SAT with unit edges, tolerance 0).
      - The bounds of every cell that meets a triangle are kept.
    - Lift: in the seed table's slot order, a cell with no seed right above
      it is lifted by floor(32 / 10) * 10 = 30 into the node table and marked
      written out.
      - A site holds the lifted node at distance 0 when its trigger collision
        overlaps the cell's box (+-5, unlifted).
      - The test is the trigger's vtable +0xd0 against an AABB shape. Touching
        counts as overlapping: site 2's face at x = 640 against cell 635.
    - Growth: rounds over the node table in slot order. A node not yet
      visited gets a box of half 5 cast by zero at it (CVPhys2World +0x558,
      shape type 2, delta 0).
      - A hit marks the node blocked.
      - Free space proposes (+-1, 0, -1), (0, +-1, -1) and (0, 0, -1) cells
        that lie in the seed bounds grown by 30 and are not visited.
      - The proposals join the table after the round. The rounds stop when
        none are proposed or after 64.
      - Only the lifted seeds are written out, but every node takes a slot,
        and that sets the order.
    - The mesh cast of an unscaled shape (CRnMeshShape 1802421c0 to
      180245a10) works on the raw values.
      - The settle port's MeshSweep divided by a refined rcpps of the scale:
        0.99999994 for 1. That moved boxes 1e-4 off walls at x = 1280 and
        y = 960 and changed the slot order.
      - MeshSweep.Query now takes a null scale for this path.
    - Distances per site (PopulateNodeDistancesFromBombsite):
      - The frontier starts as the distance-0 nodes, and every node takes an
        entry (FLT_MAX, no parent).
      - Each frontier node, in slot order, runs 26 neighbour jobs on the
        current records (steps at 180cd9170). Their results apply in step
        order.
      - A job runs when the node's distance is at most the neighbour's.
        Its parent is the node's own parent when the line between that
        parent and the neighbour is clear: a ray each way (+0x4f8), meshes
        hit on their front only. Otherwise the parent is the node.
      - The cost is the path back through the parents:
        length + clamp((length - 500) / 1000) * 20 * turns.
        - The length sums segments four at a time, each from
          z^2 + y^2 + x^2.
        - Turns are acosf of each corner times 57.29577637. A corner whose
          two segments agree to 0.01 per axis counts none.
        - A cycle gives -1.
      - A lower cost replaces the neighbour's record. A neighbour that is not
        blocked joins the next frontier.
      - The two frontier tables swap; the finished one is emptied in place
        and keeps its size.
    - Output, in node slot order:
      - positions: int16 x, y, z - 30 of the written-out nodes;
      - damage_values, per site then node: uint16 of the distance clamped to
        [0, 65535], then byte 2 = yaw / 360 * 255 and byte 3 = pitch of the
        direction from the parent;
      - within 1600 units the direction leans toward the site's bounds
        centre (its floor in z) by 1 - clamp((d - 800) / 800);
      - bombsites: mins, maxs, power per site.
    - The file: the modifier writes the .vdata text with SaveKV3 (CRLF, no
      final newline).
      - A blob over 32 bytes starts a new line at column 1 with "#[". Rows of
        32 upper-case pairs follow, every row but the first after depth + 1
        tabs. It closes with depth tabs and "]".
      - The text compiles as a child: RED2 with the text as an additional
        input (CRC, csgo_addons/<addon>), the override data argument, the
        CompileVData identities, generic_data_type and IsChildResource 1.
        Then DATA, then FLCI.
      - FLCI holds the content path and, per node in pre-order, the line and
        column delta-coded. Blobs sit at column 1.
    - CRT math as tier0 ships it: exp2f (1802bdd90) and acosf (1802df0a4).
      CrtMath.Exp2 and CrtMath.Acos.
    - Open:
      - nav generation (L9), which the bake samples: done, ledger 109;
      - the hull cast and ray (Valve's maps so far have meshes only);
      - the neighbour-index triangle variant (180243770);
      - the trigger overlap for a rotated or non-box trigger.
92. **Nav generation's Recast stage** (settled 2026-10-19 by decompile of
    navsystem.dll (20261015) and a Frida capture of probe01's compile,
    tools/re/capture_navgen.py. Fed Valve's own stage input
    (005_Recast_in), every one of probe01's 117 tiles matches Valve: the
    filtered solid heightfield span for span, the compact heightfield
    (connections, areas after erosion, distances, regions), the poly mesh
    word for word and the detail mesh bit for bit; the stage's output, 232
    polygons over 480 pool vertices, is exact. Maps/NavGen,
    NavGenRecastTests.)
    - The pipeline: RC's nav step (1800fc370) collects, then navsystem runs
      ~31 CNavGenPolyProcessor stages on a polygon list over a double vertex
      pool. A Stage1 works in place; a Stage2 writes a new list that the
      runner swaps in. Recast is one stage (180074330).
    - Input (1800852a0): every pool vertex, used or not, goes to float and
      through a 3x4 matrix, recast = (-0.0254 y, 0.0254 z, -0.0254 x): Recast
      runs in metres, y up. Each row sums as (p0 m0 + p2 m2) + (p1 m1 + m3).
      Polygons fan as (i0, i[k-1], i[k]).
    - Config: every length setting times 0.0254 (float). Hull radius plus
      *(180134b10)+0x58 (0.75). Heights in cells by ceilf (three hull
      heights; the second and third INT_MAX when off), climb by floorf, radius
      by ceilf, edge length by truncation, region areas squared, detail sample
      distance 0 under the cell size. Border = radius + 3; tile width and
      height = tile + 2 x border.
    - Tiles: (int)(extent / (tile x cs)) + 1 each way, x outer, z inner,
      each one job; results are collected in that order. A tile's bounds are
      bmin + i x tcs and (i + 1) x tcs + bmin, padded by border x cs.
    - Chunky mesh: 256 triangles a leaf, split at the middle after the CRT
      qsort on the longer axis' minimum; subdivide stops only past maxNodes.
    - Rasterising is Recast 1.5's clipper with 16-bit heights (0xffff). The
      walkable test is the unit normal's y against cosf(slope / 180 x pi).
    - Valve's own changes, all followed:
      - addSpan: the merged area is decided from the new span's top before
        the merge against each overlapped span's top. Equal tops take the
        larger area. A higher overlapped span gives its area beyond the
        climb, area 1 when it is unwalkable under a walkable new span, else
        its area. A lower one within the climb that is walkable under an
        unwalkable new span makes the new span area 1.
      - Low hanging obstacles: an unwalkable span within the climb of the
        last walkable span below it becomes area 1.
      - Ledges: neighbour columns outside the heightfield are skipped.
      - Low height: clearance must exceed one of three heights; a 0x3f span
        becomes 0x3c, 0x3d or 0x3e by the tallest it clears.
      - Compact heightfield: neighbours connect at the smallest of the three
        heights; three heights stored (0xc, 0x10, 0x14), climb at 0x18.
      - Erosion (1800aa900) replaces rcErodeWalkableArea: vector distance
        transforms (d, dx, dz) and cylinder probes into the solid heightfield
        (1800aaff0). Area-1 spans far from walkable ones are cleared,
        area-1 spans are classed by the first hull height that fits, spans
        near 0x3d/0x3e spans take that class, and spans within radius + 1 of
        a blocking column with solid across their height are cleared.
      - Poly mesh vertex buckets: (x x 0x343 + z x 799) & 0xfff.
      - removeVertex is Recast 1.4's: each rebuilt poly takes its first hole
        vertex's region, no multiple-region mark.
    - Upstream Recast 1.5 otherwise: the distance field and box blur, the
      level-stack watershed with border paint 0x8001-0x8004, region merge and
      filter, contours with hole merging (its diags[i] slip kept),
      simplification, triangulation (its next(i2) slip kept), poly merging,
      adjacency and portal edges.
    - The detail mesh (1800bd2e0) is Recast 1.5.0's with older pieces:
      - getHeightData seeds from the polygon's own region (no
        multiple-region guard); when the region has no span in the patch, the
        Recast 1.4 seeding (1800bc070): each polygon vertex's nearest span
        among 9 cells, then a depth-first walk that stops within one cell of
        the centre.
      - getHeight is 1.4's: floorf(f x ics + 0.01f), then the 8 neighbours
        (-1,0), (-1,-1), (0,-1), (1,-1), (1,0), (1,1), (0,1), (-1,1) by
        |h x ch - y| when the cell is unset.
      - triangulateHull is 1.5.0's first form: dmin starts at 0, so no ear
        is ever chosen and the fan starts at hull[0], left 1, right n - 1;
        then the shorter of the two perimeters (sqrt of z^2 + x^2) advances.
      - Edge samples: 1 + floorf(d / sampleDist), capped at 31 and at 126
        total vertices, simplified to sampleMaxError^2.
      - Interior samples only when the minimum extent is at least twice the
        sample distance (probe01 never is): jitter
        ((i x 0x8da6b343 & 0xffff) / 65535 x 2 - 1) x cs x 0.1, all float;
        delaunayHull with the 1.5 circumcircle (relative to p1).
      - No setTriFlags; the driver writes each triangle's edge flags with a
        0.001^2 threshold (1800bb260).
      - Vertices to world: x + bmin.x, y + (bmin.y + ch), z + bmin.z.
    - Collect (180082280): the output replaces both the polygons and the
      vertex pool. Each detail vertex goes through the inverted matrix
      (1800ed380: transpose, -(R^T t), all over s = m4^2 + m0^2 + m8^2 when
      |s - 1| >= 0.001) into the pool as doubles, tiles in order. Each
      detail triangle becomes a three-index CNavGenPoly (18004fe30): +0x40
      kind 1 for area 0x3d, 2 for 0x3e, else 0; +0x44 flags 4 for area 0x20,
      else 1; +0x60 5, +0x64 -1; +0x62 the stage's byte (180084da0).
    - The span flag bits above the area are never initialised by Valve
      (pool memory), so only the area is compared.
    - Open: the CNavGenPoly plane fields (18004d9a0); the oriented path
      (1800873b0); the monotone and layer region builds and the median
      filter (convars, off); delaunayHull and the 1.4 seeding are ported
      from the DLL but not yet hit by a capture; the RC collectors and the
      stages before Recast (ValidateVertices, WeldVertices, RemoveDegens,
      RemovePolysMarkedNoNav, TransformRecastNonZup); the 25 stages after.
93. **Nav generation's input and the stages before Recast** (settled
    2026-10-19 by decompile of resourcecompiler (20260923, ported to the
    installed 20261015), navsystem and vphysics2 (20261015), and a Frida
    capture of probe01. From the .vmap alone, the extracted triangles, the
    stage-000 polygons and pool, and the welded input to Recast are Valve's
    bit for bit; through the ported Recast stage, so is its output (232
    polygons over 480 vertices). Maps/NavGen/NavGenInput.cs, NavGenPolys.cs,
    NavGenInputTests.)
    - resourcecompiler does not collect geometry. MapCompile_BuildNav
      (1800fc370) runs its collectors (seeds, markups, ladders, links,
      spaces; 180f21760 onward) into a CNavGenInput, and hands navsystem the
      physics world its map compile built (the settle world: one static body
      per map mesh, the document's own body first) and the world's other
      bodies.
    - navsystem extracts (180048b20): each body of type below 2, in world
      order, whose bounds overlap the generation bounds when set and are not
      inside a NONAV markup, then each shape that passes the world's query
      test, through vphysics2's triangle source.
      - The query (180028ae0): interacts with solid, npcclip, window,
        passbullets, player, npc (0xc3021) plus navclip when the rules know
        it (name hash 0xdd7f7e69); excludes NavIgnore; queries as npc;
        static and keyframed bodies; group 3; flags 0x49. The test is
        vphysics2 1802facf0 on the shape's attributes (+0x50) after the body
        type check (1801ff7b0). probe01's sky shape (sky + StaticLevel) fails.
      - A mesh shape (type 3, 1802643e0) gives its triangles in BVH leaf
        order when its flags have bit 3, else in array order; three unshared
        vertices each, each the mesh vertex times the shape's scale
        (180260f50).
      - The body transform (vfn 0x1b0: position, scale, quaternion) on the
        new vertices: t = 2 (q x v), v' = ((q x t) + w t + v) s + p, summed
        as the code does.
    - Polygons (18004e4d0): the vertices onto the pool as doubles; one
      triangle per three indices, +4 and +0x64 -1, +0x60 5, the rest zero,
      the normal (18004d9a0) = (e2 x e1) of corners 0, 1, 2 in doubles,
      normalised above FLT_EPSILON and kept unless its length is 0.
    - ValidateVertices (1800776d0) fails on an infinite or NaN coordinate.
    - WeldVertices (180062410, tolerance the convar at *(180134b20)+0x58,
      0.001, as both doubles): each vertex's polygons per corner; a record per
      used vertex with its distance from the origin; MSVC std::sort on it;
      each record in that order opens a cluster and takes every later one
      nearer in distance than the tolerance, whose polygons all share its
      polygons' kind, within the tolerance of it, and whose move onto it
      passes 18005a240. The move check: each of the vertex's polygons that a
      collinear sweep (1800808f0) collapses is marked removed; a non-convex
      one (18005af00) or a triangle whose normal flips undoes the move.
      Polygons not removed are renumbered; the pool is one vertex per
      cluster at its first record's position.
    - RemoveDegens (18005b5f0): collinear corners dropped in place
      (18005b290), polygons left with fewer than three removed.
    - RemovePolysMarkedNoNav acts on NONAV volumes only;
      TransformRecastNonZup only when the map is not z-up.
    - Open: generation bounds (180046fc0) and NONAV markups (180049210, the
      NoNav stage, 180074590); hull, sphere and capsule shapes (vphysics2
      1802661f0's other cases); dynamic bodies at nav time (probe01 has
      none); the per-vertex attribute vector. (Recast's settings: 94.)
94. **Nav generation's settings and the stages after Recast** (settled
    2026-10-19 by decompile of resourcecompiler (20260923 and the installed
    20261015) and navsystem (20261015), and Frida captures of probe01 with
    every MergePolys inner call dumped. From the .vmap alone, the settings
    block and hull navsystem receives are Valve's, and stages 006 through
    018 give Valve's 42 polygons over 63 vertices, flags included.
    Maps/NavGen/NavGenSettings.cs, NavGenMerge.cs, NavGenStages.cs,
    NavGenInputTests, NavGenMergeTests.)
    - Settings (RC MapCompile_BuildNav): the project defaults start as the
      static block (1830237b0 in 0923, 183027600 in 1015: byte 1, tile 128,
      cell 2, cell height 2, region 8 / 20, detail 120 / 2, edge 1200 / 51,
      verts 4, small area -1); gameinfo.gi's NavSystem keys replace them
      through KeyValues GetFloat / GetInt (1818c8c60); NavAgentNumHulls is
      the deprecated path (not ported); then the hulls of NavHullsPreset
      ("default" when absent). With no CMapNavData RC logs "Nav settings not
      found" and uses the defaults.
    - A preset (1818c7c90) is a CNavHullPresetVData of
      scripts/nav_hulls_presets.vdata; when found and non-empty the hull list
      is cleared and each named CNavHullVData of scripts/nav_hulls.vdata is
      appended (1818ca350 moves m_agentBorderErosion from +0x30 to +8).
      CNavHullVData's constructor (1818f8200; 1818f9e80 in 1015) copies the
      static default hull (183027660 in 1015: enabled, radius 15, erosion
      -1, height 71, short height 35.5 off, crawl 17.5 off, climb 17.5, slope
      50, jump down 240, horiz 64, jump up 0), clears
      m_flowMapGenerationEnabled and sets m_flowMapNodeMaxRadius 400; the
      vdata overrides only its listed keys.
    - The map's block (182053dc0 from the CDmeNavData "navData"): the
      settings* attributes, +0x58 = 0, the hulls from the settingsAgent*
      arrays (settingsAgentNumHulls of them, flow map fields unwritten). Its
      hulls (1818ca150): an override vdata when not using project defaults
      (1818c8840, not ported), else the named preset, else the project's
      hulls or the arrays. MapCompile_BuildNav then takes (1818c7960): no
      preset named, the project's block when settingsUseProjectDefaults else
      the map's; a preset named, the project's numbers with the map's
      strings, hulls and +0x58/+0x59, byte 0 clear.
    - The RC nav input's +0x128 is set when the map has an
      info_nav_oriented_mesh (180f20e40); the input's constructor (1800fb1c0)
      leaves it alone. With it set, RemoveStrayVerticalPolys, MergePolys and
      LimitMaxPolyEdges pass the polygons through.
    - Stage convars (value at *(ptr)+0x58; names from their registrations):
      180134b20 nav_gen_degen_limit 0.001, b10 nav_gen_agent_radius_buffer
      0.75, bf0 nav_gen_vertical_limit 88, b00 nav_gen_remove_vertical_polys,
      b90 nav_gen_opt_to_quads, c30 ..._angle_limit 8, c00
      ..._planar_deviation_limit 4, a40 / ba0 ..._se_limit_start / end 1e-5 /
      0.1, a00 / 9d0 ..._weld_limit_start / end 0 / 0.01, c20 ..._num_steps
      6, ab0 nav_gen_max_edge_len_do_clip, b30 nav_gen_max_edge_len 512, a20
      nav_gen_max_edge_len_split_tol 24, 9f0 nav_gen_true.
    - Recast's polygons (180082280, 18004fe30): three corners, the normal of
      18004d9a0 over the output pool, kind and flags, +0x60 5, +0x62 0.
      UnTransformRecastNonZup acts only off z-up; RemovePolysMarkedNoNav
      changes nothing without NONAV markups.
    - RemoveStrayVerticalPolys (180075b90): keeps |nz| above
      V_cosd((float)88 * 0.017453292f).
    - MergePolys (180073ba0 via 180027dc0) works on 0x168-byte polygons
      (64 inline corners). Six rounds, the merge tolerance from 1e-5 and the
      weld tolerance from 0 stepping a sixth of the way to 0.1 / 0.01 each
      round (doubles of the floats); a round repeats weld (180061730: the
      weld of 93 into a new list), degens, edge merge, weld, degens, vertex
      merge while the edge merge merged, and all of it while the vertex merge
      did; then the fan split to 4.
      - The edge list (180056540): each live polygon's corner edges hashed on
        their smaller vertex % 0xfceb, each bucket MSVC std::sorted on
        (min << 32 | max) (180066b80), runs of one key folded into their
        first entry (two polygons at most, a third clears it), buckets in
        order; with lengths, std::sorted longest first (180067200, pred a > b).
      - Edge merge (18005a570): until a pass merges nothing, each edge two
        polygons share wound opposite, both live, same kind and flags,
        normals within the angle: the first's corners from after the edge,
        then the second's (under 65), collinear corners dropped, 3 to 16
        left, convex, no repeated corner, the radii (18004cd50: centroid
        18004ef30 scaled by 1/n, farthest corner) summing to 200 at most or
        the corners within 4 of their float least-squares plane z = ax + by
        + c (1800541a0, 1800ee9a0, normalised by 18001f7c0), still convex:
        both removed, the merge appended to the input list.
      - Vertex merge (18005ca90): sweeps until one merges nothing; at each
        vertex with two or three polygons (removed ones counted), each pair
        live, same kind and flags, normals within the angle, the vertex at a
        T (180081050, its prev/next neighbours collinear with it); a third
        polygon across the T (18005e8f0, the last one around both far
        corners, flags and angle only); split corners from the centroids'
        turn about its normal (18005dfa0) with side checks (180080c10); when
        the cut does not clear, the first Q edge it crosses in XY (18005e510,
        180080750) gives a corner, snapped within the tolerance or a new
        pool vertex; the third polygon grows into the gap and Q's remainder
        stands alone; the three are removed. One merge per vertex per sweep.
      - The fan split (1800584f0; LimitMaxPolyEdges 180059240 the same at 4
        on 0x88 polygons): a polygon past the limit becomes a fan of pieces
        from the first anchor corner whose pieces keep the largest fan area
        (18004d060, float sum of float |cross|) above 0.01 and smallest /
        largest above 0.05; the pieces keep the polygon's normal and fields.
    - LimitMaxPolyEdgeLen (180057b90 via 180027d90; limit 512, tol 24): each
      polygon then each queued piece: kept when no edge (summed y², x², z²)
      is longer than max(limit, 3 tol); else clipped (18004fe60, band ±tol,
      crossings 180080820) by the plane halving its bounding box across its
      longest axis (x when strictly longest, else y when longer than z, else
      z), each side fanned (18004bbe0) into quads with a triangle last, every
      corner a new pool vertex, normal from corners 0-1-2, flags written 0,
      +0x62 and +0x64 kept; the pieces are queued.
    - Open: the fan split's triangulation fallback (1800f3780, a float ear
      clipper with 1800f3d70's scoring; throws in the port; probe01 never
      reaches it); NONAV markups; non-z-up generation; stages 019 on.
      (Triangulation and stages 019-029: 95.)
95. **Nav generation after stage 018: triangulation, connections, splits**
    (decompile and disassembly of navsystem 20261015, 2026-10-20; checked
    against probe01's stage dumps where they show it. The connection blocks
    and the triangulation wait on oracles that need CS2 closed:
    capture_navgen.py now dumps each polygon's connection block and
    tools/re/call_navsystem.py calls 1800f3780 directly. Maps/NavGen
    NavGenTriangulate.cs, NavGenConnections.cs, NavGenSplits.cs.)
    - Ghidra prints right-nested float sums without parentheses: the ear
      score is inv + ((1 - c) + (1 - c)), printed as inv + (1-c) + (1-c).
      Float sums in this area are taken from the disassembly.
    - Convars: each registers its default (nav_gen_* init functions:
      dist_a 1, dist_b 1.5, dist_z_mult 0.5, connect_angle 0.75,
      angle_ignore_z 1, connect_overlap 0.5, allow_multiple 1,
      max_bottleneck_width 128, split_multi_connection_polys_tol 0.01,
      island_removal 0, island_removal_all_hulls 1, add_jumps 1,
      agent_radius_buffer 0.5, jump_connection_min_overlap_ratio 1, ...);
      csgo/gameinfo.gi's ConVars block sets agent_radius_buffer 0.75 and
      jump_connection_min_overlap_ratio 0.1, which is what runs.
    - Fan-split fallback (1800f3780, floats): 4 corners choose diagonal
      0-2 or 1-3 (1800f30b0: both triangles over 1e-7, facing the normal
      scores 3 else 2; diagonal 0-2 when its score is not lower and its
      squared length is within 1.01f of the other's; a corner within 1e-7
      of an edge fails when only one diagonal is valid); more corners clip
      the best-scoring ear (1800f3d70: area about the normal over 1e-7, no
      other corner inside (1800f33c0) unless it repeats a corner; score
      1 / max(min of its and its neighbours' ear areas, 1) + 2(1 - largest
      cosine)), rescoring the two neighbours; no ear scores 0 indices.
      Each triangle keeps the polygon's fields with its own plane; under 3
      indices drops the polygon.
    - GenerateConnections (18006ea50; invoker 180027ea0): keys
      (180077130: +0x10 the origin's distance to triangle 0-1-2, quads the
      nearer of 0-1-2 and 0-2-3 (Ericson's closest point 1800802b0);
      +0x18 the farthest corner, x²+y²+z² in the four-wide loop, y²+x²+z²
      after) and MSVC std::sort on +0x10 (180067920) match stage 023 bit
      for bit. Pairs i < j in that order (twelve thread chunks concatenate
      to it), j cut off when its key passes i's +0x18 by 2; an XY
      separating-axis prefilter on both polygons' edge normals with margin
      dist_a (18006d260, 18006d550; corners with z 0); per edge pair the
      overlap of 18006d750 (opposite directions, |cos| of the XY directions
      at least V_cosd(0.75), closest points (1800369e0) within dist_a with
      z times 0.5, clamped projection ends within dist_b with 6 units of
      height slack; the length run); the best pair, and the second when
      both run at least 1 and their edges are adjacent and within 60
      degrees on one polygon; recorded shorter edge first (18006aec0). Each
      pair is evaluated both ways (180053170: edges opposite within 0.001,
      each projected on the other in XY in floats (1800787d0), both
      overlaps at least 0.5, midpoints within 1 in XY or on the left);
      both valid adds each side to its polygon's block (18004c780: the
      0x68-byte records, each record's slot in its edge's list, five
      per-edge lists).
    - Connection-keeping splits: edge transfer (18004e040), link
      (18004db20, kept at overlap 0 or when valid), connection removal
      (180050240, swap with the last), polygon removal (180050400, the last
      polygon moves in and its links are rebuilt), corner split (1800521d0),
      corner-to-edge split (180051cb0, the cut edge's pieces at 0.1),
      two-edge split (1800507e0, at most 4 corners a piece).
      SplitBottleneckPolys (180075cc0) cuts a quad with no connections on
      edges 1 and 3 when edge 0 or 2 exceeds 128. SplitPolysWithMultiple
      Connections (18005be20) finds a corner between two edges reaching
      the same polygon (180056260, sorted by (polygon, edge)) and cuts
      across its neighbours' direction (180051990). RemoveSmallAreasOnEdge
      (180075900) drops one-connection polygons under the settings' small
      area (-1: none), compacting through 18006cb10.
    - AddJumps and IslandRemoval write each polygon's bounds over
      +0x10..+0x38 (180076d40); they match stage 026 bit for bit.
    - Open: AddJumps (18006b160: capsules per hull height from the
      physics interface vfn 0x1e8, world shape casts vfn 0x580 both ways
      in 180072ef0), ClipPolysToHullClearance (022, the world),
      LadderConnect (028, func_ladder input), IslandRemoval (029, walkable
      seeds, WALKABLESEED markups and nav links from the RC input at
      ctx+8 +0x78, +0x90, +0xc0), SplitPolysByMarkup (019); the RC
      collectors that fill that input; then the .nav writer.
96. **Nav generation's IslandRemoval (029), its GJK and the walkable seeds**
    (decompile and disassembly of navsystem 20261015, vphysics2 20261015
    (code identical to 20260924 at these addresses) and resourcecompiler
    20260923 (the Ghidra build), 2026-10-20. Verified with CS2 closed:
    tools/re/call_navsystem.py gjk, 4000 cases, both GJKs bit for bit
    (NavGjkTests); probe01's stages 024, 025, 027 and 029 from Valve's
    input, polygons and connection blocks exact (NavGenStageTests, seeds
    from the .vmap: one, (512, -64, 16)). Maps/NavGen NavGjk.cs,
    NavGenIslands.cs, NavGenMapInput.cs.)
    - Which runs: the stage list builds IslandRemoval twice, single-hull
      (nav_gen_island_removal, 0) then all-hulls
      (nav_gen_island_removal_all_hulls, 1), so 029 is the all-hulls one.
      LadderConnect (028) is enabled by its convar only with ladders.
    - IslandRemoval (180070780): marks every polygon removed, writes the
      bounds (180076d40) and fills a grid of 32-unit cells over the XY
      bounds (180046720: cells (int)(extent / 32) + 1 a side, none when an
      extent is not positive; 180046c80 adds a polygon to every cell its
      clamped truncated range covers; a query (1800475e0, 180046a40,
      180046a80) walks rows, then columns, then each cell's entries in
      insertion order, each entry once). Then: each nav link (+0xc0) pairs
      the first polygon in grid order touching a ±16 box round each end
      (18006e460; a warning when either has none); each WALKABLESEED
      markup (+0x90, the tag name through 18001ba90) floods the removed
      polygons its box (local min - 1 to max + 1 through its matrix,
      180078360) touches; each walkable seed (+0x78) floods the removed
      polygons a ±40 box (180129420) touches (180078490); each ladder's
      connectivity (ctx+0x90, 0x1c bytes) floods its ints +0x10, +0x14,
      +0x18, +0xc, +0, +4, +8 when not -1.
    - The flood (18006e5f0) is breadth first from its start (cleared),
      across connections (+8 the other polygon) then link pairs, to
      polygons still removed; each one reached is cleared and appended to
      a list (the std::function at 1800ff790, slot +0x10 = 180077c50). The
      start itself is not listed. All-hulls: while the list is not empty,
      take it and clear it; for each listed polygon, every grid neighbour
      still removed, both of kind (+0x40) 0, whose XY outline meets it
      (18006d260, margin 0) and whose GJK distance to it is at most 4.0
      floods. So a seed's own polygon does not pull in stacked neighbours;
      what its flood reaches does, and the order of seeds can matter.
    - Compaction: kept polygons in order (output vector), removed ones'
      blocks freed; per kept polygon each connection's +0 renumbered, its
      +8 renumbered unless -1, and removed (180050240, revisited) when the
      result is -1; +0x61 untouched. Ladder ints renumbered (-1 stays).
      Settings +0x58 set: the polygons move over unchanged.
    - The 4-unit distance is navsystem's GJK (1800a3060 -> 1800f09d0 ->
      1800eff20, 32 iterations, empty cache): point-cloud proxies (type 4,
      1800a2800, radius 0, scale 1) under the transform at 180139ed0,
      which the static initialisers make the identity (180004240 writes
      (0,0,0,1) twice before 180004200 copies it; table 1800fb948..58),
      so support points are the input points. Simplex vertices of 0x2c
      bytes (indices, point on A, on B, w = B - A), weights at +0xb4.
      Start: both first points, weight 0 then 1. Each round: solve (two
      inline; three 1800f14b0 with triangle barycentrics 1800ef850; four
      1800f1b80 with tetrahedron 1800ef460, four vertices = overlap),
      failing restores the last good simplex; v = the weighted w; stop
      when |v|^2 (y, z, x) does not drop; direction 1800f0a20 (-w; edge:
      (e x -w0) x e; face: normal flipped away from w0); |d|^2 under
      1.17549435e-35 = overlap; supports 1800f0c80 (farthest along d
      measured from point 0, ties keep the first) of A along -d and B
      along d; a vertex pair already in the simplex (compared as bytes)
      stops. Witness points 1800efb20; distance |pA - pB| (z, y, x;
      overlap 0); 1800a3060 subtracts the radii (0) and clamps at 0.
      symsse.py shows every sum in these functions left-nested.
    - The seed, markup and link boxes use vphysics2 instead:
      CVPhysics2Interface (VPhysics2_Interface_001, object 180460e60,
      vtable 1803fbf50) slot 0x190 = 180073970: two matrix3x4 and point
      lists through 18029b6f0 to the GJK 1802ec220 (its own variant:
      solver 180318460, support 1802eb9e0 with a box fast path, add
      180315ab0, distance x, y, z), then 0 when at most 0.125;
      navsystem's test is <= 0. Both box transforms are the identity
      (180110da0, 1800ff930). It is navsystem's algorithm and layout with
      every three-term dot product summed (x + y) + z where navsystem's
      is (y + z) + x (edges, lengths, triangle barycentrics 180315fa0,
      |v|^2, |d|^2, the face direction's sign test, the final distance),
      and a vanishing direction ends without forcing 0; the support dot,
      weighted sums and tetrahedron (180315bb0) are the same.
      NavGjk.PhysicsDistance.
    - Connection record +0x61..+0x67 after GenerateConnections: one side
      of each pair is stack garbage (probe01: 0xaa, then b9 ef e9 00 00
      00), the other zero; 18006cb10 (RemoveSmallAreasOnEdge's compaction)
      clears +0x61, so later stages see 0.
    - The world navsystem extracts from is resourcecompiler's own PhysDoc
      whose node filter (20260923 180f4d220, 20261015 180f4d790) runs
      with flags 0x16; the settle's PhysDoc is built with 0 (both captured
      by capture_navgen.py's PhysDoc/filter hooks on cardtest and
      s2c_prefabprobe4). Owner = nearest entity at or above the node:
      flag 0x10 with GenerateNav true keeps it; flag 2 makes a mesh under
      an entity need solidity 2 (missing reads 1) and, with 0x10, no
      AlwaysSolidIgnoreNav; flag 4 drops an owner whose solid key is 0.
      cardtest's func_brush (Solidity 1) is the case seen; with it out,
      cardtest from the .vmap is Valve's through stage 025 (292 triangles,
      55 polygons, 178 connections). NavGenInput.NavWorldFilter; flag 2's
      non-mesh test (entity +0x550, FUN_180dd2210), smart props and
      physics pins, and FUN_180f4bb00 are not modelled.
    - Hull shapes in the nav input (vphysics2 180130e60): the hull's
      vertices times the shape scale appended once, then each face fanned
      from its first half-edge's origin (next, next of next, until the
      next returns to the first).
    - Fan-split triangulation (1800f3780, ledger 95): call_navsystem.py
      triangulate, 4000 cases, every index list Valve's
      (NavGenTriangulateTests). GenerateConnections' blocks: probe01's 42
      polygons, 132 connections, Valve's (NavGenConnectionTests).
    - The RC seeds (180f21760): point_nav_walkable entities
      (FUN_180fc7220 search, flags 3), per instance path (FUN_180f88460,
      an identity path when none) the translation of the step matrix
      times the entity's. NavGenMapInput.Seeds takes them in the entity
      lump's order (copies before the entity they precede, templates only
      as copies, hidden left out); that ordering is unsettled.
    - The RC markups (180f209f0): func_nav_markup entities (180f1ef70,
      prefabs recursed with flag 4); each string key (type 5) named
      navProperty_<tag> registers (tag, value) in a table (1818c7010, 0x80
      bytes each) and adds a markup with that index (180f20720: the
      entity's geometry bounds and matrix per instance path, 0x50 bytes);
      with none added, a navProperty_NavAttributes array key (types
      0x22/0x23, 180f22330) adds one with index -1. Not ported.

97. **The .nav file: CS2's nav mesh, its build and its save** (settled
    2026-10-20 by decompile of resourcecompiler 20260923 (the Ghidra
    build; CS2's mesh runs server.dll 20261015's copy of the same library,
    read from its RTTI and disassembly) and navsystem 20261015. Verified:
    Valve's stage 029 polygons and connection blocks through NavWriter
    give Valve's .nav byte for byte up to the CS custom data on probe01,
    cardtest, probe_classes (a movable mesh), s2c_prefabprobe5 and 6
    (NavWriterTests). Maps/NavGen/NavWriter.cs, tools/re/navfile.py.)
    - Which mesh: the nav step's factory (1800fd2b0) asks gameinfo's
      CustomNavBuild (csgo: server.dll, customnavsystem001) for the mesh,
      a CSNavMesh (vftable 1817bebd0). It overrides 0x18/0x20 (CCSNavArea),
      0x60 (the CS analysis, called after the build), 0x70 (the custom
      data version, 1), 0x78/0x80 (custom data save/load) and a few
      more; the build (0x98) and the save are the shared library's.
    - After the stage list, navsystem appends each hull's polygons
      (18006bee0), runs JoinNonZup (180072b50, nav_gen_join_nonzup on;
      it only touches polygons with flag 8, the oriented-mesh path) and
      compacts the pool (180073ef0): vertices renumbered in first-use
      order over the polygons' corners, unused ones dropped. 180022200
      hands the pool, the polygons and the movable mesh list to vfn 0x98.
    - The build (1818d63c0): corners are the pool as floats; area i (id
      i + 1) is polygon i (1818ea6f0). Per edge, each connection of the
      edge's list (block +0x390/+0x398) of kind 0 whose other polygon is
      another area becomes (that area's id, +0xc of the connection).
      Jumps (kind != 0) are not area connections. Attributes: flag 8 ->
      0x8000, 0x10 -> 0x20000, kind 1 -> 0x10000, kind 2 -> 0x40000;
      area +0x58 is the polygon's +0x62.
    - The save (1818d1320): 0xfeedface, 36, the custom data version (1),
      +0x101 (1), the place names (u16 count, then a byte: some area
      uses name 0), KV3 vfn 0xd8 (movable_mesh_gravity_follows_rotation,
      a native int16 array; null without movable meshes), the pool
      (1818e9510: corners, polygons as byte n + n u32 + the movable mesh
      u32 (-1), the attribute flags 0, the movable meshes as the id path
      formatted "%d" then "%c%d" with ':' and the 3x4 matrix), KV3 vfn
      0xe8 (null), the areas (1818cfe80: id, attributes & 0x7f002 as u64,
      +0x58, polygon, 0.0f, per edge count + (id, edge), byte 0, u32 0,
      ladders up/down), ladders (1818e71b0), transformed bounds, the
      generation parameters (1818d0690: 13, the settings block, both
      strings, the hulls, +0x59), NavGenParams (1818d18f0) and the
      custom data. Each KV3 is binary_auto on an 8-byte boundary.
    - The movable mesh list entry (0x70 bytes, 1818ef5e0): id path count
      +0, ids from +4 (past five, +0x20 count and +0x28 pointer), matrix
      +0x38, gravity +0x68 bit 0. Captured by capture_navgen.py
      (navinput_movable.bin).
    - Native typed KV3 arrays (tier0 1800c5ea0): int16 elements are
      element type 0x14, uint16 0x15 (2-byte lane), as floats are 0x13;
      Kv3V5Writer treats all-Int16 / all-UInt16 arrays as native.
    - The custom data (CSNavMesh 0x78, 18039ac00 / 18039ad80):
      navmeshcustomdata1 { arealist [ per area hidingspotdata,
      spotencounterdata, order, approachdata, earliestoccupytime ] }.
      spotencounterdata's from/to and order's id are the low dwords of
      CNavArea* / HidingSpot* pointers at save time: heap addresses.
      Ledger 57's run-to-run difference is exactly those fields (two
      probe01 compiles compared field by field); everything else in the
      block is deterministic. The analysis that fills it is not ported.
    - Open: ladders (the build casts every 10 units through the world,
      1818dc620; ids from a process-wide counter), the CS analysis.
98. **Nav generation's AddJumps (026)** (settled 2026-10-20 by decompile
    of navsystem 20261015 and vphysics2 20260924 (world vfn 0x580 is
    180065410 in both 0924 and 1015). Verified: Valve's stage 026 input
    through NavGenPolys.AddJumps gives Valve's polygons and connection
    blocks on probe01, cardtest, probe_classes, s2c_prefabprobe5 and 6
    (NavGenJumpTests; 8 and 16 connections added, 14 of 62 casts blocked
    on prefabprobe5). Maps/NavGen NavGenJumps.cs, NavJumpSweepWorld.cs.)
    - Cylinders, not capsules (corrected 2026-10-28, ledger 174: the physics
      interface's vfn 0x1e8, 180071d70, is RnHullCreateCylinder): from
      (0, 0, MaxClimb) to (0, 0, height / 2 + MaxClimb), the hull's radius;
      height, short height and crawl height for polygon kinds 0, 1, 2.
    - Candidates (18006fcf0): each edge of a polygon with a connection
      block whose connections' overlaps sum to at most 0 or leave at least
      twice the radius open. Polygons without a block have none.
    - Targets (18006cf00): a 32-unit grid over the polygons' bounds (as
      IslandRemoval's), queried over the XY box of the edge and the edge
      moved by MaxJumpHorizDistBase along u x n (18004f120).
    - Pairs (18006bfc0, against every edge of every other target):
      180053170 with overlap radius x jump_connection_min_overlap_ratio
      (0.1 from gameinfo); kept when the overlap is not 0, the drop
      (midpoint z) is more than MaxClimb + 4 and at most MaxJumpDownDist,
      the target's kind is not above the source's, the midpoints are within
      MaxJumpHorizDistBase in XY, the cast is clear and no connection on the
      source edge covers the pair's midpoint. That flag (+0x61) is cleared
      per target polygon only, so it holds for the target's later edges.
    - The cast (180072ef0): a 4-point float path (180077e40, t 0.5): the
      source overlap's midpoint, above the target's at the source's height,
      the target's midpoint; each leg both ways through world vfn 0x580
      (a type-4 shape cast, 180201b00), blocked when the fraction is below
      1. Ported 2026-10-28 (ledger 174); the earlier NavTriangleWorld was a
      stand-in and blocked 532 jumps too many on s2c_prefabprobe3.
    - Order (18006b160): pairs sorted by squared midpoint distance (MSVC
      std::sort, 180073a20); each unmarked pair adds its connection
      (18004dd20): this side as found (kind 0) unless the polygon already
      connects to the other; the other side's pair (overlap 1) with kind 0
      when the distance is below max(MaxClimb, MaxJumpUpDist), else 1,
      unless already connected back. Later pairs whose midpoint lies in
      either side's span on the same edge are marked.

99. **Nav generation on prefab and instance maps** (capture_navgen.py on
    probe01, cardtest, probe_classes, s2c_pinst, s2c_pinst7 and
    s2c_prefabprobe3-6 with CS2 closed, 2026-10-20; disassembly of
    navsystem 20261015). From the .vmap, probe01, cardtest, probe_classes,
    s2c_pinst, s2c_pinst7, s2c_prefabprobe5 and 6 are Valve's through
    stage 025; stages 024-029 from Valve's input on all of them; the RC
    seeds on all eight maps (NavGenSeedTests).
    - The nav input's body transform (180049020, packed): t = 2 (v x q),
      then ((t x q) + (w t + v)) times the scale plus the position: the
      cross product is added to (w t + v), not (cross + w t) + v. One ulp
      on rotated bodies otherwise.
    - The fan split (1800584f0 MergePolys' last pass, 180059240
      LimitMaxPolyEdges): the success flag [rsp+0x30] is set to 1 once
      per polygon (180058785 / 1800594fd) and a rejected anchor stores 0
      (180058b7d / 1800598e9) that nothing resets, so after anchor 0
      fails every later anchor counts as failed and the polygon is
      triangulated (1800f3780). Valve bug, kept: only anchor 0 is used
      (s2c_prefabprobe5 polygon 87: anchor 1's 0.066 ratio passes the
      0.05 test, Valve still triangulates).
    - The settle world's body order puts a prefab map's nodes after the
      document's, in that map's own element order (the pointer sort of
      PhysDoc_BuildObjects over nodes loaded map by map); seen in the
      nav input of s2c_prefabprobe5/6.
    - The RC ladder input (CNavGenInput +0x60) is 0x28 bytes a ladder
      (top, bottom, normal, width as floats; FUN_1818ca470), from
      func_ladder meshes and meshes whose material has attribute
      0x1a382654 (180fb15e0, 180fb21f0: per instance path, the face of
      that material with the largest length times width via
      180fb1c10). s2c_prefabprobe4 has one: (1429, 830.5, 174) to
      (1429, 830.5, 40). Not ported, nor LadderConnect (028).

100. **Capsules in the nav input; the world's flat node array (L17)** (decompile, disassembly and
    capture, 2026-10-20).
    - Capsule shapes reach nav generation through vphysics2 18016f5d0 -> 18016ed50 (12 segments,
      2 rings): poles at (-r, 0, 0) and (L + r, 0, 0) in the capsule's frame, each segment's ring
      angle i * -6.2831855 / 12, ring k at k * pi * 0.5 / 3; records (position, normal) then each
      point through rows (axis, u, axis x u, centre A) as (A + y u) + (z w + x axis) (18008d6d0),
      u from 18008d5b0 ((1 - a.z) a.y^2 + a.z, 0, -a.x) normalised, made orthogonal and normalised
      again (180088f50: (y^2 + z^2) + x^2, untouched outside [1e-17, 1e17]). V_sinf/V_cosf are
      tier0's statically linked CRT sinf/cosf; MathF.Sin/Cos give the same bits on 20,015 samples
      (ctypes on tier0 20261015). NavGenShapes.Capsule; centres and radius scaled as the world
      builds the shape. Spheres (18016c9b0 -> 18016c380) not ported.
    - s2c_prefabprobe4's input (123,649 vertices) is not ours yet: its bodies come in an order
      other than element order (PhysDoc's pointer-keyed table), ~14,000 triangles differ by ulps,
      ~4,400 of ours are not Valve's and ~1,700 of Valve's (z near 14,200) are not ours.
    - The world's flat node array (CMapWorld +0x1828 count, +0x1830 nodes; index at node +0x498,
      set by 180ff00c0 in 10-15, appended by 180fca580 through 1800f7890) is what
      MapWorld_FindEntitiesByClass (10-15 180fc7790) walks. Captured on atixref
      (tools/entities/capture_entitysearch.py): 5032 nodes; the document's nodes in it are a
      subsequence of the world tree's pre-order walk (4559 of 4559 pairs in order), so loading
      appends in pre-order; instance copies sit partly at the end and partly in early slots, as a
      fast remove (the last node moved into a removed node's slot) would leave them. The light
      search ("light_*", caller 0xfc74c4) returns 241 lights in that array order. Open: the
      collapse's appends and removals (the capture now logs every index set).

101. **vphysics2's box cast and nav generation's ClipPolysToHullClearance (022)** (settled
    2026-10-20 by decompile of vphysics2 20260924 (instruction-identical in 20261015 at these
    functions) and navsystem 20261015; captured with tools/re/capture_navcast.py with CS2 closed).
    Verified: every shape cast navsystem made on s2c_prefabprobe3 and 4 replays bit for bit
    through Simulation/BoxCast.cs and HullCast.cs (NavCastTests: 955,324 + 549,575 mesh casts,
    17,467 + 37,112 hull casts, 10,109 + 21,190 of them hits); Valve's 022 input through
    NavGenPolys.ClipPolysToHullClearance with Valve's casts replayed asks exactly Valve's casts and
    gives Valve's polygons and vertices (NavGenClipTests): 243,480 casts, 10,497 polygons, 17,589
    vertices on prefabprobe3; 96,685, 3,966 and 7,056 on prefabprobe4 (one --with-navgen run).
    The hull cast also against vphysics2's 180251800 called directly (tools/re/hullcast_oracle.py,
    ctypes on the installed DLL): 300,000 random cases (boxes, clouds, slabs, points and segments,
    long rays, exact flush contacts), 144,084 hits, fraction and start-solid bit for bit
    (HullCastOracleTests). Never reached by either, so checked only against the decompile: capsule
    edges under 0.001 (the sphere cast), capsule rays under 1e-4 and parallel to the axis, the
    second face normal (0.5), Separated with four points, the double normalisation.
    - The path: world vfn 0x558 (180065360) -> CRnWorld::CastShape (1801f5080), type 2 ->
      CastBox (1801f29e0), the broadphase (1801d64a0), each shape's vfn 0x40. Meshes: 1802421c0
      picks 180245a10 (no shape flag, identity rotation, no translation) or 180244dc0 (local start
      ((sy t1 + sx t0) + t2 sz) + ((t1 n10 + t0 n9) + t2 n11), half extents |M| h, vertices
      row3 + (row2 z + (row1 y + row0 x)), the triangle box (min - 1/32) - h); triangles 180244380
      (180243770 for three wings, the same fraction). Closed meshes (+0xb4 bit 0): 18024ac40 ->
      180248a30, the start inside when the ray to the nearest bound face (ties z, then y; 18023fe00)
      hits a triangle facing along it, which makes the fraction 0.
    - The world's fraction is the least fraction of the shape casts that returned (1 when none):
      all 243,480 world calls on prefabprobe3. Which shapes are cast is the query's (static bodies,
      group 4, interacts with 0xc3021, excludes 0x400000); hulls take no max fraction.
    - Hulls: 180250540 -> 180251800. The hull's points scaled, moved to the frame of its
      transformed reference point; the box (support by sign bit, -0 picks -h) swept along the ray
      minus the hull is GJK'd (1802e8060: rays over 512 are first clipped to the hull's slab along
      the ray with 8/32 slack; 1802eacc0: up to 100 steps, simplex 1802e6ed0 / 1802e8750 /
      1802e8cc0, whose eight cases sit behind the jump table at 1802ea1f0; a repeated support point
      on a triangle tries the face normal, |n . p0| < 0.001; touching at w ~ 0 shortens the ray by
      tol 0.05 once). Separation seen: 1802e7840 (20 steps, 1e-4). Enclosed: the time of impact
      1802e7120 (1802e4cd0, 1802e4900 steps with 1/256 slack, 1802e52c0 for a triangle), then the
      contact depth 1802e5ab0 with margin 0 + tol 1/32 by simplex size: 1802e4200 (point; the
      segment-sphere clip 18037d090 takes the larger t), 1802e2e80 (edge; ray-capsule 18016caf0,
      ray-sphere 18016b0e0 / 18016b2f0 under 0.001, perpendicular 18008d5b0), 1802e3460 (face),
      refined through 1802eb5e0, 1802e5c80 (32 steps) and 1802e6780 (32 steps). The fraction is
      (ray length - depth) / length; a negative distance starts solid with tol / 4.
    - 022 (180054ae0 and helpers, NavGenClip.cs): the box (-r/sqrt2, -r/sqrt2, climb) ..
      (r/sqrt2, r/sqrt2, height) cast along each edge pulled in by min(0, centroid distance) with
      climb + 2.5, lifted by the deepest of four zero-width drop casts (half r/sqrt2/2, from
      min(climb + 5, height - r/sqrt2/2)); the first clear point 0.2 apart, the walks forward and
      back to the blocked stretches, then a midpoint split (length over 36, eps 0.5) or the
      perpendicular clip plane (d - 0.1 - 0.5, eps 0.1, the back kept); five non-reducing passes.
    - Valve's polygons vary from run to run on prefabprobe4 (ledger 100): its 022 input and its
      casts must come from one compile (capture_navcast.py --with-navgen).
102. **The world's node array after the instance collapse; scene light order and placement (L17)**
    (capture and simulation, 2026-10-20; ledger 100's open item). Verified: MapWorldOrder rebuilds
    the captured array of atixref (5032 nodes, all 854 SetIndexInWorld mutations in order),
    cardtest, probe01, s2c_pinst and s2c_pinst7 exactly (MapWorldOrderTests), and atixref's
    scene_info.kv3 lights (243) are Valve's text in order (SceneInfoTests.LightsAreValves,
    BarnTablesAreValves).
    - Load appends the world (its own id first), then every CMapEntity, CMapInstance, CMapPath and
      CMapPathNode in pre-order of the whole tree, target groups included. Meshes, groups, overlays,
      smart props and the loader's locator nodes are never appended.
    - One pass over the array by index, always advancing: a slot is not looked at again after a
      removal fills it. Each instance that is not inside a target group collapses: it appends its
      target's entity-like copies sorted by copy id (MapInstances.Expand's ids; a nested instance
      copy takes its block slot, start + its pre-order slot over every node of the target) and
      queues the nested instance copies; when no other live instance in the array uses the target,
      the target's own entity-like nodes are removed in pre-order; then the instance is removed.
      Removal is a fast remove: the last node moves into the slot, the removed node's index is -1.
    - The queue then collapses first in first out, by the same rule.
    - The scene build's light search walks this array, and places each light as its node is (the
      map's origin and angles, or the collapse's for a copy), not from the lump's origin: atixref's
      point_template 236 ships its eight lights only in its own lump (236#entitylumpname) relative
      to the template, and Valve's scene has them where the map puts them. One light per node.
      Vrad3Job.InSearchOrder.
    - Not captured: prefabs and hidden instances (MapWorldOrder refuses; Vrad3Job keeps the lump
      order for them).

103. **The .nav's CS custom data: earliest occupy times, hiding spots, encounter spots, approach
    areas; vphysics2's sphere cast** (settled 2026-10-20 by decompile and disassembly of server.dll
    20261015 and vphysics2 20260924 (identical in 20261015 at these functions); captured with
    tools/re/capture_navanalysis.py, CS2 closed, probe01 with -world -phys -nav -fshallow).
    Verified on probe01 from one same-run capture (NavAnalysisTests): the 42 areas, grid and 1386
    nearest-area lookups; 1344 A* travel distances and the 42 areas' occupy times; 25,269 mesh
    sphere casts and 15,842 world sphere casts (the analysis's traces, our settle bodies); the 42
    A* records after the occupy step and after the hiding-spot step; six hiding spots (ids,
    positions, flags) with 918 of 918 traces; the 42 areas' encounters; the 42 areas' approach
    entries, chained from our records; and the custom data block itself, 4220 of 4220 bytes once
    Valve's heap pointer dwords are put in.
    - Order (1803906a0): occupy times (1803935e0 / 180394e20), hiding spots (180394b10 /
      180394340, sniper classification 1803925e0), encounter spots (180393ea0 / 180391710), then
      approach areas per area (180393060). A trigger_survival_playarea skips the first three's work
      (refused).
    - Traces: the mesh's trace object (1814eb500 -> 1814eb630 -> 1814ea9a0) casts a sphere of
      radius 0.1 through vphysics2's world vfn 0x560 (180065370 -> 1801f5360) with the query
      0x3021, every body type, group 3, flags 0x49; static bodies keep their nearest hit, others
      add every hit; the broadphase (1801da640) moves start and delta into the shape's frame
      (R^T (start - origin); the delta (dy r1 + dx r0) + dz r2). The result's fraction is the least
      hit and its end start + delta * fraction. The mesh sphere cast is 180247c30 (BVH walk) with
      the triangle feature sweep 1803157c0 / 180315820 (entries 180313b80.., plain sweeps 180339a30
      vertex, 180339fc0 edge, 180339120 face), transcribed in Simulation/SphereCast.cs.
    - A* records (1814d7200, 0x30 per area by +0x64): position, parent, how (the edge, 9 at a
      start), edge back (0xff at a start). Every search shares them and leaves them as they are; a
      neighbour's record takes the search's position variable when pushed. CNavPathCost moves that
      variable to the portal crossing; the approach cost (1817bee30, vfn 0x90 180392280) leaves it
      at the start's centre and costs the distance between the two areas' record positions, so it
      reads positions an earlier search left. Approach areas therefore depend on the hiding step's
      sniper searches.
    - Hiding spots: a corner whose two edges have no connections (out or in), no spot of the area
      within 30 and an area under it (GetNavArea 1814b2910 with flags 2: the point's cell, contains
      in XY within 0.05, z from 80 below to 18 above, the nearest z, a later area winning a tie).
      Cover: from 35.5 up, a trace 20 up; clear, 16 traces 100 out (and 35.5 up) every pi/8, more
      than 7 blocked is in cover (1), else exposed (8); the first trace blocked is in cover.
      ClassifySniperSpot: the eye 62 up (attribute bit 10) else 37; a navsystem Dijkstra from the
      spot's area at its centre (1810f5f80 / 1810fb4e0; 1802c7fe0: the portal's closest point to
      the area's position, range 2000, areas whose closest point is 2000 or more away closed), then
      rings of hex-lattice samples over the visited areas inside the origin's 2000 box (spacing
      10/3 times 1.7 per ring, out to 4.1 times it, then 1.7 times the ring, the next from 1.01
      times the last), traced from the eye to 35.5 above; clear traces 1000 or more long count:
      ideal (4) when count * 100 >= 40000 or the longest >= 1500, else good (2), when the longest
      >= 1000.
    - Encounters: every ordered pair of an area's connections to different areas; the path between
      the portal midpoints, 62 above each area's z there; every 25 along it and at its end, each
      in-cover spot not yet seen on the path within 2000 (spot 62 up) with a clear trace to 35.5
      above it is seen, and kept when the dot of the directions lies strictly within +-0.7071 and
      the point is past the start, at its fraction of the path.
    - Approach areas: the eye over the centre (ground height 1814b22d0 down from 35.501 above, then
      63.9 up, 31.95 crouching); far areas at least 200 on each side
      (nav_approach_points_area_size_threshold) that are not potentially visible (180398290: the
      centre 53.25 up, corners 106.5 up whose direction differs by a dot below 0.98,
      nav_potentially_visible_dot_tolerance); the A* path both ways, its first area not potentially
      visible (one back at the far area) the next, the area before it here; the next goes on a
      blocked list and the path is rebuilt until it fails, is shorter than 3, the list holds 256
      (the area is done) or 16 entries exist.
    - The save (18039ad80): hidingspotdata, spotencounterdata (from / to the low dwords of area
      pointers; each encounter rewrites the area's order array, so it keeps the last encounter's
      spots: the spot pointer's low dword and t), approachdata (-1 for no area), earliestoccupytime;
      integers 0 and 1 as the int64 zero / one types, t and times as doubles.
    - Not ported (refused): the survival play area path, areas without polygons, special edges
      in GetNavArea. Start-solid traces and hull sphere casts: ledger 109. V_sinf / V_cosf are taken as
      MathF (only clear-or-blocked depends on them).
    - Open (inferred, not yet settled from the DLLs): the incoming-connection lists (CNavArea +0x90)
      the hiding-spot corner test reads are taken as the one-way connections into the area (another
      area connects to it on that edge and it does not connect back); probe01 has none. Not
      exercised by probe01: crouch areas (31.95 / 37), the second GetZ triangle, blocked-list
      exhaustion (256).

104. **The nav world's box casts from our own bodies; the nav world keeps no movable props**
    (decompile of resourcecompiler 20260923 and vphysics2 20260924 / 20261015, captured with CS2
    closed, 2026-10-20). Verified: NavBoxWorld over our settle bodies answers every one of the
    243,480 box casts ClipPolysToHullClearance made on s2c_prefabprobe3 with Valve's fraction bit for
    bit, and 022 from Valve's input then gives Valve's 10,497 polygons (NavGenClipTests
    .ClipMatchesValveOnOurWorld; probe01 and cardtest likewise). NavGenPipeline now runs 022.
    - A world cast (CastShape 1801f5080 -> CastBox 1801f29e0): the box's centre is (mins + maxs) * 0.5
      plus the start, its half extents (maxs - mins) * 0.5. Each broadphase tree whose kind (+0x50)
      meets the query's type mask (+0x2f) is walked (1801d64a0); nav's query (18004b370) is
      interacts 0xc3021, excludes 0x400000, as 0x80000, static only (1), group 4, flags 0x49.
    - Each leaf: the body type against the mask, the attribute test (1802facf0), then the shape's
      cast (vfn 0x40) with the best fraction so far, kept strictly below it; a fraction of 0 ends the
      walk. The shape transform: q (+0x120) as rows, doubled sums taken once and added to
      themselves (t0 = 1 - 2(z z + y y), t1 = 2(w z + y x), ...), and the world mass centre (+0xfc)
      less q times the scaled local mass centre (+0x88 times +0xc8). Meshes take 180245a10 when the
      shape has no scale flag (+0xd1) and t's diagonal is 1 with no translation, else 180244dc0;
      hulls 180251800 about their centroid. Our bodies are SettleWorld.CreateWorld's (mass update
      included).
    - The nav world (PhysDoc filter 180f4d220 with flags 0x16) drops every non-mesh node under an
      entity whose FGD class (+0x550) lacks has_static_mesh metadata (180dd2210; only prop_static
      and cable_static set it in base.fgd): prop_physics, prop_dynamic and prop_door_rotating are
      neither extracted nor cast against. Captured (tools/re/capture_bodytypes.py): every body
      navsystem tests on s2c_prefabprobe3 is static (type 0) in broadphase tree 0, 1,156 of them;
      our extraction without those props has Valve's 305,836 input polygons, with them 317,508.
    - Not ported: casts against spheres and capsules, a smart prop's +0x688 test and physics pins in
      the filter (refused).

105. **atixref's path-trace scene from the .vmap: smart props, material overrides, unnamed vertex
    fields, the IBL's sun threshold** (oracle: Valve's scene_info.kv3, 2026-10-20). Verified:
    atixref's whole scene_info.kv3 (1,869,622 characters: 4,945 instances, the IBL and 243 lights)
    is Valve's text (SceneInfoTests.SceneInfoIsValves); probe01 and cardtest still are.
    - A CMapSmartProp's models enter the scene as the world's props take them
      (NodePropAggregates.SmartPropModels): prop_static records with the node's keys and the
      element's material group as the skin.
    - materialoverride (the record's +0x118) replaces the material of every draw of that instance;
      the shared mesh keeps the draw's own.
    - A vertex field without a shader semantic takes the name of the draw material's input
      signature element (INSG m_elems, the 0xc4-byte records 1801d3050 reads) with its D3D semantic
      name and index: m_pSemantic. That covers the unnamed RGBA8 TEXCOORD4 of a 2-way blend
      (VertexPaintBlendParams) too.
    - sun_light_min_brightness is 32 when the light has no sunlightminbrightness key (csgo.fgd
      removes the key; lights_base.fgd's default is 32): atixref's light_environment has none and
      Valve writes 32.0, as in every reference scene.
    - colormode 1 takes the light's colour from colortemperature (181007c90, Tanner Helland's fit
      with its float constants from the disassembly; through 180eed4c0 for directional lights and
      180eea510's barn branch for the rest); any other mode leaves the colour 0. tier0's V_powf and
      V_logf equal MathF.Pow / MathF.Log on 207,687 inputs (tools/re/tier0_powlog_oracle.py,
      Tier0MathTests). Open: probe_classes' lumens barn is 3 ulps off in its intensity, and
      s2c_prefabprobe5's lights need the world order with prefabs (not captured).

106. **The world's node array with prefabs (L17)** (capture and simulation, 2026-10-20). Verified:
    MapWorldOrder rebuilds the captured arrays of s2c_prefabprobe5 (145 nodes) and 6 (152) exactly,
    the earlier five maps still exactly, and s2c_prefabprobe5's light search (77, 2245, 2203, 2248,
    2246) is Valve's; its scene lights then differ only in one light's intensity by a few ulps
    (as probe_classes' lumens barn).
    - A prefab's map is loaded as a world of its own (its own array, indices from 0), and its
      CMapPrefab node is entity-like in the main array.
    - In the main pass the prefab collapses like an instance: its map's entity-like nodes are
      appended in pre-order under Expand's ids (block start + PrefabSlots slot), its map's
      instances among them queue (never collapsed by the pass), then the prefab is fast-removed.
      The queued instance collapses as before, its target's originals being the renumbered nodes.
    - The prefab's own array is drained into the main one node by node (fast removes there, by an
      always-advancing index, then the moved-in nodes in their original order); only the append
      order matters to the main array.
    - The entity lump names a prefab map's entity by its id path (hammeruniqueid "2140:77"); the
      scene's light lookup maps it to the array id (MapWorldOrder.Order's ByIdPath).
    - Open: s2c_prefabprobe3 (Valve's id set, a few nodes in another order from index 2463) and
      s2c_prefabprobe4 (its prefab map, atixref with a smart prop, numbered one off from
      PrefabSlots in places: 697 ids). Captures in %TEMP%/entitysearch_capture.

107. **Nav generation's ladders: the RC collector and LadderConnect (028)** (settled 2026-10-20
    by decompile and disassembly of resourcecompiler 20260923, navsystem 20261015 and vphysics2
    20260924. Verified on s2c_prefabprobe3: the collector gives the captured ladder bit for bit,
    (1712, -3219, 198) to (1712, -3219, 64), normal (-4.371139e-8, 1, 0), width 32
    (NavGenLadderTests). LadderConnect from Valve's 028 input, tracing through our nav world,
    gives Valve's connectivity (5048, 5046, 5048, 4705, 4727, 4727, 4973; captured by
    capture_navgen.py as navinput_ladderconn.bin), then Valve's 029. Maps/NavGen
    NavGenLadders.cs, NavBoxWorld.CastRay.)
    - The collector (180fb15e0) fills CNavGenInput +0x60. First the func_ladder entities: the
      flat node array's (+0x1828/+0x1830, byte +0x370 set), then each sub-map's (180fb1380,
      180fc8c80). Then the root's children, skipping those entities (180fb2830): a mesh
      (vfn 0x138) whose material list holds a material with a non-zero int "mapbuilder.ladder"
      (hash 0x1a382654, material vfn 0x50) goes to 180fb21f0; any other node recurses into its
      children. Last, each func_ladder entity's child meshes go to 180fb21f0 too. A func_ladder
      mesh without a ladder material gives nothing (180fb21f0 needs one).
    - Per instance path (180f88460, 0x58 bytes), 180fb21f0 takes the path's step matrix times
      the node's own (vfn 0xa0, 181258890). Among the faces whose material is the first ladder
      material in the list, in face order, it keeps the largest |top - bottom| x width; the
      first face wins a tie. One ladder per placement.
    - A face's ladder (180fb1c10):
      - min and max of the corners through the matrix (1810ce420 -> 18139b130 -> 18125d1f0,
        the same transform as MapMeshes.Transform); the centre is (max + min) x 0.5;
      - n is the face's Newell normal (181384e40) rotated by the matrix (18125d1b0);
      - u = (nz 0 - ny, nx - nz 0, ny 0 - nx 0), normalised with squares summed y, z, x;
      - v = n x u, normalised with squares summed z, y, x. Lengths between 1e-17 and 1e17 use
        the reciprocal; zero stays zero; others go through VectorNormalize_Slow;
      - half = |(vz dz + vy dy) + vx dx| x 0.5 and width = |(uz dz + uy dy) + ux dx| over
        d = max - min;
      - top = v half + centre, bottom = centre - v half; the normal is n.
    - LadderConnect (1800732d0) fills ctx+0x88/0x90 with seven polygon indices per ladder.
      The polygons go into IslandRemoval's 32-unit grid. The probe height is
      (top.z - bottom.z) x 0.5, at most 120 (minss).
      - Bottoms start at (bottom.x + 0, bottom.y + 0, bottom.z + 25) + n x 16. They probe along
        n, (-ny, -nx, -0) and (ny, nx, 0), into +0x10, +0x14, +0x18. The second direction is
        not a perpendicular in general (Valve's, kept).
      - Tops start at n x 16 + top. They probe along -n, (-ny, -nx, -0), (ny, nx, 0) and n,
        into +0, +4, +8, +0xc. A top equal to the bottom found the same way (-n and n against
        the n bottom) becomes -1.
      - Then each bottom polygon whose lowest corner (float) is more than 71 below the
        ladder's bottom becomes -1. "Unconnected ladder" messages only report.
    - A probe (18006e170) takes up to three steps of 16 along its direction, accumulated in
      float. Each step is traced from the start to the step's end through the world (vfn
      0x4f8). A fraction below 1 ends the try; otherwise 18006de40 looks for a polygon at the
      step's end. Seven tries; each moves the start's z by 25 / 6 (up for tops, down for
      bottoms; x and y get + 0). None found gives -1.
    - 18006de40 queries the grid over the point's XY +- 0.45 (doubles). Bottom probes skip
      kinds 1 and 2. A polygon counts when no corner lies above the point's z + 18 or below
      z - height. The nearest wins, by 18004cc30's squared distance, a later one only when
      strictly nearer. 18004cc30 takes the least over the fan (v0, v[n-1], v[n-2]) down to
      (v0, v2, v1), through 1800802b0, summed dy^2 + dx^2 + dz^2.
    - The trace: CRnWorld::CastRay (1801f4ad0 via 180064f20). The query has layers 0x3021,
      none excluded or queried as, group 4, flags 0x49, static bodies only (+0x2f = 1).
      - Each shape (1801d8580) gets the ray in its frame: the start less the translation,
        then each component (y t1 + x t0) + z t2 against NavBoxWorld.Transform's rows.
      - Meshes (180242360 -> 1802406c0) divide the ray by their scale and are hit on their
        front only. Hulls (1802505c0 -> 180251dc0 -> 1802544a0) clip it by their planes with
        a 1/32 margin: entering raises (d0 - 1/32) / (d0 - d1), leaving lowers
        (d0 + 1/32) / (d0 - d1). A start inside every plane hits at 0.
      - A hit below the best so far is the result. Only a fraction above 0 lowers the best,
        so a negative hull entry still blocks. 0 ends the walk.
    - IslandRemoval floods each ladder's ints (ledger 96). On s2c_prefabprobe3 the ladder
      does not change 029: from Valve's 028 input, 8,562 polygons with or without it. Its
      029 gap from the .vmap (210 polygons) is AddJumps (026): from Valve's 026 input our
      NavTriangleWorld casts give 30,074 connections, Valve 30,606 (2,036 of 15,106 blocked).
    - Open: func_ladder (the pipeline refuses it: its nav world and order are not
      verified), the order of several ladders, hidden ladder meshes (left out, as the
      compile leaves hidden meshes out).

108. **Light unit maths and the scene's cameras (L21); a material vrad3 cannot load (L19)**
    (settled 2026-10-20 by disassembly of resourcecompiler 20260923, tier0 20261015 and
    scenesystem 20261015, and vrad3's host upload captured on cardtest with CS2 closed).
    Verified: probe_classes' and s2c_prefabprobe5's whole scene_info.kv3 are Valve's (lights,
    cameras), with atixref, cardtest and probe01 still Valve's (SceneInfoTests); cardtest's four
    lightmap EXRs from a GPU bake are byte for byte vrad3's (CpuBakeTests, CPUBAKE_GPU=1).
    - FUN_1812918f0 (a barn frustum's solid angle) calls tier0's V_acosf and V_atanf, and sums
      the first spherical triangle as (u + t) + (u + t) - pi and the second as
      t3 + (t1 + t2) (181292344, 1812924e8). ConeAngle (181298b90) and the inner cone
      (1812969d0) call V_acosf too. tier0's atanf (V_atanf 180007ef0 -> 1802e057c, one
      build) is ported as CrtMath.Atan: in double, the argument reduced at 0.4375, 0.6875,
      1.1875, 2.4375 (hi atan 0.5, atan 1, atan 1.5, pi / 2) and a 3 / 3 rational; it and
      CrtMath.Acos equal the installed tier0's V_atanf / V_acosf on 629,132 and 413,800 inputs
      (Tier0TrigOracleTests, called in-process).
    - The scene record's omni branch (180eea510, 180eeae24) takes the cone from the keys:
      outer_angle clamped to [1, 180], inner_angle to [0, outer], 180 when absent; the solid
      angle (1 - (cos inner + cos outer) / 2) 2 pi, the lumens factor that times 1 / (4 pi).
      Light_SyncBrightnessUnits keeps the record's acos round trip (LightUnits.Terms).
    - Cameras: the map compile's entity pass keeps each point_camera (180243b04): targetname
      ("" without one), its placed origin and angles, FOV (0 when absent); scene_info.kv3
      writes them after the lights as cameras { name, origin, angles, fov } when there are any
      (180285ff1).
    - A material vrad3 cannot load (cardtest's teleport02, in another addon) is drawn with
      rtx_default_hitgroup and the attribute-less block (0, 0, 0, 1, -1, -1, flags, 0), first in
      instance order, and its mesh's vertex buffer holds the position alone (stride 12, no field
      infos): GeometryInfo_t, the material data and every material offset equal the capture.
    - The IBL transform's inverse is MatrixInvertTR (the rotation transposed, translation
      -(t . row)): it keeps the transform's -0 (m_mIBLTransformI col 0 z on cardtest).
    - Not equal, and not read by any shader: bindless texture / sampler indices (vrad3's run
      from 248 up), the global BRDF LUT and IBL texture indices, PackedRtxInstanceData_t
      +0x14..+0x1c (Valve's are uninitialised memory). Open: cardtest's probe volume 471278508
      differs in 1 to 3 of 8,064 texels per band (ours deterministic over two runs); its
      471278509 is identical.

109. **compile-map generates the nav with its CS custom data and bakes the bomb damage from it**
    (settled 2026-10-20 by disassembly of resourcecompiler 20261015, server 20261015 and vphysics2
    20260924 / 20261015, and a Frida capture of cardtest's analysis with CS2 closed,
    tools/re/capture_navanalysis.py s2c_rc_probe cardtest). Verified from the .vmap alone, against
    Valve's full compiles in %TEMP%/vrad3_refs: on probe01 and cardtest the .nav is Valve's up to the
    custom data, the custom data is Valve's but for the heap pointer fields (spotencounterdata
    from / to, the order ids; their KV3 type follows their value), and baked_bomb_damage.vdata_c is
    Valve's byte for byte (390,594 and 391,043 bytes), through MapCompile.Run -nav as well
    (NavCompileTests).
    - The nav step (Maps/NavCompile.cs) runs NavGenPipeline on the map's settle world (unsettled),
      builds the mesh, then CS's Compute AI Data and the save. The spawns are the visible
      info_player_terrorist and info_player_counterterrorist entities in entity order. Our
      pointer fields are the area and spot ids. Movable meshes and trigger_survival_playarea are
      refused; with --skip-unported a refused nav leaves the package's.
    - The analysis traces the nav PhysDoc's world: the bodies NavGenInput.NavWorldFilter keeps
      (flags 0x16), not the whole settle world. cardtest's Solidity 1 func_brush (node 2139) is
      the difference: with the filter all 29,866 world casts up to the last area's approach step
      are Valve's; without it 329 are not and area 24 gains an approach entry. Three casts after
      the last approach step (one ground search at (-0.25, 0.25, 44)) do meet that func_brush;
      nothing of them reaches the .nav, and which step makes them is not read.
    - The hull's sphere cast (CRnHullShape vfn 0x20, 180250b20): a hull with a region SVM (+0xa8)
      runs 180257420 first (not ported, refused). Otherwise 180253ac0: the ray (1802e2c30) cut by
      1802e6e60 to f = min(1 / length * 0.3125 + the max fraction, 1) of its delta (length and end;
      the delta and direction stay), then the GJK cast HullCast already ports (1802e2ce0,
      1802e7fc0 -> 1802e8060) on the point (0, 0, 0) (18006d110) with the radius as its margin,
      against the hull's points as stored. A scaled hull uses 1802e2bd0 (not ported, refused).
      HullCast.CastSphere answers cardtest's 4 hull sphere casts bit for bit.
    - A start-solid ground trace (1814b2585): the next try starts from the trace's start when its
      start-solid flag (+0x2b) is set, else from its end position. A start-solid cast has fraction
      0, so the end position is the start and the two agree; the refusal is gone.
    - The bomb damage is a post-build step: after every compile resourcecompiler (1800fe5f0) runs
      each key of gameinfo ResourceCompiler/GameSpecificPostMapBuildSteps as a map compile
      modifier; CS2 lists cs2_bomb_damage (modtools 18003ad70). compile-map runs it after the nav
      step on whatever nav the package then holds, before the map root, so the .vmap_c lists the
      file as Valve's does.
      - The sites: func_bomb_target entities with bomb_damage_power above 0 (the FGD's 3000 when
        unset), in entity order; bounds from the trigger mesh's world corners. Only an
        axis-aligned box trigger outside instances and prefabs is ported (the overlap is then the
        box test); others are refused.
      - The world: world_physics.vmdl_c's meshes (BombDamage.MeshWorld); a world with hulls,
        spheres or capsules is refused (the hull box cast and ray are not ported for the bake).
    - The RED2 of the bomb damage file records the addon the map compiled in: Valve's references
      were compiled in s2c_rc_probe.
    - Open: the region SVM pass and scaled hulls in the sphere cast; whether the late three casts
      come from a step that writes anything; the survival play area path.

110. **bc7e is the 8-wide AVX2 build** (settled 2026-10-20 by disassembly of the game's bc7e.dll
    and an in-process oracle, Bc7eOracleTests). Verified: the BC7 blocks probe01's
    -lightmapVRadQuality 2 build encodes equal the game's bc7e.dll (level 6, not perceptual) on all
    1,584 (5 differed); probe01's default, -lightmapMaxResolution 8192 and
    -lightmapCompressionDisabled lightmaps/ files are still byte for byte; quality 2's dlshd atlas
    is now too (its irradiance waits on the BC6H hue-shift encoder, L2); ze_hold_em_p's seven
    lightmaps/ files are byte for byte, the probe atlas's 2 blocks included (CpuBakeProbe
    .LightmapOutputsFiles).
    - bc7e.dll's dispatcher (180001000) ranks the CPU 0 to 5 (SSE2, SSE4, AVX, AVX2, AVX-512 KNL,
      AVX-512 SKX); bc7e_compress (1800014d0) runs one function, 180166df0, for 3 and up, so AVX2
      and AVX-512 CPUs both run it. It works on ymm registers: ISPC's avx2-i32x8 target.
    - Our build was avx2-i32x4, which encodes 5 blocks (4 distinct inputs) differently. Rebuilt
      avx2-i32x8 (ispc 1.24, the rcpps table rewrite unchanged), it gives the game's blocks on all
      of them, on Linux and Windows.
    - bc7e.ispc is vendored in Native/bc7e (bc7enc_rdo c4a839c, Apache 2.0, NOTICE.md); the build
      no longer downloads it. Windows ispc 1.24.0 lives in D:/tools/ispc-v1.24.0-windows.
    - vpkstuff's BC7 output moves to these blocks with its next compiler package.

111. **The BC6H hue-shift fixup encoder** (settled 2026-10-20 by disassembly of resourcecompiler
    20260923 and tier0 20261015, with an in-process tier0 oracle). Verified: probe01's
    -lightmapVRadQuality 2 lightmaps/ files are all byte for byte, irradiance.vtex_c included
    (CpuBakeProbe.LightmapOutputsFiles); the default build still is. Texture/Rc/Bc6hHueShift.cs.
    - A final-quality lightmap's BC6H (hueShiftFixup, encode flag 4) goes through
      FloatBitMap_t::WriteToBufferBC6H_ispctexcomp_highest_quality_tweaked (180c46dd0). Each block
      keeps the encode with the lowest error over a list of trials, starting from FLT_MAX:
      - unmodified;
      - colour consistency over the whole block at a minimum brightness of 0, 0.005, 0.01, 0.015,
        0.02, 0.025, 0.05, 0.075 and 0.1;
      - colour consistency per BC6H partition 0..31 at 0.01, 0.03 and 0.05 (the partition table at
        182290480 is the standard one);
      - k-means quantisation to 15 down to 3 colours.
    - A trial takes a block only when its best error is at least 1.0, and only when every pixel's
      (r^2 + g^2) + b^2 is below 1e12, before and after the change.
    - Trials queue blocks. The queue holds max(block rows >> 5, 256) rows of blocks and is flushed
      when an enqueue finds it full and once at the end, so a trial sees only the errors of
      flushed batches. A flush encodes the batch (veryslow, the quality-4 settings), decodes it to
      half floats and scores each block; a lower error replaces the block's.
    - The error (180c360c0): per pixel s = 0.5 / maxss(0.25, the largest of the channels clamped
      at 0); the clamped original and the decoded pixel times s go to CIE Lab (D65, 181269b90) and
      the squared differences are summed as (dL^2 + da^2) + db^2 over the 16 pixels in order.
    - Colour consistency (180c2ed30): per region the Y-weighted mean of the xy chromaticity and the
      brightest Y (normalised by max(sum Y, 1e-8)); each pixel becomes the mean xy at
      max(Y, brightest * minBright), back through XYZ to unclamped RGB. A block whose regions both
      have sum Y <= 0 is skipped.
    - Quantisation: mathlib's CKMeansClustering on the 16 pixels with one thread. Centres start at
      distinct pixels picked by tier0's CUniformRandomStream seeded -0x75b2fe6f. Assignment takes
      the first nearest centre. Each round: an empty cluster takes the first largest cluster's
      last member, centres become means ((1 / n) * sum), a move over 0.001 or an empty cluster
      asks for another round (at most 64). The pixels then take their cluster's mean.
    - Lab's cube root is tier0's powf (V_powf -> 1802e5130), FMA3 build (1802e5896), which every
      FMA3 CPU runs: ln x from a 257-entry table and a cubic (within 1/16 of 1 a series that
      joins the SSE2 build's exp), then exp from 2^(j/64). CrtMath.Pow equals the installed
      tier0's V_powf on 554,490 inputs (Tier0TrigOracleTests.PowIsTier0s).
    - BC6H decoding is the D3D11 spec's integer decode (Texture/Rc/Bc6hDecoder.cs), equal to
      TinyBCSharp's on 200,000 random blocks of all 18 mode codes (Bc6hDecoderTests).
    - Open: our flush applies results in slot order; Valve's runs them in parallel, so only an
      exact error tie between two trials of one block in one batch could differ.

112. **The lightmap's adaptive pass count** (settled 2026-10-20 by decompile of vrad3 (1001 build;
    the installed one keeps its addresses) and a raw-image capture with CS2 closed). Verified:
    probe01's -lightmapVRadQuality 1 job (lightmap_compute_block_gpu 128 512 0.050), block 0 baked
    by the GPU host is vrad3's lightmap_image_save image bit for bit: all 40 channel planes and the
    luxel records, sample counts included (CpuBakeTests.Bake, CPUBAKE_MINSPP / CPUBAKE_ERR;
    tools/vrad3/post_oracle.py bake made the capture).
    - lightmap_compute_block_gpu [minspp 128] [maxspp 256] [err 0.02] (180030100) runs passes in
      command lists of 16. Before pass 0 and before each list that starts at or past minspp it
      dispatches lightmap_test_converged (static combo 0) over the block with g_nBlockWidth,
      g_nBlockHeight, the tolerance (0 at pass 0), the luxels and the converged texture. Passes
      inside a list skip the test. The raygen skips converged 4 x 4 groups.
    - The test (4 x 4 luxels a group): no luxel with a sample is converged, two or fewer is not;
      otherwise converged when the luminance's standard deviation (wave sums) is within the mean
      times the tolerance. Our host runs Valve's SPIR-V for it (RtxBakeConverged), since the wave
      sums' order is the GPU's.
    - Quality 1 and 2 also change the post-process: lightmap_image_filter_oidn 1 and
      lightmap_image_filter_sh_oidn 1 in place of the median filter, and
      lightmap_ameliorate_block_compression_artefacts -mips 1 after the weld (L14, open).

113. **vrad3's OIDN lightmap filters** (settled 2026-10-20 by decompile of vrad3 and vrad3 itself run on
    the same image with CS2 closed, tools/vrad3/post_oracle.py run). Verified: on probe01 quality 1's
    raw image (lightmap_image_save after the compute), Lighting/LightmapOidn.Filter and FilterSh
    give vrad3's lightmap_image_filter_oidn 1 and lightmap_image_filter_sh_oidn 1 output bit for
    bit on every channel (LightmapOidnTests, saved as half floats).
    - The filters call Intel Open Image Denoise 2 (the game's OpenImageDenoise.dll and device
      modules) with a device of type 0, the default, which picks CUDA on this RTX 4070, then
      RTLightmap with float3 colour and output in device buffers, maxMemoryMB 6000. OIDN on CUDA
      repeats exactly, so calling the same library on the same device type reproduces vrad3.
      Valve's output therefore depends on the compiling machine's GPU vendor, as bc7e and powf
      depend on its CPU (ledgers 110, 111).
    - filter_oidn: ambient occlusion (180054f50), then irradiance (180056470); filter_sh_oidn: SH L0
      (180057d80), then coefficients c1..c8 (1800597d0, directional).
    - A pass ("lg") takes the texels of large charts (valid, a chart, neither small-chart flag)
      into a width x height float3 image, zero elsewhere.
      - Twice, each empty texel with more than one valid texel in its clamped 3 x 3 window takes
        their mean, summed in row order and multiplied by 1 / n. It counts as valid only after
        the round.
      - The filter runs, and its output goes back to the large texels.
    - Irradiance and L0 scale each chart by count 0.25 / sum of ((g 0.7152 + r 0.2126) + b 0.0722)
      first, and by the inverse after. A chart whose sum is 0 keeps scale 0 and comes back 0.
    - Coefficients are divided per colour by max(L0, 1e-5), with L0 as the L0 pass left it, and
      multiplied by L0 after.
    - Line charts (small in y only) then go ("sm") into bands five rows high across the image: the
      chart's first row three times, its end texels repeated one column out, 8 texels apart.
      - A batch (width x band rows) is filtered when the next band would leave fewer than 10 rows.
      - Its results go to the chart's first 2^(mips - 1) rows.
      - After the last chart a batch is filtered only when more than one band was used.
      - Irradiance and L0 line charts are not scaled; coefficient line charts are.
    - Open: lightmap_ameliorate_block_compression_artefacts -mips 1, which quality 1 and 2 run
      after the weld (it changes 0.53% of the AHD texels and most of channel 8 on probe01).
      Settled in ledger 114.

114. **vrad3's lightmap_ameliorate_block_compression_artefacts** (settled 2026-10-20 by decompile of
    vrad3 (Run 1800672e0, vrad3_lightmap_ameliorate_bc_artefacts.cpp), Frida traces of FillBlocks and
    vrad3 itself run on saved images with CS2 closed). Verified: on probe01 and ze_hold_em_p quality 1
    images saved right before the command, Lighting/LightmapAmeliorate gives vrad3's image (channels
    as half floats, shadow records, luxel records apart from the bits below) and chart_repacking.dat
    byte for byte (LightmapAmeliorateTests, AMELORACLE). An oracle must load the saved image: run
    straight on from uber_weld, vrad3 sees floats the halves round, and FillBlocks keys differ.
    - Phases: sanitize line charts (180066c60; empty texels copy the last filled one to their left,
      then right, the last texel carried from row to row); each chart's first texel of its own id
      (lambda_6); a chart is a pixel chart unless one of its sampled texels differs from that first
      texel (18006caa0, compare 180064f30: R only within 0.04 for channels 8-13, 15, 17-19, else
      each component within 1.5% of the larger magnitude and at least 1, then the shadow floats'
      zero-ness and 0.04; a NaN difference counts as different).
    - The luxel hash (1800648f0, seed 0x7a17e123, MurmurHash2 mixing, boost combine): per live
      channel the four components, 0.1-rounded for the scalar channels, else 0 below 0.01f and the
      bits masked 0xffc00000; then the shadow record, floats 0.1-rounded and each light index byte
      read as 0xff where its float rounds to 1.
    - Buckets of texels sharing a hash (a pixel chart counts only its first texel), then aliases
      (18006cc70): each pixel texel, last first, takes the first other-chart texel it matches (its
      chart and offset); the pixel texels left alias later matching ones to themselves.
    - Pixel charts (length 1) and unaliased line charts (height must be 1) group by length. None:
      the command ends without a file. Else chart_repacking.dat's header and per-texel chart ids,
      longest first into temporary rows (first fit), then the clear (18006dbc0): empty texels, the
      luxel flags' small-y bit and pixel charts become {-1, 0, flags 0xfffffff8} (vrad3 leaves the
      bits above the three flags from the stack: 0xfffffff8 on every probe01 texel, other values
      on ze_hold_em_p, different on each run of Valve's own), shadow {0xffffffff, 1, 1, 1, 1},
      channels 0.
    - Free lines (FindLongestLinesCoveringCells 180065690): rounds over the fully cleared 4x4
      blocks; each cell gets its row and column run lengths; a row run whose cells are all at
      least as long across becomes a line, a column run whose cells are all longer down; sorted by
      length, horizontal first, y, x (a total order, so the jobs' order does not matter). Longest
      lengths first go into the longest lines, from each length's list end; leftovers re-sorted
      until a round places nothing; then the copies back (18006def0).
    - FillBlocks (180066230): a 4x4 block with 1 to 15 filled texels fills its empty ones per
      channel with the most common colour of its filled texels, keyed in a 32-slot CUtlHashtable
      by R, G and A rounded to 1/255 with V_roundf (B is computed and dropped: a typo), the first
      texel's exact value per key; among equally common keys the largest squared length, then slot
      order. The key's fourth float is a stack slot never written: Frida reads 0 on vrad3's 15
      worker threads and a run-dependent small value (0x495, 0x34b, 0x50a as bits) on the thread
      that dispatches. It moves only exact ties: 113 of ze_hold_em_p's part-filled blocks tie and
      Valve's run disagrees with slot order 0 in two of them (24 texels), probe01's one tie agrees.
      We use 0; Valve's own output can differ there run to run.
    - The record per chart: {x, y, flags (bit 0 vertical, bit 1 pixel), size}; an aliased chart
      takes its target's new corner (or its packing corner) plus the offset, swapped for a
      vertical target (lambda_40).
    - Quality scripts (resourcecompiler 1801eda50): the OIDN flag (182a88550) replaces the median
      with filter_oidn and filter_sh_oidn; ameliorate follows the weld when BakeLighting repacks
      (quality 1 or 2, no noRepack, gameinfo NoRepack or disableLightingCalculations).
    - The packer's passes follow -lightmapVRadQuality (lightmapChartPackIterations' default 4, 7, 9):
      Vrad3Job and the world node bake now read the compile's arguments over the world keys, and
      probe01's packing geometry equals Valve's quality 1 and 2 jobs byte for byte (ChartMeshesTests
      .PackingGeometryIsValves, LMGEOM_ARGS). Which wins when the map's world keys also hold a
      setting is not settled (probe01 holds none).
    - Open: the repack iterations (Run 18006e380 on: Update Block Endpoints ... Execute) that run
      when charts are left after the free lines; neither map reaches them, and the port throws.
      Also -mips above 1 (the mip image and the upsample back); the scripts pass the map's mips.


115. **Light cookies and the fog irradiance volume** (settled 2026-10-20 on a specimen, s2c_l5probe:
    probe01 plus a baked light_barn and light_omni2 with materials/effects/lightcookies/box.vtex and an
    env_volumetric_fog_controller with IndirectEnabled 1; tools/vrad3/l5_specimen.py, compiled with
    Valve's tools, captures in tools/re/out/captures/l5probe). Verified: compile-map -s2cbake on it
    writes all eight lightmaps/ files byte for byte (the four lightmaps, query data, both probe atlases,
    fog_irradiance_volume.vtex), the entity lump and scene_info.kv3; package 99.92% (nav and .vmap_c).
    - scene_info.kv3 (181113af0): a cookie light keeps m_nCookie -1 in its barn table and gets a first
      field light_cookie = resource_name:"<the lightcookie key>"; omni2's m_bSphericalCookie as before.
    - vrad3 (scenesystem 18008c110): the cookie becomes a bindless texture index and m_vCookieParameters
      (0, 0, 1, 1). The texture is read sRGB: block 0's raw image is then vrad3's on all 40 planes (read
      linear, 4,300 to 6,900 irradiance texels differ). The light buffer otherwise equals vrad3's capture.
    - A sphere luminaire's scene table: m_vPosition (0xf0) and m_flRadius (0xfc).
    - Fog (resourcecompiler 180240a60): every env_volumetric_fog_controller gets IndirectVoxelDimX/Y/Z
      after hammerUniqueId: IndirectVoxelDim (256) / the box's longest side scales each side, truncated;
      x and y at least 4, rounded up to 4; z at least 1; each at most the dim. A baked compile adds
      fogirradiancevolume = maps/<map>/lightmaps/fog_irradiance_volume.vtex. The first controller in
      export order is the volume: its box moved by its origin, identity local-to-world.
    - Script (1801f01a0): fog_irradiance_volume routine after the probe volumes: lpv_cells, lpv_box,
      lpv_image_fog_gpu 96 96 96, lpv_voxelize_gpu (no flood fill), lpv_compute_block_fog_gpu per 96
      block with samples (64, 64), (256, 512), (512, 1024) by quality, lpv_image_median_filter_fog 3,
      lpv_image_write_fog fiv.exr. Valve's progress print says X x X x Z.
    - vrad3 180035e80 with the fog flag: m_nFog 1, error 0.02 (the line has none), readback of each
      cell's first ambient cube vector RGB (unclamped) at (z * X + x, y); alpha 0. The fog median is
      the probe median's job once (one face). fiv.exr byte for byte.
    - fog_irradiance_volume.vtex (1801f07d0): 3D BC6H, z slices of x by y, no mips, argument
      fogIrradianceVolumeTextureArg, output channel rgb from rgb, image processor "none". RED2's
      arguments sort by name ignoring case after ___OverrideInputData___ (the fog argument lands
      between final and hueShiftFixup; the lightmap and probe atlas orders are unchanged).
    - Far field: CS2's FGDs lack env_farfield_probe_volume but 180240a60 handles it (box_mins,
      box_maxs, cell_size 256, five env_farfield_<n>_* textures). Valve's own compile of a 6 x 8 x 1
      grid (s2c_l5farfield, cell_size 256) fails: "Farfield atlas slice ... is 1056x33, expected
      1056x36", then "Light mapper didn't return valid triangles", map build FAILED. With cell_size
      128 (s2c_l5farfield128, 12 x 16 x 2) it builds and ships env_farfield_0_indirection, _sh2_dc
      and _sh2_l1r/g/b; vrad3 reports its far field lighting 100% black on this map. Ported: ledger 116 (the black lighting was bouncelight).

116. **The far field probe volume** (settled 2026-10-20 on a specimen, s2c_l5farfield128: s2c_l5probe
    plus an env_farfield_probe_volume, box -768 -1088 -112 to 768 896 176 at 512 96 112, cell_size 128,
    and bouncelight 2 on the two lights and the light_environment; tools/vrad3/l5_specimen.py farfield128;
    Valve's compile in tools/re/out/captures/l5probe/ff128.*; tools/vrad3/ff_oracle.py reruns vrad3's
    far field routine alone with the atlas saved after each stage, ff128_stages and ff128_stages2).
    - Lighting only gathers lights with path_trace_light_flags & 0x40 and the sky with
      m_nSkyLightFlags & 0x40: bouncelight 2 ("Realtime", commented out of lights2.fgd). Without it
      the far field is black.
    - resourcecompiler 180240a60: each env_farfield_probe_volume takes the next +0x314 index and a
      0xa50 record (+0 local-to-world from origin and angles, +0x30 box_mins, +0x3c box_maxs, +0x48
      cell_size, default 256, +0x4c index). After hammerUniqueId: farfield_indirection, farfield_sh2_dc,
      farfield_sh2_l1_r/_g/_b through EntityKv_SetResource, empty without baked lighting, else
      <map>/lightmaps/env_farfield_%d_{indirection,sh2_dc,sh2_l1r,sh2_l1g,sh2_l1b}.vtex. The %d is
      local_1a98, which only the cubemap / probe branch sets (the +0x310 counter): the export runs
      once per entity, so for a far field it is an uninitialised local; 0 in Valve's capture. We
      write the volume's own index (what 1801f0dd0 names the textures by).
    - Script (1801ee9f0), after the fog: run compute_farfield_<n> per volume, then each routine:
      farfield_box, farfield_local_to_world (%f), farfield_cells = round to nearest even of the box's
      side over cell_size, at least 1, farfield_compute_bricks farfield_<n>_bricks.kv3,
      farfield_voxelize_bricks, farfield_compute_lighting 1024, farfield_median_filter 1,
      farfield_dilate 3, farfield_filter_fireflies 4.0, farfield_resample_borders,
      farfield_write_atlas ff/farfield_<n>; not quality dependent.
    - farfield_compute_bricks (vrad3 1800204d0), farfield_0_bricks.kv3 byte for byte:
      the grid record g_Grids (0x8c: world to cell = 1801f4490's inverse with row i times 1 / cell
      size less mins / cell size, cell to world, the box's world bounds by 1801f4f70, counts);
      occupancy = rtx_vrad3_mark_occupied D_MODE 0 (a triangle against the cell grown to 0.5001 on
      13 axes; ported on the CPU, the OR makes dispatch order immaterial) and D_MODE 1, 26-neighbour
      dilation, ceil(2 max cell / max cell) = 2 passes; metrics = rtx_vrad3_farfield_metrics (Valve's
      SPIR-V on our device with VK_KHR_ray_query, 8 samples a cell at the half-cell sub-centres, 64
      rays of length 2 max cell (max cell 32 * 0.03125 * 2), flags 2 unoccupied, 1 backface majority
      over 0.4); levels 18001fa00 (min usable escaping fraction f over the 8; all flag 1 0xff, none
      usable 7; else cvtss2si(max(0, c - 1) + 5 remap(f)) clamped 0..5, remap through 0.4 - 0.4 c);
      budget 18001fc60 ((int)(MB * 1 MiB / 143748), cap 0x7c0, less 1, at least 1; MB 271.99 default
      0x4387fdf8; over budget bisects c in [0, 6] 12 times, then steps 0.05); allocation 18001e4b0
      over the six axis orders, the fewest bricks, first on a tie; 18001ea40 level 0..5 in the
      order's loop nest, a cell at the level and uncovered gets a brick, level 0 the cell, else the
      first of the 8 corner placements covering the most uncovered cells under level 6; sort 180026b70
      by level, z, y, x; root (level 7) in front; descriptors 18001f590: the finest brick over each
      cell, the first on a tie, index | level << 12 | dx << 15 | dy << 20 | dz << 25, 0x7000 for none;
      the KV3: bricks {level, origin, min, max (box space)}, cell_count, cells_x/y/z, cell_descriptors.
    - voxelize + lighting (180035580, 1800358b0, set g_vrad3FarfieldSet 180034a80: g_Luxels 158 of
      48-byte FarfieldLuxel_t, g_SolidMaskBits 159, g_LightBins 160, g_RandomSamples 161, g_Bricks 162 a
      buffer; push FarfieldPushConstants_t 80 bytes: estimate, dimension count 4, filter radius 1,
      first brick, cell counts, cell to world rows): D 0 then, per batch of 512 bricks, D 1 spp times
      with the Halton volume regenerated per pass, launch 512 x 64 x bricks. The atlas (180021fb0):
      4 images x 33 slices of 1056 x ceil(bricks / 32) * 33 RGB floats, brick b's tile at
      ((b & 31) * 33, (b >> 5) * 33), dc = floats 0, 4, 8, sh1_r 1-3, sh1_g 5-7, sh1_b 9-11. The raw
      atlas is vrad3's byte for byte (132 EXRs), with its solid count and DC statistics.
    - Median (180023510): per brick from a snapshot; taps self, -x, +x, -y, +y, -z, +z in the brick and
      not solid, insertion-sorted by 0.2126 r + 0.7152 g + 0.0722 b of dc (ties after); odd count the
      middle (written unless it is the texel), even (hi - lo) * 0.5 + lo. Byte for byte.
    - Dilate (180022330, 180022b90): passes of copy-valid, then per brick the solid texels with valid
      27-neighbours (outside the brick through the descriptors, 180021480, cvtss2si clamped 0..31;
      none for the root) take sum * (1 / n). Valve's 12 sums start at each texel from the stack slots
      of the last mean written and are never cleared (asm 180022cf6), and a brick's first fill from
      whatever the worker's stack held: Valve's own two runs differ (40 of 132 slices; 2,764 texel
      channels). Ported as one thread in brick order with the carry kept; the 678 channels we differ
      on where Valve's runs agree all lie in chunk-start bricks (chunks of bricks / (2 (threads + 1)))
      and the bricks after them. 92/132 slices exact.
    - Fireflies (180023dc0): per brick, dc luminance g 0.7152 + r 0.2126 + b 0.0722 snapshot; a texel
      with l > 0 whose in-brick 26 neighbours' mean times 4.0 is in (0, l) has all 12 values times
      m / l. Borders (180024400, 180021670): each tile's 33rd row, column and slice, trilinear (x, then
      y, then z; corners clamped 0..31, fractions to [0, 1]) in the brick the position falls in (its
      own at its own coordinates over the root). Every final-stage difference is a border or a texel
      the dilate already changed.
    - Textures (1801f0dd0): env_farfield_<n>_indirection = the descriptors' bits in the red of an x by
      y * z bitmap, 3D R32_UINT (VTexFormat 31), z slices, no extras (DATA's extra offset 0);
      _sh2_dc BC6H and _sh2_l1r/g/b BC7 from the 33 EXRs stacked (1056 by the atlas height rounded up
      to 4: RC fails a 33-row atlas expecting 36, Valve's 256 build); RGB bitmaps (mask 7, alpha 0 in
      the reflectivity and the BC7 input); descriptors "3D", processor none, linear, no mips,
      m_bNoLod and hidden set (flags 0x28), output channels r / rgb; arguments
      farfieldIndirectionTextureArg, farfieldAtlasTextureArg. All five byte for byte from vrad3's files.
    - End to end: compile-map -s2cbake on s2c_l5farfield128 against Valve's package: the entity lump
      (far field keys included), env_farfield_0_indirection and the other eight lightmaps/ files byte
      for byte; the four atlases differ only through the dilate above (sizes within 27 bytes).

117. **The cubemap array's compile: GGXCubeMapBlur and the radiance SH** (2026-10-21, decompile and
    disassembly of resourcecompiler 0923, the same code in 1015 at +0x40; docs/CUBEMAPS.md).
    - Settled by oracle: the GGX and cosine tap tables (rc 1800caa20 and the layer's inline loop).
      `tools/re/emu_cubeblur_tables.py` runs the installed resourcecompiler.dll's own code under unicorn
      with tier0's V_sincosf / V_log2f through ctypes; GgxCubeMapBlur.GgxTable / CosineTable equal all
      1,001 tables (sizes 16 to 1024, every (mip / d) and (mip / d)^2 for d up to 15) byte for byte.
    - Settled by oracle: tier0's V_log2f (the CRT's float log family 1802e33b0, selector -1, all float, no
      dispatch; not correctly rounded, so log2 in double is 1 to 2 ulps off on many inputs).
      CrtMath.Log2 equals it on 1,166,310 inputs (tools/re/tier0_log2_oracle.py), zeros, denormals,
      infinities, NaN and negatives included.
    - Read: the schedule's mip count is the destination's full chain (rc 180c49440, 8 for 128), not the
      6 the texture keeps; flags 3: GGX at (mip / (count - 4))^2 on mips 1 to count - 4, cosine on the
      last three. The quad, the face rows (rc 1827685f0), the strip, g_nSampleCount 2048, the RGBA32F
      target, mip 0 the input itself; rendersystemvulkan builds the source mips by blits
      (r_mipgen_compute_shader defaults to 0).
    - Read: the radiance SH (rc 1801378c0 .. 180138100, basis 18125d4d0, VectorNormalize 18013e340):
      the weight is (m k)(m m), not ((m k) m) m as Ghidra prints it.
    - Approximate first (GpuCubemapBlurProbe on ze_fast_escape_p's shipped array, its BC6H mip 0 as the
      input): GpuCubemapBlur with Valve's convolve_environment_map SPIR-V on the RTX 4070 gives mips 1 to
      5 within 1 to 5% mean relative error and the SH within 0.2 to 0.4% of the shipped floats, BC6H's
      noise in and out. With a 6-mip schedule, mips 1 to 3 were 20 to 90% off, which is how the full
      chain rule showed.
    - Settled 2026-10-21 by oracle (tools/re/cubemap_oracle.py: RC 1015 on a synthetic _buildcubemaps
      folder, three 256 cubes, half EXRs; CubemapOracleTests):
      - The CUBE input is a 6 x 1 strip, faces left to right (a 1 x 6 strip fails; RC also takes 4 x 3
        and 3 x 4 crosses, the layouts the client composites with its flag).
      - GpuCubemapBlur on the RTX 4070 equals RC -vulkan's RGBA32F output on all 1,572,840 floats of
        the 9 mips; RC's default DX11 compile differs from it (and from ours) by 1 ulp on 17,771.
      - The radiance SH: 81 of 81 floats.
      - The reflectivity: per cube the faces' 1 x 1 last-mip values (the full chain's, below the
        texture's 7 mips) added in face order times 1 / 6, the cubes added in order over their count
        (decompile: the per-frame loop over 180c3bb80); all four channels of all four compiles.
      - The BC6H file: CubemapArrayVtex.Build equals RC -vulkan's byte for byte (816,556 bytes: RED2,
        DATA, quality-3 ispc BC6H, LZ4 per mip). RC's DX11 and -vulkan BC6H files are identical too (the
        1-ulp float differences vanish in the encode on this input; not guaranteed for every input).
    - Settled 2026-10-21 against the game's own build (tools/cubehost: the engine in our own process,
      no Steam; GameCubemapTests): the .vtex source (CRLF), the EXR (half, uncompressed, channels B G R,
      Vrad3Exr byte for byte) and the compiled array (945,886 bytes) are the game's byte for byte.
    - Open: the render of each face (client.dll's BuildCubemaps view on the Game pipeline).

118. **The cubemap face render's inputs: its shaders and its views** (2026-10-22, offline: the probe01 cube 0
    RenderDoc capture from cubehost read without replay, client.dll decompile and unicorn; docs/CUBEMAPS.md).
    - Settled by capture: the frame holds 10 shader modules and each one's SPIR-V is byte for byte a file of
      Valve's .vcs (tools/cubehost/rdc_spirv.py + spvmatch): generic static 4 (S_MODE_TOOLS_VIS) with the
      lightmap dynamic combo for colour, csgo_depth_only for depth and the sun's cascades, the two cullbits
      compute shaders (the light binner), downsample_depth (dynamic 17 and 20) and sky.
    - Settled by emulation against the capture: each face's world-to-projection matrix. tools/re/
      emu_cube_views.py runs client.dll's own face setup (18020e5bc .. 18020e728: AngleQuaternion
      1817445e0 on the record's and the table's angles, product 181745900, QuaternionMatrix + MatrixAngles
      1817463a0 -> 1817439d0 through V_atan2f, view constructor 18174e140 from its static template
      (18174dd60: FOV 90, aspect 1, near 10, far 100), 18174fd60, the projection 18174fe70 on the record's
      near / far, 18174f330) with tier0's V_sincosf / V_atan2f / V_tanf through ctypes. The view object's
      matrix at +0x288, transposed, equals the uploaded per-view matrix (block offset 192) bit for bit on
      faces 1 to 5 of cube 0 with near 1 and far 10000; face 0 differs in one word, -0.0 uploaded where the
      view holds +0.0 (row 1, column 2), not explained by the record's signed zeros, extra projection
      rebuilds or the angles themselves. Cause found: that element's four products are all -0.0; the
      upload is a plain four-term sum (-0.0) while client.dll's 4 x 4 multiply 181746490 starts from +0
      (+0.0), so the game multiplies the upload elsewhere (presumably scenesystem). The camera-relative
      matrix of the 640-byte per-view block (projection x world-to-view without translation, transposed)
      is bit for bit 181746490's on faces 1 to 5, the same single signed zero on face 0.
    - Read: the face table (client 181ad10d0) is (pitch, yaw, roll) (0,0,90) +X, (0,180,-90) -X, (0,90,0)
      +Y, (0,-90,180) -Y, (-90,90,0) +Z, (90,-90,0) -Z, FOV 90 each; the capture's faces come in that order.
      The quaternion round trip turns 90 into 89.99999237, which is where the matrices' 1.94707184e-07
      terms come from. The client update of 2026-10-21 (2000934) left every client address the cubemap
      work cites identical (patch_check now tracks client.dll).
    - Read (client 1801ea0a0, the buildcubemaps command): the records, 0xf8 bytes each, gathered class by
      class (env_cubemap, env_cubemap_box, env_combined_light_probe_volume, func_combined_light_probe_volume),
      each class in entity-list order (1809dfb70), skipping a component with +0x20 set: floats 0..2 the
      scene node's origin, 3..5 its angles, 6 = 1.0 and 7 = 10000.0 (constants: the near and far), +0x20
      the cube array index (component +0x48) when EnvironmentMapUseCubeArray else 0, byte +0x24 the
      component's +0x65, then the EXR path "%s/_buildcubemaps/env_cubemap_%d.exr" with the component's
      +0x44 (the handshake), lowercased with backslashes.
    - Open: the rest of the per-view blocks (fog: the env_cubemap_fog origin and keys appear there),
      per-draw constants and bindings (generic's _Globals_ for these materials is csgo's generic, 116 bytes,
      with g_bRenderingToCubemaps and g_nToolsVisMode among its attributes; core's generic is another
      program), the shadow cascades' views.

119. **The .vmap_c argument table by builder** (2026-10-22, decompile of resourcecompiler 0923 and 56 Valve
    compiles: world only, -world -phys -vis -nav, full; MapRootFileTests).
    - Found by a whole-package parity run (ParityReport on the dev box, probe01 -world -phys -vis -nav against
      Valve's 10-16 build): the writer gave every compile without lightmaps the lit table, so every unlit build
      (the service's Fast and Entities presets, any -world build) had a wrong .vmap_c.
    - Settled: the table is the arguments the steps read. Always force_up_to_date_materials (1802f9cc0),
      skipauxfiles (180244d30), skipmapload (1800fd550); debugColorClustering (CVisibilityMeshMerger_
      MergeMeshes) and meshletMaxTriangles / Vertices (18025fab0) only with world node models (the gameplay
      maps, whose nodes hold only n0.vwnod, lack them); keep_vertices and mapbuilder_entity_classname with
      -phys (the physics compile); norte with vis (CWorldRendererBuilder_Build: CMapBuilderContext vfunc 0xe8
      returns the builder table at +0x609, filled by 1801f7840 in builder order, index 2 = vis) or the
      context's +0x605 flag (vfunc 0x100; its writer not found), then debugvisgeo and dumptris in
      CreateRayTracingEnvironment and disablevis (not a resourcecompiler string: the vis builder); with
      -bakelighting the 12 lighting arguments, plus the 8 texture arguments when lightmaps are written, plus
      final + lightProbeVolumeTextureArg (probe volumes), fogIrradianceVolumeTextureArg (fog volume) and the two
      far field texture arguments, with their optional input files; without it bakelighting, and
      flattenPropsWithBakedLighting when EntityLump_StaticModelRecord (180245770) runs: every visible
      prop_static and smart prop (180244d30) and brush entities with lightmapstatic (180240a60), its read
      gated by vfunc 0x108 (the compile does not bake).
    - Verified: 23 lit fixture compiles and all 11 unlit references (nobake_refs, fresh_refs, nolight_1016)
      byte for byte.
    - Settled (2026-10-22, decompile and capture): the row order. The recorder (180174f90) appends each row
      a context has not seen, to it and each parent, so the list is in first-read order; the edit info
      writer (18016c650) std::sorts it (18017a9c0, MSVC's introsort: insertion sort at 32 rows or fewer,
      else the median-guess partition) by V_stricmp_fast on the name alone. The two lightmapMaxResolution
      rows tie, so the 36- and 38-row tables of s2c_l5probe and s2c_l5farfield128 (fog, far field) come out
      texture row first and the 35-row ones value row first. Read order captured from Valve's compiles with
      tools/re/capture_argdeps.py (Frida on the writer's entry: s2c_l5probe and s2c_l5farfield128 full,
      probe01 -world -phys -vis -nav): skipmapload, nobakedlighting or bakelighting, skipauxfiles, the vis
      four, the 13 lighting rows, the texture compiles' rows (minmipsize, maxmipsize, fast, final,
      hueShiftFixup, lightmapMaxResolution 0, then the texture arguments: probe volume, fog, far field
      indirection, far field atlas, lightmap), the meshlet rows, force_up_to_date_materials, the physics
      two. MapRootFile.ReadOrder; all 25 lit and 15 unlit references byte for byte. Still open: the +0x605
      flag; where flattenPropsWithBakedLighting is read (it ties with nothing).
    - Settled (2026-10-22, capture_argdeps.py --over --trace on Valve's Only Entities compiles of probe01 and
      cardtest): an Only Entities build compiles every entity model again and writes default_ents and the
      map root. A physics-only model reads keep_vertices and mapbuilder_entity_classname, which its own
      context is given, so they stay there; a model with a render mesh, or one that fails on a missing
      material (cardtest's impmodel), runs ModelCompile_ReadMeshArgs (1802f9cc0), whose
      force_up_to_date_materials no context is given, so its row goes up to the map
      (MapRootFile.ReadsMeshArguments). The lump's RED2 is the full build's (weak references,
      UsesBuilderEntityKeys); the root's children are what the build compiled: the lumps, the entity models
      (failed ones too), world.vrman and baked_bomb_damage.vdata. compile-map's Only Entities build over the
      package Valve's ran over now equals Valve's whole package on both maps (ParityReport with PARITY_BASE).
    - Settled by capture (2026-10-22, CS2 closed: Valve's compiles of probe01 and cardtest with Hammer's Fast
      Compile, -world -skipauxfiles -nolightmaps -phys -nav, and Only Entities, -entities -skipauxfiles
      -nolightmaps -fshallow over the existing package; %TEMP%/preset_fast, preset_entities): a switch given bare
      is recorded as an IntArg with value 1 (skipauxfiles), a valued one as a FloatArg; Fast reads the same rows
      as -world -phys (no vis rows: Fast runs no vis). Only Entities reads neither bakelighting nor
      flattenPropsWithBakedLighting, and force_up_to_date_materials only on cardtest, whose impmodel fails on a
      material from another addon (probe01's entity models compile and do not record it); that reader is not
      read yet. Without -fshallow an Only Entities compile of an up-to-date map is skipped whole ("1 skipped")
      and leaves the package as it was. UnlitMapRootsEqualValves: all 15 Valve references byte for byte.
      Rechecked on CS2 1.41.9.2 (2026-10-25, the same switches over the addon's lit 10-23 packages): unchanged,
      8 compiled, no world; the 10-23 references that built the world had -world -fshallow added by
      compile_fresh.py.
      (Which entity models make the map read force_up_to_date_materials: settled below.)
    - Also from the run: probe01's world_visibility.vvis_c differs only in 4 bytes at 551 (the fingerprint,
      a CRC over a path blob, and the dev box compiled under /tmp); Valve's own probe01 .nav is 9,556, 9,580
      and 9,608 bytes across its runs, so the nav is compared within one run (ledger 109), never against a
      single Valve file.

120. **The cubemap face render's GPU state, reproduced draw by draw** (2026-10-22, RenderDoc 1.44 replay of
    the probe01 cube 0 capture, CS2 closed; tools/cubehost/rdc_replay.py, CubemapFaceReplayTests).
    - Settled by replay: every action of face 0 (+X) from the light binner to the max downsample, 30 in all
      (the two cullbits dispatches, four sun cascades into the 4352 x 5248 D16 atlas, the depth prepass, the
      checkerboard depth downsample, the generic ToolsVis colour draws, the sky, downsample_depth max), run on
      our own Vulkan device (SceneDrawRunner) with Valve's SPIR-V and the captured constants, bindings,
      samplers, vertex layouts, indirect arguments and render state, from the capture's targets before each
      action, equals the capture's targets (or written buffers) after it bit for bit. So what is left for the
      face render is producing the inputs, not the GPU state.
    - Read from the replay: world draws use viewport depth 0 .. 0.95 and LESS_OR_EQUAL, back-face culling
      with counter-clockwise front faces; the sky draws at depth 1 .. 1 without depth writes and without
      culling. Colour draws test stencil NOT_EQUAL against 3 under mask 1 with keep everywhere. The colour
      pass samples the four 4096 lightmaps (BC4, BC6H, BC1, BC1 sRGB), a 512 x 512 x 4 BGRA8 sRGB volume,
      the sun atlas through a LESS_OR_EQUAL comparison sampler, a 2040-layer 256 BC6H cube array (7 mips),
      and the half-resolution depth the downsample writes into a 1280 x 720 scratch target (its first
      512 x 512); vertex streams are the world mesh (28-byte stride: position, uv, a packed uint, a 16-bit
      lightmap uv), a per-instance uint and a stride-0 uint4.
    - Settled, inputs (CubemapFaceInputsProbe): each world draw's vertex stream is its world node model's
      decoded vertex buffer, byte for byte, and the pooled index buffer holds each model's indices whole
      (reflectivity_30's at byte 0, dev_measuregeneric01's at 624); per view, the surviving fragments of a
      model are merged into indirect sub-draws where they are adjacent (cascade 1: draws 0+1, 5, 9). The
      colour pass's lightmaps are the package's lightmaps/*.vtex_c (irradiance, directional_irradiance,
      direct_light_shadows, debug_chart_color) and each material texture its .vtex_c's whole mip chain.
      generic's _Globals_ (set 1 binding 0) is ProgramGlobals.Fill's for both materials word for word with the
      render attribute g_bRenderingToCubemaps = 1 and g_vShaderIDColor from gameinfo's
      MaterialSystem2/ShaderIDColors (materialsystem2 180035b2a reads the section; 180082cf0 makes each byte
      linear: over 255 (0.003921569), clamped, x / 12.92 at or below 0.04045, else V_powf((x + 0.055) / 1.055,
      2.4); alpha 1; generic.vfx is "255 255 255").
    - Settled by blanking each input in the replay: the face depends on the sun atlas, irradiance,
      directional irradiance, direct_light_shadows (the reflectivity draw only), the material textures and a
      1280 x 720 sRGB colour target whose first 512 x 512 is cleared to white each face (the AO target; pass
      429 clears it, no draws). It does not depend on the 512 x 512 x 4 volume, the 2040-layer cube array,
      the half-resolution depth, debug_chart_color or the 1 x 1 defaults. The face's colour and depth targets
      are loaded, not cleared, and already hold the same face from an earlier frame of the build.
    - Open: the per-view (640 bytes), scene (656) and lightmap (64) blocks, the 32 KB light block, the
      transform and instance buffers and the binner's inputs, from our own data; then the whole face chained
      from cleared targets; the reduction to the EXR (547, 549, 581 are copies).

121. **The cubemap face render's constant blocks from our own data** (2026-10-22: DX11 reflection, decompile of
    client 10-15/10-21 and scenesystem 10-15, the replay's per-word sensitivity sweep; CubemapFaceViews,
    CubemapFaceConstants, CubemapFaceViewsTests, CubemapFaceReplayTests.OurBlocksKeepTheFace).
    - Settled, layouts: the Vulkan SPIR-V is nameless, but the DX11 programs in shaders_pc_dir.vpk keep their
      RDEF (tools/re/dxbc_rdef.py over every program, spvmatch --allbytecode): PerViewConstantBuffer_t (640),
      PerViewConstantBufferCsgo_t (656), BakedLightingConstantBuffer_t (64), PerViewLightingConstantBufferGpu_t
      (32224 in DX11, its fake_out_fxc arrays the Vulkan block's 4-element ones), CullParams_t (160),
      g_transformBuffer (48 each), g_instanceBuffer (32 each), g_BarnLights (480 each).
    - Settled, which words the face reads (CubemapFaceReplayTests.Sensitivity: each non-zero word zeroed and
      its low bit flipped, each zero row set to 1.0, the draws that read the block rerun): per-view, the
      camera-relative g_matWorldToProjection, g_vWorldToCameraOffset, g_vCameraPositionWs; csgo,
      g_bOtherFxEnabled.x, g_bOtherEnabled3 (zero), g_flMinSpecLightmapSize, the cube fog offset, scale,
      exponent, height .x, culling .y and max opacity, g_vHighPrecisionLightingOffsetWs; lightmap block,
      g_vLightmapUvScale; transforms, the two the world draws' instances point at (4 and 5 here).
    - Settled by port, bit for bit: the face views. CubemapFaceViews ports client.dll's chain
      (AngleQuaternion 1817445e0, QuaternionMult 181745900, QuaternionMatrix + MatrixAngles 1817463a0 ->
      1817439d0, AngleMatrix 18173cd10, ConcatTransforms 18173fd50 with the axis swap 182291300 (its +0.0 lane
      adds), MatrixInvert 181741a70, the perspective 18174fb00 / 18174ef50 (tan 45 is V_tanf's exact 1), the
      4 x 4 multiply 181746490). All six faces' g_matWorldToProjection, g_matWorldToView and
      g_matViewToProjection equal the game's upload; 1800 random records equal client.dll's own code under
      unicorn. 181746490 multiplies with DPPS, whose hardware sum is (p0 + p1) + (p2 + p3); unicorn sums from
      +0, which is ledger 118's face-0 signed zero (no other multiply is involved). V_atan2f is the CRT's
      atan2f (tier0 1802e0184), ported as CrtMath.Atan2 with its 241-entry table (Valve's values, not
      correctly rounded): 1,188,513 pairs equal tier0's.
    - Settled by decompile: g_flMinSpecLightmapSize is gameinfo ResourceCompiler/BakedLighting/
      MinSpecLightmapSize as int then float (client 181206bfe; csgo 4096). The cube fog terms (client
      181217e20 over the attributes 180206e50 sets from the fog entity): offset (-start) / (end - start), scale
      1 / (end - start), LOD bias, exponent; culling (start^2, +inf, sign, max opacity); probe01's equal
      height start and end take the default height terms. g_vShaderIDColor as ledger 120.
    - Settled by capture: the sky's pixel _Globals_ is ProgramGlobals.Fill for its material with csgo's sky
      program; its vertex block is AngleMatrix(env_sky angles) transposed (m20 = -sin 0 = -0.0).
    - With our per-view (world and sky), csgo and lightmap blocks in place, face 0's depth prepass, colour
      draws and sky equal the capture.
    - Settled by port and replay (2026-10-22, later; CubemapShadowCascades, CubemapCascadeTests,
      CubemapLightBlockTests): the sun cascades. scenesystem's fit (180494b40) over the split distances
      (180496ef0: the light's given distances, then ((V_powf(M/n, k/N) n - lin) 0.85 + lin) + 0.1), the light
      view 180156120, slices and diameters 1804969e0 (the client's frustum corners, same code), light-space
      bounds stretched toward the sun by L = max(1.42 x the whole range's diameter, max(max visible,
      pushback)) (client r_csgo_csm_max_visible_distance 7500, r_csgo_csm_pushback_distance 7000; the
      viewmodel cascade 0.5 .. 21 with 1000 / 1500), texel-snapped (floorf) x/y, ceilf depth, the
      double-precision ortho 180498af0, all products through the DPPS 4 x 4 (1804983c0 = client 181746490):
      all 20 rendered cascades of cube 0 (faces 0-3 and 5; +Z renders none) equal the game's matrices bit for
      bit. The sun is minus the forward of QuaternionMatrix(AngleQuaternion(light_environment angles)); the
      light block's direction is that times 1/length, the cascade cameras' direction that divided by length.
    - Settled by replay: instance records (tint word, 1.0 word and the transform behind the index are read;
      ours keep the face), the light block (a per-word sweep on the dev box; ours equal every word it reads:
      PCF terms, AO proxies, sun direction and colour (sRGB to linear times brightness), baked shadow mask,
      cascade count, receiver bias, sample threshold, cross-fade and distance-fade lerps (1.42 x diameter),
      g_mWorldToShadowCascade = ((jitter P) V) T(lighting offset) transposed, atlas offsets x/W + half/W),
      the cascade per-view blocks. With every constant block and buffer of face 0 built by us, the face
      equals the run with the captured ones. The light binner's outputs and the stride-0 uint4 stream do not
      affect probe01 (zeroed or all ones): no point lights or env maps there.
    - The dev box's GTX 1080 (Linux, NVIDIA 580) reproduces the 4070 capture bit for bit on most draws (two
      colour draws differ by a few bytes), so sweeps and checks there compare with that GPU's own run
      (CUBEREPLAY_BASELINE, CUBEREPLAY_BASIC_DEVICE).
    - Settled by decompile (2026-10-22; client and scenesystem as installed 10-21; CubemapShadowCascades.Inputs):
      the cascade inputs. The light gives none: csgo.fgd removes every cascade key from light_environment and
      the server's CLightComponent constructor (18017c8d0) zeroes them. 180496ef0 then takes, in order:
      the light's distances (+0x48, each later one doubled from the last given; none here), the convars
      csm_cascadeN_override_dist when above 0 (default -1), the practical split, then the view attributes
      0x61fac369 / 0x6914dd11 / 0xc69b8e6e / ... replace distance N when set (1804976e5 on). Its whole range M is
      csm_max_shadow_dist_override when above 0 and the view's flag bit 4 is clear, else the view attribute
      ShadowFarZ (0x7b2a64e1, default 1000, set by no code), at most the view frustum's far (view + 0x10 +
      0x24, 10000 here). csm_max_shadow_dist_override is "560" in all four shadow-quality presets the video
      settings apply (client 180df0f20 = quality 2, 180df13a0 = 0, 180df1820 = 1, 180df1ca0 = 3; value
      pushed before the name), so M = 560. The capture's run logged "Detected video config level 2": quality
      2 also gives csm_max_num_cascades_override 3 and lb_csm_cascade_size_override 1280 (the 1280 tiles),
      cross fade 0.1, distance fade 0.05. The 7500 / 7000 go in as the attributes CsmMaxVisibleDistance
      (0xb506b94b) and CsmPushbackDistance (0xb7a3d0d5); the viewmodel cascade is csm_viewmodel_max_shadow_dist
      21, csm_viewmodel_nearz 0.5, csm_viewmodel_max_visible_dist 1000, r_csgo_viewmodel_csm_pushback_distance
      1500. Names from MurmurHash2 (lowercase, seed 0x31415926); no DLL holds those strings.
    - Settled by capture (2026-10-22, CS2 closed; run_probe.py --frida tools/cubehost/cascade_sources.js,
      cascade_summary.py): d0 = 75 is the view attribute 0x61fac369, set by the render pipeline (client
      1811d9d30, the call returning to 1811da424) on every game view, the BuildCubemaps view included: the local
      pawn's camera services exist and 180cb2100 finds a scripted camera, so 1808898d0 returns 75.0 (.rdata
      181b14fe0). The cubemap view's block takes no FOV attribute (0x172c3cca is set only when the pawn's FOV
      differs from its default) and then 180496ef0 runs with the face frustum (FOV 90, near 1, far 10000) and
      the light's distances all 0 (count 0). Read in the same run: ShadowFarZ 1000 (unset, the default) at
      1804975e5, csm_max_shadow_dist_override 560, CsmMaxVisibleDistance 7500, CsmPushbackDistance 7000, so the
      split is 35.206184, 113.89898, 560.1 before the attribute and 75, 113.89898, 560.1 after. Which of
      180cb2100's cameras answers is not logged (Panorama's StartCamera, end_of_match_camera or
      team_select_camera); the value is 75 whichever. The pipeline also sets 0x70644345 to 1 (1811da71c); that is
      not a cascade distance (cascade 1 and 2 are 0x6914dd11, 0xc69b8e6e).
    - Lesson (the same run): Frida hooks inside 180496ef0 (1804975e5, 1804976e5, 1804977d0) stopped the engine
      as the cubemap build began: an inline hook overwrites past its instruction and 18049784d jumps to
      1804977d5. The agent hooks function entries and the lookup functions (18004d270, 18036d160) only.
    - Settled by replay on the RTX 4070 (2026-10-22, CS2 closed): ChainedFaceEqualsTheCapture from cleared targets
      passes against the capture itself, every action of face 0 bit for bit.
    - Settled by replay on the dev box (2026-10-22, CubemapFaceReplayTests.ChainedFaceEqualsTheCapture): face 0
      chained, every dispatch and draw in order on live targets with all our constant blocks, so each draw reads
      what our earlier ones made (binner buffers, shadow atlas, prepass depth, its downsample). Against the same
      GPU's chained run with the captured blocks: every action equal. From cleared targets (CUBEREPLAY_START=
      clear: colour 0, depth far) instead of the capture's leftovers: the colour result is unchanged, the
      downsample 64018 differs only where no draw writes (nothing reads it), and against the capture only draws
      478 / 484 differ (1 and 3 bytes), the two the GTX 1080 already differs on unchained; on the RTX 4070 it
      passes against the capture (below).
    - Settled by replay (2026-10-22, CS2 closed; s2c_l5probe cube 0, a RenderDoc capture with an omni2 and a barn
      light): all 38 actions of face 0 (+X) bit for bit on our device (DrawsEqualTheCapture), the light binner
      with real lights included. Its three new dispatches (eid 132, 135, 138) take a 44-byte push constant block
      (RenderDoc: bufferBacked false, from the command buffer); SceneDrawRunner.Bindings.PushConstants carries
      it. The frame has 24 shader modules (probe01: 10) and 16 CAnimatableSceneObjectDesc batches per face
      (mode Default; a 10,818-index model, presumably the spawned player) plus an ambient occlusion proxy;
      the animatable draws change no pixel of face 0, and neither cube's EXR from the game shows a model.
    - Settled by replay (2026-10-22, CS2 closed; atixref cube 0, 42 cubes, world-only build; the .rdc, face 0's
      7 GB dump for events 335-2867, 104 shader modules and all 42 game EXRs in %TEMP%/cubehost_run/atixref):
      335 of face 0's 336 actions equal the capture bit for bit on our device. What it took, each settled by
      RenderDoc on both sides (tools/cubehost/rdc_postvs.py, rdc_debugpixel.py, rdc_pixelhistory.py,
      rdc_bindings.py and rdc_events.py; our own draw captured in-process by RenderDocInApp with
      ENABLE_VULKAN_RENDERDOC_CAPTURE=1, CUBEREPLAY_RENDERDOC=<eid>):
      (a) a single indirect draw (vkCmdDraw*Indirect(1) without sub-commands) has an internal "Indirect
      sub-command" event at eventId - 1 that already holds its output, so its targets' before-state is at
      eventId - 2 (after any render pass clear): rdc_replay.py dumps it so now, rdc_targets_before.py fixes older
      dumps (targets_before.json, 153 draws here);
      (b) one bindless binding (set 4 binding 46) is declared per module under several image types (2D, 3D, 2D
      array, cube): each element's view type comes from the declaration RenderDoc names (res<SPIR-V id> of that
      stage's module); taking the first declaration gave the foliage pixel shader's BC7 texture a 3D view, so
      its alpha read 0 and every fragment was discarded (sun draw 1019; the colour draws from 2193 on);
      (c) runtime descriptor arrays are sized to the highest slot bound (slots reach 9302), and the device enables
      scalarBlockLayout and uniformBufferStandardLayout (the material blocks are scalar: a vec3 at 60).
      Left: draw 2305 (vkCmdDrawIndexedIndirect, 16380 indices, depth EQUAL) differs by one half-float ulp on 38
      scattered pixels of 1.9 MB written; post-VS outputs and every input match, so the suspect is how the
      pipeline is compiled: the game links one-stage pipeline libraries (VK_EXT_graphics_pipeline_library) and
      we build whole pipelines. A first library build crashed the driver on every draw (an access violation in
      our code, backed out); that is the next thing to try. probe_classes crashes the engine while spawning its
      entities, so it has no capture.
    - Open: the cascade camera's y offset (1 ulp, no effect);
      the general height-fog mapping; the lightmap block's writer in worldrenderer; the binner for maps with
      lights; chaining a whole face from cleared targets.

122. **Which lights list precomputed_vis_clusters** (2026-10-22, Valve's compiles of s2c_prefabprobe4: the
    installed lit build and a capture_pvs.py capture of its FlatVisClusterVector; LUMP_FLATBOXES).
    - Settled: WRB_PrecomputeLightVisMembership reads the light's direct light mode from the node after the
      map preprocess, so the shadow slot step's rewrite (1800fe7b0: a stationary light with castshadows 0
      becomes directlight 2, one with no free channel directlight 1) takes it out. Twelve atixref lights
      authored directlight 3 with castshadows 0 (2140:228 to 235, 6789, 7197, 7198, 7203) carry no key in
      Valve's lump; ours listed them until the gate read the written keys (EntityLumpAuthor
      LightVisMembership). ze_hold_em_p's 37 all cast shadows, so it never showed.
    - Verified: with Valve's own cluster boxes, the 6 lights Valve lists are exact in value and place and
      no other light gets the key, so the light half (trace, bounds, cluster test) is exact apart from vis.

123. **The PVS scan's ray length and walk queue** (2026-10-23, visbuilder decompile and disassembly plus
    Valve's s2c_prefabprobe4 scan: a capture_pvs.py --passin capture and capture_segments.py's RayFold
    segments).
    - Settled: SamplerDriver (180019c40) computes the ray length as sqrt(dx² + (dy² + dz²)) over the
      tracer box (addss y² + z² first, then x²). BatchTracer sends each ray to reach · d + o; the packet
      renormalises end − o, so the length's grouping reaches every hit t and segment end. Grouped
      (x² + y²) + z² it rounds one ulp apart on prefabprobe4's box; with Valve's grouping all 19 captured
      segments are bit-identical and the three scan generators match Valve pass by pass (centres 10,
      boundary points 28, large regions 2; 23,534/23,534 rows).
    - Settled: SegmentWalk (18002ef60) and SunWalk (18002f1c0) queue nodes in a ring of 4,096 (index
      & 0xfff) with no overflow check, ending when the read index meets the write index after a node.
    - Verified unchanged by reading beside the port: CentrePoints (18001fda0) and its fill (18001fad0),
      CentreRays, LargeRegionRays (180021190), Ray_Toward, FillRays, BatchRay, the packet triangle test
      (divps) and normalisation (rcpps + one Newton step), OctantMask and CellMask (max of the enters
      with 0 against min of the leaves with 1).

124. **Hammer's mesh physics simplification, everywhere it acts** (2026-10-23: decompile and
    disassembly of physicsbuilder 0924 and resourcecompiler 0923, an in-process oracle, Frida
    captures and Valve compiles of s2c_simplify and s2c_simplify_types, ze_hold_em_p with every mesh's
    physicsSimplificationOverride on at errors 0.25 to 64 and, in the second, its physics type cycled).
    - Settled: the simplifier is one edge-collapse library in double (pb 18060xxxx, copied into rc at
      181bfxxxx), ported as Physics/MeshSimplifier.cs. MeshSimplifierOracleTests runs Valve's own
      functions from the archived physicsbuilder 0924 on 50,000 random meshes (grids, closed boxes,
      fans past 32 edges a vertex, non-manifold soups with NaN quadrics): every output double and
      index equal. Every float expression keeps the disassembly's grouping (two face-quadric terms
      are w * (c * a) and w * (a * -d), not Ghidra's left-to-right), and every comparison its
      comisd/jcc behaviour on NaN (a NaN determinant takes the endpoint branch; sift-up stops unless
      the parent is greater).
    - Settled: where it runs. World pieces: PhysicsBuilder_MeshPieces welds a copy at 1/32 over all
      its streams and calls CMesh_Simplify, per blend layer after the split or per unsplit piece.
      The piece's CMesh carries eight streams (26 floats) when the mesh has vertex paint, else
      positions only; one vertex per .vmap vertex in the triangle mesh's order. Brush entity models:
      MapBuilder_MeshFromMapMesh (1813089b0) runs the collapse on the piece's triangle mesh directly,
      no weld, when it has 4+ vertices and 2+ triangles. The pre-settle world: PhysMesh_Shapes'
      18105bb20 welds and simplifies each soup. Cables: 181156860 (item 126).
    - Settled: the error is the stored one squared (CMapMesh vf216, read at vf 0x6c0); a vmap older
      than 28 stored it squared with no override, and loader step 27 (rc 180d7f9c0, entry 27 of the
      upgrade table at 182fe7b00 whose entry 37 is the step to 38) takes its square root and sets the
      override. CS2's gameinfo DefaultHammerMeshSimplification is 0.0.
    - Verified: SimplifyReplay (9 world pieces, weld and simplify bit for bit), SimplifyInputs (our 9
      CMeshes byte for byte, streams included), MapSimplifyReplay (167 entity pieces and 322 soups),
      MapSimplifyInputs (157 of 157 entity inputs among Valve's) and SettleSimplifyInputs (56 of 56
      soups); world physics and all 32 entity models of both probes whole-file exact, the entity lump
      unchanged.
    - Settled since (item 126): a simplified mesh shape's vertices are listed by first use, and the
      part's flags.

125. **A mesh's own collision settings** (2026-10-23: decompile of physicsbuilder 180016cf0 and
    resourcecompiler's CMapMesh_ConvertMeshForBuilder 1810dff20; Valve compiles of s2c_meshcoll and
    s2c_meshcoll_hold, cardtest and ze_hold_em_p with each mesh given one of 13 settings).
    - Settled: physicsCollisionProperty, when scripts/collision_properties.txt has the name (case
      folded; an unknown name changes nothing), replaces the material's group and its three interact
      lists with the entry's; then physicsGroup replaces the group, and physicsInteractsAs, With and
      Exclude are appended to their lists after a space. World pieces and brush entity models alike,
      except that the map builder's record (ConvertMeshForBuilder: property +0xa0, group, as, with
      +0xb8, then the detail layers) has no exclude list, so an entity model never takes a mesh's
      physicsInteractsExclude (a property's exclude list it does). The pre-settle world reads group,
      as, with and exclude (vf 0x690 to 0x6a8) but not the property.
    - Verified: world physics and every entity model of both probes whole-file exact.
    - Not ported, refused: physicsIncludedDetailLayers and physicsMissingDetailLayers name layers
      from scripts/collision_detail_layers.vdata, which CS2 does not ship, so Hammer offers none.

126. **Cable collision, and the two rules item 124 had measured** (2026-10-23: decompile and
    disassembly of resourcecompiler 0923, physicsbuilder and vphysics2 0924; Frida captures
    capture_cable.py, capture_simplify.py, capture_mapsimplify.py, capture_layers.py and
    capture_settle.py; Valve's compile of s2c_cablecoll, 17 collision cables over every cable setting:
    simplification errors 0 to 2, sides 3 to 40, closed loops, interpolation 0 to 2, flipped faces,
    rotated, cable_static and cable_dynamic).
    - Settled: the tube. Control points (1810b7ca0, 181110ba0) with the path's origin its first node's
      (CMapPath::vf274, 1810b66e0, on every change of that node, loading included: Valve's lump ships
      every probe cable at its first node whatever origin the source stores) and a supports_loop
      closed loop's ends each other's neighbours (1810be5b0, 1810be2f0); Bezier samples (1812802d0,
      18110fc80, 1810bb3f0), per-sample scales (1810b8270), rotation minimising frames (1812820e0:
      the quaternion rotate sums (q x t) + (w t + v), read from the packed SSE; Ghidra prints it left to
      right), rings and triangles (1810b8ae0, 1810bbc40); the collision tube's sides and 1.025f
      radius (vf277, 18114d850) and its 1/32 weld and simplification with the error squared
      (181156860). The factories (1810b3e90, 18114e350) start interpolationType at 1 and a cable at 8
      sides, spacing 16, radius 16, collision on, error 2.
    - Settled: where it goes. The physics document's filter (180f4d220) keeps a non-mesh entity node
      only for a class with has_static_mesh, so cable_static's tube (vf200 export 1, moved by the
      node matrix) is the world's and cable_dynamic's is not; physicsbuilder takes the node's
      simplification error from the node (vf216), which for a cable is 1801ae2c0, returning 0. A
      cable_dynamic's model takes the render export (181151b30: full side count, plain radius) through
      the entity model step (180209e20: corners read back, weld, world and back, half-edge join) and
      the map builder's simplification with the record's error unsquared. The pre-settle world builds
      every collision cable as a mesh body (PhysMesh_Shapes' path branch, 181057020) with its
      material and surface_properties key (PhysMesh_MaterialRecord, 1810564a0).
    - Settled (item 124's first rule): a triangle-mesh shape lists its vertices by first use because
      physicsbuilder makes each piece's CMesh from the exported DmeMesh corner by corner (pb
      180085e80); captured, the CMesh MeshPieces receives is in first-use order for a simplified and
      an unsimplified cable piece alike.
    - Settled (item 124's second rule): ShapeBuilder_BuildPart (rc 180c28230) sets flag 2 when the
      shape builder's type is not 2, when a mesh shape's attribute names no detail layer (the string
      list at +0x50 of the 0x80-byte attribute) or one the intersection dictionary lacks or holds with
      +0x10 clear, and when there are shapes and every one's attribute names a detail layer. The
      dictionary (g_pVPhysics2Interface vf 0xd8, looked up by CIntersectionDictionary vf5, vp
      180296c00) holds 0 entries in resourcecompiler, read live at the first part built, and CS2
      defines no detail layers; so the flags are 2 exactly with a mesh. Not the interact-as list:
      atixref's func_breakable hulls carry interact-as tags and ship 0 (corpus regression).
    - Verified: CableReplay (controls, samples, tubes, simplified meshes on 38 calls; 20 DmeMesh
      exports), world physics of s2c_cablecoll whole-file exact, its 15 cable models' physics and
      5 brush entity models exact, the settle world's 43 bodies (transforms, shapes, attributes,
      materials) exact.
    - Not settled: the settle's body order where a cable's object sits elsewhere in the pointer sort
      (two of s2c_cablecoll's 43), as for other nodes (SETTLE.md); the water_tube branches (no such
      shader ships); cables inside instances or prefabs.

127. **Visibility end to end on atixref-sized maps, split hints and the sampler switches** (2026-10-23,
    Valve's compiles of s2c_prefabprobe4 and s2c_vishints; capture_merge.py --passes; visbuilder decompile).
    - Verified: each of the five merge passes run from Valve's captured input leaves Valve's next input,
      every set in order (PASSREPLAY); the whole chain on s2c_prefabprobe4 gives Valve's VXVS byte for
      byte (4,085,234) from Valve's .rte and from our trace scene, which equals the .rte triangle for
      triangle (33,153, flags and ids).
    - Verified: split hints (CandidateBoxes, SplitOne, GenerateRegionClusters' deal) and four voxel-hint
      types on s2c_vishints: VXVS 262,660 bytes identical from both scenes; Valve's output identical over
      two runs.
    - Settled: SampleVisForClusters (180037ee0) reads `updateloshints` only when DeterministicBuild is 0 or
      `-oldvis` is given; measured, `-updateloshints` alone leaves the package Valve's stock one and writes
      no .los. `-oldvis` takes Sampler_Original, whose output differs run to run in Valve's own tool (three
      runs, three vvis), so it has no byte reference and is not ported.

128. **Cables inside instances and prefabs** (2026-10-23: decompile of resourcecompiler 0923
    CloneObjects 180f616e0, the node clone 180f86a90 / 180f86650 and CMapCable::CopyFrom 18114b270;
    Frida captures capture_cable.py and capture_settle.py with settle_bundle.js; Valve's compiles of
    s2c_cableinst (four cables in a turned instance with a nested instance, the cable probe as a turned
    prefab) and s2c_cableinst2 (eleven cables, one setting changed each, each in its own instance)).
    - Settled: an instance's clone is a new node from the class factory (mapnodefactory, 180f86650)
      given CopyFrom (vf 0x3d8). CMapCable's (18114b270) copies the material, tint, numSides clamped to
      3..1000, tessellationSpacing and radius clamped to 0.01f..1e5f (maxss, minss), the texture
      settings, flipFaces, collisionEnabled and visOccluder, but not physicsSimplificationError
      (+0x18a4), so the copy keeps the factory's 2. Measured on s2c_cableinst2: error 0 and 0.5 copies
      simplify as 2; sides, radius, spacing, flip, loop, interpolation and material carry over.
    - Settled: a prefab's nodes are not cloned. CMapPrefab's collapse (vf 0x860, 180f96d40) adds a
      group (FUN_180f5f230, appended through AddChild 180f75010 and SetParent 180f752a0, which moves
      nothing) and reparents the prefab map's nodes to it; 181019d00 then sets the group's origin
      and angles through the group setter 180fc1bf0, which moves every node by 180f834d0 from the
      origin to the prefab's placement, as an instance collapse moves its copy (the same step
      matrix, AngleMatrixDouble against the old placement's inverse). So a prefab's cables keep
      their error (captured too: every vf277 call of the prefab copy).
    - Settled: each path node is moved on its own: the base TransformBy (180f7c570) moves the node's
      origin by the step (180fdef60) and recurses into its children with the same matrix; a path
      node's own TransformBy (18110eae0) also turns its two stored tangents by the step's rotation
      ((x m0 + y m1) + z m2 per row). The path's origin follows its first node (vf274).
    - Settled: the bake collapses instances and prefabs in tree order, each new node appended to its
      parent's children (FUN_180f5f230 -> vf 0x1c8), so a prefab's map comes after its siblings in
      the world soup as an instance's copy does: s2c_cableinst's instance copy precedes its prefab.
    - Verified: world physics of s2c_cableinst and s2c_cableinst2 whole-file exact; s2c_cableinst's
      43 entity models (33 cable models' physics) exact, the nested copy named after the id the
      prefab's block shifts (2647); its settle world's 91 bodies exact (transforms, shapes, mass).
    - Fixed 2026-10-24: the entity lump placed a path's copy by its node alone, keeping the template's
      path nodes, so s2c_cableinst's three turned copies differed in origin and pathNodes; it now
      places each copy as the physics side does (SettleWorld.PlacedPath: every path node moved, the
      origin its moved first node, under a deformer the deformer's move). s2c_cableinst,
      s2c_cableinst2, s2c_deformplace and atixref lumps: 0 differences.
    - Ported (2026-10-24, from the lump's rules, no probe yet): brush entity and cable models of what
      instances inside a prefab's map place, named by the copy's plain id (item 46) with the template's
      name fixup, placed by the prefabs then the instances (SettleWorld.NestedPlacement).
    - Not settled: no probe turns an explicit tangent yet (the rotation above is ported from 18110eae0
      alone).

129. **Deformers placed through instances and prefabs, and cables under deformers** (2026-10-23:
    decompile of resourcecompiler 0923 and physicsbuilder 0924; Frida captures capture_deformer.py,
    capture_propmatrix.py (physicsbuilder's PropMatrix_Angles inputs), capture_pbmeshnode.py
    (physicsbuilder's InitMeshShapeFromVertices), capture_originwrites.py (who stores a node origin)
    and capture_settle.py; Valve's compile of s2c_deformplace: s2c_cablecoll cables under deformerprobe2's
    two bend deformers and its lattice, a bend deformer group through a turned instance, the whole map
    as a turned prefab, one prop_physics).
    - Settled: CMapDeformer's TransformBy (vf 0xc8, 181076410) turns the step matrix into a CTransform
      (translation, MatrixQuaternion 18125de90) and moves itself and every descendant (181076680,
      181076850, iterator 180f8b7c0; mode vf 0x7e8 is 2 while the deformer's +0x4b1 is set, which the
      constructor 181175fc0 sets and no .vmap key clears) by 181076a70: Compose(step, node)
      (181253510), the origin stored (180fded80, the store capture_originwrites finds for the prefab
      props), the angles QuaternionAngles (181260200) through the setter 180fdeda0. Ordinary nodes
      take the step's TransformBy (180f7c570 / 180fdef60). (SettleWorld.UnderDeformer, Step.)
    - Settled: a lattice keeps its control points as CMapLatticeControlPointLocator children, made at
      load by vf 0x248 (1811758d0, from the document's post-load pass 180f69c50 -> 180f677a0): one
      per control point, through the node factory, so each takes a node id before the bake
      (SmartProps.NodesCreatedOnLoad; s2c_deformplace's 5x1x1 lattice shifts the copies by 24). They
      move with the lattice (mode 1), and the change hook TransformBy's caller fires (vf 0x80 with 3,
      18116faa0) reads them back (181175e60): each point is the inverse of the moved node's matrix
      (vf 0xa8, MatrixInvert 18125aba0) times its locator (Matrix3x4_TransformPoint 18125d1f0).
    - Settled: PropDeformer_Init (18128d980; physicsbuilder's copy 1800bf910) builds the lattice
      matrix as ConcatTransforms (181258890) of the inverse transform's matrix and the caller's, and
      back by MatrixInvertGeneral (18125ade0; 1800b83a0, the same code): the port's 4x4 inverse was
      ulps off on turned deformers.
    - Settled: a mesh is deformed by HammerMesh_ApplyDeformer with its own node matrix (vf 0xa0,
      181255fb0) and the deformer struct its lookup returns (181022480, the node's +0x4c0), on the
      moved copy; the cable's world export (vf200, 18114c1b0) does not deform, the pre-settle world
      does not (corrected 2026-10-24, item 130): PhysMesh_Shapes (181057020) only deforms with the
      deformer PhysMesh_Build (18105d760) hands it, 181022480 of the node, which a cable class never
      answers (the node must be of kind 3).
    - Verified: s2c_deformplace's world physics and its 14 entity models whole-file exact against a
      fresh Valve compile; 1,606 resourcecompiler and 171 physicsbuilder deform calls bit for bit;
      every placed prop's transform and placed deformer struct bit for bit.
    - Settled 2026-10-24 (item 130): the settle world of a prop under a deformer, and a lattice cloned
      by an instance (its CopyFrom 18116f970 makes no locators: the clone copies them).

130. **The settle world under deformers, lattice locators in copies, and the lattice's settings**
    (2026-10-24: decompile of resourcecompiler 0923; verified against the 10-23 capture_settle.py
    capture of s2c_deformplace: 147 of 147 bodies, 140 of 140 shapes, every body exact at step 0 and
    the settled prop exact after 2,700 steps; the earlier port stopped on the capture's deformed
    shapes).
    - Settled: a prop's settle record (CMapEntity vf 0x6f8, 181003930) holds the node matrix's
      position, its largest column scale and MatrixQuaternion (18125de90) of the rotation, before the
      compose the body transform gets, and the deformer 181022540 copies, composed after the node's
      step as a CTransform (181288690; the identity step after the bake). 181022540 answers only for
      a node of kind 3 (vf 0x4a0, 181002590: a class with the static_prop or deformable flag,
      180dd54d0), climbing groups and such props to the nearest CMapDeformer.
    - Settled: with a deformer PhysPart_BuildFromModel (18105dbd0) takes the matrix path and turns
      every shape into a deformed mesh: spheres by 1819d5810 (5 meridians, 12 latitudes, poles on x),
      capsules by 1819d5950 (12 meridians, 2 latitudes a hemisphere, turned onto the axis by the frame
      of 18125d0d0), hulls by 1819589f0 (a fan per face), meshes as they are; points through the
      placement matrix, PropDeformer_Transform with the record's matrix (1801feb70), RnMeshCreate
      (vphysics2 vf 0x238), each triangle's first and third corner swapped for a mirrored lattice,
      added at scale 1 (1810557d0, 181054970, 181054e80, 1810554d0). SettleWorld.Deformed.cs.
    - Settled: PhysMesh_Build hands PhysMesh_Shapes 181022480 of the node, so a cable (kind 0) is
      never deformed in the settle world (the eight cables of s2c_deformplace are straight in Valve's
      capture), and PhysObj_Build (18105c5b5) sends CMapPath as well as CMapMesh there, so a cable's
      body has its damping and drag zeroed too (all eight differed at step 0 before).
    - Settled: an instance's clone (180f86a90: factory, then CopyFrom, then the children) copies a
      lattice's control point locators like any child (their vf 0x188 is 1800de990, false), each with
      an id after the lattice's own subtree; the lattice's CopyFrom (18116f970) calls 1811758d0, which
      does nothing because the clone has no parent yet (vf 0x4f8, 180f78950, finds the document
      through the parent). In a prefab's map the locators are the lattice's last children too, so they
      take the slots after its subtree (Valve's ids for s2c_deformplace's prefab nodes after its
      lattice are 24 higher), not the end of the block (MapInstances.ClonedLocators, PrefabSlots).
      No .vmap stores a locator, Valve's own zoo maps included.
    - Settled: the lattice's settings. The divisions setter (1812907e0) takes at least one segment and
      1..3 divisions in y and z, regridding on a change; the interpolation name is matched by
      V_stricmp against LINEAR, B_SPLINE, CATMUL_ROM, BEZIER (183011390, 0..3), an unknown name 0;
      the bend axis Y or Z (1830114d0), an unknown name 1, and the simple deformer's constructor
      (181175fc0) starts it at 1. The cubic cell blend for four cells (18127fdc0) weighs
      g^3, (g g 3) t, (g 3)(t t), (t t) t and sums ((w1 p1 + g^3 p0) + w2 p2) + t^3 p3 (disassembly).
      A node's scales reach neither the deformer's transform (the change hook 181020fa0) nor its size
      (181022b60 -> SetSize 1812908b0), and the node matrix is unscaled (vf 0xa0, 181255fb0); only the
      simple deformer's bend takes them, re-bent on each transform change (1811764f0).
    - Not settled: BEZIER lattices, whose handles 18128cab0 resets on the grid whenever the mode or
      the divisions change, so they depend on the order the load sets size, divisions and mode (the
      attributes go through the DMX element machinery; a capture of PropDeformer_Transform's struct
      settles it); a stored control point array of another length (181290340 copies the overlap over
      whatever the lattice held). CMapDeformerPath: settled by item 131.

131. **CMapDeformerPath** (2026-10-24: decompile of resourcecompiler 0923; oracle on the archived
    0924 build; capture_deformer.py and a fresh Valve compile of s2c_deformpath, built by
    tools/physics/path_deformer_map.py: four path deformers over cable paths of two to five nodes,
    angles (0 0 0), (0 30 0), (10 -20 5) and (0 90 0), start, end and maxError stored or not).
    - Settled by oracle (PathDeformerOracleTests, 5,000 random paths per piece, bit for bit): the
      lattice fit 18128dc90 and its pieces, 181280a60 (chord parameters), 181280e20 (least squares:
      each accumulator acc + ((z + y) + x), symsse), 181281360, 181285e60 (the Newton step's
      denominator (curvature . diff) + (d . d), symsse), 1812858b0 (its loop runs while the worst
      distance is not under (length 0.001)^2, disassembly), 181285470 (the curve, its shares and
      total) and 181281aa0.
    - Settled by capture: the update is a pure function of the final state; the node's origin is
      the path's point at (end + start) 0.5 (18117aeb0 -> 1810c0530, 181282e60, 181077c20), its
      children moved along by 181076850 (pivot the old origin, the delta the move: pivot o move o
      pivot^-1 o child); the lattice transform is the origin less the turned half size
      (181020fa0, pivot mode 1 from vf10 181178520 -> 181022b50); the node's angles are the angle
      setter's (180fdeda0: a yaw of -20 is held as 340, which turns the quaternion's sign).
      Every deformer struct of the probe and 472 PropDeformer_Transform calls bit for bit; world
      physics and the 5 entity models whole-file exact.
    - Settled: a mesh whose parent is a deformer node is world geometry (Valve's world soup has the
      four meshes under the path deformers; the port's filter took world, group and layer only).
    - Settled from the setters (18117a8b0, 18117a820): start = minss(maxss(v, 0), end - 0.01f),
      end = minss(maxss(v, start + 0.01f), 1); CopyFrom (181178b00) copies all three unclamped.
    - Settled 2026-10-24 by item 132: the load order of start and end, closed and hidden target
      paths, and path deformers an instance or a prefab copies.

132. **CMapDeformerPath: load order, closed and hidden targets, copies** (2026-10-24: decompile of
    resourcecompiler 0923; capture_deformer.py and a fresh Valve compile of s2c_deformpath2, built by
    tools/physics/path_deformer_map2.py from s2c_deformpath: start/end pairs under 0.01 apart stored
    start first (0.5, 0.505) and end first (0.3, 0.7), an end of 0.004 alone, a closed-loop
    cable_static target, a force_hidden target, a target with one force_hidden path node, a path and
    its deformer in a group placed by a turned instance, a deformer in an instance whose path stays in
    the world, and s2c_deformpath as a turned prefab; 1,924 PropDeformer_Transform calls).
    - Settled from the decompile: the load sets embedded properties in the order the class registers
      them, whatever order the file stores them in. 180f77eba gets the list from vf 0xe8 (181178650:
      CMapNode's 180f88d80, CMapDeformer's 181022620, then 181179840: targetPath, start, end,
      maxError) and 180f84b70 (mode 1) finds each attribute by its serialization name, checks its
      type, sets it through the setter (180f94f70) and removes it. So start clamps against the
      factory's end of 1, then end against that start (PathDeformer.SettingsOf). Captured: all three
      pairs exact.
    - Settled from the decompile: a closed loop goes through 1810b7ca0's supports_loop test like the
      entity lump's (EntityLumpAuthor.Wraps), so the deformer needs the FGD (MapDeformers.Apply and
      MapSource.Read take or find it). Captured exact.
    - Settled from the decompile: 18117aeb0 skips the recentre when 1810be450 reports a visibility
      state (+0x3c8 of the path or of a path node), but in resourcecompiler only the node
      constructor (180f8f920) writes +0x3c8 (a sweep of every store to [reg+0x3c8]), and force_hidden
      goes to +0x366 (180f94900). A hidden target is recentred as any other; both hidden cases are
      exact in the capture.
    - Settled by capture: a copy is moved as any deformer (SettleWorld.UnderDeformer) and refitted
      along its target where the collapse left it. When the collapse moved the target too (one in the
      instance's target, or the prefab), the deformer is then recentred on the moved path, its
      subtree moved along (181077c20, 181076850). A target outside the instance stays where it is and
      leaves the moved origin. A prefab's copy is recentred twice more, back to the source centre
      (the deformer keeping its turned angles) and to the moved one again; the deformer ends where it
      was, but its subtree takes two more moves, which shifts the prefab copy's meshes in the last
      bits (PathDeformer.FollowRecentre). The double recentre is read from the capture's call order
      (each of the four prefab copies); what re-resolves the reference twice is not decompiled.
    - Settled by capture, for every deformer: the move reads the node's quaternion from its angles as
      the setter 180fdeda0 keeps them (a yaw of -20 held as 340, the quaternion's sign turned). The
      port took the raw angles, which put a pitched or rolled copy's angles a few ulps off (the prefab
      copy of the (10 -20 5) deformer: (9.999998, 69.99999, 4.9999995) against Valve's (9.999997,
      69.999985, 5.0000005)). Earlier probes turned only yaw-only deformers, which the two agree on.
    - Verified: every captured struct (NodesAgainstCapture, PlacedDeformersAgainstCapture) and all
      1,924 calls' 306,992 points (PointsAgainstCapture) bit for bit; world physics (WPBUILD: 0
      differences), the 10 entity models and the entity lump of s2c_deformpath2 whole-file exact;
      s2c_deformpath unchanged.
    - Settled 2026-10-25 by item 133: nested prefabs, two prefabs of one map, path deformers inside
      path deformers, a deformer holding its own target; the prefab copy's "double recentre" was the
      host's own copy of the path (s2c_deformpath2 is built on s2c_deformpath), not a rule.

133. **Path deformer copies step by step; the angle setter in TransformBy; the mesh self-touch GJK**
    (2026-10-25: decompile of resourcecompiler 0923 and vphysics2 0924; tools/physics/capture_deformerevents.py,
    which logs every recentre, update, reference change, deformer TransformBy, subtree move, node origin
    store, prefab place and collapse in call order, and capture_deformer.py, on four new probes:
    s2c_deformnest (tools/physics/path_deformer_map3.py: an outer path deformer holding a mesh and an inner
    one whose path is in the world or in the outer one, a deformer holding its own target, both in turned
    instances), s2c_deformpath3 (two prefabs of s2c_deformpath, one turned (10 45 0), a prefab of
    s2c_deformpath2, which holds a prefab of s2c_deformpath, and one of s2c_deformnest, on a host built from
    s2c_deformpath), s2c_deformnest2 and s2c_deformpath4 (path_deformer_map4.py: deformers holding their own
    2, 3 and 4 node targets in the world and in turned instances; the same prefabs plus s2c_deformnest2 on a
    host without the prefab maps' nodes)).
    - Settled from the decompile and the event capture, the load: each reference resolves in tree order
      (180fca9b0 through 181075180 to vf 0x740) and recentres its node, then the lattice is fitted to the path
      as it stands, which the move has carried along when the path is below the deformer. The load sends no
      change back to it (one recentre).
    - Settled, a change of the path (180f8f5a0) reaches its listeners only while it is not already
      notifying (+0x2c8): a recentre that a reference change started and that moves the deformer's own target
      is told of the move again for each of k + 2 changes (k nodes), k + 3 recentres in all (5, 6, 7 captured
      for 2, 3, 4 nodes); one the path's own notification started is not.
    - Settled, an instance copy (PathDeformer.Copies, Stage.Instance): the id remap points the copy's
      reference at its copied target, recentred where the source stands (the cascade above for a target below
      it; a target outside the copy is recentred on where it stands, which moves a copy in a prefab onto the
      prefab-placed path before the step: s2c_deformpath3's 7260 lattice). Then the group setter (180f834d0)
      moves the copy by the step and each path it moved recentres its deformers in tree order, outer before
      inner, the inner carried by the outer's move.
    - Settled, a prefab (Stage.Prefab): its map loads on its own, its world is placed (181019d00; the group
      setter skips instance targets, 180f8ba70) and every path deformer of it recentres once, target groups'
      too. Its nodes move into the host world (180f96d40) and each reference resolves again by its old id in
      the host's referenceID table, which keeps the first holder: the host's own nodes, then each prefab's in
      collapse order (a prefab's node takes an id nobody holds). Recentred there; the remap then gives a
      reference the host had taken its own copy back, recentred again. s2c_deformpath3's prefabs find the
      host's copy of s2c_deformpath's paths, s2c_deformpath4's second prefab of it the first one's.
    - Settled by capture and then the decompile: CMapDeformer's move (181076a70) composes pivot o step o
      pivot^-1 o node with the identity as pivot; each composition renormalises (a 60 degree prefab turns a 30
      degree deformer to 90.0000076, not 89.99999). TransformBy 0x41 (SettleWorld.Moved) reads the node's
      angles as the setter 180fdeda0 keeps them (an instance's (10 -20 5) as (10 340 5) gives Valve's
      (10 39.999985 4.999999) in a prefab turned (0 60 0)).
    - Settled from the decompile: RnMesh_TrianglePairTouches measures with vphysics2's second GJK
      (180129800 to 18037a1c0, ported as Physics/MeshGjk.cs, every sum in the DLL's order), not an exact
      distance; with deformed tubes ~1e4 from the origin the two disagree (s2c_deformnest2's mesh 2 is closed
      in Valve's).
    - Verified: every deformer struct of all six path deformer probes (NodesAgainstCapture,
      PlacedDeformersAgainstCapture) and the world physics of s2c_deformnest, s2c_deformnest2, s2c_deformpath3
      and s2c_deformpath4 whole-file exact; s2c_deformpath, s2c_deformpath2, s2c_deformplace unchanged.
    - Not probed: an instance inside an instance whose target group lies inside the outer target (the
      port steps such a copy once from the source, as before).

134. **Lattice interpolation, instance targets, the lump's tangents and prefab copies** (2026-10-25:
    decompile of resourcecompiler 0923; capture_collapse.py --clones --parents and capture_deformerevents.py
    on s2c_instnest (tools/physics/instance_nest_map.py), s2c_tangentinst and s2c_prefabinst
    (cable_tangent_map.py)).
    - Settled from the decompile: a lattice's interpolationMode goes through the table at 183011390 and is
      clamped to 1 (181175260), so BEZIER deforms as smooth; LatticeDeformer no longer refuses it.
    - Settled by capture: at load, an instance or prefab whose target is not a child of its world is moved
      under that world, last in its children (MapPrefabs.ReparentTargets); the inner copy of a nested
      instance is then made from the original, not the outer copy.
    - Settled: explicit path tangents of type 2 and 3 are turned by the instance step (18110eae0) and
      written un-turned in the lump's pathNodes; a path in a prefab's copy keeps its own stored origin,
      angles and nodes before the prefab chain places it (EntityLumpSet, MapEntities).
    - Verified: s2c_instnest, s2c_tangentinst and s2c_prefabinst entity lumps 0 differences and world
      physics whole-file exact; the 23-map corpus regression (tools/physics/corpus.py) all exact. Closes
      ledger 128's tangent and prefab-instance items.

135. **Ragdolls in the settle: truth captured, the conical joint ported** (2026-10-25: decompile and
    disassembly of resourcecompiler 0923 and vphysics2 0924; notes in tools/re/out/settle_notes/notes_joints.md).
    - Truth: Valve's compile of s2c_ragdollprobe (ledger 56's probe) settles the two CT agents through
      their joints and writes boneTransforms; the chickens settle as single bodies. Capture:
      capture_settle.py with settle_bundle.js and the new settle_joints.js (aggregates as built, the 56
      joints every recorded step, the island manager at steps 0..3 and every 100th).
    - Settled from the decompile: a model with 2+ parts or any joint goes to world vfn 0x418
      (CPhysAggregateInstance, 180042ba0): keyframed bodies at xf o bind pose, shapes, mass, inertia scale,
      damping and drag from the parts, then the joints by m_nType (4 and 10 -> CRnConicalJoint type 10, 3 ->
      revolute type 3). The VPhysXJoint_t offsets come from vphysics2's schema table
      (tools/re/schema_fields.py). Limits make a degrees round trip (x * 57.295776f * 0.017453292f).
      Friction mode 1 with torque r |g| m friction from the bodies' mass properties at creation.
    - Settled: made dynamic body by body (1800488d0 -> SetType 2), each SetType takes the body out of the
      island manager (its joints deactivated, leaving holes) and back (joints reactivated): the captured
      islands' 13 holes and joint order 12, 11, 2, 3, 0, ... follow.
    - Ported and exact against vphysics2 (JointSolverOracleTests, 20,000 random cases each): the world
      anchor, the limit row, the angular and point blocks, CRnConicalJoint's prepare (friction and no motor),
      velocity iteration, impulse store and position iteration (its linear block generated from a lanesym
      trace, the new --callout option).
    - Not ported yet (the order of work is PHYSICS.md's ragdoll roadmap): the conical spring and velocity
      motor (modes 0 and 2, 180205bd0), the revolute joint, the aggregate build and SetType sequence, joints
      in islands, the solver loop and sleep, and the bone write-back to boneTransforms.

136. **The revolute joint's solve, ported** (2026-10-25: decompile and disassembly of vphysics2 0924).
    - Settled from the decompile: a VPhysXJoint_t of type 3 becomes CRnRevoluteJoint (0x130 bytes, vtable
      0x1803e07e8) when its flags' bit 1 is clear and CRnRevoluteBlockJoint (record 6) when set
      (180047600 writes desc +0x90 = (m_nFlags >> 1) & 1; 1801f0750 picks the class). ctm_sas's 14 joints:
      12 type 4, the two knees type 3, all flags 0 (ModelPhysicsProbe).
    - Ported and exact against vphysics2 (RevoluteJointOracleTests, 20,000 random cases each, every motor
      mode): the prepare 180216240 (record 5: motor row 180213d10, limit rows 180213aa0, hinge rows 180213870,
      the point block; the spring's plasticity 180205e70), the velocity iteration 18021bcd0, the store
      18021ac10, the scaling 18021ac80, the position iteration 180219190.
    - Read from the disassembly, not the decompile: the motor clamp is comiss then minss and the limit
      rows' clamp maxss then minss, so a NaN passes the motor and becomes the limit's minimum (the conical
      joint's limit rows use the same pair; JointRows.SolveLimit now does, identical for any other value);
      the hinge axis rotates as (q x t) + (v + w t), not as Ghidra prints it; the position pass sums
      a . (IA + IB) a as the conical joint's does, whatever order Ghidra prints.
    - Not ported: CRnRevoluteBlockJoint, and the ctor defaults and desc setters as the aggregate build
      calls them (roadmap step 5).

137. **The cubemap face readback to the EXR, against the game** (2026-10-25: three cubehost captures, each
    with the game EXRs of the same run; tools/cubehost/rdc_readback.py, GameCubemapReadbackTests).
    - Settled: the six images each cube reads back (vkCmdCopyImageToBuffer from the 1024 x 1024
      R16G16B16A16_FLOAT colour attachment, one per face, faces in order) through CubemapFaceExr.Build
      (half to float, QuarterSize twice, the vertical flip, the 6 x 1 strip, Vrad3Exr) are the game's
      EXR byte for byte on probe01 cube 0 and s2c_l5probe cube 0 (2,363,705 bytes each). So the whole
      chain after the colour pass (readback, reduce, strip, EXR, the array's compile: ledger 117) is
      exact against the game; what is left is the colour pass itself.
    - Open: atixref cube 0 differs on 1,527 to 1,532 of 393,216 halves per channel, in three compact
      regions (face 1 x 0..42 y 84..241, face 2 x 16..101 y 213..255, face 5 x 196..251 y 126..198 of
      256). RenderDoc's pixel history puts the last write of each region's texels on the same mesh
      (a 12,840-index indirect draw: eids 4001, 5284 and 11114), whose material reads two 512 BC7/BC1
      textures with full chains, 4 x 4 placeholders, the main view's 1280 x 720 colour and depth, and the
      package's previous BC6H cube array. The replay and the live run disagree on that one material, so
      something it reads changed between the capture's snapshot and the GPU's execution (texture
      streaming is the suspect). To settle: two atixref buildcubemaps runs without RenderDoc (CS2 and
      Steam closed); if their EXRs differ, Valve's own output varies run to run there.

138. **The conical joint's spring motor and plasticity, ported** (2026-10-25: disassembly and a lanesym trace of
    vphysics2 0924's 180229870 and 180205bd0).
    - Settled from the code: modes 0 and 2 with a frequency hold the joint at its target (+0xe0) by one spring
      per world axis. The error is Mul(target, conj(rel)) as copysign(2, w) times its xyz, negated and rotated
      by qa; each axis's mass is that diagonal entry of (IB + IA)^-1 by cofactors (zeros when |det| < 1.2e-35);
      k = omega m omega, softness 1 / (2 m zeta omega + k dt) / dt, bias k softness dt error; mode 2 subtracts
      the target velocity (+0xfc) and bounds the block by max torque dt, mode 0 by FLT_MAX.
    - The plasticity (+0x10c rate, +0x110 threshold; the struct had them as TargetSpeed/TargetStep): Mul(rel,
      conj(target)) as copysign(2, w) xyz, per axis max(|v| - threshold, 0) at most rate dt of it with v's sign
      (NaN takes -1), then exp: below length 0.01858136 xyz (0.5 - len^2 / 48) and w = cosf(len / 2), else
      sinf(len / 2) (xyz / len) and cosf (V_sincosf = sinf + cosf), identity when xyz / len is zero; times the
      target, renormalised.
    - Read from the disassembly: the cofactor inverses of the angular and point blocks (180206120, 1802064a0)
      test tiny > |det| (comiss then jbe), so a NaN determinant still inverts; JointRows.Inverse now does.
      Ghidra prints the plasticity's exp as sin x len^-1 x xyz; the code scales xyz by 1 / len first.
    - Exact against vphysics2 (JointSolverOracleTests, every mode, random targets and plasticity). Velocity
      trials whose prepared record holds a NaN (a spring between two bodies with no inertia: infinite
      softness) are skipped like the position test's NaN trials; under 5% of them.


139. **The per-view culling of world aggregates, face views** (2026-10-25: decompile and disassembly of scenesystem
    10-15, client 10-15 and worldrenderer 10-23 (the 10-15 build was not archived); three cubehost captures,
    tools/cubehost/rdc_indirect.py, AggregateCullingCaptureTests; Cubemaps/AggregateCulling.cs).
    - Settled against the captures: every face-view indirect draw list (colour and depth prepass) of all six faces
      of probe01, s2c_l5probe and atixref cube 0 (25, 25 and 335 lists): same commands, merges, splits and
      instancing, first instances at one offset per view and aggregate.
    - Read: the frustum is the view's world-to-projection through client 1817465f0 (six planes, (x x + y y) + z z
      normalisation), moved to the camera (d + ((nz (-oz) + ny (-oy)) + nx (-ox))) and packed by 1817435e0;
      18003d380 culls on (px nx + py ny) + pz nz < d with the box relative to the camera.
    - Read: fragment bounds are the draw call's bounds through 18049bfb0 (worldrenderer 1800ba270 is the same
      code); transform slots follow AddFragment 1800380a0 (transform within 1e-5, packed tint, base vertex);
      above 128 fragments tinybvh (c_trav 16, c_int 1, resourcecompiler's build) reorders them into leaf order,
      which is then the visit order.
    - Read: DrawArray (180042330) merges a list's following entries while the transform slot is the first's and
      the first index continues; aggregates with a transform per fragment draw consecutive positions of one draw
      call instanced. The depth prepass layers split by the size metric 18004c390 (rsqrtss, CPU-vendor
      dependent in its low bits) at r_csgo_depth_prepass_cull_threshold / 100 = 0.6 (180041a00, client
      181201670). LOD setups pick the highest LOD whose switch distance is below 50 / (size x view height).
    - Read: the +0x34 / +0x38 draw slots 18003c5a0 assigns belong to the GPU-driven path; the CPU commands come
      from DrawArray. Instance blocks come from an atomic counter per (view, aggregate), so their order is
      thread timing (it differs between views of one frame); the image does not depend on it.
    - Open: the sun cascades' culling (180494b40: shadow view 1804a7a60, the previous cascade's box shrunk by 15%
      as six inverted planes for the culling-box test), LOD cross-fades and forced LODs, the per-view PVS test
      (FUN_180036840), the small-object fade, and the non-aggregate scene objects' culling.


140. **The sun cascades' culling** (2026-10-25: disassembly of scenesystem 10-15 and client 10-15; the three
    cubehost captures, AggregateCullingCaptureTests; Cubemaps/AggregateCulling.cs, CubemapShadowCascades.cs).
    - Settled against the captures: every sun cascade draw list of all six faces of probe01, s2c_l5probe and
      atixref cube 0 (39, 39 and 225 lists); CubemapCascadeTests (probe01's cascade per-view blocks, all faces)
      passes now that the camera is the inverted view's; the chained face 0 replay still equals the capture.
    - Read: per cascade the fit (180494b40) builds a shadow view (1804a7a60): camera = translation column of the
      view's inverse (180498c70 = client 181747070, Gauss-Jordan on [M | I] with row pivoting above 1e-6), planes
      from projection x (view x translate(camera)) through 180498520 with the zero-to-one near plane.
    - Read: each view cascade after the first gets the previous cascade's ortho pulled 15% in on each x and y side
      ((a - b) 0.15 + b in float) times its view, six planes with d negated, as the view's first culling-box set
      (+0xdc0, count +0xf34): culled inside all (min of DPPS(c, P) - (|nz| ez + (|nx| ex + |ny| ey)) not negative).
    - Read: every cascade of a fit gets the fit's caster volume (180495df0) with d negated as the second set (+0xe70,
      count +0xf30, at most 12): culled outside any. The volume from the main view's inverse world-to-projection
      corners, their centroid (must lie inside), the corners pushed by the normalised sun times the fit's reach,
      the planes facing away from the sun, a cap (sun, 1.125 x the running minimum of the 16 dots, in double) and
      one plane per silhouette edge (edge tables 1805ab720 / 1805ab738), oriented by the centroid.
    - Read: the light direction is normalised in place (180053800) before the fit reads it; SunDirections' ToSun
      from the lump's light_environment angles (stored exactly, e.g. 74.99999 180.00002 0) is that result already.
    - Open: shadowed lights' own shadow views (atixref, other atlas tiles), not compared.

141. **The lightmap atlas side and the uv snap's side** (2026-10-25: decompile and disassembly of resourcecompiler
    20260923: 18023b500, 180240b7f, 18025dd8d, 180217740; LightmapSettings, LightmapNodeBake).
    - Settled from the code: the builder's requested size (+0x2f8) is 0x7fffffff (18023b500) unless worldspawn's
      max_lightmap_resolution is above 0 (read as an int, 1802413cd-1802413ed); BakeLighting (through
      CWorldRendererBuilderNode_BakeLightMaps 18025dd8d) bakes at lightmapMaxResolution lowered to it. The port had
      a fixed 4096, so a -lightmapMaxResolution 8192 node bake ran at 4096.
    - Settled from the code: the chart uv snap after PackCharts (180217740 lines 418-444: floorf(u / (1 / R) +
      0.5) (1 / R), R = floor(32767 / side) side 2) takes the side left after the mip and MinSpecLightmapSize
      halvings (uVar13 / uVar15), as the world's m_vLightmapUvScale and the query data do. The port's world node
      snap used the unhalved side; LightmapSettings.HalvedWidth now feeds it and lightmap_seams.dat.
    - Checked: review finding F12 (the query data's scale) was already fixed on 10-18 (c984a05). Only builds that
      halve (8192 atlases, lightmapMipCount 2) change; probe01's default build is unchanged (ChartMeshesTests,
      LightmapSideAndHalvings). Not compared end to end at 8192 against a Valve node (no capture kept).
142. **Nav markups** (2026-10-25: decompile and disassembly of resourcecompiler 20260923 (180f209f0 and its helpers)
    and navsystem 20261015 (180049210, 180074590, 180075f40, the polygon BVH), unicorn on navsystem's Morton code;
    Frida captures of s2c_navmarkup (tools/coverage/nav_markup_specimen.py) and probe_classes with the markup array,
    tag table and stage arguments (capture_navgen.py); Maps/NavGen/NavGenMarkups.cs).
    - Settled against the capture: the collector (180f209f0) takes each func_nav_markup (or a class deriving from
      it, 180f2de60) in the map's node list, not hidden; its keys as CEntityKeyValues iterates them (the key table
      backwards: NavAttributes before NavGen); a navProperty_ string key gives its value, a tag_list its tokens; keys
      the FGD does not declare give nothing. Every (tag, value) is registered; the first adds the markup, with the
      matrix and local bounds of the entity's first CMapMesh (180f8b7c0), not the entity's own angles (probe_classes'
      10 20 30 entity: identity). Markup records 0x50 bytes, +0x4c byte 1.
    - Settled against the capture: the extraction leaves out a body whose AABB (shape bounds +- 0.09375) lies in a
      NONAV box shrunk by max(0.75, 0.75 + hull radius), and drops triangles whose three corners lie in it in the
      markup's frame (180049210); stage 003 shrinks the NONAV / NONAVNONSOLID boxes by 0.75 + radius, 009 grows
      NONAV by it and NONAVNONSOLID by 1 in z only, both with 1 off the matrix's z translation, tolerance 1.
    - Settled against the capture: the polygon BVH returns the polygons whose int16 boxes meet the query's
      (inclusive) in the order of the 30-bit Morton key of their box centres then their index; its tree only
      prunes. 180044fb0's keys equal ours on 1,962 boxes under unicorn (emu_navmorton.py).
    - Settled against the capture: 019 grows each markup's box by 2 (AVOID by 2 + radius), skips WALKABLESEED,
      NONAV, NONAVNONSOLID, and cuts every polygon vphysics2's point-set GJK puts within 0.125 of the box grown 1
      more; the GJK's transformed points and directions sum ((x + y) + z) + t (1802ebd20, 1802ec220).
    - Verified: 003, 009 and 019 from Valve's input are Valve's (NavGenMarkupTests); s2c_navmarkup from the .vmap is
      Valve's through 029, its .nav up to the custom data, the custom data and the bomb damage (NavCompileTests);
      probe_classes is Valve's through 029. IslandRemoval's WALKABLESEED floods (strcmp) are ported but no captured
      map floods from one that the seed does not reach already.
    - Open: markups in instances or prefabs (several instance paths) are refused. (resourcecompiler's node angle
      setter, which builds a -30 yaw as 330, is ported since: ledger 145.)
143. **Spheres and capsules in the nav world** (2026-10-25: disassembly of vphysics2 20261015 (18016c380, 18024bc00,
    18024e6b0, 18006d070; unchanged in 10-23); Frida captures of s2c_navround (tools/coverage/nav_round_specimen.py,
    prop_static spheres and capsules) with every shape cast (capture_navcast.py --with-navgen, now with the sphere and
    capsule data and an exit flush); NavGenShapes.Sphere, HullCast.CastBoxRound, NavBoxWorld).
    - Settled from the code: a sphere's triangles (18016c9b0 -> 18016c380) are 5 segments by 12 rings, poles
      (-r, 0, 0) and (r, 0, 0) last, segment angle i -2pi / 5, ring angle j pi / 13 - pi / 2 (V_sinf / V_cosf), vertex
      (r sin phi, (cos phi cos theta) r, (cos phi sin theta) r) plus the centre.
    - Settled from the code: the sphere and capsule shapes' box casts trim the ray (1802e6e60) and run the hull
      cast's GJK (1802e2ce0 / 1802e7fc0) with the box against the centre or segment placed in the world, the radius
      as margin; the segment's support takes the second centre on a tie (18006d070).
    - Verified: all 525 sphere and capsule casts of s2c_navround replay bit for bit (NavCastTests), and s2c_navround
      from the .vmap is Valve's through 029 (extraction, 022, AddJumps' stand-in, 029).
    - Open: (Valve's saved .nav lacking the connection between areas 59 and 60 is the analysis' overlap removal,
      ported since: ledger 146.) The CS analysis' sphere casts against round shapes
      (NavSphereWorld), LadderConnect's rays against them and bomb damage over such a world are not ported.

144. **The game's cubemap build run to run, and the shadowed light's views' cull** (2026-10-25: repeated cubehost
    builds without RenderDoc, CS2 and Steam closed (run_probe.py --out, --settle, --before); the atixref capture's
    per-view blocks, AggregateCullingCaptureTests).
    - Settled by experiment: probe01's EXRs are reproducible after a 20 s settle (two settled runs, a cold run and
      the 10-21 capture's run bit-identical); a warm run started at once differs on 6,366 and 1,558 pixels, only on
      the edges of high-contrast texture features (up to 315 half ulps), a finer mip resident. s2c_l5probe the same.
    - Settled by experiment: atixref's 42 EXRs differ between any two runs, settled or not and with the server
      paused (185 to 243,548 pixels per cube): foliage and shadows from it, objects at other places. Its single
      game build is no byte reference; the render depends on the time and entity state at the build.
    - Settled against the capture: the shadowed light_omni2's six atlas tiles ((0, 1536), (384, 1536), (1024,
      1536), (1344, 1536), (1664, 1536), (0, 1280), faces 0 and 3) cull with the plain frustum of their per-view
      blocks (zero-to-one near plane) through 18003d380: all 96 lists equal, no culling box sets.
    - Open: the light's view setup (six projections from its precomputed sub-frusta, the tile sizes and places)
      and its constant blocks; what the build's state depends on (clocks, entity simulation, animated materials,
      texture residency), pinning it so a Valve build of atixref repeats, and porting it: animated scenes are in
      scope (1:1 with Valve in every aspect, user 2026-10-25; CUBEMAPS.md roadmap 0a-0e).

145. **Node angles as the load holds them** (2026-10-25: disassembly of resourcecompiler 20260923's CMapPoint and
    CMapNode angle setters 180fdeda0 / 180f770a0; a Frida capture of s2c_navangles (tools/coverage/nav_angle_specimen.py,
    16 markups at odd angles) and its compiled lump; Maps/MapNodeAngles.cs).
    - Settled from the code: every node's angles are stored through CMapNode's setter (vfn 0x18) into CMapPoint's: a
      component whose magnitude is under 0.001 becomes +0, a negative yaw gains 360 until it is not negative; pitch
      and roll keep their sign, and a yaw past 360 stays.
    - Verified: the 16 markup matrices navsystem received (yaw -30, -390, 390, 720.25, -0.5, -180, -360, 360, -1e-06;
      pitch -45 and -0.0001; roll -100; mixed sets) equal ours bit for bit, zero signs included, and the map's entity
      lump (which ships 330 for -30, 0 for -0.0001) has no difference. s2c_navangles is Valve's through 029.
    - Ported in DmxBinary.ReadFile for every .vmap (SettleWorld.SetAngles, which the deformer and prefab moves already
      used for their stores). A Hammer-saved map already holds such angles, so the pinned maps do not change.

146. **The analysis' overlapping connections** (2026-10-25: decompile and disassembly of server.dll 20261015's
    CSNavMesh::Analyze chain: 1814cfaa0, 1814cf8d0, 1814c67b0, 1814be420, 1814b86f0, 1814ada50, 1814c76a0,
    1814eabb0, 1814655e0, 1814669d0, 1814a9280, the comparator 1814cb4f0; .data bytes at 181e81d00..181e81d18;
    Maps/NavGen/NavMeshOverlaps.cs).
    - Settled from the code: before the AI data, the shared step turns ids into pointers (area vfn 0x50, a -1 edge
      becoming the opposite one), puts each one-way connection on its target's incoming list (+0x90, unsaved;
      sorted by the target's heap address and edge), checks the mesh (1814be420, messages only) and removes
      overlapping connections (1814b86f0, behind the bool at 181e81d01).
    - Settled from the code: an edge's intervals are its connections whose area's edge holds this one back, whose
      edges' closest points (segment pair in double) are within 181e81d18 and whose 2D overlap (1814eabb0) is at
      least 1.1920929e-07, the overlap's ends projected on the edge from its end corner; stable sort by start.
      Where the next interval starts more than 1.1920929e-07 before the current one ends, the connection whose
      area's edge is farther from the point at (next end + the larger start) / 2 goes both ways (the next on a
      tie), and the interval indices past it shift down (the back indices do not).
    - Settled from the .data: 181e81d00 and 181e81d01 are 1 and 181e81d18 is 18.0. No code writes them (Ghidra
      xrefs: reads only), so the values are taken as constant.
    - Verified: s2c_navround's .nav is Valve's up to the custom data (areas 59 and 60 lose their link, LIGHTING_ROADMAP
      L24) from both capture runs' 029, and the other six writer maps are unchanged.
    - Open: the oriented path (attribute 0x8000, 1814eada0) and direction 8 are refused; more than 32 intervals on
      one edge (the introsort) is refused.

147. **The cubemap view's visible light probe volumes** (2026-10-26: measured in two cubehost captures of probe01,
    cube 0 on 10-21 and cube 1 on 10-26, all twelve faces; CubemapFaceConstants.LightBlock).
    - Measured: per face, the light block's g_VisibleLPV (from +18832, 96 bytes each: world-to-volume rows, the box
      padded by 0.1, 1 / edge fade, then an int) and g_nNumLPVs (+31960) hold the volumes whose box meets the face
      view's frustum (AggregateCulling.FaceFrustum and Outside give every face's list): cube 0 lists volume 0 on four
      faces and volume 1, volume 0 on +Y; cube 1 lists volume 1, volume 0 on five faces and volume 1 alone on +Y.
      The order is volume 1 before volume 0 wherever both are listed. The entry's last int is 0 for volume 0 and 1
      for volume 1, the entities' array_index. +344 is 1 on every face.
    - Not read: the gather itself (scenesystem's per-view lighting setup, the writer 1801af7b0): whether it is this
      frustum test or another (the box padded or not, an LPV cull), what orders the list (reverse entity order,
      size, array_index descending) and what the last int is.
    - Settle by decompiling 1801af7b0's LPV gather in the installed scenesystem.dll, then check on a map with more
      volumes and cubes outside every volume.

148. **The cubemap face's light binner, sky and depth downsample blocks** (2026-10-26: decompile and disassembly of
    scenesystem 20261015, the same addresses and code in 20261023: 1801b2850, 1801a8150, 1801a9b70, 18000f900,
    18000f9b0, 180155117, 18049b6d0; client 20261015 1811d62a0, 1811e58b0, 180124b60; CubemapFaceConstants
    LightBinnerCullParams and SkyWsToOs; the replay test checks each block word for word on all twelve faces).
    - Settled from the code: CullParams_t (ComputeCullBitsGPU): the tiles from lb_tile_pixels (default 8, at least 4,
      a power of two) over the view's size, lb_bin_slices (default 8192, clamped, a multiple of 32) bins over the
      far plane, epsilon 0.001, near plane and tile epsilon 0, and per item type (lights, environment maps, probe
      volumes) (count + 31) / 32 mask words and items padded to 32, in the tile and bin batch records.
    - Settled from the code: g_matSkyWsToOs is the sky object's rotation transposed with no translation and the row
      (0, 0, 0, 1); SkyTint is the object's +0xe8. The sky's pixel $Globals and the downsamples' are the programs'
      own write sequences (ProgramGlobals.Fill): the sky material with SkyTint, and g_nDownsampleRatio, which the
      HalfResDepth layer sets to r_csgo_mixed_resolution_particles_scale (default 2) and QuarterResDepth to 4.
    - Measured, not read: the sky object's rotation is AngleMatrix of env_sky's angles (only angles 0 seen; the
      client's sky object setup is not read); SkyTint from env_sky's tint_color (only white seen); the cubemap view
      sets no tile-size attribute (0x5af50a7c) and bins no lights or environment maps on probe01.
    - Open: a map with an env_sky at other angles and tint, binned lights (s2c_l5probe's omni and barn) and env maps.

149. **A second map's whole cube: the scripted camera, pass clears, the viewmodel, and the visible volumes' order**
    (2026-10-26: s2c_l5probe's 10-22 capture, faces 1 to 5 dumped this day; decompile of client 20261015 180cb2100 and
    scenesystem 20261015 1801af7b0, 18019f8b0, 1801a9b70, 1801cc400, 1801abf30, 1801aac90, with the installed 20261023
    scenesystem's stores to +0xe520 / +0xe528 scanned at the same addresses; core lights FGD;
    tools/cubehost/rdc_passes.py; CubemapFaceReplayTests.ChainedCubeMakesTheGamesExr).
    - Settled by replay: s2c_l5probe's cube 0 chained from cleared targets with every block ours gives all six
      readbacks and the game's EXR byte for byte, as probe01's two cubes still do with the changes below.
    - Settled by decompile and capture: cascade 0's 75 (ledger 121) needs a scripted camera, and 180cb2100 answers
      from the client's UI state, not the map: Panorama's StartCamera camera while 1825c9e3b is set, else
      end_of_match_camera while 1825c8832 is set, else team_select_camera while 1825c9e65 is set, else none. Both
      maps hold the same team_select and end_of_match prefabs; probe01's builds had a camera, s2c_l5probe's 10-22
      build had none (its player had spawned: the frame draws the first-person viewmodel), and its four cascade
      blocks equal the plain split's (35.206184, 113.89898, 560.1) word for word in every word a shader reads
      (CubemapShadowCascades.GameDistances scriptedCamera = false). The state is an input of the build; which one
      our compile renders at is roadmap 0e.
    - Settled by the capture's own calls (rdc_passes.py: each vkCmdBeginRendering's attachments, load ops, clear
      values and the views' images and formats): a pass's CLEAR attachments are cleared over its render area when it
      begins. The face's colour and depth are cleared (colour 0, depth 1, stencil 0) in its first colour pass, the
      AO target to white over its first 512 x 512 in its own pass, and the stencil alone again later in the face.
      The AO target is an sRGB image the pass renders through an R8G8B8A8_UNORM view (format 37), and the colour
      pass samples it through that view: s2c_l5probe's pawn draws its AO box into it with a multiplicative blend
      (colour zero x src + src colour x dst; alpha zero x src + src alpha x dst), which only reproduces through
      the UNORM view.
    - Measured: the spawned pawn's viewmodel (four skinned draws, each in the prepass and the colour pass) has its
      own per-view block (depth range 0 .. 0.1 where the world's is 0 .. 0.95), scene blocks and a light block with
      the viewmodel cascade alone (count 1 at +31248); its prepass shares the world's light block. Not ported: the
      replay keeps the blocks only its draws read from the capture.
    - Settled by decompile: the visible light probe volumes (g_VisibleLPV). Each volume's scene object adds itself
      for a view that keeps it (virtual 1801c1f70 -> 18019f8b0): it needs the view's +0xf3e8 +0x65 flag and a slot
      in the view's map (1801c7690 on +0x48, keyed by the object's +0xf4), is added once per view (+0xe508 list),
      must pass the view's layer masks (+0xe1f0 +0x490 / +0x498 against the object's +0x78) and 1801abf30, then
      appends a 0x120-byte record: the object, its priority (+0xf8), its local box's volume
      ((dy dx) dz over +0xd0 .. +0xe4) and the screen bounds. 1801abf30 / 1801aac90 reject a box only when its far
      point along the view direction is behind the camera, or when it straddles the camera plane and its
      silhouette (DAT_1805fe400) clips away against the near and +-1 planes; a box wholly in front always passes.
      The binner setup (1801a9b70) caps the list at 128 and sorts it (std::sort, 1801cc400): priority descending,
      box volume ascending, then the object's address. 1801af7b0 then writes each entry: the world-to-volume
      rows (18049b3b0 of the object's +0x30), the box padded by 0.1, 1 / each edge fade (+0xe8, +0xec, +0xf0;
      FLT_MAX for 0) and the int from the view's map. The priority is indoor_outdoor_level: core's FGD labels it
      "Priority" ("the highest priority volume takes precedence"); the write of +0xf8 is not traced.
      CubemapFaceConstants.VisibleVolumes ports the order; probe01's "volume 1 before volume 0" is the smaller box.
    - Not read: the scene's generic object cull that decides which volumes reach 1801c1f70 (VisibleVolumes uses the
      world box against the face frustum, which every face of both maps confirms; roadmap 2), the map's int.
    - Open: s2c_l5probe's cube 1 for its whole array (captured 2026-10-30: cubehost_run/s2c_l5probe_cube1b); the viewmodel's own
      draws (models, skinning, per-view and light blocks); a map whose volumes have other priorities or equal boxes.

150. **The cubemap face's fixed-function state from the programs** (2026-10-26: Valve's .vcs render state through
    ValveProgram.RenderState; scenesystem 20261023's convar registrations 180006c00 .. 180006eb0;
    CubemapFacePipelines; checked inside CubemapFaceReplayTests.ChainedCubeMakesTheGamesExr, which now draws with
    our state, on probe01's two cubes and s2c_l5probe's cube 0).
    - Settled by the shaders' files: a pixel program's .vcs carries the rasterizer, depth-stencil and blend
      descriptors of its dynamic combo (VfxRenderStateInfoPixelShader), and those are the captured pipeline's for
      generic's colour combo (back faces culled, depth less-or-equal and written), the sky (no culling, depth tested
      and not written), and both depth downsamples (depth always, written, no colour writes).
    - Settled by the capture, the engine's part on top: every pipeline has depth bias on and counter-clockwise
      front faces; the depth-only passes (csgo_depth_only, no pixel program) cull back faces and test
      less-or-equal with writes; the sun cascades' slope bias is sc_shadow_slopescale_depth_bias's default 2.13
      (180006d50 stores it as the registration's default), constant bias and clamp 0 (sc_shadow_depth_bias_clamp
      defaults to 0, 180006e00); the colour pass's stencil is front not-equal (stencil & 1, reference 3), back
      always, write mask 254, keep everywhere; the quarter downsample's stencil always replaces with 0 under
      compare mask 0; viewport depth 0 .. 0.95 for the prepass and colour, 1 .. 1 for the sky, 0 .. 1 otherwise;
      the downsamples draw 4-vertex strips. Which engine code sets the stencil states, the depth ranges and the
      depth-only passes' state is not read.
    - Not decoded: sc_shadow_depth_bias's registration stores 0x100 as its default (180006ca0), while the captured
      constant bias is 0.
    - Every world, sky and downsample draw of the three cubes takes our state, field for field the capture's, and
      the cubes' EXRs stay the game's. The viewmodel's draws and the pawn's AO box keep the capture's.
    - Settled by the shaders' files and replay: generic's colour combo binds its textures at set 1, binding 30 + the
      variable's slot in the write sequence (Sequence.RenderState). The lightmap attributes are the package's
      lightmaps of the same names, the artist textures the material's (every subresource of every face equal to the
      capture's), SSAOOcclusionFactor and ParticleShadowBuffer a 1 x 1 white, CubemapFogTexture a 1 x 1 cube of
      zeros. LightCookieTexture and EnvironmentMap are not read in the cubemap view (no light or environment map
      binned): zeros in their place leave every face equal, s2c_l5probe's stale contents included.

151. **The light binner's items for light probe volumes** (2026-10-26: decompile of scenesystem 20261015 1801b2850,
    1801abf30, 1801aac90, 1801abcf0, 1801ac1b0, 180499860, 1804983c0, 1801c6050, 1801c65c0, 1801c6b60, 1801c7100,
    1801ad810, disassembly where the decompile's operand order matters; the silhouette table at 1805fe400 (20261015)
    and 1805fe430 (20261023), the same bytes; Cubemaps.LightBinnerItems; LightBinnerItemsTests and the whole-cube test).
    - Settled by decompile and capture: the cull dispatches' item inputs (set 0 binding 30 of each) are 80-byte
      records (first edge, inside flag, edge count, 0, the footprint's bounds widened by a tile, the depth range
      along the view direction, the rest 0; padded to a multiple of 32 items with bounds -100) and 16-byte edge
      lines. A volume's footprint: its box (six inward planes, eight corners by the sign table) in its frame; the
      eye-outside plane mask (more than 1e-4 behind); the depth range of the corners along the normalised view
      direction (rejected when all are behind); inside the box: the flag and no edges; else the mask's silhouette,
      projected when the box is wholly in front, clipped in clip space (w 1e-4, then +-1 in y and x) when it is
      not; the polygon walked from its lowest vertex and summed with the half-tile rectangle; edges at least two
      pixel diagonals long that keep turning one way, d offset by a quarter diagonal (rsqrtss with a Newton step,
      (3 - r len2 r) (r 0.5), as 1801ad916 computes it).
    - Every word we write equals the capture on all eighteen faces of the three cubes (records and edges); the
      buffers past it hold earlier uploads, and zeros there leave every face equal.
    - Not read: the view's +0xf353 (walk direction) and +0x28 == -1 (orthographic) flags are taken as false for the
      cubemap views (both ported; false matches every face); 1801b52b0's depth offset as 0 (the attribute
      0x6e59bbf1, never set here); the clip path (boxes across the camera plane) is ported but no captured face
      takes it; lights and environment maps as items (none binned in these views).

152. **The cubemap face's vertex and index buffers from our data** (2026-10-26: scenesystem 20261023 .rdata
    1805ec900 and 1805ec960; Cubemaps.CubemapFaceMeshes; the whole-cube test).
    - Settled by the DLL's data: the sky draws scenesystem's unit cube, eight corners at +-0.5 (1805ec900) and twelve
      triangles from the table that follows them, one-based ints (1805ec960), as 16-bit indices.
    - Settled by capture: a world draw's first stream is its world node model's vertex buffer, the pooled index
      buffer holds each model's indices whole at the offset the draws bind it at (the pool allocation is the
      capture's, which no output shows), the second stream is the instance ids (instance i reads i), the third a
      16-byte zero; a downsample's quad is (-1, -1), (1, -1), (-1, 1), (1, 1) in the first two floats of 32-byte
      vertices (the other six, the far plane's corners in the capture, are not read: zeros keep every face).
    - Every range the world, sky and downsample draws read is ours on the three cubes, and the EXRs stay the game's.

153. **The cubemap face's samplers from the programs** (2026-10-26: the .vcs write sequences' sampler states;
    CubemapFaceSamplers; the whole-cube test draws with them).
    - Settled by the shaders' files and capture: a sampler slot's state variables (Filter, AddressU/V/W,
      ComparisonFunc, BorderColor, MaxAniso) give the bound sampler at set 1, binding 14 + the slot. Filter is
      D3D11's code (bit 0 linear mip, bit 2 linear magnification, bit 4 linear minification, 0x55 anisotropic, 0x80
      comparison; generic's slots carry 21, 21, 0, 255, 20 and 149); 255, and a slot with no Filter (the sky's bare
      DefaultSamplerState), is anisotropic. Address modes are Source 2's (0 wrap, 2 clamp, 3 border), wrap where a
      slot has none; MaxAniso -1 takes the default; every sampler's LOD range is 0 .. 15, no bias, border
      transparent black.
    - Measured, not read: the anisotropy is 4 (the video config's level, as the shadow quality sets the cascades)
      and the 0 .. 15 LOD range comes from the engine.
    - Every sampler of the world colour and sky draws is ours, field for field the capture's, on the three cubes.

154. **The cubemap face's vertex layouts from the programs** (2026-10-26: the Vulkan shader files' attribute maps,
    VfxShaderFileVulkan.AttribMap; CubemapFaceVertexLayout; the whole-cube test draws with them).
    - Settled by the shaders' files and capture: each vertex input's location has a D3D semantic in the shader
      file's attribute map; the input reads the mesh's field of that semantic from binding 0 (generic's colour
      combo: POSITION0, TEXCOORD0, NORMAL0, TEXCOORD3 of the world model; csgo_depth_only: POSITION0; the sky its
      cube's R32G32B32 position; a downsample its quad's R32G32 position); TEXCOORD13 is the per-instance id stream
      (binding 1, 4 bytes); an input the mesh lacks (BLENDINDICES0 on these models) reads the 16-byte zero stream
      (binding 2, stride 0, per instance) in its own type. Scene draws bind the three streams, a downsample its quad
      and the zero stream.
    - Every world, sky and downsample draw's layout is ours, field for field the capture's, on the three cubes.

155. **force_hidden in the subasset table; the probe references' builders and staleness** (2026-10-26: decompile
    of resourcecompiler 0923; s2c_rc_probe's references against MapRootFileTests and MapSubassetsTests).
    - Settled from the decompile: the subasset gather (180fc9930) walks the world's nodes and skips one whose
      visibility state (+0x370) is 0. The manager computes it (180f72fb0 -> 180f6ffc0) with the parent visible
      first, recursively, then vf 0x358, which on CMapGameDataNode is CMapNode_PassesFilter (180f77240; vtable slot
      read from the RTTI locator, tools/re/vtslot.py). The filter fails any node whose force_hidden (+0x366,
      180f94900) is set, whatever the flags. So a force_hidden node and everything below it add no subasset:
      s2c_deformpath2's force_hidden cable (one cable_static, three path_node_cable) and its one force_hidden
      path node are not in Valve's table, and neither are their copies in s2c_deformpath3 and 4, which take
      s2c_deformpath2 as a prefab (16/15, 41/40, 42/41 cable_static; 53/49, 136/132, 138/134 path nodes before
      the fix). MapEntities marks the node and its subtree hidden, as it does an editorOnly node. The path's
      geometry keeps its hidden nodes (ledger 132's deformer capture and world physics, unchanged).
    - Not measured: a force_hidden group's point entities in the lump. Valve's lump of these maps has no
      cable_static or path nodes, so only the subasset table saw this case.
    - Found: MapRootEqualsValves read every probe as a full build, but the capture tools leave partial builds
      (-world -phys -fshallow, -world -vis -fshallow, -world -phys -nav) with the package moved aside first.
      The test now takes the builders from the outputs the package holds (world_physics.vmdl phys,
      world_visibility.vvis vis, lightmaps a full build; a map with no world node models, such as the _gameplay
      prefabs, is a full build that bakes nothing), then reads the argument table through MapRootFile as ledger
      119 settled it.
    - Found: five references predated their .vmap. s2c_tangentinst and s2c_prefabinst were compiled from
      cable_tangent_map.py's first run (07:48; its tangentinst CRC 3225174595 is the reference's input CRC), and
      the 07:50 rerun (adding the cable_dynamic) was captured but never recompiled as a reference.
      s2c_prefabprobe7, 8 and 9 were compiled at 12:29 to 12:35 on 10-23, and their prefab maps (s2c_fixinner,
      s2c_fixmid and the probes) were regenerated at 13:32. All five were recompiled from the current .vmaps
      (tools/pipeline/compile_fresh.py, the original switches).

156. **The cubemap face's draw list and the order within a layer** (2026-10-27: decompile of scenesystem 20261015
    180041a00, 180041f30, 1800369b0, 1800371e0, 1800ed4c0, 18010f4e0 and 1801112a0, the same code in 20261023 but
    rip displacements; three cubehost runs with tools/cubehost/layer_sort.js; the chained replays of three cubes).
    - Settled: one CMeshDrawPrimitive_t (0x70 bytes) per aggregate and layer, the views and passes as
      Cubemaps/CubemapFaceDrawList.cs has them. Measured on all six faces of probe01 and s2c_l5probe: every
      captured aggregate call is one of our lists and none of ours is missing (DrawListEqualsTheCapture), in our
      pass order and with our viewports. The cascade tiles (0,0), (1280,0), (2560,0), (0,2048) are measured; the
      atlas allocator is not read.
    - Settled from the decompile: 1800ed4c0 sorts a layer's items with the full sort (layer +0x48 bit 0), none
      (bit 1) or the batch sort. Both compare the object's +0x18, +0x62 bits 3 and 10, the draw's +0x14 bit 6,
      the per-object material (+0x28), +0x62 & 0x1344, +0x48, the material (+0x20), +0x62 & 0xecbb, the draw's
      +100 and +0x58; the full sort compares +0x58 first.
    - Measured: in probe01's layers the two aggregates tie up to the material's address, and Valve draws the
      lower address first. Across two atixref runs (atixref_sort1 and 2) every sort holds the same draws, but
      their order differs in most of them (the colour-type layer 0x400104: 16 of 304 the same), and the 266
      materials' address order inverts 33% of pairs. So the order within a layer is Valve's heap order and
      differs run to run, as the settle's does (SETTLE.md).
    - It changes bytes: in aggregate order four faces of each of probe01's two cubes and s2c_l5probe's cube 0
      differ by 34 to 324 halves (coplanar surfaces of two materials both pass the colour depth test, the later
      draw wins); in the game's order (reflectivity_30 first, the same in four runs from 10-21 to 10-27) all
      eighteen faces and the three EXRs are the game's byte for byte with our lists, arguments and viewports.
      Depth and shadow results do not depend on the order.
    - Measured (2026-10-27 later, layer_sort.js, `--out probe01_order1..5` and `l5probe_order1..3`): on the two
      small maps the order holds. In all eight runs, in every batch-sorted sort where the two aggregates carry
      different materials (35 to 43 per run), reflectivity_30's material has the lower address and is drawn
      first. With the four earlier captures that is twelve runs. Why its address is lower is not read: the
      world's RERL lists dev_measuregeneric01 first, so it is not plain load order with rising addresses.
      atixref's 294 materials are where the order breaks (above).
    - Settled from the decompile (materialsystem2, installed 1.41.9.2, the 10-15 build's code): every CMaterial2
      comes from one of four sites (180033de0, 1800356f0, 18003abf0, 18003ade0), each a 0x7e0-byte g_pMemAlloc
      block with a creation serial (+0x3c, an atomic global); 18000cb40 and 18000dc00 name it.
    - Measured (layer_sort.js "material" records, `--out probe01_alloc1/2`, `atixref_alloc1`): all creations run
      on one thread. Where the sort decides on the material's address (+0x28, +0x48 and +0x62 equal), the earlier
      created material sorts first in 95% (atixref, 7,597 engine/map pairs) to 96% (237,208 map/map pairs) and in
      every probe01 pair; the rest is heap reuse. Materials the engine creates before any of a map's own (957,
      Resources/cubemap_engine_materials.txt from tools/cubehost/material_order.py: in every run, creation index
      below 1106, atixref's first own world material) sort before every other material in all 4,905 tied pairs
      of atixref. The map's own materials are created in bursts as their loads complete, interleaved with the
      game's precache (player models, weapons): their creation order matches neither the world node's aggregate
      order (65% of pairs inverted) nor the models' references (58%), and the creation sequence differs between
      runs. It is not data.
    - Ported: CubemapFaceDrawList.MaterialOrder: engine materials first in creation order, then the map's own
      in path order (ours). All three cubes are the game's EXRs with it (CUBEREPLAY_OURDRAWS=materials).
    - Whether the order changes bytes, measured: replaying atixref face 0 with the captured blocks and every run
      of indirect calls reversed (CubemapFaceReplayTests.DrawOrderChangesTheFace) changes no depth or shadow
      texel and 346 of 4,194,304 colour words: coplanar surfaces of two materials, the later draw winning. Two
      Valve builds of ze_hold_em_p (`--out holdem_order1/2`) order the same draws differently (colour layer 0x0:
      0 of 12 sorts alike) and give all 8 EXRs byte for byte equal. So the order reaches the output only where
      two materials are coplanar in a face, as on probe01 and s2c_l5probe; there Valve's own result follows its
      heap and our rule gives the observed one.
    - Open: the draw flags that choose a draw's layers (atixref's non-shadow-casting blends, its alpha-tested
      prepass layer).

157. **Which layers a world aggregate's fragments join** (2026-10-27, open: inputs; decompile of scenesystem 20261015
    180041f30, 180041a00, 1800380a0, worldrenderer 20261023 18001e920 (disassembly 18001ed6d .. 18001f375),
    meshsystem 20261001 180027140, 180027160, 180027830, 180049520 and its .rdata table 180175160 (same bytes in the
    installed build), materialsystem2 CMaterial2 vf 0x48 18000ba80; layer_members.js runs of probe01 and atixref).
    - Settled: the two tests, transcribed (tools/cubehost/layer_members_check.py, CubemapLayerFlags.DrawCallPasses
      and FragmentPasses), reproduce every logged decision from the logged inputs: probe01 206 draw-call gates and
      1,057 fragments, atixref 159,863 gates and 1,385,550 fragments; the rest are the size split AggregateCulling
      already ports.
    - Settled from the decompile: a fragment's flags come from its mesh's m_objectFlags (worldrenderer, field +0x10
      of the 0x2c AggregateMeshInfo_t by worldrenderer's schema: OBJECT_TYPE_NO_SHADOWS -> 4, RENDER_TO_CUBEMAPS ->
      1, NEEDS_DYNAMIC_SHADOWS -> 0x100 ...) and its draw call's material flags (vf 0x120 = meshsystem 180027140 =
      180027160, & 0x180 == 0 -> 8); AddFragment adds 0x20 when the fragment has no 0x40 and a vis cluster member
      list, and the object's +0xe0 gains 0x40 with a fragment's 0x100. The draw call's +0x1c is 180027160 over the
      material's attributes (the meshsystem object's flag callbacks: none, captured); its +0x14 is m_nFlags or the
      180175160 table of draw-call booleans (m_bUseCompressedNormalTangent 1, m_bHasBakedLightingFromLightMap
      0x10, ...), then 180027830's set and clear. Ported: Cubemaps/CubemapLayerFlags.cs.
    - Captured: the BuildCubemaps view's layers and their fields (Static Opaque Forward, Depth Prepass Large /
      Small / AlphaTested, OpaqueRefractDepth, SkyOccluder*, ToolsUtil Opaque), the cascades' DrawDepthOpaque.
    - Settled later 10-27 (the material's runtime store): every key is inserted by materialsystem2 18001e6ad
      (hardware watchpoints, tools/cubehost/material_store.js), called from 18000e690, which merges one render
      mode's program attributes into the store (setter 18001e4a0; expressions over that program's features); one
      job per mode (180014ed0), so a later job overwrites an earlier value and only logs a mismatch, in completion
      order. The modes are the features file's list (VRF FeaturesHeader.Modes: a fallback shader or a static combo,
      SHADERATTR probe prints them). The -vulkan game reads shaders_vulkan, whose programs declare
      BindlessResources; AllowBackfaceCulling and DepthPassBatchID come from the Depth mode's program
      (csgo_depth_only, `FEAT[2] ? 0 : 1`), which settles GROUND_TRUTH 40's fallback for these shaders. Ported:
      MaterialAttributes.OfModes over a vulkan_50 ShaderLibrary; on probe01 every key of the game's dumped store
      (16) equals ours, and so do both draw-call flag words and the object flag (CubemapLayerFlagsCaptureTests).
      Where two modes disagree the game's value follows job completion order and is not data (none seen).
    - Settled (the vis query): worldrenderer 18001f1be calls CPVS vf 8 (engine2 1.41.9.2 180239730, 10-15
      180238060: hardware breakpoint in layer_members.js): one cluster when the world has no vis data (atixref) or
      the box covers the vis bounds, else a walk of CPVS's runtime octree (180237ce0 / 1802378f0). Only the
      fragment's 0x20 depends on it, and no layer test reads 0x20 (required bits 1 and 0x100, excluded 2, 4, 8).
    - Settled by computation: every captured variant of the cascades' DrawDepthOpaque gives the same tests for a
      world fragment, so which CSM instance is the cube's does not change membership. The visible entries' +0xc
      bit was 0 in every cube-view test (measured; its writer in the cull is not read).
    - Open: (1) the stage before 180041f30 is settled in 159; (2) CPVS's octree walk for worlds with vis data
      (the 0x20 bit only); (3) the order of a cascade's two layers; (4) the cull's +0xc fade bit.

158. **The joint stream in the island solve** (2026-10-27: decompile of vphysics2 0924's 18030f0b0, 180312650,
    18030f860, 1803120f0, 180310dc0, 18030fd50, 180205b40 and the two joint vtables' +8 / +0x10 / +0x28 / +0x38;
    Simulation/JointSolver.cs, IslandSolver.cs).
    - Settled from the code: RnSolver_SolveSerial prepares the island's joints (context +0x30: array, count,
      solver bodies, stream, size) before the type-1 constraints and both contact lists, and each velocity and
      position iteration walks them first in the same order. The stream is one buffer: per joint, record 0x12 (4
      bytes) when both bodies are infinite in mass (unless joint +0x2b) or in inertia (unless +0x2c), else the
      0x14 header (type, solver +0x68 / +0x6c, mass scales 1) and the type's vtable +0x28 prepare with warm 1,
      whose return is the record type. Priority d = joint +0x2a (signed) - B + A: d < 0 zeroes scale B when A is
      not infinite in a tested axis, d > 0 zeroes scale A likewise. The last velocity iteration stores each
      record's impulses and runs every joint's break test, skipped records too (vtable +0x38 = 180205b40 for both
      ported types: |linear impulse| above +0xac, a NaN breaking too, or the angular impulse (conical 0, revolute
      the hinge pair's length) above +0xb0, each only when positive); a broken joint goes in world +0x88 +0x30.
      The base ctor (1802042b0) and both aggregate joint builders leave +0x2a..+0x2c and both thresholds 0.
    - Exact against vphysics2 (JointStreamOracleTests): 2,908 random islands of 2 to 6 bodies and 1 to 6 mixed
      conical and revolute joints through Valve's own wrapper and dispatches (each joint behind its real
      vtable), every stream, body and joint byte after the prepare, 1 to 3 velocity iterations and 2 position
      iterations; the break test on 20,000 random thresholds. Trials with a NaN in Valve's output are skipped
      (3%).
    - Open: joints in the island manager (step 4 of PHYSICS.md's roadmap), so WorldSolver still refuses a body
      with joints; records other than 5, 0x0b and 0x12 throw.

159. **The layer's object pre-filter, and the draw list from the layer tests** (2026-10-27: hardware breakpoint on
    180041f30 (tools/cubehost/caller_of.js), decompile of scenesystem 20261015 1800edb90, 1800f3b10, 1800d5aa0,
    1800d5260, 18003aa10, meshsystem 20261001 180012150; layer_members.js runs of probe01 and atixref).
    - Settled: 180041f30 is CAggregateSceneObjectDesc's slot 0x20, called from 1800edb90, which 1800f3b10 calls for
      each visible object of a layer that passes (decompile lines 214 .. 223): the object's cull record flags
      against the layer's +0x480 (all required) and +0x488 (none), the object's +0x78 against +0x490 / +0x498,
      and the layer's +0x6dc against the object's +0xc4 unless the layer has flag bit 26 (a branch first taken
      when +0x6dc is set is not ported; +0x6dc was 0 on every captured cube-view layer). Ported:
      CubemapLayerFlags.ObjectPasses.
    - Settled: the object's +0x78 and +0x80 are worldrenderer 18001e920's two words from the aggregate's
      m_allFlags / m_anyFlags (schema: AggregateSceneObject_t +0x0 / +0x4) and the first draw call's material
      flags, the +0x80 word by meshsystem 180012150 (the material system's vf 0xd0), passed to CSceneSystem's
      create (1800d5aa0) whose init (1800d5260) ORs it into the constructor's 0x40000000 (18003aa10); then
      worldrenderer sets bit 61 and clears bit 60. Captured: the create receives 0x60d100400001 and the object
      ends with 0x200060d140400001 on probe01, both equal to ours (CubemapLayerFlags.ObjectWords,
      CubemapLayerFlagsCaptureTests); the cull record's flags equal +0x80 (measured; the copy is not read).
    - Checked against the game's own calls (layer_members.js "record" with each call's layer fields, since layer
      objects are reused across views; layer_members_check.py): the flag test never rejects an object the game
      went on to test on any cube-view layer (probe01 374 agree, atixref 135,392); the passes it lets through that
      the game did not test are the loop's later conditions. The 12 exceptions are the player view's "Static
      Opaque Forward Fade" layer (+0x488 all ones, so 1800f3b10 can pass nothing there): its objects come from
      another caller of 180041f30, which the cube build does not use (its Fade layer is tested on nothing).
    - The split this makes: alpha-tested draw calls (AlphaTestZPrepass, material flag 0x100) set +0x80 bit 29,
      which "Depth Prepass AlphaTested" requires (+0x480 0x20400001) and Large / Small exclude (+0x488).
    - Measured with RenderDoc: the cascades' views also hold a "Depth Prepass AlphaTested" layer that the cascade
      passes do not draw (every alpha-tested caster's one cascade list is its DrawDepthOpaque one), as the
      BuildCubemaps view's SkyOccluder*, ToolsUtil Opaque and OpaqueRefractDepth layers draw no aggregate.
    - Result: CubemapFaceDrawList selects each list through ObjectPasses, DrawCallPasses and FragmentPasses with
      the captured layer values (CubemapLayers), and DrawListEqualsTheCapture passes on all six faces of
      probe01, s2c_l5probe and atixref (atixref: 560 lists, the alpha-tested prepass after Small).
    - Open: the loop's later per-object conditions in 1800f3b10 (the culling box, an object-level size test
      against the split, LOD fade) are not ported as such; the draw list matches without them on these maps. The
      order of the alpha-tested prepass within its pass, and the remaining items of 157 (CPVS's octree walk for
      the fragment's 0x20, the cull's +0xc fade bit).

160. **Joints in the island manager and SetType** (2026-10-27: decompile of vphysics2 0924's 1801fd720, 1802055f0,
    1801fe0b0, 1802cb0a0, 1802ca9c0, 1802cb9e0, 1802ccef0, 1802cd5b0, 1802cc790, 1802ce050, 1802cc600, 1802ce7b0,
    1802cc290, 1802ccbb0, 1802ce270, 1802cc370, 1801bd3e0, 1801b6000, 180333110, 180332cd0, 180332bb0, 180332b80,
    18022c400; Simulation/IslandManagerOps.cs, RnWorld.cs, WorldSolver.cs; RagdollIslandsCaptureTests).
    - Settled from the code: CRnWorld_AddJoint puts a joint in world +0x6a8 (type != 0) or +0x6b8 and on its
      bodies (a body with +0x4a bit 2 is dropped, one body twice drops both; edges prepended to body +0x70; the
      pair filter 1801bdea0 walks them). A joint is an island edge when it is in the world, of a type other than
      0, not disabled, has both bodies, neither out of the manager (+0x45 == 3), and one dynamic. Activation and
      deactivation are the contact ones (larger heap island, ties to body 2; holes; split pending), joints
      appended before type-1 and contacts in a merge and walked first per body in the split's flood fill.
      Colouring also starts at joints / 8 >= 25; joints take the lowest of ten colours (mask bits 0..9).
    - SetType (1801bd3e0): the body leaves the manager unless +0x45 == 3 (its active joints deactivated in edge
      order, then contacts, the node out, +0x45 = 3), the type and static flag set, to dynamic every joint's
      impulses scaled by 0 (vtable +0x18), then back for an enabled body in the world (+0x45 = 0, node added,
      each joint re-evaluated in edge order), then the wake. RnSolverBody_Build's NoDynamicContact walk: a
      dynamic body with an active joint to another dynamic body.
    - Exact against the capture: s2c_ragdollprobe's two dynamic aggregates replayed as recorded (bodies keyframed
      at creation, joints in world order, SetType 2 in the captured order) give both step-0 islands slot for slot
      (28 slots, 13 holes, the bodies' order), the joints' island order (12, 11, 2, 3, 0, 1, 7, ...), awake
      counts, iteration maxima and every joint's active flag, colour, island index and solver indices; the two
      undynamic aggregates' joints stay inactive.
    - Open: the split's result (by step 100 Valve's islands have 16 bodies and no holes, with ground contacts)
      waits for the stepped settle (step 6); joints in coloured islands' colour jobs and the step's broken-joint
      pass throw; the TOI solve with joints is not read.

161. **The aggregate build: bodies, shapes and joints** (2026-10-27, open: the model pose's last ulps; decompile and
    disassembly of vphysics2 0924's 180042ba0, 180045630, 180044bc0, 180046280, 180046220, 1801bbb60, 1801bb110,
    1801bcf70, 180256c80, 180256d00, 180047600, 18005d370, 18005cb80, 1801ef300, 1801f0750, 180228c70, 1802135a0,
    180205a20, the conical and revolute setters, 180204ff0, 180204d70 (disassembly 180204e64..180204f6e), 1801b4d30,
    the body wrapper's vtable 0x1803f44e0; resourcecompiler 0923's 18105dbd0 (disassembly 18105e18a..18105e247),
    18105ce70, 181f03210, 181f02fa0, 181f0bb60, 18174dc50; Maps/SettleWorld.Aggregate.cs, AggregateBuildCaptureTests).
    - Settled from the code: a model with two parts or a joint goes to world vfn 0x418 with the part's transform
      (node position, scale, orientation). vphysics2 places body i at MatrixQuaternion and translation of the part's
      matrix concatenated with bind pose i (translation times the aggregate scale), its scale the largest column;
      keyframes it (mode 3, SetType 1 with its wake), adds the part's shapes at the aggregate scale with their names
      and surfaces, gives each shape its palette entry as base and current attributes, then SetMass (clamped to
      (0, 1e6], 500 otherwise, +0x4a bit 8, the mass update), the inertia scale when above 1, damping and drag
      (+0x249 bit 5). Then the joints (180047600): the desc from each VPhysXJoint_t (frames, enable bits, friction,
      elasticity, plasticity, elastic damping as the damping ratio, frequency 0, limits in degrees), frames
      normalised, constructed (defaults: mode 3, frequency 10, damping 1, limits +-pi), added to the world, then the
      setters in the creator's order; friction picks body 2 unless static (180204ff0) and sets mode 1 with torque
      r ((|g| m) friction) (the disassembly's order; Ghidra prints r |g| m friction), r the larger of the inertia
      radius and the scaled anchor's distance from the local mass centre.
    - Settled: resourcecompiler then gives every shape one entity record (palette entry picked by vfn 0xe8, group 4,
      flags 7, owner -1; 0xc3001 interactions for a simulated class, collision_group, layer 0x400 dropped); a palette
      entry whose layers are the default mask (18045b2a0, set at runtime) leaves no base layers, as the capture's
      +0x78 shows. It poses each body (SetTransform, nothing below 1.4210855e-14 squared) from the model's own pose:
      181749d20 looks the part's bone up, by name with case ignored, in the pose 181f03210 builds; the bind pose is
      the fallback. Ghidra's decompile drops that override branch; the disassembly shows it.
    - Exact against the capture (s2c_ragdollprobe's four CT agents): every body field but the pose's (mass,
      inertia, damping, drag, flags, awake order, world inverse inertia), every shape's attributes, and all 56
      joints byte for byte (padding the constructors never write excluded).
    - Settled 2026-10-28 (ledger 166): the model pose is the skeleton's reference pose with each bone's scale; the
      graph pose was never read. All 60 bodies equal the capture, pose included.

162. **A cubemap face rendered with no dump: its bindings, binner dispatches and frame** (2026-10-27: Valve's .vcs
    write sequences; decompile of scenesystem 20261015 1801b2850 and rendersystemvulkan 20261015 180072850; the
    passes of the probe01, s2c_l5probe and atixref captures; Gpu/CubemapFaceRenderer.cs, CubemapFaceRendererTests).
    - Settled from the programs' own tables: every descriptor binding of every face program is named by the combo's
      write sequence (VfxVariableIndexData: layout set, slot). Measured on every binding of every action of the
      captures (CubemapFaceBindingsProbe lists them): a ConstantBuffer at its slot, a Texture register (textures and
      structured buffers) at 30 + slot, a Uav at 158 + slot, a SamplerState at 14 + slot, the stage's $Globals at
      binding 0 of its set (vertex 0, pixel 1). The descriptor layout builder in rendersystemvulkan is not read.
    - Settled: the colour pass's set 3 binding 31 is generic's g_BarnLights (attribute BarnLights, slot 1), not
      g_CullBits (slot 0, binding 30, the binner's output). No barn light is binned in the cubemap view.
    - Settled from the code: 1801b2850 dispatches through the render context's vf 0x320 with thread counts: the
      depth bins (+0xf380 = lb_bin_slices rounded, the mask words, 1), when there is more than one bin, then the
      tiles (+0xf378, +0xf37c, the mask words); the mask words are the light, environment map and volume words
      (+0xf390 + 0xf394 + 0xf398). rendersystemvulkan 180072850 divides each count by the bound program's workgroup
      size (+0x140..0x148) rounding up: (8192, 1, 1) / (32, 1, 1) and (128, 128, 1) / (8, 4, 1) give the
      captures' 256 x 1 x 1 and 16 x 32 x 1. Both programs store each output word from one thread (one OpStore, no
      atomics), so the cull bits need no clear.
    - Measured (RenderDoc, every face of the three captures): the passes and clears in a face (the atlas cleared to
      1 whole, the face to colour 0 / depth 1 / stencil 0, the half depth to depth 1 / stencil 0 and the AO target
      to white over 512 x 512, the face's stencil to 0 before the quarter downsample), the targets' sizes (half
      depth and AO at the 1280 x 720 window, quarter depth 256 x 256), the image views' formats (a texture's view is
      sRGB exactly where its variable reads sRGB, VfxVariableDescription.SrgbRead), and the whole mip chain of
      every material and lightmap texture bound.
    - Measured, not read: the renderer picks generic's and csgo_depth_only's D_COMPRESSED_NORMALS_AND_TANGENTS by
      the draw call's m_bUseCompressedNormalTangent (true on probe01's world nodes), and every program it loads is
      byte for byte a module of the capture. The engine's path from the flag to the combo is not read (meshsystem
      names MESH_DRAW_FLAGS_USE_COMPRESSED_NORMAL_TANGENT, scenesystem a COMPRESSED_TANGENT_SPACE batch flag).
    - Result: probe01's two cubes from the package, the shader and content packages and the scene's inputs alone:
      all twelve faces equal the game's readbacks, both EXRs and the whole env_cubemap_array.vtex_c (945,838
      bytes) are the game's byte for byte, no file of a dump read. s2c_l5probe: five faces exact, -X differs where
      the spawned pawn's viewmodel draws (not ported; roadmap 0e).
    - Open: the scene's inputs are typed in the test (probe01's volumes, fog, sun, sky, lightmap scale), not read
      from the package's lumps yet; aggregates with fragment transforms of their own, alpha-tested prepass
      draws, the viewmodel and non-aggregate scene objects throw; the two vkCmdCopyImage of each face (they do not
      reach the readback) are not modelled; the texture streaming state of big maps (every capture so far holds
      whole mip chains).

163. **A cubemap face's scene from the package, and its programs by the shaders' render modes** (2026-10-27:
    the probe01 / s2c_l5probe / atixref lumps and captures; the .vcs features files' mode tables; client 20261015
    1801fd3c0 (C_EnvCubemapFog's schema), 180206e50 (the fog attributes); CubemapFaceBlocks.FromPackage,
    CubemapFaceRenderer.Programs, ValveProgram.LoadOrNone, CubemapFaceProgramsTests).
    - Settled by the readers' result: probe01's and s2c_l5probe's scene read from the package (volumes, cube origins
      and handshakes, env_cubemap_fog, light_environment, env_sky, the world's lightmap uv scale, gameinfo's
      MinSpecLightmapSize) equals the values typed before, which matched the captures word for word; both probe01
      cubes stay byte for byte. Read but not ported: the fog's height vector (the struct 180206e50 receives is
      filled elsewhere; C_EnvCubemapFog has its own m_flFogHeightWidth), so a height range throws.
    - Settled from Valve's data: a material draws a pass in its shader's mode (features header Modes): Depth for
      the cascades and prepass, ToolsVis for colour; a mode names another shader (generic, csgo_complex,
      csgo_vertexlitgeneric: csgo_depth_only) or a static combo of the shader's own (csgo_foliage,
      csgo_environment, csgo_environment_blend: S_MODE_DEPTH=1). A pixel program is absent where its static combo
      is not in the file (csgo_environment's depth mode) or its dynamic combo's render state names shader file -1
      (csgo_depth_only's opaque combos): every depth draw of the three captures without a pixel shader is one of
      these. D_ALPHA_TEST_PREPASS=1 in the alpha-tested prepass layer.
    - Result: every world draw of face 0 of all three captures draws with our programs byte for byte
      (CubemapFaceProgramsTests: atixref 256 draws, 121 cascade, 68 prepass, 67 colour; probe01 24; s2c_l5probe 22),
      with these rules:
      - Settled by decompile (ledger 157's table): the shadow fast path. A draw call whose +0x14 has bit 2
        (m_bUseShadowFastPath, meshsystem 180027830 over the material's attributes, CubemapLayerFlags.DrawCallFlags)
        draws the cascades with materials/dev/reflectivity_90.vmat (the material every such batch names), generic's
        Depth mode, csgo_depth_only's plain combo; the prepass keeps the material's own Depth mode.
      - Settled by decompile: the colour pass's baked lighting from the same word: 0x10
        (m_bHasBakedLightingFromLightMap) the lightmap, 0x8 the vertex stream, neither D_BAKED_LIGHTING_FROM_PROBE
        (a pixel-only combo). atixref's props and its vertex-painted brush blends are probe-lit.
      - Measured: the depth passes bind the draw call's m_nDepthVertexBufferIndex stream alone (255: the mesh's
        one stream), the colour pass every stream, each in its own binding before the instance ids and the zero
        stream; D_COMPRESSED_NORMALS_AND_TANGENTS is the draw call's m_bUseCompressedNormalTangent where a bound
        stream holds the NORMAL.
      - Measured: S_SHADER_QUALITY (a system-set combo, VfxStaticComboSourceType S_SHADER_QUALITY) is 1, the
        captures' video config. A configuration the file's static rules exclude is reduced as VRF's
        ShaderDataProvider does (csgo_foliage's depth pixel combo, csgo_vertexlitgeneric's); the captured modules
        are the reduced combos. (tools/cubehost/spvmatch needs a size limit above the largest .vcs, 200000000,
        or the big pixel programs read as "not found".)
    - Settled by the captures' buffers (CubemapInstanceRecordsTests): an instance record's first word is its
      fragment's transform slot's packed tint (AddFragment's PackTint; atixref's mailbox 0xff88bb77 from
      m_vTintColor 119 187 136) and its second the index of the slot's 3 x 4 (AggregateCulling.Fragments'
      transform). Every (view, aggregate) block of atixref's face 0 (93 blocks, 401 instances) and probe01's (10)
      reads the captured tints and transforms bit for bit. The buffers' own layout is the game's allocator's; ours
      places each (aggregate, slot) once in first-use order (only the entry an index names is read).
    - Read, open: the sky's D_FOG_TYPE is the sky scene object's +0xf4 (scenesystem 180154fb0 sets the token
      0x83cb90d6 from it; type 2 adds the angular test of +0xf8 / +0xfc), which client 180282390 copies from
      C_EnvSky's m_nFogType (+0x10b8, key fog_type, schema 18026d020). Neither probe01's nor atixref's env_sky
      has the key, yet probe01 draws 0 and atixref 1: the value's other writer (C_EnvSky is client-side; no server
      class has it) is not found. The renderer draws D_FOG_TYPE 0.

164. **Batch A: single-function reads of rules taken from output** (2026-10-27, resourcecompiler 0923 through
     ReVa, with CS2 open; no compile or capture).
     - Settled, ported: CMapMesh's copy (181081be0, the instance collapse's vf 0x3d8 through 180f86650) sets
       a fixed list of properties. It leaves out renderwithdynamic (setter 1810e9960, +0x3b74), fademindist
       (1810e9820, +0x3b50), fademaxdist (1810e9800, +0x3b54) and keep_vertices (1810e9870, +0x3b76), so a
       copy keeps the constructor's -1, 0, 0 and 0 (181086b70). CMapMesh_ConvertMeshForBuilder hands +0x3b50
       and +0x3b54 to the record's +0x5c and +0x60. NodeEntryHeader.World and NodeMeshEntries.RecordOf now drop
       the fades on copies as they already dropped 0x200 (ledger 41). No corpus copy is known to have a fade.
       CMapStaticOverlay derives from CMapMesh (its factory 18118fb60 runs 181086b70), so its copies take the
       same path.
     - Settled, ported: the cubemap and probe record's targetname (ledger 48). 180240a60 reads every record key
       from the builder node's exported keys (the node's vf 0x190 into the KV at local_1e68), and the record
       keeps the name as it is there. The landmark and point_camera branches of the same function strip a
       leading "[PR#]" (V_stristr_fast), so those keys carry the lump's fixup. EntityLumpAuthor.Lighting now
       gives the record the targetname with the lump's fixup. Open: whether a world mesh's lightingorigin,
       which the pick compares against it, carries the fixup too.
     - Settled, ported: the prop aggregate threshold's sum (180272ce0, `0x35f < sum`) adds each entry's CMesh
       +0x24. CMesh_Alloc (1812d6a90) stores the vertex count at +0x18, floats per vertex at +0x1c and the
       index count at +0x24 (its buffer is count x 4 bytes); WRBMeshEntry_CanMerge caps the +0x18 sum at
       0x200000. NodePropAggregates now sums the draw's index count (MinimumIndices).
     - Read, open: the builder context's +0x605 (vf 0x100, 1801f9040) is "lighting is baked", set in
       1800fd550 (MapCompileArgs.BakesLighting; a [reg+0x605] scan does not find that write, so the store goes
       through another base). MapRootFile's argument table says CWorldRendererBuilder_Build reads norte when
       vis is on or when it is set, then debugvisgeo and dumptris in CreateRayTracingEnvironment. The port
       adds those only when vis runs, so a compile that bakes with vis off may read three arguments more than
       ours lists. Read Build's branch before changing it; no Valve compile of that switch set is in the
       corpus.
     - Read, not ported: the extra vertex streams of a mesh (NodeMeshEntries.WithPaintStreams, measured on
       LowPrecisionUv1). 1810dca50 (from CMapMesh_NameSlotsAfterOverride 1810dcca0) walks each material's input
       list (181ef9b30: the material's vf 0x98, records of 0xc4 bytes, the name at +0x40) and adds
       VertexPaintTintColor, VertexPaintBlendParams or VertexPaintBlendParams1 (AddExtraAttributeStream, slots
       0 to 2; defaults in the table at 18237b480) when an input of that name is found. With its second
       argument set it removes the ones no material wants. Porting it needs each material's input names (the
       shader's vertex inputs). The second texcoord is not added here.
     - Read, open: Material_VisFlags (18020d860) asks the loaded material for int attributes by token hash
       through its vf 0x50; bShaderBlended, renderbackfaces and NeedsDynamicShadows are answered there, in
       materialsystem2, which is the next read (TraceScene.MaterialFlags).
     - Located, open: the overlay texcoord shift (NodeOverlays.Recentre). CMapStaticOverlay_GetOverlayDescs
       (181194f40) copies each face's texcoords from the overlay's projection cache (+0x3cc0; records 0x4a0
       apart, texcoords at +0x100) through 181363320. There is no float truncation in 18118f000 to 1811a0000 or
       in WRBNode_GenerateOverlayMeshes, so the shift is where that cache is filled.
     - Located, open: the builder mesh's renormalised normals (NodeMeshEntries). Ruled out: the world transform
       1810c4e50, CMapMesh_ConvertMeshForBuilder's direct callees and WRB_CollectMeshEntries' callees.
     - Ledger 15 (what picks Zstd or LZ4) is answered by ledger 3's decompile of binary_auto.

165. **The trace flags from the material store, and the node's static prop lists** (2026-10-27, decompile of
     resourcecompiler 0923 and materialsystem2 20261015 with CS2 open; checked offline against Valve's .rte).
     - Settled, ported: Material_VisFlags (18020d860) asks the loaded material's int store (CMaterial2 vf 0x50,
       18000bb80: chunks of 12 key hashes at +0x180, the chunk list at +0x68, then the parent set at +0x60) for
       each key. The key names are not in materialsystem2, so the store is filled by hash from data: the
       vmat_c's attributes and every render mode's program attributes (ledger 157, MaterialAttributes.OfModes).
       renderbackfaces is a shader attribute in the pc programs (`FEAT[n] ? 1 : 0` on most csgo shaders, always
       True on csgo_panolayer_rt, False on csgo_eyeball, csgo_weapon_sticker and grasstile); bShaderBlended and
       NeedsDynamicShadows are declared by no pc vs or ps program. TraceScene.MaterialFlags now reads the store
       (GameContent attaches it to each MaterialInfo, built from shaders_pc_dir.vpk on first use); the measured
       table (ShaderAttributes.AlphaTest / Translucent and the F_RENDER_BACKFACES shortcut) no longer feeds it.
       TraceSceneTests: every triangle's flags equal Valve's .rte on probe01, cardtest, ze_hold_em_p, atixref
       and s2c_prefabprobe4 (Mako's fixture is not on disk).
     - Settled, ported: the node's static prop lists (ledger 47). EntityLump_StaticModelRecord (180245770)
       files each record in a CUtlRBTree at node +0x190 (builder +0x118), compared by byte strcmp (180291a00),
       under its model path; a record with material overrides (+0x40, from the prop's +0x268), a deformer
       (+0x80) or +0x1e8 (the prop's +0x230) goes under "%s_%d" of a process-wide counter that starts at 0
       (183bb375c, in .bss). AddStaticProps walks the tree in order (1800e00b0) and each list last to first.
       NodePropEntries now keys by ordinal model path and gives a deformed prop its own "path_N" list; the
       +0x40 and +0x1e8 triggers are not ported (their sources are not read). Not verified: deformerprobe2's
       capture (NODEENTRIES) is not on disk and needs CS2 closed; it also checks that our walk is the export
       order the counter runs in.
     - Read, open: the paint streams' material inputs (ledger 164). Material vf 0x98 is
       CMaterial2::GetVertexShaderInputSignature (18000c450): the first valid layer's CVsInputSignatureVector,
       whose elements' names (+0x40) 1810dca50 compares. Next: which layer and which dynamic combo's signature
       (VfxStaticComboData.VsInputSignatureIndices) it returns, then port WithPaintStreams from it.


166. **The model pose the aggregate build reads** (2026-10-28: decompile of resourcecompiler 0923 181f0bb60, 181f02fa0,
     181f03210, 18174dc50 (disassembly 18174dca0..18174dd3b), 1817dd320, 181eff710, 18105dbd0 (disassembly at
     18105e100), and of animationsystem 20261023 (new in the Ghidra project) 18006d560, 1804abdb0, 1804acdd0,
     18006f450, 18006cd20; Frida captures of resourcecompiler's s2c_ragdollprobe settle with
     tools/settle/probes/settle_pose.js, settle_posewriter.js and settle_poseupdate_calls.js; ModelPoseCaptureTests,
     AggregateBuildCaptureTests).
     - Settled from the code: 181f0bb60 keeps two model-space poses on the node. Without stored overrides it writes
       the skeleton's reference pose into +0x310 (18174dc50: each bone's local is skeleton +0x98 position, +0xc8 scale
       in the w lane, +0xb0 rotation, composed onto its parent by 181253510, the root onto the identity). With an
       animation helper (node +0x2b0, a CAnimationHelper from animationsystem; vfn 0x90 returns helper +0x40) it also
       composes the helper's pose (1817dd320, same compose) into +0x328. 181f02fa0 returns +0x310 for flag 0 and
       +0x328 for flag 1, and the aggregate build asks 181f03210 with flag 0 (18105e100: xor edx, edx). So bodies are
       posed from the reference pose. Our ModelPoses composed with scale 1: that was the 1 to 4 ulps of ledger 161.
     - Settled from the capture: the helper's skeleton set (vfn 0, 18006d560 -> 1804abdb0 -> 1804acdd0) gives the
       same reference locals, bit for bit, for both models of the probe (ctm_sas, 94 bones, 93 with a scale other
       than 1, and the chicken, 117). rc 181eff710 then calls the helper's update (vfn 0x88, 18006f450, dt 0). For
       ctm_sas the update changes the locals (no graph instance at +0x150, byte +0x249 0, no sequences; the decode
       context path), and for the chicken it leaves them. 1817dd320 composes whatever the helper holds; its result
       equals our ModelSpace of the captured locals on all 8 calls (ModelPoseCaptureTests).
     - Ported: ModelBones carries m_boneScaleParent, SettleWorld.ReferencePose and ModelSpace.
       AggregateBuildCaptureTests passes: every field of all 60 bodies, pose included, every shape attribute and
       all 56 joints equal the capture.
     - Measured: with the pose exact the stepped settle equals Valve's on the ragdoll bodies through step 98; the
       step 99 difference is ledger 167 (the chicken's placement and the dynamic order).
     - Not ported: the helper's update (only +0x328 reads it; nothing in the settle does), stored bone overrides
       (181efec40, which fill +0x328 and copy it to +0x310), and models with no m_boneScaleParent (refused).

167. **The ragdoll settle in lockstep: a single body's bind pose, and the dynamic order** (2026-10-28: disassembly of
     resourcecompiler 0923 18105dbd0 at 18105e299..18105e317 and decompile of 1801ae160; vphysics2 0924 1801f6980 and
     1801ecfb0 for the contact list; three Frida captures of s2c_ragdollprobe's settle, the last with
     tools/settle/probes/settle_contacts.js; RagdollLockstepTests).
     - Settled from the code, ported: a model with one part and no joint gets one body, which rc then sets (body vfn
       0x1b8) to the part's transform (part +0x30 position and scale, +0x40 rotation) composed (181253510) with bind
       pose 0 as 1801ae160 decomposes it: the unscaled matrix's translation, the largest column length as the scale,
       the unscaled rotation's quaternion. With the physics data's +0x18 count at 0 it composes with the identity
       (the WithIdentity the port already had). We placed such a body at the node: the chicken (prop_ragdoll, one
       part with a bind pose) sat 7.3 units low and unrotated. SettleWorld.FromModel and BindPoseTransform.
     - Settled later the same day (ledger 168): the +0x18 count is the bone names' (vphysics2's aggregate vfn 0xd0,
       180045af0, reads the names at +0x10 under it); the port tests the bone names.
     - Measured: the contact differences from step 33 and the step 99 drift came from the chicken and from the order
       bodies turn dynamic. That order is ledger 52's (the node hash table over heap pointers): three Valve runs gave
       three orders. With the chicken placed and each capture's own order, all 33 dynamic bodies equal Valve's before
       every captured step, every active contact equals Valve's (touch state, list index, size, manifolds at the
       collide's entry and exit, steps 0 to 100), and so do the final poses, on all three captures, 2700 steps each.
     - Read for the probe: world +0xa10 count, +0xa14 capacity, +0xa18 the active contacts; contact +8 and +0x10
       shapes (shape +0x10 body), +0x18 and +0x1c children, +0x40 sort key (the shapes' +0x1c proxies), +0x80 the
       manifold block (count at +4, manifolds of 0xe0 bytes from +8). The key alone repeats across body pairs.

168. **The ragdoll write-back and boneTransforms** (2026-10-28: decompile and disassembly of resourcecompiler 0923
     18105b350, 18105a410 (18105a530..18105a6b5), 181253780, 18105cac0, 181749980, 181053ee0, 18174b4b0, 18174aeb0,
     181004390, 180dd2500, 18105b2d0; vphysics2 0924's aggregate vtable 1803f8740 (180048860, 180045af0, 180046a80);
     a full compile of s2c_ragdollprobe under capture, capture_settle.py --keep-package with hold_to_exit.js).
     - Settled from the code, ported (Maps/SettleWorld.WriteBack.cs): PhysObj_WriteBackTransform passes the node's
       transform to 18105a410 and sets the node's origin and angles from what it returns. For an aggregate the entity
       transform is body 0's (GetTransform) composed with the inverse of bind pose 0, its translation times the
       instance scale (+0x28), decomposed as 18125b270 / 18125de90 do; each body's bone is the inverse entity
       transform composed with the body's, its position times 1 / s, under the part's bone name (vfn 0xd0). For a single
       body with bone names (+0x18) it is the body composed with the inverse of bind pose 0 (1801ae160), else the body's
       own (the existing EntityPose). CTransform_Invert's scaled branch is ported too.
     - Settled, ported: 18105cac0 keeps the bones found in the model's skeleton by name, case ignored (181749980),
       ordered by bone index (181053ee0 sorts on it), names as the physics data spells them, and stores the list on
       the node; ExportToLump writes it as boneTransforms ("%s: %f %f %f %f %f %f %f;", 18174aeb0) after the table
       keys and before class_game_keys, for a class deriving from PosableSkeleton (180dd2500). An aggregate is asleep
       when every body is (vfn 0x180).
     - Measured, not a rule: a single body (the chicken) writes no boneTransforms key; the port writes none for an
       empty list. The node's earlier pose entries that 18105cac0 merges in are not ported (no .vmap stores one,
       ledger 45).
     - Exact: SettleLumpTests on s2c_ragdollprobe: the whole lump equals Valve's, 0 differences (both settled agents'
       boneTransforms, the chickens' poses, spawnflags). The settle ran in our dynamic order, not the reference's;
       this map's lump is the same from two Valve compiles (10-23 and 10-28), so here the order does not reach the
       printed values. SettleWorld.Run no longer refuses an aggregate, and no longer refuses a single body with a bind
       pose.

169. **Ragdolls beyond the probe: hulls in an aggregate, and the body part's defaults** (2026-10-28: decompile of
     vphysics2 0924 180044bc0 (the shape loop) and 180091a10 (VPhysXBodyPart_t's schema factory, whose class entry at
     18044a198 points at the field table 180449fb0); a full compile of s2c_ragdollprobe2 under capture; RagdollLockstepTests,
     SettleLumpTests, AggregateScanTests).
     - Found: no other content map on disk has an aggregate in its settle world (AggregateScanTests over every .vmap).
       ar_pool_day ships no source, and its mattress models only inside its own package. s2c_ragdollprobe2
       (tools/physics/ragdoll_probe_map.py --set pool on s2c_rounds) drops its lounge_chair_mattress (9 parts, hulls) and
       buoy_pineapple_mattress_ragdoll (13 parts): flat, tilted, two stacked, spawnflags 65540; the two models are copied
       into the s2c_rc_probe game folder.
     - Settled from the code, ported: 180044bc0 adds a part's hulls after its spheres and capsules (body vfn 0x68, the
       hull as stored at the aggregate scale), named and given the surface's material like the rest. Meshes (vfn 0x70
       with per-triangle materials) are still refused.
     - Settled from the code, ported: a VPhysXBodyPart_t key the model leaves out keeps the factory's value: linear
       and angular drag 1, the rest 0. These mattress models carry no drag keys; reading them as 0 left every body's
       drag off by 1 before step 0.
     - Exact: RagdollLockstepTests on the capture (62 dynamic bodies, every contact, final poses, 2700 steps, Valve's
       order) and SettleLumpTests (our order): the whole lump equals Valve's, 0 differences.
170. **Steam Audio's reverb bake: where its randomness comes from, and the bake replayed** (2026-10-28, open: the
     scene and probes from the .vmap; disassembly of phonon 0923 180a580e0, 1801f5e48..180249a08 and steamaudio 1023
     180087e90, 180084d90, 1800646d3; Frida captures of resourcecompiler on s2c_saprobe; [STEAM_AUDIO.md](STEAM_AUDIO.md)).
     - Measured: three Valve compiles of s2c_saprobe give three .sareverb files; the .sapaths is the same each time.
     - Settled from the code: the reverb rays' seeds are rdtsc (Embree RandomSampler_init from clock() in phonon's
       ISPC jobs, 16 sites; the low 32 bits feed the murmur mix). The time(NULL)-seeded RandomNumberGenerator
       (180a580e0) is not reached by the bake (a hook on it saw no call).
     - Exact: with every rdtsc pinned and -sareverb_threads 1, two Valve compiles are byte-identical; with 8 threads
       they are not (per-thread energy-field sums), so Valve's multithreaded reverb is not reproducible by anyone.
     - Settled from the code: the bake block (180087e90's 0x80 bytes), the context variables and the Embree device
       settings (one reserved byte, 1800646d3); the scene settings (type 1, the Embree device at +0x30) and the
       static mesh settings (180084d90).
     - Exact: replay_phonon.py runs Valve's call sequence through phonon.dll in our own process with the captured
       mesh, probes and block, seeds patched to 0: its probe batch equals the m_pProbeBatch of Valve's seed-0
       one-thread .sareverb byte for byte.
     - Exact (port, 10-28): ProbeBatch / FlatBufferBuilder re-write all 14 Valve probe batches (reverb and pathing)
       byte for byte; SteamAudioFiles writes both whole KV3 files from the batch. The reverb settings are read as
       18006bc60 does (code defaults, gameinfo ResourceCompiler/SteamAudio/ReverbDefaults, worldspawn via the KV3
       getters 1801deba0 / 1801de860 / 1801de9f0); the KV3 conversions between value types are refused, not ported.
     - Settled from the code: Embree 4.4.0's Scene::createTriangleAccel gives phonon's scenes (quality HIGH,
       DYNAMIC, not robust) BVH8Triangle4 with Möller intersectors. Exact: the Möller tests (FMA dot / cross,
       rcpps + FMA Newton) reproduce 75,874 captured rtcIntersect8 lanes (t, u, v, Ng, primID) and 55,854
       rtcOccluded8 lanes by brute force (SteamAudioRayProbe); no tied t occurred, the only case the BVH decides.
     - Settled from the code (10-28): the sample tables (180a58220, kPi 3.141592f, CRT cosf / sinf FMA paths
       182683c40 / 182685260) and the AVX2 reverb kernel 180201a50 with its ISPC math, ported; equal to phonon's
       tables and to Valve's energy fields on 72 of 73 probes. Open: probe 65's single exact tie (triangles 166
       and 169 share the hit edge at equal t), which Embree's BVH traversal order decides; the reverb estimator.
     - Exact (10-28, pipeline): reverb times (180a84df0 + CRT expf / log10f FMA paths) on 7 files; the automatic
       probes (180100e40 box, 18006b2d0 matrix, 180a86090 generator) = Valve's 73; the material table (180128710,
       180084a40, 180084d90 bands 0/7/9) = Valve's 164. Open: the acoustic geometry collector (180f53780 and its
       mesh builders), the probe filters (inside shapes 180fbe450 and the rest), the pathing bake.

171. **Steam Audio's acoustic geometry from the .vmap** (2026-10-28: decompile of resourcecompiler 0923 180101690,
     180100e40, 180f54950, 180f53f40, 180f53780, 180f4bb00, 180f519d0, 180f510d0, 180f50ed0, 180f54020, 1813a3370,
     18139f930, 181057020, 1819cea20, 1801feb70, 1810e6fb0, 1810e3f50 and the SIMD 18125d1f0 behind 1812651a0;
     steamaudio 1023 180067dc0, 180084d90; the s2c_saprobe capture's four scenes; SteamAudioSceneTests).
     - Settled from the code: the scene is the map meshes' physics shapes, not their faces. 180f519d0 calls
       PhysMesh_Shapes (the settle's own, ported) and turns each shape back into triangles (hull fan 1819589f0 at
       scale 1, RnMesh vertices x (1, 1, 1) and its triangles 1819cea20), grouped by (surface index, InteractsAs,
       collision group) in first-seen order, under the node's (translation, longest column, quaternion) rebuilt as a
       matrix (1801feb70) and applied with 1812651a0. 180f510d0 appends the groups: group 1 never, otherwise kept when
       the mask is 0, the group is 0 or InteractsAs meets the mask; a negative surface becomes material 1.
     - Settled from the code: the two collections differ only in the mask. The bake's (180101690, first flag set)
       is 0x1400 (blocksound, window), the probe generation's (180100e40, flag clear) 0x3400 (passbullets too); a
       mesh in Force Static mode (2) takes mask 0. 180f53780's rules: owner solid 0 out; mode from the mesh's
       embedded steamaudioGeometryMode (+0x3b70) else the owner's steamaudio_geometry_mode; 1 out; 3 goes to the
       dynamic list, which neither collection keeps; a mesh under an entity needs solidity 2. The walk is depth first
       in stored order (180f53f40). steamaudio's build (180084d90) takes the lists as they are: metres and axes
       (180063600), each triangle reversed; nothing is welded anywhere (Valve's 206 vertices are the shapes' own).
     - Exact: SteamAudioSceneTests: s2c_saprobe's scene from the .vmap equals all four captured scenes (206
       vertices, 280 triangles, material indices) bit for bit; the probe box from our generation scene gives the
       captured iplProbeArrayGenerateProbes matrix bit for bit, and the floor probes on our scene Valve's 73.
     - Not ported, refused: instances and prefabs (the instance paths), hidden nodes (whether the compile drops them
       before this walk is not read), a mesh's own steamaudioGeometryMode (its .vmap storage is not read), entity
       models and smart props (180f52220), paths and cables, faces whose material sets tools.steamaudiogeometry
       (180f54020), a default-group shape with no InteractsAs (the physics interface's layers, 180f519d0's vfn
       0xd8 / 0x58). s2c_saprobe does not tell the two masks apart (it has no passbullets-only shape).

172. **Steam Audio's pathing bake (.sapaths)** (2026-10-28: decompile and disassembly of phonon 0923 180acb910,
     180ab5650, 180a978e0, 180a93db0, 180a93fe0, 180a93c70, 180a939f0, 180a71c40, 180afbc20, 180a954f0, 180a986a0,
     180a9a8b0 / 180a9ae80, 180a96a30, the CRT atan2f 182683840 and its table 182b64e60; the s2c_saprobe capture's
     iplPathBakerBake block; tools/steamaudio/oracle_atan2.py; SteamAudioSceneTests, SteamAudioAtan2Tests).
     - Settled from the code: the bake is 4.8.1's PathBaker with two 4.9 changes. Dijkstra's range test is the
       straight distance |p[start] - p[v]| <= pathRange, not the path cost; a new parameter flag (+0x40, 0 in Valve's
       call) skips the path search when 1. The API fixes asymmetricVisRange, down (0, -1, 0), visRangeRealTime =
       visRange and no pruning. Visibility with one sample or radius 0 is one rtcOccluded1 from p[i] to p[j] over
       (0, |p[j] - p[i]|]. The heap is MSVC's push_heap / pop_heap on (index, cost) with cost >. The sort predicate
       (invalid, then direct, then node lists) is a total order on content, so the unique-path order is fixed. No FMA:
       lengths are sqrt(((x x + 0) + y y) + z z); the turning angle is atan2f(|perpendicular|, |parallel|).
     - Exact: CrtMath.Atan2 against phonon's atan2f on 200,000 oracle pairs (4,867 edge pairs refused, none differ).
     - Exact: SteamAudioSceneTests.SaprobePathsFromTheMap: from the .vmap alone (our scene, our 73 probes, the
       captured settings) the whole s2c_saprobe.sapaths equals Valve's, 14,007 bytes (73 visibility lists, 51 paths of
       which 3 turn, 2628 refs).
     - Not ported, refused: sampled visibility (numSamples > 1 with a radius), coincident probes (kZero's
       direction), the flag at +0x40, pruning, atan2f's NaN / infinity / mixed-sign zero and underflow paths.
     - Exact (end to end): SteamAudioSceneTests.SaprobeReverbFromTheMap (SAREVERB_FULL=1): from the .vmap alone, our
       scene, probes, reverb kernel (one thread, seed 0), reverb times, the reflections layer (type 0, variation 0,
       needs_update cleared per probe as 4.8.1's BakedReflectionsData::set does; Valve's file has all 73 at 0) and the
       KV3 file: 72 of 73 probes bit-exact, probe 65 the known tie; with Valve's probe 65 put in, the whole
       s2c_saprobe.sareverb (355,507 bytes) equals Valve's seed-0 file.

173. **Steam Audio's probe filters: inside shapes ported, the nav coverage appender read** (2026-10-28: decompile and
     disassembly of resourcecompiler 0923 180100e40, 180fbe450, 180fbcb10, 1800a1980, 1818e4a00 / 1818dace0 /
     1818e3290, the step table at 182771988 and CompileMap 1800fd550; vphysics2 0924 1800655d0, 1801fcf60, 1801dda70,
     1801ec3c0, 1802facf0, 18024ca60, 18024fbc0, 180254790, 1802eca10, 18012e550, 18024af70, 180248c80, 1800010b0;
     a Valve capture of s2c_sainside; SteamAudioSceneTests.ProbesFromTheMap).
     - Settled from the code: the filters run in the order 180100e40 calls them, on Source-inch probes. The physics
       world they query is the compile context's (vfn 0x18), built in CompileMap's "Build physics world"
       (180fb6de0, MapCompile_NavPhysicsWorld): the world navsystem reads, the settle build through PhysDoc flags 0x16.
     - Settled from the code, ported (SteamAudio/ProbeFilters.cs): inside shapes is world vfn 0x5d8, a sphere query of
       radius 0 per probe with InteractsWith 0x400, static bodies, group 3, flags 0x49 (bit 0x80, left from the stack,
       is never read by 1802facf0); a probe goes when any shape passes the filter and its exact test. The tests:
       sphere and capsule against (r + R)^2, a hull by GJK distance to the point (both at the 3x4 identity, which a
       static initialiser makes from 1.0) at most r, a mesh holding the point when closed, else a BVH triangle within
       r. The body frame's origin sums (k x q) + (c + w k), unlike 1801d64a0's ((k x q) + c) + w k.
     - Exact: s2c_sainside (tools/coverage/steamaudio_inside_specimen.py: s2c_saprobe with a floating mesh box, a
       hull box and a closed-mesh box over three probe columns): our scene equals Valve's (226 vertices, 308
       triangles), our generation gives 75 probes, the filter drops the one inside the hull and the one inside the
       closed mesh, and the 73 left equal the probes Valve baked bit for bit (Valve's log: 73 remaining). The open
       mesh box keeps its probe in both.
     - Read, not ported: the nav coverage appender (180fbcb10) runs on every compile with a nav mesh (its convar,
       snd_steamaudio_enabe_append_probes_to_cover_navmesh, registers on). Per nav area it traces from the centre and
       corners and, for areas over (spacing x 39.37)^2, up to 10 points from tier0's global RandomFloat (1818e3290),
       keeps candidates no existing probe within the spacing can see, and clusters them (180fbb450). Porting it needs
       the global random stream's state at that point of the compile. On s2c_saprobe and s2c_sainside it appends
       nothing (73 = 73).

174. **Nav ladders in the mesh build, and AddJumps' sweep** (2026-10-28: decompile and disassembly of resourcecompiler
     0923 1818d63c0, 1818dc620, 1818e7300, 1818e69b0, 1818ea300, 1818eb970, 1818e4490, 18129b060, 1818e71b0,
     1818d2b40, 1818d2c10, 1818d1fa0; navsystem 20261015 18006b160, 180072ef0; vphysics2 0924 180065410, 180202b00,
     180201b00, 1802023d0, 1801dea00, 1801ecb30, 1801e23b0, 1801ebc00, 1801ee7b0, 1801e85a0, 1800092f0, 180128ff0,
     1801ad370, 180089a30, 18012e550 / 600 / 650, 18024a4a0, 1801c6ae0, 1801c7c70, 18003f500.)
     - Ladders (NavMeshLadders, NavWriter; docs/NAV.md "Ladders in the mesh build"): direction, the normal from a
       0.1 sphere cast (static only, group 4, nearest strictly), the bottom and top stepped by 10, length, the seven
       area links into the areas' two lists, the top raised to the top areas' closest point, and the save.
     - AddJumps' cast is a sweep of the kind's cylinder: RnHullCreateCylinder, 16 angles of pi/8 in float through
       tier0's V_cosf and V_sinf, the bottom ring point then the top, 32 points into RnHullCreate. World vfn 0x580
       sweeps its vertices (scale 1, radius 1/16, no turn) by conservative advancement (RnTimeOfImpact to 1, 32
       iterations) against each static shape that passes the query (layers 0x3021, group 4) and whose broadphase
       leaf meets the swept box: hulls, spheres and capsules whole, meshes per triangle (RnMesh_QueryBox, radius
       1/16). The call's overlap byte is 0, so any time below 1 blocks. A static body's two frames (1800092f0 and
       1801ecb30's inline copy) sum the origin as P - ((k x q) + (c + w k)), unlike 1801d64a0.
     - Exact: 18129b060 on 200000 random triangles (NavMeshLaddersOracleTests); RnHullCreateCylinder on 400
       cylinders, and V_cosf / V_sinf against CrtMath at the 16 angles (NavJumpSweepOracleTests); AddJumps from
       Valve's 026 input on probe01, cardtest, probe_classes and s2c_prefabprobe3, 5, 6 (prefabprobe3: 30606
       connections, 16849 casts, 1453 blocked); from the .vmap, stage 029 and the .nav up to the custom data on
       the 11 captured maps (NavGenPipelineTests), prefabprobe3 with its ladder (id 1, width 32, top (1712, -3219,
       202.00002), bottom 64, length 134, direction 2, areas 4011 4009 4011 3694 3713 3713 3942).
     - Argued, not measured: the mesh query box's sums can't change a verdict, since its 1/16 growth exceeds the
       touching distance (1/32 + 1/128). The +0x8ac pass (1801c6ae0) only records the query.
     - Not ported (refuses): 180202b00's types 1 and 3, compound shapes, a cylinder pointing down (180089a30's
       opposite case), the closed-mesh normal of the ladder cast (18016f710), a func_ladder in an instance or prefab.
175. **Nav links (CMapNavLink): the collector, the islands and the entity lump** (2026-10-28: decompile and
     disassembly of resourcecompiler 0923 180f203b0, 180fc7220, 180f2ddc0, 180fefbc0, 181063b90, 181064a10,
     181064ab0, 181065c90, 181065ee0, 181068680, 18106b4f0, 180fa8bf0; navsystem 20261015 1800217b0 and a load scan
     of every function reading +0xc0 / +0xc8; hammer 1023 180b33ae0; tier0 V_CompareNameWithWildcards by ctypes,
     tools/re/tier0_wildcard_oracle.py; Frida captures of s2c_navlink, tools/re/capture_navgen.py.)
     - The collector takes each CMapEntity of the root (then each sub-map's) whose classname
       V_CompareNameWithWildcards("ai_nav_link_area", name, 0) matches, case-blind (the oracle: AI_NAV_LINK_AREA
       matches with flag 0, not 1; a longer or shorter name never), and that IsA CMapNavLink. Corrected by ledger
       177: the wrapper 180fc6f40 passes mode 2 (the classname test 180f2ddc0 only; the targetname test is
       180fefbc0, mode 1) and the caller's 3 as flags (bit 1: the visibility state +0x370 must be on). Per instance
       path: start = the translation of step matrix x the node's, end = start + locatoroffset (s2c_navlink link0 at
       yaw 90 ends at (300, 1000, 272), Valve's record).
     - The .vmap attribute is `locatoroffset`, the tool attribute's lower-case name: a probe with `locatorOffset`
       (the change-tracking name in 18106b4f0) gave Valve's default (128, 0, 0) on all three links
       (%TEMP%/navgen_capture/s2c_navlink_camelkey). Factory defaults: width 128, locatorangles (0, 180, 0), type 0.
     - In navsystem only IslandRemoval reads the links (+0xc0 with +0xc8 loaded together nowhere else; the other
       hits are the 0x1b8-byte polygon array); the "no walkable seeds" skip counts seeds and markups, not links.
     - The lump: CMapNavLink is a CMapEntity (181064a10) and takes its compile_source_id in the walk; its vf
       (181065c90) adds width (a number), locatoroffset and locatorangles ("%f %f %f"), nav_link_movement and
       the reverse (empty strings not written, 180fa8bf0). s2c_navlink: entities 70 to 72 Valve's.
     - Exact: the three records (NavLinkTests.LinksAreValvesRecords), 029 from Valve's 028 (pad A kept, pad B
       removed), the link entities and every compile_source_id, and s2c_navlink's whole .nav from the .vmap
       (NavCompileTests: bytes 0..5216 and all 3292 custom data lines).
     - Not measured (refuses): links in instances, prefabs or groups, hidden or editorOnly links. The other
       attributes: ledger 176.

176. **Nav links: every CMapNavLink attribute from the .vmap** (2026-10-28: decompile of resourcecompiler 0923
     181068680 (the attribute table), 1810663e0, 181067770, 181067b60, 18106b6d0, 18106b850, 18106b350,
     18106b6b0, 18106b6c0, 18106b830, 18106b840 and the table at 18300fa90; the capture of s2c_navlink2,
     tools/coverage/nav_link_specimen.py --keys, %TEMP%/navgen_capture/s2c_navlink2_in, and its package.)
     - navlink_type is a string: its setter from a string (181067b60) compares V_stricmp_fast against each
       MapNavLinkType_t name of 18300fa90 (GENERIC 0, CLIMB_UP 1, JUMP_DOWN 2, VAULT_OVER 3, GROUND 4) and
       takes 0 for any other; the display names (Generic, Climb Up, ..., 1810663e0) are editor choices only.
       Measured: "mapnavlinktype_t::jump_down" loads as 2 (no reverse key), "Ground" as 0 (reverse key written).
     - width (float), locatorangles (qangle), nav_link_movement and nav_link_movement_reverse (strings) are
       stored as given (no clamp in their setters); nav_link_preview and nav_link_preview_reverse go to +0x1890
       and +0x1898, which neither the collector nor the export reads (s2c_navlink2 sets both, lump unchanged).
     - The export's branches, all measured: GROUND writes "__ground__" for both keys whatever the strings;
       CLIMB_UP, JUMP_DOWN and VAULT_OVER write only nav_link_movement (none when empty); GENERIC writes both,
       each only when not empty; width 0 is written as 0.
     - Exact on s2c_navlink2 (six links): the records, 029, the six link entities and every compile_source_id,
       and the whole .nav from the .vmap (40 areas, bytes 0..5216 and all 3292 custom data lines).
     - A key in another DMX type than the above refuses (MapEntities.NavLinkFields).

177. **The world's entity list after the bake, and nav links in instances** (2026-10-28: decompile of
     resourcecompiler 0923 180fc6f40, 180fc7220, 180f2ddc0, 180fefbc0, 180fca3d0, 180fca010, 180fcb150, 180f60740,
     180f5ff40, 180f6d410, 180f684c0, 181017820, 180f7a350, 180f8f920, 181064ab0, 181065610, 181065720, 181064300
     and the locator vtable 182362db0; Frida on the 1023 build, tools/re/capture_entitylist.py: every add to and
     remove from the list (180fcb1d0, 180fcc310) with the bake as markers, and the list at the collector
     (180f21570); captures of s2c_navlink3 and s2c_navlink4, tools/coverage/nav_link_nest_probe.py.)
     - The search (180fc7220) walks the CMapWorld's entity list (+0x1828 count, +0x1830 array) in stored order.
       The collector's call (180fc6f40) is mode 2, the classname, with flags 3: a node whose visibility state
       (+0x370, off for force_hidden, editorOnly or a visibility manager hide, inherited) is off is skipped
       (s2c_navlink3's hidden and editorOnly links are in the list and not in the records).
     - The list: at load the world (180fca3d0), then each CMapEntityIONode in tree preorder as it joins
       (180fca010): every CMapGameDataNode, CMapInstance and CMapPrefab (the IsA chains); not groups, meshes or
       the navlink locators. A prefab's map fills its own world's list. In the bake each collapse appends its
       copy's listed nodes in preorder; when no other instance names the target (180f684c0) the target's subtree
       is removed in postorder, then the instance; a prefab appends its map's nodes in preorder, then leaves.
       Removal is FastRemove (180fcb150): the last entry moves into the slot. Read off s2c_navlink3's 203 edits;
       MapEntityList simulates it, and the records' order on s2c_navlink4 (11 links, copies interleaved by the
       swaps) is Valve's.
     - A CMapNavLink's two CMapNavLinkLocators (181064ab0, at load, from the node factory) take ids: the load's
       ids run two past each link and a copy of a link takes three slots (MapInstances.ClonedLocators,
       SmartProps.NodesCreatedOnLoad).
     - A copy: the link moves by TransformBy (181001820), its locators by CMapTransform's (vf 0xc8 181076410, the
       step as a CTransform, SettleWorld.UnderDeformerStep); the locators' handler (181065720) gives the link
       locator 0's origin and angles and sets locatoroffset and locatorangles to locator 1 minus locator 0
       (Valve's yaw 90.0000076 for a copy turned 90, which the matrix path gives as 90). SetScales (181065610)
       keeps a link at scale 1: an instance's scale moves neither its position nor its offset.
     - Exact on s2c_navlink4: the 11 records (two instances of one target, a nested copy through a turned
       instance, a plain group), 029, the whole lump (every copy's id, origin, angles, locatoroffset,
       locatorangles) and the whole .nav from the .vmap.
     - Refused: a hidden instance or prefab, a prefab whose map has instances or prefabs, a prefab inside a
       target, a target deleted with another, a hidden link inside an instance or prefab, a link under a group
       that moves. Links in prefabs: ledger 178.

178. **Nav links in a prefab** (2026-10-28: the prefab collapse as ledger 133 reads it, 180f96d40, 181019d00,
     180fc1bf0, 180f834d0, with the locators' TransformBy 181076410 and handler 181065720 of ledger 177; the
     s2c_navlink3 capture.)
     - A prefab's map nodes are reparented, not cloned, and moved by the group setter's step (180f834d0) from the
       origin to the prefab's placement, each node by its own TransformBy: the link's locators by CMapTransform's
       (the step as a CTransform), so the link ends at locator 0, as in an instance (NavLinkFields.Copied with
       the prefab chain; MapEntities places a prefab map's link the same way for the lump).
     - At yaw 90 the CTransform path and the matrix path part by ulps: Valve's start (-259.99994, 1000.00006) is
       locator 0's, the matrix gives (-260, 1000). Ledger 177's quaternion observation was this, not a separate
       clone transform (181102620, which writes lightmapscalebias, is not the collapse ledger 133 reads).
     - Exact on s2c_navlink3 (14 records, three of them a prefab's at yaw 90): the records, 029, the whole lump
       and the whole .nav from the .vmap.

179. **The walkable seeds through the entity list, and prefabs whose map has instances** (2026-10-28: decompile of
     resourcecompiler 0923 180f21760, 180f60740, 180f60af0, 180f57ff0, 180f86e70, 180feb950 and the CMapInstance
     vtable 182349b48 slots 0x850 to 0x870; Frida on the 1023 build, tools/re/capture_entitylist.py, which now records
     each entry's origin +0xa0; captures of s2c_prefabprobe5 and s2c_prefabprobe3.)
     - The seed collector is the link collector's search (180fc6f40 for "point_nav_walkable", mode 2, flags 3) with
       no IsA test; per instance path the translation of step matrix x the node's vf 0xa0 matrix. So the seeds come
       in the entity list's order after the bake, not the lump's walk (the "unsettled" note of NavGenMapInput.Seeds).
     - A bake round (180f60740): terrain features (180f60af0, CMapTerrainFeature only), tile meshes (180f5f490), then
       the CMapInstance-typed nodes (CMapPrefab included) collected once by 180f86e70 with flags 0xd: children before
       the node, sub-worlds skipped, a node with instance referrers (180f8ba70) skipped with its subtree. The filter's
       vf 0x868 and 0x870 are 1800de990 (return 0), so it drops only non-instances and never reorders. A prefab's map
       moves in during the round and its instances wait for the next round's collection.
     - A prefab map's instances and targets collapse as the root's do, each named within its own tree (two prefabs
       of one map stay apart), and a prefab map's hidden ids are its own document's.
     - Exact: s2c_prefabprobe5's whole list (145 entries, one instance in its prefab's map: each entry's class and
       origin, MapEntityListTests; prop_physics skipped, the settle moves them). The seed records
       (navinput_seeds.bin) bit for bit and in order on all 16 nav captures (NavSeedTests).
     - The first reading of s2c_prefabprobe3 (Valve's list up to entry 2463 and not after) is settled by ledger 180.
     - Still refused: a point_nav_walkable under a deformer, and ledger 177's other refusals (prefabs: ledger 180).

180. **A prefab map's instance that joins the world ahead of its target names a clone** (2026-10-29: decompile of
     resourcecompiler 0923 1810158a0, 181019590, 1810197f0, 181018fa0, 180f8f0d0, 180f8ba70, 180f88920,
     181017820 (its end), 180fc5010, 180f75010, 180fca010, 180fcb150; tools/re/capture_entitylist_trace.py on the
     1023 build: every list edit with its world, node, slot and counts, the root list around every collapse, the
     target referrer counts and the target setter's calls with a backtrace; s2c_prefabprobe3, 4, 5, 7, 9 and
     s2c_deformpath3, 4.)
     - The prefab collapse reparents the map's nodes (180f75010): each leaves the sub-world's list and joins the
       root's in preorder (180fc5010: OnNodeAdded, then vf 0x228, then the children). A CMapInstance joining
       (vf69 1810158a0) resolves its target from the element handle (+0x558) in 181019590: a target in another
       world (here the prefab map's, not yet moved) with two or more referrers is cloned (180f86a90) into the
       instance's world (180f5f230) and the instance names the clone; a target already in its world is named as it
       is. Measured: s2c_prefabprobe3's instance 4928, ahead of its target 4888 in the map's preorder, gets clone
       5496 (and its entity 5497, listed right after the instance); the 79 behind it name 4888.
     - The referrer count (180f8ba70) is cached on the target (+0x388 / +0x38c) and reset only by an instance's
       SetTarget / ClearTarget (180f8f0d0). CMapInstance_Collapse deletes the target when 180f88920 finds exactly
       one referrer: a clone leaves with its instance's collapse (Valve's slot 2464 on s2c_prefabprobe3).
     - In a bake round a prefab map's prefabs collapse in the next round as the root's do (s2c_prefabprobe7, 9).
     - Exact: the whole list after the bake on s2c_prefabprobe3 (2976 entries), 4 (5102, atixref as a prefab), 5,
       7 (nested prefabs), 9 and s2c_deformpath3, 4 (MapEntityListTests; nodes a deformer bends are checked by type),
       and s2c_prefabprobe4's six seed records (NavSeedTests).
     - Refused: a prefab map's instance ahead of a target with fewer than two other referrers (181019590 moves the
       target there, and whether the two are one document is not read), a cloned target holding instances or
       prefabs, and a copied prefab whose map has instances (Valve's compiler crashes on s2c_prefabprobe10).

181. **Hidden instances in the bake, and which hiddenFlags hide** (2026-10-29: decompile of resourcecompiler 0923
     180f5ff40, 180f6d410, 180f684c0, 180f6ffc0, 1810eeba0, 1810f2750, 1810ecf10; capture_entitylist_trace.py
     --bake-only on ze_ffvii_mako_reactor_v6_p and c2m3_coaster_d_d.)
     - A node is hidden by the visibility manager when hiddenFlags & 0xb & ~manager+0x508 is not zero
       (VisibilityMgr_IsNodeVisible passes 0xb to 1810eeba0; +0x508 is only set to 0, in CVisibilityMgr::vf10). Flag 4
       alone does not hide: c2m3_coaster_d_d's four prefabs with flag 4 collapse in Valve's bake. MapEntities.HiddenNodes
       tested any non-zero flag; it now tests & 0xb. On disk flag 4 is on those four prefabs, one prefab of
       dkr_m1_motel_d_c2m2 and mako's prop_physics_override 1305 (mako's 70 lumps unchanged: 0 differences).
     - CollapseInstance deletes an instance whose visibility state is off (180f6d410 with flags 3): it leaves the
       list (FastRemove), then UnregisterSubtree deletes its target when no other instance names it, the target's
       listed nodes in postorder. Measured on mako's four hidden instances: the first three leave alone (their target
       still named), the last takes its target's two nodes with it, and Valve's slots (2096, 2097, 2100 hold 33734,
       33733, 33735) are those of instance-first, then the target's nodes.
     - Exact: ze_ffvii_mako_reactor_v6_p's whole list after the bake (5753 entries) and c2m3_coaster_d_d's (1884)
       (MapEntityListTests).
     - Refused: a hidden CMapPrefab (none on disk once flag 4 is read right; ledger 183).
182. **CS2 1.41.9.3's VData strip rule** (2026-10-29: disassembly of csgo modtools 20261023 and 20261029 at
     18002e1ef; resourcecompiler 1.41.9.3's own compile of the RcReference probes through tools/rc-oracle.ps1.)
     - CModTools_VDataStripRules (18002e190, unmoved) builds the same one-rule list, now
       {"scripts/weapons.vdata", 1, "weapon_lar"} (was weapon_m14). Its fingerprint through 18015bc50's hash is
       55841844 (was 2278732723), the "VData ModTools Stripping Version" RC 1.41.9.3 records in rc_probe.vdata_c
       and rc_probe_ints.vdata_c: those differ from their 1.41.9.2 compiles in those 4 bytes only.
     - Source2ContainerAuthor.VDataModToolsFingerprint uses the new rule; RcReference re-baked from the 10-29
       compile (the .vsndevts_c and .vpcf_c references were from 09-07, their RED2 now RC's current one).
     - Every .vdata_c and map root with a .vdata child (baked_bomb_damage) compiled by Valve before 10-29 holds the
       old value: refresh such references before comparing.
183. **Hidden prefabs, prefabs inside instance targets, and a target moved ahead** (2026-10-29: decompile of
     resourcecompiler 0923 180f5ff40, 180f96d40, 181018a30, 181019590 and the CMapPrefab vftable 18234a700;
     capture_entitylist_trace.py --bake-only on the 1.41.9.3 build (its addresses now mapped through rva_map: the build
     moved them all by 0x40) on s2c_hidepf, s2c_pfintarget and s2c_aheadprobe, written by
     tools/entities/entitylist_refusal_probes.py.)
     - A hidden prefab (force_hidden or editorOnly; with or without instances in its map) is deleted as a hidden
       instance is (MapDoc_CollapseInstance, +0x370 off: 180f6d410 with flags 3): it leaves the list alone and its
       map's nodes never join. Measured: s2c_hidepf's three hidden prefabs each take the count down by one.
     - CMapPrefab's vf 0x860 is CMapPrefab_Collapse, vf 0x868 1800de990 (false): a prefab whose collapse makes no
       group (no loaded map world, 181018a30's +0x550) is deleted. A prefab never in the world tree (a target group
       kept outside it) has no map loaded and so leaves alone; Hammer keeps targets in the world, so the probe has its
       groups there.
     - A prefab inside an instance target collapses in the round after the instance, from the copy: its map joins
       like any prefab's (s2c_pfintarget, 238 entries). Its placement is the instance's step, then the prefab's
       placed by it: SettleWorld.Step over the chain in nesting order (MapEntityList.Item.Chain), which the two
       separate lists (every prefab before every instance) could not give (off by 256 x sqrt 2 at yaw 90).
     - A prefab map's instance that joins the world ahead of its target, with fewer than two other referrers
       (MapNode_ReferrerCount after the instance dropped its own link), moves the target itself into the instance's
       world (the world's vf 0x1c8): the target's listed nodes join right after the instance, under the same pointer,
       and are not added again where the walk meets them. Measured: s2c_aheadprobe with 0 and 1 other referrers;
       ledger 180's 79 others clone.
     - Exact: whole lists of s2c_hidepf (168), s2c_pfintarget (238) and s2c_aheadprobe (165), and every earlier
       MapEntityListTests map unchanged; NavSeed and NavLink tests unchanged.
     - Refused: a target holding instances or prefabs that such an instance clones or moves, a copied prefab whose map
       has instances (Valve crashes), a target deleted with another. Not checked: the moved target's place among the
       world's children (only its list entries and removal depend on it), and the entity lump of a prefab inside a
       target (EntityLumpSet takes its own copy chains).

184. **An unbaked world's lightmap block, and light_environment's fades with no key** (2026-10-29: RenderDoc dump of
     atixref's cube 0 face 0 (%TEMP%/cubehost_run/atixref/replay_face0, 1.41.9.2) read with
     CubemapFaceReplayTests.BlocksOfTheCapture; decompile of worldrenderer 1023 180015740.) MEASURED, writers not read.
     - atixref's world has m_bHasLightmaps false and no lightmaps. worldrenderer's loader (180015740) returns before it
       builds BakedLightingConstantBuffer_t (a 0x34-byte buffer through the render system's vfn 0x218; the only such
       creation in worldrenderer), yet the game bound one: uv scale (1, 1), size 256 x 256, live bindless slots at
       +16. Its writer is not found (no 0x34 creation in scenesystem, engine2, materialsystem2 or client; no "default
       lightmap" string). CubemapFaceBlocks takes 256 for an unbaked world (UnbakedLightmapSize).
     - Words past +0x34 are junk in both maps (0x3e3e3e3e on probe01), and probe01's faces are exact with ours at 0:
       the programs do not read them.
     - csgo.fgd removes cascadecrossfade and cascadedistancefade from light_environment (remove_key), so a Hammer-made
       CS2 map carries neither; probe01 has them only because its .vmap was written by hand. With both absent the
       game's light block holds 1 / crossfade = 10 and the same distance-fade words as probe01's 0.05: 0.1 and 0.05,
       which equal lights_base.fgd's defaults. Where the game takes them from (the schema default) is not read.

185. **An unbaked light probe volume's record: the measured parts** (2026-10-29: cubehost on atixref with
     tools/cubehost/lpv_records.js, records of all 44 probe volume objects; client 1.41.9.3 1802819e0 disassembled;
     atixref face 0's RenderDoc dump.) The code is read (CUBEMAPS.md A.1); three inputs are measured, not read:
     - The engine's default probe texture (rendersystem vfn 0x198, type 6) is 2 x 2 x 2 R8G8B8A8_SRGB (viewed UNORM)
       of zeros: the capture's 3D Image 1330, 32 bytes all 0, which every unbaked record and default slot indexes.
     - The entity data's +0x70 .. +0x78, copied to record +0x50, is (1, 1, 1) on all 42 of atixref's volumes.
     - Record +0x5c, 1 only when +0x69 is set and +0x6a is not (the SDF path), is 0 on all 42.
     None of the three is mapped to a key or a schema field; a map whose values differ would need that first.

186. **tier0's global random stream, and the nav coverage appender's randomness** (2026-10-30: disassembly of tier0
     20261023 18015d160, 18015d220, 18015e1b0, 18015e780, 1800057e0, 18017bd20, 18017c72e; resourcecompiler 0923
     180fbcb10 decompiled, 20261029 180fbdd10 / 1818e5f60 (ported with port_addr.py); Frida captures of 20261029,
     tools/steamaudio/capture_randomstream.py, on s2c_saprobe twice and s2c_sanavcover three times.)
     - Settled from the code: the generator (GenerateRandomNumber_Locked, 18015e1b0) is Numerical Recipes' ran1:
       Park-Miller (16807, 2147483647, Schrage 127773 / 2836) with a 32-entry Bays-Durham shuffle, warmed with 8
       extra steps when idum <= 0 or iy is 0. RandomFloat (18015d220) is (float)draw x a constant, min'd with a
       second constant, times (hi - lo) plus lo. The global stream is at tier0 +0x3cc328 (idum, iy, iv[32]).
     - Settled from the code: every seed tier0 gives the global stream is time-based. Its static initialiser
       (1800057e0) mixes QueryPerformanceCounter, a QPC-derived double and another value; 18017bd20 and 18017c72e
       seed elapsed milliseconds. RandomSeed (18015d160) writes idum = -|seed|, iy = 0 inline, not through SetSeed.
     - Measured: the stream differs from its first draw between two compiles of s2c_saprobe, and the number of
       draws before the appender differs too (3267 / 3268: other users of the global stream). So Valve's appender
       is not reproducible run to run; it falls under the variance rule (HANDOFF, "What passing means").
     - Measured: the appender's draws are contiguous on its own thread, three per CNavArea_RandomPoint (52 calls on
       s2c_saprobe, 31 on s2c_sanavcover). Reseeding the global stream at its entry (RandomSeed(1)) made two
       compiles' draws equal (93 of 93); RandomSeed(2) changed them.
     - Measured: s2c_saprobe's 73 automatic probes all sit within 2 units of one plane (59-61) and its 42 areas at
       floor level, so every candidate is seen (appends 0). s2c_sanavcover (tools/coverage/steamaudio_navcover_
       specimen.py: a closed room over the floor plus a nav seed in it; IslandRemoval drops an unseeded island)
       appends 2, at (98.5, -439.5, 211.06) and (936.17, -312.0, 211.06), and the SAME 2 under seeds 1 and 2: its
       random points never decide the result there. The appender's non-random half (centre, corners, the
       sight test, clustering) can be checked on it; a specimen where a random point survives is still needed.

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
  0.0). Above 0 the piece is welded at 1/32 and simplified (1800c5b90).
  None of the 227 installed maps sets the override. Ported 2026-10-23 with
  the entity and pre-settle paths: item 124.

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
