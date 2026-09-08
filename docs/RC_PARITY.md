# resourcecompiler.exe parity — what matches, what does not, and why

> This is the working audit as written, kept verbatim rather than rewritten for
> publication, because its value is that it is a record of measurements. A few
> names in it (`GenerateJobRunner`, `SoundOverrideApplier`, the glove and
> hitmarker test suites, the "base packs") belong to the content pipeline this
> compiler was extracted from and are not part of this repository. Everything
> stated about the compiler's own output applies here unchanged, and the tests
> that pin it are in `tests/Source2.Compiler.Tests/`.

Audit date: **2026-08-10**. Oracle: CS2's own `resourcecompiler.exe` (Workshop
Tools) driven by `tools/rc-oracle.ps1`, outputs compared leaf-by-leaf
against ours. This file records the measured state so the next person does not
have to re-derive it.

The contract we hold ourselves to is **decoded-tree identity**, not byte
identity. The reasoning is in "Why byte identity is not the target" below; it is
not a shrug, it is a measurement.

## Scoreboard

| Type | Container identity | RED2 | RERL | DATA tree | Donor-free |
|---|---|---|---|---|---|
| `.vsndevts` | matches | matches | n/a | **identical** | yes |
| `.vdata` | matches | matches | matches | **identical** | yes |
| `.vpcf` | matches | matches | superset (see G3) | **differs — G2** | yes |
| `.vagrp` | unknown (G4) | — | — | — | yes |
| `.vtex` | **authored (C4)** | **authored (C4)** | n/a | n/a (binary) | yes |
| `.vsnd` | **authored (C6)** | **authored (C5)** | dropped (correct) | n/a (binary) | yes |
| `.vsvg` | **authored (C6)** | **authored (C5)** | n/a | n/a (binary) | yes |

**No donor of any kind is required.** Every compiled type describes its own
compile AND authors its own container frame. A template is still accepted for the
binary types, to carry a detail this compiler does not infer (a packed normal
map's mip algorithm, say); compiling with one and without gives byte-identical
output otherwise. The one real donor left is a material's vertex input signature,
which follows its shader's feature combo and so is stated rather than guessed.

Pinned by `Source2ContainerAuthorTests` against the real compiler outputs in
`tests/Source2.Compiler.Tests/RcReference/`.

## Closed

### C6 — the container frame is authored, so no type needs a donor (fixed 2026-09-08)

C4/C5 stopped the three binary types from shipping a donor's *metadata*, but
they still lifted the donor's *frame*: the header, and the block layout. That was
never a metadata leak, but it did mean `.vtex_c`, `.vsnd_c` and `.vsvg_c` could
not be produced at all without some compiled file of the same type on hand -
awkward exactly when it matters, since a source file often has no donor to take.

What a frame was actually contributing turned out to be one 16-bit integer, the
resource version, which upstream VRF had no public setter for. With the
`vrf-resource-version-settable` patch it is stated outright, and the rest of the
frame is block construction this code was already doing:

| type | authored version | authored blocks | sample it was measured against |
|---|---|---|---|
| `.vtex_c` | 1 | `RED2 DATA` | 200 of 200 stock textures agree |
| `.vsvg_c` | 2 | `RED2 DATA`, empty name table | 400 of 400 stock vector graphics agree |
| `.vsnd_c` | 4 | `RED2 DATA` | 93 of 200; the other 107 are v5 (`+ CTRL`), which `ModernizeVsnd` produces from this |

The numbers live in `ContainerFacts.cs` next to how they were taken. A template
is still *accepted* everywhere, to carry a detail this compiler does not infer,
and `Texture_AuthoredAndTemplated_AgreeByteForByte` /
`VectorGraphic_AuthoredAndTemplated_AgreeByteForByte` pin that the two paths
agree. `s2c selftest` compiles every supported type on a machine with no CS2
installed, which is the check that this stays true.

### C1 — integer widths (fixed 2026-08-10)

Every KV3 compile we had ever shipped typed integers wrong. Our KV3 text reader
returns `UInt64` for unsigned literals and `Int64` for negative ones, and that
type went straight to the binary writer. RC never emits `UInt64`. Nothing caught
it because the parity test compared container structure but never the decoded
DATA tree.

The rule, measured across five probes at every boundary:

**Ordinary value position** (object field, or an element of a mixed array):

| literal | RC type |
|---|---|
| `0`, `1` | `Int64` — the dedicated INT64_ZERO / INT64_ONE type codes, no payload |
| fits signed 32-bit (incl. `2147483647`, `-2147483648`) | `Int32` |
| `2147483648`, `-2147483649`, `4294967295`, int64 max | `Int64` |

**All-integer array** — RC writes a TYPED array, which changes the rule twice:

- the singleton codes are unavailable: `[0, 0]` and `[1, 1]` are `Int32`, not zero/one codes
- the element type is the widest any element needs, applied to all: `[7, 5000000000]` is `Int64, Int64`, not `Int32, Int64`

A **mixed** array is not typed and keeps ordinary rules — probed `[0, "s"]`
keeps the singleton, `[true, 7]` keeps `Int32`. Empty arrays are unaffected.

Implemented in `Source2ContainerAuthor.NarrowIntegers` (applied to both DATA and
our authored RED2). No VRF patch was needed: VRF's writer already emits INT32
and the INT64 zero/one codes, it was only ever being handed the wrong types.

Fixture `rc_probe_ints.vdata` carries every discriminating case and is compiled
by rc-oracle, so a CS2 toolchain change to the rule fails the gate.

### C5 — `.vsnd` and `.vsvg` no longer ship the donor's edit info (fixed 2026-08-10)

The last two donor-metadata leaks, same shape and same fix as C4. Ground truth
dumped per type from real resourcecompiler compiles (an SVG and a PCM WAV) —
deliberately NOT reusing vtex's fingerprints, which are per-compiler:

| type | resver | special dependency | input dependencies RC records |
|---|---|---|---|
| `.vsvg` | 2 | `Vector Graphic Version` / `CompileVectorGraphic` / 2 | the `.svg` (real CRC) + a same-named optional `.vsvg` |
| `.vsnd` | 5 | `Sound Compiler Version` / `CompileSound` / 1 | the real source, plus optional probes: a per-tree and per-folder `encoding.txt` and same-named `.fbx` / `.mp3` / `.rts` / `.txt` / `.vsnd` |

The sibling probes ARE mirrored here (unlike vtex's argument-dependency tail),
because each is an honest statement that a file does not exist — CRC 0, optional,
not-exists — rather than an invented option value. RC lists dependencies sorted
by path, so the real source is not necessarily first; the author sorts to match.

Verified by feeding both outputs back through Valve's `resourceinfo.exe`: the
`.vsvg` reproduces RC's two-entry list exactly, and the `.vsnd` reproduces all
eight entries in the same order. The `.vsnd` pin also asserts the edit info
survives `ModernizeVsnd`, which is what the override path actually ships, and
which reorganises blocks into RC's v5 `RED2 + DATA(0) + CTRL` layout.

**`RebuildSound` is deliberately untouched, and is NOT a donor path.** Its
"template" argument is the pack's OWN `.vsnd_c` being volume-scaled in place, so
its RED2 legitimately belongs to that clip — replacing it would destroy genuine
metadata, the mirror image of the bug being fixed. Same for a `.vsnd_c` input to
`SoundOverrideApplier.Convert`, which only modernizes the container.

Callers now pass their real ship path (`GenerateJobRunner` for killfeed icons,
`SoundOverrideApplier` for weapon clips), so the recorded identity is meaningful
rather than the neutral `vpkeditor/` fallback.

### C4 — `.vtex` no longer ships the donor's edit info (fixed 2026-08-10)

`BuildTexture` authored the whole DATA block itself but kept the embedded
`hitmarker_vtex_template.vtex_c`'s RED2 verbatim. Dumped with Valve's own
`resourceinfo.exe`, every texture we have ever compiled — skin composites, glove
textures, hitmarkers, `/compile` uploads — shipped declaring:

```
m_RelativeFilename = "materials/mac/hud_hit_marker_hs.vtex"
m_SearchPath       = "csgo_addons/c"
m_nFileCRC         = 3986264652
```

a third party's source paths and their file CRCs, plus their argument
dependencies. Same defect class as the 2026-07-26 KV3 donor bug, milder only
because the donor is at least type-correct.

`Source2ContainerAuthor.BuildTextureEditInfo` now authors it. Shape dumped from a
real RC texture compile (probe in `tools/rc-oracle.ps1`), with the three special
dependencies verified identical on two independent samples — our probe and the
donor itself:

| m_String | identifier | fp | userData |
|---|---|---|---|
| `Texture Compiler Version` | `CompileTexture` | 11 | 0 |
| `Texture Compiler Version Mip None` | `CompileTexture` | 1 | 0 |
| `Texture Encode Quality` | `CompileTexture` | 1 | 3 |

("Mip None" is not a claim that mips are absent — it reflects an unset mip
ALGORITHM, and both real samples carry it while both have mip chains.)

**Only provenance is replaced.** The template's `m_SpecialDependencies` ride
along verbatim, because they describe the DATA's ENCODING SEMANTICS rather than
who wrote the source. A stock glove normal template carries
`Mip HemiOctAnisoRoughness`; dropping it makes every consumer decode the normal
as plain RGB with roughness read as Z — the "black patches" class. The first
revision of this change authored the generic set unconditionally and
`GloveSurfaceMapShipTests` failed it, which is the gate doing its job. The table
above is the fallback for a template carrying none, and it is what the hitmarker
donor already had, so the skin/hitmarker path is unchanged in that field.

**Deliberately not reproduced:** RC's per-option `m_ArgumentDependencies` tail
(`fast`, `final`, `hueShiftFixup`, `lightmapMaxResolution`, `maxmipsize`,
`minmipsize`, and the ImageArg/BinaryBlobArg entries naming the source). Those
encode options from a `.vtex` source file our composites never had; inventing
values would be fabricating metadata rather than fixing it. Only
`___OverrideInputData___`, which every RC compile of every type carries, is
emitted.

Verified by round-tripping the output back through Valve's `resourceinfo.exe`:
it parses the authored RED2 field for field and reports our source path and CRC
with no donor trace. The container frame is authored too (C4/C6), so a donor file
is optional throughout.

**This moved the `bc7_texture_*` and `glove_normal_container_*` goldens** (the
RED2 is inside the hashed bytes). All four win-x64 variants were re-baked
deliberately via the documented procedure; `glove_vmdl_dataonly` was unaffected,
as it exercises the model re-serialize path rather than the texture builder.

### C3 — stale RERL ids after a rename (fixed 2026-08-10)

`ResourceBuilder.RewriteParticlePaths` renamed a RERL entry without re-hashing
its id, so the entry kept pointing at the pre-rename asset. Both shipped
hitmarker particles carried it: `particles/gfl/hitmarker.vpcf_c` named
`materials/vpkedit_hm/hud_hit_marker.vtex` under the id of the template's
original `materials/mac/...` path. It was the only rewrite site in the pipeline
that renamed without re-iding — `BuildSkinMaterialFromTemplate`,
`VmdlMeshGroupPatcher`'s anim-include repoint and `ModelSwapStage` all already
did it, and its comment ("updating Name in place is enough") was reasoning about
string offsets, which is true and beside the point.

Note on severity: hitmarkers do render in game, so a stale id is evidently not
the hard FATAL that a stale id in a *model* draw-call material is. The
in-game consequence of this specific case was not established. What is
established is that it deviates from an invariant Valve's own content holds
without exception, and from what the rest of our pipeline does.

### C2 — flagless resource refs in the authoring builders (fixed 2026-08-10)

`ResourceBuilder.ApplyMaterial` wrote `m_textureParams[].m_pValue`, and
`ApplyModel` wrote `m_materialGroups[].m_materials[]`, as plain KV3 strings.
Stock compiles store both as `resource:"..."`. CS2 reads a flagless ref as a
bare string, fails to resolve it, and renders the error material. See
the 2026-08-10 reference audit.

## The reference invariant

Separate from RC parity, and worth stating on its own because it caught C2 and
C3: a compiled resource states each external reference **twice** — as a
`resource:`-flagged string in DATA, and as a RERL entry pairing that path with
its 64-bit engine path-hash id. Both halves must agree.

Measured 2026-08-10 across CS2's `pak01` (3953 resources carrying flagged refs,
`vmat_c` + `vmdl_c` + `vpcf_c`) and both live base packs (176 and 336): **zero**
missing RERL entries, **zero** id mismatches, **zero** flagless refs. The rule is
Valve's, not a house convention — and the sweep doubles as an independent
confirmation that `Source2ResourceId.ForPath` reproduces Valve's ids exactly,
3953 times over.

`ResourceRefIntegrityTests` pins both directions: our builders' output must hold
it, and a trimmed sweep of the installed game asserts stock still does, so a CS2
change to how references are stated fails loudly instead of being papered over.

## Open gaps

### G1 — KV3 binary v4 vs v5 (cosmetic, not planned)

RC emits KV3 binary **version 5**; VRF's writer emits **version 4**. v5 splits
the payload into two independently sized/compressed buffers with about a dozen
extra header fields (`BinaryKV3.cs`, `if (version >= 5)`). Both decode to the
same tree and CS2 reads both — every KV3 generation stays loadable, which is why
years-old community containers still work. Closing this means writing a v5
serializer in vendored VRF, and it buys nothing the engine can observe.

### G2 — `.vpcf` legacy-source schema migration (real, unimplemented)

RC does not merely recompile a `vpcf26` source, it **migrates** it to the current
particle schema. Measured on `rc_probe_refs.vpcf`:

| source (vpcf26) | RC output |
|---|---|
| `C_INIT_RandomLifeTime` + `m_fLifetimeMin/Max` | `C_INIT_InitFloat` + `m_InputValue.m_flLiteralValue` |
| `C_OP_DistanceToCP` | `C_OP_DistanceToTransform` |
| `m_bAdditive = 1` | `m_nOutputBlendMode = PARTICLE_OUTPUT_BLEND_MODE_ADD` |
| `m_hMaterial = resource:"..."` | `m_vecTexturesInput[0]` |
| format GUID `26288658-…` | `e41b57dd-…` |

We do not implement the migration, so a **raw legacy** `.vpcf` source compiles to
the legacy schema under its own declared GUID. Whether CS2 still honours a
legacy-GUID particle is **unverified** — it needs an in-game check on a local map
(GFL servers do not honour content-VPK particle overrides). The normal product
path is unaffected: decompiling a compiled `vpcf_c` yields already-migrated text,
and recompiling round-trips it.

The parity test exempts `.vpcf` from the tree comparison for exactly this reason
and says so at the assertion.

### G3 — RERL superset on unresolvable refs (benign, understood)

RC consults its content tree while compiling and silently drops a
renderer-material ref it cannot resolve, while keeping unresolvable child-vpcf
refs. We have no content tree, so we list every `resource:`-flagged ref. On the
probe that is 3 entries vs RC's 2. When the refs resolve we match exactly
(pinned on stock `blood_impact_basic`). We over-declare only what RC would have
dropped as missing anyway.

### G4 — `.vagrp` has no ground truth

Standalone `vagrp_c` is extinct in CS2 content (0 in pak01 and every base pack,
re-checked 2026-07-20), so there is no RC output to mirror. It keeps the
sound-event compiler identity the legacy donor skeleton gave it. Do not "fix"
this without a real `.vagrp` sample.

### G5 — CLOSED 2026-08-10

Was: `.vsnd` / `.vsvg` shipping donor edit info. Fixed in C5; see above. No
donor-metadata leak remains on any compiled type, and since C6 no donor file is
required at all.

Residual, low priority: `TextureDef.SourceName` is still unset by most texture
callers (the skin and glove builders know their real source names and should
pass them), so those record the neutral `vpkeditor/texture.png` rather than a
meaningful path. Strictly better than naming a third party's file with their
CRC, but not yet the real identity. The sound and killfeed callers DO pass
theirs.

## Why byte identity is not the target

Two independent blockers, both measured rather than assumed:

1. **v5 layout** (G1). Matching byte-for-byte means implementing RC's v5
   two-buffer split exactly — including how it partitions values between the
   buffers, which is not documented anywhere and would have to be reversed from
   samples.
2. **LZ4 is encoder-defined.** Both sides compress the body with LZ4 at frame
   size 16384, but LZ4 permits many valid encodings of the same input. Matching
   Valve's bytes needs their exact encoder build and settings, not merely "an
   LZ4 encoder". K4os (ours) and Valve's will legitimately disagree.

So byte equality is unreachable in practice and would not prove anything extra:
the engine consumes the decoded tree plus the container metadata, and those are
what the gate pins.

Two smaller layout differences fall out of the same v4/v5 split and are expected:
`countBytes1` differs (RC's v5 bins values differently) and we omit `FLCI`, the
editor-only source line map, which pre-FLCI stock resources load fine without.

## Re-deriving after a CS2 update

```bash
powershell -ExecutionPolicy Bypass -File tools\rc-oracle\rc-oracle.ps1
```

It recompiles every probe in `RcReference/` with the local resourcecompiler and
prints the container facts. A drifted fingerprint means Valve bumped a compiler
version: update `Source2ContainerAuthor.SpecByExtension`, copy the freshly
compiled `*_c` over the fixtures, and re-run `Source2ContainerAuthorTests`. If
`rc_probe_ints.vdata_c` starts failing the tree comparison, the integer rule
itself moved — re-derive it before touching `NarrowIntegers`.
