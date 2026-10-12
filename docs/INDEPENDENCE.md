# Owning the whole stack: dropping ValveResourceFormat

Goal (2026-10-12): the compiler depends on nothing it does not own. No ValveResourceFormat
(VRF), and in the end no ValveKeyValue or ValvePak either. VRF releases often, and most of
its changes are irrelevant to us; tracking them costs a re-vendor and patch rebases each
time (five of ten patches broke at `d9926fcb`). Its reader also trusted file sizes enough
to allocate gigabytes from a few hundred hostile bytes (`s2c fuzz`), and ValveKeyValue's
KV3 text reader loops forever on a stray `;`. A standalone compiler should not inherit
either.

What exists already, and is ours:

| piece | where | proven by |
|---|---|---|
| KV3 v5 binary reader and lossless writer | `Kv3/Kv3Tree.cs` | 60,857 of 60,924 stock blocks re-encode byte-identical (`kv3-parity --bytes`); fuzzed without a hang |
| KV3 writer from a tree, as tier0's SaveKV3 | `Containers/Kv3V5Writer.cs` | map resources bit-exact; text compiles byte-identical to RC |
| container layout, RERL read and write | `Containers/ValveLayout.cs` | 42,250 stock files round-trip byte-identical |
| RERL ids | `Containers/Source2ResourceId.cs` | 3,953 stock ids |
| RED2 authoring, FLCI, texture / sound / SVG / sheet payloads | `Containers/`, `Texture/`, `Building/` | RcReference, today's RC |
| KV3 text scanning (positions, nesting, hostile input) | `Kv3/Kv3LineColumns.cs` | FLCI byte-identical |

What still comes from outside (inventory of `src/Source2.Compiler`, 2026-10-12):

| dependency | used for | references |
|---|---|---|
| VRF `Resource`, `Block`, `BlockType`, `BinaryKV3`, `ResourceExtRefList`, `ResourceEditInfo2`, `KeyValuesOrNTRO`, `NTRO` | reading every compiled file; the write path's block objects | ~250 |
| VRF `Texture`, `Sound`, `Panorama` | template-based builders, `ModernizeVsnd` | ~55 |
| VRF `Model`, `Mesh`, `VBIB`, `PhysAggregateData` | model edits in `ResourceBuilder`; map code | ~220 |
| VRF `VfxProgramData` | compiled-shader reading in map code | 6 |
| ValveKeyValue `KVObject`, `KVDocument`, `KVSerializer` | the KV3 tree model everywhere; KV3 text parsing | ~950 |
| ValvePak `Package` | reading VPKs | ~30 |

## Order

Each stage ends with the stock sweeps unchanged (`kv3-parity --bytes`, `reserialize-parity`,
`recompile-parity`, the RcReference byte tests) and `s2c fuzz` clean.

1. **Container I/O.** *Started 2026-10-12:* `ResourceFile` / `ResourceRefs`
   (`Containers/ResourceFile.cs`) read and write every compiled file in pak01, all
   137,469 of 26 types, byte-identical (`s2c container-parity`), and under `s2c fuzz`
   throw only `InvalidDataException`. Next: move the write paths onto it. A resource reader of our own: header, block table, each block's
   bytes, all bounds-checked before anything is allocated. Typed views over it: RERL
   (`ValveLayout` already parses and writes it), RED2 and every KV3 block through
   `Kv3Tree`, and the three small binary payloads the builders read (vtex header, legacy
   vsnd header, Panorama CRC + text). The write side is `ValveLayout`, which takes
   blocks as bytes. `ResourceBuilder.Serialize` and its model variants move onto it.
   This covers everything the vpkstuff compiler paths touch except model edits.
2. **Our KV3 value model and text reader.** Replace `KVObject` with a model of our own
   that keeps wire types (so the UINT8 that VRF widens, and v3/v4 blocks, survive an
   edit). It converts losslessly to and from `Kv3Tree`. The text reader grows out of
   `Kv3LineColumns`' scanner, which already handles comments, multi-line strings, flags
   and blobs, and refuses hostile input. Migrate the ~950 call sites behind an adapter,
   file by file, the compile path first.
3. **Model edits on our own reader.** DATA, CTRL, PHYS, MDAT and the anim blocks are KV3,
   so they move with stage 2. Mesh buffers (MVTX/MIDX/VBIB) only need to be carried as
   bytes for the edits vpkstuff makes.
4. **VPK reading.** The format is small; the map branch already writes VPKs.
5. **Map code** (`Maps/`, `Physics/`): `Mesh`, `VBIB`, `PhysAggregateData`, compiled
   shaders. Coordinated with the map-compiler work, which owns those files.

When stage 3 is done the library no longer needs VRF on the vpkstuff paths. vpkstuff can
then take it as a NuGet package (docs/CONSUMING.md), and the shared-VRF constraint and
its compatibility test retire. vpkstuff keeps its own VRF for what it uses VRF for
(previews, glTF export); that stops being the compiler's concern.

Until then, `third_party/patches` carries guards for the VRF the compiler still runs on,
because vpkstuff runs it today on files strangers upload.
