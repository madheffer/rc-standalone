# Entity-side functions: the physics settle, the entity lump, light precompute

Reference tables for the entity side of the port: what each Valve function is,
the name it carries in the shared Ghidra project, what it needs, and where its
port lives. resourcecompiler.dll is the 2026-09-23/24 build (byte identical),
vphysics2.dll the 2026-09-24 build. Names were applied as primary labels, only
to functions that were `FUN_…`; a function another area also uses is left
unnamed until both sides agree on it.

Status: **exact** means the port matches the DLL byte for byte in oracle tests
(the DLL loaded in the test process, same memory in); **mapped** means read
and understood but not ported; **open** means not read yet.

## How the pieces depend on each other

```
entity lump (CMapEntity_ExportToLump)
 ├─ FGD key tables (FgdClass_*), key typing (EntityKey_TypeValue), I/O schema
 ├─ instance bake (MapDoc_BakeInstances) ─ node ids of every copy
 ├─ template pass (TemplatePass_*)
 ├─ paths and cables (CMapPath_ExportKeys, Path_ComputeTangents)
 ├─ light precomputed keys (LightPrecompute_*) ─ needs the world ray trace scene [baked lighting]
 └─ settled props: origin, angles, "Start asleep" (MapSettle_Run)
     ├─ the physics world (PhysDoc_*): static collision from map meshes
     │    [mesh pieces and physics type: physics area], props from model PHYS data,
     │    mass from shapes, surface materials
     ├─ vphysics2: CVPhysics2Interface_SimulateWorlds → CPhysicsWorld_Simulate
     │    └─ CRnWorld_Step (2700 × 1/90 s)
     │        ├─ CRnWorld_WakeBodiesFromAppliedForces
     │        ├─ CBroadphase_PreStepQuery → CRnWorld_BuildNewContactsFromOverlappingPairQuery
     │        │    └─ RnTree_* (7 trees), RnPairFilter_CreateContact, RnCreateContact
     │        ├─ CRnWorld_Collide → CRnWorld_CollideWorker
     │        │    ├─ CRnConvexContact_Update → RnCollideHullHull(Manifold)
     │        │    ├─ CRnMeshContact_Update → RnMesh_QueryBox → RnCollideHullTriangle
     │        │    │    → RnMeshContact_ClusterNormals / MergeClusters / ReducePoints / WarmStart
     │        │    └─ CRnWorld_FlushCollideLists (destroy, touch begin/end → island manager)
     │        ├─ CRnWorld_Solve → RnSolve_Worker → RnSolve_SerialIsland → RnSolver_SolveSerial
     │        │    ├─ RnSolver_BuildBodiesAndIntegrate (RnSolverBody_Build/Integrate*)
     │        │    ├─ RnSolver_PrepareContacts (CRnContact_PrepareSolver, _PreparePoints)
     │        │    ├─ RnSolver_VelocityIteration (CRnContact_SolveVelocity, _StoreImpulses)
     │        │    ├─ RnSolverBody_IntegratePosition, _SleepTest
     │        │    ├─ RnSolver_PositionIterationContacts (CRnContact_SolvePosition)
     │        │    └─ RnSolver_WriteBackBody (CRnBody_PutToSleep, fast-mover bit)
     │        ├─ CRnWorld_SolveContinuous
     │        └─ CRnWorld_ClampToWorldBounds
     └─ PhysObj_WriteBackTransform → PhysPart_GetEntityTransform (CTransform concat/inverse,
          quaternion to angles, angle clean-up) → CMapEntity_SetStartAsleep
```

## vphysics2.dll: the Rubikon step

| address | name | status | port |
|---|---|---|---|
| 180075b60 | CVPhysics2Interface_SimulateWorlds | mapped | |
| 18006dd90 | CVPhysics2Interface_CreateWorld | mapped | |
| 180058200 | CPhysicsWorld_ctor | mapped | |
| 180059f30 | CPhysicsWorld_Simulate | mapped | |
| 1801e7df0 | CRnWorld_ctor | mapped (defaults) | |
| 180200840 | CRnWorld_Step | mapped | |
| 180203d70 | CRnWorld_WakeBodiesFromAppliedForces | mapped | |
| 1801f6980 | CRnWorld_Collide | mapped | |
| 1801ecfb0 | CRnWorld_CollideWorker | mapped | RnTransform.Of |
| 1801dceb0 | CRnWorld_FlushCollideLists | in progress | |
| 1801ff820 | CRnWorld_Solve | mapped | |
| 1801ffe80 | CRnWorld_SolveContinuous | mapped | |
| 1801faf80 | CRnWorld_ClampToWorldBounds | mapped | |
| 1801ef120 | CRnWorld_CreateBody | mapped | |
| 1801c0360 | CRnBody_ApplyDesc | mapped | |
| 1801ba3e0 | CRnBody_GetDesc | mapped | |
| 180138e70 | RnBodyDesc_t_LoadKV3 | mapped | |
| 1801be270 | CRnBody_PutToSleep | exact (replay) | IslandSolver |
| 1801b9270 | CRnBody_UpdateProxies | in progress | |

### Solver

| address | name | status | port |
|---|---|---|---|
| 180311610 | RnSolve_Worker | in progress | |
| 1803111e0 | RnSolve_SerialIsland | exact (replay) | IslandSolver.Solve |
| 180310dc0 | RnSolver_SolveSerial | exact (replay) | IslandSolver.Solve |
| 18030d370 | RnSolver_BuildBodiesAndIntegrate | exact (replay) | IslandSolver |
| 18030cc80 | RnSolver_BuildBodiesAndIntegrateJob | mapped | |
| 1803100d0 | RnSolver_PrepareContacts | exact (replay) | IslandSolver.Prepare |
| 18030fd50 | RnSolver_VelocityIteration | exact (replay) | IslandSolver |
| 180312010 | RnSolver_PositionIterationContacts | exact (replay) | IslandSolver |
| 180313090 | RnSolver_WriteBackBody | exact (replay) | IslandSolver.WriteBack |
| 180313990 | RnSolver_CopyBodyBack | exact (replay) | IslandSolver.WriteBack |
| 180312df0 | RnSolveContext_Create | mapped | |
| 180338a20 | RnSolveContext_Init | mapped | |
| 1801b6000 | RnSolverBody_Build | exact | Integrator.Build |
| 1801b7940 | RnSolverBody_IntegrateLinear | exact | Integrator.IntegrateLinear |
| 1801b7d60 | RnSolverBody_IntegrateAngular | exact | Integrator.IntegrateAngular |
| 1801c0040 | RnSolverBody_IntegratePosition | exact | Integrator.IntegratePosition |
| 1801c02a0 | RnSolverBody_SleepTest | exact | Integrator.SleepTest |
| 1801bc710 | RnSolverBody_SetOrientation | exact | Integrator.SetOrientation |
| 1801b1750 | RnSolverBody_ApplyPositionImpulse | exact | ContactSolver.ApplyPositionImpulse |
| 180292de0 | Mat3_RotateInertia | exact | RnMath.RotateInertia |
| 1801b2570 | RnClampLinearVelocity | exact | RnMath.Clamp |
| 1801b2450 | RnClampAngularVelocity | exact | RnMath.Clamp |
| 1800899b0 | Quat_Mul | exact | RnMath.Mul |

### Contacts

| address | name | status | port |
|---|---|---|---|
| 1801d3fc0 | CRnContact_PrepareSolver | exact | ContactSolver.Prepare |
| 1801d3360 | CRnContact_PreparePoints | exact | ContactSolver.PreparePoints |
| 1801d2860 | RnContactRow_PreparePoint | exact | ContactSolver.PreparePoint |
| 1801d2030 | RnContactRow_PrepareFriction | exact | ContactSolver.PrepareFriction |
| 1801d1610 | RnEffectiveMass | exact | ContactSolver.EffectiveMass |
| 1801d17c0 | RnMaterial_Mix | exact | ContactSolver.MixMaterials |
| 1801d5e60 | CRnContact_SolveVelocity | exact | ContactSolver.SolveVelocity |
| 1801d5a00 | RnContactRow_SolveFriction | exact | ContactSolver.SolveFriction |
| 1801d5880 | CRnContact_StoreImpulses | exact (replay) | ContactSolver.StoreImpulses |
| 1801d4c10 | CRnContact_SolvePosition | exact | ContactSolver.SolvePosition |
| 1801d18c0 | CRnContact_SolverSize | exact | MeshCollision |
| 1801d1390 | RnCreateContact | mapped | |
| 1801d1170 | CRnContact_ctor | mapped | |
| 180306710 | CRnOverlappingPair_ctor | mapped | |

### Narrowphase

| address | name | status | port |
|---|---|---|---|
| 180307750 | CRnConvexContact_Update | mapped | |
| 1802f2560 | RnCollideHullHull | exact | HullCollision |
| 1802f15e0 | RnCollideHullHullManifold | exact | HullCollision.Collide |
| 1802f62d0 | RnSatCache_Revalidate | exact | HullCollision |
| 18028b1c0 | RnHull_Support | exact | HullQueries |
| 18028cc00 | RnHull_FaceQuery | exact | HullQueries |
| 18028bbd0 | RnHull_EdgeQuery | exact | HullQueries |
| 1802ee580 | RnHull_FaceContact | exact | HullContacts |
| 1802ed320 | RnHull_EdgeContact | exact | HullContacts |
| 1803372b0 | RnHull_IncidentFace | exact | HullContacts |
| 180336c80 | RnHull_IncidentPolygon | exact | HullContacts |
| 1803bb310 | RnClipPolygon | exact | HullContacts |
| 1803bb850 | RnManifold_CullReduce | exact (this CPU's rsqrtps) | HullReduce |
| 1802f59c0 | RnManifold_MinSeparation | exact | HullContacts |
| 180321cc0 | RnTangents | exact | HullContacts |
| 1802f61a0 | RnManifold_TransferFriction | exact | HullContacts |
| 180321270 | RnClosestPointsLines | exact | HullContacts |
| 180305460 | CRnMeshContact_Update | exact | MeshCollision.Update |
| 1803049b0 | CRnMeshContact_QueryTriangles | exact | MeshCollision |
| 18024a4a0 | RnMesh_QueryBox | exact | MeshQuery |
| 1802ff110 | CRnMeshContact_CollideTriangles | exact | MeshTriangles |
| 1802f5c20 | RnHull_BuildTriangle | exact | MeshTriangles |
| 1802f23f0 | RnCollideHullTriangle | exact | MeshTriangles |
| 1802fe660 | RnMeshContact_ClusterNormals | exact | MeshCluster |
| 1802fd8b0 | RnMeshContact_MergeClusters | exact | MeshCluster |
| 180300d50 | RnMeshContact_ReducePoints | exact | MeshReduce |
| 180304620 | RnMeshContact_WarmStart | exact | MeshReduce |

### Broadphase

| address | name | status | port |
|---|---|---|---|
| 1802d3dd0 | CBroadphase_ctor | in progress | |
| 1802d59b0 | CBroadphase_SelectTree | in progress | |
| 1802d6de0 | CBroadphase_MoveProxy | in progress | |
| 1802d4ed0 | CBroadphase_BeginHierarchyUpdate | in progress | |
| 1802d6050 | CBroadphase_FinalizeHierarchyUpdate | in progress | |
| 1802d5740 | CBroadphase_PreStepQuery | in progress | |
| 1801f1dc0 | CRnWorld_BuildNewContactsFromOverlappingPairQuery | in progress | |
| 1802038b0 | RnPairFilter_CreateContact | in progress | |
| 1801f17e0 | CRnContact_ProxiesOverlap | mapped | |
| 1802d7380 | RnTree_Refatten | exact | DynamicTree.Refatten |
| 180334130 | RnTree_InsertLeaf | exact | DynamicTree.Insert |
| 180334330 | RnTree_FindBestSibling | exact | DynamicTree |
| 1803342b0 | RnTree_RemoveLeaf | exact | DynamicTree.Remove |
| 180335f20 | RnTree_Grow | exact | DynamicTree |
| 180336c20 | RnTree_Rebuild | exact (modes 0, 1, 3, 4) | DynamicTree.Rebuild |

## resourcecompiler.dll

### The settle

| address | name | status |
|---|---|---|
| 180f1e490 | MapSettle_Run | mapped |
| 180f1c060 | MapSettle_BuildExcludedSet | mapped |
| 180f4a620 | PhysDoc_ctor | mapped |
| 180f4b360 | PhysDoc_CollectNodes | mapped |
| 180f4c360 | PhysDoc_GetNodes | mapped |
| 180f4c9e0 | PhysDoc_BuildObjects | mapped |
| 180f4bce0 | PhysDoc_SetSimulating | mapped |
| 180f4d380 | PhysDoc_Simulate | mapped |
| 180f4c560 | PhysDoc_GetObjectsOfNode | mapped |
| 180f4b6a0 | PhysDoc_StopSimulating | mapped |
| 1810543f0 | PhysObj_ctor | mapped |
| 18105c330 | PhysObj_Build | mapped |
| 18105dbd0 | PhysPart_BuildFromModel | mapped |
| 18105b2d0 | PhysObj_IsAsleep | mapped |
| 18105b350 | PhysObj_WriteBackTransform | mapped |
| 18105a410 | PhysPart_GetEntityTransform | mapped |
| 181006ee0 | CMapEntity_SetStartAsleep | mapped |

Left unnamed because the physics area uses them too: the map mesh collision
pieces builder, the mesh physics-type resolver, and the generic CTransform and
quaternion-to-angles math.

### The entity lump

| address | name | status |
|---|---|---|
| 180fa84e0 | EntityKey_TypeValue | ported (EntityLumpAuthor.Typed) |
| 180dd08c0 | FgdClass_AddVariable | ported (FgdSchema) |
| 180dd1110 | FgdClass_Finalize | ported (FgdSchema) |
| 180dd1680 | FgdClass_MergeOverride | ported (FgdSchema) |
| 181004020 | CMapEntity_ExportToLump | ported (EntityLumpAuthor) |
| 180fc1560 | CMapWorld_ExportToLump | ported (EntityLumpAuthor) |
| 180240a60 | EntityLump_ExportNodeLate | ported (EntityLumpAuthor) |
| 18024d710 | TemplatePass_Run | ported (EntityLumpSet) |
| 18024c910 | TemplatePass_RenameCopies | ported (EntityLumpSet) |
| 1801fdae0 | TemplatePass_RewriteReferences | ported (EntityLumpSet) |
| 18024a970 | TemplatePass_RemoveOriginals | ported (EntityLumpSet) |
| 1803670b0 | EntityIOConnection_SchemaBind | ported (connections) |
| 1810b5480 | CMapPath_ExportKeys | ported (paths) |
| 1812806d0 | Path_ComputeTangents | ported (paths) |
| 180d7c3f0 | MapDoc_UpgradeTo38 | ported (MapEntities.Upgrade) |
| 180f60740 | MapDoc_BakeInstances | ported (MapInstances) |
| 180f5ff40 | MapDoc_CollapseInstance | ported (MapInstances) |
| 181017820 | CMapInstance_Collapse | ported (MapInstances) |

### Light precomputed keys

| address | name | status |
|---|---|---|
| 180f19f20 | LightPrecompute_SampleVolume | mapped |
| 180f19840 | LightPrecompute_TraceSamples | mapped |
| 180f18820 | LightPrecompute_TraceRay | mapped; needs the world ray trace scene (baked lighting area) |
