# Handoff: the map compile port, as it stands on 2026-09-28

For whoever picks this up next, cold. It gives:
- the goal and the rules the work runs under;
- where each module stands, with measured numbers;
- how to run and score things;
- what is open, in the order worth taking it.

The older vis-only handoff is [`HANDOFF_VIS.md`](HANDOFF_VIS.md); its section 4
(capture and replay tools) still applies.

## 1. The goal and the rules

The goal is Valve's CS2 map compile (resourcecompiler with its builders) ported
to C#, output matching Valve's bit for bit. Zombie escape maps are the target.
Branch `research/map-authoring`, remote `box`.

The rules the user set, all still in force:
- **Authoritative is law.** Only decompiled or measured facts. Anything
  inferred, assumed or fitted to output is written down in
  [`GROUND_TRUTH.md`](GROUND_TRUTH.md) and settled from the DLLs: a decompile, an
  in-process oracle test calling Valve's DLL, or a Frida capture of
  resourcecompiler. CS2 itself may be debugged, but ask first, and always run it
  with `-insecure`.
- **Full coverage.** Every entity class Hammer offers and every compile option
  Hammer or resourcecompiler offers is in scope, not only what the test maps
  use. [`COVERAGE.md`](COVERAGE.md) is the list.
- **VAC safety.** Never attach to or read cs2.exe's memory.
- **Compiles and captures only while CS2 is closed.**
  - Check `tasklist` first, and run the watchdog (a flag-file loop that kills
    resourcecompiler the moment cs2.exe appears).
  - Back up any package a compile overwrites, restore it after and `cmp` it.
  - Delete crash dumps after reading them.
  - Keep 10 GB free on D:.
- **One subagent at a time**, at most.
- **Pushing:** push to `box` only when asked. Commits end with the co-author
  line.
- **Writing style:** no em dashes, and comment blocks of at most 20 lines.
- **Addresses live in the repo:** every address read from a DLL goes into
  `tools/re/names/make_manual.py` under its DLL build. `address_doc.py` then
  writes [`ADDRESSES.md`](ADDRESSES.md), with each build's identity and where
  `src/` cites each address. The context around them (what was read, at
  which offsets and vtable slots) is in [`RE_NOTES.md`](RE_NOTES.md). Nothing
  is kept only in memory. `patch_check.py` re-finds every listed address after
  a CS2 patch (2026-09-28: all 1,109 identical in the installed builds).
- **After big tasks**, re-read each ported function beside Valve's decompile.
- **After a CS2 patch**, run `tools/re/patch_check.py`.

## 2. Where each module stands

Measured; the tracker (artifact 4whctxfaTdHEiE4d3oAVHA, collections `items`,
`modules`, `next`, `meta`) carries the same with per-item notes.

| module | state |
|---|---|
| Entity lump | Every lump of the 8 pinned maps and of both probe maps matches Valve's outside the lights' shape and bake keys and the probe volumes' bake keys; the settle is exact in the lump build. All 354 classes Hammer offers are measured through the probe map: 343 exact. |
| Physics | world_physics exact on all 9 maps with a current compile (decoded trees and container facts): atixref, cardtest, s2c_rounds, four ze_hold_em builds, c2m2's prefab, Mako. Physics-only entity models exact. |
| Visibility | Every stage exact against captured inputs, Mako included; the VXVS assembled from Valve's .rte is byte identical on three maps. The root cube now comes from the scene. Not yet run on a trace scene built from the .vmap. |
| Containers | Every container type authored; VPK writer byte for byte; `s2c compile-map` updates a compiled package from Hammer's command line. |
| Geometry (render) | Barely started: world nodes, render meshes, aggregation, overlays, detail. |
| Baked halves | Lighting, probes, cubemaps, shadows, nav, audio, bomb damage: vrad3 is driven as a stopgap; the port is not started. |

## 3. This session (2026-09-28)

- **Coverage list** ([`COVERAGE.md`](COVERAGE.md), `tools/coverage/`):
  - every FGD class, split into what csgo.fgd offers (354) and what it
    declares but excludes or never loads (82);
  - what the compile does with each of the classes it names;
  - every Hammer build switch with its dialog label, every map compile
    argument and every resourcecompiler option, each with its state.
- **Probe map** (`tools/coverage/probe_map.py`):
  - every offered class twice (bare, and with every key set), built on a copy
    of cardtest, round-tripped through Valve's dmxconvert and pinned against
    resourcecompiler;
  - it found six rules, each read from the DLL and ported: flags and tag_list
    defaults, the flags choice merge on redeclaration, the spawnflags mask,
    name fixup by key type, the `name` key, and the particle snapshot path;
  - a second probe, `probe_cable`, settled cable_dynamic's rendercolor (the
    node's tint).
- **Visibility root cube** ported from visbuilder: exact on four maps.
- **`s2c compile-map`:**
  - reads Hammer's line as resourcecompiler does and selects builders as
    CompileMap does;
  - runs the entities-only world step (settled lumps) and the physics step,
    replacing their files in the existing package as a `-fshallow` build does;
  - refuses what is not ported, naming it.
- **Found and recorded, not yet fixed:**
  - the settle does not port props with sphere or capsule shapes (s2c_rounds);
  - the .rte flag word's derivation (RTE.md, "Where the flag word comes from").

## 4. How to run and score things

- **Whole suite:** `dotnet test tests/Source2.Compiler.Tests` (516 tests; a
  few minutes).
- **Entity lumps against Valve:** `EntityLumpAgainstValveTests` prints
  `differs: <class> <n>` per map. `EntityClassCoverageTests` is the per-class
  ratchet. `ENTCOV_DUMP=<tsv>` dumps every difference with its FGD type.
- **Valve's compile of a map:** `MapFixtures.RcCompiledLump(s)` compiles into
  the scratch addon `s2c_rc_probe`, caches by compiler stamp, and refuses
  while cs2 or another resourcecompiler runs.
  - Run the watchdog beside it anyway: `scratchpad/watchdog.sh <flag>`
    (copy it from an earlier session's scratchpad if it is missing).
  - That addon lacks other addons' materials: a map whose world needs them
    (cardtest's doom meshes) compiles without its world physics there.
- **Probe maps:**
  - `python tools/coverage/probe_map.py <base.vmap> <fgd_keys.json> <fgd_classes.json> <out.vmap>`;
  - `fgd_keys.json` comes from `COVERAGE_KEYS=<json>` on `CoverageKeysProbe`,
    `fgd_classes.json` from `tools/coverage/fgd_classes.py`;
  - the probes live in `content/csgo_addons/s2probe/maps/` (not in the repo).
- **Coverage doc:**
  `python tools/coverage/coverage_doc.py <fgd_classes> <class_refs> <map_classes> <fgd_keys> docs/COVERAGE.md`
  regenerates the class half. Per-class notes are in
  `tools/coverage/class_notes.json`.
- **World physics:** `WPBUILD=<addon>|<map>|<compiled vpk>` on
  `WorldPhysicsAuthorTests` diffs decoded trees. Compressed bytes are not
  expected to match; trees and container facts are.
- **compile-map:**
  `s2c compile-map -i <content/csgo_addons/<addon>/maps/<map>.vmap> -outroot <dir> -entities [-phys] [-nosettle] [--accept-gaps] [--gpu]`.
  It needs a package already at `<outroot>/csgo_addons/<addon>/maps/<map>.vpk`.
- **Visibility inputs:** `VISOWN=1` on `VisOwnProbe` checks the root cube.
  `VisConfigDump` prints a map's .viscfg.
- **Decompiles:** `python tools/re/dec.py <dll> <addr>` through ReVa (see the
  reva-headless memory note to start it). Hammer is not in the Ghidra project;
  read it from the PE with capstone (`tools/re/disx.py`, `leascan.py`).

## 5. Open, in the order worth taking it

1. **Visibility on our own geometry** (the module's last item):
   - The .rte flag word from the mesh entry and the material attributes
     (RTE.md). With it, probe01 and cardtest can run end to end: their
     triangles are exact already.
   - The sun direction for the .viscfg: `-forward` of the directional light's
     world matrix, from the light description, for a light that passes the
     export's three checks (read in FUN_180244d30).
   - pvstype from worldspawn, and the visibility hints (every
     visibility_hint entity as the lump writes it) for maps that have them.
   - Subdivided faces in the trace scene through `SubdivisionBake` (exact for
     physics), then the rotated-instance ulps (a capture).
2. **Entity lump:**
   - The light export: shape keys, the angles rewrite and `directlight`.
     The precomputed keys are ported and exact against an empty scene; they
     need the editor's ray-trace scene (see the light-precompute memory note)
     and wiring into the lump.
   - The settle for sphere and capsule shapes.
   - World-layer instance angles, 1 or 2 ulps (parked; a capture of the
     reparent settles it).
3. **compile-map:**
   - What an entities-only build clears (the entities' models, flammables).
   - `-entities` with no package in place.
   - The world step once geometry exists.
4. **Containers:** the addon content a map references, compiled beside it
   (c-deps). A whole package from our own outputs waits on geometry and the
   baked halves.
5. **Physics leftovers:** prop bone overrides, props in CMapPrefab, the lattice
   deformer, the prop override keys, the bake's vertex merge and edge
   smoothing, and ze_hold_em_p's six doors.
6. **Stale comments:** some `Vis*.cs` comments cite addresses from the
   2026-07-09 visbuilder build; `ADDRESSES.md` has the current ones.
7. **Geometry (render)** and the **baked halves**: the two large remaining
   parts, both unstarted beyond what visibility and physics needed.

The ground-truth ledger's open list ([`GROUND_TRUTH.md`](GROUND_TRUTH.md))
runs alongside all of these: every rule still fitted to output rather than
read.
