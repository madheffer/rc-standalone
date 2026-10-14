# Changelog

The `Source2.Compiler` package. Versions are `0.1.<commit count of main>`; one entry per published
version that changes something a host sees.

## Unreleased (VRF independence: models, textures, animations)

### Added
- `Meshes.Skeleton` (bones, parents, bind and inverse bind poses, world pose, lookup by name or
  token) from a model, a `.vnmskel` or a render skeleton; `CompiledModel.Skeleton`, `MeshGroups`,
  `DefaultMeshGroups` (the `m_nDefaultMeshGroupMask` rule), `MaterialGroups`, `ReferenceMeshes`.
  Equal to VRF on every pak01 model.
- `StringToken.Get`: the engine's name token (MurmurHash2 of the lowercased name).
- `Textures.CompiledTexture`: a `.vtex_c` read without VRF; `GenerateBitmap` returns VRF's pixels on
  every pak01 texture (BC1-BC7 and BC6H decoders of our own, the simple and HDR formats, embedded
  PNG/JPEG/WebP, YCoCg / normal / hemi-octahedral codecs from the edit info). `ReadMip`,
  `RawImageData`, the header and extra data as properties.
- `SpriteSheet.Read`; `SpriteSheet.Frame.MoreImages` and `SpriteSheet.Image` for frames with several
  rects, so stock multi-layer sheets write back to their own bytes.
- `Anim`: a model's animations without VRF (`ModelAnimations.All`: embedded sequences, animation
  groups, animation graph clips, included models), decoded per frame by `Animation.DecodeFrame`;
  `AnimationClip.Read` for a `.vnmclip_c`. Equal to VRF on every frame of every pak01 animation.

## 0.1.106 (production cleanup, 2026-10-14)

### Added
- `TextureDef.MipChainBgra` and `VtexCompileSettings.MipChainBgra`: a caller-made mip chain
  (8-bit BGRA, level 0 first, each level half the one before), checked and encoded as given.
- KV3 binary v4 writing (`Kv3V5Writer.Write(..., version: 4)`, `Kv3Binary.Write(..., version: 4)`),
  Valve's layout: every v4 block of pak01 re-encodes to its own bytes except 17 large old-zstd ones.
  `Kv3Binary.Version` reads a block's version.
- `Source2.Compiler.KeyValues.KvExtensions`: ValveResourceFormat's `KVObject` helpers with the
  same conversions (`GetIntegerProperty`, `GetArray<T>`, `GetIntegerArray`, `ToVector3`,
  `ToMatrix4x4`, `GetEnumValue`...), for hosts moving off VRF.
- `Kv3Text.Parse`: KV3 text read with the compiler's safety checks (nesting, stray control
  characters) and quoted values kept strings.
- `MurmurHash2.Hash32` and `MurmurHash2.Hash64B`.
- `Kv3Type.Int8` (22); `Kv3Tree` reads INT8 nodes.
- `ResourceBuilder.FindTemplateBytes(Package, extension)`.

### Changed
- `CompiledResource.Write` re-encodes an edited KV3 block in the version it was read in (4 stays
  4; older versions are written as 5). Before, every edited block became v5.
- The KV3 header's object and array counters saturate at 65535 in every writer (Valve's maps).
- `ImageTextureSettings` is `(TextureDef? Texture, byte[]? Template)`: the options are a
  `TextureDef`, no longer eight copied fields.
- `Kv3Type.Int32AsByte` is `Kv3Type.UInt8` (the same value, 23).
- `ShaderProgram.InputSignatures` holds `MaterialAuthoring.InputSignatureElement` (it held
  `VsInputElement`, the same four fields).
- `Source2ContainerAuthor.AuthorKv3Tree` is one method with an optional `editRed2`.
- The PDB and every source are embedded in the DLL (file and line in stack traces); no `.snupkg`.
- `ResourceFile.Write` refuses a block type that is not four ASCII characters.
- WAV input must carry a RIFF/WAVE header.
- `SheetAtlas.Pack` takes a `maxAtlasSize` of at most 16384.

### Removed
- `Source2ContainerAuthor.SoundSpecialDeps` and `VectorGraphicSpecialDeps` (use `SoundDeps` and
  `VectorGraphicDeps`, the same tables), `TextureCompileDeps` (use `TextureDeps("Box")`).
- `GeneratedTexture.MurmurHash2` (use `MurmurHash2.Hash32`).
- `ResourceBuilder.FindMaterialTemplateBytes`, `FindModelTemplateBytes`, `FindTextureTemplateBytes`
  (use `FindTemplateBytes(pkg, "vmat_c")` and so on).
- `Shaders.VsInputElement`.

### Fixed
- Hostile input: every KV3 reader (v1-v5, the legacy VKV3 container, `Kv3Tree`) checks declared
  sizes before allocating and bounds nesting and value counts; the legacy reader had no guard and
  a deep block ended the process with a stack overflow.
- `DirectoryContentSource` read files outside its roots through a rooted or drive-qualified path.
- A crafted WAV chunk size made the chunk walk run backwards, possibly forever.
- `BuildTexture` wrote a 0 width for an image padding past 32768; sizes over 16384 are refused.
- `CloneKv` lost the low bits of 64-bit integers above 2^53.
- Meshopt decoders, `CompiledModel`, `NmClipEdit`/`NmClipCodec`, `ShaderExpression` and
  `SheetAtlas`: malformed input fails with `InvalidDataException` (or the documented argument
  exception) instead of `NullReferenceException`, `IndexOutOfRangeException`, a huge allocation or
  an endless loop.
- `Bc7Native` loads its library from the application directory only.
