# Geometry

The .vmap's meshes into what the compile builds from them:
- the mesh export (shared with physics);
- the ray trace scene (`.rte`) that visibility reads;
- the world nodes and render meshes the map ships.

Only the first two are ported.

## Mesh export

Valve's side is `CMapMesh_ConvertMeshForBuilder` and its steps:
`HammerMesh_CopyFrom`, `HammerMesh_TransformToWorld`, texcoord shifting,
`CMesh_WriteDmeMesh`, then `DmeMeshToCMesh`. Ours:
- `Maps/MapMeshCorners.cs`: the per-corner export (PHYSICS.md has the weld).
- `Maps/MapMeshes.cs`: the node walk (instances, prefabs, hidden nodes).
- `Physics/HalfEdgeMesh.cs` and `SubdivisionBake.cs`: subdivision.

Placement:
- **A node's world matrix** is the instance path times its own
  `AngleMatrix(angles, origin)`, in the binary's float order
  (ConcatTransforms sums z, y, x, then the translation).
- **Points** go through tier0's SIMD transform, each row summed
  `(x + z) + (y + t)`.
- **An instance copy** is placed where the collapse leaves it
  (`SettleWorld.Baked`). The node's origin and angles move through each
  instance's matrix against its target's inverse, both built in double, then
  go through MatrixAngles and SetAngles. Multiplying the path matrix in
  instead is one ulp out under rotated instances.
- **A prefab** (`CMapPrefab`) is placed the way instance collapse moves a node
  (`SettleWorld.PrefabPlacement`).

Open:
- the stream layout per material shader (render meshes need it);
- faces re-projected by about 1e-5;
- `ApplyDeformer` (deformer meshes);
- displacement, which sits in the subdivision code.

## The trace scene (`.rte`)

`CVisBuilder::Build` writes `%TEMP%\csgo_addons\<addon>\maps\<map>.rte`
during `-world`, and the vis entry reads it back through
`CVisibilityMesh::LoadRTEFromFile`, which settles the layout.

```
offset          size     section
0               60       header: nine u32 (6, 3, 0, A, B, C, B, 0, B), world box min/max, u32
60              A * 8    kd nodes: float split, u32 packed child + axis
60 + A*8        B * 48   triangles
                C * 4    leaf triangle indices
                B * 8    per-triangle id (groups by surface; nothing reads it)
                B * 12   per-triangle reflectivity, all (1,1,1)
```

The 48-byte triangle record:

| slot | holds |
|---|---|
| floats 0-2 | unit normal |
| float 3 | plane distance |
| float 4 | triangle id (equals the record's index) |
| floats 5-7 | edge equation 0 (a, b, c) |
| floats 8-10 | edge equation 1 (a, b, c) |
| bytes 0x2c, 0x2d | projection axes u, v |
| u16 0x2e | flags |

The edge equations are `E(u, v) = a u + b v + c` over the projection. A
vertex is rebuilt by solving `E0, E1 = (0,1), (0,0), (1,0)`, then recovering
the third axis from the plane. That is `RayTraceEnvironment`, and the loader
rejects only non-finite corners.

Flags:

| bit | meaning |
|---|---|
| 0x0001 with 0x0800 | skipped by the loader |
| 0x0010 | nodraw in the file, stored as 0x0020 |
| 0x0800 | excluded from the trace |
| 0x1000 | occupancy only for voxel boxes wider than 256; the sky for the sky stage |

If nodraw is over 80% of the total area, the loader clears the mark from
every triangle.

The file is not byte-stable between compiles. The triangle order changes
(and with it the ids and the kd tree), so compare by content and never by
hash.

### Built from the .vmap (`TraceScene`, `MapGeometry`)

`TraceSceneTests` finds each of our triangles' records bit for bit in the
same compile's `.rte`. Every specimen is exact both ways:

| map | triangles |
|---|---|
| probe01 | 288 |
| cardtest | 300 |
| ze_hold_em_p | 4,548 |
| atixref | 32,854 |
| Mako | 279,064 |

The rules:
- **Which meshes:** those under the world or a group. Under an entity only
  when its class renders as world (FGD `render_as_world_but_physics_as_entity`,
  which in CS2 is only `func_water`).
- **Dropped faces:** a face whose material has `mapbuilder.nodraw` or
  `mapbuilder.occluder`, and none of `visblocker`, `acceptvis` or `sky`, is
  dropped. The attributes come from the compiled `vmat_c`.
- **No props:** the collector's prop pass is off in every compile measured.
- **Hidden:** a mesh hidden by `CVisibilityMgr`, under a hidden node or
  through a hidden instance is not compiled.
- **Slivers:** `WRB_EmitRteTriangles` computes the three edge lengths in
  float, sorts them, and keeps a triangle only when the longest is at least
  0.0001 and `longest * 1.0001 <= sum of the other two`.
- **Subdivided meshes** go through `SubdivisionBake` (PHYSICS.md).

Open (GROUND_TRUTH.md 25):
- **The flag word:** atixref and Mako still differ in bits 0x2, 0x2000 and
  0x8000. Visibility reads none of them. The collector's rules as read:
  - 0x800 unless the entry is traced, and 0x800 again for a second entry
    byte;
  - 0x80 from the entry's flags;
  - 0x2000 or 0xa000 from two bits of its 64-bit flags;
  - then the material's int attributes.
- **Skip boxes:** the emitter also skips triangles inside the builder's
  oriented boxes (`WRB_RteSkipBoxes`). What places them is unknown, and no
  specimen has any.
- **Triangle order:** ours is walk order. The file groups each mesh by
  material, and the rest follows the world renderer's mesh split.
- **Bad meshes:** a mesh whose bounds exceed `2 sqrt(3) g_flConfigMaxCoord`
  is skipped whole; no specimen has one.

## World nodes and render meshes

Not started. Valve's entry points:
- `CompileAndSaveNodes` and `Step_BuildingRenderClusters`;
- `CVisibilityMeshMerger`, which splits meshes by vis cluster (VISIBILITY.md);
- `Step_SplittingMeshWith` and `Step_RemovingTrianglesInside`;
- `FixTJunctionEdgeCracks` and `BuildAggregateRTProxies`;
- `PostCompileNode` and `Step_BuildingVertexOverrideStreams`.

These need visibility's two blocks, the mesh export, the shader vertex
layouts and the static props.
