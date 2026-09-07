# Patches applied to the vendored ValveResourceFormat

`third_party/ValveResourceFormat/` is not committed. `dotnet run tools/vendor.cs`
fetches the exact upstream commit pinned in [`VENDORED.json`](VENDORED.json) and
then applies the twelve patches in [`patches/`](patches/) to it.

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

| id | file |
|---|---|
| `vrf-kv3-lz4-usings` | `ResourceTypes/BinaryKV3.Serialization.cs` |
| `vrf-kv3-lz4-header` | `ResourceTypes/BinaryKV3.Serialization.cs` |
| `vrf-kv3-lz4-body` | `ResourceTypes/BinaryKV3.Serialization.cs` |

The stock writer emits uncompressed KV3 bodies. CS2's vdata loader tolerates
that; its **material** loader does not, and a VRF-written `.vmat_c` is rejected
on load with "attempting to render with error material". Stock `.vmat_c` always
carries `compressionMethod=1` with a 16384-byte frame. The patch routes the body
through `K4os.Compression.LZ4` and fixes up the compressed/uncompressed size
fields. Measured on `weapon_knife_butterfly.vmat_c` (stock 3864 B): unpatched
round-trip 7757 B (+101%), patched 4053 B (+4.9%), a resourcecompiler reference
+0.5%. Binary blobs stay uncompressed at the resource tail because the reader
expects the body and blobs to match, so `useLz4` falls back to false when blobs
are present.

| id | file |
|---|---|
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
