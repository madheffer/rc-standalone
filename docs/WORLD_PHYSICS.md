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

## What goes in

`Physics/WorldCollision.cs` ports how the soups are put together. Both of
cardtest's world soups equal the captured RnMeshCreate inputs, vertex for
vertex and index for index (`WorldCollisionInput`, `WORLDCOL=...`).

1. **Walk.** physicsbuilder's `CPhysicsBuilder::Build` walks the map's node
   tree depth first, children in stored order. This is the order
   `MapMeshes.Read` walks in. Each world mesh is split by material, in
   material order, and each piece becomes one node of an in-memory ModelDoc.
2. **Material physics.** A table of material attributes decides each piece's
   fate:
   - `mapbuilder.nonsolid` drops the piece (`toolslightmapres`, `toolstrigger`);
   - the sky, clip, ladder, water and los/sound attributes give the collision
     group `conditionallysolid` and an interact-as tag;
   - water forces the piece solid;
   - `translucent` gives `window`, on materials that are not nodraw.

   `PhysicsSurfaceProperties` names the surface property.
3. **Painted layers.** A piece whose mesh has vertex paint, in a blend
   material, is cut by layer before it becomes a shape:
   - The shader says how many layers there are. csgo_simple_2way_blend
     has 2. csgo_environment_blend has 2, or 3 with `F_ENABLE_LAYER_3`.
     csgo_water_fancy has 4. csgo_environment_blend and csgo_environment
     add a puddle channel with `F_WETNESS`. These come from the compiled
     shaders' attributes (`ShaderAttributeProbe`), and no other CS2 shader
     declares any.
   - Layer i takes `PhysicsSurfaceProperties<i>`, falling back to
     `PhysicsSurfaceProperties`. Layers with the same surface share it.
     `PhysicsSurfacePropertiesWet` is the puddle surface.
   - With one surface and no puddles the piece stays whole but takes that
     surface. Otherwise each triangle averages its corners' paint. Puddle
     paint above 0.5 picks the puddle surface; else, with two or more layers,
     the first channel at 0.5 or more picks layer 1.
   - Each surface gets a shape named `<name> [<surface>]`, even when it gets no
     triangles. Its triangles go in unshared and are welded at 1/32.

   On ze_hold_em_p the split layers and both empty ones are bit-identical,
   and so is every soup vertex before the subdivided floor. That floor's
   triangles are cut slightly differently (see below). atixref's trim splits
   into 8-vertex `[Wood]` shapes and empty `[plaster]` ones, as captured.

   The paint the split sees, measured from a capture of painted copies of
   ze_hold_em_p:
   - A plain piece keeps one paint per .vmap vertex: the first of its
     corners met in its own face set (bias, material), faces in order and
     each loop from its first half-edge.
   - A subdivided face takes the paint stored per grid point in
     subdivisionData (the displacement's layout), not a lerp.

   Not ported: the **material sampler**. A two-surface csgo_environment_blend
   material (its shader sets SupportsMaterialLayerSampling) takes each
   triangle's layer from a GPU render of the material. It runs in every
   compile: atixref's plaster/concrete pieces get plaster where the 0.5 rule
   gives concrete. How it works (physicsbuilder, see `CMaterialSampler`):
   1. **Sample points.** Per triangle, round(area / 128) clamped to 1..53
      picks a count from a fixed table, and that many points are taken from
      a fixed 53-point barycentric table. Each point gets uv0, uv1 (uv0 when
      there is no second set), the paint, a tint of 1, a tangent (1,0,0,1),
      a normal (0,0,1) and its position.
   2. **Render.** Batches of up to 2^20 points, each a small triangle
      centred on its own pixel of a square target (the next power of two of
      the square root of the count, at least 16), drawn with an orthographic
      view through the material's **ToolsVis** mode (`S_MODE_TOOLS_VIS` 1,
      `g_nToolsVisMode` at its default 0) with the render attribute
      MaterialSamplerMode 0. The vertex layout "MaterialSampler" is PosXyz,
      CompressedTangentFrame, LowPrecisionUv, LowPrecisionUv1 and
      VertexPaintBlendParams, the paint as RGBA8 (x * 255 clamped).
   3. **Read back.** Only the colour target, RGB8: with m = min(R, G, B), the
      layer weights are (R - m, G - m, B - m, m) / 255.
   4. **Vote.** Each point picks its largest weight (the lower layer on a
      tie) and each triangle the layer most of its points picked (the lower
      layer on a tie).

   The plan is to run the same ToolsVis programs on the GPU (the Vulkan
   build on Linux), not to rewrite the shader on the CPU.
4. **Order.** resourcecompiler appends each node's shape to the physics part,
   then re-sorts the whole list by shape type with tier0's `V_qsort` after every
   append. That is the Microsoft CRT qsort (`Maps/CrtQsort.cs`). On runs of equal
   types, its selection sort for eight elements or fewer swaps the first and last
   element every pass. Meshes appended in order 0..17 therefore come out as
   4,2,5,1,6,3,7,0,8,...,17. This is why a mesh's two pieces land far apart.
5. **Soups.** The part builder gives each collision attribute one soup, in
   first-appearance order. A shape joins its attribute's newest soup unless
   that soup already has triangles and the shape would need a per-triangle
   surface property above 255. Vertices are appended as they are, and indices
   are offset. RnMeshCreate then welds the soup.

Still open:
- The static-prop hulls. They are shapes of another type in the same part, so
  they change where the meshes land in the sort. With the captured hull slots
  filled in, ze_hold_em_p's pieces sort as captured (the test does this).
- ze_hold_em_p's subdivided floor: 224 of its 4480 triangles differ from the
  capture, and their order does too, though its faces are not stitched.
- The shipped `m_Materials`. RnMeshCreate gets no materials for cardtest's
  soups, yet the shipped mesh has 292. They are written afterwards.
- One material attribute: a named collision property that overrides the group
  and tags.
