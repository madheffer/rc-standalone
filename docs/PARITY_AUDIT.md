# Parity audit (2026-11-03)

A read-only audit of what stands between the port and 1:1 bit parity with Valve, chunk by chunk. Nothing in the
code was changed. The tree audited is `d2937a4` (ledger 210 merged).

## Method, and what this audit is not

- **Sources read:** GROUND_TRUTH.md (all 5,800 lines skimmed; every entry with "open", "not read", "measured",
  "from output", "fitted", "not ported" read in full), HANDOFF.md (rules, roadmap, the last sessions),
  CS2_CLOSED_QUEUE.md, GEOMETRY.md, PHYSICS.md, SETTLE.md, VISIBILITY.md, ENTITIES.md, CONTAINERS.md, RESOURCES.md,
  COVERAGE.md (switch tables), NAV.md, CUBEMAPS.md ("What is left", "Open"), STEAM_AUDIO.md, LIGHTING_ROADMAP.md.
- **Code:** every `NotSupportedException` in `src/Source2.Compiler` and `src/Source2.Compiler.Gpu`, every
  `notes.Add(... not ported ...)` and every comment matching measured / fitted / fit / from output / not read /
  guess / assum / stand-in / stands in / likely / presumably / unknown / not found / not captured / not measured.
  Each hit was read in context; false positives were dropped (for example `PackedNormals.cs:110`'s "fitted arcsine"
  and `TangentFrame.cs:16`'s "approximate asin" are Valve's own approximations, cited by address; PathDeformer's
  "fit" is Valve's curve fit, oracle-checked).
- **Not done:** a line-by-line re-derivation of every constant against its decompile. A rule whose comment cites
  an address was taken as cited. A constant with no comment at all can be missed by a keyword sweep; the ones
  found by reading around the hits are listed, but the (a) lists are a floor, not a census. Items whose source I
  could not settle from the docs are marked **unclear**.
- **No compile, Frida, cubehost, ReVa or test was run.** Counts quoted ("344 of 553", "1,268 of 1,270") are the
  docs' own, with their dates; where a doc is older than the code it describes, that is said.

Legend:
- **(a)** a rule, constant, order or default with no Valve citation, or one the code or docs call measured,
  fitted or not read.
- **(b)** a refusal (`NotSupportedException`, a "not ported" note, an output silently left out). **Reach**: *map*
  (a .vmap can trigger it), *option* (a compile switch), *gameinfo* (only a gameinfo.gi change reaches it, which a
  map cannot), *internal* (guards a state the code cannot reach), *none known* (map-reachable in principle, no
  specimen or corpus map has it).
- **(c)** a known output that is not 1:1. **Kind**: *variance* (Valve's own tool differs run to run or by
  machine; allowed under HANDOFF's rule if ours is one of Valve's values), *gap* (a real difference), *unverified*
  (ported, but the check against Valve is still owed).

Ranking "by maps affected" is by the corpus and the docs' own counts; where no count exists the rank is a judgement
and says so.

---

## 1. World geometry

Folders: `Maps/MapMeshes.cs`, `MapMeshCorners.cs`, `MapGeometry.cs`, `TraceScene.cs`, `NodeMeshEntries.cs`,
`NodeEntryHeader.cs`, `MeshEntryFlags.cs`, `MaterialAttributes.cs`, `MaterialStreams.cs`, `MeshLists.cs`,
`WrbMeshEntry.cs`, `TJunctionFix.cs`, `VisibilityMeshMerger.cs`, `RenderClusters.cs`, `WorldNodeBuild.cs`,
`WorldNodeCompile.cs`, `WorldNodeModels.cs`, `WorldNodeModel.cs`, `WorldNodeFiles.cs`, `NodeDraw.cs`, `UvDensity.cs`,
`TangentFrame.cs`, `PackedNormals.cs`, `NodeOverlays.cs`, `OverlayProjector.cs`, `MeshTangents.cs`,
`PolygonTriangulator.cs`, `NodePropEntries.cs`, `NodePropAggregates.cs`, `PropTransform.cs`, `EnvVolumes.cs`,
`HalfEdgeMesh.cs`, `SubdivisionBake.cs`, `MeshTessellation.cs`, `FaceArrays.cs`, `VertexKdTree.cs`,
`EntityRenderModels.cs`, `SmartPropEvaluator.cs`, `SmartProps.cs`, `LatticeDeformer.cs`, `PathDeformer*.cs`,
`MapDeformers.cs`, `CTransform.cs`; `Meshopt/*`; `Containers/WorldNodeModelAuthor.cs`, `WorldNodeModelTrees.cs`,
`WorldNodeSceneTrees.cs`, `WorldTrees.cs`. Docs: GEOMETRY.md, GROUND_TRUTH 4-6, 10, 29, 34-44, 47, 54, 55, 58-60,
87, 164, 165, 201, 210.

### 1(a) Uncited, measured or not-read rules

| # | where | rule | status |
|---|---|---|---|
| 1.a1 | `Maps/MapMeshes.cs:316-322` | A point through a node matrix sums each row `(x*m0 + z*m2) + (y*m1 + t)` | "Measured on atixref" (a roll of -89.999985 and a 180.5 yaw tell orders apart); GEOMETRY.md:23 calls it "tier0's SIMD transform" with no address |
| 1.a2 | `Maps/MapMeshes.cs:15-27`, `Physics/BrushHulls.cs:125-127` | An instanced mesh's faces placed by the collapsed node then its own matrix (path order) | Measured against the .rte (atixref, Mako); ledger 5 "both place atixref the same; not read" |
| 1.a3 | `Maps/NodeMeshEntries.cs:5-15` | The builder mesh renormalises normals and tangents: squares summed z, y, x, times the reciprocal of the root | Measured exact on probe01; ledger 164 "Located, open" (ruled out 1810c4e50 and ConvertMeshForBuilder's direct callees) |
| 1.a4 | `Maps/NodeMeshEntries.cs:150-153` | An added texcoord 1 goes right after the first texcoord | "The place after the first is measured" (538 atixref entries); the add itself is read (1810dcca0, ledger 201) |
| 1.a5 | `Maps/NodeOverlays.cs:84-88` | An overlay face's texcoords whose box leaves [0,1] lose the box centre truncated toward zero | Measured on 297 atixref overlays; ledger 164 "Located, open": the shift is where the projection cache (+0x3cc0) is filled |
| 1.a6 | `Maps/MaterialAttributes.cs:57-63` | AllowBackfaceCulling answers true when undeclared, except the `NoCullDefault` shaders (csgo_water_fancy) | Ledger 40: measured on atixref, probe01, cardtest; the parent attribute set (CMaterial2 +0x60) not identified |
| 1.a7 | `Maps/WorldNodeFiles.cs:150-152` | The node lists its aggregates in ordinal model-name order | Measured (atixref 264, ze_hold_em_p 7); "Where Valve sorts them is not found" |
| 1.a8 | `Maps/WorldNodeFiles.cs:154-156` | Standalone (unbaked, unaggregated) props within one model in walk order | Measured (ledger 55, 2026-10-08, 65 atixref records); ledger 165 read the lists for baked props as last-to-first, so whether the standalone path walks the same lists differently is **unclear** |
| 1.a9 | `Maps/WorldNodeModels.cs:348-353` | Vertex field order: position, texcoords (and lightmap uv), normal, tangent, then the rest | Measured (ze_hold_em_p baked props stream P N Tg T0, lay out P T0 N); ledger 54 "the code that orders them is not read" |
| 1.a10 | `Maps/WorldNodeModels.cs:371-379` | Field formats by stream name; any other stream R8G8B8A8_UNORM | The named ones are ledger 44 / 83; the default branch has no citation (**unclear**) |
| 1.a11 | `Containers/WorldTrees.cs:40-42`, `Maps/WorldNodeCompile.cs:75-81` | `m_flMinDrawVolumeSize` 1024, `m_bAggregateInstanceStreams` false, MinTrianglesPerCluster 2048 | No source cited at the line; GEOMETRY.md:188-196 attributes the cluster minimums to csgo_core gameinfo, the 1024 and the false are not attributed (**unclear**) |
| 1.a12 | `Maps/WorldNodeCompile.cs:75-77` | Node +0x300 / +0x304 override the cluster size and minimum triangles "when above 0" | Read; the port never sets them. Whether a .vmap can set them is **unclear** |
| 1.a13 | `Maps/NodePropEntries.cs:8-30` | Baked prop entries: vertices from the call's lowest to highest referenced vertex, stream list and zero defaults | Order and triggers read (180255b90, ledger 165); the vertex range and default streams measured on atixref's 102 entries |
| 1.a14 | `Maps/NodePropAggregates.cs:256-259` | A non-uniformly scaled smart prop model is baked | "not measured on a smart prop" (assumed from prop_static) |
| 1.a15 | `Maps/MeshTessellation.cs:69-98` | The builder's dense face order for subdivided faces; nested midpoints on a polygon face's edge | Face order "measured against atixref's exported meshes"; the edge midpoints "not measured". Used by the settle world and the editor ray scene (`SettleWorld.Meshes.cs:220`, `EditorTraceScene.cs:295`), not by the render path (which uses SubdivisionBake) |
| 1.a16 | `Maps/SubdivisionBake.cs` (ledger 6) | The face collapse | "Modelled from the edge collapses (confirmed by exact output, not read line by line)" |
| 1.a17 | `Maps/MapMeshCorners.cs:193-200` | The subdivided export end to end | Built from read pieces; checked on atixref's 44 subdivided entries only |
| 1.a18 | `Maps/TraceScene.cs:66-77` | Shadow modes 2 and 3 flag words | Read, not measured (no specimen) |
| 1.a19 | `Maps/SmartPropEvaluator.cs:674-680` | A smart prop node's own scale (node +0xb8) taken as 1 | "only 1 is measured, so only 1 is taken" |
| 1.a20 | Ledger 43, `NodePropAggregates` | Prop aggregate membership and draw order: Morton order of placed LOD bounds, then mesh pointer order | Grouping read (180272ce0); the pointer order is Valve heap order (see 1.c6); ours picks Morton/walk order |
| 1.a21 | Ledger 43 | The texcoord stream test after the runs (180273xxx, V_stricmp "texcoord") and extra vertex streams in prop aggregates | "Open" in ledger 43; not re-mentioned since (**unclear** whether ported) |

### 1(b) Refusals and silent omissions

| # | where | what | reach |
|---|---|---|---|
| 1.b1 | `Maps/NodePropEntries.cs:158-160` | csgo_foliage's NeedsLocalSpaceVertices combo rule (a note; the prop is baked as an ordinary shader) | map: any baked (non-uniformly scaled or baketoworld) foliage prop; ledger 39 open |
| 1.b2 | `Maps/NodePropEntries.cs:17` | Clutter, +0x40, extra-stream and +0x17c bake triggers; their sources not read (ledger 165) | map: clutter-tool props, props with material overrides that Valve treats as +0x40 |
| 1.b3 | `Maps/NodePropEntries.cs:135` | A mirrored deformer on a baked prop | map, none known |
| 1.b4 | `Maps/WorldNodeBuild.cs:142-147` | An overlay entry whose attributes ask for a cubemap or probe (bits 20/21): overlay entry +0x80 not read | map: an unlit overlay material that wants a probe or cubemap (lightmapped ones drop NeedsLightProbe) |
| 1.b5 | `Maps/SmartPropEvaluator.cs` 111, 161, 184, 222, 239, 262, 284, 368, 377, 388, 409, 450, 544, 567 | Smart prop features: stored m_DeltaTransform, unseeded elements, root modifiers, detail objects and surface overrides, other element and modifier classes, PickOne modes and IsValid, FitOnLine variants | map: any map using such a smart prop (Mako's scale operations are named) |
| 1.b6 | `Maps/SmartPropEvaluator.cs:636`, `:653` | A scaled smart prop node, a smart prop inside an instance inside a prefab: **skipped with a note**, so its geometry is missing from the output | map |
| 1.b7 | `Maps/MapDeformers.cs:52`, `:54` | A scaled mesh, or a subdivided mesh, under a deformer | map, none known |
| 1.b8 | `Maps/LatticeDeformer.cs:411`, `:434`; `PathDeformer.cs:96` | Control point count mismatch; an unknown deformer class; a closed path target with no FGD | map (malformed or future classes); the FGD case is API-only |
| 1.b9 | `Maps/SubdivisionBake.cs:735-742` | An edge between two coincident baked vertices (CollapseEdge 181382170) | map, none known |
| 1.b10 | `Maps/CTransform.cs:45`, `:91`, `:99` | Inverting or matrixing a scaled CTransform; a zero quaternion | map: scaled smart prop transforms (none known beyond 1.b6) |
| 1.b11 | `Maps/MeshTessellation.cs:282`, `FaceArrays.cs:261`, `MeshTessellation.cs:560` | Stitched patches' paint (NaN, silent) in the settle/ray-scene tessellation; a patch frame of extreme length | map (stitched subdivision with paint) |
| 1.b12 | `Maps/OverlayProjector.cs:200`, `:229`, `:338` | An overlay target without a tangent stream (throws); two solver fallbacks not ported (NaN / 2, silent) | map, **unclear** how often |
| 1.b13 | `Maps/TraceScene.cs:77`, GEOMETRY.md:124-132 | RTE oriented skip boxes (ledger 27), the whole-mesh skip past 2 sqrt(3) g_flConfigMaxCoord, entry flag 0x80: none in code | map, none known (affects the .rte and so vis too) |
| 1.b14 | `Maps/WorldNodeCompile.cs:37` | "Only one layer is ported": every object is `_lr0`. What Valve's layer index is (node +0x148?) and whether any map makes lr1+ is **unclear** | **unclear** (possibly world layers) |
| 1.b15 | `Maps/WorldNodeFiles.cs:9-12`, `:170-171` | One world node `n0` only | **unclear** whether Valve ever splits a CS2 world into several nodes |
| 1.b16 | `Maps/WorldNodeCompile.cs:181`, `:569`, `:320-322` | No boxes and no bounds; aggregates of an unexpected list; the `Unported` default | internal (all 12 list kinds are handled, so `Unported` is unreachable today) |
| 1.b17 | HANDOFF roadmap 7 | Detail grass / clutter objects: no code | map |
| 1.b18 | COVERAGE.md switch table | `-tileMeshBaseGeometry`, `-deformables`, `-rebake_surfacegraph`, `-debugvisgeo`, `meshletMaxTriangles` / `meshletMaxVertices`: not read | option (Hammer's dialog writes the first four) |
| 1.b19 | `Meshopt/MeshoptSimplify.cs:93` | Other simplify options | internal (lightmap query data only) |

### 1(c) Outputs not yet 1:1

| # | output | source | kind |
|---|---|---|---|
| 1.c1 | Tangents of subdivided node entries: 24 of 538 atixref entries differ by tangents alone | ledger 38, CS2_CLOSED_QUEUE A.8 | gap |
| 1.c2 | Overlay tangents: about 30 small atixref models; our packed NORMAL holds a default tangent (angle 512) where Valve's varies | ledger 60 (HANDOFF roadmap 7) | gap |
| 1.c3 | 95 agg_prop models on atixref: the NORMAL word's tangent angle 180 (Valve) vs 0 (ours) on a few vertices | ledger 55 | gap |
| 1.c4 | Two BuildNode agg_nomerge aggregates (inferno_trim_wood01_painted_blend_02, hr_concrete_wall_painted_001): later meshlets differ on Valve's own entries | ledger 34 | gap |
| 1.c5 | cardtest's teleport02 entry: Valve gives a missing material 0x160100000 and a 256x256 representative texture; ours the empty set | ledger 42 | gap (any map referencing a material the content lacks) |
| 1.c6 | Prop aggregate draw/fragment order: Valve's is heap order (two compiles swap metal_door_001 _0/_1); ours matches 138 of 171 draw orders, 218 of 361 fragment orders | ledger 43 | variance, **unverified**: the rule needs ours to equal one Valve run, which has not been checked against two runs |
| 1.c7 | atixref `-world` whole package: last whole count 344 of 553 files byte-identical (2026-10-08); since then 1,268 of 1,270 draws exact (ledger 34) but no newer whole-package count is recorded | ledgers 55, 34 | **unclear** (needs a fresh ParityReport) |
| 1.c8 | ze_hold_em_p `-world`: "three light aggregates' MDAT" (ledger 55, 10-07) | ledger 55 | **unclear**: the 11-02 full compile is 55 of 56 files, so probably closed, but no ledger entry closes it |
| 1.c9 | s2c_coloradjust m_flUvDensity on five draws in the last digits | ledger 201 | **unclear** (ledger 210's run has every vmdl_c byte for byte, so probably closed with the texcoord fix) |
| 1.c10 | .vwrld m_nCompileTimestamp and m_nCompileFingerprint | ledgers 54, 55 | variance (allowed; S2C_WORLD_STAMP pins them) |
| 1.c11 | A -world compile's merger input from the .vmap is not built end to end for a full compile (the bake's lightmap UVs) | GEOMETRY.md:301-311 | **unclear**: ledger 83/87 later ported the bake's write-back; GEOMETRY.md not updated |

### 1. Verdict

Not 100%: three tangent gaps (1.c1-1.c3), one meshlet gap (1.c4), the missing-material rule (1.c5), an unverified
variance claim (1.c6), and a dozen measured-only rules (1.a1-1.a9) that hold on the specimens but are not read.
Refusals that ZE maps can reach: foliage (1.b1), clutter (1.b2, 1.b17), smart props (1.b5, 1.b6).

Next steps, by maps affected (judgement where noted):
1. **Measured placement and normalisation (1.a1, 1.a3)**: every map. Read the SIMD transform HammerMesh_TransformToWorld
   (1810c4e50) calls, and find the renormalise between ConvertMeshForBuilder and WRB_CollectMeshEntries (ledger 164
   lists what is ruled out; next candidate: CMesh_WriteDmeMesh / DmeMeshToCMesh 180d46590's callees).
2. **agg_prop tangent angle (1.c3)**: every map with aggregated static props. Capture the tangent handed to
   CompressTangentFrame (182067e80) for one of the 95 models in a -world compile (Frida, CS2 closed), against
   meshsystem's unpack (180032730) of the model's TANGENT.
3. **Field order and aggregate list order (1.a9, 1.a7)**: every map. Read AddDrawDescriptors' layout builder
   (1802ed7f0 / 1802f5680) for the field order, and WorldNodeFiles' aggregate list writer (the .vwnod scene
   object array fill in CompileNode's tail, 180277ab0) for the sort.
4. **Overlay tangents and recentre (1.c2, 1.a5)**: maps with static overlays (most hand-made maps). Read
   COverlayProjector's cache fill (+0x3cc0, from CMapStaticOverlay_GetOverlayDescs 181194f40) and capture the
   overlay CMesh's tangent stream before FUN_181365ce0.
5. **Subdivided tangents (1.c1)**: maps with subdivided meshes. CS2_CLOSED_QUEUE A.8 is written and ready.
6. **AllowBackfaceCulling default (1.a6)**: every map (affects attribute bit 32 and meshlet cones). Find what fills
   CMaterial2 +0x60 in materialsystem2's material load.
7. **atixref whole package (1.c7)**: re-score with ParityReport against the current atixref reference; it turns
   1.c4, 1.c8 and the agg_nomerge MDAT note into numbers.
8. **Foliage, clutter, smart prop skips (1.b1, 1.b2, 1.b6)**: ZE maps with foliage and clutter (judgement: common).
   Read the csgo_foliage static combo for NeedsLocalSpaceVertices and the AddStaticProps clutter / +0x40 / +0x17c
   triggers (180255b90's callers).
9. **Missing material (1.c5)**: read CompileNode's code before the list loop (FUN_18026cac0, FUN_18027add0).
10. **lr / n0 (1.b14, 1.b15)**: read CompileNode's layer loop and CWorldRendererBuilder's node split to say whether
    either is reachable.
11. **Prop aggregate variance (1.c6)**: compile atixref twice with Valve's tool and check ours equals one run.

---

## 2. Physics and collision

Folders: `Physics/*`, `Simulation/*`, `Maps/SettleWorld*.cs`, `Containers/WorldPhysicsAuthor.cs`. Docs: PHYSICS.md,
SETTLE.md, GROUND_TRUTH 3-7, 26, 52, 53, 56, 124-126, 128-138, 158-169.

### 2(a)

| # | where | rule | status |
|---|---|---|---|
| 2.a1 | `Physics/ShaderAttributes.cs:13-62`, used by `Physics/MaterialCollision.cs:51-54` | Per-shader translucent and alpha-test tables (combos scanned on 1.41.8.8; csgo_unlitgeneric / csgo_static_overlay blend modes "measured 1.41.8.8"; csgo_environment's alpha test "measured" on Mako's .rte) | Measured tables, not read from physicsbuilder. Ledger 165 replaced them for the trace scene with the material store; **world physics still reads them** |
| 2.a2 | `Physics/BrushHulls.cs:21-22` | Face order around a vertex in the hull neighbour set: triangle order stands in | Ledger 4 (decides only on a hash collision) |
| 2.a3 | `Physics/BrushHulls.cs:125-127` | Instance path order for brush entity meshes | Ledger 5, not read (same as 1.a2) |
| 2.a4 | `Physics/WorldCollision.cs:548-552`, `:1039-1043` | Painted vertex = first corner met in its own face set | Measured on ze_hold_em_paint_flat (76 of 76) |
| 2.a5 | `Maps/SettleWorld.cs:131-133` | Surface thickness 0.1 when no base states one | "measured on atixref's settle" |
| 2.a6 | `Simulation/RnMassUpdate.cs:421-424` | An all-zero inverse inertia becomes the identity | DAT_18055e8a8 cited, "measured on atixref's static mesh bodies" |
| 2.a7 | Ledger 7 | Collision names keep the spelling first registered; the registration order is not read | No specimen spells a game name two ways |
| 2.a8 | Ledger 26, `SettleWorld.PrefabPlacement` | A prefab placed as instance collapse moves a node | Measured whole-file on s2c_prefabprobe; the prefab loader (+0x6b8 documents) not read |
| 2.a9 | `Containers/WorldPhysicsAuthor.cs:10-20` | Container facts (version 1, PHYS CTRL RED2 DATA, generic KV3) | Measured on 32 Valve compiles |
| 2.a10 | `Maps/SettleWorld.Run.cs:30-33`, SETTLE.md out-of-bounds note | Bodies turn dynamic in build order (Valve: a hash map over node pointers); several bodies leaving the world in tree order (Valve: hash-set pointer order) | Valve variance (heap order); see 2.c3 |
| 2.a11 | `Physics/StaticPropHulls.cs:18` | Static prop hulls | Verification statement (129 hulls of ze_hold_em_nb exact), chain read; listed only because it says "measured" |

### 2(b)

| # | where | what | reach |
|---|---|---|---|
| 2.b1 | `Physics/WorldCollision.cs:526`, `:566`, `:1022` | **"the 0.5 rule stands in"**: a new-blending painted material's layers fall back to the non-sampled split when the GPU material sampler has no render (no GPU, no ToolsVis program) or the piece is subdivided | map: painted new-blending materials (csgo_environment_blend family); without a GPU on every such map |
| 2.b2 | `Physics/WorldCollision.cs:434`, `:509`, `:519` | Subdivided faces stitched to finer neighbours: cut order not ported; a stitched painted mesh is not split by layer | map |
| 2.b3 | `Physics/WorldCollision.cs:891` | physicsIncludedDetailLayers / physicsMissingDetailLayers set | map, but CS2 ships no detail layers, so Hammer cannot offer one |
| 2.b4 | `Physics/RnHullBuilder.cs:249`, `:312`; `RnMeshBuilder.cs:79` | Simplify algorithms 1 and 2, the minimum radius check, RnMeshCreate's simplifier | model compile only (the map passes fixed blocks) |
| 2.b5 | `Physics/RnHullBuilder.cs:535`, `:540`; `HullSimplifier.cs:1085` | A cylinder pointing down; a zero turn; a hull falling apart into clusters | map, none known |
| 2.b6 | `Maps/SettleWorld.Aggregate.cs:73`, `:109`, `:419`, `:435`, `:437`, `:505`, `:540` | Aggregates with mesh shapes, a mass-centre override, joints to the world body, motors and linear limits, elastic joints, other joint types, friction torque on a singular inertia | map: ragdoll / physics-part props whose models use them (PHYSICS.md 5c) |
| 2.b7 | `Maps/SettleWorld.Run.cs:46`, `:75`, `:77`, `:100`; `SettleWorld.cs:306`, `:773`; `SettleWorld.WriteBack.cs:71`, `:109`; `Simulation/SettleWriteBack.cs:64` | A body the port cannot build; an aggregate with parts left static or no skeleton; several bodies per node; bone names with no bind pose; two parts on one bone; a scaled bind pose | map |
| 2.b8 | `Simulation/ContactLifecycle.cs` (9 throws), `ContinuousSolve.cs` (8), `WorldSolver.cs:44`, `:68`, `:311`, `RnWorld.cs:522`, `:543`, `:601`, `:631`, `:694`, `Broadphase.cs:386`, `:666`, `:687`, `JointSolver.cs:96`, `:244`, `:276`, `StepGlue.cs:287`, `MeshConvex.cs:24`, `RnMassUpdate.cs:476`, `:507`, `HullCast.cs:1043`, `DynamicTree.cs:644`, `IslandManagerOps.cs:835`, `:849` | vphysics2 paths the settle never takes on the corpus: contact and touch events, sensors, compound shapes, mesh shapes on moving bodies, joints in coloured islands, broken joints, other shape and joint types | mostly internal to a settle; compound shapes and moving mesh shapes are map-reachable through models |
| 2.b9 | `Simulation/CrtMath.cs` (12 throws) | CRT special paths (large-argument trig, NaN, overflow) | internal unless a settle produces such a value |
| 2.b10 | `Simulation/CrtMath.cs:12-14` | The SSE2 CRT build; only the FMA3 path is ported | machine: a CPU without FMA3 (Valve's own output differs there) |
| 2.b11 | PHYSICS.md:174-180 | Cable models' render half (no cable model is written); instances inside a prefab's map for entity models (no Valve probe) | map |
| 2.b12 | `Physics/EntityPhysicsModels.cs:22` | Mesh-type pieces and PhysicsTypeOverride base classes other than func_shatterglass | map |

### 2(c)

| # | output | source | kind |
|---|---|---|---|
| 2.c1 | s2port_doom's fix3_geo_all: one byte longer in 33 LZ4 blob frames (likely an lz4hc version difference) | ledger 3 | gap (1 of 80 corpus files) |
| 2.c2 | ze_hold_em_p's six doors: a toolsclip mesh ships no hull because the .vmap is older than the compile | PHYSICS.md:176-177 | **unclear** (a stale reference, not a port gap, per the doc) |
| 2.c3 | Settle poses on coaster, dkr_m4_ferris, dkr_m2_carnival, dkr_m5_stadium, dkr_m3_tunneloflove (`UnstableSettle`) | ledger 52, ENTITIES.md:23-29 | variance (checked against two compiles or Valve's captured order) |

### 2. Verdict

world_physics is whole-file exact on every corpus map with a current compile, so the blockers are provenance and
reach, not known bytes: the measured shader tables (2.a1) feed every map's collision side; the 0.5 fallback
(2.b1) changes bytes on any painted new-blending map compiled without a GPU; the settle refusals (2.b6, 2.b7) stop a
compile rather than differ.

Next steps, by maps affected:
1. **2.a1** (every map): read physicsbuilder's material read (1806208d0 and the collision attribute it sets) and
   switch MaterialCollision to the material store the trace scene already reads (ledger 165).
2. **2.b1** (painted new-blending maps): make the GPU sampler a hard requirement or refuse instead of standing in;
   then port the subdivided new-blending texcoord carry (WorldCollision.cs:526).
3. **2.c1** (one file): diff the 33 frames against an lz4hc built at tier0's version.
4. **2.a5, 2.a6**: read the surface property base walk (the vsurf loader) for the 0.1 default.
5. **2.b6 / 2.b7**: wait for a corpus model (ModelPhysicsProbe lists them), per PHYSICS.md 5c.

---

## 3. Containers and packaging

Folders: `Containers/*`, `Io/*`, `Kv3/*`, `Compression/*`, `Dmx/*`, `Maps/MapRootFile.cs`, `MapAssets.cs`,
`MapSubassets.cs`, `ResourcePath.cs`. Docs: CONTAINERS.md, RESOURCES.md, GROUND_TRUTH 3, 12, 15, 21, 24, 55, 61,
85, 86, 119, 155.

### 3(a)

| # | where | rule | status |
|---|---|---|---|
| 3.a1 | `Io/VpkWriter.cs:9-23`, `:96-99` | VPK v2 layout: tree by extension, folder, name in reverse first appearance, data in ordinal path order, 1 MiB BLAKE3 chunks, MD5s, empty signature | Ledger 12: byte exact on four packages, "the packer not read" |
| 3.a2 | `Containers/ContainerFacts.cs:8-12` and the constants below | Per-type resource versions and RED2 special dependencies | "measurements, not guesses" from 200-sample surveys of stock content |
| 3.a3 | `Containers/Source2ContainerAuthor.MapRoot.cs:10-14`, `:78-81`; `Source2ContainerAuthor.cs:36-43` | Map child identities; RERL = children plus external references; child resources record no input dependency | Measured across 116 shipped maps |
| 3.a4 | `Containers/ValveLayout.cs:9-18` | Blocks padded to 16, RERL to 4 with entries 8-aligned, nothing after the last block | Measured on world node models, node files and world files |
| 3.a5 | `Containers/Source2ContainerAuthor.cs:615-630` | Integer retyping (0/1 singletons, Int32, typed arrays widest element) | "measured against the real compiler at both boundaries"; ledger 61 settled the reader side by decompile |
| 3.a6 | `Containers/AuthoredKv3.cs:14` | KV3 version 5 | Measured on a stock vmat_c |
| 3.a7 | `Maps/MapSubassets.cs:18`, `:54` | Subasset pairs in the RED2: the main lump's walk, a path's nodes before the path; each type a sub-table, entity's first | Measured on 24 s2c_rc_probe compiles (MapSubassetsTests pass on all 54; ledger 155) |
| 3.a8 | `Maps/MapRootFile.cs:42`, `:116`, `:139-140` | Argument table rows by builder; flattenPropsWithBakedLighting's place | Rows read (ledger 119), "Measured on 56 Valve compiles"; the one row "not captured" (cannot move a row, per the comment) |
| 3.a9 | Ledger 21 | Case folding: Containers, Kv3, Dmx, Io, Texture, Building keep .NET folding (23, 1, 1, 6, 6, 15 sites) | Open; also, `Maps/` has 79 .NET case-insensitive sites in 24 files and `Physics/` 2, although the ledger says those folders fold A-Z only. Identical for ASCII names; differs only on non-ASCII (**unclear** impact) |
| 3.a10 | Ledger 3 / 15 / 164 | Which writer uses binary_auto vs binary vs binary_bc | binary_auto settled; the writer-to-encoding map is open (no block measured so far uses another) |

### 3(b)

| # | where | what | reach |
|---|---|---|---|
| 3.b1 | `Containers/Kv3TextWriter.cs:243` | A KV3 text double that is not a float (V_DoubleToString_NoExponent) | **unclear** which output takes the text path with a double |
| 3.b2 | `Containers/Kv3TextWriter.cs:220`, `:232`; `Kv3V5Writer.cs:91`, `:260` | Other KV3 value types and flags | internal |
| 3.b3 | `Compression/Lz4Hc.cs:87` | An LZ4 HC stream past 2 GB | internal |
| 3.b4 | `Maps/MapCompile.cs:130`, `MapCompileArgs.cs:186` | `-f` / `-fshallow2` with a partial build | option (Valve refuses it too) |
| 3.b5 | COVERAGE.md resourcecompiler table | `-filelist`, `-r`, `-game`, `-skiptype`, `-dependency_check_only`, `-crc`, `-allowdebug`, `-vis_preview`, `-retail`: not read or not taken | option |
| 3.b6 | CONTAINERS.md:148-152, ledger 24, ledger 55 | What an entities-only build clears (entity models kept); `-entities` with no package; a no-vis -fshallow build keeping an earlier vvis | option |

### 3(c)

| # | output | source | kind |
|---|---|---|---|
| 3.c1 | fix3_geo_all's LZ4 frames | ledger 3 (also 2.c1) | gap |
| 3.c2 | .vwrld stamp and fingerprint; RED2 fingerprints that hash install paths (`VisibilityAuthor.cs:30`, CAVEATS "m_nFingerprint changes with the install root") | ledgers 54, 55 | variance (machine and time) |
| 3.c3 | RESOURCES.md:9-12 states the single-resource contract as "decoded-tree identity, not byte identity" | RESOURCES.md | gap against the user's bit-for-bit rule for the single-resource types (chunk 8); map containers are byte exact (ledger 3) |

### 3. Verdict

The map containers, KV3 v5, the VPK and the .vmap_c are byte exact on every reference, so nothing known differs
but one LZ4 frame set. What blocks a 100% claim is provenance: the VPK packer (3.a1), the container facts (3.a2-3.a4)
and the encoding-per-writer map (3.a10) are measured, not read.

Next steps, by maps affected:
1. **3.a1** (every package): read the VPK writer (vpk.dll or resourcecompiler's packer; find it from the "BLAKE3"
   chunk hash type and the 0x55AA1234 signature).
2. **3.a2-3.a4** (every resource): read CResourceCompiler's block writer and the per-type compiler identity tables;
   `tools/rc-oracle.ps1` already re-measures them after updates.
3. **3.a10**: list the KV3 save call sites in resourcecompiler with their encoding argument (ledger 3's plan).
4. **3.a9**: replace .NET folding with Tier0Strings in Maps/ and Physics/ where the Valve call is V_stricmp (low
   risk, ASCII-identical), then audit the other folders per ledger 21.

---

## 4. Visibility

Folders: `Maps/Vis*.cs`, `VoxelVisibility*.cs`, `RayTraceEnvironment.cs`, `TracerKd.cs`, `TraceScene.cs` (the
.rte), `Containers/VisibilityAuthor.cs`. Docs: VISIBILITY.md, GEOMETRY.md (.rte), GROUND_TRUTH 27-29, 51, 123, 127.

### 4(a)

| # | where | rule | status |
|---|---|---|---|
| 4.a1 | `Maps/VisOutput.cs:52`; `VoxelVisibility.cs:74-78` | PVS row stride `((clusters + 31) >> 5) * 4` | The writer line has no citation; the reader's copy is "Measured over 112 maps" (**unclear** whether read) |
| 4.a2 | `Maps/VisClusters.cs:110-118`, `:376-378` | `Uniform`, a closed-form shortcut for the cluster count | Diagnostic only (not on the output path); "identical ... on every map measured" |
| 4.a3 | `Maps/VisClusters.cs:160-166`; ledger 28 | Split hints path | Read; since measured on s2c_vishints (ledger 127), so the comment is stale |

### 4(b)

| # | where | what | reach |
|---|---|---|---|
| 4.b1 | `Maps/VisOutside.cs:271`, `:301` | MarchRay reaching a leaf: CellMask (18010e0d0) and the solid records' unwritten status bytes | map, **unclear** how often (no corpus map hits it) |
| 4.b2 | `Maps/VisConfig.cs:58`, `:104` | A visibility_hint, or a sun, inside an instance | map |
| 4.b3 | 1.b13 | RTE skip boxes, the oversize-mesh skip, entry flag 0x80 | map, none known |
| 4.b4 | VISIBILITY.md:83-91 | `-oldvis` / DeterministicBuild 0 (Sampler_Original) | option; Valve's own output is not reproducible under it |

### 4(c)

| # | output | source | kind |
|---|---|---|---|
| 4.c1 | `-oldvis` vvis | VISIBILITY.md | variance (three Valve runs, three files) |
| 4.c2 | Per-CPU estimates: Valve's vis is the compiling CPU's (rcpps/rsqrtps); S2C_ESTIMATE=zen4 reproduces the Zen 4 references | ledger 54 | variance (machine) |
| 4.c3 | PVS scan speed (about 90 of 113 min on s2c_prefabprobe4) | HANDOFF roadmap 8, 12 | not a parity item (any byte-changing speed-up needs the user's OK) |

### 4. Verdict

At parity for the deterministic build: byte-identical VXVS from the .vmap on five maps including s2c_prefabprobe4
and split hints. Only refusals remain (4.b1, 4.b2), plus the .rte gaps shared with chunk 1 (4.b3).

Next steps:
1. **4.b1**: port CellMask (18010e0d0) and find what the solid records' status bytes hold when unwritten (a capture
   of one map that reaches a leaf; first find such a map by running the vis port over the corpus .vmaps offline).
2. **4.b2**: port the instance placement for hints and suns (the instance matrix as the parent, as VisConfig's
   comment says), with a probe map compiled by Valve.
3. **4.a1**: cite or read the stride in visbuilder's output writer.

---

## 5. Entities and the entity lump

Folders: `Maps/EntityLumpAuthor*.cs`, `EntityLumpSet.cs`, `MapEntities.cs`, `MapEntityList.cs`, `MapInstances.cs`,
`MapPrefabs.cs`, `MapWorldOrder.cs`, `FgdSchema.cs`, `TemplateTransform.cs`, `MapNodeAngles.cs`, `MapCompile*.cs`,
`GameContent.cs`; the light keys' trace (`Light*.cs`, `EditorTraceScene*.cs`, `BakedShadowAssignment.cs`,
`ProbeAtlas.cs`). Docs: ENTITIES.md, SETTLE.md, GROUND_TRUTH 22, 24, 26, 31-33, 46, 48, 52, 56, 102, 106, 122, 164,
177-183, LIGHTING_ROADMAP L17, L20.

### 5(a)

| # | where | rule | status |
|---|---|---|---|
| 5.a1 | `Maps/EntityLumpAuthor.Lighting.cs:247-260` | Prefab light gather order: documents breadth first, a placement's lights in walk order except the document's first placement, last to first | Ledger 46: **fitted** to probes 7, 9, 11, 12; MapDoc_GatherLights (180f1c660) and the referrer list (newest first, 180cdf6f0) read, the load order of the referencing attributes and the +0x6b8 appender not |
| 5.a2 | ENTITIES.md:129-136, `Entity.KeysForward` | A repeated prefab placement's keys flipped (as copying the node from the one before would) | Ledger 46: from output only; ExportToLump (181004020) read, the node copy not |
| 5.a3 | ENTITIES.md:108-112 | An instance copy's undeclared keys in reverse template order | Ledger 32: the head insertion (FUN_180ce08f0) read, the copy loop not |
| 5.a4 | `Maps/MapEntities.cs:416-421` | One extra compile_source_id before a prefab's contents, taken as the prefab map's world | "Whether it is that world or the prefab node is not read" |
| 5.a5 | `Maps/EntityLumpAuthor.cs:16-18` | The class header: "Every rule below was measured against resourcecompiler output rather than inferred" | Provenance per rule is mixed: typing is RC's table (EntityKey_TypeValue), others are measured. **unclear** per rule; the measured ones found are 5.a6-5.a10 |
| 5.a6 | `Maps/EntityLumpAuthor.cs:242-246` | cable_dynamic writes rendercolor "%i %i %i" from its tint | Measured on probe_cable (CMapCable::vf217 named) |
| 5.a7 | `Maps/EntityLumpAuthor.cs:674-678` | KV3 text arrays: one line up to four entries, else four a line under tabs | Measured on ropes of 4 to 7 nodes |
| 5.a8 | `Maps/EntityLumpAuthor.cs:595-601` | A path's world-to-local rotation | Only the zero-rotation case is measured |
| 5.a9 | `Maps/EntityLumpAuthor.cs:445-453` | pathNodeColors | Its getter not traced to an attribute; never written by the port |
| 5.a10 | `Maps/EntityLumpAuthor.cs:755-762` | Placements as double arrays, not six-decimal strings | Measured on a current compile |
| 5.a11 | `Maps/FgdSchema.cs:278-284` | A @SolidClass with no brushes is not compiled | Measured (ze_doom_p2_c_gameplay's 43) |
| 5.a12 | `Maps/MapWorldOrder.cs:18-26` | The world's node array after the instance collapse | Read and simulated; s2c_prefabprobe3 a few nodes in another order from 2463, s2c_prefabprobe4's prefab map one off from PrefabSlots on 697 nodes (L20) |
| 5.a13 | Ledger 33 | Static props' ray triangles built from render buffers (draw calls then indices) | Exact on every atixref barn light; meshsystem's TraceData fill (TraceDataForDraw_t) not read |
| 5.a14 | Ledger 31 (2026-10-09 tail) | Where RayScene_AddFace (1813ccd20) picks a face's first corner | Not read; 130 of 172,032 hits of light 1099 off in the last bits, no key changes |
| 5.a15 | `Maps/EntityLumpSet.cs:48-56` | A particle snapshot key appended when absent | Only the key-present case is measured |
| 5.a16 | Ledger 48 / 164 | Whether a world mesh's lightingorigin carries the [PR#] fixup | Open (no specimen has a lighting origin) |

### 5(b)

| # | where | what | reach |
|---|---|---|---|
| 5.b1 | `Maps/MapEntityList.cs:150`, `:160`, `:162`, `:164`, `:194`, `:200`, `:230`, `:260` | Entity list simulation: hidden instance with no target, copied prefab whose map has instances (Valve crashes), prefab in an instance target, unloaded prefab map, a target that an instance ahead clones or moves, a target outside the map, a target deleted with another | map |
| 5.b2 | `Maps/MapWorldOrder.cs:69`, `:75`, `:176` | World node order with a prefab inside a prefab, a hidden instance, a target holding only instances | map (affects the path-trace light order, L17/L20) |
| 5.b3 | `Maps/EntityLumpSet.cs:65-66` | A particle snapshot node inside an instance | map |
| 5.b4 | `Maps/EntityLumpSet.cs:391`, `MapEntities.cs:176` | A CMapNavLink copied inside a prefab's map; a nav link attribute of an unmeasured type | map |
| 5.b5 | `Maps/MapEntities.cs:371` | An editorOnly node with children | map |
| 5.b6 | `Maps/GameContent.cs:243` | A skeleton without m_boneScaleParent | map (older models) |
| 5.b7 | `Maps/MapCompile.cs:322` | A lump whose lights or probe volumes would lose their bake keys (unless --accept-gaps) | map |
| 5.b8 | `Maps/BakedShadowAssignment.cs:159-184` | Shadow slot shapes other than the ported ones; a light with no falloff range (point hull 180f48520) | map |
| 5.b9 | `Maps/LightPow.cs:17-32`, `LightCrt.cs:27`, `:41` | powf / tanf special paths | internal unless a key holds such a value |
| 5.b10 | ENTITIES.md:18-22 | func_movelinear and info_particle_system: their outputs outside the lump (a movable nav mesh, a .vsnap) | map |

### 5(c)

| # | output | source | kind |
|---|---|---|---|
| 5.c1 | Settle poses on the `UnstableSettle` maps | ledger 52 | variance |
| 5.c2 | `precomputed_vis_clusters` missing when the test runs no vis | ENTITIES.md:73-76 | test allowance, not an output gap (compile-map runs vis) |
| 5.c3 | Prefab light order (5.a1) and key flip (5.a2): exact on every probe but fitted | ledger 46 | **unverified** provenance; no known byte difference |
| 5.c4 | s2c_prefabprobe3/4 world node order (5.a12) | L20 | gap (path-trace light order on those maps) |
| 5.c5 | Light 1099's 130 hits in the last bits (5.a14) | ledger 31 | gap without a key change (no output byte known to differ) |

### 5. Verdict

Every lump of every pinned map and probe matches, so no known lump byte differs. A 100% claim is blocked by two
fitted orders (5.a1, 5.a2), a reversed-key rule whose loop is not read (5.a3), and a class whose own header says its
rules were measured from output (5.a5); the reach gaps are the entity list refusals (5.b1, 5.b2).

Next steps, by maps affected:
1. **5.a3** (every map with instances, which is most ZE maps): read the instance copy loop that walks the template
   (CMapInstance_Collapse 181017820's key copy).
2. **5.a5**: walk EntityLumpAuthor rule by rule and add the address or ledger entry each comes from; the ones with
   neither go to GROUND_TRUTH (5.a6-5.a10 found so far).
3. **5.a1 / 5.a2** (maps with prefabs holding lights or placed twice): read when each placement's referencing
   attribute is set during the load (the DmAttribute setters 180ce5810, 180d01160, 180d03430, 180d19000 from the
   map load) and the +0x6b8 appender; read the node copy behind the key flip.
4. **5.a12 / 5.c4** (prefab maps): MapWorldOrderTests against the captures in %TEMP%/entitysearch_capture.
5. **5.b1, 5.b2**: probe maps for each refused layout, compiled by Valve (s2c_prefabprobe pattern).

---

## 6. Own lighting (the vrad3 port and the GPU bake)

Folders: `Lighting/**`, `Source2.Compiler.Gpu/OwnLightingBake.cs`, `RtxBake*.cs`, `ComputeOracle.cs`,
`GpuMaterialSampler.cs`, `ValveProgram.cs`, `VfxExpression.cs`, `ProgramRunner.cs`. Docs: VRAD3.md, BAKED.md,
LIGHTING_ROADMAP.md, GROUND_TRUTH 62-90, 105, 108, 110-116, 184, 197, 201, 210.

### 6(a)

| # | where | rule | status |
|---|---|---|---|
| 6.a1 | `Lighting/Bake/RtxMaterials.cs:842` | A material block ends at the next multiple of 16 past the last write | Measured (348 gives 352, 432 gives 448) |
| 6.a2 | `Lighting/Bake/RtxMaterials.cs:902-905` | A 4x4 is written transposed | Measured on vrad3's g_mTextureColorAdjust1 |
| 6.a3 | `Lighting/LightmapImage.cs:129-133` | lightmap_image_save's float to half rounds ties away from zero | Measured on ze_hold_em_p's raw image |
| 6.a4 | `Lighting/Bake/LightmapBaker.cs:98` | The records' initial values (chart id 0xffffffff ...) | Measured on vrad3's raw image |
| 6.a5 | `Source2.Compiler.Gpu/RtxBakeHost.cs:1011` | Every generation starts from an empty buffer | Measured |
| 6.a6 | `Source2.Compiler.Gpu/RtxBakeRaster.cs:16` | D_REVERSE_DEPTH 0 | rendersystem's choice not read |
| 6.a7 | `Source2.Compiler.Gpu/RtxBakeHost.cs:24` | An enum used by the host | "not yet read, see the ledger" |
| 6.a8 | `Lighting/Bake/BakeScene.cs:76` | path_trace_light_flags 1 when ibl is null | Measured |
| 6.a9 | `Source2.Compiler.Gpu/VfxExpression.cs:218-220` | A texture we cannot read evaluates to zero | Not measured |
| 6.a10 | `Lighting/LightmapAmeliorate.cs:338` | Cleared texels: sample word 0, flags 0xfffffff8 | Measured on probe01 |
| 6.a11 | `Lighting/LightmapTextures.cs:172-186` | direct_light_shadows with no shadow: only an all-1 first channel is taken | The texture compiler's downscale to 4x4 not read |
| 6.a12 | `Lighting/LightmapAhd.cs:191`, `LightmapPost.cs:495` | The 1e-17..1e17 rescue approximated in double; MathF.Log for tier0's V_logf | Not met on real data / the script never reaches it |
| 6.a13 | ledger 197, CS2_CLOSED_QUEUE A.3 | Colour adjustment (contrast other than 1, coloured tints) in the bake's material blocks | Read from disassembly; nothing measured yet (1,120 stock materials have a non-default adjustment) |
| 6.a14 | `Lighting/Bake/BakeMaterial.cs:15-25`, `:67-70` | The CPU bake's surface is the default hit group's, a layered shader's first layer standing in | CPU bake only (the GPU bake runs Valve's SPIR-V); the csgo hit groups are not ported there |

### 6(b)

| # | where | what | reach |
|---|---|---|---|
| 6.b1 | `Source2.Compiler.Gpu/RtxBakeHost.cs:113-130`, `Lighting/Bake/RtxMaterials.cs:677-679` | A material whose rtx combo is not transpiled: the GPU bake refuses ("no hit group"), the CPU bake stands in the default hit group | map: any material with an untranspiled combo |
| 6.b2 | `Lighting/Bake/RtxMaterials.cs:741-762` | LightSim_Albedo in the default block; a colour binding the material does not set (shader default); a translucent material's default opacity | map |
| 6.b3 | `Lighting/Bake/PathTracer.cs:415` | Subsurface (material type 2) | map |
| 6.b4 | LIGHTING_ROADMAP.md:47-50, M4 | `lpv_type sh2` (LPV SH paths) | map (worldspawn key) |
| 6.b5 | `Lighting/SceneLights.cs:190`, `:259`, `:261`, `:264`, `:475`; `Bake/Light.cs:228` | A light cookie's texture index; a sky light with no env_sky; sky_lightingonly_name; a tinted env_sky; other luminaire tables | map (ze_hold_em_p has no sky and passes, so 6.b5's sky refusal is for a sky light without env_sky) |
| 6.b6 | `Lighting/PathTraceProps.cs:57`, `:105`, `:21-29` | Clutter instances, a lodlevel, deformers, per-draw tint overrides, draw records flagged 0x800 in the path-trace scene | map |
| 6.b7 | `Lighting/ChartMeshes.cs:101-103`, `LightmapCharts.cs:103` | A shared (instanced) lightmap chart mesh; charts without mesh uvs (box projection) | map |
| 6.b8 | `Lighting/LightmapAmeliorate.cs:57`, `:287` | Ameliorate `-mips` above 1; the repack iterations past the free lines | option (`-lightmapVRadQuality 1/2`); no measured map reaches the repack |
| 6.b9 | `Lighting/LightmapTextures.cs:186` | 6.a11's case with values over 1 or several mips | map |
| 6.b10 | `Lighting/Bake/SceneVertexLayout.cs:130`, `:176`, `:192`, `:215`, `:293`; `MeshGeometry.cs:95`, `:123`, `:144` | Blend weight/index layouts, other vertex formats, unnamed RGBA8 texcoords, extra fields without semantics | map (skinned or unusual prop meshes in the scene) |
| 6.b11 | `Lighting/LightmapNodeBake.cs:32`, `Vrad3Job.cs:74`, `:149`, `LightmapSettings.cs:90` | Charts that do not pack; other classes in the path-trace scene; a non-power-of-two resolution | map / option |
| 6.b12 | `Lighting/Bake/LightmapBaker.cs:153`, `:190` | The adaptive pass count on the CPU | internal (the GPU bake has it) |
| 6.b13 | COVERAGE.md | `-lightmapDisableFiltering` and `-disableLightingCalculations` partly read; `-lightmapDeterministicCharts`, `-gpuraytracing` not read; `-vrad3LargeBlockSize` recorded but the job always writes 1024 | option |
| 6.b14 | LIGHTING_ROADMAP.md:50 | AMD GPUs (the bake needs Valve's RTX pipelines) | machine |

### 6(c)

| # | output | source | kind |
|---|---|---|---|
| 6.c1 | Lightmaps differ by GPU driver in Valve's own compile (70% of ze_hold_em_p's irradiance blocks moved with one NVIDIA update); ours equals Valve's on the same driver | CS2_CLOSED_QUEUE part C | variance (machine) |
| 6.c2 | Ameliorate FillBlocks ties Valve breaks with a stack value of its dispatching thread (2 of 113 tied blocks on ze_hold_em_p) | L14, ledger 114 | variance |
| 6.c3 | Far field dilate: Valve's sums are never cleared, so its runs differ; ours matches 92 of 132 slices of one run | L5, ledger 116 | variance, **unverified** (ours must be one of Valve's values; 40 slices are not shown to be) |
| 6.c4 | cardtest probe volume 471278508: 1 to 3 of 8,064 texels per band | L19 | gap |
| 6.c5 | s2c_coloradjust's three lightmap textures | ledger 210 | gap (cause not known: colour matrices, surface properties or the streams) |
| 6.c6 | -s2cbake on more maps (cardtest, the prefab probes, c2m2, atixref): packages not compared | L16 | **unverified** |
| 6.c7 | LPV ambient cubes on ze_hold_em_p "1.12 to 1.22x" | LIGHTING_ROADMAP.md:33-36 | **unclear**: an old CPU-bake note; the GPU bake's 16 probe EXRs are byte for byte (same doc, line 21) |

### 6. Verdict

The GPU bake is at parity on the maps measured (ze_hold_em_p's lit package 55 of 56, the .vwrld stamp aside, on the
same driver), because it runs Valve's SPIR-V. What blocks 100%: a measured material-block layout (6.a1, 6.a2) and
half rounding (6.a3) on the host side; untranspiled hit groups refusing (6.b1); unported vrad3 features (6.b3-6.b7);
two small known gaps (6.c4, 6.c5); and the bake not yet compared on more maps (6.c6).

Next steps, by maps affected:
1. **6.c6** (every lit map): run -s2cbake on cardtest, c2m2 and atixref against fresh same-driver vrad3 references
   (CS2 open, heavy only).
2. **6.a1 / 6.a2** (every lit map): read vrad3's material block writer (the RTX material data upload in vrad3's host,
   next to 18009b330's hit group setup) for the size rounding and the matrix layout.
3. **6.b1** (maps with uncommon materials): enumerate the rtx combos the corpus materials select and transpile the
   missing ones (tools/re/glsl_to_cs.py).
4. **6.c5 / 6.a13**: CS2_CLOSED_QUEUE A.3 (Valve's compile of s2c_coloradjust with the vrad3 job kept).
5. **6.a3, 6.a4**: read lightmap_image_save's half conversion and the record initialiser in vrad3.
6. **6.c4**: diff one band of probe volume 471278508 texel by texel against the capture.

---

## 7. Baked outputs: nav, bomb damage, cubemaps, Steam Audio

Folders: `Maps/NavCompile.cs`, `NavFile.cs`, `Maps/NavGen/*`, `Maps/BombDamage.cs`, `Cubemaps/*`,
`Source2.Compiler.Gpu/Cubemap*.cs`, `GpuCubemapBlur.cs`, `GpuTextures.cs`, `SceneDrawRunner.cs`, `SteamAudio/*`.
Docs: NAV.md, CUBEMAPS.md, STEAM_AUDIO.md, BAKED.md, GROUND_TRUTH 57, 81, 91-104, 107, 109, 117-121, 137-157,
159, 162, 163, 170-176, 186, 187, 192-199.

### 7(a)

| # | where | rule | status |
|---|---|---|---|
| 7.a1 | CUBEMAPS.md:1256-1270 (item E) | Binding numbers (cbuffer slot, texture 30+, UAV 158+, sampler 14+); D_COMPRESSED_NORMALS_AND_TANGENTS by the bound NORMAL; depth passes' stream 255; S_SHADER_QUALITY 1; a face's passes, clears and target sizes; BakedLightingConstantBuffer_t's lightmap size; the instance and transform buffers' layout ("output-equivalent, not the allocator's") | Measured on captures, engine code not read |
| 7.a2 | GROUND_TRUTH 147, 148, 151, 156, 157, 159, 162, 163, 184, 185, 193-196 | Many cube-face inputs marked "MEASURED, not read": the sky object's rotation and tint (only angles 0 and white seen), anisotropy 4, the cull record flags = +0x80, the fragment 0x20 bit (CPVS walk not read), light_environment fades with no key (0.1 / 0.05), the unbaked probe record's measured parts, the barn light screen-size formula, Csgo +68's writer | Measured |
| 7.a3 | CUBEMAPS.md:1214-1216 | The game's cube order in the .vtex | Not read; FromPackage's order equals the EXR name order on probe01 |
| 7.a4 | STEAM_AUDIO.md:224-226 | The hemisphere sample generator (table matches, function not located); the surface property system's order (matches the vsurf list, not read) | Measured |
| 7.a5 | NAV.md:68-70 | Which local maps set CreateMovableNavMesh | Not checked |
| 7.a6 | `Maps/NavCompile.cs:99` | Bomb damage power default "3000" when the FGD has no default | No citation (**unclear**; the FGD normally supplies it) |
| 7.a7 | `Maps/NavCompile.cs:20-28`; ledger 57 | The .nav's custom data from / to / order ids are heap pointers in Valve's file; ours the area and spot ids | Valve variance by construction; see 7.c1 |

### 7(b)

| # | where | what | reach |
|---|---|---|---|
| 7.b1 | `Maps/NavCompile.cs:43`, `NavGen/NavWriter.cs:75` | Movable nav meshes (func_movelinear / func_mover with CreateMovableNavMesh, point_nav_movable_mesh, prop_deformable) | map: **ZE maps with moving platforms** (judgement: common when the key is set) |
| 7.b2 | NAV.md collectors 3, 4, 6, 8, 10 | NONAV blocks from prop_deformable (181352fb0, 1818e0800 unread), oriented and projected generation (info_gravity_shift, info_nav_oriented*), AVOID from models, info_nav_space, oriented debug clips | map |
| 7.b3 | `NavGen/NavGenLadders.cs:60`, `NavGenMarkups.cs:78`, `:125`, `NavGenMapInput.cs:30`, `:91`, `:98` | A func_ladder or func_nav_markup inside an instance or prefab; a scaled markup mesh; walkable seeds under a deformer, a moving group, hidden in an instance | map |
| 7.b4 | `NavGen/NavAnalysis.cs:530`, `:532`, `NavAnalysis.CustomData.cs:21`, `NavAnalysis.Hiding.cs:310`, `NavMeshOverlaps.cs:122`, `:208`, `:210`, `NavSphereWorld.cs:56`, `:58`, `:63`, `:99`, `:147`, `NavBoxWorld.cs:79`, `:218`, `NavGenClip.cs:272`, `NavGenInput.cs:120`, `:213`, `:249`, `NavJumpSweepWorld.cs:55`, `:92`, `:205`, `NavMeshLadders.cs:143`, `NavWriter.cs:102` | Analysis and generation paths: portal kinds, the survival play area, sniper spots with no area, more than 32 overlaps, sphere casts against SVM or scaled hulls, rays past the world limit, casts against other shape types, JoinNonZup | map |
| 7.b5 | `NavGen/NavGenPipeline.cs:100`, `NavGenSettings.cs:143`, `:191` | Several hulls; NavAgentNumHulls; settingsAgentHullsVDataOverride | gameinfo (NavAgentNumHulls) / map (a vdata override, **unclear**) |
| 7.b6 | `Maps/NavCompile.cs:108`, `:114`, `:117`, `:133`; `BombDamage.cs:157`, `:248` | Bomb damage: a target in an instance or prefab, with no mesh, not an axis-aligned box; a world with hulls, spheres or capsules; a nav area with no polygon; a site with no sampled cell | map: defuse maps only (needs func_bomb_target) |
| 7.b7 | COVERAGE.md | `-navdbg`, `-gridnav` not started | option |
| 7.b8 | `Source2.Compiler.Gpu/CubemapFaceRenderer.cs:688`, `:692`, `:752`, `:940`, `:1006`, `:1089`, `CubemapFacePipelines.cs:87`, `:138`, `CubemapFaceSamplers.cs:85`, `:107`, `CubemapFaceVertexLayout.cs:69`, `:86`, `GpuTextures.cs:152` | Engine samplers not measured, a baked light probe volume beyond the default probe texture's record, other binding kinds, formats, blends, address modes | map |
| 7.b9 | CUBEMAPS.md F.2-F.4, B.1, D | Non-aggregate scene objects (entity models: func_door, func_breakable, func_physbox, func_tracktrain), AO proxies, light_omni2 and cookie lights in the binner, shadowed lights' own views, the viewmodel, the team intro agents | map (every map with brush entities has 7.b9's first item) |
| 7.b10 | `Source2.Compiler.Gpu/CubemapBuild.cs:49` | EnvironmentMapRenderSize other than the renderer's 1024 | gameinfo |
| 7.b11 | STEAM_AUDIO.md:184-231 | Scene from instances, prefabs, entity models, smart props, cables, steamaudiogeometry faces, per-mesh mode, hidden nodes; probe filters (nav mesh, ladders, missing nav areas, exclusion volumes, ziplines, placed probes, snd_opvar_set_point); other generation types; sampled visibility; coincident probes; ambisonics above 1 | map |
| 7.b12 | `Maps/MapCompile.cs:135-153`; STEAM_AUDIO.md item 7 | `-sacustomdata` (occlusion, dimensions, materials) at Auto; Cleanup over custom data files; reverb compression and clustering; `-steamaudio_gpu`; `-sareverb_threads` above 1 | map (rebake option Auto) / option |
| 7.b13 | STEAM_AUDIO.md:226 | Non-FMA CRT paths and SSE2 / SSE4 / AVX ISPC kernels | machine (Valve's numbers differ there too) |

### 7(c)

| # | output | source | kind |
|---|---|---|---|
| 7.c1 | The .nav's AI data varies between two Valve compiles of probe01 (9,580 vs 9,590 bytes); its ids are heap pointers | ledger 57 | variance (but "ours is one of Valve's values" cannot hold for pointer bytes; the rule needs a decision for this file) |
| 7.c2 | s2c_coloradjust's .nav (9,579 vs 9,607 bytes) | ledgers 201, 210 | **unverified** (not checked against a second Valve run) |
| 7.c3 | ze_hold_em_p cubes 0-6, faces 0 and 1: 16 to 34 pixels each (the team intro's skinned agents, their AO splat in RenderDoc runs) | CUBEMAPS.md F, CS2_CLOSED_QUEUE B.2 | gap (needs a build-state decision, roadmap 0e) |
| 7.c4 | s2c_l5probe's -X face: 16,114 halves where the pawn's viewmodel draws; cube 1 captured, not compared | CUBEMAPS.md B | gap / unverified |
| 7.c5 | atixref: face 0 not rendered against its readback yet; layer flags differ (object +0x80 bit 41; two attributes 4294967295 vs -1) | CUBEMAPS.md A, C.1 | gap |
| 7.c6 | Animated maps (atixref): Valve's build varies; with every pin, 135,760 pixels between two runs not localised | CUBEMAPS.md D, ledger 187 | variance; ours must land inside Valve's range, **unverified** |
| 7.c7 | atixref draw 2305: one half-float ulp on 38 pixels (pipeline libraries) | ledger 121 | gap |
| 7.c8 | `.sareverb` probe 65: an Embree BVH8 tie (60 of 1,200 values) | ledger 170, STEAM_AUDIO.md item 5 | gap |
| 7.c9 | `.sareverb` with rdtsc seeds and several threads | STEAM_AUDIO.md:58-70 | variance (references are pinned, one thread) |
| 7.c10 | The nav coverage appender's random points (tier0's time-seeded stream) | ledger 186 | variance; a specimen where a random point survives is still owed (CS2_CLOSED_QUEUE A.6) |

### 7. Verdict

Nav (from the .vmap through the CS analysis), bomb damage, probe01's cubemaps and Steam Audio's pathing are exact on
their specimens. Blocking 100%: the nav's movable meshes and the unported collectors (7.b1, 7.b2); the cube render's
measured engine rules (7.a1, 7.a2) and its missing object kinds (7.b9, 7.c3-7.c5); Embree's tie (7.c8); and two
places where Valve's own output cannot be "one of Valve's values" byte for byte (7.c1's pointers, 7.c6's animated
cubes), which need a written pass rule.

Next steps, by maps affected:
1. **7.b9** (every map with brush entities in a cube's view): port non-aggregate scene objects (CBaseSceneObjectDesc /
   CAnimatableSceneObjectDesc draws, CUBEMAPS.md A.4's inventory).
2. **7.b1** (ZE maps with movers, judgement): check which corpus maps set CreateMovableNavMesh (7.a5), then read
   180fb5ce0 / 180fb5020 and capture a specimen.
3. **7.a1** (every cube): read rendersystemvulkan's descriptor layout builder for the binding numbers, and
   meshsystem's draw setup for the normal-compression combo.
4. **7.c1**: decide with the user how the .nav's pointer fields pass (mask them, or compare structure).
5. **7.c3 / 7.c4**: the build-state decision (roadmap 0e), then CS2_CLOSED_QUEUE B.2 / B.3.
6. **7.b11** (every map with Steam Audio Auto and instances or entity models): 180f88460's instance paths for the
   acoustic scene, then the probe filters in 180100e40's order.
7. **7.c8**: port Embree's BVH8 FastSpatialSAH build order for ties (STEAM_AUDIO.md item 5).

---

## 8. Resource compiler types: textures, materials, models, scripts

Folders: `Building/ResourceBuilder.cs`, `VmatParams.cs`, `SvgSanitizer.cs`, `Texture/*` (`Texture/Rc/*` are the
Valve-parity encoders), `Containers/MaterialAuthor.cs`, `ScriptAuthor.cs`, `EntityModelAuthor.cs`,
`Kv3/Kv3SourceCompiler.cs`, `Imaging/*`; `Maps/EntityRenderModels.cs`, `Physics/EntityPhysicsModels.cs`. Docs:
RESOURCES.md, GROUND_TRUTH 20, 110, 111, 200. Note: memory records that material and texture parity work moved to the
separate source2-assets repository; that repository is outside this audit.

### 8(a)

| # | where | rule | status |
|---|---|---|---|
| 8.a1 | `Building/ResourceBuilder.cs:1057-1068`; ledger 20 | vmat_c DATA field order | Measured from stock dumps ("likely NTRO positional"); the loader's reason not read |
| 8.a2 | `Containers/MaterialAuthor.cs:15`; `ResourceBuilder.cs:538`, `:571` | Material container (RERL RED2 DATA INSG), texture versions, sheet flags | Measured invariant across 200 stock samples |
| 8.a3 | `Texture/SheetAtlas.cs:19` | Sheet atlas layout | Measured on Valve's explosion_blast_01_flame |
| 8.a4 | `Containers/ScriptAuthor.cs:5-14` | .vjs_c: version 4, RED2 then DATA, source byte for byte, identities | Measured on cs_script_demo's seven scripts with the 1.41.9.4 tools (ledger 200); equal byte for byte |
| 8.a5 | `Texture/Bc7Native.cs:46-52` | BC7 through bc7enc at uber level 1 / 16 partitions, chosen by a PSNR A/B | **Not Valve's encoder**: a quality/speed choice, not a port (see 8.c2) |

### 8(b)

| # | where | what | reach |
|---|---|---|---|
| 8.b1 | HANDOFF roadmap 10 | Compiling the addon content a map references (materials, textures, models): compile-map still needs Valve's compiled assets | map: every map with custom content |
| 8.b2 | HANDOFF roadmap 10 | Model compiles from ModelDoc sources, and shaders: not ported | content |
| 8.b3 | `Containers/ScriptAuthor.cs:40` | A .ts source | content (a user's RC refuses it too: parity) |
| 8.b4 | `Containers/ScriptAuthor.cs:10-14` | The Panorama preprocessor pass (comments and Console.Log blanked) | Valve's build machine only (no user install has panorama/preprocessor_config.txt) |
| 8.b5 | `Texture/Rc/Bc6hHueShift.cs:92`, `:324` | Hue-shift BC6H of an image whose sides are not multiples of 4; k-means with a cluster per pixel | internal / map (lightmaps are multiples of 4) |
| 8.b6 | `Physics/EntityPhysicsModels.cs:19`; `Source2.Compiler.Cli/Commands.cs:454-457` | Comments still say entity render meshes are not ported (EntityRenderModels now writes them in compile-map; the separate CLI command still writes physics-only models) | doc/command staleness |
| 8.b7 | RESOURCES.md:32-37 | G2: a vpcf26 source is not migrated to the current particle schema; G3: an unresolvable renderer material reference is kept | content |
| 8.b8 | COVERAGE.md | `-maxtextureres`'s effect on texture output; `suppress_texture_compiles`, `permissive_panorama_compiles`, `compress` not read | option |

### 8(c)

| # | output | source | kind |
|---|---|---|---|
| 8.c1 | Single resources: "decoded-tree identity, not byte identity" is the stated contract (vsndevts, vdata, vpcf, vtex, vsnd, vsvg) | RESOURCES.md:9-19 | gap against the bit rule |
| 8.c2 | Textures from `ResourceBuilder.BuildTexture` use BCnEncoder / bc7enc / ispc_texcomp, not Valve's texture compiler; they also carry a METADATA extra-data entry stock textures lack | `Bc7Native.cs`, RESOURCES.md:73-74 | gap (this path cannot be byte exact). The Valve-parity encoders (`Texture/Rc`: bc7e, BC6H hue shift, ATI) serve lightmaps and cubemaps only |
| 8.c3 | vpcf: RERL superset (G3), DATA differs (G2) | RESOURCES.md:19 | gap |
| 8.c4 | A pre-1.41.9.2 .vjs_c reference | CS2_CLOSED_QUEUE A.4, ledger 200 | **unverified** |

### 8. Verdict

Scripts are byte exact against a user's resourcecompiler. Everything else in this chunk is an authoring path for
mod content, built on VRF's serializer with its own encoders, whose stated contract is decoded-tree identity; it is
not a resourcecompiler port and cannot be bit exact as it stands (8.c1, 8.c2). The map compile's own reach gap is
8.b1: a map's addon content is still Valve-compiled.

Next steps, by maps affected:
1. **8.b1** (every map with custom content): decide whether this repo or source2-assets owns material and texture
   compiles (memory: parity work landed there), then wire that compiler into compile-map.
2. **8.c1 / 8.c2**: either restate the single-resource scope as "not part of the bit-parity port" or move each type
   onto Valve's encoders (resourcecompiler's texture compile path, the same bc7e build Texture/Rc uses).
3. **8.a1**: read the vmat_c DATA writer (CompileMaterial) for the field order.

---

## Cross-cutting: docs that no longer match the code

These do not change bytes but will mislead the next reader of chunk 1 and 2:
- `docs/GEOMETRY.md:8` "Only the first two are ported" (world nodes are ported); `:128-129` RTE triangle order "ours is
  walk order" (settled, ledger 29); `:207-216` the T-junction fix "not ported yet" (`Maps/TJunctionFix.cs`, 180259d30);
  `:307-311` the -world merger "on six grid boxes ... the code is not found" (read: 180283520, ledger 55);
  `:503-523` texcoord formats "not settled" (settled, ledger 44); `:525-528` the open list.
- `Maps/MapGeometry.cs:21-23` "Not yet covered: a face with subdivision levels" (TraceScene bakes subdivision).
- `Maps/VisClusters.cs:160-166` split hints "not measured" (measured, ledger 127).
- `docs/SETTLE.md:36-42` joints "not yet wired into the world" (ledgers 158-168 wired them).
- `docs/PHYSICS.md:140` "SettleWorld.Run still refuses a map with an aggregate" (steps 6 and 7 are done).
- `Physics/EntityPhysicsModels.cs:19-22` and `Source2.Compiler.Cli/Commands.cs:454-457`: render meshes "not ported".
- GROUND_TRUTH 54's open "the instanced list's vf 0x38 ... not modelled": `WorldNodeCompile.CompileInstanced`
  (`:465-474`) models the hash table now; the ledger entry was not updated. Ledger 35 (probe01's four dev meshes)
  is probably closed by ledger 55's byte-exact probe01, but no entry says so.
- `docs/LIGHTING_ROADMAP.md:33-36` LPV ambient cubes "1.12 to 1.22x" (pre-GPU note).

## Summary

| chunk | (a) | (b) | (c) | top blockers |
|---|---|---|---|---|
| 1 World geometry | 21 | 19 | 11 | measured transform / renormalise / field order (1.a1, 1.a3, 1.a9); agg_prop and overlay tangents (1.c3, 1.c2); subdivided tangents (1.c1) |
| 2 Physics | 11 | 12 | 3 | measured shader translucency tables feeding collision (2.a1); the 0.5 stand-in without the GPU sampler (2.b1); fix3_geo_all's LZ4 byte (2.c1) |
| 3 Containers | 10 | 6 | 3 | VPK packer not read (3.a1); container facts measured (3.a2-3.a4); encoding per writer (3.a10) |
| 4 Visibility | 3 | 4 | 3 | CellMask refusal (4.b1); hints and suns in instances (4.b2); .rte skip boxes / oversize skip (4.b3) |
| 5 Entities | 16 | 10 | 5 | instance copy key loop not read (5.a3); fitted prefab light order and key flip (5.a1, 5.a2); entity list refusals (5.b1, 5.b2) |
| 6 Own lighting | 14 | 14 | 7 | -s2cbake not compared beyond ze_hold_em_p / probe01 (6.c6); measured material-block layout (6.a1, 6.a2); untranspiled hit groups refuse (6.b1) |
| 7 Baked outputs | 7 | 13 | 10 | non-aggregate scene objects in cubes (7.b9); movable nav meshes and unported collectors (7.b1, 7.b2); measured cube engine rules (7.a1, 7.a2) |
| 8 RC types | 5 | 8 | 4 | addon content not compiled by us (8.b1); single resources are decoded-tree only, non-Valve encoders (8.c1, 8.c2); vmat field order measured (8.a1) |

Rows group several call sites where they share one cause (for example 2.b8 lists 30 vphysics2 throws), so the counts
are of causes, not of lines.
