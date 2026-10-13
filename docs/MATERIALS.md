# Materials: `.vmat` to `.vmat_c`, as resourcecompiler does it

Work in progress toward byte parity (API.md, order of work step 2). This file holds what is
settled from the binaries; `RcReference/materials` holds the probes and RC's outputs.

Build: `resourcecompiler.dll` 1.41.9.0 (sha256 `d1115f9f9b976db6...`, the 2026-10-09 build).

## What one `.vmat` compile writes

A `.vmat` (KV1 `Layer0 { shader ...; TextureColor "x.png"; F_...; g_... }`) compiles to the
`.vmat_c` plus one child `.vtex_c` per texture the shader's static combo reads. RC writes the
children beside their first input image (`materials/default/...` for shader defaults), named
`<first input file, '.' as '_'>_<logical CRC as %x>.vtex` (`'%s_%x.%s'`, 18010bc8b).

- DATA: `m_materialName`, `m_shaderName`, the params the `.vmat` sets (int/float/vector, not
  the shader's defaults), `m_textureParams` = every output texture the combo reads, pointing at
  the child, and `RepresentativeTextureWidth/Height` from the colour texture.
- RED2: input dependencies of every image (and its optional `.txt`), every argument and special
  dependency of the texture compiles, `Material Compiler Version` 25, and the shader as
  `vpk:<absolute path of shaders_pc.vpk>/<shader>.vfx` with the shader's `m_nVersion` as its
  fingerprint (csgo_complex 5), plus each mode's fallback shader (`csgo_depth_only.vfx` 1). The
  absolute path is the machine's install, so a compile must be told it to match RC.
- `m_SearchableUserData`: the shader's attributes, the F_ values set, the representative texture.
- INSG: the vertex input signature of the shader's static combo.

## Which textures: the shader's program files

CS2's shaders are VCS v72: Source 2 resources (`resver 72`, blocks `DATA RED2`) whose DATA is
KV3, so `CompiledResource` reads them; `shaders_pc_dir.vpk` holds
`shaders/vfx/<shader>_pc_50_{features,vs,ps}.vcs`. csgo_complex's 28 MB PS parses in 0.5 s.

- `features.vcs`: `m_nVersion` (RED2 fingerprint), modes and their fallback shaders, the
  feature combos (`F_*`), `m_variableDescriptionArray`, `m_textureChannelProcessorArray`.
- `ps.vcs`: its own variables and static combos (`S_*`, each with `m_iFeatureIndex` into the
  features' combos and `m_nComboIndexValue` as its stride), `m_staticComboIDs`, and per static
  combo `m_allVars.m_indexAndRegisterOffsetArray` (variable index in bits 16..27).

An output texture such as `g_tColor` has several variable descriptions, one per packing. The
material's F_ values give the static combo id (`sum value * m_nComboIndexValue`), and the
`g_t*` variables in that combo's `m_allVars` are the ones compiled. csgo_complex, measured:

| combo | id | g_tColor | packing |
|---|---|---|---|
| none | 0 | #79 | DXT1, colour rgb |
| S_ALPHA_TEST | 40 | #81 | BC7, colour rgb + translucency a (PreserveCoverage) |
| S_TRANSLUCENT | 80 | #82 | BC7, colour rgb (ArtistDefinedOrAlphaWeighted) + translucency a (Box) |
| S_METALNESS_TEXTURE | 1280 | #80 | BC7, colour rgb + metalness a |

### From a variable to a generated `.vtex`

An output variable has `m_outputTextureFormat` (20 DXT1, 24 ATI1N, 25 ATI2N, 27 BC7) and up to
four `m_nChannelInfoIndex` into the channel processors. A channel processor has
`m_nChannelDesc` (one byte per channel: dst in the high nibble, src in the low, 0..3 = r..a,
FF unused), up to four `m_nInputTextures` (input variable indices), `m_outputColorSpace`
(1 srgb) and `m_mipProcessingCommand`. An input variable has `m_inputColorSpace`,
`m_nMinPrecisionBits`, `m_szTextureFileEnding`, `m_inputProcessingCommand` and
`m_defaultInputTexture`.

The generated `.vtex` lists the channels' inputs in order, without merging repeats (a
translucent colour lists the translucency image twice), but a channel refers to an input by
name and so reads the first one with that name (the translucent alpha reads input 1, not 2;
whether the name is the input variable or the file is not separated by the probes). An input the `.vmat` does not set is
`m_defaultInputTexture`, else `materials/default/default_<ending>.<ext>` for the first of
`psd tga tif pfm exr` that exists in the CONTENT search path, else
`materials/default/default.tga` (181f18f9f). An input naming a `.vtex` is the output texture
itself (cx_vtex: `g_tColor` = `materials/s2ctex/dxt1.vtex`, no child).

Code: `Shaders.ShaderProgram` reads the programs, `Materials.MaterialTextures` resolves the
outputs, `Materials.GeneratedTexture` writes the logical text and the child path.
`MaterialTextureTests` checks all seven probes: DATA texture params, RED2 child list and every
child's logical text equal RC's.

## The logical CRC (the child's name and its `___OverrideInputData___` fingerprint)

`181e91070` prints the `.vtex` into a text `CUtlBuffer` and hashes it with MurmurHash2, seed
`0x24871466`. The text starts `VTEX_LOGICAL_CRC_VERSION = 1`; the dumper is `181e8fcf0`, the
input texture `181e8f660`, the output channel `181e8f9c0`, floats and ints `181e90870` and
`181e909b0` (printed only when not their default). Strings are lowercased (`ToLowerFast`). Its
`'m_textureOutputChannelArray[%d]:'` line prints the input count, not the channel count (RC's
bug, kept). Example, the default AO:

```
VTEX_LOGICAL_CRC_VERSION = 1
m_inputTextureArray[1]:
m_inputTextureArray[0] =
{
m_texture_index = 0
m_fileName = "materials/default/default_ao.tga"
m_colorSpace = "linear"
m_fileExt = "ao"
m_nMinBitsPerChannel = 8
m_typeString = "2d"
m_imageProcessorArray[1]:
m_imageProcessorArray[0] =
{
}
}
m_outputTypeString = "2d"
m_outputFormat = "ati1n"
m_textureOutputChannelArray[1]:
...
```

`RcReference/materials/spew/*.txt` are RC's own texts for every probe child, hashing to the
names. They were logged by RC itself: the naming function (`18010ae53`) logs the text under
`logicalCRCDebugSpew` when a static flag (`183bb83a4`) is set, which nothing sets. A copy of
`game/bin/win64` (hard links) with `resourcecompiler.dll` patched at file offsets `0x10afbc`
(`cmove r9, rsi`, 4 bytes) and `0x10b0a8` (`je`, 6 bytes) to NOPs logs it on every compile and
writes the same bytes as the stock DLL (checked on 5 outputs).

## The `.vmat_c`: `Materials.MaterialCompiler`

`ResourceCompiler` compiles a `.vmat` with `MaterialCompileSettings` (the game's
`shaders_pc_dir.vpk`, and content mounts after the request's own: RC searches the addon, then
`csgo`, then `core`). All seven probes are byte-identical to RC (`MaterialCompilerTests`).

- DATA: the params the `.vmat` sets, in its order (F_ combos and int params as `m_intParams`,
  type 1 as floats, types 3/4 as vectors), texture params by name, then
  `RepresentativeTextureHeight/Width`: the colour texture's first image size, 1x1 when the
  colour is a `.vtex` named directly.
- RED2 input dependencies: the `.vmat`, every image (found in the first mount that has it) and
  its optional `.txt` (recorded in the addon when absent, with its own mount and CRC when there:
  core's `default_normal.txt`), sorted by path. Argument dependencies: RC's texture switches,
  each image as `ImageArg` and `BinaryBlobArg`, a named `.vtex` as `ImageArg` only, sorted by
  name. Special dependencies: `Material Compiler Version` 25, the texture compiler's (11, encode
  quality user data 3, one entry per mip algorithm and image processor the children run:
  `Box`/`Box3`/`ArtistDefined` add `Mip Box` 1, `ArtistDefinedOrAlphaWeighted` `Mip
  AlphaWeighted` 1, `HemiOctIsoRoughness_RG_B` `Mip HemiOctAnisoRoughness` 3 and its own 1,
  `NormalizeNormals` and `Inverse` `Image <name>` 1), the shader and the Depth mode's fallback
  shader by their `m_nVersion`; sorted ignoring case.
- `m_SearchableUserData`: the attributes of the VS, PS and PSRS programs for the material's
  combo and for every mode the shader runs itself (a mode with no fallback, its static combo
  settings applied: unlit's Depth mode, `S_MODE_DEPTH` 1, adds `DepthPassBatchID`), bools as
  0/1, ints, floats, a texture attribute as the path of the texture its variable binds; vector
  attributes (`LightSim_MaterialTint`) are not listed. Then the F_ values the `.vmat` sets,
  `IsChildResource` 0, `shader`, and `RepresentativeTextureHeight/Width` beside
  `RepresentativeTexture`. Sorted ignoring case. Expressions run in `Shaders.ShaderExpression`:
  a material parameter is `1D` + MurmurHash2 of its lowercased name, seed `0x31415926`, its
  value the `.vmat`'s or the shader default; branch targets are absolute offsets.
- INSG `m_elems`: the union of the VS static combo's input signatures (every dynamic combo),
  sorted by D3D semantic name, index, then element name. `m_depth_elems`: the same from the
  Depth mode's fallback shader, or from the shader's own VS with the Depth mode's settings.
- RERL: every texture param's resource, sorted by path.

## Child textures: `Materials.ChildTextureCompiler`

A child is the shared texture build (`VtexCompiler.BuildTexture`) on one packed 8-bit image.
Eight of the probe children are byte-identical (`ChildTextures_CompileToResourceCompilersBytes`),
DXT1, ATI1N and BC7, one and two inputs.

- Packing: unset channels are 0; each channel group copies its source channels of its input to
  its destinations (colour.png rgb + metal.png r to a, BC7). sRGB covers RGB from sRGB inputs.
- `ArtistDefined` with no `.txt` is the Box chain, float for float. `Box3` is the Box chain cut
  to three levels: the float chain itself stops (ao256: 256, 128, 64), so the reflectivity is
  the mean of its 64x64 level, summed in four SIMD lanes by pixel index and the lanes added
  pairwise (`(l0 + l1) + (l2 + l3)`, then `/ n`); for a full chain that is the 1x1 pixel.
- A side under 4 in a block format: the image is resized up to 4 with the wrap bilinear
  resampler in gamma space (sRGB colour encoded first), one level, and the fallback is made
  from that 4x4 image; the reflectivity still comes from the original chain. DATA then carries
  extra data type 3 (FILL_TO_POWER_OF_TWO) after the fallback: 128 bytes, `u16 0`, the
  original width and height, zeros (default_ao 1x1, cx_tiny 2x2).
- Inputs of different sizes: each is loaded in its own colour space and brought to the largest
  size with the wrap resampler, and FILL_TO_POWER_OF_TWO records the first input's size when
  it differs from the output (character's default masks: 1x1 metal, 16x16 masks, output 16x16,
  fill 1x1). Measured on constant images only; a varying smaller input is refused.
- Images are PNG/JPG (SkiaSharp) or TGA (`Imaging.TgaReader`: Valve's defaults are TGA).
- Child RED2: no input dependencies; each image and its `.txt` as additional ones; the
  arguments with `___OverrideInputData___` fingerprint = the logical CRC, default 0;
  `maxmipsize` fingerprint and default 1048576f; `IsChildResource` 1.

## Open

- Which mode wins when two modes give an attribute different values (first seen, here).
- The order of params of one kind when a `.vmat` sets several (file order, here; one each in the probes).
- A varying input smaller than the others (cx_metal_noise: 128x64 colour, 64x64 metalness): the wrap resampler does not reproduce RC's.
- The normal maps' `HemiOctIsoRoughness_RG_B`: mip 0 is per pixel (R,G the hemi-octahedral encoding of the normalised normal times 0.5 plus 0.5, B the roughness after `Inverse` is undone, A 1: the default normal child decodes to 128,128,128,255, reflectivity 0.50389, 0.5, 0.50196, 1); the lower mips need RC's decompile. `ArtistDefinedOrAlphaWeighted` likewise.
- odd_png (24x40): one ulp in the reflectivity's green. Every 8-bit level matches; our float 3x5 -> 1x2 step (3-tap both ways, a 1-wide output) rounds differently. A Frida capture of QuarterSize on that chain settles it (CS2 closed).
- Child mip algorithms beyond the Box family: `ArtistDefined`, `Box3`, `ArtistDefinedOrAlphaWeighted`,
  `PreserveCoverage`, `HemiOctIsoRoughness_RG_B`, `AnisoRoughness_RG`, input processors
  `NormalizeNormals`, `Inverse`, `Mod2XCenter`, several inputs per texture, channel packing.
- Image `.txt` settings (core's `default_normal.txt` exists, CRC 1948310964).
- Channel references by variable or by file name (above).
- Which search path an input resolves in (`core` for `default_ao.tga`, `csgo` for the others).
- The value RC uses for system combos (`S_SHADER_QUALITY`, source type 2).
- Where a compile reads the default images when no Workshop Tools content is installed.
