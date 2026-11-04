# What is still open (2026-11-04)

One page per module of everything that is not yet 1:1 with Valve's tools, or not yet read from the binaries,
as of 2026-11-04. Detail, evidence and the item numbers (`1.c1`, `2.a1` ...) are in
[PARITY_AUDIT.md](PARITY_AUDIT.md) (2026-11-03); this page brings it up to date and says what changed since.
"Not read" means the rule holds on every specimen but its code is not cited (GROUND_TRUTH's hard rule), so it
is open even where the bytes already match.

Allowed variance, not counted as open: compile timestamps and fingerprints (`.vwrld`, RED2), rdtsc seeds,
Valve's own run-to-run heap order where it is proven (settle, the far field dilate).

## 1. World geometry

Changed since the audit: **1.c1 subdivided tangents is closed** (ledger 38, f01a454 and d2fe461, 11-04). The
bake's tangent stream is the patch evaluator's mode 3 output, and the patch builder 1813b82f0 writes every
face-vertex float as lerp * weight + grid (1813c83f0: weight 0 for a grid, 1 and a +0.0f for a stream with
none), which is what turns the flat meshes' -0 into +0. atixref at HammerMesh_TransformToWorld: 103,887 of
103,887 corners (capture_subdivtangents.py on rc 1103, CS2 closed).

Open:
- 1.c2 overlay tangents: the default tangent explained 11-04 (ledger 249: the material reads no tangent, the stream filter drops it, and the model compile recomputes it); 16 models now exact, the rest are 1.c7's one-step angles (CS2_CLOSED_QUEUE A.13).
- 1.c3 95 agg_prop models' tangent angle (Valve's encoder NaN path vs ours, a last-bit source difference).
- 1.c4 two agg_nomerge aggregates' later meshlets; 1.c5 the missing-material rule (cardtest teleport02): **11-04 Frida: it loads as materials/error.vmat** (core's, the only one; GROUND_TRUTH 42), so its flags should follow from that material; ours gives the empty set.
- 1.c6 prop aggregate draw order: **models closed 11-04** (ledger 244): WRB_BuildAggregate's sort (LOD mask,
  vertex count descending, memcmp) ported; every agg_prop model is Valve's but the trim aggregate (1.c7), the
  metal_door_001 buckets equal Valve's second run. Open: n0.vwnod's fragment order in 21 aggregates, which follows
  the CMesh pointer order of the stable sort before (heap order): CS2_CLOSED_QUEUE A.11.
- 1.c7 atixref `-world` ParityReport, 11-05 (CS2 and Steam closed): **539 of 553 files (97.05% of bytes)**, from 521: the model compile's DmeMeshToCMesh copy renormalises every normal and tangent once more (ledger 251, A.13). Before that, 11-04 night: 521, from 486.
  Ported (ledgers 248-250): the model compiler's merge order (180344ae0), draws set by set, each mesh laid out with
  its own material, the RERL sorted by V_stricmp_fast (18016c0e0), NoCollapse for instanced meshes (one draw set
  each), the mesh compile's own tangents for a mesh without valid ones (CMesh_ComputeTangents with its vertex
  split), and draw sets matched before the texcoord compression. ze_hold_em_p's `-world` output is unchanged but
  the .vwrld stamp. Left, by family:
  - ~~tangent angles one step off at the basis axis~~ (closed 11-05, ledger 251: Valve renormalises every direction in
    the model compile's DmeMeshToCMesh copy; the overlays, blocklights, cm00 objects and most aggregates now Valve's).
    Left: agg_nomerge_inferno_plaster01yellow_brick02_blend_0 (vertex count differs; 67 tangents of another kind),
    hr_concrete_wall_painted_001_1 and inferno_trim_wood01_painted_blend_02_0 (TangentFrameCaptureProbe lists them).
  - vism0 overlay objects (8): field order. Valve's mesh puts VertexGenericIntegerData right after position, the
    rest in mesh order, so the CMesh -> half-edge -> DmeMesh -> CMesh round trip reorders it. Read so far:
    CMeshToHalfEdge (1813319d0) classes each stream through the row table at 18243b690 (name and float count,
    then count-only rows, else "invalid"; each writer checks the row's type, +0x28, through 18243b8d0 and
    branches on its byte +0x24) and hands it to eight typed writers (181323b30 .. 181327220); not read: what the
    two branches write, and how the model compile turns the half-edge data back into the DmeMesh's stream order.
    WorldNodeModels' Rank is still fitted.
  - metal_door_001's two buckets: equal to Valve's second run (the known swap, within variance).
  - n0.vwnod: the fragment order (A.11) and the blocklight shadow objects' light probe handshake (Valve 0).
- 1.a1-1.a9 measured, not read: the SIMD transform and the renormalise before WRB_CollectMeshEntries, field and
  aggregate list order, AllowBackfaceCulling's parent set (materialsystem2 +0x60, written at 1800364c3).
- 1.b refusals ZE maps can reach: foliage (NeedsLocalSpaceVertices), clutter, smart prop skips; detail grass
  files are not carried into the package (only Hammer writes them; RC packs them, not read).
- Cross-cutting stale docs listed at the end of PARITY_AUDIT (GEOMETRY.md, MapGeometry.cs, VisClusters.cs ...).

## 2. Physics and collision

Unchanged since the audit. world_physics is whole-file exact on every corpus map with a current compile.
Open: 2.a1 measured shader translucency tables feeding collision (read physicsbuilder's material read
1806208d0); 2.b1 the 0.5 stand-in without the GPU material sampler; 2.c1 fix3_geo_all's LZ4 frames;
2.a5 / 2.a6 the surface property default walk; 2.b6 / 2.b7 settle refusals waiting for a corpus model.

## 3. Containers and packaging

Unchanged. Every reference is byte exact; open is provenance only: 3.a1 the VPK writer is measured, not read;
3.a2-3.a4 container facts per type; 3.a10 the KV3 encoding per writer; 3.a9 string folding outside Tier0Strings.
The generic VPK writer on the vpkstuff side is blocked: its frozen splice case lost expected.vpk and replay.vpk
(deleted 11-03 23:30) and the source pack is not identified.

## 4. Visibility

Byte-identical VXVS from the .vmap on five maps. Open: 4.b1 CellMask (18010e0d0) refuses; 4.b2 hints and suns
inside instances; 4.b3 the .rte skip boxes shared with chunk 1; 4.a1 the output stride is not cited.

## 5. Entities and the entity lump

Every pinned lump matches. Open: 5.a1 / 5.a2 two fitted prefab orders (light order, key flip); 5.a3 the
instance copy key loop (CMapInstance_Collapse 181017820) not read; 5.a5 EntityLumpAuthor rules measured from
output; 5.b1 / 5.b2 entity list layouts that refuse.
5.b10 particle snapshots: **closed 11-04** (ledger 241, Maps/ParticleSnapshots.cs, Compression/BlockCompress.cs):
probe_classes' node_3 and s2c_snapprobe's five (two CMapPaths, a CMapCable, a closed box, a scaled box) are Valve's
byte for byte; the world and an entity write none, as Valve's. Still open: the third mesh stream "color" (mesh
+0x10de) not identified (refused); snapshots inside instances / prefabs (refused); other node types (refused);
whether an Only Entities build writes them; the DMX -> runtime mesh load order (measured, not read).

## 6. Own lighting

ze_hold_em_p's lit package is 55 of 56 on the same driver (the .vwrld stamp). Done 11-04 (CS2 closed, against
Valve's compiles on the 11-01 driver): the sky light's tinted env_sky, sky_lightingonly_name and uniform sky
colour, QUAD and TUBE lights and lodlevel props (ea32f68): scene_info.kv3 byte for byte on s2c_skytint,
s2c_skylightonly, probe_classes, s2c_prefabprobe5, atixref and s2c_coloradjust, and with -s2cbake every
lighting file of s2c_skytint, probe_classes and s2c_coloradjust is Valve's. The colour adjust (ledger 197,
68e77a7): vrad3's material block holds the rtx program's constant g_mTextureColorAdjust1, which the static
combo binds; ours took the artist one by name.

Done 11-04 later (cc475d5): rtx combos with no C# transpile bake on the GPU (Valve's SPIR-V, the block from the
combo's write sequence), so 6.b1 no longer refuses a GPU bake; a translucent material's default hit group opacity
(8e5d8c3). atixref now bakes: lightmap_chart_color byte for byte, irradiance / directional / direct shadows differ
all over the atlas (31,171 shadow texels). Every atixref material block equals vrad3's captured one but bindless
indices, words past a blend block's last field and g_flDetailBlendFactor on two csgo_vertexlitgeneric combo 16
materials. Next for atixref: localise the differing texels to instances (charts -> meshes) before guessing.

Open: 6.c6 -s2cbake not compared on c2m2; s2c_prefabprobe5's lighting differs where its world geometry does
(scene_meshes.kv3 and the packing differ with it: 1.c5's teleport02 and two agg_merge models); 6.a1 / 6.a2 the
material block layout is measured; 6.a3 half rounding in lightmap_image_save; 6.b1 the CPU bake still stands the default hit group in for an untranspiled combo; 6.b3-6.b7 unported vrad3 features; 6.c4 one probe volume band.

## 7. Baked outputs (nav, bomb damage, cubemaps, Steam Audio)

To be finished after the above (user, 11-03). **7.b1 movable nav meshes from func_movelinear done 11-04** (ledger 242,
NavMovable; probe_classes, s2c_navmovable and s2c_navmovable4 (func_mover with a parented func_brush) .nav Valve's
up to the custom data); still refused: point_nav_movable_mesh (Valve's ref_ent search found nothing on
s2c_navmovable2/3, not understood), prop_deformable's, a mover in an instance / prefab or on a map with ladders. Open: 7.b9
non-aggregate scene objects in cubemaps; 7.b2 unported collectors; 7.a1 / 7.a2 measured cube engine rules; 7.c1 the .nav pointer
fields need a pass rule; 7.c3-7.c5 cube build state; 7.b11 Steam Audio instances and entity models; 7.c8
Embree's BVH tie order; lightmap L16 and the ameliorate repack iterations.

## 8. Resource compiler types

Changed since the audit: 8.c1 / 8.c2 are no longer true for textures and materials. Released as
**Source2.Compiler 0.1.153** (box feed, tag v0.1.153, main ad2e29a, 11-04): every RcReference texture,
material, child texture, sound, SVG and KV3 fixture is byte-identical to resourcecompiler 1.41.9.5, plus
54 more RC probes in `RcReference/textures/algorithms2`:
- mip algorithms: every one a CS2 shader names, for 2D textures (AnisoNormal included), and Box<N>,
  MultiplyBox / MultiplyNoMip, RGBM (2D), AlphaWeighted, HemiOctNormal, HemiOctAnisoRoughness_RG_BA,
  MipWidth / MipHeight / MipDepth, DepthPyramidMin / Max, AutoLevels (plain, perchannel, luminance),
  HeightCombine (max, method2, sumnormalized), WrapGaussian;
- image processors: Mod2XCenter, ConvertToYCoCg, EncodeRGBM, ScaleToPowerOfTwo, FillToPowerOfTwo,
  FillToMultipleOfFour, HeightToNormal, DilateColorInTransparentPixels;
- 15 output formats (I8, IA88, A8, R8_UNORM = the vtex "format 33", BGRA8888, R16, RG1616, RGBA16161616,
  R16F, RG1616F, RGBA16161616F, R32F, RG3232F, RGB323232F, RGBA32323232F); the 20 formats RC itself
  refuses are refused.
docs/TEXTURES.md on main has every rule. vpkstuff master carries it (11-04, lane `--master` c075c60d0 and
the fix-forward cb34705fb); dev is shipped. Left: the operator's in-game check on dev, then the live lane
(`dep-release.sh s2c 0.1.153 --verified-in-game`).

Open (each refused with an error naming it; RC probes already compiled in algorithms2 where noted):
- Mip algorithms no CS2 shader names: Mip2DGradient (18014dd80 -> the four-wide rsqrtps Sobel 181274380,
  per vendor; probe gradient2d_rg), RelaxedCones (180154570, 900 lines), HemiOctIsoRoughnessAdjustable_RG_BA
  (18014a080, the eight-channel chain with NORMALZ), Sharpen* and Nice (180152c00: kernel 180152880 and the
  unsharp mask are read; the sigma and amount come from the image settings struct, +0x40, whose defaults are
  not read), AutoLevels with GammaToLinear (180c3bc80 first).
- Cube-only: GGXCubeMapBlur(_RGBM) (180150130), RGBM's irradiance pass, SphericalHarmonics* (RC refuses the 2D
  probe: "Invalid output texture"); cube and 3D textures in general.
- Image processors: UniformRandom (180140320: Valve's SIMD lagged-Fibonacci stream, read; its seed is
  MurmurHash2 of a string from the compile context's vtable +0x170, not identified); processor arguments;
  several processors on one input.
- Output: BC6H (vtex 19; 180c46b20 / 180c46dd0), an sRGB texture in a non-8-bit format, colour-space conversion
  between input and output.
- Inputs: downsizing past a shader's m_nMaxRes, mip size settings (maxmipsize / minmipsize), the artist
  settings in an image's .txt and `_mipN` images, PSD and colour-mapped TGA.
- Materials: a DXT5 child probe (stickers need defaults that are not installed); undeclared `Texture*` params
  (RC drops them, we refuse).
- `.vpcf` legacy schema migration (RC_PARITY G2); `.vagrp` has no ground truth (G4).
- Owned by source2-assets, not this repo: glTF animations and morphs, model internals readers, the full
  VfxProgramData reader, sound decode, ETC2 decode.
- 8.b1: a map's addon content is still compiled by Valve's RC in compile-map; wiring our compilers in is a
  scope decision.
