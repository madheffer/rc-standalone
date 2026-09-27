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

## The container (`WorldPhysicsAuthor`, `WorldPhysicsTrees`)

Measured on 32 local Valve compiles. Resource version 1; blocks `PHYS CTRL
RED2 DATA` in that order, all KV3 v5 under the generic format. CTRL only says
the physics is embedded block 0 and is stored raw. RED2 and DATA take the
usual cut (LZ4 above 256 bytes of payload). PHYS is LZ4 up to at least
456,332 bytes of uncompressed payload (buffer plus blobs) and Zstd from
527,006; 512 KiB is taken as the cut. Compressed bytes are encoder defined,
so parity means the decoded trees and the container facts (`KvTreeDiff`).
Re-authoring Valve's own decoded trees gives them back unchanged on five maps
on both sides of the cut.

The trees, as Valve's decode back: a scalar unsigned field of 0 or 1 comes back
Int64 (KV3's zero and one), but typed arrays keep UInt32 throughout; floats are
doubles.
- DATA: a model named `maps/<map>/world_physics.vmdl` with nothing else.
- RED2: the ModelDoc compiler (version 3), three argument dependencies, the
  shape counts (hulls, and mesh pieces rather than soups), and the surface
  properties in table order under their canonical names
  (`surfaceproperties.vsurf_c` by hash: `Wood`, `Metal_Box`).
  `has_default_surface_property` is there only when the table holds `default`.
- PHYS: one part with the hulls, then the soups as meshes, then one collision
  attribute index per shape (none at all when every index is 0); the surface
  hashes; the collision attributes.
- A soup's `m_nToolMaterialHash` is the string token of its shapes' material
  path when they all share one (a prop's shape has none), else 0; its
  `m_UserFriendlyName` joins its shapes' names with "; ", a painted layer's
  name being " [surface]" (physicsbuilder 180016230).

- A collision attribute keeps the strings of the shape that registered it,
  its tags each once and in alphabetical order (c2m2: `ladder, npcclip,
  playerclip`). A world material keeps its own spelling (`default`,
  `conditionallysolid`). A prop's attribute is rebuilt from its model's cooked
  one through vphysics2's intersection dictionary (physicsbuilder 180153d40),
  so it takes the dictionary's spelling (`Default`, `ConditionallySolid`);
  names the game registers itself keep the model's.

Built from the map, the whole file is Valve's on atixref, ze_hold_em_nb,
ze_hold_em_p, ze_hold_em_paint and ze_hold_em_paint_flat (every block's tree
and the container facts; ze_hold_em_nb with the GPU sampler).

## Into the package (`WorldPhysicsFiles`, `VpkWriter`, `s2c map-physics`)

The model goes in beside a manifest that lists it, and `world.vrman` names that
manifest (`world_physics.vrman`, from rc's table of world files).

- `world_physics.vrman_c` is compiled from a file RC saves first (AddManifest,
  rc 1801f9550): KV3 text, generic format, the paths in a flat
  `resourceManifest` array (181c1f980), CRLF and no final newline (SaveKV3
  through 181c1fae0). Its RED2 names that file under `csgo_addons/<addon>`
  with the CRC32 of those bytes; the CRC matches Valve's on four maps.
  `world.vrman` is built in memory (1800ff1f0) and names no file.
- A child resource's RED2 is RC's struct written out: 64-bit fingerprints and
  user data, an unsigned 32-bit compiler fingerprint, and
  `m_SpecialInputDependencies` written empty. Workshop maps from older
  compilers leave that key out.
- Built from the map, the manifest matches Valve's (RED2 tree, DATA bytes,
  references, block order) on the same five maps as the model.

A map package is a single-file VPK v2, and `VpkWriter` rewrites Valve's own
byte for byte (atixref twice, ze_hold_em_p, ze_hold_em_paint):
- data in ordinal path order; the tree lists each extension, folder and name in
  reverse order of first appearance, as if each new one were put at the front;
- a chunk section with one entry per MiB of data: archive 0x7FFF, hash type 1
  and the chunk's BLAKE3 cut to 16 bytes (packages from June and August use
  type 0x8000, MD5);
- MD5s of the tree, of the chunk section and of everything before the last,
  then an empty signature section (magic, version 1).

`s2c map-physics <addon> <map> --into <map.vpk> -o <out.vpk>` builds both files
and writes them into a copy of a compiled package. Into ze_hold_em_p's, 54 of 56
entries stay byte-identical and the two world physics files differ only in
KV3 encoding.

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
   and so is the whole soup, subdivided floor included. atixref's trim splits
   into 8-vertex `[Wood]` shapes and empty `[plaster]` ones, as captured.

   The paint the split sees, measured from a capture of painted copies of
   ze_hold_em_p:
   - A plain piece keeps one paint per .vmap vertex: the first of its
     corners met in its own face set (bias, material), faces in order and
     each loop from its first half-edge.
   - A subdivided face takes the paint stored per grid point in
     subdivisionData (the displacement's layout), not a lerp.

   The **material sampler**. A two-surface csgo_environment_blend material
   (its shader sets SupportsMaterialLayerSampling) takes each triangle's
   layer from a GPU render of the material. It runs in every compile:
   atixref's plaster/concrete pieces get plaster where the 0.5 rule gives
   concrete. How it works (physicsbuilder, see `CMaterialSampler`):
   1. **Sample points.** Per triangle, round(area / 128) clamped to 1..53
      picks a count from a fixed table, and that many points are taken from
      a fixed 53-point barycentric table. Each point gets uv0 twice, the
      paint, a tint of 1, a packed tangent frame (normal (0,0,1), tangent
      (1,0,0), sign 1) and its position. The code asks the mesh for a second
      texcoord set, and the split's mesh has one (zeros), yet every captured
      record holds uv0 there; ours match the ze_hold_em_nb records bit for
      bit that way. Without a texcoord 0 stream the sampler gives up.
   2. **Render.** Batches of up to 2^20 points, all on one square target
      sized for the largest batch (the next power of two of the square root
      of the count, at least 16), each point a small triangle centred on its
      own pixel, drawn with an orthographic view through the material's
      **ToolsVis** mode (`S_MODE_TOOLS_VIS` 1) with `g_nToolsVisMode` 80 and
      the render attribute MaterialSamplerMode 0. The vertex layout
      "MaterialSampler" is PosXyz (a stream of its own), then the 40-byte
      records: uv0, uv1, position, packed frame, paint as RGBA8 (x * 255
      truncated and clamped), tint.
   3. **Read back.** Only the colour target (RGBA8 unorm), RGB: with
      m = min(R, G, B), the layer weights are (R - m, G - m, B - m, m) / 255.
   4. **Vote.** Each point picks its largest weight (the lower layer on a
      tie) and each triangle the layer most of its points picked (the lower
      layer on a tie). The split uses the result only when it has one layer
      per triangle; otherwise (the sampler failed) it keeps the 0.5 rule.

   What the programs write (csgo_environment_blend, read from all 384 of
   its ToolsVis pixel programs with `ShaderProgramDump`; the pixel side only
   exists at `S_SHADER_QUALITY` 1). Mode 80 writes
   mix(mix(mix(red * w.x, green, w.y), blue, w.z), white, w.w) for the
   layer weights w:
   - Without `S_USE_NEW_BLENDING` (F_USE_NEW_BLENDING 0), w is
     (1, 0, 0, 0), scaled down by puddles at most, so every triangle reads
     layer 0. Ported without a render: atixref's 5 sampled splits are exact.
     188 of CS2's 239 two-surface environment_blend materials are like this.
   - With it, w is (1 - b, b, 0, 0) (or three layers), b being the height
     blend of the layers' height textures, paint, softness and scales: 51 of
     CS2's materials. These run on the GPU (`Source2.Compiler.Gpu`, Vulkan,
     `GpuMaterialSampler`), drawing Valve's own programs from
     shaders_vulkan_dir.vpk.

   The GPU runner, as it matches resourcecompiler:
   - Programs: vs with `S_MODE_TOOLS_VIS` 1 and dynamic
     `D_COMPRESSED_NORMALS_AND_TANGENTS` 1; ps with `S_SHADER_QUALITY` 1 and
     the statics the material's features set (a static tied to a feature
     takes its value, or == / != a value where the combo says so).
   - `_Globals_`: each constant of the combo's write sequence from the
     material's value or the shader default, expressions evaluated over them
     (`VfxExpression`, single precision; matrix builders and
     TextureAverageColor only feed colour matrices, so they stand as the
     identity and zero), render attributes by source name, features by
     index. Textures and samplers are slots in the bindless arrays.
   - Samplers: the engine's by name, and g_sUserConfig (the state with
     Filter 255) with the material's address modes.
   - Textures: resourcecompiler never streams, so a texture loads from its
     preload level, the first mip whose longer side is at most
     RenderSystem/MaxPreloadTextureResolution (default 512; the render
     system compares max(width, height) of the resident top mip with it).
     Sampling the 4096 grass textures at mip 0 missed by up to 21 steps; at
     mip 3 (512) they match.
   - Inside each point's triangle the texcoords are constant, so the UV
     derivatives are zero: textures read their top loaded mip, and the
     blend's distance term (textureQueryLod, fwidth) is zero.

   Measured on ze_hold_em_nb (ch2_blend_grass_backdrop_001, 2 splits,
   3,816 points): 3,793 points bit for bit and the other 23 one step off in
   one channel, each within about 0.1 of a rounding step; every point's
   layer and both splits' meshes are exact (`GpuSamplerReplay`,
   `BlendSplitReplay` with `BLENDREPLAY_GPU`), and the world soup's
   vertices and indices match the capture (`WorldCollisionInput` with
   `WORLDCOL_GPU=1`). The one-step points come from the API: by default
   resourcecompiler loads rendersystemdx11 (DefaultToolsRenderSystem, or
   `-vulkan`), so that compile ran the DX11 build of the programs. The same
   compile with `-vulkan` (`capture_physshapes.py --vulkan`) reads back
   exactly those 23 bytes differently from the DX11 one, and ours match it
   on all 3,816 points, bit for bit. Valve's own output therefore depends
   on the render system and GPU; the Vulkan path is the one we reproduce.
   Still open: a subdivided new-blending piece (the tessellation does not
   carry texcoords), and a texture's own request for more than the preload
   cap (the render system takes the larger of the two).
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

Static props (`StaticPropHulls`, `WorldCollision.PropPieces`):
- **Which props.** The builder walks the node tree; at each node it takes the
  node's own meshes, then its entities, then its children. A `prop_static`
  whose `solid` key (6 when absent) is 6 gives shapes. A collapsed instance's
  copy is appended to its parent's children, so instances come after their
  siblings; the copy's origin and angles are the collapse's (the settle port's
  `BakedPlacement`).
- **Hulls.** For each body of the model's physics: the prop's AngleMatrix with
  its origin, columns scaled by `scales`, times the body's bind pose. Each hull
  becomes a PhysicsShapeHull node: its vertex positions with the node's origin
  and MatrixAngles, or, when that matrix is scaled, the positions moved and no
  transform. The model compile quickhulls the points (tolerance 0, at most 256
  faces, half-edges and vertices), cooks them with RnHullCreate, builds the
  region SVM, then moves the hull by QuaternionMatrix of the node's transform
  composed with the inverse of its parent's. That composition turns a -0 in
  the quaternion into +0; only the SVM planes show it.
- **Meshes.** Every vertex moved by the same matrix, triangles as stored; a
  mesh with per-triangle materials is split into one node per surface index
  (each node keeps every vertex, then appends the ones its triangles use).
  The node holds a half-edge mesh, which comes back to the part builder as a
  triangle mesh numbered in the order its triangles meet the vertices, each
  vertex kept apart. Measured on atixref's radiator (below): 13 of 13.
- **Attributes.** Each shape carries its body's collision attribute (group and
  interact lists) and surface property hash. The part builder writes spheres,
  capsules, hulls, then gathers the meshes, registering attributes and
  surfaces as it goes, so hull surfaces come first in the table.
- **Measured.** Hulls and SVMs bit for bit against shipped world_physics:
  atixref 3840/3840 (79 in instances), ze_hold_em_nb and ze_hold_em_p
  129/129, c2m2 environment prefab 1348/1348, cs_script_demo 12/12
  (`StaticPropHullsReplay`). atixref's hull attribute and surface indices all
  match, and every matched mesh has as many hulls before it as in the capture;
  ze_hold_em_nb and its flat paint copy are exact end to end
  (`WorldCollisionInput`).

World mesh pieces (measured on atixref's full-compile capture):
- A world mesh moves by its node's own matrix (AngleMatrix with the origin),
  a brush entity's mesh by the CTransforms. Rotated tool meshes at a pitch of
  89.99999, a yaw of 179.99997 or a roll of 90 tell the two apart.
- A subdivided world mesh's tessellated pieces then go through the 1/32 piece
  weld on their world positions, which joins points of neighbouring patches a
  hair apart. Such points are one vertex of the baked mesh, and the bake
  positions faces one after another (builder order, a face's patches in
  corner order), so the last patch to write the point wins: the cluster takes
  its latest-written member's position, not its first.
- Past level 2 a patch is split more than once (FUN_1813ca560): a region's
  children go in corner order 0, 1, 3, 2, children 0 to 2 appended and child
  3 keeping the region's slot, and each child keeps its parent's corner order
  (parent corner k at its own index k). ze_hold_em_p's level 3 floor, 4480
  triangles, comes out in Valve's order.
- A world mesh's faces are cut on their world positions, not the mesh's own:
  ear scores that tie or nearly tie come out the other way in the mesh's
  space. On atixref that is eleven faces over six pieces: large ceiling and
  floor faces of 17 to 55 corners, and the two caps of a rotated 16-sided
  cylinder.
- A world mesh's weld never joins two .vmap vertices, however close. A sliver
  quad on atixref's ceiling has two corners 0.004 apart; they stay two
  vertices and the thin triangle between them stays.
- 522 of atixref's 525 captured mesh pieces are ours (the other three are
  empty), all 522 bit for bit, and every captured soup is the same.

Smart props (CMapSmartProp), measured on atixref's radiator_01.vsmart:
- The .vmap keeps only each element's random seed and locator deltas, keyed
  by element path; the models come from evaluating the .vsmart the way
  smartprops.dll does. Each element's stream is tier0's uniform random stream,
  seeded on first use from its path's stored seed. FitOnLine takes a start
  cap, the end cap, then random fillers while the line is not covered (the
  slack is the length over 1024, held between 1/32 and 1), each at the
  running length along the line.
- The evaluation starts from the node's world transform (its world matrix's
  rotation as a quaternion, its translation, scale 1), so every placement is
  in the world. The compile then brings each one back into the node's space
  with the inverse and places the prop by the node's transform again. That
  round trip through coordinates near 14000 moves the props by about 1e-4;
  evaluating in the node's own space instead misses every radiator piece.
- Each placement becomes a prop_static with the node's collision mode as its
  solid key when that is set, and goes through the static prop path above.
- Ported: groups, models, ModifyState (its modifiers change the state its
  later siblings see), PickOne (the stored choice; FIRST and SPECIFIC),
  FitOnLine with random and largest-first picks and fixed lengths, the sizer
  (with its constraints), locators, Translate, Scale and the variable filter,
  with the classes' own defaults. Translate, Scale and a locator store the
  composition's renormalised rotation.
- An element with no stored seed, or a random PickOne with no stored choice,
  draws from the evaluation's master stream, which nothing seeds (tier0's
  default constructor takes the clock). Such a compile cannot be repeated, so
  the port lists it instead.
- Mako's 35 smart props (19 train ladders, 14 wall AC units, 2 floodlights)
  all evaluate, and every model placed has an entry stored at its element
  path, every stored entry a model under it. Their positions are not yet
  checked against a capture.
- Not ported: smart props in instances, scaled ones, stretched line items,
  root modifiers, IsValid criteria, detail objects, surface overrides and a
  stored locator delta transform.

Still open:
- Static props: spheres and capsules; a prop's bone overrides (PosableSkeleton);
  props inside a CMapPrefab (c2m2 multi); a lattice deformer; the `solid`
  override keys for a collision property and surface.
- The shipped `m_Materials`. RnMeshCreate gets no materials for cardtest's
  soups, yet the shipped mesh has 292. They are written afterwards.
- One material attribute: a named collision property that overrides the group
  and tags.
