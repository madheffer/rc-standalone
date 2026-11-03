# What is still open (2026-11-04)

One page per module of everything that is not yet 1:1 with Valve's tools, or not yet read from the binaries,
as of 2026-11-04. Detail, evidence and the item numbers (`1.c1`, `2.a1` ...) are in
[PARITY_AUDIT.md](PARITY_AUDIT.md) (2026-11-03); this page brings it up to date and says what changed since.
"Not read" means the rule holds on every specimen but its code is not cited (GROUND_TRUTH's hard rule), so it
is open even where the bytes already match.

Allowed variance, not counted as open: compile timestamps and fingerprints (`.vwrld`, RED2), rdtsc seeds,
Valve's own run-to-run heap order where it is proven (settle, the far field dilate).

## 1. World geometry

Changed since the audit: **1.c1 subdivided tangents** is mostly closed (ledger 38, f01a454). The bake's
tangent stream is the patch evaluator's mode 3 output; the normal grid (1813c3cf0) and the tangent grid
(1813c2090) are bit for bit on all 1,183 captured atixref patches, and at HammerMesh_TransformToWorld 103,161
of 103,887 corners match (was 84,443).

Open:
- 1.c1 rest: 726 corners on flat subdivided meshes (atixref 1367, 1372-1380, 6778, 6781) have -0 where Valve's
  mesh has +0. Valve's grids give the same -0, so a step after the grid write changes it; AddEdgeToFace and its
  listener copies (1813b2a40 -> 181387c70) are plain copies, so it is elsewhere. Next: a capture of each
  corner's face-vertex slot and tangent after 1813baa40 on node 1375 (CS2 closed).
- 1.c2 overlay tangents (about 30 small atixref models, packed NORMAL holds a default tangent).
- 1.c3 95 agg_prop models' tangent angle (Valve's encoder NaN path vs ours, a last-bit source difference).
- 1.c4 two agg_nomerge aggregates' later meshlets; 1.c5 the missing-material rule (cardtest teleport02).
- 1.c6 prop aggregate draw order variance claim, not yet checked against two Valve runs.
- 1.c7 atixref whole `-world` package needs a fresh ParityReport (last count 344 of 553, 10-08).
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

## 6. Own lighting

ze_hold_em_p's lit package is 55 of 56 on the same driver (the .vwrld stamp). Open: 6.c6 -s2cbake not compared
on cardtest, c2m2 and atixref; 6.a1 / 6.a2 the material block layout is measured; 6.a3 half rounding in
lightmap_image_save; 6.b1 hit groups not transpiled refuse; 6.b3-6.b7 unported vrad3 features; 6.c4 one probe
volume band; 6.c5 s2c_coloradjust (CS2_CLOSED_QUEUE A.3).
Uncommitted on research/map-authoring, not verified yet: the sky branches in SceneLights, QUAD and TUBE light
read-back (Lighting/Bake/Light.cs), PathTraceProps' lodlevel grouping. Waiting runs: atixref with a longer
timeout, prefabprobe5 and probe_classes without nav, the sky specimens s2c_skytint / s2c_skylightonly.

## 7. Baked outputs (nav, bomb damage, cubemaps, Steam Audio)

To be finished after the above (user, 11-03). Open: 7.b9 non-aggregate scene objects in cubemaps; 7.b1 / 7.b2
movable nav meshes and unported collectors; 7.a1 / 7.a2 measured cube engine rules; 7.c1 the .nav pointer
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
docs/TEXTURES.md on main has every rule. Taking it into vpkstuff is its lane:
`deploy/dep-release.sh s2c 0.1.153 --master` (output changes, so goldens re-bless), then the in-game check.

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
