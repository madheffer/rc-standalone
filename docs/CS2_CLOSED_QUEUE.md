# CS2-closed queue

What waits for CS2 to be closed, in the order to take it. Everything else is done with CS2 open: decompiles,
ports and offline tests ([HANDOFF.md](HANDOFF.md) "Rules"). Run heavy jobs through `tools/re/heavy.py`, delete any
crash dumps the game or resourcecompiler leaves, and kill everything you started when done. Each item names the
ledger or doc that is waiting for its result. Last updated 2026-11-04 evening (A.10-A.12 done).

Steam has to be closed too for anything run in cubehost (part B). Part A needs only CS2 closed; A.1's in-game
step needs Steam open, since it launches the game.

Why each item waits (user, 2026-11-02: VAC is the reason for closing CS2 and Steam, not load; an item that is only
heavy runs with the game open at below-normal priority):
- **VAC**: Frida on any process, cubehost and RenderDoc (Valve's engine in our exe), anything that attaches to or
  reads cs2.exe, and launching the game with -insecure. These stay in the queue.
- **Heavy only**: our own s2c builds and tests (s2c starts no Valve process and touches no game process), and
  Valve's resourcecompiler run on its own (it does not touch cs2.exe). Run these with the game open:
  `heavy.py run --allow-cs2`, below-normal priority (`full_compile_test.py --cs2-open` does both).

## A. CS2 closed (Steam may stay open)

1. **The full compile test, in game** (HANDOFF "Latest session (2026-11-02 later)"; memory full-compile-test). The
   compile half ran 2026-11-02 with CS2 open (`full_compile_test.py ... --cs2-open`, 12 min in all: compile-map
   7 min, build-cubemaps 4.5 min): every step exit 0, and against Valve's fresh compile on the same GPU driver
   (%TEMP%/fresh_compile/ze_hold_em_p_lit_1102.vpk) 55 of 56 files byte for byte, 99.99% of Valve's bytes; the one
   left is world.vwrld_c, whose compile timestamp and per-compile fingerprint are the only differences
   (S2C_WORLD_STAMP pins them). Our package: %TEMP%/full_compile/ze_hold_em_p. Left, CS2 closed: copy it into
   game/csgo/maps (back up anything already there), launch `cs2.exe -tools -insecure -netconport 29000 -novid`,
   and run `python tools/pipeline/map_test.py ze_s2c ze_hold_em_p <spawn x,y,z>`. Look for missing lighting,
   magenta, wrong reflections and broken triggers. Report what the game showed.
2. ~~Steam Audio's reverb through compile-map's builder~~ (ledger 199): done 2026-11-02 with CS2 open. 73 probes,
   the only differing probe the Embree tie (65), as SaprobeReverbFromTheMap.
3. ~~The colour-adjust specimen, Valve's compile~~ (done 2026-11-04, ledger 197: %TEMP%/fresh_compile/s2c_coloradjust.vpk
   and its job; capture_host.py showed vrad3 bakes the constant colour matrix; ours now equals every lighting file).
   Was: (ledger 197; nothing measured yet for a contrast other than 1
   or a coloured tint). The specimen is made: `content/csgo_addons/s2c_rc_probe/maps/s2c_coloradjust.vmap`
   (`tools/cubehost/coloradjust_specimen.py`: probe01 with cobblestone_ground_large_1 and italy_cobblestone_01).
   ```
   python tools/pipeline/compile_fresh.py s2c_rc_probe s2c_coloradjust --full --out %TEMP%/fresh_compile/s2c_coloradjust.vpk
   ```
   Copy the result to game/csgo_addons/s2c_rc_probe/maps/ for B.1. With vrad3's job kept (CPUBAKE_HOSTCAP,
   tools/vrad3/capture_host.py), the bake's material blocks also check RtxMaterials' colour matrices.
4. ~~Map scripts, Valve's reference~~ (ledger 200): done 2026-11-02 with CS2 open. RC on this install wrote all
   seven cs_script_demo scripts (the files Steam ships are Valve's build machine's, with the Panorama
   preprocessor); ScriptAuthor (`s2c compile <in.js>`) equals them byte for byte. Copies in %TEMP%/vjs_refs
   (20261023 = as shipped, current = this install's RC). Left: a pre-1.41.9.2 .vjs_c, and .ts sources.
5. **The Blender viewmodel add-on's in-game test** (memory blender-vm-anim): export a SCAR-20 reload into an addon
   and check that it plays.
6. **Steam Audio, the nav coverage appender** (ledger 186): a specimen where a random point survives, captured
   with `capture_randomstream.py --pin SEED`.

7. ~~HasTexcoordTransform in resourcecompiler~~ (done 2026-11-03: 1 for italy_cobblestone_01, so not the shift; ledger 201). (ledger 201, italy_cobblestone_01's texcoord shift). Frida on a
   standalone resourcecompiler compiling s2c_coloradjust (`compile_fresh.py s2c_rc_probe s2c_coloradjust -world
   -fshallow`): log CMaterial2::GetBoolAttribute (materialsystem2 vf 0x48) for key 0x2661694a with the material's
   name and the answer, and whether the attribute merge (18000e690 in 20261015; find it in the installed build with
   patch_check) runs for italy_cobblestone_01 at all. A merge that runs and stores 0 means the rewrite (1800ad440)
   left a 0x1D; no merge means resourcecompiler reads only the vmat_c, and KeepsTexcoords becomes "never" for
   these shaders. Then the same for one csgo_environment_blend material with a transform (a specimen like
   coloradjust_specimen.py, e.g. materials/concrete/vertigo_concretefloorc_blend.vmat).

8. ~~Subdivided entries' tangents~~ (done 2026-11-04, ledger 38: capture_subdivtangents.py showed the bake makes the +0;
   1813b82f0's lerp * weight + grid, ported; 103,887 of 103,887 corners). Was: (ledger 38; 24 of 538 atixref entries differ by tangents alone). Frida on a
   standalone resourcecompiler (not cubehost) compiling atixref `-world -fshallow` (compile_fresh.py pattern, package
   backed up and cmp-restored as capture_texcoordtransform.py does). Find the 1103 addresses of
   HammerMesh_BakeAllSubdivision (0923 1810c6520, called in ConvertMeshForBuilder when 1813c83d0 says the mesh has
   subdivided faces) and HammerMesh_TransformToWorld with patch_check; on entry and exit of the bake and at
   TransformToWorld's entry, dump the half-edge mesh's tangent stream (and normal, position, texcoord) per face
   vertex for nodes 1370, 6516, 6778 and 1367 (match the mesh by its node id; tools/vis capture_meshturn.py already
   hooks TransformToWorld and gives the jsonl SubdividedTangentsProbe reads, MESHTURN=). The entry/exit pair tells
   whether the bake writes the tangents (then read its tangent step) or they come in from the load.

9. ~~The edge collapse on s2c_edgecollapse2~~ (done 2026-11-03 with CS2 closed, ledger 211). Frida on a standalone resourcecompiler only:
   back up game/csgo_addons/s2c_rc_probe/maps/s2c_edgecollapse2.vpk, then from tools/physics with
   S2C_BINARIES pointing at the main checkout's tools/re/binaries:
   `python capture_bake.py s2c_rc_probe s2c_edgecollapse2 %TEMP%/geomagent_ec2/bake_ops.jsonl --ops`; restore and
   cmp the package. Then BAKEBEFORE=<vmap>|<jsonl> (BakeBeforeMergeProbe: our MergeVertexPair calls against
   Valve's) and BAKEMERGE (BakeMergeCaptureTests). If the merge calls agree but the "after" faces do not, add a
   faces snapshot to capture_bake.py's MergeVertexPair hook (the faces() helper, as opHook sends) and compare
   after each merge.
10. ~~The missing-material fallback~~ (done 2026-11-04, GROUND_TRUTH 42: tools/re/capture_missingmaterial.py showed
   teleport02 resolves to materials/error.vmat, core's; next is CS2-open work, its flags through the shader
   attribute probe). Was: (OPEN 1.c5, GROUND_TRUTH 42; queued 2026-11-04). A material the content
   lacks (cardtest's teleport02, which lives in another addon) reaches the world node with attribute flags
   0x160100000 and a 256x256 representative texture, which is none of error.vmat, error_indicator.vmat or
   materialforerrormodel.vmat; ours gives it the empty set. No string anchors the fallback in materialsystem2
   (searched 11-04). Frida on a standalone resourcecompiler compiling cardtest -world: hook
   materialsystem2's material lookup by name (the IMaterialSystem2 FindOrCreate / the resource system's
   handle for the .vmat path) for "teleport02", log the returned material's resource name, its shader and its
   representative texture; then the flags follow from that material (CS2-open work: shader attribute probe).

11. ~~The prop aggregate draw order~~ (done 2026-11-04 by decompile, 8beaa82: WRB_BuildAggregate's sorts key on
   CMesh +0x58, welded vertex count descending, then the vertex floats, not the pointer). Was: (OPEN 1.c6; queued 2026-11-04). Valve's agg_prop draws come in
   non-increasing vertex count (56 of 56 multi-draw scene objects, atixref, deterministic over two compiles), and
   CAggregateMeshList::Compile sorts by (attribute bits 0x6000000000, the mesh pointer +0x0) (180290090). Frida on
   a standalone resourcecompiler compiling atixref -world -fshallow: at 180290090's comparator (or on entry to
   180272ae0) log each entry's mesh pointer, the mesh's vertex count, its model and LOD, and where the CMesh was
   allocated (hook the CMesh constructor / the allocation that creates it: its caller and the size requested). The
   question is what puts the pointers in descending size: allocation order (which then needs porting) or a size
   class of the allocator.

12. ~~An aggregate's missing stream as zeros~~ (done 2026-11-04, ledger 244: tools/re/capture_aggstreams.py showed the
   same CMesh goes stride 18 -> 22 inside WRB_BuildAggregate; 1802619c0's stream union and 1812e4040's
   zero-filled relayout read; port open, OPEN 1.c7). Was: (OPEN 1.c7, ledger 243; queued 2026-11-04). atixref's
   agg_prop_inferno_concrete_trim_weathered_dirt_blend_01_0 is one vertex buffer in Valve's output with
   VertexPaintBlendParams = 0 for the inferno_fountain_01 draw, whose CMesh (WRB_LoadPropMeshes) has no such
   stream. Ruled out statically: CMesh_Concatenate (1812dd570), InputLayout_MatchSignature (181363de0),
   MapBuilder_CMeshToHalfEdge (1813319d0). Frida on a standalone resourcecompiler compiling atixref -world
   -fshallow (package backed up and cmp-restored): log the stream lists (CMesh +0x8, count +0x20) of the two
   CMeshes at WRB_MeshesToModelDoc (1801fee20) entry, then at DmeMeshToCMesh (180d47b90) exit and at
   CMesh_BuildPackedInputLayout (181364720) entry for the same material; the first place the fountain mesh
   has the stream names the function to read.

## B. CS2 and Steam closed (cubehost)

1. ~~The colour-adjust cubes~~ (done 2026-11-04): three cubehost runs of Valve's s2c_coloradjust package
   (%TEMP%/cubehost_run/coloradjust_int, _int2, _int3) give three different arrays: the two map materials' seams
   follow their heap order (CUBEMAPS "The order within a layer"), up to 2,214 pixels a face. Our build-cubemaps of the
   same package: every pixel equals one of the runs, and every pixel the runs agree on is exact (the variance
   rule). Was: (after A.3): `python tools/cubehost/run_probe.py --map s2c_coloradjust --out
   coloradjust`, then a second run with `--renderdoc 1`. Compare the EXRs with our render
   (`CubemapFaceRenderer`; tools/cubehost/exr_diff.py) and the csgo_environment `$Globals` with the capture
   (`CUBEREPLAY_MATERIAL`, CubemapFaceInputsProbe.MaterialGlobals). This checks ColorMatrices' HSV path and
   TextureAverageColor.
2. **The team intro agents** (CUBEMAPS.md F; roadmap 0e). On ze_hold_em_p, faces 0 and 1 of cubes 0 to 6 differ by
   16 to 34 pixels: the skinned agent models that terrorist_team_intro, counterterrorist_team_intro and team_select
   spawn. Capture which models they spawn, with which pose and transform (cubehost Frida on client.dll's
   CAnimatableSceneObjectDesc creation), and their AO proxy splat (aoproxy_splat, 2 instances in the RenderDoc run).
3. **s2c_l5probe's -X viewmodel** (CUBEMAPS.md B): the pawn's viewmodel draws, if the build state is decided
   (ledger 149).
4. **Animated maps** (CUBEMAPS.md D): after the offline pixel diff of atixref_srt_1 against _srt_2 has localised
   the last 135,760 pixels, the captures it asks for.
5. **The map decompiler's Hammer re-save** (branch research/vmap-decompile, DECOMPILE.md "Handoff"):
   hammer_autosave.py in cubehost, then decompile_rc_batch.ps1.

## C. After every CS2 update or GPU driver update

`python tools/re/patch_check.py`, then `tools/re/build_diff.py --strings`, and `rc-oracle.ps1` (memory
patch-check). Refresh any reference older than the update (memory stale-references; cs2_closed_session.py
--fixtures).

A GPU driver update changes vrad3's output (and so ours, which runs Valve's SPIR-V): the 2026-11-01 05:21 NVIDIA
update (32.0.15.9579 to 32.0.16.1088) moved 737,382 of ze_hold_em_p's 1,048,576 irradiance blocks in Valve's own
compile, and ours equals Valve's on either driver. Every lit reference made before a driver update is stale for
lightmap parity: recompile it (`compile_fresh.py <addon> <map> --full --cs2-open --out ...`, no VAC concern).
Old-driver copies of the vrad3 references: %TEMP%/vrad3_refs/driver_32.0.15.9579; new ones in
%TEMP%/vrad3_refs/driver_32.0.16.1088 (the default %TEMP%/vrad3_refs files are still the old driver's).

## Done since the last window, with CS2 open

- 2026-11-02 evening (CS2 open, below-normal): A.1's compile half, A.2, A.4 and ScriptAuthor (ledger 200), the
  driver finding (part C), new-driver vrad3 references.
- 2026-11-02: TextureAverageColor read (ledger 197), Steam Audio's rebake rule and full builds from an empty
  package in compile-map (ledger 198), the Steam Audio Auto bake in compile-map (ledger 199), full_compile_test.py,
  the colour-adjust specimen.
