# Patches applied to the vendored ValveResourceFormat

`third_party/ValveResourceFormat/` is not committed. `dotnet run tools/vendor.cs`
fetches the exact upstream commit pinned in [`VENDORED.json`](VENDORED.json) and
then applies the thirteen patches in [`patches/`](patches/) to it.

Each patch is a literal find-and-replace against one file. The `find` snippet
must occur **exactly once** in the pristine upstream file; zero matches or more
than one aborts the whole vendor run. That is deliberate: an upstream change
that moves the code a patch depends on has to be looked at by a person, not
silently dropped so the build fails somewhere far away, or worse, succeeds with
the behaviour missing.

Upstream is MIT licensed. Patching a fetched copy rather than committing a
mutated fork keeps the attribution honest and keeps the delta reviewable: the
patch files below are the entire difference.

## Why each one exists

VRF is, by design, a **reader**. Almost everything below is about making it
write, which is the seam this project sits on.

### Writing KV3

Compressing the KV3 body used to be three patches of ours
(`vrf-kv3-lz4-usings` / `-header` / `-body`), retired at the `661a5f58` re-vendor.
They existed because the stock writer emitted uncompressed bodies and CS2's
**material** loader rejects those on load ("attempting to render with error
material"), while stock `.vmat_c` always carries `compressionMethod=1` at a
16384-byte frame. Upstream does that itself now, and does more of it: `Serialize`
honours `SerializationCompressionMethod` for Uncompressed / Lz4 / Zstd, writes the
frame size, and compresses binary blobs per segment, where the patch could only
compress the body and only when no blob was present. Choosing the method per block
is now `AuthoredKv3`, not a fork.

| id | file |
|---|---|
| `vrf-kv3-integer-array-unchecked-uint64` | `Serialization/KeyValues/KVObjectExtensions.cs` |

`GetIntegerArray` converts each element to `long` under a checked conversion, so
an all-bits `UInt64` throws `OverflowException`. Community models store
`m_refMeshGroupMasks` exactly that way, which cost 32 models their mesh-group
reads. The unchecked conversion (`0xFFFFFFFFFFFFFFFF` becomes `-1L`) mirrors how
upstream's own `GetUnsignedIntegerArray` already handles it. Still needed at
`d9926fcb`: `GetIntegerArray` became `GetArray<long>`, which goes through
`Convert.ChangeType` on a ValveKeyValue `KVObject`, and that still throws on
`UInt64.MaxValue` (checked with ValveKeyValue 0.71.0.528).

### Writing other block types

| id | file |
|---|---|
| `vrf-panorama-writable-data` | `ResourceTypes/Panorama.cs` |
| `vrf-vbib-serialize-passthrough` | `Blocks/VBIB.cs` |

`Panorama.Data` and `.CRC32` have no setters upstream, so a `.vsvg_c` cannot be
authored from a template; the patch opens both (`BuildPanoramaSvg` recomputes
the CRC after swapping the payload, because upstream's `Serialize` writes the
stored checksum verbatim rather than recomputing it). `VBIB.Serialize` throws
`NotImplementedException` upstream, so re-serializing any model that carries a
vertex buffer fails; vertex and index buffers are byte-stable, so the patch
writes the original block bytes straight back.

### Guards for untrusted input

This compiler is driven by files users upload. These make a malformed one an
exception rather than a process death.

| id | file |
|---|---|
| `vrf-kv3-depth-guard-field` | `ResourceTypes/BinaryKV3.cs` |
| `vrf-kv3-depth-guard-readbinaryvalue` | `ResourceTypes/BinaryKV3.cs` |
| `vrf-vtex-dimension-guard` | `ResourceTypes/Texture.cs` |

A deeply nested KV3 document recurses through `ReadBinaryValue` until the stack
overflows, which no `try`/`catch` can stop; the depth counter bounds it at 512
and raises a catchable `InvalidDataException` instead. A `.vtex_c` header can declare 65535 in each of three
dimensions, which both overflows the unchecked `Int32` size math in
`CalculateBufferSizeForMipLevel` and demands tens of gigabytes; the clamp lands
before any allocation.

### Hostile sizes (2026-10-12)

`s2c fuzz` mutates stock files and drives every write path a host feeds uploads
to. VRF's reader took sizes from the file on trust, so a few hundred bytes could
make it reserve gigabytes, and a block of zero-width typed elements could build
billions of nodes. Each of these refuses such a file with an `InvalidDataException`
before allocating:

| id | file | checks |
|---|---|---|
| `vrf-resource-block-count-guard` | `Resource/Resource.cs` | block count against the 12-byte entries the stream can hold |
| `vrf-kv3-size-guard` | `ResourceTypes/BinaryKV3.cs` | stored sizes against the rest of the block; decompressed sizes at most 4096 times that plus 1 MB; nothing negative; sets the node budget |
| `vrf-kv3-string-count-guard` | `ResourceTypes/BinaryKV3.cs` | string count against buffer 1's bytes |
| `vrf-kv3-capacity-array`, `-typed-array`, `-object` | `ResourceTypes/BinaryKV3.cs` | a length from the file is clamped to 4096 where it is only a capacity hint |

`vrf-kv3-depth-guard-field` and `-readbinaryvalue` also count values against the
node budget (1M plus 16 per decompressed byte). Measured not to reject anything
real: all 42,250 stock files of ten types round-trip byte-identical with them, and
`kv3-parity` reads all 60,924 v5 blocks. The long-term plan is not to need VRF at
all (docs/INDEPENDENCE.md).

### Retired at the `d9926fcb` re-vendor (2026-10-12)

Five patches retired because upstream now does the job itself:

| id | why it is no longer needed |
|---|---|
| `vrf-model-lod-mask-fallback-lod` | `GetEmbeddedMeshesAndLoD` is gone. LoD now lives in `ModelLodInfo`, whose `IsMeshInLevel` treats a mesh with no mask entry as present in every level. That is what the patch did for CS2's CTRL-block models with an empty `m_refLODGroupMasks`. |
| `vrf-model-anim-include-cycle-guard` | `GetReferencedAnimations` no longer recurses. For each included model it takes that model's embedded and animation-group animations, and never follows the include's own `m_refAnimIncludeModels`, so a cycle cannot form. |
| `vrf-kv3-v5-saturate-counts-header`, `-trailer` | The v5 writer used to cast the object and array counts to `ushort` under `checked`, so a block with more than 65,535 objects threw (dkr_onelevel's world physics has 132,490). Upstream now saturates both 16-bit header fields and writes the full counts as ints in the trailer, which is what Valve's file holds and what the patch did. |
| `vrf-resource-texture-trailing-padding` | `Resource.Read` dropped its file-size check entirely (and the `verifyFileSize` parameter with it), so trailing padding after a texture no longer throws. |

`vrf-kv3-depth-guard-readbinaryvalue` was rewritten for the new
`ReadBinaryValue(..., Buffers lane)` signature, and
`vrf-kv3-integer-array-unchecked-uint64` moved with `GetIntegerArray` into
`KVObjectExtensions.cs`. Both still do the same thing.

## Changing the pin

Bumping the sha in `VENDORED.json` is a deliberate act:

1. Update the sha, delete `third_party/ValveResourceFormat/`, re-run the vendor step.
2. Expect conflicts where upstream moved the patched code. For each one, fetch
   the pristine snippet from the GitHub raw URL at the new sha, rewrite
   `<id>.find.txt` to match it exactly (no trailing newline), and rewrite
   `<id>.replace.txt` on top of it.
3. If upstream has implemented the behaviour itself, retire the patch: delete
   its files and its entry in `index.json`, and say so here.
4. Re-run `dotnet test`. The parity gate against `resourcecompiler.exe` output
   is what proves the compiler still emits what Valve emits.
