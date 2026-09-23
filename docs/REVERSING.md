# Re-reading visbuilder.dll after a game update

Everything we know about Valve's vis compiler was read out of one build of
`visbuilder.dll`. On 2026-09-23 CS2 rebuilt it and every address in `VIS.md`
moved at once: `0x18017f128`, the coarse weight, went from holding `0.25` to
holding the ASCII of `idates()`. Four tests failed and the one that failed most
usefully said "visbuilder.dll has 2.03e-110, we use 0.25", which reads as though
OUR number were wrong when what was wrong was the build it was looking at.

Doing that by hand once is research. This file is so it is never done by hand
again. **The happy path is three commands and about fifteen minutes**, most of
which is Ghidra analysing.

---

## 1. What is installed, and where

| | |
|---|---|
| Ghidra | `D:\tools\ghidra_12.1.3_PUBLIC` |
| project | `D:\tools\ghidra_projects\cs2` -- holds one program per build |
| scripts | `D:\tools\ghidra_scripts` (the versioned copies live in `tools/`) |
| live DLL | `D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\visbuilder.dll` |
| older build | `D:\cs2shadow\game\bin\win64\visbuilder.dll` -- the 2026-07-09 one |
| staging | `D:\tools\ghidra_staging` -- dated copies, so a program name says which build it is |

The project currently holds `visbuilder.dll` (2026-07-09),
`visbuilder_20260923.dll`, `resourcecompiler.dll` and `vrad3.dll`. **Keep the
old program.** Comparing a new decompile against the old one is what turns "this
moved" into "this changed", and that distinction is the whole game.

Ghidra scripts must sit in the scripts directory to run, so `tools/*.java` are
the versioned copies and get copied in. They must not drift; the update
procedure copies them every time so they cannot.

---

## 2. The update procedure

### Step 0 -- confirm it really is a new build

```bash
md5sum "/d/Steam/steamapps/common/Counter-Strike Global Offensive/game/bin/win64/visbuilder.dll"
```

against `reference.md5` in `docs/visbuilder.signatures.json`. Same hash, the
problem is not the binary.

### Step 1 -- see how much survived, before doing anything

```bash
python tools/sigscan.py "/d/Steam/.../game/bin/win64/visbuilder.dll"
```

Every symbol, its old address, its new one, and what happened. This alone may be
all you need: the C# side resolves the same way at run time, so if everything
resolves the tests already pass and there is nothing to do.

The 2026-09-23 rebuild resolved **91 of 94** straight off.

### Step 2 -- import and fully analyse the new build

```bash
cp "/d/Steam/.../visbuilder.dll" /d/tools/ghidra_staging/visbuilder_<YYYYMMDD>.dll

cd /d/tools
GHIDRA_HEADLESS_MAXMEM=12G ./ghidra_12.1.3_PUBLIC/support/analyzeHeadless.bat \
  D:/tools/ghidra_projects cs2 \
  -import D:/tools/ghidra_staging/visbuilder_<YYYYMMDD>.dll -overwrite
```

Full analysis, not `-noanalysis`: this is the run that finds the functions, the
RTTI classes and the strings. It takes a few minutes. Every LATER command uses
`-noanalysis -process visbuilder_<YYYYMMDD>.dll`, because re-analysing is slow
and destroys names.

### Step 3 -- name it

```bash
python tools/sigscan.py <new dll> --json scan.json
# turn the resolved rows into "<name> <address>" lines -> names.txt

cp tools/ApplyNames.java tools/NameByLogStrings.java tools/MakeSignatures.java \
   /d/tools/ghidra_scripts/

GHIDRA_HEADLESS_MAXMEM=12G ./ghidra_12.1.3_PUBLIC/support/analyzeHeadless.bat \
  D:/tools/ghidra_projects cs2 -process visbuilder_<YYYYMMDD>.dll -noanalysis \
  -scriptPath D:/tools/ghidra_scripts \
  -postScript ApplyNames.java names.txt \
  -postScript NameByAsserts.java asserts.txt \
  -postScript NameByLogStrings.java logstrings.txt
```

On 2026-09-23 that gave 57 functions and 32 data labels from the manifest, 3
more from asserts and 259 from log strings -- **634 of 3,869 functions carrying
a real name** before any hand work.

### Step 4 -- recover whatever did not resolve

Section 4. Usually a handful, and each is a ten minute job.

### Step 5 -- re-sign from the new build and verify

```bash
ARGS="Name1 addr1 Name2 addr2 ..."        # every symbol, new addresses
... -postScript MakeSignatures.java sig_new.json $ARGS

# merge into docs/visbuilder.signatures.json, then:
python tools/sigscan.py <new dll>          # must be N of N, all "unmoved"
python tools/sigscan.py <old dll>          # how far back it still reaches
dotnet test tests/Source2.Compiler.Tests -c Release --filter FullyQualifiedName~BinarySignature
```

Signing against the NEW build and checking it backwards against the OLD one is
the real test of the method. The 2026-09-23 manifest resolves 97 of 97 forwards
and **94 of 97 backwards**, the three misses being exactly the three that
genuinely changed.

---

## 3. How a signature works

`docs/visbuilder.signatures.json` holds, per symbol, the BYTES around it with the
ones the linker moves blanked out.

**The blanks are not guessed.** Ghidra knows which BITS of an instruction encode
each operand, so for any operand carrying a reference -- a call's `rel32`, a RIP
relative displacement -- `getOperandValueMask` says exactly which bytes to
wildcard, and every other byte is kept verbatim. A pattern grows one instruction
at a time until it matches exactly once in the executable section.

A constant is not code and has no bytes worth matching, so it is signed by the
instructions that READ it:

```json
{ "name": "CoarseWeight", "kind": "data", "was": "1801823d0", "sites": [
  { "pattern": "F2 0F 10 0D ?? ?? ?? ?? 48 8B 9C ?? ?? ?? ?? ??", "disp": 4, "next": 8 } ] }
```

`F2 0F 10 0D` is `movsd xmm1, [rip+disp32]`; the four blanks are the
displacement, and the answer is `match + next + int32_at(match + disp)`. Up to
four sites per constant, for two reasons: an update that rewrites one function
takes its pattern with it and the next site is usually untouched, and two sites
agreeing on an address is the strongest confirmation available.

**Nothing is guessed at the resolving end either.** A pattern that matches zero
times is reported `gone`, more than once is `ambiguous`, and sites that disagree
are `split`. None of those produce an address. A wrong address is worse than a
missing one.

---

## 4. When a signature breaks

A signature breaks because the function containing it changed. That is
information, not a failure -- and the way to recover the symbol is to walk to it
from a neighbour that DID resolve. All three misses of 2026-09-23 went this way,
and they are worth reading because they are the three shapes this takes.

### It was inlined -- `Normalise`

`GatherRays` resolved, so decompile it and look at what it calls. Four of its
callees were already named by the manifest; of the rest, the one in the right
place read:

```c
fVar22 = fVar23 * fVar23 + fVar19 * fVar19 + fVar17 * fVar17;
if (fVar22 < 0.0) fVar22 = FUN_18013efb0(fVar22); else fVar22 = SQRT(fVar22);
if ((fVar22 < DAT_18018236c) || (DAT_18018247c < fVar22)) { ... FUN_18010ac60(&local_88b0); }
```

The old `180109570` was a whole normalise: an inline fast path guarded by
`DAT_1801646f0 <= len <= _DAT_180164700`, with a double precision fallback.
The new build hoisted the fast path into every caller and left only the
fallback. So the symbol did not move, it **split**: `FUN_18010ac60` is the
slow path, 201 bytes against the old 391, with the same 17 callers.

Recorded as `NormaliseSlowPath`, and the old `Normalise` retired. The lesson:
check the byte count and the caller count before believing a function "moved".

### It grew but did not change -- `SampleCluster`

Find it by who calls something that DID resolve:

```bash
awk -F'\t' '$1=="CALL" && $3=="<new SamplerWalk address>"' inv_new.txt
```

One caller, `FUN_180032fa0`, 1,337 bytes against the old 1,053. Growth looks
alarming. It was not: decompiling both shows identical logic -- same reach from
the set box diagonal, same `(flags & 1) == 0` choosing reach over the hit
distance, same `SamplerWalk` call -- and the extra 284 bytes are the normalise
fast path that got inlined into its direction loop.

**This is why you decompile before you conclude.** An earlier note in this repo
said Valve had changed the visibility sampler, which would have mattered a great
deal for the merge. It is wrong, and it was wrong because the signature breaking
was treated as evidence about the function instead of evidence about its bytes.

### It is a constant with no distinctive code -- `AbsMask`

All four read sites were inside functions that changed. Go to a named function
that uses it -- `CheapestPair` -- and read which `DAT_` it takes as a mask:

```c
uVar5 = _DAT_180182500;   // ff ff ff 7f  ff ff ff 7f  ff ff ff 7f  ff ff ff 7f
```

Confirm the bytes are the value you expect before naming it.

### The general method

1. `python tools/sigscan.py <dll>` -- what is missing.
2. Find a resolved NEIGHBOUR: a caller, a callee, or a function that reads the
   same constant. `InventoryDll.java` output is a tab separated call graph, so
   `awk` answers "who calls X" directly.
3. Decompile the neighbour in the NEW build and the OLD one, side by side.
4. Match by position, by what it calls, and by the shape of its arguments --
   never by size alone, and never by address.
5. Confirm a constant by reading its bytes; confirm a function by decompiling
   it and comparing to the old.
6. Apply the name, re-sign, re-verify.

---

## 5. Where names come from, in order of authority

1. **Asserts.** `"CVoxelSampler3::MergeClusterSet(), C:/.../vis3.cpp:3206"` names
   the containing function outright. `NameByAsserts.java`. There are about six.
2. **RTTI.** Full analysis applies these itself: type descriptors give class
   names and vftables. 205 descriptors, 80 of them Valve's own.
3. **Our manifest.** `ApplyNames.java` -- the names this project has established
   by reading the code, which is what `VIS.md` uses throughout.
4. **Log strings.** A format string referenced by exactly ONE function is
   evidence about what that function does. `NameByLogStrings.java` applies them
   with a `Logs_` prefix so nobody mistakes one for a symbol Valve shipped. It
   never overwrites a better name. 259 on the 2026-09-23 build.
5. **The call graph.** A function that logs "Voxelize" IS the voxelizer, and its
   callees are that stage whether or not any of them says anything about itself.
   This is manual, and it is what most of `VISBUILDER_FUNCTIONS.md` came from.

---

## 6. The scripts

| script | what it does |
|---|---|
| `tools/MakeSignatures.java` | addresses in, byte signatures out |
| `tools/ApplyNames.java` | `<name> <address>` lines in, a named database out |
| `tools/NameByLogStrings.java` | names the functions that log something |
| `tools/sigscan.py` | resolves the manifest against any DLL, non-zero on loss |
| `BinarySignatures.cs` | the same resolver in C#, used by the tests |
| `NameByAsserts.java` | names functions from MSVC assert strings (scripts dir) |
| `InventoryDll.java` | every function, call edge and string use, tab separated |
| `DumpAt.java` | decompile a list of addresses -- the workhorse |
| `DumpCallers.java` | decompile everything that calls an address |
| `DumpAsm.java` | disassembly, for when the decompiler hides an operand |

`DumpAt` is the one to reach for. Ghidra leaves a vtable slot that nothing calls
undefined, so a plain "decompile this address" is the only way to reach virtual
methods, and it creates the function if it has to.

---

## 7. What the tests guarantee

- `BinarySignatureTests.MostOfTheManifestStillFindsItsSymbol` fails if under 80%
  of the manifest still resolves against the installed build, and names what to
  re-sign.
- `BinarySignatureTests.TheConstantsItFindsHoldWhatTheyShould` reads 18 known
  constants back out of the installed DLL and compares them to the values the
  port uses.
- `VisMergeCostTests.EveryConstantIsTheOneValveCompiledIn` does the same for the
  merge cost's own constants, by NAME rather than address.

None of them quotes an address. That is the point.

---

## 8. What "something seriously big" looks like

The procedure above assumes a rebuild: same code, moved. The signs that it is
more than that, and what each means:

| sign | what it means |
|---|---|
| sigscan resolves under about half | a compiler or optimiser change; re-sign wholesale from the new build, the method still works |
| a constant resolves but holds a different VALUE | Valve changed a threshold. This is the one that matters. The tests fail loudly and the port needs the new number |
| a function resolves but its decompile differs | read both, side by side, and treat it as a behaviour change until shown otherwise |
| a log string disappears | a stage was removed or renamed; check `VIS.md`'s stage list against the new strings |
| the function count moves by more than a few percent | 3,848 to 3,869 across this update was normal; a big jump means new code, so re-inventory |

In all but the second row the work is mechanical. The second row is the only one
that changes what the compiler should DO, and it is exactly the one the tests are
built to catch.

---

## 9. Address translation, 2026-07-09 to 2026-09-23

`VIS.md`, `VISBUILDER_FUNCTIONS.md` and `VISBUILDER_ANALYSIS.md` quote the
2026-07-09 addresses throughout, because that is the build they were written
against and re-writing them would lose the link to the decompiles they came
from. This table translates them. It was generated by resolving the 2026-09-23
manifest against the 2026-07-09 build, so it is derived, not typed.

| symbol | kind | 2026-07-09 | 2026-09-23 |
|---|---|---|---|
| `AbsorbPair` | code | `180030a50` | `180031fd0` |
| `AreaLimitAndPassCell4096` | data | `18017f1cc` | `180182474` |
| `AssignClusters` | code | `18002ed60` | `1800302e0` |
| `AssignClusters2` | code | `180037840` | `180038ed0` |
| `BatchRay` | code | `18010e8a0` | `180110430` |
| `BatchTracer` | code | `180016010` | `180016d40` |
| `BestPartner` | code | `1800284a0` | `180029910` |
| `Better` | data | `18017f0cc` | `180182374` |
| `BoxGap` | code | `18002fec0` | `180031440` |
| `BoxOrder` | code | `180027e10` | `180029280` |
| `BoxOverlap` | code | `18004bf20` | `18004d750` |
| `BoxTraversal` | code | `18004c400` | `18004dc30` |
| `BudgetMult1` | data | `18017f168` | `180182410` |
| `BudgetMult2` | data | `18017f164` | `18018240c` |
| `BudgetMult3` | data | `18017f15c` | `180182404` |
| `BudgetMult4` | data | `18017f158` | `180182400` |
| `BudgetMult5` | data | `18017f14c` | `1801823f4` |
| `BuildCandidates` | code | `180030df0` | `180032370` |
| `CandidateAlive` | code | `180030190` | `180031710` |
| `CandidateBoxes` | code | `18002beb0` | `18002d320` |
| `CastRayGrid` | code | `18004a690` | `18004bd50` |
| `CellMask` | code | `18010c6e0` | `18010e0d0` |
| `CheapestPair` | code | `180031680` | `180032c00` |
| `ClassifyRegion` | code | `18002e050` | `18002f5d0` |
| `CoarseWeight` | data | `18017f128` | `1801823d0` |
| `Compaction` | code | `180032670` | `180033d10` |
| `CostScale` | data | `18017f174` | `18018241c` |
| `DistancePreMerge` | code | `18002f5c0` | `180030b40` |
| `DistanceToBox` | code | `180108d70` | `18010a460` |
| `EscapeShare` | data | `18017f138` | `1801823e0` |
| `FaceTolerance` | data | `18017f0dc` | `180182384` |
| `FineBoxSizeAndPass2Margin` | data | `18017f1a8` | `180182450` |
| `FirstCostLimit` | data | `18017f18c` | `180182434` |
| `FloatMax` | data | `18017f1d8` | `180182484` |
| `FlushBatch` | code | `18010ebb0` | `180110740` |
| `FoldVoxelPairs` | code | `18002f250` | `1800307d0` |
| `GainFloor` | data | `18017f1dc` | `180182488` |
| `GatherRays` | code | `18004b260` | `18004c9d0` |
| `GenerateRegionClusters` | code | `180032d80` | `180034420` |
| `GoldenSpiral` | code | `180027400` | `1800287c0` |
| `GroupByDistance` | code | `180028c70` | `18002a0e0` |
| `Half` | data | `18017f108` | `1801823b0` |
| `LeafEntries` | code | `18002d670` | `18002ebf0` |
| `LoopSeedCostAndSlack` | data | `18017f1e8` | `180182494` |
| `MarchRay` | code | `18002deb0` | `18002f430` |
| `MarchShortest` | data | `18017f0e4` | `18018238c` |
| `MemSet` | code | `180151c30` | `180154b60` |
| `MergeBestCandidates` | code | `180027f50` | `1800293c0` |
| `MergeClusterSet` | code | `180033fd0` | `180035670` |
| `MergeCost` | code | `1800301c0` | `180031740` |
| `MergeInsideRegions` | code | `180034ea0` | `180036530` |
| `MergeLoop` | code | `1800337a0` | `180034e40` |
| `NoDrawSecondLook` | code | `18004b970` | `18004d1a0` |
| `OctantMask` | code | `18010cb10` | `18010e500` |
| `One` | data | `18017f118` | `1801823c0` |
| `OutsideDetection` | code | `1800321f0` | `180033890` |
| `PassCell2048` | data | `18017f1c4` | `18018246c` |
| `PassCell512` | data | `18017f1b8` | `180182460` |
| `PreMergeMaxDimensionAndPass4Margin` | data | `18017f1c0` | `180182468` |
| `PreMergeMaxRatio` | data | `18017f154` | `1801823fc` |
| `PreMergeVolumeFloor` | data | `18017f178` | `180182420` |
| `PreferredCandidate` | code | `180030d70` | `1800322f0` |
| `PrintedRegionCount` | code | `180032b80` | `180034220` |
| `RebuildCandidates` | code | `1800306e0` | `180031c60` |
| `RegionBox` | code | `18010be50` | `18010d840` |
| `Regrid` | code | `180034220` | `1800358c0` |
| `SamplerDriver` | code | `180018f10` | `180019c40` |
| `SamplerWalk` | code | `18003d600` | `18003ed60` |
| `SeedDecide` | code | `18004a2f0` | `18004b9b0` |
| `ShellPadding` | data | `18017f148` | `1801823f0` |
| `SizeMismatchPenalty` | data | `18017f1a4` | `18018244c` |
| `SortPairs` | code | `1800294f0` | `18002a960` |
| `SplitOpenSpace` | code | `18010c3f0` | `18010dde0` |
| `SpreadPenaltyAndMarchBackOff` | data | `18017f170` | `180182418` |
| `SqrtNegative` | code | `18013c080` | `18013efb0` |
| `SubBox` | code | `18010cc20` | `18010e610` |
| `SubCellAndTouchTolerance` | data | `18017f0f0` | `180182398` |
| `TagAndZSpanPenalty` | data | `18017f190` | `180182438` |
| `TallyRays` | code | `18004a420` | `18004bae0` |
| `TargetVolumeScale` | data | `18017f0f8` | `1801823a0` |
| `Tie` | data | `18017f0d0` | `180182378` |
| `TreeBalance` | code | `18010a6e0` | `18010c0d0` |
| `TreeCreate` | code | `18010ad30` | `18010c720` |
| `TreeDestroy` | code | `18010ae00` | `18010c7f0` |
| `TreeInit` | code | `18010a520` | `18010bf10` |
| `TreeMove` | code | `18010b510` | `18010cf00` |
| `TreeQuery` | code | `18010bbb0` | `18010d5a0` |
| `TreeRefit` | code | `18010a5d0` | `18010bfc0` |
| `VectorAndNot` | code | `180014ea0` | `180015930` |
| `VectorGrow` | code | `18004c670` | `18004dea0` |
| `VectorOr` | code | `180020580` | `180021940` |
| `VoxelStageDriver` | code | `180031f00` | `1800335a0` |
| `Voxelize` | code | `18002e310` | `18002f890` |
| `ZLimit` | data | `18017f19c` | `180182444` |

The three not in it -- `NormaliseSlowPath`, `SampleCluster`, `AbsMask` -- are the
ones whose shape changed, and section 4 is how each was recovered.

