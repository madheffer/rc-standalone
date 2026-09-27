# Caveats: everything this project learned the hard way

Traps, corrections and measurements that cost real time to find. Written so the
next person, or the next session, does not pay for them twice. Each entry says
what was believed, what is actually true, and how it was settled.

Companion documents: `PLAN.md` (where this is going), `VIS.md` (visibility),
`VRAD3.md` (lighting), `MAP_RESOURCES.md` (map file formats),
`VISBUILDER_FUNCTIONS.md` and `RESOURCECOMPILER_FUNCTIONS.md` (recovered symbols),
and `VISBUILDER_ANALYSIS.md` (how visbuilder.dll was read, what its design is,
and which of its decisions our own visibility code had to adopt).

---

## Corrections we had to make to our own conclusions

These were stated confidently and were wrong. They are listed first because the
failure mode matters more than the fact.

### Visibility is 84% of a map compile, not 30%

**Was claimed:** visibility is about 400 of 1,313 seconds on Mako Reactor.

**Actually:** resourcecompiler prints `Visibility complete in 1088.67s.` on a
1,299 second compile. **83.8%.** Lighting is 16.02 seconds, 1.2%.

**Why the error:** the 400 was assembled by summing the stages we had already
recognised, and it silently missed the LOS ray scan, 568 seconds. **Quote the
number the tool prints about itself; never total up the stages you happen to
know.**

### Lighting is not the expensive part, which is why this project pivoted

The whole effort started from "lighting takes a day, put it on a GPU". Measured:
lighting is 9.33 s of Mako's compile at default settings, and the sample count
scales linearly at 0.344 s per sample, so even a 32x quality increase only
reaches 176 s. Sample count is not a hidden multiplier. Full working in
`VRAD3.md`.

### A "798 second stage" that never existed

The first compile profile reported 798 seconds after `Building ray trace
environment...`. It is an artifact: **resourcecompiler block buffers stdout when
it is piped**, so 5,733 Mako lines arrive in a handful of bursts and every gap
lands on whichever line preceded a flush. `profile_compile.py` now prints the
tool's self-timed numbers first and marks any gap ending in a burst as
`BUFFERED FLUSH, not one stage`.

### A cost model fitted to endpoints that its own interior contradicted

`lightmap_compute_block_gpu X Y T` looked like `cost = a*min(X,Y) + b*Y`. Fitted
to the endpoints it predicts 16.7 s for `32:64`, which measures 19.67, and
overpredicts `128:512` by 6%. The law was recorded as **unknown** rather than
published. The diagonal (X = Y) IS linear at 0.344 s/sample and that is what is
claimed.

### Tests that passed against fixtures they never found

`MapFixtures.Resource` returned null, tests returned early, and the suite went
green while verifying nothing. **A gate that self-skips is not a gate.** The
fixtures now throw when workshop maps ARE installed but a specific one is
missing, and every corpus test prints the map count it actually covered.

### "Deterministic" measured on runs that never ran

Two rebuilds reported identical hashes, which looked like proof the vis builder
is deterministic. Both had silently skipped: RC printed no stage lines and left
the previous file in place. The harness now says
`NO VIS STAGES RAN` loudly. With genuine rebuilds the builder IS deterministic,
but that is a different sentence.

### MeanVisibleFraction counted padding

A PVS row is padded to a multiple of four bytes, so a two-cluster skybox reported
"16" as a fraction. Count only the first `BaseClusterCount` bits of each row.

### A prefab index that was right for the wrong reason

An earlier filesystem-derived prefab index produced correct entity output. The
FGD's `class_game_keys` metadata is the actual authority. The index was deleted.

---

## resourcecompiler and the map build

### Visibility is built by `-world`, not by `-vis`

Run both and every vis stage prints under `Building 'world'` while
`Building 'vis'` prints nothing at all.

### `-vis` without `-world` produces a GUTTED VPK

It repacks without the world content. It turned Mako's 74 MB map VPK into
**84 KB** and did not rebuild visibility. Never run `-vis` alone.

### `-vis -f` is a hard FAIL, not a warning

The message reads like advice (`DANGER! Running partial map compile ... You
should use -fshallow.`) but the compile aborts. The force flag must be
`-fshallow`.

### The up-to-date check is on source CRC, not timestamp

`touch`ing the `.vmap` forces nothing; that is the same CRC a compiled resource
records in its RED2. **Delete the output VPK** to force a rebuild.

### The working recipe

```bash
rm "<game>/csgo_addons/<addon>/maps/<map>.vpk"
resourcecompiler.exe -nop4 -game <game/csgo> -i <content>/maps/<map>.vmap \
    -world -vis -fshallow
```

ze_hold_em_p: 13.5 s total, 8.2 s of it visibility, against 50 s for a full
compile. `tools/vis/rebuild_vis.py` does this.

### Intermediates live in `%TEMP%`, not the game tree

    %TEMP%\csgo_addons\<addon>\maps\<map>.rte      23 MB on Mako
    %TEMP%\csgo_addons\<addon>\maps\<map>.viscfg   1,308 bytes

They survive the compile. Searching the CS2 tree for them finds nothing, which
cost a whole round of "the intermediates are deleted".

### RC's own help does not list the map phases

`-world`, `-vis` and `-vis_preview` are registered by the map compiler, not in
the global help. `-fshallow`, `-novpk`, `-skiptype` and `-vpkincr` are listed.
Most "options" recovered by scanning the binary for `-xx` patterns are
compression noise, not switches.

### An addon without `addoninfo.txt` is invisible to the game

Compiling into `game/csgo_addons/<addon>/` produces a working map VPK but no
`addoninfo.txt`, and every addon the game actually mounts has one. A map addon's
is minimal, just an empty KV3 document with the generic format header. Without it
the map is on disk and the engine will not find it.

### The VConsole protocol, and the two things that block a first attempt

`magic(4) + u16 version + u32 length` big endian, length counting the 10 byte
header. Packet types seen: `AINF` `CHAN` `PRNT` `ADON` `CVRB` from the game, and
`CMND` to it.

The version field **must be 212**. Sending 0 is refused with
`Message Version Mismatch: 'CMND', Expected 212, Got 0`, and the value is sitting
in plain sight in the `AINF` the game sends on connect.

A `CMND` body starts with **two bytes of padding** before the nul terminated
command. Without them the game parses from the third byte and answers
`Unknown command: ho` for `echo hi`, which is how the offset was measured.

### A local addon map will not load in the normal game

`map <name>` answers `invalid map name` for anything under `csgo_addons` unless
the addon is mounted, and engine2 carries
`AddonConfig/RestrictFlatFileAddonsToTools`, so flat file addons are tools only.
Dropping the map VPK into `game/csgo/maps` loads it with no addon at all, which is
enough to test the map itself, but the addon's materials are then not mounted and
the map renders magenta. Valve's own build of the same map does this too, so
compare against it before concluding anything about your own output.

### Driving a running CS2 needs `-netconport`

Console commands cannot be injected into a running instance otherwise: CS2
launched normally opens no listening socket. `-condebug` is still worth having
because it mirrors the console to `game/csgo/console.log`, which is readable from
outside, so a person typing one command gives a full diagnostic back. Addons are
enumerated at startup, so a newly created one needs a restart regardless.

### `-novpk` hangs on a map

Documented as "generate loose files for the map resource and its children instead
of generating a vpk", and it never returns: 35 minutes at a constant 532 MB
producing no output and no files, on a map that compiles in 45 seconds. The same
command without `-novpk` is fine. Write the VPK yourself instead;
`tools/pipeline/splice_map.py` does, and ValvePak reads the result.

### Materials are fatal, models are not

A missing material aborts the compile, LATE: on Mako it spent 22 minutes on
lights, voxelisation and visibility and only then stopped at the first mesh whose
material was missing. A missing MODEL is worse in one way: the compile proceeds
and the prop is simply absent from the ray trace scene, so lighting and timings
measured from it are a lower bound. Mako is missing 282 models.
`tools/vrad3/check_references.py` answers this in seconds, before the compile.

### A stub material must be COMPILED before the map

A stub sitting in the content tree is invisible to the map build, which resolves
materials as compiled resources. The error stays "referencing missing material"
and looks like the stub did not work.

### Tool materials live in `game/core`, not `game/csgo`

`toolsnodraw`, `toolstrigger` and friends. Reading only `csgo` reports them
missing on every map ever made.

---

## Visibility

### It is not a file you can swap

Compiling one map twice, identical but for `BaseVoxelSize`, changes **11 of 72
files**: visibility, six world node `.vmdl_c` with all their mesh buffers, the
world node index, the world, the entity lump, and a light probe octree. The stage
`Splitting geometry using visibility...` is why. Shipping a new `.vvis_c` beside
old geometry ships an inconsistent map, and that is the most likely shape of the
failure the cs2map project hit.

### The `.los` cache is inert and cannot be switched on

The writer is called unconditionally every build and returns immediately because
the hint set is empty; nothing in the shipped path fills it. `-debuglos` only
widens an in-function cap (`0x200000` to `0x2000000`). Confirmed statically and
by running a compile with it. The loader DOES work, so authoring the format is
the only route in. See `VIS.md`.

### PVS bit order is least significant first

Settled, not assumed: read LSB first and 4,095 of ze_aztecnoob_p's 4,096 clusters
see themselves; the other way, 3,136 do.

### Not every cluster sees itself

A build can allocate a cluster id nothing occupies, and its PVS row is then
entirely zero. 13 of 51,066 clusters over 20 maps. Cluster 0 is the fallback and
sees everything.

### Nearly every real map has exactly 4,096 clusters

That is `MaxVisClusters` clamping, not map size.

### Uniform bounding-box sampling is useless on a map

0.57% of points land in a cluster, because a map's bounding box is almost
entirely solid or outside, and what survives is biased toward large open volumes.
Sample the octree's occupied leaf cells weighted by volume instead: 100% placed.

---

### nodraw has to stay invisible in game

A nodraw surface is a surface the renderer must not draw. It still exists for
everything else: it blocks, it seals, and visibility is built from it. Anything we
author has to keep both halves of that, and getting it half right is worse than
either extreme, because a nodraw face that renders shows as a solid slab across
the level and one that stops sealing opens the map up.

This is not hypothetical for the vis work. `visbuilder.dll` logs

    Vis geometry appears to be mostly nodraw (%.2f%%), reconfiguring... %s

from the function that converts the ray trace scene (`180049e00`), so the builder
measures how much of the geometry it was handed is nodraw and CHANGES WHAT IT DOES
when there is too much of it. Whatever we feed a visibility build has to carry the
same distinction, and our own compiled output has to mark those surfaces so the
renderer skips them.

Where it touches what is already built:

- `RayTraceEnvironment` skips triangles whose flag word is `0x0800`, which is what
  reproduces the compile's own "Convert RTE with 4536 triangles" on three maps.
  Whether that flag IS the nodraw bit is NOT established; it is established only
  that the trace conversion drops those triangles.
- The entity lump work has never had to think about it, because materials are not
  entity keys. The geometry tier will, and so will anything that authors a surface.

Owed: confirm what the `0x0800` flag means against a map with known nodraw
brushes, and confirm that a surface we author as nodraw both fails to render and
still seals.

## Formats and authoring

### A map resource cannot be authored from text

RC serializes a C++ struct, so an integer's width is the field's, not the
value-based rule RC applies to a KV3 source file. A world's
`m_nCompileTimestamp` ships `UInt32` for a value the by-value rule types
`Int32`, and KV3 text cannot express the difference. Decompiled text also rounds:
`0.1f` reads back as `0.10000000149011612`. Author from a typed tree, and round
trip tree to tree.

### Byte equality is the wrong verdict for KV3

KV3 binary has several valid encodings of one tree (string table order,
compression choice), so our writer reproducing the TREE while differing in bytes
is a pass. `s2c reauthor` reports bytes, decoded tree, references and resource
version separately.

### RED2's `m_nFingerprint` changes with the install root

The same map compiled from the real install and from a shadow install produces
**byte-identical DATA and VXVS** and a different RED2 fingerprint. Hash DATA and
VXVS when you mean "the same visibility"; `vis_digest()` does.

### A child resource carries a different RED2 shape

No input dependencies, no `m_SpecialInputDependencies` key at all,
`IsChildResource = 1`, null subasset fields. Identical in all 116 maps.
The 0923 compiler writes `m_SpecialInputDependencies` empty on child resources
too, and a physics or world node manifest names the file RC saved it from
(docs/WORLD_PHYSICS.md); the authoring follows the current compiler.

### `.vmap_c` carries no map data

Its DATA block is zero bytes in every one of 116 maps. The map root is a pure
manifest; the map itself is `world.vwrld_c`.

---

## Tooling and environment

### Never `rm -rf` the shadow install from git-bash

`D:\cs2-shadow` is built from junctions and hardlinks. A recursive delete can
follow the junctions into the real CS2 install. Remove it with
`new-shadow-cs2.ps1 -Force`, which deletes reparse points without descending, or
`rmdir /S` from cmd.

### Redirecting RC's stdout and waiting for exit deadlocks

It fills the pipe buffer and hangs, which looks exactly like a slow map. Read the
pipe as output arrives.

### `strings` is not on this box

Neither on the host nor in the images. Parse binaries with Python.

### javac expands `\u` inside comments

A Windows path in a Ghidra script comment (`C:\...\utils\...`) is a compile error
reading `illegal unicode escape`. Write paths with forward slashes.

### Ghidra dies saving a 56 MB binary at the default heap

`resourcecompiler.dll` analyses but exits 255 while saving, at the default
`MAXMEM_DEFAULT=2G`. It DOES run post scripts first, so an inventory written by
one survives while the applied renames do not. Raise `GHIDRA_HEADLESS_MAXMEM`, or
use `tools/re/dump_asserts.py`, which needs no disassembler at all and recovers
the same names, files and lines for every binary in a second. Addresses are what
it cannot give you.

### Ghidra will not find x64 MSVC RTTI cross references

Those links are 32-bit RVAs, not pointers, so `getReferencesTo` on a type
descriptor returns nothing and a vtable walk fails. This is why
`CLargeClusterRegionsRayGenerator` is still unexplained.

### Bash heredocs eat backslashes

`\\n` inside a quoted heredoc arrives as a real newline and splits C# and Java
string literals across lines. Use `chr(92)`, the Edit tool, or forward slashes.

### Kill stale test hosts before rebuilding

Orphaned `testhost` and `resourcecompiler` processes hold the DLLs open.
