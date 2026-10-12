# Owning the whole stack: dropping ValveResourceFormat

Goal (2026-10-12): the compiler depends on nothing it does not own. No ValveResourceFormat
(VRF), and in the end no ValveKeyValue or ValvePak either. VRF releases often, and most of
its changes are irrelevant to us; tracking them costs a re-vendor and patch rebases each
time (five of ten patches broke at `d9926fcb`). Its reader also trusted file sizes enough
to allocate gigabytes from a few hundred hostile bytes (`s2c fuzz`), and ValveKeyValue's
KV3 text reader loops forever on a stray `;`. A standalone compiler should not inherit
either.

## Status (2026-10-13)

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

The operations on VRF objects that a host may still hold (`Serialize(Resource)`,
`SerializeModel*`, `GetDataRoot(Resource)`, the `Resource` overloads of the decompiler,
`DirFileLoader`) live in the separate `Source2.Compiler.Vrf` adapter, which is the only
project that references VRF. The CLI uses it for the VRF-side parity sweeps.

Still from outside, as plain NuGet packages with no patches:

| dependency | used for |
|---|---|
| ValveKeyValue `KVObject`, `KVDocument`, `KVSerializer` | the KV tree model everywhere; KV3 text parsing (guarded by `Kv3SourceCompiler.PreParse`) |
| ValvePak `Package` | template lookup in a VPK |

## What is left

1. **Our KV3 value model and text reader.** Replace `KVObject` with a model of our own
   that keeps wire types (so the UINT8 that the reader widens survives an edit of the
   block that holds it). The text reader grows out of `Kv3LineColumns`' scanner, which
   already handles comments, multi-line strings, flags and blobs, and refuses hostile
   input.
2. **VPK reading.** The format is small; the map branch already writes VPKs.
3. **Map code** stays on the map-authoring branch, which owns it and still uses VRF.

vpkstuff moves its compiler call sites to `CompiledResource` as it touches them; until
then it references `Source2.Compiler.Vrf` with its own VRF (`Source2CompilerVrfProject`),
and `third_party/patches` keeps the guards for that VRF, because vpkstuff runs it on files
strangers upload.
