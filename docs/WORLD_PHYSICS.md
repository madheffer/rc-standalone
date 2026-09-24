# World collision: `world_physics.vmdl_c`

A full compile ships `maps/<map>/world_physics.vmdl_c`, a model whose embedded
physics holds the world's collision. The fast `-world -fshallow` compiles this
project uses for visibility do not build it; only a full compile does.

## What it holds

One part. atixref's has 3,840 convex hulls (placed props, it seems) and six
triangle meshes; de_dust2's has 10,143 hulls and six meshes. Each mesh carries one
collision attribute (collision group plus interaction tags). When its triangles
mix surface properties, it also carries a surface property per triangle
(`m_Materials`). Otherwise the shape's own surface property index covers it.

A mesh shape's fields are its bounds, per-triangle materials, orthographic areas,
flags, surface area, BVH nodes, triangles and vertices.

## How a mesh is built: `RnMeshCreate`

vphysics2's `RnMeshCreate` turns a triangle soup into the shape.
`Physics/RnMeshBuilder.cs` ports it:

1. **Weld.** The vertices are welded at 1/32 by the same CMesh weld the map
   builder uses (`Physics/MeshWeld.cs`). The clusters keep the order they formed
   in, which is the input order of their first vertex. This is not the first-use
   order the map builder's weld renumbers to.
2. **Filter.** Triangles with a repeated corner, or a cross product whose squared
   length is 1e-10 or less, are dropped. Each kept triangle's box, grown by 1/32
   on every side, is recorded. The mesh bounds are the union of those boxes.
3. **BVH.** Four triangles or fewer make a leaf. Otherwise the triangle centroids
   are binned 32 ways along each axis whose centroids span at least 1/16. Every
   cut between bins is priced by area times count, and the strictly cheapest cut
   wins. The records are then partitioned in place by swapping. When no cut
   exists, eight or fewer triangles make a leaf. More are halved by count across
   the widest axis. Nodes are stored depth first, with the left child next to its
   parent and the right child's distance in the node. A leaf stores its first
   triangle and its count.
4. **Output.**
   - Unused vertices are dropped, and the rest keep their order.
   - Triangles follow the leaves, each rotated by its edge lengths.
   - Per-triangle materials follow their triangles.
5. **Areas and flags.**
   - The orthographic areas are the summed projected triangle areas per axis
     (the larger of the positive and negative sides), over the box face across
     that axis, capped at 1.
   - A mesh of more than three triangles is flagged closed when every half-edge
     has exactly one twin and no two triangles that share no vertex come within
     1.19e-7 of each other. It is also flagged inverted when its signed volume
     about the box centre is negative.

`tools/physics/capture_rnmesh.py` records every call of a compile, inputs and the
mesh built. `RnMeshReplay` (`RNMESH=<capture>`) replays them. Every captured call
matches bit for bit: all 55 of cardtest's and all 956 of atixref's (the capture
of atixref stopped before its world call).

Two parts are not the binary's own code:
- The simplifier the options can ask for is not ported. No captured call uses it.
- The self-touch test computes an exact distance rather than running
  vphysics2's GJK. A pair right at the threshold could come out differently.

## What goes in: open

On cardtest, the world's main mesh takes every world mesh's material pieces,
except those whose material is `toolsskybox` or `toolslightmapres`:
- Each piece is welded at 1/32 by the map builder and joined by position, the
  same per-piece path brush-entity hulls take.
- The skybox piece is its own mesh, under another collision attribute.

The pieces do not come in node order or element order. A mesh's two material
pieces land at different places. Whether the order follows visibility clusters,
or is not stable between compiles at all, is the next thing to measure.
