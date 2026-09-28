# Physics settle

resourcecompiler settles every prop that can move before it writes the lump
(`MapSettle_Run`). It steps a Rubikon world 2,700 times at 1/90 s and writes
each prop's origin, angles and "Start asleep" back into the entity. The port
is `src/Source2.Compiler/Simulation` plus `Maps/SettleWorld*.cs`. It runs
inside the lump build (`SettleWorld.Run` then `EntityLumpSet.Author`) in about
2 s per map.

## Status

- **Exact against Valve's lumps**, every settled prop's origin, angles and
  spawnflags:
  - atixref (81 props);
  - c2m2's prefab (63);
  - Mako (28, plus 2 pallets that start asleep).
- **Exact against vphysics2**: the whole `CRnWorld_Step`, covering:
  - integration and sleep;
  - broadphase, and hull-hull and hull-mesh contacts;
  - islands with graph colouring;
  - the solver;
  - continuous collision (GJK and TOI).
- **Round shapes:** all seven sphere and capsule narrowphase cores are
  exact (`RoundCollisionOracleTests`, 8,000 to 20,000 random cases each).

Open (GROUND_TRUTH.md 23):
- **The mesh contact for a sphere or capsule shape A:**
  - the per-type box (vfn 0x80, unpadded);
  - the bounding sphere (vfn 0xc0: a sphere's radius is unpadded; a capsule
    is its midpoint plus half its length plus the radius);
  - the per-triangle dispatch (`CRnMeshContact_CollideTriangleByType`).
- **The convex dispatch for round pairs** (table 0x1803e4200, indexed
  `A.Type * 5 + B.Type` with `A.Type <= B.Type`):
  - the GJK cache sits at contact +0xa8, the same slot as the hull pair's
    SAT cache;
  - `ContactToi` for round pairs;
  - a sphere at a non-uniform scale (s2c_settleround refuses on it).
- **Not ported:** joints (and their colouring), compound shapes, and the
  out-of-world-bounds fix-up (`CRnWorld_ClampToWorldBounds`).

## How it is built

Valve's call tree, with the port for each part:
```
MapSettle_Run
 ├─ PhysDoc_*: world from the map document (meshes as triangle meshes), props from model PHYS,
 │    mass from shapes, surface materials                                   SettleWorld.CreateWorld
 ├─ CRnWorld_Step (2700 x 1/90)                                             RnWorld.Step
 │   ├─ CBroadphase_PreStepQuery, RnTree_* (7 trees), RnPairFilter_*        Broadphase, DynamicTree
 │   ├─ CRnWorld_Collide
 │   │   ├─ CRnConvexContact_Update -> dispatch 0x1803e4200                  ContactLifecycle, HullCollision, RoundCollision
 │   │   ├─ CRnMeshContact_Update: query, per-triangle, cluster, merge,      MeshCollision
 │   │   │    reduce, warm start
 │   │   └─ CRnWorld_FlushCollideLists (unstable MSVC std::sort)             ContactLifecycle.Flush
 │   ├─ CRnWorld_Solve: islands, colouring, build/integrate, velocity,      IslandSolver, WorldSolver, ContactSolver,
 │   │    position, write-back                                               Integrator
 │   └─ CRnWorld_SolveContinuous                                            Continuous, ContinuousSolve, Gjk, TimeOfImpact
 └─ PhysObj_WriteBackTransform -> CMapEntity_SetStartAsleep                 SettleWriteBack
```

Facts that are not obvious from the code:
- **Candidates** are the nodes with bodies (`PhysDoc_GetNodes`). A candidate
  starts asleep when all its objects are asleep; static objects count as
  asleep.
- **The collision group table** (vphysics2 64x64 u16) is filled by vphysics
  startup, which creating a world in-process does not run.
  `CollisionGroupTable.cs` ports the fill. Without it contacts are missed.
- **Colouring** starts once either contact group of an island reaches 100
  contacts (a quarter of a group at 25). The island manager's +0x50 flag is
  on by default.
- **tier0's `cosf`, `sinf` and `expf`** are AMD libm FMA3 builds and not
  correctly rounded (`CrtMath`). `rsqrtps` results are CPU specific; the
  reference machine is a Ryzen 7 7800X3D.
- **Valve's multithreaded collide** sorts contacts with an unstable
  `std::sort`, so equal keys can come out either way. None of the three
  settled maps has such a tie.
- **Round shapes:**
  - Shape data sits at +0xb8, already scaled.
  - Capsule-hull goes to its deep path below 0.003125 and uses margin 1/16.
  - Its tangents are built inline with a 0.57735 threshold.
  - Where an edge contact replaces a face contact, Valve copies an
    uninitialised stack manifold; the tests mask those padding bytes.

## Method

- **The in-process oracle.** Load the installed `vphysics2.dll` in the test
  process (`tests/Source2.Compiler.Tests/Vphysics2Oracle.cs`, hash-gated to the 20260924 build) and
  call internal functions by address on the same memory as the port.
  - Structs are mirrored byte for byte (`RnBodyState` 0x280,
    `SolverBody` 0xC0, `CachedManifold` 0xE0).
  - Allocate 16-byte aligned, because Valve uses `movaps`.
  - `CreateInterface("VPhysics2_Interface_001")` slot 0x60 creates whole
    worlds in a plain process.
- **Float order.** `tools/settle/lanesym.py` traces four-lane SSE code.
- **Captures.** `tools/settle/capture_settle.py` records bundles (every body
  before and after each step) for replay (`SETTLE_CONTACTS=`).
- **From the map.** `SettleFromMapTests` checks the world built from the map
  alone; the lump tests (`EntityLumpAgainstValveTests`) check the
  write-back.
