# Entity lump

`maps/<map>/entities/*.vents_c`: every entity, key and I/O connection the
compile writes. The port is `Maps/EntityLumpAuthor*.cs`, `EntityLumpSet.cs`,
`MapEntities.cs`, `MapInstances.cs`, `MapPrefabs.cs` and `FgdSchema.cs`. The
settle that moves physics props is in [SETTLE.md](SETTLE.md).

## Status

- **Pinned maps:** every lump of the pinned maps (atixref, ze_hold_em_p,
  Mako, cardtest, probe01, ze_doom_p2's gameplay, c2m2's gameplay,
  probe_classes, probe_cable) and of the 13 prefab, instance and deformer
  probes matches Valve's with lights and baked keys included; the only
  allowances are Valve's own run-to-run settle poses (below) and vis
  clusters when the test runs no vis.
- **The comparison is strict:** types to the array element, KV3 flags, key
  and entity order, every connection field.
- **Coverage:** all 354 classes Hammer offers are measured through the probe
  map (every class bare and with every key set), and all 354 are exact,
  their outputs outside the lump included: info_particle_system's .vsnap from
  mesh, path and cable nodes (ledger 241) and func_movelinear's movable nav mesh
  (ledger 242).
  [COVERAGE.md](COVERAGE.md) has the list.
- **The settle** runs in the lump build, and its output is exact, except
  where Valve's own output varies between compiles: a stack of props whose
  result depends on the order bodies turn dynamic, which follows heap
  addresses (GROUND_TRUTH 52). Those props are a documented gap, listed by
  map in the lump test (`UnstableSettle`): coaster, dkr_m4_ferris,
  dkr_m2_carnival, dkr_m5_stadium and dkr_m3_tunneloflove, each measured against two compiles
  or Valve's captured order.
- **Prefab corpus against fresh references (2026-10-23):** the 14 corpus
  maps with prefabs, recompiled by Valve that day (`%TEMP%/fresh_corpus`),
  differ only in settle poses Valve itself varies between compiles. The
  last light keys (c2m2 3:2184, coaster 3:1209) closed with the trace
  scene's inverse: its translation is summed (x + y) + z per row
  (18125ade0), which a prop tilted on every axis showed (every ray of both
  lights now matches `capture_lightrays.py` bit for bit).
- **Corpus sweep (2026-10-06):** all 80 compiled corpus maps are within
  the documented gaps (dkr_onelevel's 38,633 settle bodies exact, its 882
  props exact in Valve's captured order).

Light keys (done):
- **Light keys:** the precomputed shape keys and, when baked, the shadow
  slot keys and probe and cubemap keys are in the lump, exact on every
  pinned map:
  - **ze_hold_em_p (baked):** every light's shape keys are exact, and so
    is `precomputed_vis_clusters` on all 37 (value and place, last of the
    values; `LUMP_VIS=1` runs vis for it): the whole lump has no
    difference. A baked compile writes it (WRB_PrecomputeLightVisMembership)
    for barn, rect and omni2 lights of direct light mode 3: the light is
    sampled as for its shape keys, and every vis cluster with a box
    overlapping the light's bounds and reached by one of its rays
    (`LightVisClusters`) is listed, ascending. omni2's is not measured.
  - **light_rect** (2026-10-04): FUN_180eea510's rect branch is an omni
    record at outer angle 90 and inner 0 with a rect (shape 0) or disc
    (shape 1) luminaire of size_params' x and y; probe_classes' rect is
    exact in every precomputed key.
  - **Brightness units** (Light_SyncBrightnessUnits 180eee150, every
    barn, rect and omni2): the record built with flags 2 (local, an omni's
    shape 3 a sphere, a barn's luminaire 0 a disc) gives a scale, a
    factor, a luminaire area and a solid angle (`LightUnits`); the
    intensity comes from the unit brightness_units names and the other
    units are written back through "%g": brightness (not for units 0),
    lumens rounded (not 1), nits with an area (not 2), candelas with a
    solid angle (not 4), legacy (not 3). An invalid unit gives intensity
    0. Every barn, rect and omni2 of the lump specimens is exact.
  - **atixref and Mako:** every light exact, key order included (2026-10-22
    run: 0 differences on both). The one-face omni2 misses (7306, 7318,
    7348) closed with the packet-of-4 ray tracer and the kd leaf rule
    (GROUND_TRUTH 31, 2026-10-09).
  - **precomputed_vis_clusters** goes only on lights still directlight 3
    after the shadow slot step (ledger 122); s2c_prefabprobe4 is exact
    against Valve's captured cluster boxes (`LUMP_FLATBOXES=<capture>`).
  - **The lump test allows no light or baked-key difference** since
    2026-10-22. The only allowance left is `precomputed_vis_clusters`
    missing on our side when the test does not run vis; `LUMP_VIS=1` runs
    it and checks the key value and place.
- **Prefabs (done 2026-10-23):** the three layouts once refused are measured
  on Valve compiles of probes 7 to 9 (`tools/entities/prefab_nest_probes.py`)
  and exact: a prefab inside a prefab, a prefab's instances beside a nested
  instance of the map's own, and fixup prefabs inside fixup prefabs with
  either prefix kind. Probes 11 and 12 place one map holding stationary
  lights three times (apart, and at one spot so the shadow slots run out).

Open: nothing measured. Read from output only: the order of the loaded
prefab documents in the light gather (and the first placement's reversed
lights), and the node copy behind the key flip.

## Rules

What goes in:
- **Walk order:** entities follow the world's child tree, depth first, not
  file order. `compile_source_id` is the ordinal in that walk and counts
  every node owning game keys: `CMapEntity`, `CMapPath`, `CMapPathNode`
  and `CMapWorld`.
- **Not shipped:**
  - `prop_static` (FGD `static_prop`) and `editor_only` classes;
  - a `@SolidClass` with no brushes;
  - nodes hidden in `CVisibilityMgr` (walked and numbered, then dropped);
  - consumed classes: `visibility_hint`, `info_cull_triangles`,
    `point_scale_reference_human`, `env_world_lighting`,
    `light_irradvolume`, `func_deformable_density`;
  - an entity with no class.
- **Instances:**
  - `CMapInstance` targets a group elsewhere. Each placement ships a copy of
    the group's entities; the template itself never ships.
  - A copy carries the template's `compile_source_id`.
  - Copies are written where the instance's PARENT subtree ends.
  - A copy's keys: classname and targetname, the class's keys in finalised
    order with the template's values, then the template's undeclared keys in
    REVERSE template order. A node's keys are a list that grows at its head
    (`FUN_180ce08f0`), so walking one from the head and adding each reverses
    them (atixref's 186 copied lights, ledger 32).
  - A copy's node ids are allocated past the map's highest node id and past
    the smart-prop locator nodes the loader made. Each collapse takes a
    block the width of its group plus one.
  - An instance inside a target group is reached through its group's copies
    and numbered after every top-level block.
  - Copies are placed by the bake collapse (`SettleWorld.BakedPlacement`).
- **Prefabs (`CMapPrefab`):**
  - The prefab's map is loaded as its own world and walked in place,
    placed as instance collapse moves a node.
  - Id paths look like `108:3`: that is `hammerUniqueId`, and brush models
    are named `<name>_108_3.vmdl`.
  - One extra `compile_source_id` is numbered before the prefab's contents.
    A prefab map is one source however often it is placed: the first
    placement in the walk numbers its nodes and every later one, nested or
    not, reuses those numbers (s2c_prefabprobe9: three placements of
    s2c_fixmid all carry 18 to 101).
  - A repeated placement's keys: CMapEntity_ExportToLump (181004020)
    writes the table in its stored order and leaves it alone, so the flip
    comes from copying the node. Every second placement of a map ships its
    source keys in table order with classname still last, and the keys the
    preprocess writes (lights, probe volumes) first, as always
    (`Entity.KeysForward`): what a copy that sets the class first and adds
    every other key at the head of its table gives, each placement copied
    from the one before. The copy itself is not read.
  - Prefabs inside prefabs ship by the whole id path (`108:2140:4`).
  - `fixupEntityNames`: the prefix is the prefab's targetName when
    useTargetNameAsPrefix is on and it has one, else its node id, then `_`;
    nested fixup prefabs append theirs to the outer prefix (`112_2156_`,
    `outerpre_tnpre_`), and a prefab without fixup keeps its parent's.
  - A prefab with `loadAtRuntime` is skipped.
  - The instances inside a prefab's map (GROUND_TRUTH 46, captured): the
    prefab collapses first, its block its map's copy count (every node,
    plus a target group's subtree again for an instance that precedes its
    target) plus its loader's nodes plus one; then those instances in tree
    order, one inside a target group after every first-round block. Their
    copies ship with plain ids.
  - The rounds, read in MapDoc_BakeInstances (180f60740) and
    CMapInstance_Collapse (181017820): until nothing collapses, each round
    walks the tree in preorder and collapses every instance-typed node it
    holds, prefabs included; a copy (or a prefab's map) is added as the
    last child of the node's parent, so later rounds find what it holds
    there. `MapInstances.Expand` simulates exactly that (`BakeNode`).
    Checked on s2c_prefabprobe3 to 9 and 11 and the pinned maps.
  - Lump order: a copy is written after its parent's own subtree and after
    the maps of the parent's prefabs collapsed before it; copies sharing a
    place follow the final tree (a nested copy inside its outer one).
  - Lights are gathered for the shadow slots as MapDoc_GatherLights
    (180f1c660) does: the world's own through every placement, then each
    loaded prefab document of the +0x6b8 list (18100c780, no recursion).
    Measured on s2c_prefabprobe7, 9, 11 and 12 (12: nine stationary omni2
    at one spot for one free slot): documents in first-placement order,
    breadth first; a document's placements breadth first, each placement's
    lights in walk order, except the document's first placement, whose
    lights come last to first. The list's order and that reversal are not
    read in the binary.
  - A prefab inside an instanced group (s2c_prefabprobe10) crashes Valve's
    compiler in the light preprocess, so it has no reference and is out of
    scope.
- **World layers:** each `CMapWorldLayer` is its own lump,
  `world_layer_<name>.vents_c`. Numbering stays global.
  `info_world_layer` gets `world_layer_` prefixed and `worldname` last.
- **Template lumps:** each `create_entity_template_lumps` class
  (point_template) gets `<nodeid>#entitylumpname.vents_c`:
  - For Template01..128, it copies every entity whose name matches, in list
    order. The wildcards sit in the entity's name, not the template's
    string.
  - Copies are placed in the template's space through matrices, so yaw 270
    reads back as pitch -0, yaw -90.
  - Names get `&0000` unless spawnflag 2 is set, and every string value
    equal to an old name is rewritten.
  - The template gets `worldName` (backslashes), `entityLumpName` and
    `TemplateFixup`.
  - Originals are removed unless spawnflag 1 is set. A hidden member still
    ships in the template lump.
  - default_ents lists child lumps: layers first, then templates.

Keys:
- **Order:** the source's keys in source order, plus every class key the
  source lacks at its FGD default, in finalised order. The node writes that
  table BACKWARDS.
  - Then come the game keys, `compile_source_id`, `origin`, `angles`,
    `scales` and `hammerUniqueId`, then path keys, then the brush model.
  - worldspawn writes `compile_source_id` first.
  - A vmap older than 38 gets `prefab_has_runtime_entity_by_default "0"`.
- **The FGD** (`FgdSchema`): finalised as RC does it:
  - bases in order, then the class's own keys;
  - a redeclared key keeps its first place;
  - `remove_key` removes a key;
  - `@OverrideClass` merges; `@exclude`d classes have no schema, so they ship
    the source's keys as plain strings.
  - Defaults: flags are the OR of on-bits, `tag_list` values are joined with
    ",", and a key named `name` gets no default.
- **Typing** (RC's own table, `EntityKey_TypeValue`):

  | type | ships as |
  |---|---|
  | `integer`, `int`, `intchoices` | V_atoi prefix |
  | `node_id` | strict int32 |
  | `flags` | strict uint32 |
  | `float`, `floatchoices` | strict float widened to double |
  | `boolean` | "true" (any case), or atoi != 0 |
  | `vector`, `angle` | three floats until one fails |
  | `vector2d` | as sscanf reads it |
  | `vector4d` | four floats |
  | `color255` | three UInt32, four when alpha is not 255 |
  | resource types | normalised lowercase path with the kind's extension, resource-flagged |
  | `kv3` | not written |
  | anything else, choices included | text |

  Integers narrow as KV3 does (0 and 1 as singletons, then Int32). Flags are
  UInt32 except 0 and 1. Empty values ship (as `""` or zeros). Spawnflags
  are masked at SetClass.
- **Name fixup** `[PR#]`: applied to each key typed as one of four name
  kinds (fixup type mask 0x830006), unless the value is empty, starts with
  `! * ? @`, or is a class name. `targetname` is always fixed up.
  - Connection targets are prefixed unless they start with `!`.
  - A parameter is prefixed when it names one of the map's entities.
- **Connections** go through `EntityIOConnectionData_t`'s schema:
  - the target type is UInt32 7;
  - the delay is a float widened to double;
  - the fire count is Int32;
  - an empty parameter map is written as null.
- **Brush entities** point at `maps/<map>/entities/<lowercased name or
  unnamed>_<node id>.vmdl` (PHYSICS.md builds them).
- **Paths and cables:** nodes serialise into the path as KV3 text strings.
  - `pathNodes` holds nine floats per node: position, then the in and out
    handles by tangent type, computed as direction times length.
  - The optional keys are `pathNodePinsEnabled`, `pathNodeRadiusScales` with
    their height pair, `pathNodeNames` and `closed_loop`.
  - A list of four or fewer entries fits on one line; longer lists wrap.
  - A cable_dynamic's `rendercolor` is its node tint formatted as
    `"%i %i %i"`.
- **Particle snapshot** paths come from `snapshot_mesh`.

## Light keys

`Lighting.For` builds each light's bake-related keys, at the end of
`BuildEntity`:
- **Handshake:** MurmurHash2Lower of the backslashed map path, seed
  0x3501a674, counted over non-hidden export order.
- **Probe grid and bakeresource paths:** the paths are joined by id path
  with `_`.
- **Probe atlas** (`ProbeAtlas`):
  - Valve's `CUtl3DAllocator`: buckets by `floor(log2 volume)`, best fit
    `max(1,|dw|)*max(1,|dh|)*max(1,|dd|)`, split x, then y, then z.
  - Boxes are sorted by z, y, x descending with MSVC qsort.
  - The cells per axis are `(size + 5) & ~3`.
  - It is packed when baked lighting is on, or always in an entities-only
    build.
- **The bake flag** in an entities-only build is whether
  `maps/<map>/lightmaps/{irradiance,direct_light_shadows}.vtex_c` exists.
- **Baked shadow slots** (`BakedShadowAssignment`):
  - Directional lights take slots 0 to 2.
  - Overlap is a bounding-sphere test, then box SAT or box-sphere, then GJK.
  - Lights are sorted by assigned neighbours, then neighbours, and take the
    lowest free slot of 4.
  - `light_path_uniqueid` = `NodeIdPath_Hash`; `light_map_uniqueid` =
    MurmurHash2Lower(`maps/x.vmap`).
- **Precomputed keys** (`LightPrecompute`):
  - 24,576 Halton samples (bases 2, 3, 5, 7) per light volume.
  - The light's shape and falloff are evaluated at each sample, and the
    survivors are traced.
  - Bounds, an OBB and subfrusta (six for omni) are fitted to what the rays
    reach.
  - Exact against an empty scene, in process.
- **The traced scene** is Hammer's editor ray trace scene, not the `.rte`
  (`EditorTraceScene`, `LightTrace`):
  - Instances are two-level, at world +0x2bd0.
  - Mask 0xc00060b1 is tested against the object flags and the triangle
    flags.
  - `CMapMesh` flags are 0x4000 under an entity (brush entities never
    occlude), 0x20 when node +0x278 is 0, and 0xa000 or 0x2000 from
    `disableShadows`.
  - Triangle flags come from the material's int attributes, plus 0x20 for
    face flag 4.
  - Mode 2 is the masked front-face trace; flag-8 triangles get a second
    pass.
  - **Static props** are in the scene (`EditorTraceScene.StaticPropInstances`),
    two levels deep as Valve builds them:
    - The prop's model scene is instanced at the prop's placement, with owner
      flags 0x80b0000, which the light mask passes. Other model owners carry
      0x4000 and are skipped.
    - Inside it, each selected mesh's own scene is instanced at the identity
      (`ModelRayScene_Build`: `MeshSystem001` virtual 0xe8, meshsystem
      20260923).
    - Each level takes the ray into its own space and renormalises it, which
      moves last bits. That is why nesting mattered: atixref went from 38
      lights differing to 22.
  - **A map mesh's faces** go in as `RayScene_AddFace` adds them
    (`MeshTessellation.RayScene`):
    - A face whose first corner has no subdivision level is the polygon the
      physics cut takes: its corners from the first half-edge, with 2^level
      lerped points on each edge a subdivided neighbour splits, cut by the
      polygon triangulator.
    - A subdivided face is one displaced patch grid per corner at that
      corner's own level, each cell as [a, (r, c+1), b] then [a, b, (r+1, c)]
      (`MeshTessellation_PatchIndices`). Nothing is stitched, and a level
      above 5 adds nothing.
    - Unwelded, faces in order, winding (0, 1, 2). A mesh under a
      `CMapDeformer` is moved through the lattice and wound (2, 1, 0) when
      the deformer says so; that path is not ported (no specimen has one).
    - Tracing subdivided faces this way took atixref from 22 lights off to 4.
  - **A mesh's ray scene** (`CMeshRayTrace`, meshsystem):
    - It is built from the mesh's trace data (`CMeshSystem` slot 30, mesh
      +0x200: per draw call a triangle count, 0x48-byte `TraceVertex_t`
      vertices, index triples and the material).
    - Each triangle's flags are the material's int attributes. It is the same
      ten-hash table as resourcecompiler's material vis flags
      (`TraceScene.MaterialFlags`).
    - Its acceleration structure is a `RayTracingEnvironment` (the kd tree
      visibility uses, `TracerKd`).
    - We read the triangles from the render vertex and index buffers (VRF),
      draw calls in order and triangles in index order, for the selected
      meshes: LOD mask 0 or holding LOD 0, and the mesh group mask meeting
      the model's default (`Model_MeshSelected`). Stock models carry no tools
      vertex buffer. Where the trace data is filled from is not read yet
      (ledger 33).
  - Our scene's acceleration structure is our own (a padded bounding-volume
    tree), not Valve's kd tree. Ties between triangles hit at the same
    distance are broken by triangle index, which Valve's walk may not do
    (ledger 31).
- **Speed:** each light traces on its own, in parallel on half the cores.
  The oriented-box fit stops scoring an orientation once 32-point chunks
  exceed the best volume, as the binary does, which is what makes it fast
  (the result is unchanged; `TheBoxFitMatches` checks it against the DLL).
  atixref's lump now takes about 2.5 minutes and Mako's about 6.

## Tests and tools

- **Against Valve:** `EntityLumpAgainstValveTests` prints
  `differs: <class> <n>` per map, and `LUMPDIFF` / `LUMPDIFF_ALL` show the
  diffs. `EntityClassCoverageTests` is the per-class ratchet.
  `ENTCOV_DUMP=<tsv>` dumps every difference.
- **Valve's compile of a map:** `MapFixtures.RcCompiledLump(s)` compiles
  into the scratch addon `s2c_rc_probe`, cached by compiler stamp. It refuses
  while CS2 or another resourcecompiler runs.
- **Probe maps:**
  - `tools/coverage/probe_map.py` builds the class probe, and
    `tools/physics/prefab_probe_map.py`, `prop_override_map.py`,
    `settle_round_map.py` and `tools/entities/prefab_nest_probes.py` the
    others.
  - `LUMPDUMP=<vpk>` (`DumpPackage`) prints a package's entities one per
    line; `LUMPKEYS=<hammer id>` both sides' key order.
  - They live in `content/csgo_addons/s2probe/maps/`, not in the repo.
- **Captures:**
  - `tools/entities/capture_instances.py` and `find_new_nodes.py` record
    instance ids.
  - The light oracles: `LightBuildOracleTests`,
    `LightPrecomputeOracleTests` (resourcecompiler in process), and
    `EditorTraceSceneTests` (`ETS_MAP=<map>` or `all`, `ETS_RAYS`).
