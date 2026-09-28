# Single resources

The resource compiler side (the `s2c compile`, `texture`, `sheet`, `svg` and
`sound` commands): what each type needs, and how it compares with
`resourcecompiler.exe`. Map containers are in [CONTAINERS.md](CONTAINERS.md).

## Parity with resourcecompiler

The contract is decoded-tree identity, not byte identity. LZ4 is
encoder-defined, so matching Valve's bytes would need their encoder build.
The engine reads the decoded tree plus the container metadata, and those are
what the tests pin (`Source2ContainerAuthorTests` against the compiles in
`tests/Source2.Compiler.Tests/RcReference/`).

| type | container | RED2 | RERL | DATA tree |
|---|---|---|---|---|
| `.vsndevts` | matches | matches | n/a | identical |
| `.vdata` | matches | matches | matches | identical |
| `.vpcf` | matches | matches | superset (G3) | differs (G2) |
| `.vtex`, `.vsnd`, `.vsvg` | authored | authored | n/a | binary |
| `.vagrp` | no ground truth (extinct in CS2) | | | |

Rules:
- **KV3** is binary v5. A block is compressed when its payload is above
  about 256 bytes and stored raw below (tier0 `binary_auto`, CONTAINERS.md).
- **Integers** narrow by value: 0 and 1 as singletons, then Int32.
- **References** are stated twice, as a `resource:`-flagged string in DATA
  and as a RERL entry with `Source2ResourceId.ForPath` (MurmurHash64B, seed
  0xEDABCDEF). Checked on 90,371 references of 116 maps and all of pak01
  (`ResourceRefIntegrityTests`).

Open:
- **G2:** RC migrates a `vpcf26` source to the current particle schema, and
  we do not. Whether CS2 still honours a legacy-GUID particle needs an
  in-game check.
- **G3:** RC drops a renderer-material reference it cannot resolve; we list
  every flagged reference.

After a CS2 update, run `tools/rc-oracle.ps1`. It recompiles the
`RcReference` probes. When a fingerprint drifts, update
`Source2ContainerAuthor.SpecByExtension` and the fixtures.

## No donor file

A template only ever contributed the 16-bit resource version (patched
settable, `third_party/patches/vrf-resource-version-settable`). The header is:
```
u32 fileSize; u16 headerVersion 12; u16 Version; u32 blockInfoOffset 8; u32 blockCount;
{ fourCC, u32 offset, u32 size } x blockCount; 16-aligned payloads
```

Per type:

| type | version | blocks | RED2 special dependencies |
|---|---|---|---|
| `.vtex_c` | 1 | `RED2 DATA` | Texture Compiler Version fp 11; Texture Encode Quality fp 1 user 3 |
| `.vsvg_c` | 2 | `RED2 DATA` | Vector Graphic Version fp 2 |
| `.vmat_c` | 1 | `RERL RED2 DATA INSG` | Material Compiler Version fp 25 plus the texture ones |
| `.vsnd_c` | 4 or 5 | `RED2 DATA` | Sound Compiler Version fp 1 |

Notes:
- **Texture encoding semantics.** A texture's encoding (for example
  `Mip HemiOctAnisoRoughness` on packed normals) rides in RED2; with no
  template, state it in `TextureDef.EncodingSemantics`.
- **INSG** is the shader's vertex input signature. It follows the feature
  combo, not the shader name (22 signatures across 14 shaders in 600
  materials). So `MaterialAuthor.NewContainer` takes it as an argument, and
  `ExtractInputSignature` lifts one from a material.
- **Shader dependency.** A material's shader dependency fingerprint is only
  known from a real compile, so it is passed in.
- **METADATA.** Our textures write a `METADATA` extra-data entry that stock
  textures lack; nothing shows it matters.
- **In game:** authored textures, sheets and SVGs are confirmed. An authored
  `.vsnd_c` has not been played yet.

## Sprite sheets (`.mks`, the SHEET block)

The animation lives in the texture: a `.vpcf` picks a sequence and a rate,
and SHEET holds the frames and UVs. SHEET version 8, with every offset
relative to its own field:
```
u32 version 8; u32 numSequences
sequence header (32 B): u32 id; u8 clamp, alphaCrop, noColor, noAlpha;
                        u32 framesOffset, numFrames; f32 totalTime; u32 nameOffset, floatParamsOffset, floatParamsCount
per sequence: name (padded to 4), frames (f32 displayTime, u32 imageOffset, imageCount),
              images (32 B: croppedMin, croppedMax, uncroppedMin, uncroppedMax)
```

- `totalTime` is the SUM of the display times, not a rate.
- UVs address texel centres: `(x + 0.5) / width` to
  `(x + w - 0.5) / width`.
- A sheet declares `GenerateSheetData` fp 5 and ships `NO_LOD` with one mip.
- The `.mks` grammar follows VRF's emitter:
  - `packmode`;
  - `sequence`, `sequence-rgb` and `sequence-a`;
  - `LOOP`;
  - `frame <file> <time>`.

  `MksSource.Parse` rejects any line it does not know.
