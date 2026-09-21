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

## What a `.vmap` can actually contain

Two lists, and the gap between them is the answer to "are we going to be caught
short by something Hammer can make".

### What 135 real map sources use

Every `.vmap` under `content/csgo_addons`, by DMX element type:

| element | count | what it is |
|---|---|---|
| `CDmePolygonMeshDataStream` / `DataArray` | 879,122 | the mesh vertex streams |
| `DmePlugList`, `EditGameClassProps` | 169,040 | an entity's editor properties |
| `CMapEntity` | 83,474 | **an entity** |
| `CDmePolygonMesh` + subdivision | 123,756 | brush geometry |
| `CMapMesh` | 52,088 | **a brush** |
| `CMapGroup` | 22,798 | a group |
| `CMapStaticOverlay` | 5,747 | a decal or overlay |
| `DmeConnectionData` | 4,143 | **an output wiring two entities** |
| `CMapPathNode` / `CMapPath` | 749 | paths |
| `CDmeNodeInstanceData` / `CMapInstance` | 850 | **prefab instances** |
| `CStoredCamera(s)`, `CMapSelectionSet` | 469 | editor state |
| `CMapRootElement`, `CMapWorld`, `CMapVariableSet` | 405 | one each per map |
| `CVisibilityMgr` | 129 | visibility hints |
| `CMapPrefab` | 42 | prefabs |
| `CMapNavData` / `CDmeNavData` | 38 | nav |
| `CMapOpaqueDataBlob` | 2 | |

**14 distinct `CMap*` node types**, and 115 entity classes.

### What the compiler knows how to load

resourcecompiler's RTTI carries **111 `CMap*` classes**. Most are machinery, docs,
jobs, locators and proxies rather than saved nodes, but these are node types a map
could hold that **no map in the corpus uses**, so nothing here is tested against
them:

- `CMapCable` - cables
- `CMapSmartProp`, `CMapSmartPropInstance`, `CMapSmartPropShapeMesh`,
  `CMapSmartPropShapePath` - smart props
- `CMapTerrain` and 17 siblings - a whole terrain and layer system
- `CMapTileGrid`, `CMapTileMesh`, `CMapTileSet`, `CMapDotaTileGrid` - tile grids,
  which look like they belong to another game
- `CMapDeformerLattice`, `CMapDeformerPath`, `CMapDeformerSimple` - deformers
- `CMapWorldLayer` - map layers
- `CMapCordon`, `CMapSpawnGroup`, `CMapIsoSurface`, `CMapGrassTile`,
  `CMapNavLink`, `CMapChoreoAnchor`, `CMapBox`, `CMapPoint`

The honest reading: our entity walk handles the node types real maps are built
from, and has never been shown a cable, a smart prop, a terrain or a map layer.
`CMapWorldLayer` is the one to watch, because entities could sit under a layer
rather than directly under the world, and a walk that does not descend into it
would silently lose them.

Regenerate both lists with `tools/map-survey` and
`python tools/re/dump_asserts.py`.

## Entity lump: what still differs from resourcecompiler

Measured per CLASS across seven local map sources, because a total difference count
is dominated by whichever class appears most and says nothing about whether
TRIGGERS work. `EntityClassCoverageTests` reports it and ratchets it: every class
that differs has to be named in the test, so the list only shrinks.

| corpus | classes reproduced exactly | classes differing |
|---|---|---|
| 4 maps | 33 | 5 |
| + `s2probe/atixref` | 7 | 63 |
| after the fixes below | 61 | 9 |
| + the two prefab sources, 7 maps and 1,820 entities | **82** | **12** |

Widening the corpus is what made the work possible, and it first made the numbers
look far worse. That was honest: atixref is a real zombie escape map with 4,588
entities, 144 instances and 10 point templates, and the four-map figure had never
been asked a hard question.

Two of the seven sources live under `maps/prefabs/<parent>/` rather than beside
the map, so `MapFixtures.VmapSource` searches. They were being SKIPPED, and a
skipped map is indistinguishable from a passing one unless the harness says so,
which it now does.

The 82 include the classes a ZE map is actually built from: 39 `func_button`,
70 `func_breakable`, 17 `prop_door_rotating`, 16 `path_track`, 15
`trigger_teleport`, 10 `trigger_once`, 8 `trigger_hurt`, 5 `trigger_multiple`,
2 `trigger_push`, 1 `func_tracktrain`, 1 `point_teleport`, plus the filters,
`logic_*` and `math_counter` that wire them together.

### `compile_source_id` is the ordinal in the WALK, not in the lump (FIXED)

The key that made every class on atixref look wrong. Valve's ids there run 0 to
4,600 over a lump of only 764 entities, with a gap wherever a walked node was not
shipped, and **583 distinct ids across 764 rows** because instanced copies repeat
their source's id.

Our ids drifted 12 low from the first rope onward. The cause is that
`EditGameClassProps` does not hang off `CMapEntity` alone:

| node type owning game keys | atixref |
|---|---|
| `CMapEntity` | 4,588 |
| `CMapPathNode` | 8 |
| `CMapPath` | 4 |
| `CMapWorld` | 1 |
| total | **4,601**, which is Valve's id range exactly |

The walk now covers `CMapPath` and `CMapPathNode`, and every one of atixref's 565
matchable entities agrees with Valve's id. The number is taken before the filter
below and not after.

### Entities the compiler drops, and why

Valve ships 764 of atixref's 4,601 walked nodes. Every part of that gap is now
accounted for:

| cause | entities | status |
|---|---|---|
| `prop_static`, baked into the world | 3,952 | filtered |
| `path_node_particle_rope`, holds a path's shape | 8 | filtered |
| written to a CHILD entity lump instead | 57 | known, not built |
| `CMapInstance` template, the copies ship instead | 18 | known, not built |
| hidden in `CVisibilityMgr` | 1 | one specimen only |
| `game_weapon_manager`, no such class in CS2 | 28 (ze_hold_em_p) | known, not built |

The first two are **declared in the FGD**, which is what makes them safe to act
on. `prop_static` carries `metadata { static_prop = true }` and the five path node
classes carry `metadata { editor_only = true }`. `FgdSchema.HasFlag` reads them.

An earlier attempt at this returned empty and was reverted. The reason was NOT the
`@OverrideClass` merge theory recorded here before: the header slice always held
the metadata block, and `class_game_keys` was being read out of that same slice
successfully. The regex was at fault, and specifically a word-boundary escape that
a shell edit had written as a literal backspace byte, so the pattern demanded a
control character before `metadata` and never matched. Print a regex with
`cat -A` before concluding it is wrong about its input.

`game_weapon_manager` is an ordinary `@PointClass` with no marker, and RC drops
all 28 with no diagnostic. The reason is that **CS2 has no such entity class**:
the string occurs zero times in `server.dll` and `client.dll`, while
`func_button`, `point_template`, `prop_static` and `game_player_equip` all occur.
base.fgd is shared across Source 2 games and declares more than CS2 implements, so
the FGD is not the authority on what ships. The game's entity registry is.

### Child entity lumps

A map compiles to more than `default_ents`. atixref also gets ten
`maps/atixref/entities/<nodeid>#entitylumpname.vents_c`, and it has exactly ten
`point_template` entities. The template names its lump in `entityLumpName`
(`367#entityLumpName`) and the world it came from in `worldName`
(`maps\atixref`, backslash as shown).

The 57 entities in those lumps are the template's contents: lump `187` holds nine
`func_button`, two `point_soundevent`, a `logic_case`, two `trigger_teleport`, a
`point_teleport`, a `func_breakable` and a `logic_relay`. They keep the
`compile_source_id` their source node was given in the main walk, so the numbering
is already right for them.

### Instances expand, and the template does not ship

`CMapInstance` carries no children; it carries a `target` pointing at a `CMapGroup`
elsewhere in the tree. atixref has 144 placements over 15 distinct targets. Valve
ships one transformed copy per placement, each with its own `hammerUniqueId` and
**the template's** `compile_source_id`: id 670 carries 30 `light_omni2` and id 671
carries 30 `light_barn`. The template entities themselves are not in the lump.

That is what the 18 ours-only and 199 valve-only entities on atixref are, and it
is why 181 of Valve's ids are repeats.

### FGD type aliases (FIXED)

The FGD spells the same types more than one way, and reading an alias as a plain
string is a per-class difference:

| declared | is | evidence |
|---|---|---|
| `bool` | Boolean | `path_particle_rope_clientside.static_collision( bool )` ships Boolean 0 |
| `node_id` | Integer | `info_particle_system.snapshot_mesh(node_id)` ships Int64 0 |

An integer key holding a decimal is **truncated**, not left a string: one of
atixref's fifteen `func_door` carries `wait "0.600000"` against a `wait(integer)`
declaration and Valve's lump has Int64 0.

Still untested, with no evidence either way: `int` (17 uses), `vecline` (11),
`local_point`, `npcclass`.

### Integer width on spawnflags (FIXED)

A flags field ships UNSIGNED, **except that 0 and 1 ship Int64**, which is the
same exception `Integer` already made for ordinary integers and which had simply
never been applied to flags.

The evidence that it is the VALUE's rule and not the class's is one class holding
both: ze_hold_em_p's `func_door` has six `spawnflags 0` as `Int64` and seven
`spawnflags 6144` as `UInt32`. The source stores every one of them as a string,
so nothing is inherited from the map file.

`info_player_terrorist` and `info_player_counterterrorist` carry spawnflags as a
plain `String` because their classes do not declare the key, and those already
matched.

### Connection name fixup (FIXED)

Outputs carry two names that need the prefab fixup and were not getting it. Both
are now applied and connections match.

The **target** is prefixed unless it is an engine keyword: Valve's lump prefixes
144 of its 145 connection targets and the single exception is `!activator`, so
the rule is "prefix unless it starts with `!`", which also covers `!self`,
`!player` and `!caller`.

The **override parameter** is prefixed when it NAMES one of the map's entities.
resourcecompiler states this itself while compiling ze_hold_em_p:

    Parameter 'humans' corresponds to an entity target name, but is sent to input
    'SetDamageFilter' on 'boss' which is not marked as being a target name.
    (FGD Error?)

and writes `[PR#]humans` anyway. So the test is the map's own set of targetnames,
not the FGD's input declaration, which is why a purely schema-driven rule would
have missed it.

### The prefab name fixup: a name always, a reference sometimes

A `targetname` is fixed up whatever the class is, including one the FGD never
declares. `ze_doom_p2_c_gameplay` places four such classes
(`func_physbox_multiplayer`, `player_speedmod`, `prop_door_rotating_checkpoint`,
`ambient_music`) and Valve prefixes every one of their names while shipping their
other keys as plain strings.

A REFERENCE is not always fixed up. The same map wires `env_texturetoggle` at
`CacoDemonModel`, which is **no entity's targetname anywhere in the map**, and
Valve ships it bare while prefixing every reference beside it that does resolve.
That is the same shape as the rule already proven for an output's override
parameter.

Applying "prefix only when it resolves" to every name-typed key is nonetheless
WRONG, and the measurement says so rather than the reasoning: it fixes
`env_texturetoggle` and regresses `light_environment` from 18 differences to 30
and `point_template` from 36 to 49, and introduces three classes that had been
exact. `light_environment` is the clearest counterexample, since its
`ambient_occlusion_proxy_position_0` is declared `target_destination`, holds
`0 0 0`, and ships as `[PR#]0 0 0`. So some references are prefixed blindly and
some are tested, and which is which is not known.

Note that both regressed classes were already on the differing list, so the test's
ratchet would NOT have caught this. Compare per-class difference COUNTS before and
after a change to a shared rule, not just the set of failing class names.

### What still differs, with its reason

| class | entities | reason |
|---|---|---|
| the six light and probe classes | 326 | vrad3 writes its results back INTO the lump: `bakedshadowindex`, `light_map_uniqueid`, the probe atlas textures. Arrives with the lighting tier. |
| `beam_spotlight`, `env_sprite_oriented` | 74 | Valve ships every key as a plain String while we type them from the FGD that declares both. NOT established. Both appear only on the two prefab sources, and the leading guess is that instance-expanded entities carry their keys verbatim, which the instance work would settle. |
| `prop_physics_override` | 81 | Valve MOVES the origin, presumably to the hull's mass center, and sets a spawnflag with it: `[-292.998, -568.019, 270.668]` against the source's `[-293, -568, 283.255]`, and spawnflags 5 against 4. |
| `point_template` | 21 | needs `entityLumpName` and `worldName`, which need child lumps. |
| `path_particle_rope_clientside` | 4 | needs `pathNodes` and `pathNodeRadiusScales` built from the `CMapPathNode` children, which are already walked and numbered. |
| `env_texturetoggle` | 4 | the unresolved reference above. |
| `func_physbox` | 4 | `hoverposeflags`, which the compiler writes and we do not. |

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

## Entities: compiling them, not just re-authoring them

`EntityLumpAuthor` takes the entities out of a `.vmap` and writes the lump, which
is the first piece of a map this project COMPILES rather than round-trips. The
rules below were each found by diffing against `resourcecompiler.exe`'s own output
for the same source, and every one of them was a correction to what was assumed.

**The reference has to be compiled fresh.** An addon's existing VPK is whatever
Valve's compiler did the day it was built, and that changes: an April 2026 compile
writes an entity's `origin` as `"-210.738342 109.667847 8.100571"`, and an August
one writes `[-210.73834228515625, 109.6678466796875, 8.100570678710938]`. Both
load. A test pinned to the shipped artifact would pin this compiler to a version of
Valve's that no longer exists, so `MapFixtures.RcCompiledLump` runs the real
compiler on the source and compares against that.

**The compile writes the class's whole key set, not the source's.** A map saved
before a key existed still compiles with that key at its FGD default: `probe01`'s
worldspawn ships thirty-odd Steam Audio settings its `.vmap` has never heard of.
This is the correction that mattered most - the assumption in the prior art was the
opposite, that RC emits only keys differing from their default.

**What is dropped is an EMPTY value**, whether it came from the source or from a
default. Hammer writes an unset key as `""`, and an empty target or vector is what
the entity system chokes on. A key sitting at a non-empty default is kept, which is
why a spawn point's `priority 0` and `enabled 1` are both in Valve's lump.

**Types come from the FGD, and integers then follow the C1 rule.** `enabled "1"`
becomes a boolean, `priority "0"` an integer, a choices field stays a string, and a
`color255` or `vector` becomes an array. A float is parsed at 32-bit precision and
widened, so `0.1` lands as `0.10000000149011612` exactly as Valve's does. Integers
narrow the same way every other KV3 compile here does: 0 and 1 keep the singleton
codes, the rest is Int32 until it does not fit.

**Entity order is the world's child TREE, depth first** - not the order the
elements sit in the file. Valve's lump for `cardtest` runs 2138, 4, 5, 6, 7, which
is the tree; the file lists those elements far apart. `compile_source_id` is the
index in that order, and it is a NUMBER, while `hammerUniqueId` beside it is a
string.

**A choices field keeps its FGD spelling.** `rendermode` ships as the string
`kRenderNormal`, not as an index. This is a second correction to the prior art,
which has the compiler mapping each choice to its integer. A `flags` field is the
one place the integer rule bends: `spawnflags 2` ships UInt32, not Int32. A colour
ships as THREE components even when its type carries alpha and its value has four:
`point_worldtext`'s `color` is `color255alpha` defaulting to `"0 0 0 255"`, and the
lump has `[0, 0, 0]`.

**Reading the FGD is most of the work, and it has three traps**, each of which put
keys into entities that Valve's compile does not write:

- `//` comments must go first. csgo.fgd comments OUT env_sky's fog block, and a
  comment carrying a bracket unbalances the scan for a class body, so keys bleed
  from one class into the next.
- `@OverrideClass` MERGES into an existing class; any other class declaration
  REDECLARES it. csgo.fgd overrides `light_environment` to strip keys while it
  keeps everything `lights_base` gives it, and separately restates `env_sky`
  wholesale. Treating both the same way is wrong either way round.
- `remove_key` takes a key out of the schema entirely - no type, no default, not
  inherited. A value the source still carries then ships as the raw string it was,
  which is why `light_environment`'s `ambient_occlusion` is the string `"0"` rather
  than a boolean.

**A brush entity is pointed at a model the compile builds for it**, by a derived
path: `maps/<map>/entities/<lowercased targetname>_<node id>.vmdl`, and `unnamed`
in place of the name when it has none. The model itself is the brush tier; the key
is written now because the lump is what references it.

**Entity-name fixup is applied by TYPE, not by meaning.** When the world asks for
it, every `target_source` and `target_destination` value gets the `[PR#]` prefix
the engine resolves at spawn - including one that is not a name at all, since
`light_environment`'s `ambient_occlusion_proxy_position_0` is declared
`target_destination`, holds `"0 0 0"`, and ships as `"[PR#]0 0 0"`.

### What is left, and how it stays honest

`EntityLumpKnownGapsTests` runs the same full comparison on the maps that still
differ and fails on any difference OUTSIDE a written-down list, so a gap cannot
quietly grow and closing one is visible as the list shrinking. The list today:

- **Keys the bakes write back into entities**: a light's baked ids, a light probe
  volume's atlas textures, handshake and probe dimensions. That is vrad3's output,
  so it arrives with the lighting tier.
- **`nearclipplane`**: declared `remove_key` like `ambient_occlusion` beside it,
  yet it ships typed as a float while the others ship as strings. The FGD cannot
  tell those apart, so this is waiting on the engine's own schema rather than a
  guess.
Two that WERE on this list are closed, and both closed the same way: by finding
the answer in the FGD rather than inferring it.

**A point prefab is a declared class, not a resolved path.** An entity whose
`classname` looks like a map name - `counterterrorist_team_intro` - is an ordinary
`@PointClass`, and its metadata carries the keys every instance ships with:

```
class_game_keys =
[
    { key = "isPointPrefab" value = true },
    { key = "targetMapName" value = "prefabs/misc/counterterrorist_team_intro" }
]
```

That is exactly what Valve's compile writes into the entity, so the compiler reads
it off the schema. The first implementation here indexed every map under
`game/csgo/maps` and resolved the classname against it, which produced the same
two keys for the wrong reason and would have been wrong for any class whose game
keys are not a prefab path. `class_game_keys` is a general mechanism; treating it
as prefab lookup was a guess that happened to fit.

**`prefab_has_runtime_entity_by_default`** follows from the same thing: worldspawn
gets it when the map places at least one point prefab. probe01 places four and gets
the key though its source never mentions it; untitled_1 places none and does not.

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
