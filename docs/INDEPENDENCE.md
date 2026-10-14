# Owning the whole stack: dropping ValveResourceFormat

Goal (2026-10-12): the compiler depends on nothing it does not own. No ValveResourceFormat
(VRF), and in the end no ValveKeyValue or ValvePak either. VRF releases often, and most of
its changes are irrelevant to us; tracking them costs a re-vendor and patch rebases each
time (five of ten patches broke at `d9926fcb`). Its reader also trusted file sizes enough
to allocate gigabytes from a few hundred hostile bytes (`s2c fuzz`), and ValveKeyValue's
KV3 text reader loops forever on a stray `;`. A standalone compiler should not inherit
either.

## Status (2026-10-14)

`src/Source2.Compiler` references no VRF: no project reference, no `using ValveResourceFormat`
(`VrfRequirementsTests.Core_DoesNotDependOnVrf` keeps it that way). What replaced it:

| piece | where | proven by |
|---|---|---|
| container read/write, RERL | `Containers/ResourceFile.cs` | all 137,469 pak01 files byte-identical (`s2c container-parity`); fuzzed, throws only `InvalidDataException` |
| binary KV3 reader, every version (v1-v5, legacy VKV3) | `Kv3/Reading/` (port of VRF's reader, MIT) | the same tree as VRF on all 245,859 pak01 KV3 blocks (`s2c kv3-read-parity`) |
| KV3 writer, as tier0's SaveKV3 | `Containers/Kv3V5Writer.cs`, `Kv3/Kv3Binary.cs` | text compiles byte-identical to RC |
| editable resource: unchanged blocks keep their bytes, edited ones re-encode | `Containers/CompiledResource.cs` | the builders' A/B against the VRF-based build |
| legacy sound header | `Building/LegacySoundHeader.cs` | `ModernizeVsnd` / `RebuildSound` A/B |
| RERL ids, RED2, FLCI, texture / sound / SVG / sheet payloads | `Containers/`, `Texture/`, `Building/` | RcReference, today's RC |
| model skeleton (bones, bind poses, world pose), mesh groups, default mesh groups, material groups, reference meshes | `Meshes/Skeleton.cs`, `Meshes/CompiledModel.cs` | equal to VRF on all 13,105 pak01 models and three community packs |
| texture decode to pixels (`GenerateBitmap`, every stored format CS2 uses), sprite sheet read | `Texture/CompiledTexture.cs`, `Texture/Decoding/`, `Texture/SpriteSheet.cs` | VRF's pixels, byte for byte, on all 71,966 pak01 textures (docs/TEXTURES.md, "Decoding") |
| animations: sequences, groups, graph clips, included models; frame decode | `Anim/` (port of VRF's reader, MIT), `Anim/ModelAnimations.cs` | VRF's bones on all 169,076 animations (15,195,587 frames) of the 539 animated pak01 models (docs/ANIMATION.md) |

The operations on VRF objects that a host may still hold (`Serialize(Resource)`,
`SerializeModel*`, `GetDataRoot(Resource)`, the `Resource` overloads of the decompiler,
`DirFileLoader`) live in the separate `Source2.Compiler.Vrf` adapter, which is the only
project that references VRF. The CLI uses it for the VRF-side parity sweeps.

Still from outside, as plain NuGet packages with no patches:

| dependency | used for |
|---|---|
| ValveKeyValue `KVObject`, `KVDocument`, `KVSerializer` | the KV tree model everywhere; KV3 text parsing (guarded by `Kv3SourceCompiler.PreParse`) |
| ValvePak `Package` | template lookup in a VPK |
| SkiaSharp | image decode and the `SKBitmap` texture decode returns |

## Roadmap: what VRF still does for vpkstuff that we do not

In order. The counts are uses in vpkstuff's own code (2026-10-14), so they show what each step
unblocks. Each item is ported or written from the format, then checked against VRF over pak01
before it ships, like the rows above.

1. **Vertex and index buffers as data** (VBIB, 216 uses; DXGI_FORMAT, 70). A view over
   `MeshBuffer` that decodes each input-layout field (position, normal, tangent, texcoords, colour,
   blend indices and weights) from its DXGI format, including the packed normal/tangent encodings.
   It also decodes index buffers to 16- or 32-bit indices. The centralization session asked for
   this, and glTF export needs it.
2. **glTF 2.0 / GLB export** (`GltfModelExporter`, 18 uses, about 4,100 lines in VRF on SharpGLTF).
   It needs a writer of our own (JSON plus a binary chunk, accessors, buffer views; no SharpGLTF).
   It comes in stages, in the order vpkstuff uses them:
   - meshes with the mesh filter;
   - the skeleton as a skin;
   - materials, with textures adapted to glTF channels, embedded images and the preview size cap;
   - animations, which reuse `Anim/`;
   - morph targets;
   - physics and nav mesh export.

   Each stage is checked by reading both GLBs back and comparing accessor data.
3. **Model internals**:
   - hitbox sets and attachments (from MDAT; 14 uses);
   - morph data (MRPH, and the `.vmorf` side file that holds some models' flex controllers);
   - typed physics (`PhysAggregateData`: shapes, hulls, meshes, joints; 5 uses).
4. **Decompile to source**:
   - `ModelExtract` (vmdl_c back to a `.vmdl`);
   - animation graph extracts (`NmGraphExtract`, AG1 graphs; vpkstuff's AnimGraphs folder).
5. **Shaders**:
   - the whole `VfxProgramData` / `ShaderCollection` reader (5 and 3 uses);
   - `VfxEval` beyond the dynamic expressions `ShaderExpression` already runs (8 uses).
6. **Other resource types vpkstuff opens**:
   - sound decode beyond the header (VRF `Sound`, 78 uses: sample format, streaming to WAV/MP3);
   - particle systems (7);
   - Panorama files (15);
   - worlds and world nodes (7). Map code stays on the map-authoring branch.
7. **Leftovers in what is done**:
   - textures: the ETC2/EAC decoders (no CS2 texture uses them), a name for format 33 from the
     texture compiler, RGBM;
   - animation: additive tagging from animation graph 1 (`IsAdditive` only, the frames are equal),
     flex controllers from `.vmorf`.
8. **Our KV3 value model and text reader.** Replace `KVObject` with a model of our own
   that keeps wire types (so the UINT8 that the reader widens survives an edit of the
   block that holds it). The text reader grows out of `Kv3LineColumns`' scanner, which
   already handles comments, multi-line strings, flags and blobs, and refuses hostile
   input.
9. **VPK reading and writing.**
   - Reading is a small format.
   - A generic VPK v2 writer for vpkstuff must reproduce `SplicedVpkWriter` at vpkstuff master
     `b1e8218e`. Frozen case: `vpkstuff/out/splice-case`, expected sha256 `d74cdc0f...4273`, replayed
     with `--splice` on vpkstuff branch `perf/gpu-bc7-quality`.
   - The map branch already writes VPKs.
10. **Speed.**
    - A faster LZ4 HC match finder (the block writer's hot spot).
    - Encoding independent KV3 blocks of one file in parallel.

vpkstuff moves its compiler call sites to `CompiledResource` as it touches them; until
then it references `Source2.Compiler.Vrf` with its own VRF (`Source2CompilerVrfProject`),
and `third_party/patches` keeps the guards for that VRF, because vpkstuff runs it on files
strangers upload.
