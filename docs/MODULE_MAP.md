# Module map: what each part of the compile needs, and where Valve does it

The map compile is one resourcecompiler run. This file maps each of our modules
to three things:
- the Valve code it mirrors, by the names the Ghidra project now carries;
- what it takes from other modules;
- what it still needs read.

Addresses stay out of prose. They live in `tools/re/names/` and `D:/tools/names/`,
and the Ghidra project carries every name there. Look a name up with
`python tools/re/dec.py <dll> <address>`, which decompiles with all known names
substituted.

## Naming, and how to keep it current

- `tools/re/harvest_names.py <dll> --out <json>` names a stripped Valve DLL from
  the PE alone, in about 10 seconds for resourcecompiler. It uses these sources,
  strongest first:
  - assert strings
  - bare `Class::Method` strings
  - progress messages (`Step_...`)
  - profiler scope names
  - exports
  - RTTI vtable slots (`Class::vfN`)

  On the 2026-09-23 resourcecompiler it names 28,973 of 74,359 functions.
  physicsbuilder gets 6,466 and vphysics2 1,089.
- `tools/re/names/make_manual.py` holds the names established by reading, each
  with a module tag (`s2c:physics/hull`, `s2c:geometry/mesh-export`, and so on).
  Every one of them is backed by a port that matches Valve's output.
- `tools/re/apply_names.py` writes both into Ghidra through ReVa:
  - It uses primary labels, so no signature gets locked.
  - It never replaces a name someone else set.
  - It checks the program in.
  - RTTI names stay in the JSON (`--rtti` would take hours through ReVa); `dec.py`
    substitutes them on the fly.
- The entity side keeps its own lists in `D:/tools/names/` and its map in
  `docs/SETTLE_FUNCTIONS.md`.

After a game update, rerun the harvest on the new DLL. Move the hand-kept
addresses with `D:/tools/port_addr.py` and reapply.

## The spine: `CWorldRendererBuilder::Build`

The `-world` phase, in order:

1. Output folders (`worldnodes`, `lightmaps`, `flammables`, `entities`) and the
   `default_ents` lump.
2. The scene build, followed by `WRB_CreateEntityTemplateLumps`.
3. `WRB_LoadStaticPropModels` and `WRB_RemoveZeroExtraAttributeStreams`.
4. **Ray Tracing Environment**: `CreateRayTracingEnvironment` writes the `.rte`
   that visibility traces.
5. `WRB_PrecomputeLightVisMembership` (lights' `precomputed_vis_clusters`) and
   `WRB_BakePrecomputedShadows`.
6. **Creating World**: world nodes via `CompileAndSaveNodes` (render clusters,
   mesh splits, T-junction fixes, aggregate RT proxies).
7. `Vrad3_Init` and `WRB_BuildPathTraceSceneInfo` (lights, cameras, instances,
   mesh files for vrad3), then the LPV atlas.
8. **Creating entity lumps**: `WRB_WriteEntityLump`, once per lump.

Visibility itself is `visbuilder.dll`, run inside the same phase (`docs/VIS.md`).

A full compile (no `-fshallow`) then compiles the children:
- world physics: `CPhysicsBuilder::Build` in physicsbuilder writes
  `world_physics.vmdl`;
- baked lighting: `CStaticLightingProcessor::BakeLighting`;
- nav: `CNavMesh`;
- cubemaps, and the rest.

## Modules

### Containers and packaging

- Mirrors: resource writers (KV3, RERL, the index files), not map-builder code.
- Needs: every module's outputs.
- Open: packing a whole map from our own outputs.

### Map loading and instances (shared)

- Mirrors:
  - `Step_LoadingMap`
  - `MapNode_WorldMatrix` and `MapInstance_StepMatrix`
  - the `CMapNode` classes (`CMapMesh`, `CMapEntity`, `CMapInstance`, `CMapGroup`,
    `CMapWorld`, ...), whose vtables are RTTI-named
- Needed by: geometry, physics, entities and the settle.
- Open: the ulp-level differences on rotated instance copies. They show up in
  the trace scene, in entity angles and in per-corner positions, and they are
  one question, how instance matrices compose.

### Geometry: the mesh export

- Mirrors: `CMapMesh_ConvertMeshForBuilder` and its steps, all named:
  - `HammerMesh_CopyFrom`, `HammerMesh_TransformToWorld`
  - `HammerMesh_TexcoordsOutOfRange`, `HammerMesh_ShiftTexcoordIslands`
  - `CMesh_WriteDmeMesh`
  - then the builder reads the DMX back through `MapMeshBuffer_Unserialize` and
    `DmeMeshToCMesh`
- Ported: `Maps/MapMeshCorners.cs` (`docs/HULLS.md`).
- Needed by: physics (every piece), world nodes, the trace scene.
- Open:
  - the stream layout per material shader (render meshes need it, physics does not);
  - faces re-projected by about 1e-5;
  - `ApplyDeformer`, used by deformer meshes only.

### Geometry: trace scene (`.rte`)

- Mirrors: `CreateRayTracingEnvironment`, fed by the scene the mesh export builds.
- Ported: `Maps/MapGeometry.cs` (`docs/RTE.md`).
- Needs: the mesh export, including subdivision.
- Needed by: visibility (and the settle? no, the settle builds its own collision).
- Open: displacement, which is in the subdivision code (`BakeSubdivisionForFaces`
  is scope-named: start there).

### Geometry: world nodes and render meshes

- Mirrors:
  - `CompileAndSaveNodes` and `Step_BuildingRenderClusters`
    (`VisibilityGuidedMeshClustering`)
  - `Step_SplittingMeshWith`, `Step_RemovingTrianglesInside`
  - `FixTJunctionEdgeCracks`, `BuildAggregateRTProxies`
  - `PostCompileNode`, `LoadMaterialsInMeshList`
  - `Step_BuildingVertexOverrideStreams`
- Needs:
  - visibility, for the clusters that split the geometry;
  - the mesh export;
  - material shaders, for vertex layouts;
  - static props.
- Open: all of it; these are the entry points.

### Visibility

- Mirrors: `visbuilder.dll` (`docs/VIS.md`), plus `CVisibilityMeshMerger::MergeMeshes`
  on the resourcecompiler side.
- Needs: the trace scene.
- Needed by:
  - world nodes (the clusters);
  - light keys (`precomputed_vis_clusters`);
  - possibly the world-physics piece order (open).

### Physics: brush-entity hulls

- Mirrors:
  - `MapBuilder_BuildPhysicsPieces`, `MapBuilder_BuildPhysicsPiece`
  - `MapBuilder_HullsFromMesh`, `RnHullCreate` and its helpers
  - `HullSimplifier_*`, `Qem_*`, `HullAgglomerator_*`
  - `RegionSvm_*`, `RnHull_Transform`
- Ported: `Physics/` (`docs/HULLS.md`), exact on every test map.
- Needs: the mesh export, the weld, the CTransform math.

### Physics: world collision

- Mirrors:
  - `MapBuilder_BuildPhysicsPiece` (mesh branch) and `ModelDoc_CreateNode`
  - `CModelDocCompileInstance::CompilePhysics`, then `CPhysicsBuilder::Build`
  - then `RnMeshCreate` and `RnMesh_*`
- Ported: `Physics/RnMeshBuilder.cs`, exact on every captured call
  (`docs/WORLD_PHYSICS.md`).
- Needs:
  - the mesh export and weld (for the pieces);
  - material surface properties and collision groups (`CModelDocPhysicsShape_Mesh`,
    `CModelDocPhysicsMeshFile`);
  - static prop models (the hulls, via `CModelDocPhysicsHullFile`);
  - possibly visibility (piece order, open).
- Needed by: containers, and the settle (it builds world collision the same way).
- Open:
  - how `CPhysicsBuilder::Build` groups pieces by collision attribute;
  - the piece order;
  - static-prop hulls.

  The entity side's in-process vphysics2 oracle (`tests/Vphysics2Oracle.cs`) can
  run `RnMeshCreate` and friends without a compile.

### Entity lump (entity side)

- Mirrors: `WRB_CreateEntityTemplateLumps`, `WRB_WriteEntityLump`, and the
  entity-side names (`EntityLump*`, `Fgd*`, `TemplatePass*`, `MapDoc_*`).
- Ported: the lump itself, exact on every test map.
- Needs:
  - map loading;
  - the settle (prop origin, angles, spawnflags);
  - light keys.

### Physics settle (entity side)

- Mirrors: `MapSettle_*` in resourcecompiler; `CRnWorld_Step` / `Solve` / `Collide`
  and the broadphase in vphysics2 (`docs/SETTLE_FUNCTIONS.md`).
- Needs:
  - world collision (triangle meshes: the `RnMeshCreate` port applies);
  - brush-entity hulls;
  - prop collision from models.
- Needed by: the entity lump.

### Light precomputed keys (entity side)

- Mirrors: `WRB_PrecomputeLightVisMembership` (Halton samples of each light's
  volume, traced against the world, fitted).
- Needs: visibility (clusters) and the trace scene.
- Needed by: the entity lump.

### Baked lighting, probes, cubemaps, nav

- Mirrors:
  - `Vrad3_Init`, `WRB_BuildPathTraceSceneInfo` (the scene vrad3 gets)
  - `WRB_BakePrecomputedShadows`, `CStaticLightingProcessor::BakeLighting`
  - `CWorldRendererBuilderNode::BakeLightMaps`
  - `CBakeSkyLightHelper`, `COmniLightHelper` (RTTI)
  - `CNavMesh::CreateArea` / `Update` and `CMapNavData`
- Needs:
  - world nodes (lightmap charts);
  - visibility;
  - the trace scene;
  - world collision (nav walks it).
- Open: all of it; vrad3 covers lighting as a stopgap (`docs/VRAD3.md`).

## Dependency order, as it stands

```
map loading ─┬─ mesh export ─┬─ trace scene ── visibility ─┬─ world nodes ── lighting, probes, cubemaps
             │               │                             ├─ light keys ─┐
             │               ├─ brush hulls ───────────────┤              │
             │               └─ world collision ───────────┼─ settle ─────┼─ entity lump
             └─ static props ─┘                            └─ nav         │
                                                                          └─ (packaging takes everything)
```
