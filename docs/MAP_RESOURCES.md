# What a compiled CS2 map is made of, and which parts we could author

This is a measurement of real shipped maps, taken to answer one question: if this
compiler grew a map-authoring side, which resources could it author
**authoritatively** - meaning the output is what Valve's own compiler would have
written, not a plausible guess.

Corpus: every subscribed CS2 workshop item on this machine that contains a map,
read straight out of the published VPKs.

| | |
|---|---|
| workshop items scanned | 270 |
| items containing maps | 64 |
| map VPKs inside them | 116 (most items ship a main map plus its 3D skybox map) |
| bytes of map content | 5.27 GB |

Re-run any figure here with the probes in [`../tools/map-survey/`](../tools/map-survey).
They read the published VPKs directly and need no CS2 install.

## Where the bytes are

Per-role totals across all 116 maps. This is what a map *is*, by weight:

| role | bytes | share | files |
|---|---|---|---|
| lightmaps | 2090 MB | 39.6% | 3731 |
| worldnodes (render geometry) | 834 MB | 15.8% | 17681 |
| cubemaps | 748 MB | 14.2% | 86 |
| world_visibility | 546 MB | 10.4% | 109 |
| world_physics | 419 MB | 7.9% | 128 |
| entities (brush entity models) | 131 MB | 2.5% | 12780 |
| steam audio bakes (`.sareverb`, `.sapaths`) | 143 MB | 2.7% | 16 |
| navmesh (`.nav`) | 13.5 MB | 0.3% | 59 |
| **the index layer** (`.vmap_c`, `.vwrld_c`, `.vwnod_c`, `.vrman_c`, `.vents_c`) | **14.0 MB** | **0.27%** | 1363 |

The index layer is a quarter of one percent of the bytes. That asymmetry is the
whole finding: the files that *describe* a map are trivial next to the files that
*bake* one.

## Per-type structure

Block layouts and KV3 facts, counted over the whole corpus:

| type | resver | blocks | DATA |
|---|---|---|---|
| `.vmap_c` | 1 | `RERL RED2 DATA` | **empty in 116 of 116** |
| `.vwrld_c` | 1 | `RERL RED2 DATA` | KV3, ~700 B |
| `.vwnod_c` | 1 | `RERL RED2 DATA` (4 without RERL) | KV3 |
| `.vrman_c` | 1 | `RERL RED2 DATA` | **not KV3** - a 94 B self-relative string list |
| `.vents_c` | 1 | `RED2 DATA` (673), `+ RERL` (41) | KV3 |
| `.vvis_c` | 7 | `RED2 DATA VXVS` | KV3 index, opaque `VXVS` payload |
| `.vtex_c` | 1 | `RED2 DATA` | binary header, pixels past `fileSize` |
| `.vmdl_c` | 1 | `CTRL DATA MBUF MDAT RED2 RERL` (19106) · `CTRL DATA PHYS RED2` (7197) · both (4717) | KV3 |

Two things in that table are worth stating plainly.

**`.vmap_c` carries no map data.** Its DATA block is zero bytes in every one of
the 116 maps. The map root is a pure manifest: a RERL naming every child resource
and a RED2 whose child list repeats them. The map itself is `world.vwrld_c` and
what it points at.

**7,128 of the 11,923 models under `entities/` have no mesh at all.** Layout
`CTRL DATA PHYS RED2`, about 3.5 KB each: KV3 plus a collision hull, no MBUF, no
MDAT, no vertex buffers. Triggers, doors, platforms and clip volumes are collision
only, so authoring them never touches mesh compression.

### KV3 versions in the wild

Published maps are mostly KV3 **v4**, not v5, because they were compiled by
mappers over the last two years on older CS2 builds. Of 116 maps only one
(`ze_out_into_the_open`) has a v5 `world.vwrld_c`. Both load, so a map resource
is not obliged to be v5 the way a current `resourcecompiler.exe` output is.

Compression is not uniform either. Alongside LZ4 the corpus carries **Zstd**
(`compressionMethod = 2`) on some entity lumps, world nodes and physics blocks -
for example `ze_aztecnoob_p`'s `world_physics.vmdl_c` ships a 1.05 MB Zstd PHYS
block. Upstream VRF gained Zstd at the `661a5f58` re-vendor, so reading those is
already covered and writing them is a flag.

## The prerequisite that is already solved

Every map resource states its external references twice: as a `resource:`-flagged
string in its data tree, and as a RERL entry pairing the path with a 64-bit
engine id. Get an id wrong and the engine fatals or silently fails to bind, so
this is the gate on authoring anything that points at anything.

`Source2ResourceId.ForPath` was recovered from 25 material and model pairs. It
turns out to hold over map content without modification:

```
files with RERL:  24,509
refs checked:     90,371        distinct (path, id) pairs: 48,099
id matches:       90,371        mismatches: 0
```

by type: `vmap_c` 41,134 · `vmdl_c` 28,380 · `vwnod_c` 19,201 · `vents_c` 598 ·
`vwrld_c` 572 · `vrman_c` 486. Reproduce with `tools/map-survey/rerl_check.py`.

That is the same function the compiler already ships, re-confirmed against 48,099
pairs it was never fitted to.

## Ranked by how authoritatively we could author it

### Tier 1 - authorable now, nothing new to reverse engineer (implemented, see below)

**`.vrman_c`, the resource manifest.** DATA is 54 to 187 bytes (mean 94) holding
one to three strings, in a three level self-relative layout that all 301 manifests
in the corpus parse under:

```
i32 rel, u32 count   -> outer array
i32 rel, u32 count   -> inner array, per entry
i32 rel              -> string, per entry, 4 byte stride
```

Every offset is relative to the address of the field holding it, the same rule the
container itself uses. There is nothing to infer.

**`.vwrld_c`, the world.** A 56 line KV3 document: builder params, the world node
list with its bounds and prefix, the lighting info block and the entity lump
reference. Our KV3 compiler already writes this shape, and the RERL synthesis
already keys off `resource:` flags, so the references come out for free.

**`.vmap_c`, the map root.** Empty DATA, a RERL of children and a RED2 child list.
The only content is a list of paths we would already be generating.

These three are the whole index layer of a map minus the entity lump, and none of
them needs a format discovery pass. What each *does* need is a `ContainerFacts`
row (resource version plus RED2 special dependency fingerprints), which is a
measurement, not engineering - the measured values are below.

### Tier 2 - authorable, but the content rules are the work

**`.vents_c`, the entity lump.** Structurally a flat KV3 map of string keys. The
difficulty is not the container, it is knowing what Valve puts in it: only keys
that differ from the FGD default, choice keys mapped from their spelling to an
int, resource paths carrying the KV3 resource flag, and the lump tree
(`m_childLumps`) that `point_template` resolves its templates through. Ship an
FGD and this is data plumbing.

**`.vwnod_c`, the world node.** A flat list of scene objects, each a transform
plus fade distances, tint, flags and a model reference. Note `m_nObjectTypeFlags`
is sometimes an int (1040) and sometimes a string enum
(`OBJECT_TYPE_RENDER_TO_CUBEMAPS`), so the writer has to preserve which.

**Brush entity collision models.** The 7,128 mesh-free `CTRL DATA PHYS RED2`
models above. KV3 plus convex hull arrays, and the hull is a half-edge polyhedron
with `u8` index widths, so 255 vertices is a hard ceiling per hull.

### Tier 3 - real reverse engineering, and mostly not ours to do

- **Lightmaps, 39.6% of all bytes.** Four textures per map (irradiance in BC6H,
  directional irradiance, direct light shadows, a debug chart), single mip, up to
  4096x4096, plus a 3.3 MB `lightmap_query_data.kv3` and light probe volume
  octrees. The container we can already write; the *content* is a GPU path tracer
  (`vrad3.exe`). Without it, static geometry renders black.
- **Cubemaps, 14.2%.** Same story, baked.
- **`world_visibility.vvis_c`, 10.4%.** The KV3 index is small, the `VXVS` payload
  is megabytes and undecoded here.
- **Render geometry in world nodes, 15.8%.** Newer maps use `MBUF` rather than the
  `MVTX`/`MIDX` pair, and the buffers are meshopt compressed. VRF ships decoders
  only. The sibling project reports the compressed flag is per buffer, so raw
  buffers are a legal encoding that skips the codec entirely; this survey did not
  re-verify that claim.

## Measured RED2 identities

A `ContainerFacts` row is a resource version plus the RED2 special dependencies
the type declares. Taken across all 116 maps with `tools/map-survey/red2_survey.py`,
they do not vary at all:

| type | resver | special dependency | compiler identifier | fp | agreement |
|---|---|---|---|---|---|
| `.vwrld` | 1 | `World Compiler Version` | `CompileWorld` | 1 | 116 of 116 |
| `.vwnod` | 1 | `World Node Compiler Version` | `CompileWorldNode` | 1 | 116 of 116 |
| `.vrman` | 1 | `Manifest Compiler Version` | `CompileResourceManifest` | 2 | 116 of 116 |
| `.vents` | 1 | `Entity Lump Compiler Version` | `CompileEntityLump` | 3 | 116 of 116 |
| `.vvis` | 7 | none | | | 116 of 116 carry no special dependency |

`.vmap` is the exception, and instructively so: its RED2 carries the **union of
the identities of everything the map built**. `Map Compiler Version`
(`CompileMap`, fp 2) and the manifest row appear in all 116; the world, world
node, entity lump, model and texture rows appear only when the map contains that
kind of child. So a map root cannot be stamped from a fixed table - it is
assembled from what was actually compiled. Its input dependencies behave the same
way, listing the `.vmap` source plus optional probes such as
`lightmaptexturearg.txt` at CRC 0, the same "this file does not exist" statement
`.vsnd` records for its sibling probes.

One value in that set is a setting rather than a version: `Texture Encode Quality`
carries `user=4` in 51 maps and `user=3` in 49, so it reflects the compile options
a mapper chose. Everything else was identical everywhere.

## What is implemented

Tier 1 landed, pinned by `MapResourceAuthoringTests` against Valve's own compiles
lifted from a subscribed workshop map at test time:

| type | authored by | what the test asserts |
|---|---|---|
| `.vrman` | `ResourceManifestAuthor` | the DATA payload is **byte-identical** to Valve's, plus identity and references |
| `.vwrld` | `Source2ContainerAuthor.AuthorKv3Tree` | decoded tree value for value, identity, every RERL id |
| `.vwnod` | same | same |
| `.vents` | same | same |
| `.vmap` | `Source2ContainerAuthor.AuthorMapRoot` | block table, identity union, child list, source CRC, every reference id |

Two things had to be learned to make those pass, and both are worth knowing before
attempting anything further up the map.

**A map resource is not compiled from text, so it cannot be authored from text.**
RC serializes a C++ structure, so an integer's width is the struct field's, not the
value-based rule RC applies when it compiles a KV3 source file (`docs/RC_PARITY.md`
C1). A world's `m_nCompileTimestamp` ships `UInt32` for a value that the by-value
rule types `Int32`, and KV3 text cannot express the difference. Decompiled text also
rounds: an entity lump's `0.1f` reads back as the double `0.10000000149011612` and
renders as `"0.1"`. So `AuthorKv3Tree` takes an already-typed tree and narrows
nothing, and the round trip is tree to tree.

**A child resource carries a different RED2 from everything else this compiler
writes.** Not just a different special dependency: no input dependencies at all, no
`m_SpecialInputDependencies` key (the others write it empty), `IsChildResource = 1`,
and null subasset fields. That shape is identical in all 116 maps.

One value in there is not reproducible and does not need to be: the
`___OverrideInputData___` argument fingerprint is 0 on every world node and physics
manifest, and non-zero only on `world.vrman_c`, which is the one RC hands override
input data to. Authoring without that data, 0 is the honest value.

### The map root's three rules

The root is the one map resource whose identity is assembled rather than stamped,
and all three of its rules came out of the corpus rather than a guess.

**The identity union follows the children, exactly.** A `.vmap_c` declares the
compiler identity for a kind of child if and only if its child resource list
contains one. Checked over 116 maps against 5 kinds of child - 580 observations,
**zero exceptions**, both directions. So the union is derived from what was
compiled rather than configured.

**The RERL is not the child list.** It is the children PLUS what the map points at
without having built it. On `ze_out_into_the_open` that is 212 references against
171 children, the other 41 being stock materials. Both lists are sorted ordinal.

**Two rows are settings rather than versions.** `Texture Encode Quality` carries
user data 4 in 51 maps and 3 in 49, and a map whose textures were compiled at two
qualities carries the row twice. RC's wider argument tail (`bakelighting`,
`lightmapMaxResolution`, three dozen more, varying by toolchain) is the same class
of thing and is deliberately not reproduced, exactly as the texture author already
refuses to invent its option tail. Only `___OverrideInputData___` is written, which
all 116 maps carry.

## Reading the source: `.vmap` is DMX

`DmxBinary` reads the binary DMX that Hammer writes, which is the front door to
compiling a map rather than re-authoring one. The layout is in the class doc; the
two rules that are not guessable from the shape are that an attribute type above 32
is an array of the type below it, and that a string inside an array is written
inline even where a scalar string in the same section is a string-table index.

The check that matters is consumption: the reader ends exactly at the last byte of
Valve's own `.vmap` sources, which is the only way to notice that a length was
misjudged (the graph would otherwise still "parse", against bytes that mean
something else). `DmxBinaryTests` asserts that over the sources shipped under
`content/csgo_addons`, smallest first and capped, because that corpus is 3.2 GB
with single files near 800 MB.

## Ground truth available for the next tier

| source | what it is | where |
|---|---|---|
| compiled maps | 116 map VPKs, 5.27 GB | the workshop install |
| Valve's own map sources | 36 uncompiled `.vmap`, 3.2 GB | `content/csgo_addons` in the CS2 install |
| community map sources | 364 `.vmap` for CS2 ZE ports, MIT, **89 of them with a compiled counterpart in the workshop install** | [Source2ZE/Port-Vmaps](https://github.com/Source2ZE/Port-Vmaps) |
| the compiler itself | `resourcecompiler.exe`, headless | the CS2 install; driven by `tools/rc-oracle.ps1` |

Those 89 pairs are the useful thing: a source and the exact output Valve's compiler
produced from it, for maps this project already reads.

Two Source 2 hashes are already recovered and worth knowing before starting on
entities. The resource id is `Source2ResourceId` here (MurmurHash64B, seed
`0xEDABCDEF`), verified 90,371 times above. The other is **StringToken**, the
32-bit MurmurHash2 with seed `0x31415926` that Source 2 hashes names through; it
lives in the vpkeditor pipeline rather than in this repository, and the entity work
will need it.

## Prior art, and what this survey adds

The sibling project at `D:\_cursor projects\cs2map\s2compile` already drives
`resourcecompiler.exe` headlessly and has a python map compiler that produces a
VPK CS2 loads. Its `docs/PIPELINE.md` and `docs/SEMANTICS.md` are the deeper
source on compile *stages* and on the semantics of brush entities, instances,
overlays and lighting wiring.

What this survey did independently, from published workshop content rather than
from local compiles: the corpus byte census, the block and KV3 census over 116
maps, the 90,371 reference id check, and the manifest layout decode. Three of its
claims reproduce here exactly: the empty `.vmap_c` DATA block, the self-relative
manifest, and the `u8` ceilings in the hull format.

## Not established

- Whether a map authored at KV3 v4 versus v5 matters to any engine version. Both
  ship in the corpus.
- What decides Zstd over LZ4 in Valve's map output. Size is the obvious guess and
  was not tested.
- The `VXVS` payload layout.
- Whether the engine accepts an uncompressed MBUF buffer (claimed by the sibling
  project, not re-verified here).
