# Source 2 resource compiler

A standalone .NET library and CLI that produces **compiled Source 2 resources**
(`.vdata_c`, `.vsndevts_c`, `.vpcf_c`, `.vtex_c`, `.vsnd_c`, `.vsvg_c`,
`.vmat_c`, `.vmdl_c`) that Counter-Strike 2 loads.

It does not use `resourcecompiler.exe`. It does not need the Workshop Tools, a
CS2 install, or the game running. It shells out to nothing. Every byte of every
output is written by managed code in this repository, and every structural fact
it writes was measured against Valve's own compiler output rather than guessed.

```
$ s2c compile my_sounds.vsndevts
my_sounds.vsndevts_c  (1,113 bytes)

$ s2c inspect my_sounds.vsndevts_c
type           SoundEventScript
resver         1
blocks         RED2 DATA
RED2 special dependencies
  Sound Event Script Version             CompileSoundEventScript      fp=10 user=0
RED2 input dependencies
  soundevents/my_sounds.vsndevts                             crc=3086475412
RERL           0 entries
```

---

## Why this exists

The usual way to make CS2 content is the Workshop Tools: author a source file,
run `resourcecompiler.exe`, get a `_c` file. That is a 60 GB install, a Windows
GUI, and a batch process. It is not something a web service can run per request
for thousands of users, and it cannot run at all on a Linux worker.

This project replaces the compiler for the resource types that matter to
content VPKs. It runs in-process, cross-platform, in milliseconds, and it is
what powers the build pipeline at [vpkeditor.xyz](https://vpkeditor.xyz).

## What "compiling" a Source 2 resource actually involves

A compiled resource is not a wrapped source file. It is a container:

```
uint32 fileSize; uint16 headerVersion (=12); uint16 resourceVersion;
uint32 blockInfoOffset (=8); uint32 blockCount;
{ fourCC; uint32 relOffset; uint32 size; } x blockCount;   // RERL? RED2 DATA ...
16-aligned block payloads
```

Getting the framing right is the easy part, and
[ValveResourceFormat](https://github.com/ValveResourceFormat/ValveResourceFormat)
(MIT) already does it. The hard parts, which are what this repository is, are:

**1. The RERL ids.** Every external reference a resource makes is stated twice:
as a `resource:`-flagged string inside the data tree, and as a RERL entry
pairing that path with a 64-bit id. The engine resolves assets by that id and
fatals on a wrong one, so a compiler that cannot compute it cannot emit a
material that points at a new texture. The function was recovered by
brute-forcing 25 known `(path, id)` pairs lifted from stock CS2 RERL blocks:
**MurmurHash64B over the lowercase path with the uncompiled extension, seed
`0xEDABCDEF`**. It is 40 lines, it is in
[`Source2ResourceId.cs`](src/Source2.Compiler/Containers/Source2ResourceId.cs),
and a sweep of 3,953 stock resources reproduces every id exactly.

**2. The RED2 identity.** Each compiled resource carries an edit-info block
naming the compiler that made it, that compiler's version fingerprint, the
source file's path and CRC, and the subassets it defines. Getting this wrong is
not cosmetic: the wrong resource version or a missing `CompileVData` dependency
produces a file the loader treats as a different type. Every value here was
dumped from real `resourcecompiler.exe` output with Valve's own
`resourceinfo.exe` (see [`tools/rc-oracle.ps1`](tools/rc-oracle.ps1)), and the
authored blocks round-trip back through `resourceinfo.exe` cleanly.

**3. KV3 integer widths.** Valve's compiler types integers by value and by
context: `0` and `1` get dedicated singleton codes, values that fit signed
32-bit become `Int32`, an all-integer array is written as a typed array whose
element type is the widest any element needs, and a mixed array is not typed at
all. Get this wrong and the tree decodes differently from a stock compile. The
rule was mapped with probes at every boundary and is implemented in
`Source2ContainerAuthor.NarrowIntegers`.

**4. LZ4 bodies.** CS2's material loader rejects an uncompressed KV3 body, so
`.vmat_c` must ship `compressionMethod=1` with a 16384-byte frame size, the
same as stock.

**5. Textures.** `.vtex_c` output is a real mip chain, block-compressed
(BC7 / BC5 / BC4 / BC3 / BC1 or raw), with the full stock extradata set
(`FALLBACK_BITS`, `METADATA`, `COMPRESSED_MIP_SIZE`) that the texture streamer
expects.

## What it emits

| Source | Output | Container | Notes |
|---|---|---|---|
| KV3 text | `.vsndevts_c` | authored from scratch | no template involved |
| KV3 text | `.vdata_c` | authored from scratch | no template involved |
| KV3 text | `.vpcf_c` | authored from scratch | RERL re-synthesised from the tree's `resource:` refs |
| KV3 text | `.vagrp_c` | authored from scratch | extinct in CS2 content; kept for completeness |
| PNG / JPG / TGA / BMP / WebP | `.vtex_c` | frame from a template | mips + block compression, authored RED2 |
| WAV / MP3 | `.vsnd_c` | frame from a template | authored RED2 |
| SVG | `.vsvg_c` | frame from a template | sanitised, CRC32 refreshed |
| descriptor | `.vmat_c` | frame from a template | shader + params + RERL |
| descriptor | `.vmdl_c` | frame from a template | mesh refs, LoD masks, material groups |
| any `_c` | KV3 text | — | the decompile direction, including typed blocks |

"Frame from a template" means the header version and block layout are taken
from an existing compiled file of the same type. **Only the frame.** The
metadata is authored fresh, which is a fix rather than a limitation: an earlier
version of this code kept the template's RED2 verbatim and every texture it
ever produced shipped declaring a third party's source paths and file CRCs.

The template does not have to come from the game, and it does not have to come
from anywhere in particular: **any** compiled file of that type works, including
one this compiler produced earlier. Compiling the same image against a template
lifted from CS2 and against a `.vtex_c` this tool wrote three generations back
gives byte-identical output. So the game is a one-time bootstrap for the binary
types, not a dependency: seed one file of each type and the compiler sustains
itself from there.

## What it is not

It is not a general replacement for `resourcecompiler.exe`. It does not compile
maps (`.vmap`), shaders (`.vfx`), or meshes from DCC formats. It does not
implement Valve's asset-processing options. Where the output differs from a
stock compile, the differences are measured and written down in
[docs/RC_PARITY.md](docs/RC_PARITY.md) rather than assumed away.

The contract this project holds itself to is **decoded-tree identity**, not byte
identity. Byte identity is not attainable: Valve's compiler writes KV3 binary v5
(a two-buffer split layout) while the serializer here emits v4, and even
matching that would require a bit-identical LZ4 encoder. The decoded tree is
what the engine consumes, so that is what is pinned.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and `git`.

```bash
git clone <this repo> && cd source2-compiler
dotnet run tools/vendor.cs     # fetch + patch ValveResourceFormat
dotnet build
```

The vendor step clones ValveResourceFormat at the commit pinned in
[`third_party/VENDORED.json`](third_party/VENDORED.json) and applies the twelve
patches in [`third_party/patches/`](third_party/patches/). Those patches are
what make VRF *write* rather than only read: an LZ4-compressed KV3 body, a
writable Panorama payload, a VBIB serialize passthrough, plus recursion and
dimension guards for untrusted input. Each is a literal find-and-replace that
**aborts on conflict**, so an upstream change surfaces as a review rather than a
silently dropped patch. See [third_party/PATCHES.md](third_party/PATCHES.md).

## Running the tests

```bash
dotnet test
```

The parity suite runs with no game installed: it compares against real
`resourcecompiler.exe` outputs committed under
[`tests/Source2.Compiler.Tests/RcReference/`](tests/Source2.Compiler.Tests/RcReference/),
which are compiles of this project's own probe sources.

Tests that need genuine game bytes (a structural template, or Valve's own
content as ground truth) read them from your CS2 install. There are 19 tests:
**9 run with no game installed at all** (the container author, the parity gate,
the KV3 surface), and the other 10 need it.

```bash
CS2_DIR="/path/to/Counter-Strike Global Offensive" dotnet test
CS2_DIR=/somewhere/empty dotnet test   # reproduce the no-game case anywhere
```

`CS2_DIR` is authoritative: set it and only it is consulted, so you can
reproduce the no-game case on a machine that does have CS2 installed. It is only
when the variable is unset that the usual Steam locations are guessed.

**A test may only skip when the game genuinely is not there.** xUnit 2.x has no
dynamic skip, so a test that returns early still reports as a pass, and a
fixture lookup that broke for some other reason would vanish into a green run.
So whenever an install is reachable, a missing fixture throws instead of
skipping. `S2C_REQUIRE_ASSETS=1` forces that on for a CI runner that has mounted
the game and wants to assert it is really being used.

## The CLI

```
s2c compile   <in.vdata|.vsndevts|.vpcf|.vagrp> [-o <out_c>]
s2c decompile <in.*_c> [-o <out.txt>]
s2c texture   <image> -o <out.vtex_c> --template <any.vtex_c> [--format bc7|bc5|bc4|bc3|bc1|rgba]
s2c svg       <in.svg> -o <out.vsvg_c> --template <any.vsvg_c>
s2c sound     <in.wav> -o <out.vsnd_c> --template <any.vsnd_c>
s2c id        <resource/path.vtex>
s2c inspect   <in.*_c>
s2c selftest  [--cs2 <CS2 install dir>]
```

`s2c selftest` compiles one of every supported type and reports PASS plus
timing, which is the fastest way to see the whole thing work.

### The optional native BC7 encoder

BC7 block compression dominates texture cost. The managed fallback
(BCnEncoder.Net) works everywhere but takes tens of seconds on a 4096-square
texture; the bundled [bc7enc](src/Source2.Compiler/Native/bc7enc) drops that to
well under a second. It is optional: without it `Bc7Native.Available` is false
and `BuildTexture` transparently falls back.

```bash
cmake -S src/Source2.Compiler/Native/bc7enc -B src/Source2.Compiler/Native/bc7enc/build
cmake --build src/Source2.Compiler/Native/bc7enc/build --config Release
```

## Layout

```
src/Source2.Compiler/
  Containers/   container authoring from scratch, and the RERL id function
  Kv3/          KV3 text to compiled, and the decompile direction
  Building/     material / texture / model / sound / SVG builders
  Texture/      BC7 encoders (native P/Invoke + the managed seam)
  Imaging/      bounded image decode
  Io/           VPK entry access, path-traversal safety, VRF file loader
src/Source2.Compiler.Cli/    the s2c command
tests/                       parity gate + reference-integrity invariants
third_party/                 the pinned upstream sha and the patches applied to it
tools/                       the vendor step, and the resourcecompiler oracle
```

## Credits

Built on [ValveResourceFormat](https://github.com/ValveResourceFormat/ValveResourceFormat),
[ValvePak](https://github.com/ValveResourceFormat/ValvePak) and
[ValveKeyValue](https://github.com/ValveResourceFormat/ValveKeyValue), all MIT.
This project reads and writes formats they made legible; the compiler layer,
the reverse-engineered id function and the container ground truth are its own.

Not affiliated with or endorsed by Valve. Counter-Strike and Source 2 are
trademarks of Valve Corporation.

MIT licensed. See [LICENSE](LICENSE).
