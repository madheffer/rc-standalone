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
2. **Painted convex_multi pieces**: each layer's mesh is grouped by
   connectivity as an unsplit piece is; physicsbuilder's multi path is not
   read yet. (convex_single is settled below.)
3. **KV3 encodings per resource type.** Most block writers save with
   binary_auto (settled below), but some call sites pass plain "binary" or
   "binary_bc". Map each writer to the resource types it serves.
4. **Face order around a vertex** in the brush hull neighbour set: triangle
   order stands in (it only matters on a hash collision).
5. **Instance path for brush entity meshes**: applied after the node's own
   move, or composed first. Both place atixref the same; not read.
6. **Subdivision bake remainder**: the face collapse is modelled from the
   edge collapses (confirmed by exact output, not read line by line), and
   the wrapper's vertex merge (tolerance about 1e-6) and edge smoothing are
   not ported. tools/physics/capture_bake.py logs every operation to settle
   both when a map needs them.
7. **Collision names registered by the game** (csgo_*) keep the model's
   spelling: assumed, no counter-example.
9. **Texcoord transform defaults** for parameters a material leaves out
   (UV set 0, scale 1, rotation 0).
10. **Hammer's re-projected texcoords and normals** (about 1e-5 off on some
    faces). They do not change a hull, but the source is not read.
12. **VPK v2 layout** (tree order, chunk hashes, signature section): byte
    exact on four packages, the packer not read.

21. **Case folding.** tier0 folds A-Z only (ToLowerFast, stristr,
    stricmp_fast); the port uses .NET's wider folding in about 160 places.
    Identical on ASCII text; audit each against the Valve call it mirrors.
    The string token hash already uses the ASCII rule.

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
25. **The .rte flag word** from the mesh entry and material attributes: read
    in outline (GEOMETRY.md), not ported.

26. **Prefab placement**: a node in a prefab's map is placed as instance
    collapse moves a node (SettleWorld.PrefabPlacement: the prefab's own
    placement as the move, the node's origin through it and its angles
    rebuilt); a quaternion or plain matrix path each leave spheres a few
    ulps off. Measured whole-file exact on s2c_prefabprobe; the prefab
    loader (Hammer's map document, +0x6b8 docs) is not read.

### Map resources
13. The angles of instance copies inside a world layer (a round trip off).
15. What picks Zstd or LZ4 for map output.

### Other modules
16. Visibility: the compile's normalise guard (NormaliseSlowPath) against our
    double normalise in three places; no specimen reaches it.
17. RTE: what the per-triangle id hashes.
29. RTE triangle order: within a mesh the file groups triangles by material,
    in the order of the mesh's materials array on 66 of atixref's 68
    multi-material meshes; the rest are the world renderer's own mesh split
    (measured, not ported: the order follows the render meshes). Whether
    visibility sees the order is measured only on probe01 (three compiles,
    three orders, one output).
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
    box (LightRayProbe, LIGHTRAY_SWEEP).
32. **Instance copies' undeclared keys** ship in reverse template order:
    measured on atixref's 186 copied lights; the key list's head insertion
    (FUN_180ce08f0) is read, the copy loop that walks the template is not.
33. **Static props' ray triangles**: the light scene takes a prop mesh's
    trace data (meshsystem CMeshRayTrace, mesh +0x200); we build it from
    the render vertex and index buffers, draw calls then indices, which
    makes every atixref barn light exact. The code that fills the trace
    data (TraceDataForDraw_t, TraceVertex_t) is not read.
30. The outside seed traces the .rte's own kd tree as the file wrote it
    (RayTraceEnvironment.Trace, leaves' filed triangles over the whole ray):
    measured, every specimen exact that way, while the loader's rebuilt
    tracer tree changes the verdicts (RebuiltTreeProbe, RTREE=<map>). Which
    structure visbuilder's seed trace reads is not read in the 09-23 build.
18. vrad3 trace cost law (performance only; not output).
19. Legacy-GUID particles in game (needs the user's go-ahead for a game run).
20. The vmat_c field order note ("likely NTRO positional"): the order is
    measured; the loader's reason is not read.

## Resolved

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
