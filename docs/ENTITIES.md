# Entity lump

`maps/<map>/entities/*.vents_c`: every entity, key and I/O connection the
compile writes. The port is `Maps/EntityLumpAuthor*.cs`, `EntityLumpSet.cs`,
`MapEntities.cs`, `MapInstances.cs`, `MapPrefabs.cs` and `FgdSchema.cs`. The
settle that moves physics props is in [SETTLE.md](SETTLE.md).

## Status

- **Pinned maps:** every lump of the 8 pinned maps matches Valve's outside
  the documented gaps (atixref, ze_hold_em_p, Mako, cardtest, probe01,
  ze_doom_p2's gameplay, c2m2's gameplay and its environment prefab). So do
  both prefab probes.
- **The comparison is strict:** types to the array element, KV3 flags, key
  and entity order, every connection field.
- **Coverage:** all 354 classes Hammer offers are measured through the probe
  map (every class bare and with every key set), and 343 are exact.
  [COVERAGE.md](COVERAGE.md) has the list.
- **The settle** runs in the lump build, and its output is exact.

Open:
- **Light keys:** the precomputed shape keys and, when baked, the shadow
  slot keys are in the lump (the lights are still a documented gap in the
  lump test while these close):
  - **ze_hold_em_p (baked):** every light's shape keys are exact, and so
    is `precomputed_vis_clusters` on all 37 (value and place, last of the
    values; `LUMP_VIS=1` runs vis for it): the whole lump has no
    difference. A baked compile writes it (WRB_PrecomputeLightVisMembership)
    for barn, rect and omni2 lights of direct light mode 3: the light is
    sampled as for its shape keys, and every vis cluster with a box
    overlapping the light's bounds and reached by one of its rays
    (`LightVisClusters`) is listed, ascending. light_rect gets none from
    the port (no record), and omni2 is not measured.
  - **atixref:** 236 of 239 lights exact, key order included. Omni2 lights
    7306, 7318 and 7348 are each off by 0.01 to 0.03 on one cube face.
    Traced with `LightRayProbe`, 7306's face 4 comes down to single rays
    grazing prop geometry: the one that reaches Valve's bound lands on a
    box's corner edge (local 6.00, -6.00). No single prop or material
    explains it (GROUND_TRUTH 31).
- **Prefabs:** instances inside prefabs, and prefabs with
  `fixupEntityNames`, are refused.

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
  - A prefab with `loadAtRuntime` is skipped.
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
    `tools/physics/prefab_probe_map.py`, `prop_override_map.py` and
    `settle_round_map.py` the others.
  - They live in `content/csgo_addons/s2probe/maps/`, not in the repo.
- **Captures:**
  - `tools/entities/capture_instances.py` and `find_new_nodes.py` record
    instance ids.
  - The light oracles: `LightBuildOracleTests`,
    `LightPrecomputeOracleTests` (resourcecompiler in process), and
    `EditorTraceSceneTests` (`ETS_MAP=<map>` or `all`, `ETS_RAYS`).
