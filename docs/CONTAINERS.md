# Map containers and packaging

What a compiled map is made of, the index layer that describes it, and the
formats around it:
- KV3 and RED2;
- the `.vmap` DMX reader;
- VPK packages;
- `s2c compile-map`.

World physics' own container rules are in [PHYSICS.md](PHYSICS.md).

## What a map is

Measured over 116 workshop map vpks (5.27 GB, `tools/map-survey/`):

| role | share of bytes |
|---|---|
| lightmaps | 39.6% |
| world nodes (render geometry) | 15.8% |
| cubemaps | 14.2% |
| world_visibility | 10.4% |
| world_physics | 7.9% |
| Steam Audio | 2.7% |
| entity models | 2.5% |
| nav | 0.3% |
| the index layer (`.vmap_c .vwrld_c .vwnod_c .vrman_c .vents_c`) | 0.27% |

Notes:
- **`.vmap_c`** carries no data. It is a RERL of every child plus a RED2
  child list.
- **Entity models:** 7,128 of 11,923 have no mesh at all
  (`CTRL DATA PHYS RED2`).
- **Workshop maps** are mostly KV3 v4 and some use Zstd; both load.

## The index layer

Authored by `ResourceManifestAuthor` (`.vrman`, byte-identical DATA) and by
`Source2ContainerAuthor.AuthorKv3Tree` / `AuthorMapRoot`
(`MapResourceAuthoringTests`).

The RED2 identities are identical in all 116 maps:

| type | resver | special dependency | fp |
|---|---|---|---|
| `.vwrld` | 1 | World Compiler Version, `CompileWorld` | 1 |
| `.vwnod` | 1 | World Node Compiler Version | 1 |
| `.vrman` | 1 | Manifest Compiler Version | 2 |
| `.vents` | 1 | Entity Lump Compiler Version | 3 |
| `.vvis` | 7 | none | |

Rules:
- **The map root's identity** is the union of its children's: a kind appears
  exactly when a child of that kind exists (580 observations, no
  exception).
  - Its RERL is the children plus what the map references without building,
    sorted ordinal.
  - `Texture Encode Quality` is a setting (user 3 or 4), and appears twice
    when textures were compiled at two qualities.
- **Map resources** are serialised C++ structures, so author them tree to
  tree: an integer's width is the struct field's, which KV3 text cannot
  express.
- **A child resource's RED2:**
  - no input dependencies;
  - `IsChildResource = 1`;
  - null subasset fields;
  - the 0923 compiler writes `m_SpecialInputDependencies` empty.
  - `___OverrideInputData___` is non-zero only on `world.vrman_c`.
- **`.vrman` DATA** is a self-relative three-level string list:
  ```
  i32 rel, u32 count -> outer; i32 rel, u32 count -> inner; i32 rel -> string
  ```

## KV3 and block compression

tier0's `SaveKV3` with `binary_auto` chooses per block:
- raw when the put sum is under 0x100;
- LZ4 while buffer plus blob maxput is under 0x80001;
- Zstd otherwise.

`Containers/AuthoredKv3.ChooseCompression` ports it. Some writers pass plain
`binary` or `binary_bc`, and which resource types those serve is not mapped
(GROUND_TRUTH 3).

A decoded scalar 0 or 1 comes back Int64, typed arrays stay UInt32, and
floats are doubles. Compressed bytes are encoder-defined, so compare decoded
trees (`KvTreeDiff`) and container facts.

## Reading the source: `.vmap` is DMX

- **Binary DMX** (`DmxBinary`): an attribute type above 32 is an array of the
  type below it. A string inside an array is written inline, even where a
  scalar string in the same section is a string-table index.
  - The reader must end exactly at the file's last byte (`DmxBinaryTests`).
- **Keyvalues2 text DMX** (`DmxText`) reads into the same document:
  - Id and name are element header fields.
  - The prefix element sits at index 1.
  - dmxconvert writes floats to 7 significant digits and upgrades the vmap
    version.
- **Prefabs** are loaded by `MapPrefabs.Attach`: nested prefabs when
  `loadIfNested`; `loadAtRuntime` ones are skipped.

## VPK packages

`Io/VpkWriter.cs` rewrites Valve's single-file VPK v2 byte for byte:
- data in ordinal path order;
- tree names in reverse order of first appearance;
- a chunk section per MiB (BLAKE3 cut to 16 bytes);
- MD5s and an empty signature section.

PHYSICS.md has the details. `tools/pipeline/splice_map.py` rebuilds a map vpk
with entries replaced.

## `s2c compile-map`

`Maps/MapCompile.cs` and `MapCompileArgs.cs` take Hammer's resourcecompiler
command line and run the ported steps on an existing package, as a
`-fshallow` build does:
```
s2c compile-map -i <content/csgo_addons/<addon>/maps/<map>.vmap> -outroot <dir> -entities [-phys] [-nosettle] [--accept-gaps] [--gpu]
```

- **The package** must already be at
  `<outroot>/csgo_addons/<addon>/maps/<map>.vpk`.
- **The steps:** the entities-only world step (settled lumps) and the
  physics step, each replacing its files in the package.
- **Refused, by name:** a full world build, nav, Steam Audio, `-f` and
  `-fshallow2`, and lumps with light or probe gaps unless `--accept-gaps`.
- **Parsing:** the line is read as RC reads it: its own switches first, then
  generic ones typed `%f`, then `%i`, then string. Builders are selected as
  `CompileMap` does.
  - The default builders are world, phys, vis, bakedlighting, nav, sareverb,
    sapaths and sacustomdata.
  - Hammer's `-bakelighting` is not the builder `bakedlighting`.
  - `-nolightmaps` and `-vpkincr` do nothing.
- **Hammer's own switches:** hammer.dll is not in Ghidra; read it from the
  PE with capstone (`tools/re/disx.py`). It checks its switches as
  MurmurHash2 of the lowercase name, seed 0x31415926.

Open (GROUND_TRUTH 24):
- what an entities-only build clears;
- `-entities` with no package in place;
- the world step once geometry exists;
- compiling the addon content a map references.

Every option is listed in [COVERAGE.md](COVERAGE.md).

## Resourcecompiler recipes and traps

- **Visibility** is built by `-world`, not `-vis`. `-vis` without `-world`
  guts the vpk, and `-vis -f` fails.
- **The up-to-date check** uses the source CRC, so delete the output vpk to
  force a rebuild.
- **Intermediates** (`.rte`, `.viscfg`) live in
  `%TEMP%\csgo_addons\<addon>\maps\`.
- **Missing inputs:** materials are fatal, models are not. A stub material
  must be compiled before the map. Tool materials live in `game/core`.
- **Loading an addon map:** it needs `addoninfo.txt`, and a local addon map
  will not load in the normal game. Use `-tools -insecure` with netcon
  (`tools/pipeline/map_test.py`, `cs2_console.py`).
- **Only a full compile** (no `-fshallow`) ships world_physics.
