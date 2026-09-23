# Handoff: the vis compiler, as it stands on 2026-09-23

For an agent picking this up cold. It says what the goal is, where the numbers
are right now so you have a reference position to move from, what the CS2 update
of 2026-09-23 did, how addresses are found now that they move, and which
questions are actually open.

Read this, then [`VIS.md`](VIS.md). Nothing here replaces that file; this is the
map to it.

---

## 1. The goal, stated exactly

Produce a self authored map compiler whose output matches what Valve's would
produce for the same input. The active front is **visibility**: a byte faithful
reimplementation of `visbuilder.dll`, read out of the binary rather than guessed
at, targeting **0.01% divergence from a Valve compile** (the operator's bar, which on
these counts means exact) on every stage the compile prints a number for.

Everything lives in this repo, on branch `research/map-authoring`, remote `box`.
The head of the work is the latest `vis:` commit on the branch.

---

## 2. Where to read

| doc | what it is |
|---|---|
| [`VIS.md`](VIS.md) | 2,628 lines. The real notes: every stage, what was read out of the binary, what was measured, what was ruled out and how. Newest work is at the BOTTOM. |
| [`REVERSING.md`](REVERSING.md) | How to re-read `visbuilder.dll` after a game update. Three commands, about fifteen minutes. Read this BEFORE touching Ghidra. |
| [`VISBUILDER_FUNCTIONS.md`](VISBUILDER_FUNCTIONS.md) | The function inventory. |
| [`VISBUILDER_ANALYSIS.md`](VISBUILDER_ANALYSIS.md) | Structure of the module. |
| [`RTE.md`](RTE.md) | The ray trace environment file the compile leaves behind, which is the geometry input. |
| [`VALVE_BINARIES.md`](VALVE_BINARIES.md) | Which binary does what. |
| [`PLAN.md`](PLAN.md) | The longer arc beyond vis. |

`visbuilder.signatures.json` is data, not prose. Section 5 below.

---

## 3. The reference position

These are the numbers to beat, and the ones to check you have not broken. Three
specimen maps: **ze_hold_em_p** (a real Zombie Escape map, long and enclosed),
**cardtest** and **probe01** (small constructed probes).

Per stage, ours against the compile's own printed count, as of 2026-09-23:

| stage | ze_hold_em_p | cardtest | probe01 |
|---|---|---|---|
| voxelize (nodes) | **exact** | **exact** | **exact** |
| enclosed regions | **exact** | **exact** | **exact** |
| clusters generated | **exact** | **exact** | **exact** |
| pre-merged | **exact** | **exact** | **exact** |
| target (pass budget base) | **exact** | **exact** | **exact** |
| first pass cost | **exact** | -- | **exact** |
| merge first / second / final | **exact** | **exact** | **exact** |
| assignment | **exact** | **exact** | **exact** |

`VisClusterSetTests` (`MERGE=1`) asserts all of it at 0.01%, on our own target.
The previous handoff had the merge at -7.36% / +3.75% / +3.15%.

It is not just the counts. On Valve's own captured inputs every intermediate is
bit-identical: 528,630 merge prices, every merge in order (79,955 on probe01),
every sampled visibility bit, every one of 512 rays from a wall-hugging cluster,
the rebuilt kd tree node for node, all 13 pre-merge rounds, and each pass's set
list in order. See section 4 for how that is measured; it is how every one of
these was fixed.

**Everything after assignment is still unbuilt** (section 8), so the shipped
`vvis_c` is not yet ours. That is the next front.

## 4. How to run and score anything

**Tests.** Everything is scored by xunit, in `tests/Source2.Compiler.Tests/`.

```bash
dotnet test tests/Source2.Compiler.Tests -c Release
```

Current state of that command: **173 passed, 4 failed**. The four are section 9;
they are not yours.

**Diagnostics are environment gated**, so they do not run in the ordinary suite
and do not need deleting when they stop being interesting. Set the variable to
anything non empty:

| var | file | what it prints |
|---|---|---|
| `GEOM` | `VisOutsideGeometry.cs` | what a march actually hits |
| `DIFF` | `VisMarchDiff.cs` | our march against the compile's, leaf by leaf |
| `WALK` | `VisMarchWalk.cs` | one march, step by step |
| `VALVE` | `VisAgainstValve.cs` | our whole stage chain against the compiled file |
| `SHAPE` | `VisShippedShape.cs` | what the shipped `vvis_c` is shaped like |
| `MARGIN` | `VisSeedMargins.cs` | seed placement and its margins |
| `COMPACT` | `VisCompactionShape.cs` | the compaction's output records |
| `HALT` | `VisMergeHalt.cs` | where each bucket's merge stops and why |
| `OPEN` | `VisBucketOpenness.cs` | per bucket visibility saturation |
| `SCALE` | `VisCostScale.cs` | the cost curve, taken apart |
| `MERGE` | `VisClusterSetTests.cs` | the five pass chain |
| `MAKO` | `VisCorpusTests.cs` | the wide corpus sweep |

**The oracle.** Valve's builder can be re-run per change rather than per compile,
which is what makes any of this scoreable:

```bash
python tools/vis/rebuild_vis.py <addon> <map> --runs N
```

Four traps, each of which cost a wrong run to find, are written up in VIS.md
"Running vis on its own, as an oracle". The one that bites first: **visibility is
built by the `-world` phase, not `-vis`**, and a skipped phase silently leaves
yesterday's output in place.

Valve against Valve is a **byte identical** `vvis_c`, so any deviation you
measure is entirely ours and none of it is oracle noise.

---

### Capture and replay, which is how parity was actually reached

Counts at the end of a stage cannot localise a defect in a greedy merge: one
wrong tie-break early shows up as a soft percent later. What works is taking the
compile's intermediate state out of the running process and feeding the port the
SAME input. Frida is installed; the tools:

| tool | what it records |
|---|---|
| `tools/vis/capture_merge.py <addon> <map> [--vis] [--gen]` | every merge loop call: input set, leaf boxes, sampled visibility, candidate prices, every merge and cost, output; and the whole set list at every pass entry |
| `tools/vis/capture_rays.py <addon> <map> x,y,z ...` | one cluster's per-ray tracer results |

and the replays that consume them, all env gated:

| var | test | what it compares |
|---|---|---|
| `REPLAY=<map>` | `VisMergeReplay` | sampler, cost, merge order per bucket; bucketing per pass; our generation + pre-merge against the pass 0 entry (`REPLAY_CHAIN=1` runs the whole chain and compares every pass entry) |
| `RAYS=<map>` | `VisRayReplay` | our segment trace against Valve's raw tracer output, ray by ray |
| `KD=<map>` | `TracerKdReplay` | our rebuilt kd tree against the one dumped from memory |

Dumps of the tracer (kd nodes, triangles) and of pre-merge rounds were taken
with one-off Frida scripts; the pattern is in `capture_merge.py`. Addresses
come from the signature manifest, so all of it survives a game update.

**Ghidra/ReVa.** The `ReVa` MCP server is a Ghidra plugin and refuses
connections unless something serves it. `tools/re/reva_serve.py` serves it
headless on the `cs2` project (it locks the project while running);
`tools/re/reva_call.py` calls any ReVa tool over HTTP if the MCP client did not
connect. Ghidra's decompile has twice been wrong in ways that mattered here: it
DROPPED the whole tail of `BoxGap` after its square root, and it reordered a load
past a store in the triangle conversion. Disassemble anything surprising.

## 5. The CS2 update of 2026-09-23, and signatures

**What happened.** CS2 rebuilt `visbuilder.dll` and every address in VIS.md moved
at once. `0x18017f128`, the coarse merge weight, went from holding `0.25` to
holding the ASCII of `idates()`. Four tests failed, and the one that failed most
usefully said "visbuilder.dll has 2.03e-110, we use 0.25", which reads as though
OUR number were wrong when what was wrong was the build it was looking at.

**The fix is not to re-read the addresses.** `docs/visbuilder.signatures.json`
holds, per symbol, the bytes AROUND it with the ones the linker moves blanked
out. 97 symbols, 64 code and 33 data, across 144 read sites. `tools/sigscan.py`
resolves them against whatever DLL you point it at:

```bash
python tools/sigscan.py "/d/Steam/.../game/bin/win64/visbuilder.dll"
```

The blanks are not guessed. Ghidra knows which BITS of an instruction encode
each operand, so `getOperandValueMask` says exactly which bytes carry a
reference; every other byte is verbatim. A constant has no bytes worth matching,
so it is signed by the instructions that READ it, up to four of them, and the
target is `match + next + int32_at(match + disp)`. A pattern matching zero times
is `gone`, more than once is `ambiguous`, sites disagreeing is `split`, and none
of those produce an address. **A wrong address is worse than a missing one.**

Where it stands right now, verified this session:

| | resolves |
|---|---|
| 2026-09-23 build (installed, md5 `13f3752…`) | **97 of 97** |
| 2026-07-09 build (`D:\cs2shadow\...`) | **94 of 97** |
| known constants read back and matched | **18 of 18** |

The manifest is signed against the NEW build and still reaches 94 of 97 symbols
BACKWARDS into the old one, which is the evidence that the method is build
agnostic rather than tuned to one image.

**What the update actually changed: nothing that matters.** Three symbols needed
recovering by hand, and two of them looked like behaviour changes and were not:

- **`Normalise` was inlined.** The old whole function was an inline fast path
  with a double precision fallback; the new build hoisted the fast path into all
  17 callers and left the fallback alone (201 bytes against 391).
- **`SampleCluster` grew 284 bytes and is otherwise identical.** An earlier note
  claimed Valve had changed the visibility sampler, which would have mattered a
  great deal for the merge. That was wrong: decompiling both builds side by side
  shows the same reach from the set box diagonal, the same `(flags & 1) == 0`
  branch and the same walk. The extra bytes are that inlined normalise.
- **`AbsMask`** had all four read sites inside changed functions. Found again by
  reading which `DAT_` the new `CheapestPair` takes as a mask and confirming the
  bytes are `7fffffff` four times over.

**Every constant the port depends on came back with the same value.** The lesson
worth carrying: **a signature breaking is evidence about BYTES, not behaviour.**

If a signature does break, [`REVERSING.md`](REVERSING.md) §4 has those three
recoveries written out as the three shapes this takes, plus a 94 row table
translating every address VIS.md used to quote into the new build.

---

## 6. The rules this work runs under

These are the operator's, and they are not negotiable.

- **Always decompile. Never guess.** Every conclusion comes from the actual
  bytes, the running system or a primary reference. Do not theorise a cause and
  act on it. If something cannot be confirmed, say so.
- **No assuming variable types, values, or anything else.** Read them.
- **Never invent a mechanism to explain a number.** A previous agent invented a
  "Touches" relation to make a count work; it did not exist. Find the truth.
- **A Ghidra `void` return type is not evidence.** `180034220`, `18002fec0` and
  `CVoxelSampler3::MergeClusterSet` all return values in XMM registers and
  Ghidra types them `void`.
- **Measure before believing a hypothesis is the lever.** Several very plausible
  ones were killed this way: the march (matches brute force), the visibility
  saturation (hold_em's fullest cell is a genuinely open box, 39,800 of 39,800
  lines clear), the cost limit (ours is lower, wrong direction), the bucketing,
  all seven cost constants, and the cluster birth values.
- **Do not quote addresses in prose any more.** Name the symbol; let the
  manifest resolve it.
- No em dashes in prose or comments. Comment blocks cap at 20 lines.

---

## 7. What is built

| piece | state |
|---|---|
| VXVS decode and encode | byte exact on 112 maps, 481 MB |
| DATA index derivation | equals Valve's numbers on 112 maps |
| point and visibility queries | agree with an independent reader on 84,673 points |
| `VisVoxelizer` | exact on hold_em, +0.28% on the probes (six branches each) |
| `VisOutside` | outside detection, the seed and the propagation |
| `VisRegions` | region generation and compaction, three unions per leaf |
| `VisClusters`, `VisClusterSet` | the birth rule and the five pass chain |
| `VisPreMerge` | `18002f5c0`, the distance pre merge |
| `VisAssign` | assignment, carrying the compaction's outside union |
| `VisMerge`, `VisMergeCost` | the greedy merge and its cost function |
| `VisBoxTree` | the dynamic AABB tree, now WITH its rotation |
| `VisSampler`, `VisClusterSample` | the visibility sampler |
| `RayTraceEnvironment` | the file's kd trace, plus `Segment`: the compile's batch tracer on the loader's converted triangles |
| `TracerKd` | the kd tree the loader rebuilds (`RefineNode`), and the voxelizer's box query through it |
| `MsvcSort` | MSVC's `std::sort`, where the compile's unstable sort order is observable |

Two fixes from this session are worth knowing about because both were invisible
to the tests that existed:

- **The kd trace was clamping each triangle to the leaf holding it.** The `.rte`
  stores 4,548 triangles in 5,983 index slots, about 1.3 leaves per triangle, so
  standard kd t-range clamping is simply invalid here. Removing it is what took
  the enclosed regions to exact.
- **The AABB tree was never balanced.** `18010a5d0` opens every refit iteration
  with `18010a6e0`, which is a rotation returning the subtree's new root. A query
  tests BOXES, so an unbalanced tree answers every query correctly and all five
  existing tree tests passed either way. What balance changes is the ORDER
  candidates come back in, and the merge takes a rival no dearer than a
  tolerance over the one it holds. It is pinned now by the one thing it is
  observable through: 4,096 boxes inserted along a line come out 12 deep.

---

## 8. What is not built

In rough order of how much is known about each:

| piece | what is known |
|---|---|
| the four PVS ray generators | found and named, not ported. VIS.md "The four ray generators". |
| `CNeighboringClustersList::Build` | the neighbour list. |
| `AdaptivelySampleBorders` | prints `Adaptive border clusters`. |
| the collapse | `Collapsing resolution`, `Reduced node count from %d to %d`, `%d unique masks`. |
| sky visibility | not started. |
| the volume gate | below 2^20 cubic units of enclosed space the whole PVS is disabled. |
| the write | our codec already does this byte exactly. |

---

## 9. Open, in the order worth taking them

1. **The stages after assignment** (section 8): the PVS ray generators, the
   neighbour list, adaptive border sampling, the second cluster merge ("Target
   608 clusters, clamped to 606" on probe01), the collapse, sky visibility.
   Capture first: the same harness will record their inputs and outputs, and
   the ray scan is threaded, so check which parts are deterministic before
   comparing (VIS.md: the file is byte stable across runs, the ray count is not).
2. **More specimens.** Three maps are exact; a map with nodraw, hint entities
   or `vis_voxel_size` override volumes (the list at sampler+0x88 in
   `Voxelize`) exercises code paths none of these do.
3. **One fidelity item recorded and not fixed.** `NormaliseSlowPath` (lengths
   under 1e-17 or over 1e17) is approximated with a double normalise in three
   places; no specimen reaches it.

**Four failing tests that are NOT this work.** `EntityLumpAuthorTests`,
`EntityLumpKnownGapsTests` (x2) and `EntityClassCoverageTests` are the asset half
of the same 2026-09-23 CS2 update: new entity classes and changed key types in
maps that shipped with it. They were failing before this session's work and were
deliberately left alone. Do not let them mask a vis regression, and do not fold
them into vis work without saying so.
