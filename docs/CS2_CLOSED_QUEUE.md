# CS2-closed queue

What waits for CS2 to be closed, in the order to take it. Everything else is done with CS2 open: decompiles,
ports and offline tests ([HANDOFF.md](HANDOFF.md) "Rules"). Run heavy jobs through `tools/re/heavy.py`, delete any
crash dumps the game or resourcecompiler leaves, and kill everything you started when done. Each item names the
ledger or doc that is waiting for its result. Last updated 2026-11-02.

Steam has to be closed too for anything run in cubehost (part B). Part A needs only CS2 closed; A.1's in-game
step needs Steam open, since it launches the game.

## A. CS2 closed (Steam may stay open)

1. **The full compile test** (HANDOFF "Latest session (2026-11-02 later)"; memory full-compile-test). The first
   whole compile of a ZE map from its .vmap with only our code. About an hour, most of it the visibility build and
   the GPU bake:
   ```
   python tools/pipeline/full_compile_test.py s2c_rc_probe ze_hold_em_p --valve "<cs2>/game/csgo_addons/s2c_cubeprobe/maps/ze_hold_em_p.vpk"
   ```
   The script builds s2c, runs `compile-map` (full build, `-s2cbake`, `--gpu`) and `build-cubemaps`, then scores
   the package file by file against Valve's (expected: lit 55 of 56 files as on 10-19, plus the cube array).
   Then, in game: copy the package into game/csgo/maps (back up anything already there), launch
   `cs2.exe -tools -insecure -netconport 29000 -novid`, and run `python tools/pipeline/map_test.py ze_s2c
   ze_hold_em_p <spawn x,y,z>`. Look for missing lighting, magenta, wrong reflections and broken triggers.
   Report the score and what the game showed.
2. **Steam Audio's reverb through compile-map's builder** (ledger 199). The wiring is in and the .sapaths through
   it is exact; the reverb run is heavy CPU (about 90 s on 8 cores), so it waits for CS2 closed or the dev box:
   `SAREVERB_FULL=1 dotnet test tests/Source2.Compiler.Tests --filter SaprobeReverbFromTheBuilder` (expected: equal
   but for the one Embree-tie probe, 65).
3. **The colour-adjust specimen, Valve's compile** (ledger 197; nothing measured yet for a contrast other than 1
   or a coloured tint). The specimen is made: `content/csgo_addons/s2c_rc_probe/maps/s2c_coloradjust.vmap`
   (`tools/cubehost/coloradjust_specimen.py`: probe01 with cobblestone_ground_large_1 and italy_cobblestone_01).
   ```
   python tools/pipeline/compile_fresh.py s2c_rc_probe s2c_coloradjust --full --out %TEMP%/fresh_compile/s2c_coloradjust.vpk
   ```
   Copy the result to game/csgo_addons/s2c_rc_probe/maps/ for B.1. With vrad3's job kept (CPUBAKE_HOSTCAP,
   tools/vrad3/capture_host.py), the bake's material blocks also check RtxMaterials' colour matrices.
4. **Map scripts** (HANDOFF roadmap 11): Valve's fresh compile of cs_script_demo as the reference for our own
   .js/.ts to .vjs_c compile, and a pre-1.41.9.2 `.vjs_c` if one can be found.
5. **The Blender viewmodel add-on's in-game test** (memory blender-vm-anim): export a SCAR-20 reload into an addon
   and check that it plays.
6. **Steam Audio, the nav coverage appender** (ledger 186): a specimen where a random point survives, captured
   with `capture_randomstream.py --pin SEED`.

## B. CS2 and Steam closed (cubehost)

1. **The colour-adjust cubes** (after A.3): `python tools/cubehost/run_probe.py --map s2c_coloradjust --out
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

## C. After every CS2 update

`python tools/re/patch_check.py`, then `tools/re/build_diff.py --strings`, and `rc-oracle.ps1` (memory
patch-check). Refresh any reference older than the update (memory stale-references; cs2_closed_session.py
--fixtures).

## Done since the last window, with CS2 open

- 2026-11-02: TextureAverageColor read (ledger 197), Steam Audio's rebake rule and full builds from an empty
  package in compile-map (ledger 198), the Steam Audio Auto bake in compile-map (ledger 199), full_compile_test.py,
  the colour-adjust specimen.
