# Patches applied to the vendored ValveResourceFormat

`third_party/ValveResourceFormat/` is not committed. `dotnet run tools/vendor.cs`
fetches the exact upstream commit pinned in [`VENDORED.json`](VENDORED.json) and
then applies the ten patches in [`patches/`](patches/) to it.

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
| `vrf-kv3-v5-saturate-counts-header`, `-trailer` | `Resource/ResourceTypes/BinaryKV3.Serialization.cs` |

The v5 writer cast the object and array counts to `ushort` under `checked`,
so a block with more than 65,535 objects threw (dkr_onelevel's world physics
has 132,490). Valve's file stores 65535 in both 16-bit header fields and the
full counts as ints in the trailer; the patch does the same.

| `vrf-kv3-integer-array-unchecked-uint64` | `Serialization/IKeyValueCollection.cs` |

`GetIntegerArray` converts each element to `long` under a checked conversion, so
an all-bits `UInt64` throws `OverflowException`. Community models store
`m_refMeshGroupMasks` exactly that way, which cost 32 models their mesh-group
reads. The unchecked conversion (`0xFFFFFFFFFFFFFFFF` becomes `-1L`) mirrors how
upstream's own `GetUnsignedIntegerArray` already handles it.

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

This compiler is driven by files users upload. These three make a malformed one
an exception rather than a process death.

| id | file |
|---|---|
| `vrf-kv3-depth-guard-field` | `ResourceTypes/BinaryKV3.cs` |
| `vrf-kv3-depth-guard-readbinaryvalue` | `ResourceTypes/BinaryKV3.cs` |
| `vrf-model-anim-include-cycle-guard` | `ResourceTypes/Model.cs` |
| `vrf-vtex-dimension-guard` | `ResourceTypes/Texture.cs` |

A deeply nested KV3 document recurses through `ReadBinaryValue` until the stack
overflows, which no `try`/`catch` can stop; the depth counter bounds it at 512
and raises a catchable `InvalidDataException` instead. A model whose
`m_refAnimIncludeModels` reference each other recurses across fresh `Model`
instances with no cache to stop it; a thread-static visited set skips includes
already on the load stack. A `.vtex_c` header can declare 65535 in each of three
dimensions, which both overflows the unchecked `Int32` size math in
`CalculateBufferSizeForMipLevel` and demands tens of gigabytes; the clamp lands
before any allocation.

### Robustness and correctness

| id | file |
|---|---|
| `vrf-model-lod-mask-fallback-lod` | `ResourceTypes/Model.cs` |
| `vrf-resource-texture-trailing-padding` | `Resource.cs` |

CS2's CTRL-block models ship an empty `m_refLODGroupMasks`, and the upstream zip
over it yields nothing, so every mesh reads as invisible. Defaulting an empty
mask to 1 (LoD 0 visible) matches what the engine does.

Some community-packed `.vtex_c` carry around 16 bytes of trailing padding after
the texture data, making the file larger than its declared `FullFileSize`, and
`Resource.Read` threw on it. VRF already tolerates trailing bytes for raw
PNG/JPEG payloads; this extends the same tolerance to compressed ones. A file
that is *smaller* than declared still throws, because that is real truncation.

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
