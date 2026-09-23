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
at, targeting **below 0.5% divergence** on every stage the compile prints a
number for.

Everything lives in this repo, on branch `research/map-authoring`, remote `box`.
The head of the work is `239755c`.

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

Per stage, ours against the compile's own printed count:

| stage | ze_hold_em_p | cardtest | probe01 |
|---|---|---|---|
| voxelize (nodes) | **exact** | +0.28% | +0.28% |
| enclosed regions | **0.00%** | **0.00%** | **0.00%** |
| clusters generated | **0.00%** | -0.06% | -0.06% |
| pre-merged | **0.00%** | +0.02% | +0.03% |
| assignment | **0.00%** | -0.15% | -0.11% |
| **merge final** | **-7.36%** | +3.75% | +3.15% |

Where that came from in one session, for a sense of what moving these looks
like:

| stage | before | after |
|---|---|---|
| enclosed regions | +7.87 / +0.36 / -0.05 | 0.00 / 0.00 / 0.00 |
| clusters generated | +2.81 / -1.22 / -1.26 | 0.00 / -0.06 / -0.06 |
| assignment | -61.48 / -42.21 / -42.53 | 0.00 / -0.15 / -0.11 |
| merge final | +35.66 / +4.18 / -2.10 | -7.36 / +3.75 / +3.15 |

**The merge is the only stage still outside target**, and the two errors are
different in kind: ze_hold_em_p **under** merges (239 clusters against the
compile's 258), the probes **over** merge.

Supporting numbers for ze_hold_em_p's merge, because they narrow it a long way:

| | min | max | avg |
|---|---|---|---|
| compile, first pass cost | 20.0 | 45,960.3 | 21,940.0 |
| ours | 20.0 | 45,040.0 | 21,000.4 |
| | exact | -2.0% | -4.28% |

That is a soft offset spread across the merging buckets, not a few buckets
landing the wrong side of a threshold. Everything the cost curve is built from
has been read out of the image and matches: all seven cost constants, the cost
function to its end, the candidate rule, the absorb, the tie break, and the
cluster BIRTH values. See VIS.md "Where that leaves it" and everything after it.

---

## 4. How to run and score anything

**Tests.** Everything is scored by xunit, in `tests/Source2.Compiler.Tests/`.

```bash
dotnet test tests/Source2.Compiler.Tests -c Release
```

Current state of that command: **168 passed, 4 failed**. The four are section 9;
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
| `RayTraceEnvironment` | the kd tree trace |

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

1. **ze_hold_em_p's merge, -7.36%.** 239 clusters against 258. Its 82 merging
   buckets run out of pairs and collapse to one cluster each where the compile
   stops at about 1.3, so the difference is the LAST merge in roughly 25
   buckets. The cost curve is 4.5% low at the same point, uniformly. Not a
   discrete defect anywhere read so far.
2. **The probes' +3%**, which is now an over merge where it used to be an under
   merge, so it is a cost limit problem rather than an exhaustion one.
3. **The probes' 0.28% voxelize gap**: six branches each, identically on both,
   with the rebuilt box matching the header to the unit. Open and small.
4. **One fidelity item recorded and not fixed.** The compile's normalise has a
   magnitude guard with a double precision path above and below it;
   `VisSeed.Directions` uses plain `Vector3.Normalize`. For directions built from
   a box's half extents the fast path always applies, so it is very unlikely to
   matter, but it has not been shown not to.

**Four failing tests that are NOT this work.** `EntityLumpAuthorTests`,
`EntityLumpKnownGapsTests` (x2) and `EntityClassCoverageTests` are the asset half
of the same 2026-09-23 CS2 update: new entity classes and changed key types in
maps that shipped with it. They were failing before this session's work and were
deliberately left alone. Do not let them mask a vis regression, and do not fold
them into vis work without saying so.
