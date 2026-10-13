# Particle migration: legacy `.vpcf` sources to the current schema

Roadmap for RC_PARITY gap **G2**. Not started; nothing here is implemented yet.

## The gap

resourcecompiler does not just recompile a `.vpcf` source, it **upgrades** it from the
format it declares to the newest one before writing the `.vpcf_c`. On `rc_probe_refs.vpcf`
(declared `vpcf26`) RC wrote the current format (`e41b57dd-…`), with operators renamed
(`C_INIT_RandomLifeTime` to `C_INIT_InitFloat`, `C_OP_DistanceToCP` to
`C_OP_DistanceToTransform`), fields restructured (`m_bAdditive` to `m_nOutputBlendMode`,
`m_hMaterial` to `m_vecTexturesInput[0]`) and literals moved into float inputs.

We compile the same source as-is, under its declared GUID. `Source2ContainerAuthorTests`
exempts `.vpcf` from the tree comparison for that reason.

## Who needs it

- **vpkstuff today: no one.** Its particle path decompiles a stock `.vpcf_c` (already the
  current format), edits it and recompiles, so the text is migrated before we see it.
- **Authoring from a legacy source** (an old CS:GO-era or workshop `.vpcf`, a hand-written
  file copied from an old example): today it ships in the old schema. Whether CS2 still loads
  a legacy-GUID particle and upgrades it at load time has not been checked in game.
- **Parity:** the `.vpcf` exemption is the last open tree difference in the RC parity gate.

## What exists to build on

- **tier0** registers all 66 particle formats (`vpcf1` … `vpcf66`) in its KV3 format table;
  `particles.dll` and `resourcecompiler.dll` each name only `vpcf66`, the current one. Our
  `Kv3/Kv3Formats.cs` already carries the 66 GUIDs.
- **ValveResourceFormat** (our pin `d9926fcb`, MIT) has `Particles/Upgrade/`: one step class
  per format pair from `GenericToVpcf1` to `Vpcf65ToVpcf66` (about 5,100 lines), the walking
  helpers (`UpgradeKV`), a step trace, and `LatestBehaviorVersion = 12`. It works on
  ValveKeyValue's `KVObject`, the tree type the compiler already uses. That makes it a
  reference to check against, not ground truth, and not a dependency we may take (the core
  takes no VRF, see INDEPENDENCE.md).

## Plan

1. **Ground truth from the DLLs.** Find where Valve registers the vpcf converters (the KV3
   format-converter table that walks `vpcfN` to `vpcfN+1`; start from the tier0 format names
   and their cross-references into `particles.dll` / `resourcecompiler.dll`). Record the
   chain, each converter's address and what it rewrites in `docs/GROUND_TRUTH.md` and
   `make_manual.py` (addresses live there, per DLL build).
2. **Corpus.** Count the stored format of every stock `.vpcf_c` in pak01 and the base packs
   (a compiled particle may keep an older format and be upgraded by the engine at load).
   Each older format found is a free test input. For the rest, compile probe sources of chosen
   old formats with RC (addon `vpkstuff_rc_probe`, ask the map agent for the slot) to get
   Valve's upgraded output.
3. **Port** into `Source2.Compiler/Particles/Upgrade/`, one step per format pair, written from
   the decompiled converters and checked against the VRF chain step by step. Where the two
   disagree the DLL wins and the difference goes into GROUND_TRUTH. Keep VRF's attribution on
   anything taken from it.
4. **Wire it in.** `Kv3SourceCompiler` upgrades a `.vpcf` whose declared format is older than
   `vpcf66` before encoding, and stamps the current GUID. Settle from RC whether
   `m_nBehaviorVersion` moves with it, and how the RERL changes when a ref moves
   (`m_hMaterial` to `m_vecTexturesInput`).
5. **Prove it.** Remove the `.vpcf` exemption from `Source2ContainerAuthorTests`; tree-equal on
   every probe and every older-format stock file; `kv3-parity` over pak01's `vpcf_c`; byte
   parity where the rest of the container already matches.
6. **Ship to vpkstuff** through the usual sync; an in-game check of a migrated legacy particle
   on a local map (GFL servers ignore content-VPK particle overrides).

## Open questions

- Does the engine still upgrade a legacy-GUID `.vpcf_c` at load? If it does, G2 is a parity
  item only; if not, a legacy source compiled by us today does not render.
- Does RC run the chain only on sources, or also when a compiled `.vpcf_c` is used as a child?
- Are there steps that need data outside the file (other particles, materials)?
