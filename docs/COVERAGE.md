# Coverage: every entity class and every compile option

The port has to support everything Hammer offers a mapper, not only what the
test maps happen to use. That means two lists:
- every entity class the game's FGDs declare, `point_servercommand` and the other
  server, logic and point classes included;
- every compile option in Hammer's build dialog and on resourcecompiler's
  command line.

This file is both lists, each entry with its state in the port.

How the lists were made (re-run after a CS2 update, with `tools/re/patch_check.py`):
- `tools/coverage/fgd_classes.py`: every class in core, csgo_core and csgo FGDs,
  following `@include` order the way the loader does.
- `tools/coverage/class_refs.py`: which compile DLLs name each class as a string.
  A class a builder names is one it treats specially.
- `CoverageClassesProbe` (`COVERAGE_CLASSES=<out.json>|<vmap>...`): which local
  maps hold each class.
- `tools/coverage/compile_args.py`: the argument names the map compile asks its
  compile context for.
- Hammer's build dialog switches: the strings in `tools/hammer.dll` beside the
  dialog's labels.
- `tools/coverage/probe_map.py`: the probe map `s2probe/probe_classes`, every
  offered class twice on a copy of cardtest: once bare, so the compile fills
  every default, and once with every key set to a non-default value of its
  type. Its lumps are pinned against resourcecompiler like the other maps.
- `tools/coverage/coverage_doc.py`: writes the entity half below from the above
  and `tools/coverage/class_notes.json`.

## Compile options

`s2c compile-map` takes the command line Hammer gives resourcecompiler and
reads it as resourcecompiler does:
- A `-name value` switch becomes a compile argument, typed float, then int, then
  string. A bare `-name` is the int 1.
- The builders come from a fixed table: world, phys, vis, lod, gridnav, nav,
  bakedlighting, sareverb, sapaths, sacustomdata.
  - `-all`, or naming none, selects every builder the game allows (CS2 has no
    lod or gridnav).
  - `-entities` without `-world` is an entities-only build.
- Like a `-fshallow` build, it keeps the package already at
  `<outroot or game>/csgo_addons/<addon>/maps/<map>.vpk` and replaces only what
  its steps write. With `-f` or `-fshallow2` it refuses, as the compile refuses
  a partial build.

What runs today:
- The world step in entities-only mode: the entity lumps, settled unless
  `-nosettle`.
- The physics step: world_physics.

The full world step (render geometry, with vis and the lighting bake inside it),
nav and Steam Audio are refused by name. A lump whose lights or probe volumes
would lose their bake keys is refused unless `--accept-gaps` is passed.
Hammer's Only Entities preset (`-entities -skipauxfiles -nolightmaps`) runs as
is.

Status words:
- **ported**: the part this option drives matches Valve's output.
- **partial**: the part is ported for some inputs; details given.
- **default only**: the port always does what the default does; the switch
  itself is not wired.
- **not read**: what the switch changes has not been read from the DLLs yet.
- **no output**: changes logging, paths or process behaviour, not the files.
  Still to be confirmed from the DLLs for each switch marked with a `?`.

### Hammer build dialog

Presets, with the descriptions Hammer shows:
- Full Compile: "Standard compile of all map components".
- Fast Compile: "Build World, Physics, and NAV, but no vis or lighting".
- Final Compile: "Build everything, including 'final' quality Lighting".
- Only Entities: "Build Entities. Nothing else!".
- Custom Settings.

Which switches each preset sets is still to be read from `hammer.dll`. The
labels below are the dialog's own strings. Where the dialog has no label beside
a switch, or which check box sets a switch is not read yet, the label column
says so.

| switch | dialog label | state |
|---|---|---|
| `-world` | Build world | refused by compile-map: render geometry and world nodes not started |
| `-entities` | Entities Only | compile-map: the lumps; exact on every pinned map except the lights' and probe volumes' bake keys |
| `-nosettle` | Pre-Settle physics objects (unticked) | compile-map: skips the settle (the settle does not port props with sphere or capsule shapes yet) |
| `-tileMeshBaseGeometry` | Only base tile mesh geometry | not read |
| `-deformables forced`, `-deformables none` | pairing not read | not read |
| `-rebake_surfacegraph` | Build World Dynamic Surface Effects; Rebake All Surface Effects From Scratch ( recommended ) | not read |
| `-debugvisgeo` | Debug Vis Geometry | not read |
| `-skipauxfiles` | no label found | not read |
| `-bakelighting` | Baked Lighting | not started (vrad3 is driven standalone as a stopgap) |
| `-lightmapMaxResolution N` | no label found | not started |
| `-lightmapVRadQuality 0/1/2` | Quality | not started |
| `-lightmapCompressionDisabled` | Compression (unticked) | not started |
| `-lightmapDisableFiltering` | Noise Removal (unticked) | not started |
| `-disableLightingCalculations` | Disable lighting calculations (debug texel density / chart allocation) | not started |
| `-lightmapDeterministicCharts` | Deterministic Charting Computations (slow) | not started |
| `-vrad3LargeBlockSize` | no label found | not started |
| `-gpuraytracing` | GPU Lightmap Baking | not started |
| `-nolightmaps` | no label found | no output: resourcecompiler has no such argument |
| `-vis` | Build vis | partial: every stage exact against captured inputs, Mako included; not yet end to end on our own geometry |
| `-phys` | Build physics | compile-map: world_physics, exact against Valve's decoded trees on 9 maps |
| `-nav` | Nav | not started |
| `-navdbg` | Save debug stages to file | not started |
| `-gridnav` | Grid Nav, Build grid nav | not started |
| `-lod` | Build LOD | not read |
| `-sareverb -sareverb_threads N` | Steam Audio: Bake Reverb, Threads | not started |
| `-sapaths` | Bake Paths | not started |
| `-sacustomdata -sacustomdata_threads N` | Bake Custom Data | not started |
| `-sabakestrictmode` | Strict Bake Mode | not started |
| `-steamaudio_gpu` | no label found | not started |
| `-threads N` | fixed | no output? (lighting has a deterministic-charts switch, so threads may change lighting) |
| `-fshallow`, `-maxtextureres 256`, `-quiet`, `-html`, `-unbufferedio`, `-i` | fixed | `-maxtextureres` changes texture output; the rest no output? |
| `-nop4`, `-outroot`, `-noasync_shaders`, `-retail`, `-noassert`, `-breakpad`, `-changelist` | fixed | no output? (`-retail` not read) |

Post-build actions run in the game after the compile, not in resourcecompiler:
Load in engine, Build cubemaps on load (`buildcubemaps`), Create minimap
(`minimap_create`), `buildsparseshadowtree`, Check map for problems. Cubemaps and
the sparse shadow tree write shipped files, so they are part of the port
(cubemaps, precomputed shadows); the minimap and the problem check are not map
compile output.

### Map compile arguments

What the map compile asks its compile context for. Hammer's switches arrive as
these; a few are only reachable from resourcecompiler's command line.

| argument | state |
|---|---|
| `world`, `entities`, `all` | read as the compile reads them (see above) |
| `skipmapload` | not read |
| `nosettle` | compile-map skips the settle |
| `tileMeshBaseGeometry`, `deformables` | not read |
| `bakelighting`, `nobakedlighting`, `lightmapMaxResolution` | not started |
| `steamaudio`, `sareverb_*`, `sapaths_*`, `sacustomdata_*`, `sacustombake_*` | not started |
| `meshletMaxTriangles`, `meshletMaxVertices` | not read (render geometry) |
| `keep_vertices` | written into world_physics' compile arguments as Valve does; the option itself not read |
| `entity_lump_params` | not read |
| `suppress_texture_compiles`, `permissive_panorama_compiles` | not read |
| `compress` | not read |

### resourcecompiler command line

| option | state |
|---|---|
| `-i`, `-outroot` | compile-map takes both |
| `-filelist`, `-r`, `-game` | not taken: compile-map compiles the one map `-i` names |
| `-f`, `-fshallow`, `-fshallow2` | `-fshallow` keeps the package (compile-map's only mode); `-f` and `-fshallow2` start empty and are refused for a partial build |
| `-novpk` | not read beyond one filesystem call |
| `-vpkincr` | no output: the parser skips it and nothing reads it |
| `-skiptype` | not read |
| `-nop4`, `-changelist`, `-norevert` | source control; no output? |
| `-v`, `-pause`, `-pauseiferror`, `-html`, `-logwarnings`, `-breakpad`, `-telemetry_level` | no output? |
| `-dependency_check_only`, `-crc` | not read |
| `-allowdebug` | not read |
| `-vulkan` (and its validation switches) | renders on Vulkan; the material capture showed the same bytes under `-vulkan` |
| `-vis_preview` | not read |

<!-- generated by tools/coverage/coverage_doc.py; edit the script or class_notes.json, not this part -->
## Entity classes

354 placeable classes are offered: the ones csgo.fgd loads through its @include chain and does not
@exclude. 81 more are declared in FGD files but not offered (listed last); a map can still carry
one (Mako holds info_world_layer entities), and the compile then treats it as a class the FGD does not know.
32 of them are named by a compile DLL, so the compile does something with them beyond copying
their keys into the entity lump. The other 322 only pass through the lump writer, which is driven by
the FGD's key types for every class alike.

Status:
- **exact**: present in a map whose every lump is compared against Valve's compile, with no allowed difference.
- **partial**: present in such a map, and some of its keys still differ (reason given).
- **unpinned**: present in a local map, but none with a pinned compile.
- **no sample**: no local map holds it; a probe map is needed.

### Classes the compile handles

exact 22, partial 10, unpinned 0, no sample 0

| class | kind | named by | what the compile does | port | status |
|---|---|---|---|---|---|
| cable_dynamic | Cable | resourcecompiler | lump: rendercolor written as the text "%i %i %i"; secondary_material added to the material list | path keys ported; rendercolor text write not ported; secondary material not checked | partial (rendercolor ships untyped; the write is not read yet) |
| env_combined_light_probe_volume | Point | resourcecompiler | cubemap and probe records for the bake; cubemaptexture, array_index and lightprobetexture* keys added; kept | kept; baked keys not ported (probes, cubemaps) | partial (baked keys (atlas textures, handshake, size) not ported) |
| env_cubemap_box | Point | resourcecompiler, physicsbuilder, vrad3 | as env_cubemap with box bounds; upgrade of older vmaps only renames boxproject_* keys | baked keys not ported; vmap upgrader not ported | partial (baked keys (cubemaptexture, handshake) not ported) |
| env_light_probe_volume | Point | resourcecompiler | probe record for the bake; lightprobetexture* keys added; kept | kept; baked keys not ported (probes) | partial (baked keys (atlas textures, handshake, size) not ported) |
| env_sky | Point | resourcecompiler, physicsbuilder, vrad3 | upgrade of older vmaps only: skyname gets .vmat | lump pass-through ported; vmap upgrader not ported | exact |
| func_movelinear | Solid | resourcecompiler | nav: movable nav mesh when CreateMovableNavMesh is set | lump ported; nav not started | partial (Mako world-layer instance copies: angles 1 or 2 ulps off (parked)) |
| func_nav_markup | Solid | resourcecompiler | nav: markup volumes | lump ported; nav not started | exact |
| func_physbox | Solid | resourcecompiler, physicsbuilder, vrad3 | upgrade of older vmaps only: spawnflags to gravity-grab keys | lump ported; vmap upgrader not ported | exact |
| info_cull_triangles | Point | resourcecompiler | dropped from the lump; a record for render-mesh triangle culling | dropped as Valve does; culling not ported (render geometry) | exact |
| info_particle_system | Point | resourcecompiler, physicsbuilder, vrad3 | snapshot_mesh naming a map node: snapshot_file set to maps/<map>/particle_snapshots/node_<id>.vsnap and the snapshot generated from the node; upgrade of older vmaps: snapshot_file gets .vsnap | snapshot_file key ported (not inside instances); .vsnap not generated; vmap upgrader not ported | partial (Mako world-layer instance copies: angles 1 or 2 ulps off (parked)) |
| info_player_start | Point | resourcecompiler | Hammer's map check only | nothing to port for the compile | exact |
| light_barn | Point | resourcecompiler, physicsbuilder, vrad3 | light description for precompute, bake and vis membership; precomputed* keys, directlight, lightcookie resource; upgrade of older vmaps only converts light_spot/omni/ortho | precompute exact against an empty scene; lump keys, bake and vis membership not ported; vmap upgrader not ported | partial (light export (shape keys, matrix angles) and baked keys not ported) |
| light_environment | Point | resourcecompiler, physicsbuilder, vrad3 | legacy directional light for bake and shadows; lump through the light path; upgrade of older vmaps only | lump partial; bake not ported; vmap upgrader not ported | partial (light export (shape keys, matrix angles) and baked keys not ported) |
| light_omni2 | Point | resourcecompiler, physicsbuilder, vrad3 | as light_barn | as light_barn | partial (light export (shape keys, matrix angles) and baked keys not ported) |
| light_rect | Point | resourcecompiler | as light_barn (no upgrader) | as light_barn | partial (light export (shape keys, matrix angles) and baked keys not ported) |
| path_corner | Point | resourcecompiler | nav: air-nav hint when info_nav_space exists | lump ported; nav not started | exact |
| path_generic | Path | resourcecompiler, physicsbuilder, vrad3 | upgrade of older vmaps only: classnames on paths, nodes and cables | lump ported; vmap upgrader not ported | exact |
| path_node_cable | PathNode | resourcecompiler, physicsbuilder, vrad3 | upgrade of older vmaps only | lump ported; vmap upgrader not ported | exact |
| path_node_generic | PathNode | resourcecompiler, physicsbuilder, vrad3 | the owning path's node keys (pathNodes, names, colours, radius scales, closed_loop) | ported | exact |
| path_node_particle_rope | PathNode | resourcecompiler, physicsbuilder, vrad3 | upgrade of older vmaps only | lump ported; vmap upgrader not ported | exact |
| path_track | Point | resourcecompiler | editor only (clone chaining) | nothing to port for the compile | exact |
| point_camera | Point | resourcecompiler | kept; a camera record added to a world list | lump ported; where the list is written not read | exact |
| point_nav_walkable | Point | resourcecompiler | nav: walkable seed | lump ported; nav not started | exact |
| point_scale_reference_human | Point | resourcecompiler | dropped from the lump | ported | exact |
| point_template | Point | resourcecompiler | entities it names are left out of the settle | ported (settle) | exact |
| prop_physics_override | Point | resourcecompiler, physicsbuilder, vrad3 | upgrade of older vmaps only: spawnflags to gravity-grab keys | lump and settle ported; vmap upgrader not ported | exact |
| prop_static | Point | resourcecompiler, physicsbuilder | dropped from the lump; static prop record for render, lighting and world physics | dropped as Valve does; world physics hulls exact; render and lighting not started | exact |
| sky_camera | Point | resourcecompiler | none found at compile time | nothing found to port | exact |
| snd_event_box_helper | Point | resourcecompiler | editor only (clone chaining) | nothing to port for the compile | exact |
| snd_opvar_set_point | Point | resourcecompiler | Steam Audio probe positions | not started (Steam Audio) | exact |
| visibility_hint | Point | resourcecompiler | dropped from the lump; the entity goes into the vis build's hints | dropped as Valve does; vis hints not wired | exact |
| worldspawn | Solid | resourcecompiler, physicsbuilder, vrad3, worldrenderer | kept; pvstype and the lightmap/vis settings (max_lightmap_resolution, novis_*, lightmap_queries) read into the build | lump ported; pvstype read by the vis port; lightmap settings not started | exact |

### Classes that only pass through the lump

exact 321, partial 1, unpinned 0, no sample 0

| class | kind | fgd | local maps | status |
|---|---|---|---|---|
| ambient_generic | Point | base.fgd | ze_hold_em_p, probe_classes +3 | exact |
| cable_static | Cable | base.fgd | probe_classes | exact |
| chicken | Point | csgo.fgd | probe_classes | exact |
| commentary_auto | Point | base.fgd | probe_classes | exact |
| counterterrorist_rush_intro | Point | csgo.fgd | probe_classes | exact |
| counterterrorist_team_intro | Point | csgo.fgd | atixref, cardtest +12 | exact |
| counterterrorist_team_intro_variant2 | Point | csgo.fgd | probe_classes | exact |
| counterterrorist_wingman_intro | Point | csgo.fgd | probe_classes | exact |
| cs_minimap_boundary | Point | csgo.fgd | atixref, cardtest +13 | exact |
| cs_minimap_volume | Solid | csgo.fgd | probe_classes | exact |
| csm_fov_override | Point | csgo.fgd | probe_classes | exact |
| custom_hud_layout | Point | csgo.fgd | probe_classes | exact |
| dz_door | Point | csgo.fgd | probe_classes | exact |
| end_of_match | Point | csgo.fgd | atixref, cardtest +13 | exact |
| env_blood | Point | base.fgd | probe_classes | exact |
| env_credits | Point | base.fgd | probe_classes | exact |
| env_cs_place | Solid | csgo.fgd | probe_classes | exact |
| env_cubemap_fog | Point | csgo.fgd | atixref, cardtest +9 | exact |
| env_decal | Point | base.fgd | probe_classes | exact |
| env_entity_igniter | Point | base.fgd | ze_ffvii_mako_reactor_v6_p, probe_classes | exact |
| env_entity_maker | Point | base.fgd | ze_doom_p2_c_gameplay, ze_ffvii_mako_reactor_v6_p +1 | exact |
| env_explosion | Point | base.fgd | ze_doom_p2_c_gameplay, ze_ffvii_mako_reactor_v6_p +1 | exact |
| env_fade | Point | base.fgd | atixref, c2m2_fairgrounds_csgo_gameplay +6 | exact |
| env_fire | Point | base.fgd | ze_ffvii_mako_reactor_v6_p, probe_classes | exact |
| env_firesensor | Point | base.fgd | probe_classes | exact |
| env_firesource | Point | base.fgd | probe_classes | exact |
| env_fog_controller | Point | base.fgd | probe_classes | exact |
| env_gradient_fog | Point | csgo.fgd | ze_ffvii_mako_reactor_v6_p, probe_classes | exact |
| env_hudhint | Point | csgo.fgd | ze_ffvii_mako_reactor_v6_p, probe_classes | exact |
| env_instructor_hint | Point | base.fgd | probe_classes | exact |
| env_message | Point | base.fgd | probe_classes | exact |
| env_particle_glow | Point | csgo.fgd | atixref, ze_ffvii_mako_reactor_v6_p +4 | exact |
| env_particlescript | Point | base.fgd | probe_classes | exact |
| env_physexplosion | Point | base.fgd | atixref, probe_classes | exact |
| env_physimpact | Point | base.fgd | probe_classes | exact |
| env_player_visibility | Point | csgo.fgd | probe_classes | exact |
| env_shake | Point | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p +5 | exact |
| env_shake_volume | Point | csgo.fgd | probe_classes | exact |
| env_shooter | Point | base.fgd | probe_classes | exact |
| env_soundscape | Point | base.fgd | atixref, cardtest +8 | exact |
| env_soundscape_proxy | Point | base.fgd | probe_classes | exact |
| env_soundscape_triggerable | Point | base.fgd | atixref, probe_classes | exact |
| env_spark | Point | base.fgd | atixref, c2m2_fairgrounds_csgo_gameplay +5 | exact |
| env_tilt | Point | base.fgd | probe_classes | exact |
| env_viewpunch | Point | base.fgd | probe_classes | exact |
| env_wind | Point | base.fgd | atixref, probe_classes | exact |
| filter_activator_attribute_int | Filter | base.fgd | probe_classes | exact |
| filter_activator_class | Filter | base.fgd | probe_classes | exact |
| filter_activator_context | Filter | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p +1 | exact |
| filter_activator_mass_greater | Filter | base.fgd | probe_classes | exact |
| filter_activator_model | Filter | base.fgd | c2m2_fairgrounds_csgo_gameplay, probe_classes +2 | exact |
| filter_activator_name | Filter | base.fgd | ze_doom_p2_c_gameplay, probe_classes | exact |
| filter_activator_team | Filter | csgo.fgd | atixref, ze_doom_p2_c_gameplay +6 | exact |
| filter_damage_type | Filter | base.fgd | atixref, ze_doom_p2_c_gameplay +2 | exact |
| filter_enemy | Filter | base.fgd | probe_classes | exact |
| filter_los | Filter | base.fgd | probe_classes | exact |
| filter_modifier | Filter | base.fgd | probe_classes | exact |
| filter_multi | Filter | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p +1 | exact |
| filter_proximity | Filter | base.fgd | probe_classes | exact |
| flashbang_projectile | Point | csgo.fgd | probe_classes | exact |
| fog_volume | Solid | csgo.fgd | probe_classes | exact |
| func_bomb_target | Solid | csgo.fgd | cardtest, probe01 +7 | exact |
| func_breakable | Solid | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p +8 | exact |
| func_brush | Solid | base.fgd | atixref, cardtest +6 | exact |
| func_button | Solid | base.fgd | atixref, c2m2_fairgrounds_csgo_gameplay +5 | exact |
| func_buyzone | Solid | csgo.fgd | atixref, cardtest +14 | exact |
| func_clip_interaction_layer | Solid | base.fgd | probe_classes | exact |
| func_clip_vphysics | Solid | csgo.fgd | ze_ffvii_mako_reactor_v6_p, probe_classes +1 | exact |
| func_conveyor | Solid | csgo.fgd | probe_classes | exact |
| func_door | Solid | base.fgd | atixref, c2m2_fairgrounds_csgo_gameplay +7 | exact |
| func_door_rotating | Solid | base.fgd | c2m2_fairgrounds_csgo_gameplay, ze_ffvii_mako_reactor_v6_p +1 | exact |
| func_guntarget | Solid | base.fgd | probe_classes | exact |
| func_hostage_rescue | Solid | csgo.fgd | probe_classes | exact |
| func_nav_blocker | Solid | csgo.fgd | probe_classes | exact |
| func_nav_gen_proj | Solid | markup_volumes.fgd | probe_classes | exact |
| func_physical_button | Solid | base.fgd | probe_classes | exact |
| func_platrot | Solid | base.fgd | probe_classes | exact |
| func_rot_button | Solid | base.fgd | c2m2_fairgrounds_csgo_gameplay, probe_classes | exact |
| func_rotating | Solid | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p +4 | exact |
| func_survival_c4_target | Point | csgo.fgd | probe_classes | exact |
| func_tablet_blocker | Solid | csgo.fgd | probe_classes | exact |
| func_tanktrain | Solid | base.fgd | ze_doom_p2_c_gameplay, probe_classes | exact |
| func_timescale | Point | base.fgd | probe_classes | exact |
| func_trackautochange | Solid | base.fgd | probe_classes | exact |
| func_trackchange | Solid | base.fgd | probe_classes | exact |
| func_tracktrain | Solid | base.fgd | atixref, c2m2_fairgrounds_csgo_gameplay +6 | exact |
| func_traincontrols | Solid | base.fgd | probe_classes | exact |
| func_useableladder | Point | base.fgd | probe_classes | exact |
| func_water | Solid | csgo.fgd | atixref, ze_ffvii_mako_reactor_v6_p +3 | exact |
| game_end | Point | csgo.fgd | probe_classes | exact |
| game_gib_manager | Point | base.fgd | probe_classes | exact |
| game_money | Point | csgo.fgd | probe_classes | exact |
| game_player_equip | Point | base.fgd | probe_classes | exact |
| game_ragdoll_manager | Point | base.fgd | probe_classes | exact |
| game_text | Point | base.fgd | ze_doom_p2_c_gameplay, probe_classes | exact |
| game_weapon_manager | Point | base.fgd | ze_hold_em_p, probe_classes +3 | exact |
| game_zone_player | Solid | base.fgd | ze_doom_p2_c_gameplay, ze_ffvii_mako_reactor_v6_p +1 | exact |
| gibshooter | Point | base.fgd | probe_classes | exact |
| hammer_updateignorelist | Point | base.fgd | probe_classes | exact |
| hostage_entity | Point | csgo.fgd | probe_classes | exact |
| info_armsrace_counterterrorist | Point | csgo.fgd | probe_classes | exact |
| info_armsrace_terrorist | Point | csgo.fgd | probe_classes | exact |
| info_constraint_anchor | Point | base.fgd | probe_classes | exact |
| info_deathmatch_spawn | Point | csgo.fgd | probe_classes | exact |
| info_enemy_terrorist_spawn | Point | csgo.fgd | probe_classes | exact |
| info_hostage_spawn | Point | csgo.fgd | probe_classes | exact |
| info_intermission | Point | base.fgd | probe_classes | exact |
| info_map_parameters | Point | csgo.fgd | cardtest, probe01 +7 | exact |
| info_map_region | Point | csgo.fgd | probe_classes | exact |
| info_null | Point | base.fgd | probe_classes | exact |
| info_offscreen_panorama_texture | Point | csgo.fgd | probe_classes | exact |
| info_paradrop_denial | Point | csgo.fgd | probe_classes | exact |
| info_particle_target | Point | base.fgd | ze_ffvii_mako_reactor_v6_p, probe_classes +3 | exact |
| info_player_counterterrorist | Point | csgo.fgd | atixref, cardtest +19 | exact |
| info_player_terrorist | Point | csgo.fgd | atixref, cardtest +18 | exact |
| info_spawngroup_landmark | Point | base.fgd | probe_classes | exact |
| info_spawngroup_load_unload | Point | base.fgd | probe_classes | exact |
| info_target | Point | base.fgd | atixref, ze_doom_p2_c_gameplay +4 | exact |
| info_target_instructor_hint | Point | base.fgd | probe_classes | exact |
| info_target_server_only | Point | base.fgd | probe_classes | exact |
| info_teleport_destination | Point | base.fgd | atixref, ze_doom_p2_c_gameplay +7 | exact |
| info_visibility_box | Point | base.fgd | probe_classes | exact |
| item_defuser | Point | csgo.fgd | probe_classes | exact |
| keyframe_rope | KeyFrame | base.fgd | probe_classes | exact |
| keyframe_track | KeyFrame | base.fgd | probe_classes | exact |
| logic_active_autosave | Point | base.fgd | probe_classes | exact |
| logic_activityevent | Point | base.fgd | probe_classes | exact |
| logic_auto | Point | base.fgd | atixref, ze_doom_p2_c_gameplay +7 | exact |
| logic_autosave | Point | base.fgd | probe_classes | exact |
| logic_branch | Point | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p +1 | exact |
| logic_branch_listener | Point | base.fgd | probe_classes | exact |
| logic_case | Point | base.fgd | atixref, ze_doom_p2_c_gameplay +3 | exact |
| logic_collision_pair | Point | base.fgd | probe_classes | exact |
| logic_compare | Point | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p +1 | exact |
| logic_eventlistener | Point | csgo.fgd | ze_ffvii_mako_reactor_v6_p, probe_classes | exact |
| logic_gamestate_report | Point | base.fgd | probe_classes | exact |
| logic_lineto | Point | base.fgd | probe_classes | exact |
| logic_measure_movement | Point | base.fgd | ze_ffvii_mako_reactor_v6_p, probe_classes | exact |
| logic_multicompare | Point | base.fgd | probe_classes | exact |
| logic_navigation | Point | base.fgd | probe_classes | exact |
| logic_npc_counter_aabb | Point | base.fgd | probe_classes | exact |
| logic_npc_counter_obb | Point | base.fgd | probe_classes | exact |
| logic_npc_counter_radius | Point | base.fgd | probe_classes | exact |
| logic_playerproxy | Point | base.fgd | probe_classes | exact |
| logic_relay | Point | base.fgd | atixref, ze_doom_p2_c_gameplay +3 | exact |
| logic_script | Point | base.fgd | ze_ffvii_mako_reactor_v6_p, probe_classes | exact |
| logic_timer | Point | base.fgd | atixref, ze_doom_p2_c_gameplay +7 | exact |
| map_preview_camera_path | Path | csgo.fgd | probe_classes | exact |
| map_preview_camera_path_node | PathNode | csgo.fgd | probe_classes | exact |
| markup_group | Solid | markup_volumes.fgd | probe_classes | exact |
| markup_volume | Solid | markup_volumes.fgd | probe_classes | exact |
| markup_volume_tagged | Solid | markup_volumes.fgd | probe_classes | exact |
| markup_volume_with_ref | Solid | markup_volumes.fgd | probe_classes | exact |
| math_colorblend | Point | base.fgd | probe_classes | exact |
| math_counter | Point | base.fgd | atixref, ze_doom_p2_c_gameplay +6 | exact |
| math_remap | Point | base.fgd | probe_classes | exact |
| momentary_rot_button | Solid | base.fgd | c2m2_fairgrounds_csgo_gameplay, probe_classes +2 | exact |
| move_rope | Move | base.fgd | probe_classes | exact |
| move_track | Move | base.fgd | probe_classes | exact |
| observable_element | Point | csgo.fgd | probe_classes | exact |
| path_particle_rope_clientside | Path | base.fgd | atixref, probe_classes +1 | exact |
| path_simple | Path | base.fgd | probe_classes | exact |
| phys_ballsocket | Point | base.fgd | ze_ffvii_mako_reactor_v6_p, probe_classes | exact |
| phys_constraint | Point | base.fgd | probe_classes | exact |
| phys_genericconstraint | Point | base.fgd | probe_classes | exact |
| phys_hinge | Point | base.fgd | probe_classes | exact |
| phys_hinge_local | Point | base.fgd | probe_classes | exact |
| phys_keepupright | Point | base.fgd | ze_doom_p2_c_gameplay, probe_classes | exact |
| phys_lengthconstraint | Point | base.fgd | probe_classes | exact |
| phys_magnet | Point | base.fgd | probe_classes | exact |
| phys_motor | Point | base.fgd | probe_classes | exact |
| phys_pulleyconstraint | Point | base.fgd | probe_classes | exact |
| phys_ragdollconstraint | Point | base.fgd | probe_classes | exact |
| phys_ragdollmagnet | Point | base.fgd | probe_classes | exact |
| phys_slideconstraint | Point | base.fgd | probe_classes | exact |
| phys_splineconstraint | Point | base.fgd | probe_classes | exact |
| phys_spring | Point | base.fgd | probe_classes | exact |
| phys_thruster | Point | base.fgd | ze_doom_p2_c_gameplay, probe_classes | exact |
| phys_torque | Point | base.fgd | probe_classes | exact |
| point_anglesensor | Point | base.fgd | probe_classes | exact |
| point_angularvelocitysensor | Point | base.fgd | probe_classes | exact |
| point_broadcastclientcommand | Point | base.fgd | c2m2_fairgrounds_csgo_gameplay, probe_classes | exact |
| point_camera_vertical_fov | Point | base.fgd | probe_classes | exact |
| point_clientcommand | Point | base.fgd | ze_ffvii_mako_reactor_v6_p, probe_classes | exact |
| point_clientui_dialog | Point | base.fgd | probe_classes | exact |
| point_clientui_world_panel | Point | base.fgd | probe_classes | exact |
| point_clientui_world_text_panel | Point | base.fgd | probe_classes | exact |
| point_commentary_node | Point | base.fgd | probe_classes | exact |
| point_deathcam_bounds | Point | csgo.fgd | probe_classes | exact |
| point_devshot_camera | Point | base.fgd | probe_classes | exact |
| point_dz_dronegun | Point | csgo.fgd | probe_classes | exact |
| point_dz_weaponspawn | Point | csgo.fgd | probe_classes | exact |
| point_dz_weaponspawn_group | Point | csgo.fgd | probe_classes | exact |
| point_enable_motion_fixup | Point | base.fgd | probe_classes | exact |
| point_entity_finder | Point | base.fgd | probe_classes | exact |
| point_gamestats_counter | Point | base.fgd | probe_classes | exact |
| point_hurt | Point | base.fgd | ze_ffvii_mako_reactor_v6_p, probe_classes | exact |
| point_instructor_event | Point | base.fgd | probe_classes | exact |
| point_message | Point | base.fgd | probe_classes | exact |
| point_orient | Point | base.fgd | probe_classes | exact |
| point_proximity_sensor | Point | base.fgd | probe_classes | exact |
| point_script | Point | csgo.fgd | probe_classes | exact |
| point_servercommand | Point | base.fgd | atixref, ze_doom_p2_c_gameplay +7 | exact |
| point_soundevent | Point | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p +6 | partial (Mako world-layer instance copies: angles 1 or 2 ulps off (parked)) |
| point_teleport | Point | base.fgd | atixref, ze_doom_p2_c_gameplay +4 | exact |
| point_value_remapper | Point | base.fgd | probe_classes | exact |
| point_velocitysensor | Point | base.fgd | probe_classes | exact |
| point_workplane | Point | base.fgd | probe_classes | exact |
| point_worldtext | Point | base.fgd | cardtest, probe01 +9 | exact |
| post_processing_volume | Solid | postprocessing.fgd | atixref, cardtest +14 | exact |
| prop_counter | Point | csgo.fgd | probe_classes | exact |
| prop_door_rotating | Point | csgo.fgd | atixref, ze_ffvii_mako_reactor_v6_p +4 | exact |
| prop_dynamic | Point | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p +5 | exact |
| prop_dynamic_ornament | Point | base.fgd | probe_classes | exact |
| prop_dynamic_override | Point | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p +3 | exact |
| prop_exploding_barrel | Point | csgo.fgd | probe_classes | exact |
| prop_magic_carpet | Point | base.fgd | probe_classes | exact |
| prop_physics | Point | base.fgd | ze_ffvii_mako_reactor_v6_p, probe_classes +3 | exact |
| prop_physics_multiplayer | Point | base.fgd | ze_ffvii_mako_reactor_v6_p, probe_classes +2 | exact |
| prop_ragdoll | Point | base.fgd | probe_classes | exact |
| radar_element | Point | csgo.fgd | probe_classes | exact |
| sky_camera_volume | Point | csgo.fgd | probe_classes | exact |
| sky_camera_volume_target | Point | csgo.fgd | probe_classes | exact |
| skybox_reference | Point | csgo.fgd | atixref, ze_ffvii_mako_reactor_v6_p +1 | exact |
| snd_event_alignedbox | Point | base.fgd | probe_classes | exact |
| snd_event_cone | Point | base.fgd | probe_classes | exact |
| snd_event_oriented_boxes | Point | base.fgd | probe_classes | exact |
| snd_event_orientedbox | Point | base.fgd | probe_classes | exact |
| snd_event_param | Point | base.fgd | ze_ffvii_mako_reactor_v6_p, probe_classes | exact |
| snd_event_path_corner | Point | base.fgd | probe_classes | exact |
| snd_event_point | Point | base.fgd | probe_classes | exact |
| snd_event_sphere | Point | base.fgd | probe_classes | exact |
| snd_opvar_set | Point | base.fgd | probe_classes | exact |
| snd_opvar_set_aabb | Point | base.fgd | probe_classes | exact |
| snd_opvar_set_auto_room | Point | base.fgd | probe_classes | exact |
| snd_opvar_set_dome | Point | base.fgd | probe_classes | exact |
| snd_opvar_set_obb | Point | base.fgd | probe_classes | exact |
| snd_opvar_set_path_corner | Point | base.fgd | probe_classes | exact |
| snd_opvar_set_wind_obb | Point | base.fgd | probe_classes | exact |
| snd_sound_area_obb | Point | base.fgd | probe_classes | exact |
| snd_sound_area_sphere | Point | base.fgd | probe_classes | exact |
| snd_soundscape | Point | base.fgd | probe_classes | exact |
| snd_soundscape_proxy | Point | base.fgd | probe_classes | exact |
| snd_soundscape_triggerable | Point | base.fgd | probe_classes | exact |
| snd_stack_save | Point | base.fgd | probe_classes | exact |
| tanktrain_ai | Point | base.fgd | probe_classes | exact |
| tanktrain_aitarget | Point | base.fgd | probe_classes | exact |
| team_select | Point | csgo.fgd | atixref, cardtest +13 | exact |
| terrorist_rush_intro | Point | csgo.fgd | probe_classes | exact |
| terrorist_team_intro | Point | csgo.fgd | atixref, cardtest +12 | exact |
| terrorist_team_intro_variant2 | Point | csgo.fgd | probe_classes | exact |
| terrorist_wingman_intro | Point | csgo.fgd | probe_classes | exact |
| test_traceline | Point | base.fgd | probe_classes | exact |
| texture_based_animatable | Point | base.fgd | probe_classes | exact |
| trigger_autosave | Solid | base.fgd | probe_classes | exact |
| trigger_bomb_reset | Solid | csgo.fgd | probe_classes | exact |
| trigger_changelevel | Solid | base.fgd | probe_classes | exact |
| trigger_gravity | Solid | base.fgd | probe_classes | exact |
| trigger_hostage_reset | Solid | csgo.fgd | probe_classes | exact |
| trigger_hurt | Solid | base.fgd | atixref, ze_doom_p2_c_gameplay +6 | exact |
| trigger_impact | Solid | base.fgd | probe_classes | exact |
| trigger_look | Solid | base.fgd | probe_classes | exact |
| trigger_multiple | Solid | base.fgd | atixref, ze_doom_p2_c_gameplay +7 | exact |
| trigger_once | Solid | base.fgd | atixref, ze_doom_p2_c_gameplay +6 | exact |
| trigger_proximity | Solid | base.fgd | probe_classes | exact |
| trigger_push | Solid | base.fgd | atixref, ze_doom_p2_c_gameplay +2 | exact |
| trigger_remove | Solid | base.fgd | probe_classes | exact |
| trigger_serverragdoll | Solid | base.fgd | probe_classes | exact |
| trigger_snd_sos_opvar | Solid | base.fgd | probe_classes | exact |
| trigger_soundscape | Solid | base.fgd | atixref, probe_classes | exact |
| trigger_survival_playarea | Solid | csgo.fgd | probe_classes | exact |
| trigger_teleport | Solid | base.fgd | atixref, ze_doom_p2_c_gameplay +7 | exact |
| trigger_transition | Solid | base.fgd | probe_classes | exact |
| trigger_wind | Solid | base.fgd | probe_classes | exact |
| vgui_movie_display | Point | base.fgd | probe_classes | exact |
| vgui_slideshow_display | Point | base.fgd | probe_classes | exact |
| water_lod_control | Point | base.fgd | probe_classes | exact |
| weapon_ak47 | Point | csgo.fgd | probe_classes | exact |
| weapon_aug | Point | csgo.fgd | probe_classes | exact |
| weapon_awp | Point | csgo.fgd | probe_classes | exact |
| weapon_bizon | Point | csgo.fgd | probe_classes | exact |
| weapon_c4 | Point | csgo.fgd | probe_classes | exact |
| weapon_cz75a | Point | csgo.fgd | probe_classes | exact |
| weapon_deagle | Point | csgo.fgd | probe_classes | exact |
| weapon_decoy | Point | csgo.fgd | probe_classes | exact |
| weapon_elite | Point | csgo.fgd | ze_doom_p2_c_gameplay, ze_ffvii_mako_reactor_v6_p +1 | exact |
| weapon_famas | Point | csgo.fgd | probe_classes | exact |
| weapon_fiveseven | Point | csgo.fgd | probe_classes | exact |
| weapon_flashbang | Point | csgo.fgd | probe_classes | exact |
| weapon_g3sg1 | Point | csgo.fgd | probe_classes | exact |
| weapon_galilar | Point | csgo.fgd | probe_classes | exact |
| weapon_glock | Point | csgo.fgd | probe_classes | exact |
| weapon_healthshot | Point | csgo.fgd | probe_classes | exact |
| weapon_hegrenade | Point | csgo.fgd | probe_classes | exact |
| weapon_hkp2000 | Point | csgo.fgd | probe_classes | exact |
| weapon_incgrenade | Point | csgo.fgd | probe_classes | exact |
| weapon_knife | Point | csgo.fgd | ze_ffvii_mako_reactor_v6_p, probe_classes | exact |
| weapon_m249 | Point | csgo.fgd | probe_classes | exact |
| weapon_m4a1 | Point | csgo.fgd | probe_classes | exact |
| weapon_m4a1_silencer | Point | csgo.fgd | probe_classes | exact |
| weapon_mac10 | Point | csgo.fgd | probe_classes | exact |
| weapon_mag7 | Point | csgo.fgd | probe_classes | exact |
| weapon_molotov | Point | csgo.fgd | probe_classes | exact |
| weapon_mp5sd | Point | csgo.fgd | probe_classes | exact |
| weapon_mp7 | Point | csgo.fgd | probe_classes | exact |
| weapon_mp9 | Point | csgo.fgd | probe_classes | exact |
| weapon_negev | Point | csgo.fgd | probe_classes | exact |
| weapon_nova | Point | csgo.fgd | probe_classes | exact |
| weapon_p250 | Point | csgo.fgd | probe_classes | exact |
| weapon_p90 | Point | csgo.fgd | probe_classes | exact |
| weapon_revolver | Point | csgo.fgd | probe_classes | exact |
| weapon_sawedoff | Point | csgo.fgd | probe_classes | exact |
| weapon_scar20 | Point | csgo.fgd | probe_classes | exact |
| weapon_sg556 | Point | csgo.fgd | probe_classes | exact |
| weapon_smokegrenade | Point | csgo.fgd | probe_classes | exact |
| weapon_ssg08 | Point | csgo.fgd | probe_classes | exact |
| weapon_tagrenade | Point | csgo.fgd | probe_classes | exact |
| weapon_taser | Point | csgo.fgd | probe_classes | exact |
| weapon_tec9 | Point | csgo.fgd | probe_classes | exact |
| weapon_ump45 | Point | csgo.fgd | probe_classes | exact |
| weapon_usp_silencer | Point | csgo.fgd | probe_classes | exact |
| weapon_xm1014 | Point | csgo.fgd | probe_classes | exact |

### Declared but not offered

| class | fgd | why | what the compile does | port | local maps |
|---|---|---|---|---|---|
| addoninfo | workshop_addoninfo.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ai_attached_item_manager | ai_defaultnpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ai_battle_line | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ai_changehintgroup | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ai_goal_actbusy | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ai_goal_actbusy_queue | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ai_goal_follow | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ai_goal_injured_follow | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ai_goal_lead | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ai_goal_operator | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ai_point_of_interest | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ai_reaction | ai_defaultnpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ai_relationship | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ai_script_conditions | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ai_scripted_abilityusage | ai_defaultnpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ai_scripted_idle | ai_defaultnpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ai_scripted_moveto | ai_defaultnpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ai_speechfilter | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ai_volumetric_event | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ai_volumetric_event_sensor | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| beam_spotlight | base.fgd | excluded by @exclude |  |  | c2m2_fairgrounds_csgo_gameplay |
| color_correction | base.fgd | excluded by @exclude |  |  |  |
| color_correction_volume | base.fgd | excluded by @exclude |  |  |  |
| env_beam | base.fgd | excluded by @exclude |  |  |  |
| env_beverage | base.fgd | excluded by @exclude |  |  |  |
| env_cubemap | lights.fgd | excluded by @exclude | cubemap record for the bake; cubemaptexture (customcubemaptexture honoured) and array_index added; kept | baked keys not ported (cubemaps) |  |
| env_instructor_vr_hint | base.fgd | excluded by @exclude |  |  |  |
| env_laser | base.fgd | excluded by @exclude |  |  |  |
| env_rotorshooter | base.fgd | excluded by @exclude |  |  |  |
| env_rotorwash_emitter | base.fgd | excluded by @exclude |  |  |  |
| env_smokestack | base.fgd | excluded by @exclude |  |  |  |
| env_smoketrail | base.fgd | excluded by @exclude |  |  |  |
| env_splash | base.fgd | excluded by @exclude |  |  |  |
| env_sprite | base.fgd | excluded by @exclude |  |  |  |
| env_sprite_oriented | base.fgd | excluded by @exclude |  |  | ze_doom_p2_c_gameplay |
| env_texturetoggle | base.fgd | excluded by @exclude |  |  | ze_doom_p2_c_gameplay |
| env_tonemap_controller | base.fgd | excluded by @exclude |  |  |  |
| env_volumetric_fog_controller | base.fgd | excluded by @exclude | fog record for the bake; IndirectVoxelDim* and fogirradiancevolume keys added; kept | not ported (lighting) |  |
| env_volumetric_fog_volume | base.fgd | excluded by @exclude | fog indirect bake target | not ported (lighting) |  |
| func_detail_blocker | base.fgd | excluded by @exclude |  |  |  |
| func_fish_pool | base.fgd | excluded by @exclude |  |  |  |
| func_illusionary | base.fgd | excluded by @exclude |  |  |  |
| func_instance | base.fgd | excluded by @exclude |  |  |  |
| func_orator | base.fgd | excluded by @exclude |  |  |  |
| func_precipitation | base.fgd | excluded by @exclude |  |  |  |
| func_precipitation_blocker | base.fgd | excluded by @exclude |  |  |  |
| func_reflective_glass | base.fgd | excluded by @exclude |  |  |  |
| func_shatterglass | base.fgd | excluded by @exclude | its generated model's physics gets a model flag | mesh override taken in entity physics; the model flag not ported |  |
| func_wall | base.fgd | excluded by @exclude |  |  |  |
| func_wall_toggle | base.fgd | excluded by @exclude |  |  |  |
| generic_actor | ai_defaultnpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| ghost_speaker | ai_defaultnpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| haptic_relay | base.fgd | excluded by @exclude |  |  |  |
| info_hint | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| info_ladder_dismount | base.fgd | excluded by @exclude |  |  |  |
| info_landmark | base.fgd | excluded by @exclude | kept; a landmark record added to a world list | lump ported; where the list is written not read |  |
| info_lighting | base.fgd | excluded by @exclude |  |  |  |
| info_node | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain | nodeid key assigned by editor hooks | whether the compile runs the hooks is not read |  |
| info_node_air | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain | nav: air-nav seed when info_nav_space exists | nav not started |  |
| info_node_air_hint | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| info_node_climb | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| info_node_hint | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain | nav: air-nav hint when info_nav_space exists | nav not started |  |
| info_node_link_controller | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| info_npc_spawn_destination | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| info_radial_link_controller | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| info_world_layer | base.fgd | excluded by @exclude | layername becomes world_layer_<name>; worldname set; kept | ported | ze_ffvii_mako_reactor_v6_p |
| light_dynamic | base.fgd | excluded by @exclude |  |  |  |
| logic_choreographed_scene | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| logic_scene_list_manager | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| npc_bullseye | ai_defaultnpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| npc_enemyfinder | ai_defaultnpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| npc_furniture | ai_defaultnpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| npc_maker | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| npc_puppet | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| npc_template_maker | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| npc_vehicledriver | ai_basenpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| path_particle_rope | base.fgd | excluded by @exclude | upgrade of older vmaps only | lump ported; vmap upgrader not ported | c2m2_fairgrounds_csgo_environment_prefab |
| postprocess_controller | base.fgd | excluded by @exclude |  |  |  |
| scripted_sequence | ai_defaultnpc.fgd | its FGD is not in csgo.fgd's @include chain | upgrade of older vmaps only: spawnflags and m_fMoveTo to keys | lump ported; vmap upgrader not ported |  |
| scripted_target | ai_defaultnpc.fgd | its FGD is not in csgo.fgd's @include chain |  |  |  |
| trigger_tonemap | base.fgd | excluded by @exclude |  |  |  |

<!-- end generated -->
### What the class list does not show

- The lump export drops three more classes the CS2 FGDs do not declare:
  `env_world_lighting`, `light_irradvolume` and `func_deformable_density`. The
  port drops them too.
- Classes are found by their name strings only. A class the compile picks out
  some other way, by an FGD flag (`static_prop`, `editor_only`) or through a
  base class, is not in the "handled" list. The port reads those flags from the
  FGD as the export does.
- Loading an older vmap runs Valve's upgrade table first. The steps rename keys,
  rewrite values and convert classes: old lights to `light_barn`/`light_omni2`,
  spawnflags to gravity-grab keys, `.vmat`/`.vsnap` extensions, path and cable
  classnames. The port has only the worldspawn step. The table runs only for vmaps older
  than the current format, so this matters for maps last saved by an old Hammer.

### Still to read

- Where the compile writes its landmark list (`info_landmark`), its camera list
  (`point_camera`) and its triangle-cull records (`info_cull_triangles`).
- Who reads `prop_static`'s vis-occluder flag, and the cable's child-geometry
  flag.
- Whether the compile runs the editor hooks that give `info_node` its `nodeid`.

### What the probe settled

The first probe compile found rules no local map had exercised. Each is now
read from resourcecompiler and ported:
- A flags key the entity lacks defaults to the OR of its default-on choices;
  a tag_list key to its default-on tags joined with ",".
- A flags key declared again (trigger_look over its trigger base) keeps the
  choices it inherited, after its own.
- spawnflags keeps only the bits its class declares.
- The prefab name fixup applies by key type: target_destination,
  target_name_or_class, npcclass, filterclass and pointentityclass, plus the
  entity's own targetname. Values starting with `!*?@` and values naming an
  FGD class are left alone.
- A key spelled `name` never gets a default: every DMX element already has a
  name attribute, and the default fill finds it.
- snapshot_mesh naming a map node sets snapshot_file to the particle snapshot
  the compile generates from it. The key is ported. The .vsnap file itself is
  not, and neither is a snapshot node inside an instance (the port refuses it).

### Next

- The `compile-map` command, routing Hammer's switches to the parts above.
- Particle snapshot files (.vsnap) from mesh nodes.
