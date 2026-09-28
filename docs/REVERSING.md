# Reading Valve's binaries

How facts are taken out of the toolchain DLLs, where the names and addresses
live, and what to do after a CS2 patch. The rule behind all of it is in
[GROUND_TRUTH.md](GROUND_TRUTH.md): a rule is settled by a decompile, an
in-process oracle, or a capture, never by fitting output.

## The binaries

| DLL | what it does for the map compile |
|---|---|
| resourcecompiler.dll | the map builder: mesh export, trace scene, entity lump, settle driver, light keys, world renderer, shape conversion, region SVM |
| physicsbuilder.dll | world collision assembly (`CPhysicsBuilder::Build`), material reader, material sampler |
| vphysics2.dll | Rubikon: `RnHullCreate`, `RnMeshCreate`, the whole simulation step |
| visbuilder.dll | visibility |
| smartprops.dll | the smart prop evaluator |
| tier0.dll | strings, paths, KV3 save, qsort, libm |
| vrad3.dll / vrad3.exe | lighting |
| hammer.dll | the build dialog and command line (not in Ghidra; read from the PE) |

Copies of each analysed build are kept in `D:/tools/binaries`; the Ghidra
project is `D:/tools/ghidra_projects/cs2`. Check a program's MD5 against the
installed file before trusting a decompile.

## Tools

Decompiling:
- **ReVa** (Ghidra MCP) serves the Ghidra project headless:
  `tools/re/reva_serve.py`, called over HTTP by `tools/re/reva_call.py`.
  It locks the project while it runs.
- **Decompiling one function:** `python tools/re/dec.py <dll> <addr>` fills
  in every known name. ReVa times out on huge functions; use
  `tools/re/disx.py` (capstone, imports named) instead.
- **Other helpers** in `tools/re/`:
  - `leascan.py` (references to an address);
  - `impcallers.py` (callers of an import);
  - `symsse.py` (the real association of SSE float chains);
  - `simd.py`, `read_rte.py`, `subdiv_sim.py`.
- **Ghidra headless scripts:** `tools/*.java` (`ApplyNames`,
  `MakeSignatures`, `NameByLogStrings`) and `tools/vrad3/*.java`
  (`DumpByString`, `DumpCallers`, `InventoryDll`, `NameByAsserts`, ...).
  Copy them into `D:/tools/ghidra_scripts` to run.

Names and addresses:
- `tools/re/harvest_names.py <dll> --out <json>` names a stripped DLL from
  the PE alone. Sources, strongest first: exports, assert records, bare
  `Class::Method` strings, profiler scopes, progress messages, then RTTI
  slots (`Class::vfN`).
  - `Inl_X` means the function contains X (an inlined assert).
  - `Folded_C::vfN` means identical-code folding.
- **`tools/re/names/make_manual.py`** holds every name established by
  reading, per DLL build, each with its module tag. It is the one place
  addresses are kept. Rerun it after editing, then run
  `tools/re/address_doc.py`, which regenerates
  [ADDRESSES.md](ADDRESSES.md) with the build identity and where `src/`
  cites each address.
- `tools/re/apply_names.py` writes the names into Ghidra (primary labels;
  `--fix` and `--rename` go through the function signature).
  `tools/re/audit_names.py` lists names to doubt.
- **visbuilder** also has a byte-signature manifest
  (`docs/visbuilder.signatures.json`, `tools/sigscan.py`,
  `BinarySignatures.cs`). Bytes the linker moves are blanked. A constant is
  signed by the instructions that read it.

## After a CS2 patch

1. **Check the patch.** Run `python tools/re/patch_check.py`. It reports:
   - which toolchain DLLs changed;
   - every hand-kept address, script RVA (`tools/re/tracked_rvas.json`) and
     address cited in `src/`, classified identical, relocated, moved,
     changed or missing;
   - changes in the FGDs and pak01.

   The report goes to `D:/tools/patch_reports`, and the exit code is 2 when
   something needs attention.
2. **For a new build of a DLL:**
   - Import it with full analysis:
     `analyzeHeadless ... -import <dll> -overwrite`. Every later run uses
     `-noanalysis`.
   - Rerun the harvest.
   - Move the addresses reported as moved.
   - Re-read the functions reported as changed.
3. **For visbuilder:** `python tools/sigscan.py <dll>` resolves the manifest
   first. Re-sign from the new build with `MakeSignatures.java`, and check
   it backwards against the old build.
4. **The tests read constants back from the installed DLLs**
   (`BinarySignatureTests`, `VisMergeCostTests`). A constant that resolves
   with a different value is a real behaviour change; the rest is
   mechanical.

A signature breaking is evidence about bytes, not behaviour. In the
2026-09-23 rebuild, `Normalise` was inlined into its callers and
`SampleCluster` grew by 284 bytes; neither changed behaviour.

## Getting ground truth

- **In-process oracles.**
  - `tests/Source2.Compiler.Tests/Vphysics2Oracle.cs` and `ResourceCompilerOracle` load the
    installed DLL into the test process, hash-gated to the analysed build.
  - They call functions by address on shared memory, with random inputs.
  - Mirror Valve's structs byte for byte and allocate 16-byte aligned.
- **Frida captures of resourcecompiler.**
  - CS2 must be closed, the watchdog must run, and any package the compile
    overwrites is backed up and restored.
  - The scripts: `tools/vis/capture_*.py`, `tools/physics/capture_*.py`,
    `tools/hulls/capture_weld.py`, `tools/settle/capture_settle.py` and
    `tools/entities/capture_instances.py`.
  - Frida drops messages over 128 MiB, so send big blobs in parts.
- **Never attach to cs2.exe.** Game tests run through the workshop tools
  with `-insecure` (`tools/pipeline/map_test.py`), and only after asking.

## Decompiler traps

These cost real time. Check the disassembly whenever a decompile surprises
you:
- **Dropped code:** Ghidra dropped the tail of a function after a square
  root (the vis box gap).
- **Reordering:** it reordered a load past a store.
- **Arguments:** it misassigns arguments for member calls that return a
  class by value, because the hidden return pointer shifts them.
- **Float association:** the printer drops parentheses in `*` and `+`
  chains, and hides `minss` and `maxss` NaN behaviour.
- **Precision:** a double computation can print as float.
- **Returns:** a `void` return type is not evidence; several functions
  return in XMM registers.
- **Stored values:** a stored composed quaternion can be hidden (smart prop
  Translate stores xmm1).
- **Case folding:** tier0 folds ASCII A-Z only (`ToLowerFast`, `stristr`,
  `stricmp_fast`); .NET folds more.
- **Signed zeros:** skipped or identity transforms still turn -0 into +0.
