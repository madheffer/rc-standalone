# Reverse-engineering notes

The working notes behind the port: what was read in Valve's DLLs, with the
addresses, offsets and vtable slots it was read at. Moved here from private
session memory on 2026-09-28 so they can be kept, shared and checked.
[`ADDRESSES.md`](ADDRESSES.md) is the verified index of function addresses
per DLL build (generated from `tools/re/names/make_manual.py` and re-checked
by `tools/re/patch_check.py`); these notes carry the context around them.
Unless a note says otherwise, resourcecompiler addresses are the 2026-09-23
build, vphysics2 and physicsbuilder the 2026-09-24 build.

Sections: [blend-capture-pending](#blend-capture-pending), [compile-map](#compile-map), [cs2-voice-research](#cs2-voice-research), [entity-lump-port](#entity-lump-port), [full-coverage-scope](#full-coverage-scope), [ground-truth-ledger](#ground-truth-ledger), [hull-cook-chain](#hull-cook-chain), [light-precompute](#light-precompute), [settle-port](#settle-port), [smart-props](#smart-props), [static-props](#static-props), [stitched-subdivision](#stitched-subdivision), [vis-own-geometry](#vis-own-geometry), [world-physics-container](#world-physics-container).

<a id="blend-capture-pending"></a>
## blend-capture-pending

*Blend split and material sampler: captures, what is exact, GPU sampler status (step 3 done 2026-09-27), -vulkan capture bit-exact*

The capture ran on 2026-09-25 18:20–18:41, after cs2 and another session's ze_doom_p2 compile had closed. Data is in `C:/Users/User/AppData/Local/Temp/worldcol4`:
- `ze_hold_em_paint.json` and `ze_hold_em_paint_flat.json`: copies of ze_hold_em_p in s2c_lighting with node 409's paint rewritten; the flat copy also has its subdivision zeroed;
- `atixref.json`.

Measured and ported:
- A plain piece's vertex paint = the first corner met in its own face set (bias, material), walking faces in order and each loop from its first half-edge: 72/72, 4/4.
- Subdivided faces take the paint stored per grid point in subdivisionData.
- The split reproduces bit for bit (BlendSplitReplay).

Still open: the material sampler (18064c460) runs in every compile, and it decides layers for two-surface csgo_environment_blend pieces. On atixref it chose plaster where the 0.5 rule picks concrete. It is a GPU MaterialSampler render, so porting it is a big job (tracker n3c). The capture script now also records its per-triangle layers.

**How to apply:** before claiming blend layers are exact on a map, check whether it has two-surface csgo_environment_blend world pieces. WorldCollision notes them.

Related: [shader-attributes](#shader-attributes), [progress-tracker](#progress-tracker).

Sampler, as read on 2026-09-27 (docs/WORLD_PHYSICS.md has the details):
- It is CMaterialSampler in physicsbuilder, entry 18064c460.
- It renders the material's ToolsVis programs (hash 0xd102bcad = "ToolsVis") with the render attribute MaterialSamplerMode (0xa94de356) set to 0.
- The vertex layout "MaterialSampler" (0x180ba3440) takes the paint as RGBA8.
- The output is RGB8, and the weights are (c - min) / 255.

The user chose to run it on the GPU (Vulkan build for RunPod) rather than port the shader to the CPU.

Step 2 (2026-09-27):
- Sampler mode = g_nToolsVisMode 80.
- Without F_USE_NEW_BLENDING the weights are the constant (1,0,0,0), so every triangle gets layer 0. This is ported; atixref is 5/5.
- Only new-blending materials (51 in CS2) need the GPU runner.
- tests/ShaderProgramDump.cs dumps and decompiles the programs; "*=1" in the settings dumps every matching combo.

Step 3 (2026-09-27, commit 849527d): GpuMaterialSampler in Source2.Compiler.Gpu runs the ToolsVis programs on Vulkan. Non-obvious facts:
- Textures load at the preload cap: the first mip with max(w,h) <= 512 (RenderSystem/MaxPreloadTextureResolution). Using mip 0 missed by up to 21 steps.
- The records carry uv0 in both texcoord slots, though the code asks for texcoord 1.
- resourcecompiler uses rendersystemdx11 by default (-vulkan switches), so a default compile's pixels come from DX11 programs: 23 of 3,816 points were 1 step off, all near a rounding boundary. Valve's output depends on the API and GPU.
- ze_hold_em_nb captures: $TEMP/worldcol5 (default, DX11) and $TEMP/worldcol6 (-vulkan, 2026-09-27 16:44). Our GPU sampler is bit for bit against the -vulkan one: 3,816/3,816 points (GPUREPLAY=<json>|materials/de_cache/ground/ch2_blend_grass_backdrop_001.vmat|exact). DX11 vs Vulkan differ in exactly those 23 bytes.

<a id="compile-map"></a>
## compile-map

*s2c compile-map and the Hammer/resourcecompiler switch rules it follows; addresses for the command line, builder flags and package handling*

`s2c compile-map` (src/Source2.Compiler/Maps/MapCompile.cs + MapCompileArgs.cs, 2026-09-28) takes Hammer's resourcecompiler line and runs the ported steps (entities-only lumps, physics world_physics) on the package at <outroot|game>/csgo_addons/<addon>/maps/<map>.vpk, like -fshallow. Refuses full world, nav, Steam Audio, -f/-fshallow2, and lumps with light/probe gaps unless --accept-gaps.

Addresses (hammer.dll VA base 0x180000000, read from the PE with capstone; not in Ghidra):
- Command line assembler 0x1800dc470 (called from 0x1800dc340).
- ApplyPreset 0x1800d54d0.
- Widgets: World 0x1800d6760, Lighting 0x1800d7240, Phys 0x1800d7ad0, Vis 0x1800d7b50, Nav 0x1800d7e80, Steam Audio 0x1800e2990.
- Previous-build copy-in 0x1800de950, copy-out 0x1800df660.
- Hammer checks its own switches as MurmurHash2 of the lowercase name, seed 0x31415926.

Addresses (resourcecompiler 0923):
- Parser 0x18018b630: its own switch list, else generic, typed %f -> %i -> string.
- Context tables 0x18016b550. GetBool 0x1801742f0 (GetInt != 0); GetInt 0x180174310 (falls back to (int)GetFloat).
- Builder flags 0x1801f7840: name table 0x183074030; gated by IsBuilderEnabled 0x1801f9a50.
- Step table 0x182771980. GenerateResourceFile 0x180188960, UnzipVpk 0x180160ca0, CleanupIntermediateCruft 0x1801fcc50.

Facts:
- csgo_core gameinfo DefaultMapBuilders plus code defaults: world, phys, vis, bakedlighting, nav, sareverb, sapaths, sacustomdata. No lod or gridnav.
- Hammer's `-bakelighting` is not the table's `bakedlighting`. `-nolightmaps` and `-vpkincr` are no-ops.

Open: what an entities-only build clears (entity .vmdl_c, flammables); `-entities` with no package. See [full-coverage-scope](#full-coverage-scope), [world-physics-container](#world-physics-container).

<a id="cs2-voice-research"></a>
## cs2-voice-research

*CS2 robotic-voice bug research lives outside this repo at D:/_cursor projects/cs2-voice-research; Ghidra cs2 project has engine2/soundsystem/client imported with renamed voice functions*

Started 2026-09-23 for build 1.41.8.2 (SourceRevision 11026673). The report is README.md in
`D:/_cursor projects/cs2-voice-research`. Also there: VALVE_BUG_REPORT.md, ONLINE_RESEARCH.md,
evidence/*.c, full decomp dumps and convar TSVs, plus tools (vpk.py, Ghidra dump scripts, a
VRF CLI built from third_party/ValveResourceFormat).

The main suspect is the global FIFO in soundsystem that binds voice decoders to talkers
(Voip_Fifo_Push / VoipSource_CreateDecoder_FifoPop). The leak is proven from the binaries:
- if the first vmix_start_vsnd fails (pool full, or a purged vsnd), vmix_voice_start skips its only
  dependency add;
- the executor had zeroed the event's dependencies, and the reaper FUN_1801d0d90 removes the event
  the same update;
- so the entry is never popped, one leaks per packet batch, and nothing resets the FIFO.
Still unobserved: whether the pool really fills in the ZE sessions. The user does not want Frida
or the game launched.

**Why:** the user wants voice fixed without restarting the game, and wants a submission to Valve.
**How to apply:** rebuild addresses after each CS2 update. Anchor on the strings "Created a VOIP
stream without an associated entity.", "core.voip" and "vmix_start_vsnd: %s No available voices".
Convars registered with flags 0 are developmentonly in retail. See [reva-headless](#reva-headless) and
[vis-capture-harness](#vis-capture-harness).

<a id="entity-lump-port"></a>
## entity-lump-port

*Entity lump (.vents_c) port state as of 2026-09-25, where the rules live in resourcecompiler, and the open threads (light precompute, layer-copy angles; the settle is closed)*

Entity lump work landed on research/map-authoring as 3f95f70 (and plan note d1f35a4), 2026-09-25.
Every lump of atixref, ze_hold_em_p, Mako, cardtest, probe01, ze_doom_p2_c, c2m2 gameplay is pinned by
EntityLumpAgainstValveTests (strict: types to the element, KV3 flags, key/entity order, all connection fields).

Key resourcecompiler_20260923 functions (addresses live here, not in prose docs):
- key typing switch FUN_180fa84e0; FGD type table at 0x182fe9f60 (type, pad, name ptr); aliases FUN_18009fe10
- FGD AddVariable FUN_180dd08c0, class finalize FUN_180dd1110, override merge FUN_180dd1680
- entity export FUN_181004020, world export FUN_180fc1560, lump-time export FUN_180240a60 (consumed classes, info_world_layer, lights)
- template pass FUN_18024d710 / d9c0 / ccd0 / c6d0 / c910 (rename) / FUN_1801fdae0 (rewrite refs) / FUN_18024a970 (removal)
- connection schema FUN_1803670b0; path export FUN_1810b5480, tangents FUN_1812806d0
- vmap<38 upgrade adding prefab_has_runtime_entity_by_default: FUN_180d7c3f0 (upgrade table ~0x182fe7b08)

Instance ids SOLVED 2026-09-25 (c19723c) by Frida capture (tools/entities/capture_instances.py, find_new_nodes.py):
smart prop locators take ids first; see SmartProps. Node id at CMapNode+0x300; CMapInstance collapse = vtable
0x182349b48 slot 0x860 -> FUN_181017820.

Open threads:
- A few Mako instance copies inside world layers ship angles 1-2 round trips off (reparent under layer, unread).
- Physics settle CLOSED 2026-09-25: SettleWorld.Run -> EntityLumpSet.Author(settled:) matches every settled
  prop's origin/angles/spawnflags on atixref, c2m2 prefab, Mako (SettleLumpTests; gap removed from
  EntityLumpAgainstValveTests). Candidates = PhysDoc_GetNodes (nodes with bodies); Start asleep via
  CMapEntity_SetStartAsleep for every candidate whose objects are all asleep (static counts as asleep).
- Light precomputed* keys: FUN_180f19f20 casts 24,576 samples per volume (Halton 2,3,5,7) against the world (FUN_180f19840, trace FUN_180f18820),
  writer FUN_180f1def0 / FUN_180f1e0e0. Needs the ray trace scene; see [vis-capture-harness](#vis-capture-harness).
- cable_dynamic rendercolor ships as text (embedded property binding registered in FUN_181089940).

**Why:** these took many hours of decompiling to locate; the next session should start from them.
**How to apply:** verify addresses against the installed build (resourcecompiler.dll changed 2026-09-24) before
trusting them; the diff/dump harness used was a scratch C# console (typed-tree dump, strict per-lump diff).

**2026-09-28:**
- The probe map (tools/coverage/probe_map.py, s2probe/probe_classes) measures all 354 offered classes: 343 exact.
- Rules ported from the DLL:
  - flags default: OR of on-bits (FUN_180dd70d0);
  - tag_list default: joined with "," (FUN_180dd6db0);
  - AddVariable merges choice lists on redeclaration (FUN_180dd08c0 -> FUN_180dd37a0);
  - spawnflags masked at SetClass (FUN_180f2e4d0);
  - fixup types by mask 0x830006 (CMapGameDataNode::vf157), skipping "!*?@" and FGD class names;
  - a key named "name" gets no default;
  - snapshot_file from snapshot_mesh (FUN_180f8ee20).
- cable_dynamic rendercolor = tint "%i %i %i" (CMapCable::vf217 0x18114cb20; tint at +0x1878, set by vf196), measured on probe_cable.
- Open: the settle for sphere and capsule shapes; the light export.

<a id="full-coverage-scope"></a>
## full-coverage-scope

*Scope rule: every entity (point_servercommand etc.) and every compile option Valve/Hammer offers must be supported, not only what test maps use*

2026-09-28 the user said: things like point_servercommand need to be included too, along with any option Valve/Hammer offers the user in terms of compilation.

**Why:** the port replaces Valve's compile for real maps; any entity class or build option a mapper can pick is in scope, even if no local test map uses it yet.
**How to apply:** when a module is called done, check it against the full list: every FGD entity class (including server-command, logic and point entities like point_servercommand), and every option in Hammer's Build/Compile dialog and resourcecompiler's command line (e.g. -world, -fshallow, vis/lighting quality presets, -nolightmaps style switches, map compile flags). Enumerate the options from the binaries/FGD, not from memory, and track gaps in docs/GROUND_TRUTH.md and the tracker. See [ze-focus](#ze-focus), [ground-truth-ledger](#ground-truth-ledger), [progress-tracker](#progress-tracker).

**State 2026-09-28:** docs/COVERAGE.md holds both lists; tools/coverage/ regenerates the class half (fgd_classes.py, class_refs.py, CoverageClassesProbe, coverage_doc.py + class_notes.json). 104 exact, 10 partial, 316 no sample. Addresses (rc 0923): per-entity lump export FUN_180240a60 (decompile times out; read disassembly), driver FUN_180244d30; static-model record FUN_180245770; vmap upgrade table 0x182fe7b08 (slot 36 = vmap<38 worldspawn step, the only one ported); nav gathers 0x180f20f00 / 0x180f21760 / 0x180f1ef70; Steam Audio probes 0x180fbd800; shatterglass flag in CModelDocCompileInstance::CompilePhysics 0x18032e3e0; Hammer build dialog strings near hammer.dll 0x1ddceb0..0x1dde9c0. Agent scratch: xrefs.txt, f240.txt in session scratchpad.

**Probe map 2026-09-28:** tools/coverage/probe_map.py (KV2 parse/emit + dmxconvert round trip, byte-identical) writes content/csgo_addons/s2probe/maps/probe_classes.vmap from cardtest; pinned in EntityLumpAgainstValveTests + EntityClassCoverageTests. 343/354 exact. Rules found (rc 0923): flags default OR of on-bits FUN_180dd70d0; tag_list join FUN_180dd6db0 ","; AddVariable merge of choice types FUN_180dd08c0 -> FUN_180dd37a0 (own first, inherited appended); spawnflags mask at SetClass FUN_180f2e4d0; fixup type mask 0x830006 in CMapGameDataNode::vf157 FUN_180f24170, class-name test FUN_180dcab50 (case blind); snapshot path FUN_180f8ee20, generator CMapNode::vf205 FUN_180f7aa30. Only real map nodes (world tree) count for node ids: CVisibilityMgr carries nodeID 0. Only 354 classes are offered (csgo.fgd include chain minus @exclude); ai_*/npc_* FGDs are not included.

<a id="ground-truth-ledger"></a>
## ground-truth-ledger

*User rule \"authoritative is law\": every inferred/assumed/measured-only rule must be re-derived from the DLLs; ledger in docs/GROUND_TRUTH.md; addresses of what was settled*

2026-09-28 the user said: anything inferred, guessed or assumed must be revisited and settled from the DLLs; "authoritative is law. law can't be broken." A rule fitted to Valve's output is only a lead.

**Why:** fitted rules broke before (surface listing, attribute union, Mako glass) and only held on the maps we had.
**How to apply:** keep docs/GROUND_TRUTH.md current (open list + resolved with source); prefer an in-process oracle test (ResourceCompilerOracle / Vphysics2Oracle call the installed DLL by address; check function bytes match the Ghidra build first) or a Frida capture of resourcecompiler over fitting. Never attach to cs2.exe; the user offered "safe" game debugging, but ask first and -insecure only (see [ingame-testing](#ingame-testing)). Never phrase an unread rule as fact in docs or comments.

Settled 2026-09-28 (addresses):
- Tool hash: rc 180c25900 hashes shape +0xf8 (set by 180c26aa0 from modeldoc node +0x108, getter 1814f4fd0) via 181c1ea80 -> FixupResourceName 181c1e120 ('vmat') then 1800c7c10 (ToLowerFast + MurmurHash2, seed 0x31415926). Port Io/ResourceNames.cs + Io/Tier0Paths.cs; oracle test ResourceNamesOracleTests (4,048 names).
- tier0 in Ghidra: the old /tier0_20260923.dll.0 is a broken DOS import; use /tier0_20260923.dll.1. Exports: FixupPathName 180054c60, SetExtension 1800560c0, ToLowerFast 1800544c0, FixSlashes 180054b40, V_IsAbsolutePath 180014830 (prefixes "vpk:"/"ugc:" at 180306700/708), V_GetFileExtension 180013780, V_RemoveDotSlashes 1800137f0.
- physicsbuilder: node tool material = mesh +0x68 name via 18001b0a0 -> 1801749f0 (+0x108); blend split 180015930 builds positions-only meshes (FUN_1800c1050 init, weld 1800c1ad0 at 1/32), no material -> tool hash 0.
- Soup: rc 180c27e30 skips members with 0 tris; 180c28690 tool hash = the one hash with tris.

- KV3 compression: tier0 SaveKV3 180124b00 -> binary save 1800c7220; binary_auto GUID picks LZ4 (1800c8790) if buffer maxput (incl 0xffeedd00 trailer) + blob maxput < 0x80001 else Zstd (1800c8d80); both store raw if put sum < 0x100. RC block writers pass "binary_auto" (RED2 writer 181c24a70; encodings named by KV3ID_t name ptr). Some sites pass "binary"/"binary_bc" (1800fd135, 180243e63, 18024a498, 181eccc11) - unmapped. Ported in Containers/AuthoredKv3.ChooseCompression.
- RE tools in repo tools/re: impcallers.py (callers of an import), leascan.py, disx.py (disasm any win64 DLL naming imports). ReVa decompile times out (10 s) on huge functions: use disx.py.
- Material reader physicsbuilder 1800132f0: V_SplitString(",", false) (tier0 18000e1e0 -> 18000df20); water rule V_stristr_fast then CUtlString::Remove(", window", caseSensitive=0) at call 180013bc1 (r9d=0). Port Io/Tier0Strings.cs, oracle Tier0StringsMatchTheExports.
- LESSON: Ghidra misassigns args for member calls returning a class by value (hidden return ptr in rdx shifts args): it showed Remove(...,true) where r9d=0. Check such calls in disassembly (tools/re/disx.py).
- Valve folds case ASCII-only (A-Z) in tier0 (ToLowerFast, stristr, stricmp_fast); .NET OrdinalIgnoreCase / ToLowerInvariant fold more. Audit before claiming exact.

Related: [world-physics-container](#world-physics-container), [vis-capture-harness](#vis-capture-harness).

<a id="hull-cook-chain"></a>
## hull-cook-chain

*Addresses of Valve's convex hull cook (vphysics2 RnHullCreate, resourcecompiler shape conversion and region SVM builder), 2026-09-24 binaries*

Prose is in docs/HULLS.md; addresses kept here because docs don't quote them.

- vphysics2_20260924.dll is in Ghidra (imported via reva_call import-file and analyzed). Decompile with `/d/tools/decpv.sh <addr>`. It exports RnHullCreate 1801ac5c0, RnHullCreateBox 1801ac890, RnHullCreateRuntime 1801ad740, RnMeshCreate 1801ad8a0.
- CVPhysics2Interface vtable 1803fbf50; slot 0x1f8 → RnHullCreate.
- In RnHullCreate:
  - AABB FUN_1801303b0.
  - Build FUN_1803843f0, which normalises, then runs quickhull init 1803c3440 / Build 1803c5650 / IsValid 1803c6300 / destroy 1803c3510.
  - Extrude FUN_180384ab0, limits FUN_180385370, check FUN_180385100, unread step FUN_1803c7020, translate FUN_1803c70c0.
  - Result FUN_180384f80.
  - Quickhull → RnHull_t FUN_1801a7310, then passes mass 180292700, area 18012ffd0, centroid radius 1801302f0.
- resourcecompiler_20260923.dll (Ghidra copy; the Steam copy changed on 09-24 and is saved as binaries/resourcecompiler_20260924.dll):
  - shape loop FUN_1802c4a20 → hull shape FUN_180c253c0; interface global DAT_183b5b5c0;
  - SVM init 1819d08a0, build 1819d2250, write 1819d25c0, free 1819d18d0; ported bit-exact in Physics/RegionSvm.cs (2026-09-25). Region nodes: root 1819d45c0, edge 1819d3e90, face 1819d41c0, vertex 1819d4740. Split step 1819d4d70, plane choice 1819d2c80, fallback 1819d2740 → separator 1819d3600 (edge helper 1819d34c0). Clip flag DAT_18399447e is bss and never written (clip FUN_1819cfc20 dead). SVM planes moved in FUN_1819595d0 like face planes;
  - RnHull_t schema binding FUN_1819f2a70.
- Helpers: /d/tools/fieldscan.py (functions storing to given offsets) and /d/tools/globref.py (RIP-relative users of a global).

See [reva-headless](#reva-headless), [ze-focus](#ze-focus).

**First stage.** The hull points come from physicsbuilder:
- FUN_18015aff0 ("%s_hull_shape") → FUN_18015f450 builds the options from the CModelDocPhysicsHullFile attributes (+0x1a0 angle, +0x1a4 max verts, +0x1a8 import mode, +0x1b4 algorithm).
- It then hulls via FUN_1800d4600 / FUN_1800d3f40.
- FUN_18015b640 copies each quickhull's vertex list (FUN_1800d5310) into hull_vertices.

**Port and validation.** Ported in src/Source2.Compiler/Physics (commit 2434a43). tests/HullReplay.cs (HULL=<vpk>) replays the shipped hulls.

**Map builder (resourcecompiler 0923).**
- Physics type resolver: FUN_181083540.
- Per-mesh physics piece: FUN_1801ff720, called from FUN_18020b230 with 0xf0-byte entries; +0x58 holds the type.
- Hull from mesh: FUN_18131f680, modes 0 single / 1 per mesh / 2 per element. It calls builder FUN_18131efc0 (same as vphysics2's FUN_1803843f0) and creates the nodes with FUN_1814e81b0.
- Element split: FUN_18130cb80 → FUN_18130c0d0. Per-element vertex hash set: FUN_18130f460. Mesh build from the map mesh: FUN_1813089b0.

**Simplifier (resourcecompiler 0923), ported in Physics/HullSimplifier.cs.**
- Limits FUN_1813207c0 (algo 0 FUN_1813d3db0, 1 FUN_1813e0460, 2 FUN_1813f11f0); needs-simplify FUN_1813d1ed0.
- QEM: hull→mesh FUN_1813d0f90/FUN_1813cfad0; simplify FUN_1813fc5d0 (adjacency 1813f9e70, quadrics 1813fae30, cost 1813fd060 + Cholesky 1820682a0/182068430/182068480, collapse 1813fa630, retarget 1813fc310, flip 1813fbf30, link 1813fb800).
- Agglomerator ctor FUN_1813d02e0 (leaves 1813d3a30, dual 1813d20d0, edges 1813d30f0); generic clustering 1820712f0/182071730/1820722c0; cost vfunc 1813d06c0; record 1813d1560; planes 1813d0aa0/1813d3700; rebuild loop 1813d3f00; hull from planes 181bfd190 (dual qh tolerance scale 1.0 at +0xc).
- Dual mesh is Valve's generic half-edge lib (collapse 181382170, merge 1813a7d00): modelled, not ported.
- Lesson: Ghidra's printer drops parens in float `*`/`+` chains (a*(b*c) prints as a*b*c). Check association with tools/re/symsse.py before trusting a decompile's float order.

**Per-corner mesh (resourcecompiler 0923/0924 same RVAs), 2026-09-25.** The builder reads each mesh node as a binary DMX: list via FUN_18023bd00 (interface slot 0x168 -> forwarder FUN_180f74350 -> slot 0x640 = FUN_1810dff20 "ConvertMeshForBuilder" for CMapMesh), 0xf0-byte entries, buffer data ptr at +0x20, size +0x38; reader FUN_180ffd010, DMX->CMesh FUN_180d47810/FUN_180d47b90. In ConvertMeshForBuilder: copy FUN_1810c1eb0, world move FUN_1810c4e50 (normals/tangents via FUN_18125d1b0, skipped for entity meshes), deformer FUN_1810c4000, smoothing FUN_1810db6a0, texcoord range check FUN_1810e7020 (+-1.03125) then island shift FUN_1810d93c0 (islands FUN_1813b6550, edge join FUN_1813b6190 dist^2<=1e-6, shift FUN_1813cc880), DmeMesh writer FUN_180d5c570. tools/hulls/dump_meshbuf.py dumps them; ported in MapMeshCorners.ShiftTexcoords, weld wired via BrushHulls.Pieces -> Mako 1266/1266 hulls.

**World collision (vphysics2 0924), 2026-09-25.** RnMeshCreate 1801ad8a0 (export; args tris, int* indices, u8* materials, vcount, float3* verts, mesh out, options{u8 weld, f32 tol, f32 simplify, i32 target}); weld FUN_180383b00 (CVertexKDTree, clusters in formation order); BVH FUN_1801a4070, SAH FUN_1801aab10 (32 bins, 31.999996 scale), halve FUN_1801a93e0, node alloc FUN_1801a3a70; leaf reorder FUN_1801a8960; adjacency FUN_180165750; flags FUN_1801673a0 (self test FUN_180164000 + pair FUN_180165350, tetra FUN_18008d0b0); ortho FUN_180166ff0. Output struct: +0 min, +0xc max, +0x18/+0x20 nodes, +0x30/+0x38 verts, +0x48/+0x50 tris, +0x90/+0x98 mats, +0xa8 ortho, +0xb4 flags, +0xbc area. Ported in Physics/RnMeshBuilder.cs, replay RnMeshReplay. World input goes resourcecompiler FUN_1801ff720 mesh branch -> modeldoc node (FUN_18140ebc0) -> physicsbuilder. Only a FULL compile (no -fshallow) ships world_physics.vmdl_c; atixref full compile ~20 min (vis).

## World collision assembly (2026-09-25)
- physicsbuilder 0924: CPhysicsBuilder::Build 1800142d0; tree walk 180014aa0/180014b40 over CMapNodeDataProvider (children = map node +0x200 handles); per-node meshes 180016e90 -> 180016230 -> CPhysicsBuilderWorld_ModelDoc::vf1 18001b280 -> node creator 18001b0a0; material physics record 180018450 / ctor 180017d90 / attribute reader 1800132f0, table at 180ba72b0 (13 x 0x20).
- resourcecompiler 0923: ModelDoc physics node -> shape 1802c0e70 (mesh shape 1802c0820, init 180c25ce0); node loop 1802c3030; part insert 180c28150 = append + V_qsort(cmp 180c28210: a.type-b.type) + renumber; part builder 180c28230; mesh gatherer 180c29500 (desc 180c25900, join 180c27e30, RnMeshCreate call 180c28690).
- tier0 V_qsort 180007cf0 = CRT qsort (shortsort <= 8 unstable on ties). Port: Maps/CrtQsort.cs.
- Capture: tools/physics/capture_physshapes.py (--full --rnmesh). cardtest soups exact (b8b919e).
- Static props in world physics (physicsbuilder 0924): 180014b40 also lists node entities (+0x180); prop_static with solid==6 -> 1800174e0 (CreateStaticPropShapesForPropInstance): per body of the prop model's compiled PHYS, transform, then sink vf2 18001b420 adds spheres(0x28), capsules(0x38), hulls(0x110), meshes(0xd8), 0x178-entries in that order as ModelDoc nodes. Prop meshes therefore join the world soups. Checker: tools/physics/check_partorder.py.

<a id="light-precompute"></a>
## light-precompute

*Light precomputed* keys port (s2c-entities, uncommitted as of 2026-09-25): what is exact, the scene it traces against, what is left*

Port lives in s2c-entities src/Source2.Compiler/Maps/Light*.cs (LightPrecompute = SampleVolume driver,
LightOmni = FUN_181298170, LightTrace = TraceSamples/TraceRay, LightObb, LightSampler, LightBuild, LightPow, LightCrt).
Oracle tests: tests/.../LightBuildOracleTests.cs, LightPrecomputeOracleTests.cs (resourcecompiler.dll in-process).

Exact in-process: whole TraceSamples+TraceRay+OBB fit against an EMPTY scene (fake world, FUN_1801f6f40 at
world+0x2bd0), omni frusta, barn builder, sampler, powf (log exponent bias is 1023, not 1022), trig via MathF.

The scene is NOT the .rte: it is Hammer's editor ray-trace scene at world+0x2bd0 (two-level instances,
0xd0 per instance). Mask 0xc00060b1 is tested against the object's flags (skip whole mesh) and the
triangle's flags at +0x2e. CMapMesh object flags (FUN_1810e11a0): 0x4000 when the parent is a CMapEntity, so
brush-entity meshes never occlude; 0x20 when node+0x278 is 0; 0xa000/0x2000 from byte +0x3a74.
Triangle flags = per-material FUN_18020d860 (material int attributes by murmur hash) | 0x20 for faces with
face flag 4; faces with flag &3 or no material are skipped (FUN_1813ccd20). Triangles added by FUN_181c14020.

Empty-scene result (probe LightPrecomputeProbe, LIGHT_DUMP): atixref 239 lights, key presence exact (121 with keys,
89 subfrusta counts exact); every Valve bound inside ours except 8 instanced copies whose lump origin/angles are
themselves wrong (7876, 7879, 7882-7888 nested group; 7828, 7836 angles), masked today by the LightClass gap.
Remaining: the scene (needs capture or port), light angles rewrite + directlight (FUN_180240a60),
precomputed_vis_clusters (vis membership), capsule luminaire FUN_181296730, light_rect branch.

**Why:** a long decompile trail; restart from here.
**How to apply:** verify addresses against the installed build first.

<a id="settle-port"></a>
## settle-port

*Physics settle port (Rubikon/vphysics2) , method (in-process DLL oracle), addresses, notes location, what is exact*

Porting resourcecompiler's physics settle (2700 x RnWorld::Step at 1/90, vphysics2 FUN_180200840) into
src/Source2.Compiler/Simulation (not Physics/, which is the hull agent's).

**Method that works:** load the installed vphysics2.dll into the test process (NativeLibrary, hash-gated to
the 2026-09-24 build, tests/Vphysics2Oracle.cs) and call internal functions by address on the same memory as
the port; random-input oracle tests (IntegratorOracleTests). Valve's structs are mirrored byte for byte
(RnBodyState 0x280, SolverBody 0xC0) so captures and the DLL share memory. Allocate 16-aligned: Valve uses movaps.
Read float order with tools/settle/lanesym.py (4-lane SSE tracer, --alias/--set/--ssa).

**Why:** hand transcription from Ghidra was wrong in the last bit; the oracle catches it in seconds.

**Facts:**
- tier0 cosf/sinf/expf are AMD libm FMA3 builds (CPU flag at tier0 0x1803ab28c); not correctly rounded; ported in CrtMath.
- Machine is AMD Ryzen 7 7800X3D (FMA3 path; rsqrtps results are CPU-specific).
- atixref settle: 81 bodies, all asleep by step 75; Valve's own runs differ in last bits on 2 bodies (multithreaded).
- Settle builds collision live from the map document (world meshes = triangle meshes), not from compiled vphys.
- Mapping notes (agents, with addresses): D:/tools/settle_notes/notes_{solver,collide,broadphase,setup}.md, raw decompiles in s/ and collide/.

Integration layer exact as of 2026-09-25 (commit ca2e9ad). Next: contacts (SAT hull-hull, hull-mesh), solver,
broadphase order, CCD, world setup. See [entity-lump-port](#entity-lump-port), [vis-capture-harness](#vis-capture-harness).

Ghidra names (2026-09-25): entity-side functions are primary-labelled in the shared project (vphysics2 CRnWorld_*,
RnSolver*, CRnContact_*, RnHull_*, RnMesh*, RnTree_*, CBroadphase_*; resourcecompiler MapSettle_*, PhysDoc_*,
PhysObj_*, EntityLump/Fgd/TemplatePass/MapDoc_*). Lists and the apply script: D:/tools/names/ (apply.py skips
anything already named). Table and dependency map: docs/SETTLE_FUNCTIONS.md. Peer (map builder/physics area) owns
the mesh collision pieces builder, physics-type resolver, CTransform math: ask before renaming those.
In-process oracle: vphysics2 CreateInterface("VPhysics2_Interface_001") → slot 0x60 CreateWorld works in a plain
process (whole Rubikon worlds step in tests); resourcecompiler.dll also loads in-process.

World build order: static creation order measured NOT to change the atixref result.

Status 2026-09-25: whole Step exact. Replays from captured worlds are exact through 2700 steps and write-back on
atixref (81 props), c2m2 prefab (63) and Mako (28). From the map alone (SettleWorld.CreateWorld, 41cac24) atixref
lands 79/81; the 2 misses sit by a vertex-paint tessellated mesh (subdivisionData, not ported yet). Graph-coloured islands are ported (threshold: contacts/4 >= 25 in either
group, i.e. 100 contacts, not 25; on by default via island manager +0x50) and exact, also against Valve on 7 threads.
Gotcha: the collision group table (vphysics2 0x18045b328, 64x64 u16) is filled by vphysics startup FUN_18006e1d0 ->
FUN_1802d79a0 (ported: Simulation/CollisionGroupTable.cs; creating a world in-process does not run it; identical on atixref, c2m2, Mako). Without it
the port misses contacts. settle_bundle.js now dumps it; older bundles borrow atixref's via SETTLE_CONTACTS=cap7.
Open: joints (and their colours), sphere/capsule/compound paths, the out-of-world-bounds fix-up
(FUN_1801faf80), light precompute.
World build from map+models (committed f100ea0 and 41cac24): Maps/SettleWorld*.cs (Build, Settled,
CreateWorld), Simulation/RnMassUpdate.cs (mass, capsule, drag axes), CollisionGroupTable.cs (FUN_1802d79a0 = captured table).
SettleFromMapTests: world from vmap+models only, step 0 exact but 2 bodies (+awake order); 79/81 after 2700 steps.
Open: vertex-paint tessellation (meshData.subdivisionData, subdivisionLevels per edge, displacement) blocks 2 props;
toolsclip under entity (351, 6181) Valve builds no shape - rule not found; smart prop evaluation; SetType order = pointer hash.
Build privately: dotnet build --artifacts-path D:/s2c_me_artifacts (other sessions lock bin/).
Captures (scratchpad settle/): bundle_atixref, bundle_c2m2_fairgrounds_csgo_environment_prefab,
bundle_ze_hold_em_p (all static, 0 steps), bundle_ze_ffvii_mako_reactor_v6_p; *_v1 = pre-group-table, cap5 islands, cap7 narrowphase+world.

CLOSED 2026-09-25 (pushed to box research/map-authoring c810cc3): the settle runs in the entity lump
(SettleWorld.Run -> EntityLumpSet.Author settled:) and every settled prop's origin/angles/spawnflags match
Valve's lumps on atixref, c2m2 prefab, Mako. Trigger meshes: class auto_apply_material (toolstrigger) replaces slots.
Glass "translucent" row: measured rule (csgo_glass.vfx or F_TRANSLUCENT=1), not disassembly-confirmed.
Valve's multithreaded collide sorts with unstable MSVC std::sort (FUN_1801dceb0 -> FUN_1801e6a70): contacts with
equal keys can come out either way (13/81 runs on a pile); none of the 3 settles has a key tie.
Light work left uncommitted in the s2c-entities worktree: InstanceBake.cs, LightScene.cs, LightSceneOracleTests.cs,
light_scene.js, and edits to MapInstances.cs, EntityLumpAgainstValveTests.cs, capture_settle.py, docs/MAP_RESOURCES.md.
See [light-precompute](#light-precompute) and [entity-lump-port](#entity-lump-port).

<a id="smart-props"></a>
## smart-props

*CMapSmartProp evaluator port (2026-09-27): atixref radiator 13/13 bit-exact; addresses, the world round trip, enum/default tables, what throws*

Smart prop evaluator: src/Source2.Compiler/Maps/SmartPropEvaluator.cs, wired into WorldCollision (SmartPropPieces) via MapMeshes walk recording CMapSmartProp as an EntityNode. atixref radiator_01: all 13 mesh pieces bit for bit (atixref now 522/522 exact after the 09-28 world-cut, per-vertex weld and last-writer tessellation fixes).

Chain (addresses here, not in docs):
- resourcecompiler 0923: 181230360 calls SmartPropsSystem_001 (global 183b5b4c0) vtable +0x88 with the node's WORLD CTransform (MapNode_WorldMatrix -> 18125b270 -> MatrixQuaternion 18125de90, scale = node+0xb8), then 181c51040 rebases every record: Compose(CTransform_Invert 181253780 (W), record). 18122d020 / 181c51410 later Compose(W, record) again. The round trip is the ~1e-4 the props carry; evaluating in node space misses all 13.
- smartprops 0923: ctx ctor 18000d4b0, setup 18000eb00 (object +0x170 = element +0x100 = given transform), FitOnLine eval 180026230 (item pos lanes: cross + ((w*t) + p), like Compose), select 1800250b0, caps 180024f20, filter 180024ad0, LinearLength 1800249a0, place 180026030, scale passes 180025b20/c00/e60.
- Defaults read from constructors: FitOnLine 180027c00 (pick LARGEST_FIRST, scale NONE, m_PointSpace ELEMENT, orient false); EndCap 1800273e0 (enabled, m_bStart, m_bEnd all true); LinearLength 180026af0 (len/min/max 1, allowScale false); Model 18000b9xx (scale 1,1,1, uniform 1, detail false, lod -1); CreateSizer 180051a9c (all initial/constraint 0); Translate default space ELEMENT.
- Enums: pick LARGEST_FIRST 0 / RANDOM 1 / ALL_IN_ORDER 2; scale NONE 0 / END_TO_FIT 1 / EQUALLY 2 / MAXIMIZE 3; space WORLD 0 / OBJECT 1 / ELEMENT 2; comparison EQUAL 0 .. GREATER_OR_EQUAL 5.
- Prop meshes: physicsbuilder 180106be0 builds a half-edge mesh; the part builder sees it renumbered by first use (BrushHulls.TriangleMesh with cornerIds = the face indices). This was the first prop mesh ever verified.

Tools: tools-free RTTI-less ctor finding = scratchpad vtscan.py (pointer scan + lea scan); ReVa read-memory for enum tables (0x20-byte entries {name, value, flags, desc}).

2026-09-28: also ported ModifyState (vtable slot 0x68 returns 0 = transform not restored), PickOne (180030ad0 -> chooser 18000fab0/18000f8e0: stored choiceValue; modes RANDOM 0 / FIRST 1 / SPECIFIC 2 via m_SelectionMode, m_SpecificChildIndex), Scale (180019410), CreateLocator (180017910: m_bConfigurable default true -> compose with locator config m_DeltaTransform, identity default), LARGEST_FIRST (180024dd0). Ghidra hides that Translate/Scale/Locator store the COMPOSED quat (disasm: xmm1 stored) - fixed. Master stream +0x194 is never seeded (tier0 default ctor = clock), so INT_MIN seeds / unstored RANDOM picks throw NotSupported (a real compile is nondeterministic there). Mako: all 35 smart props evaluate; stored config paths match placements 1:1; positions unverified (no capture).

Not ported (throws or notes): instances/scaled nodes, stretch (min != max), ALL_IN_ORDER, orient along line, root modifiers, IsValid criteria, detail objects, surface override, stored m_DeltaTransform, node collision_override string (node+0x690), lattice records (+0x98).

Related: [static-props](#static-props), [progress-tracker](#progress-tracker).

<a id="static-props"></a>
## static-props

*Static-prop world collision port (started 2026-09-27): hulls bit-exact, how the chain works, the -0 lesson, what's next*

Static props in world_physics (tracker n4). Hulls are done and bit for bit (commit 90e6c14): ze_hold_em_nb/ze_hold_em_p 129/129, c2m2 environment prefab 1348/1348, cs_script_demo 12/12 (StaticPropHullsReplay, PROPHULLS=<addon>|<map>, compares with the map vpk's world_physics.vmdl_c).

Chain (addresses in docs are not allowed, so here):
- physicsbuilder 0924: walk 180014b40 (per node: meshes, then entities; prop_static with KV solid (default 6) == 6) -> 1800174e0 per body (bind pose m_bindPose, bone override table at +0x268 not ported) -> sink 18001b420 -> hull node 180152fd0 (PhysicsShapeHull: points + origin + MatrixAngles; scaled matrix: points moved, no transform; lattice +0x2b0 -> mesh node, not ported). Attributes 180153d40.
- resourcecompiler 0923: node -> shape 1802c0e70 (quickhull tol 0, <=256 faces/half-edges/verts), 180c261c0 stores CTransform, node transform 1814f34e0 = Compose(node, parent inverse) via 181253510, ShapeBuilder_BuildHullShape 180c253c0 (RnHullCreate, SVM, QuaternionMatrix 181260150, RnHull_Transform).

Lesson: the parent-inverse composition turns quaternion -0 into +0; only the region SVM planes showed it (brush hulls hid it behind an identity transform). Suspect signed zeros whenever a transform is skipped.

Part order + attributes done (commit 37e350d): atixref 3840/3840 hulls incl. 79 instanced, all hull attr/surf indices match. Non-obvious: collapsed instance copies are appended to the parent's children (walk them after siblings; MapMeshes does this now); instanced prop placement = SettleWorld.BakedPlacement (a plain matrix compose was off by ulps); surface table keyed by hash, attribute key normalised to tag sets; hulls register table entries before meshes. Ground truth for atixref: $TEMP/worldcol7 (capture + atixref_compiled.vpk kept before restore).

Spheres/capsules DONE 2026-09-28 (commit 2819644): physicsbuilder sink 18001b420 order spheres 180153740 (0x28), capsules 180152230 (0x38), hulls 180152fd0, meshes 180153390, 1801525d0 (0x178, unknown). Centre = 1800b9810 (point w=1 rows, same sum order as MapMeshes.Transform), radius = max col length (1800b6b50) * r. RC per-type passes 180c28230: spheres 180c26e50 -> 180c25810, capsules 180c26ac0 -> 180c25230 (node CTransform: t=2 q x v ... then *scale + pos; drops radius<=0), hulls 180c26ce0, meshes 180c29500. Ground truth map s2c_rounds + addon model models/s2c_test/round_shapes.vmdl in s2c_rc_probe (content + compiled). GameContent.Physics now reads addon models first.
Next: PosableSkeleton bone overrides, CMapPrefab contents (c2m2 multi), lattice, override keys; scaled props and prop meshes are ported but unseen in test maps. atixref's 24 unmatched mesh pieces (2026-09-27, commits cbd425d, 64c0e8c): 5 were world meshes moved by the wrong matrix (world meshes use AngleMatrix, entity meshes CTransform), 3 subdivided pieces needed the 1/32 weld in world space, 3 are empty pieces. The last 13 came from CMapSmartProp 6783 (radiator_01.vsmart): done 2026-09-27, 13/13 bit-exact, see [smart-props](#smart-props). Prop meshes are renumbered by first use at the part builder (half-edge mesh round trip).

Related: [hull-cook-chain](#hull-cook-chain), [progress-tracker](#progress-tracker).

<a id="stitched-subdivision"></a>
## stitched-subdivision

*SOLVED 2026-09-28: Maps/HalfEdgeMesh.cs + SubdivisionBake.cs port Valve's half-edge bake; c2m2 prefab + Mako whole-file exact; what is modelled/not ported; addresses*

Found 2026-09-28 on c2m2_fairgrounds_csgo_environment_prefab (s2c_rc_probe; capture $TEMP/worldcol8/c2m2d.bin/json with --dump, ground truth $TEMP/worldcol8/c2m2_compiled.vpk). Only pieces 1227/0 (blacktop01_wet) and 5079/1 (concrete_ext_14) differ: meshes mixing levels 2 and 3. 24 of 3204 triangles differ in 1227/0: tiny z residues (ours 3.8e-06 / 0.0021, Valve 0) and opposite quad diagonals along the shared edges.

Also Mako (capture $TEMP/worldcol2, full compile $TEMP/gt/ze_ffvii_mako_reactor_v6_p.vpk): 7 pieces, nodes 3945, 8000, 11644, 10740, all level-0 polygons beside level-2 faces; Valve's pieces have a few more vertices (107 vs 104, 1584 vs 1568).

Mechanism (rc 0923): the bake (FUN_1813baa40) splits faces level by level (lower first) with FUN_1813ca560, which splits real half-edges. Its edge splitter FUN_1813ba570(mesh, face, fromVertex, toVertex, 0.5) walks the chain of edges between two vertices (earlier splits may have cut it), sums the edge lengths (sqrt), finds the edge where 0.5*L falls, local t = (L*0.5 - acc)/len; t < 0.01 reuses the edge's start vertex, t > 0.99 its end, else FUN_18137a050 splits the edge at t. So a level-3 face next to a level-2 face puts its edge points by arc length over already-split edges, and the level-2 face's boundary cells gain those vertices (pentagons), later cut by the polygon cutter (first triangle keeps the cell's slot, the rest appended, as BuilderPatches does for quads).

A 1:1 port = simulate FUN_1813ca560 on a half-edge mesh in builder order (would also cover the uniform case BuilderPatches models). Our MeshTessellation stitched path (FUN_1813c26a0 style lerps) is the ray-trace scene's, not this.

Related: [world-physics-container](#world-physics-container).

Port started 2026-09-28 (decompiles saved in session scratchpad stitch/*.c; re-fetch with reva_call get-decompilation). Mesh object (param_1 of the bake): vertices data +0x20 (0x20 each), handles +0x38, count +0x30; half-edges data +0x58 (0x50 each; +0x10 next/opposite handle, +0x30 face handle), handles +0x70, count +0x68; faces data +0x90 (0x20 each), handles +0xa8, count +0xa0; handle = 22-bit index (0x3fffff = null) + generation in bits 22..31, handle-table entry 0x18 bytes {int dense index; ...; uint handle @+8}. Face level: byte array +0x1b38 indexed by 181399f40(face). Generic lib at mesh+8: 18138f230 (edge between two vertices in a face), 18138be80 (corner lookup), 1813922e0. Bake calls: 18139bd10 face data, 1813c7a40 level, 1813b7750/1813b82f0 per-face patch grids (positions / paint), 1813ca560 split, 1813c64d0, 18138e6e0, then position write-back via 1813c94f0 on +0xd0 (positions) / +0xb20 (paint), 181376f80 (AddEdgeToFace), 18137a050 (AddVertexToEdge), 181384470 (collapse inner face -> centre), 18138fa30 grid-slot lookups (param_7 slot table: 2^(level-1) grid).

DONE 2026-09-28 (commits 8f10de4, 050c629, c9dc5cf): Maps/HalfEdgeMesh.cs (containers 181375e20 add / 1813995a0 remove = swap-last + FIFO free list; SplitEdge 181379a40; AddEdge 181376810; Between 1813937a0 a->b; Corner 18138be80; lerp 1813a4a00; SplitBetween 1813ba570) + Maps/SubdivisionBake.cs (1813baa40 driver; collapse modelled). Keys to exactness: paint per corner (half-edge); baked vertices never welded (export numbers by vertex, piece weld gets a vertex stream 0x22); export cuts polygons on WORLD positions. c2m2 prefab + Mako whole-file exact, all other maps still exact.
Not ported: post-bake MergeVertices (1813a8ae0, tol 0x358637bd) + AssignSmoothingModeToEdges in BakeSubdivisionForFaces 1810c65c0. Capture tool tools/physics/capture_bake.py (--ops) ready for when a map needs them. MeshTessellation.TriangulateBuilder is now only a test baseline (SubdivisionBakeTests, BAKECMP).

<a id="vis-own-geometry"></a>
## vis-own-geometry

*What visibility still needs to run on a trace scene built from the .vmap; root cube done; flag word, sun and hints addresses*

State 2026-09-28: vis is exact from Valve's .rte. To run it from the .vmap:

- **Root cube: DONE.** VisVoxelizer.RootCube, from visbuilder's sampler constructor FUN_18002b730 on TracedBounds. Exact on 4 maps. The shipped bounds were previously read from the vvis. Current visbuilder addresses differ from docs/VISBUILDER_FUNCTIONS.md: VisBuild 0x18003d7a7, VoxelStageDriver 0x1800335a0, RTE loader 0x18004b4c0.
- **.viscfg** = pvstype + vDirToSun + visibility_hints (rc 0923 FUN_180240b7f writes the hints and pvstype).
  - vDirToSun is written in FUN_180244d30 at 0x1802451b0 from the light description (rbp+0x84).
  - Conditions: the light-info virtual (slot 0x118) true, type 2, byte rbp+0x1d, byte rbp+0x120 in {2,3}; the last passing light wins.
  - The direction is -column0 of the light's world matrix (legacy description FUN_180eed570: FUN_18125bad0 then a sign flip for type 2).
  - Probe maps give (0.258819, 0, 0.965926).
- **Triangle flag word: NOT ported.** The collector FUN_1802839d0 builds each mesh entry's flags; the emitter FUN_1802821f0 ORs in the material's FUN_18020d860 bits.
  - The entry fields are the world renderer mesh list's +0x1b0 (64-bit flags), +0xbc, +0x1a1 (traced byte, else 0x800), +0x1a5 (0x800).
  - Entries with flags & 0x131 == 1, or with bit 0x2000000000, are skipped.
- probe01/cardtest triangles from MapGeometry.RteTriangles are exact. Build a RayTraceEnvironment from them, then run the pipeline as VisPvsReplay.TheWholeBuild does.

See [vis-capture-harness](#vis-capture-harness), [stitched-subdivision](#stitched-subdivision), [light-precompute](#light-precompute).

<a id="world-physics-container"></a>
## world-physics-container

*world_physics.vmdl_c authoring (2026-09-28): whole file exact on all 9 maps with a compile (incl. Mako, c2m2 prefab); entity models exact; rules, what is left*

Started 2026-09-28. Code: Containers/WorldPhysicsAuthor.cs (container), Physics/WorldPhysics.cs (part assembly + WorldPhysicsTrees for PHYS/RED2/DATA). Tests: WorldPhysicsAuthorTests (WPAUTHOR re-authors Valve's decoded trees; WPBUILD=addon|map|compiled vpk builds from the vmap and diffs with KvTreeDiff), WorldPhysicsContainerSurvey (WPSURVEY), ContainerDumpProbe (CONTAINERDUMP, typed dumps).

Status: whole file exact (all four trees + container facts) on atixref, ze_hold_em_nb (WPBUILD_GPU=1), ze_hold_em_p (s2c_rc_probe vpk), ze_hold_em_paint and ze_hold_em_paint_flat. Floor fixed 2026-09-28: rc FUN_1813ca560 quad branch recurses corners 0,1,3,2 and each child keeps the parent's corner order (child corner j = Mid(q[k], q[j])); decoded from the WORLDCOL_SETS order permutation, then confirmed in the decompile. cs_script_demo's vpk is an 08-26 compile (older hull schema) - not ground truth.

Facts found: Zstd cut READ (tier0 binary_auto, see [ground-truth-ledger](#ground-truth-ledger)): buffer@48 + blobs@60 < 0x80001 LZ4 else Zstd; < 0x100 raw. Scalar 0/1 ints decode Int64, typed arrays stay UInt32. Hull ortho areas: rc 181a09e20 sums (row1+row0)+row2 (Ghidra shows running sum) - fixed, all 3840 hulls exact. Tool material hash READ: ResourceNames.ToolMaterialHash (FixupResourceName + MurmurHash2) per member; soup keeps the one hash with triangles (see [ground-truth-ledger](#ground-truth-ledger)). UserFriendlyName: physicsbuilder 180016230 AppendFormat(" [%s]") for blend layers, soup joins with "; ".

Attribute strings (solved): verbatim from the registering shape, tags distinct + alphabetical. World materials keep their spelling (default, conditionallysolid). Props: physicsbuilder 180153d40 rebuilds strings from the cooked attribute via vphysics2's intersection dictionary (group index -> name, interaction bits -> names), so they take vphysics2's registered spelling (Default, ConditionallySolid; list in Physics/CollisionNames.cs from vphysics2 0924 string pool at 0x3e0248..). Game-registered names (csgo_*) keep the model's spelling (assumed; no counter-example).

Package (2026-09-28): Physics/WorldPhysicsFiles.cs builds vmdl_c + world_physics.vrman_c; Maps/GameContent.cs is the production content reader (tests' PakModels subclasses it; Physics() still reads pak01 only, not addon models); Io/VpkWriter.cs; CLI `s2c map-physics <addon> <map> --into <vpk> -o <out>` [--gpu]. WPBUILD now checks the manifest too (5 maps, 0 diffs).
- vrman source: rc 1801f9550 AddManifest -> 181c1f980 appends to flat `resourceManifest` -> 181c1fae0 SaveKV3 text generic, CRLF, no final newline; CRC32 = RED2 m_AdditionalInputDependencies m_nFileCRC, search path csgo_addons/<addon>. world.vrman is in-memory (1800ff1f0), no file dep, but its ___OverrideInputData___ fingerprint is nonzero (not reproduced).
- Child RED2 is struct-typed (0L fingerprints, uint compiler fp, 1L IsChildResource) and 0923 RC writes m_SpecialInputDependencies empty (old workshop maps omit it).
- VPK: ordinal data order; tree = reverse first appearance at each level; chunk section per 1 MiB, u16 0x7FFF, u16 type 1 = BLAKE3 truncated 16 (older packages type 0x8000 MD5); MD5 tree/chunks/whole; signature section magic,1,0,0,0. Byte-exact on 4 local compiles. Workshop packages differ (other packer).
Entity models (2026-09-28, Physics/EntityPhysicsModels.cs, test ENTBUILD=addon|map|vpk, survey ENTMODELS=vpk): physics-only models whole-file exact atixref 86, ze_hold_em_p 24, Mako 324. Rules: instance copies via MapInstances.Expand callback; missing material -> no file (Valve's compile fails); FGD flags physics_only_model / render_as_world_but_physics_as_entity (HasFlag, unquoted true) and auto_apply_material (MetadataOf); attribute: a material with no group/tags leaves it unset -> attribute 0 (whichever registered first; world too), surface + tool hash per material; instance copies built from BakedPlacement transforms (BrushHulls transformOf); part flags 0, 2 with meshes; RED2 keep_vertices IntArg, classname fingerprint = StringToken; RED2 lists only named surfaces (world too). Mako (full compile kept at $TEMP/gt/ze_ffvii_mako_reactor_v6_p.vpk, 21 min): world_physics 62 tree diffs, all hulls exact (was 47,448; 408 at e3a2397, 113 at 28e393a, 62 at 51e8e58); capture 4,787/4,821 inserts exact. Fixes: CMapWorldLayer meshes are world; hidden nodes + subtrees skipped (MapEntities.HiddenNodes); convex_single/multi world meshes hulled in world space, input joined by .vmap vertex id (Inputs cornerIds); blend -> PhysicsSurfaceProperties1, painted convex -> " [layer]" name + tool hash 0; instanced world meshes via BakedPlacement then own matrix; instanced smart props Concat(entity.Path, Local(node)); unknown prop surface hash -> default. Entities IGNORE shader translucency (MaterialCollision.Read shaderTranslucency:false) - Mako render models' PHYS 237/237.
Decompiled 2026-09-28: soup join rc 180c27e30 (name: "; " join of non-empty, Long once >50 chars, next non-empty adds "; ..." + Sealed; per-tool-hash triangle counts in a hash map); rc 180c28690 soup finish (tool hash = the one hash with nonzero tris, else 0). physicsbuilder world callback 18001b280 -> 18001b0a0 (type 1/4 mesh, 2 -> 18001a950 convex single, 3 -> 18001ac20 convex multi); 18001a950 hulls ALL vertices of the per-material CMesh vertex buffer (1800c3430 copies buffer in order) via PhysicsBuilder_QuickHullBuild.
Convex input SETTLED by capture (tools/physics/capture_convex.py, $TEMP/convex_mako.json): quickhull input = per-material CMesh vertex buffer; the pipes were subdivision surfaces -> convex path tessellates subdivided meshes. Left on Mako: Also 7 stitched subdivided pieces (see [stitched-subdivision](#stitched-subdivision)), 27 built-mesh inserts without soup data, cable model unnamed_20788, render half of entity models.
Regression: scratchpad wpall.sh (FILTER=regex) runs WPBUILD on every ground truth; atixref's is $TEMP/worldcol7/atixref_compiled.vpk (the addon vpk is shallow). $TEMP/gt/ze_doom_p2_c_gameplay.vpk is a FAILED compile (30 errors), not ground truth.
In-game: ze_hold_em_p + atixref with our physics load and collide as Valve's (see [ingame-testing](#ingame-testing)).

Related: [static-props](#static-props), [smart-props](#smart-props), [progress-tracker](#progress-tracker).
2026-09-28 later: subdivision bake port ([stitched-subdivision](#stitched-subdivision)) made c2m2 prefab and Mako whole-file exact.
