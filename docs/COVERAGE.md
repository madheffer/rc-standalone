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
- `tools/coverage/coverage_doc.py`: writes the entity half below from the above
  and `tools/coverage/class_notes.json`.

## Compile options

The port has no single map compile command yet. Each part runs on its own
(`s2c map-physics`, the entity lump set, the vis pipeline) and always behaves as
Hammer's default build does. The first thing the options need is a `compile-map`
command that takes Hammer's switches and routes each to the part it changes.

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
| `-world` | Build world | partial: world_physics whole-file exact on 9 maps; render geometry and world nodes not started |
| `-entities` | Entities Only | partial: every lump exact on the pinned maps except lights, probe volumes, cubemaps and one cable key |
| `-nosettle` | Pre-Settle physics objects (unticked) | default only: the port always settles (exact on 2 maps) |
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
| `-nolightmaps` | no label found | not started |
| `-vis` | Build vis | partial: every stage exact against captured inputs, Mako included; not yet end to end on our own geometry |
| `-phys` | Build physics | partial: as the physics half of `-world` |
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
| `world`, `entities`, `all` | as `-world` / `-entities` above |
| `skipmapload` | not read |
| `nosettle` | default only |
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
| `-i`, `-filelist`, `-r`, `-game`, `-outroot` | input and output paths; the port takes its own |
| `-f`, `-fshallow`, `-fshallow2` | force recompile; no output? |
| `-novpk`, `-vpkincr` | not read (loose files against a map package; the port writes both) |
| `-skiptype` | not read |
| `-nop4`, `-changelist`, `-norevert` | source control; no output? |
| `-v`, `-pause`, `-pauseiferror`, `-html`, `-logwarnings`, `-breakpad`, `-telemetry_level` | no output? |
| `-dependency_check_only`, `-crc` | not read |
| `-allowdebug` | not read |
| `-vulkan` (and its validation switches) | renders on Vulkan; the material capture showed the same bytes under `-vulkan` |
| `-vis_preview` | not read |

<!-- generated by tools/coverage/coverage_doc.py; edit the script or class_notes.json, not this part -->
## Entity classes

435 placeable classes across core.fgd, csgo_core.fgd and csgo.fgd (Source 1 import scripts left out).
43 of them are named by a compile DLL, so the compile does something with them beyond copying
their keys into the entity lump. The other 392 only pass through the lump writer, which is driven by
the FGD's key types for every class alike.

Status:
- **exact**: present in a map whose every lump is compared against Valve's compile, with no allowed difference.
- **partial**: present in such a map, and some of its keys still differ (reason given).
- **unpinned**: present in a local map, but none with a pinned compile.
- **no sample**: no local map holds it; a probe map is needed.

### Classes the compile handles

exact 15, partial 9, unpinned 2, no sample 17

| class | kind | named by | what the compile does | port | status |
|---|---|---|---|---|---|
| cable_dynamic | Cable | resourcecompiler | lump: rendercolor written as the text "%i %i %i"; secondary_material added to the material list | path keys ported; rendercolor text write not ported; secondary material not checked | partial (rendercolor ships untyped; the write is not read yet) |
| env_combined_light_probe_volume | Point | resourcecompiler | cubemap and probe records for the bake; cubemaptexture, array_index and lightprobetexture* keys added; kept | kept; baked keys not ported (probes, cubemaps) | partial (baked keys (atlas textures, handshake, size) not ported) |
| env_cubemap | Point | resourcecompiler | cubemap record for the bake; cubemaptexture (customcubemaptexture honoured) and array_index added; kept | baked keys not ported (cubemaps) | no sample |
| env_cubemap_box | Point | resourcecompiler, physicsbuilder, vrad3 | as env_cubemap with box bounds; upgrade of older vmaps only renames boxproject_* keys | baked keys not ported; vmap upgrader not ported | no sample |
| env_light_probe_volume | Point | resourcecompiler | probe record for the bake; lightprobetexture* keys added; kept | kept; baked keys not ported (probes) | partial (baked keys (atlas textures, handshake, size) not ported) |
| env_sky | Point | resourcecompiler, physicsbuilder, vrad3 | upgrade of older vmaps only: skyname gets .vmat | lump pass-through ported; vmap upgrader not ported | exact |
| env_volumetric_fog_controller | Point | resourcecompiler | fog record for the bake; IndirectVoxelDim* and fogirradiancevolume keys added; kept | not ported (lighting) | no sample |
| env_volumetric_fog_volume | Point | resourcecompiler | fog indirect bake target | not ported (lighting) | no sample |
| func_movelinear | Solid | resourcecompiler | nav: movable nav mesh when CreateMovableNavMesh is set | lump ported; nav not started | partial (Mako world-layer instance copies: angles 1 or 2 ulps off (parked)) |
| func_nav_markup | Solid | resourcecompiler | nav: markup volumes | lump ported; nav not started | no sample |
| func_physbox | Solid | resourcecompiler, physicsbuilder, vrad3 | upgrade of older vmaps only: spawnflags to gravity-grab keys | lump ported; vmap upgrader not ported | exact |
| func_shatterglass | Solid | resourcecompiler | its generated model's physics gets a model flag | mesh override taken in entity physics; the model flag not ported | no sample |
| info_cull_triangles | Point | resourcecompiler | dropped from the lump; a record for render-mesh triangle culling | dropped as Valve does; culling not ported (render geometry) | no sample |
| info_landmark | Point | resourcecompiler | kept; a landmark record added to a world list | lump ported; where the list is written not read | no sample |
| info_node | Point | resourcecompiler | nodeid key assigned by editor hooks | whether the compile runs the hooks is not read | no sample |
| info_node_air | Point | resourcecompiler | nav: air-nav seed when info_nav_space exists | nav not started | no sample |
| info_node_hint | Point | resourcecompiler | nav: air-nav hint when info_nav_space exists | nav not started | no sample |
| info_particle_system | Point | resourcecompiler, physicsbuilder, vrad3 | upgrade of older vmaps only: snapshot_file gets .vsnap | lump ported; vmap upgrader not ported | partial (Mako world-layer instance copies: angles 1 or 2 ulps off (parked)) |
| info_player_start | Point | resourcecompiler | Hammer's map check only | nothing to port for the compile | exact |
| info_world_layer | Point | resourcecompiler | layername becomes world_layer_<name>; worldname set; kept | ported | exact |
| light_barn | Point | resourcecompiler, physicsbuilder, vrad3 | light description for precompute, bake and vis membership; precomputed* keys, directlight, lightcookie resource; upgrade of older vmaps only converts light_spot/omni/ortho | precompute exact against an empty scene; lump keys, bake and vis membership not ported; vmap upgrader not ported | partial (light export (shape keys, matrix angles) and baked keys not ported) |
| light_environment | Point | resourcecompiler, physicsbuilder, vrad3 | legacy directional light for bake and shadows; lump through the light path; upgrade of older vmaps only | lump partial; bake not ported; vmap upgrader not ported | partial (light export (shape keys, matrix angles) and baked keys not ported) |
| light_omni2 | Point | resourcecompiler, physicsbuilder, vrad3 | as light_barn | as light_barn | partial (light export (shape keys, matrix angles) and baked keys not ported) |
| light_rect | Point | resourcecompiler | as light_barn (no upgrader) | as light_barn | partial (light export (shape keys, matrix angles) and baked keys not ported) |
| path_corner | Point | resourcecompiler | nav: air-nav hint when info_nav_space exists | lump ported; nav not started | no sample |
| path_generic | Path | resourcecompiler, physicsbuilder, vrad3 | upgrade of older vmaps only: classnames on paths, nodes and cables | lump ported; vmap upgrader not ported | no sample |
| path_node_cable | PathNode | resourcecompiler, physicsbuilder, vrad3 | upgrade of older vmaps only | lump ported; vmap upgrader not ported | exact |
| path_node_generic | PathNode | resourcecompiler, physicsbuilder, vrad3 | the owning path's node keys (pathNodes, names, colours, radius scales, closed_loop) | ported | unpinned |
| path_node_particle_rope | PathNode | resourcecompiler, physicsbuilder, vrad3 | upgrade of older vmaps only | lump ported; vmap upgrader not ported | exact |
| path_particle_rope | Path | resourcecompiler, physicsbuilder, vrad3 | upgrade of older vmaps only | lump ported; vmap upgrader not ported | unpinned |
| path_track | Point | resourcecompiler | editor only (clone chaining) | nothing to port for the compile | exact |
| point_camera | Point | resourcecompiler | kept; a camera record added to a world list | lump ported; where the list is written not read | exact |
| point_nav_walkable | Point | resourcecompiler | nav: walkable seed | lump ported; nav not started | exact |
| point_scale_reference_human | Point | resourcecompiler | dropped from the lump | ported | exact |
| point_template | Point | resourcecompiler | entities it names are left out of the settle | ported (settle) | exact |
| prop_physics_override | Point | resourcecompiler, physicsbuilder, vrad3 | upgrade of older vmaps only: spawnflags to gravity-grab keys | lump and settle ported; vmap upgrader not ported | exact |
| prop_static | Point | resourcecompiler, physicsbuilder | dropped from the lump; static prop record for render, lighting and world physics | dropped as Valve does; world physics hulls exact; render and lighting not started | exact |
| scripted_sequence | Point | resourcecompiler, physicsbuilder, vrad3 | upgrade of older vmaps only: spawnflags and m_fMoveTo to keys | lump ported; vmap upgrader not ported | no sample |
| sky_camera | Point | resourcecompiler | none found at compile time | nothing found to port | no sample |
| snd_event_box_helper | Point | resourcecompiler | editor only (clone chaining) | nothing to port for the compile | no sample |
| snd_opvar_set_point | Point | resourcecompiler | Steam Audio probe positions | not started (Steam Audio) | no sample |
| visibility_hint | Point | resourcecompiler | dropped from the lump; the entity goes into the vis build's hints | dropped as Valve does; vis hints not wired | exact |
| worldspawn | Solid | resourcecompiler, physicsbuilder, vrad3, worldrenderer | kept; pvstype and the lightmap/vis settings (max_lightmap_resolution, novis_*, lightmap_queries) read into the build | lump ported; pvstype read by the vis port; lightmap settings not started | exact |

### Classes that only pass through the lump

exact 89, partial 1, unpinned 3, no sample 299

| class | kind | fgd | local maps | status |
|---|---|---|---|---|
| addoninfo | Point | workshop_addoninfo.fgd |  | no sample |
| ai_attached_item_manager | Point | ai_defaultnpc.fgd |  | no sample |
| ai_battle_line | Point | ai_basenpc.fgd |  | no sample |
| ai_changehintgroup | Point | ai_basenpc.fgd |  | no sample |
| ai_goal_actbusy | Point | ai_basenpc.fgd |  | no sample |
| ai_goal_actbusy_queue | Point | ai_basenpc.fgd |  | no sample |
| ai_goal_follow | Point | ai_basenpc.fgd |  | no sample |
| ai_goal_injured_follow | Point | ai_basenpc.fgd |  | no sample |
| ai_goal_lead | Point | ai_basenpc.fgd |  | no sample |
| ai_goal_operator | Point | ai_basenpc.fgd |  | no sample |
| ai_point_of_interest | Point | ai_basenpc.fgd |  | no sample |
| ai_reaction | Point | ai_defaultnpc.fgd |  | no sample |
| ai_relationship | Point | ai_basenpc.fgd |  | no sample |
| ai_script_conditions | Point | ai_basenpc.fgd |  | no sample |
| ai_scripted_abilityusage | Point | ai_defaultnpc.fgd |  | no sample |
| ai_scripted_idle | Point | ai_defaultnpc.fgd |  | no sample |
| ai_scripted_moveto | Point | ai_defaultnpc.fgd |  | no sample |
| ai_speechfilter | Point | ai_basenpc.fgd |  | no sample |
| ai_volumetric_event | Point | ai_basenpc.fgd |  | no sample |
| ai_volumetric_event_sensor | Point | ai_basenpc.fgd |  | no sample |
| ambient_generic | Point | base.fgd | ze_hold_em_p +2 | exact |
| beam_spotlight | Point | base.fgd | c2m2_fairgrounds_csgo_gameplay | exact |
| cable_static | Cable | base.fgd |  | no sample |
| chicken | Point | csgo.fgd |  | no sample |
| color_correction | Point | base.fgd |  | no sample |
| color_correction_volume | Solid | base.fgd |  | no sample |
| commentary_auto | Point | base.fgd |  | no sample |
| counterterrorist_rush_intro | Point | csgo.fgd |  | no sample |
| counterterrorist_team_intro | Point | csgo.fgd | atixref, cardtest +11 | exact |
| counterterrorist_team_intro_variant2 | Point | csgo.fgd |  | no sample |
| counterterrorist_wingman_intro | Point | csgo.fgd |  | no sample |
| cs_minimap_boundary | Point | csgo.fgd | atixref, cardtest +12 | exact |
| cs_minimap_volume | Solid | csgo.fgd |  | no sample |
| csm_fov_override | Point | csgo.fgd |  | no sample |
| custom_hud_layout | Point | csgo.fgd | cs_script_demo | unpinned |
| dz_door | Point | csgo.fgd |  | no sample |
| end_of_match | Point | csgo.fgd | atixref, cardtest +12 | exact |
| env_beam | Point | base.fgd |  | no sample |
| env_beverage | Point | base.fgd |  | no sample |
| env_blood | Point | base.fgd |  | no sample |
| env_credits | Point | base.fgd |  | no sample |
| env_cs_place | Solid | csgo.fgd |  | no sample |
| env_cubemap_fog | Point | csgo.fgd | atixref, cardtest +8 | exact |
| env_decal | Point | base.fgd |  | no sample |
| env_entity_igniter | Point | base.fgd | ze_ffvii_mako_reactor_v6_p | exact |
| env_entity_maker | Point | base.fgd | ze_doom_p2_c_gameplay, ze_ffvii_mako_reactor_v6_p | exact |
| env_explosion | Point | base.fgd | ze_doom_p2_c_gameplay, ze_ffvii_mako_reactor_v6_p | exact |
| env_fade | Point | base.fgd | atixref, c2m2_fairgrounds_csgo_gameplay +5 | exact |
| env_fire | Point | base.fgd | ze_ffvii_mako_reactor_v6_p | exact |
| env_firesensor | Point | base.fgd |  | no sample |
| env_firesource | Point | base.fgd |  | no sample |
| env_fog_controller | Point | base.fgd |  | no sample |
| env_gradient_fog | Point | csgo.fgd | ze_ffvii_mako_reactor_v6_p | exact |
| env_hudhint | Point | csgo.fgd | ze_ffvii_mako_reactor_v6_p | exact |
| env_instructor_hint | Point | base.fgd |  | no sample |
| env_instructor_vr_hint | Point | base.fgd |  | no sample |
| env_laser | Point | base.fgd |  | no sample |
| env_message | Point | base.fgd |  | no sample |
| env_particle_glow | Point | csgo.fgd | atixref, ze_ffvii_mako_reactor_v6_p +3 | exact |
| env_particlescript | Point | base.fgd |  | no sample |
| env_physexplosion | Point | base.fgd | atixref | exact |
| env_physimpact | Point | base.fgd |  | no sample |
| env_player_visibility | Point | csgo.fgd |  | no sample |
| env_rotorshooter | Point | base.fgd |  | no sample |
| env_rotorwash_emitter | Point | base.fgd |  | no sample |
| env_shake | Point | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p +4 | exact |
| env_shake_volume | Point | csgo.fgd |  | no sample |
| env_shooter | Point | base.fgd |  | no sample |
| env_smokestack | Point | base.fgd |  | no sample |
| env_smoketrail | Point | base.fgd |  | no sample |
| env_soundscape | Point | base.fgd | atixref, cardtest +7 | exact |
| env_soundscape_proxy | Point | base.fgd |  | no sample |
| env_soundscape_triggerable | Point | base.fgd | atixref | exact |
| env_spark | Point | base.fgd | atixref, c2m2_fairgrounds_csgo_gameplay +4 | exact |
| env_splash | Point | base.fgd |  | no sample |
| env_sprite | Point | base.fgd |  | no sample |
| env_sprite_oriented | Point | base.fgd | ze_doom_p2_c_gameplay | exact |
| env_texturetoggle | Point | base.fgd | ze_doom_p2_c_gameplay | exact |
| env_tilt | Point | base.fgd |  | no sample |
| env_tonemap_controller | Point | base.fgd |  | no sample |
| env_viewpunch | Point | base.fgd |  | no sample |
| env_wind | Point | base.fgd | atixref | exact |
| filter_activator_attribute_int | Filter | base.fgd |  | no sample |
| filter_activator_class | Filter | base.fgd | cs_script_demo | unpinned |
| filter_activator_context | Filter | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p | exact |
| filter_activator_mass_greater | Filter | base.fgd |  | no sample |
| filter_activator_model | Filter | base.fgd | c2m2_fairgrounds_csgo_gameplay +1 | exact |
| filter_activator_name | Filter | base.fgd | ze_doom_p2_c_gameplay | exact |
| filter_activator_team | Filter | csgo.fgd | atixref, ze_doom_p2_c_gameplay +5 | exact |
| filter_damage_type | Filter | base.fgd | atixref, ze_doom_p2_c_gameplay +1 | exact |
| filter_enemy | Filter | base.fgd |  | no sample |
| filter_los | Filter | base.fgd |  | no sample |
| filter_modifier | Filter | base.fgd |  | no sample |
| filter_multi | Filter | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p | exact |
| filter_proximity | Filter | base.fgd |  | no sample |
| flashbang_projectile | Point | csgo.fgd |  | no sample |
| fog_volume | Solid | csgo.fgd |  | no sample |
| func_bomb_target | Solid | csgo.fgd | cardtest, probe01 +6 | exact |
| func_breakable | Solid | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p +7 | exact |
| func_brush | Solid | base.fgd | atixref, cardtest +5 | exact |
| func_button | Solid | base.fgd | atixref, c2m2_fairgrounds_csgo_gameplay +4 | exact |
| func_buyzone | Solid | csgo.fgd | atixref, cardtest +13 | exact |
| func_clip_interaction_layer | Solid | base.fgd |  | no sample |
| func_clip_vphysics | Solid | csgo.fgd | ze_ffvii_mako_reactor_v6_p | exact |
| func_conveyor | Solid | csgo.fgd |  | no sample |
| func_detail_blocker | Solid | base.fgd |  | no sample |
| func_door | Solid | base.fgd | atixref, c2m2_fairgrounds_csgo_gameplay +6 | exact |
| func_door_rotating | Solid | base.fgd | c2m2_fairgrounds_csgo_gameplay, ze_ffvii_mako_reactor_v6_p | exact |
| func_fish_pool | Point | base.fgd |  | no sample |
| func_guntarget | Solid | base.fgd |  | no sample |
| func_hostage_rescue | Solid | csgo.fgd |  | no sample |
| func_illusionary | Solid | base.fgd |  | no sample |
| func_instance | Point | base.fgd |  | no sample |
| func_nav_blocker | Solid | csgo.fgd |  | no sample |
| func_nav_gen_proj | Solid | markup_volumes.fgd |  | no sample |
| func_orator | Point | base.fgd |  | no sample |
| func_physical_button | Solid | base.fgd |  | no sample |
| func_platrot | Solid | base.fgd |  | no sample |
| func_precipitation | Solid | base.fgd |  | no sample |
| func_precipitation_blocker | Solid | base.fgd |  | no sample |
| func_reflective_glass | Solid | base.fgd |  | no sample |
| func_rot_button | Solid | base.fgd | c2m2_fairgrounds_csgo_gameplay | exact |
| func_rotating | Solid | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p +3 | exact |
| func_survival_c4_target | Point | csgo.fgd |  | no sample |
| func_tablet_blocker | Solid | csgo.fgd |  | no sample |
| func_tanktrain | Solid | base.fgd | ze_doom_p2_c_gameplay | exact |
| func_timescale | Point | base.fgd |  | no sample |
| func_trackautochange | Solid | base.fgd |  | no sample |
| func_trackchange | Solid | base.fgd |  | no sample |
| func_tracktrain | Solid | base.fgd | atixref, c2m2_fairgrounds_csgo_gameplay +5 | exact |
| func_traincontrols | Solid | base.fgd |  | no sample |
| func_useableladder | Point | base.fgd |  | no sample |
| func_wall | Solid | base.fgd |  | no sample |
| func_wall_toggle | Solid | base.fgd |  | no sample |
| func_water | Solid | csgo.fgd | atixref, ze_ffvii_mako_reactor_v6_p +2 | exact |
| game_end | Point | csgo.fgd |  | no sample |
| game_gib_manager | Point | base.fgd |  | no sample |
| game_money | Point | csgo.fgd |  | no sample |
| game_player_equip | Point | base.fgd |  | no sample |
| game_ragdoll_manager | Point | base.fgd |  | no sample |
| game_text | Point | base.fgd | ze_doom_p2_c_gameplay | exact |
| game_weapon_manager | Point | base.fgd | ze_hold_em_p +2 | exact |
| game_zone_player | Solid | base.fgd | ze_doom_p2_c_gameplay, ze_ffvii_mako_reactor_v6_p | exact |
| generic_actor | Point | ai_defaultnpc.fgd |  | no sample |
| ghost_speaker | Point | ai_defaultnpc.fgd |  | no sample |
| gibshooter | Point | base.fgd |  | no sample |
| hammer_updateignorelist | Point | base.fgd |  | no sample |
| haptic_relay | Point | base.fgd |  | no sample |
| hostage_entity | Point | csgo.fgd |  | no sample |
| info_armsrace_counterterrorist | Point | csgo.fgd |  | no sample |
| info_armsrace_terrorist | Point | csgo.fgd |  | no sample |
| info_constraint_anchor | Point | base.fgd |  | no sample |
| info_deathmatch_spawn | Point | csgo.fgd |  | no sample |
| info_enemy_terrorist_spawn | Point | csgo.fgd |  | no sample |
| info_hint | Point | ai_basenpc.fgd |  | no sample |
| info_hostage_spawn | Point | csgo.fgd |  | no sample |
| info_intermission | Point | base.fgd |  | no sample |
| info_ladder_dismount | Point | base.fgd |  | no sample |
| info_lighting | Point | base.fgd |  | no sample |
| info_map_parameters | Point | csgo.fgd | cardtest, probe01 +6 | exact |
| info_map_region | Point | csgo.fgd |  | no sample |
| info_node_air_hint | Point | ai_basenpc.fgd |  | no sample |
| info_node_climb | Point | ai_basenpc.fgd |  | no sample |
| info_node_link_controller | Point | ai_basenpc.fgd |  | no sample |
| info_npc_spawn_destination | Point | ai_basenpc.fgd |  | no sample |
| info_null | Point | base.fgd |  | no sample |
| info_offscreen_panorama_texture | Point | csgo.fgd |  | no sample |
| info_paradrop_denial | Point | csgo.fgd |  | no sample |
| info_particle_target | Point | base.fgd | ze_ffvii_mako_reactor_v6_p +2 | exact |
| info_player_counterterrorist | Point | csgo.fgd | atixref, cardtest +18 | exact |
| info_player_terrorist | Point | csgo.fgd | atixref, cardtest +17 | exact |
| info_radial_link_controller | Point | ai_basenpc.fgd |  | no sample |
| info_spawngroup_landmark | Point | base.fgd |  | no sample |
| info_spawngroup_load_unload | Point | base.fgd |  | no sample |
| info_target | Point | base.fgd | atixref, ze_doom_p2_c_gameplay +3 | exact |
| info_target_instructor_hint | Point | base.fgd |  | no sample |
| info_target_server_only | Point | base.fgd |  | no sample |
| info_teleport_destination | Point | base.fgd | atixref, ze_doom_p2_c_gameplay +6 | exact |
| info_visibility_box | Point | base.fgd |  | no sample |
| item_defuser | Point | csgo.fgd |  | no sample |
| keyframe_rope | KeyFrame | base.fgd |  | no sample |
| keyframe_track | KeyFrame | base.fgd |  | no sample |
| light_dynamic | Point | base.fgd |  | no sample |
| logic_active_autosave | Point | base.fgd |  | no sample |
| logic_activityevent | Point | base.fgd |  | no sample |
| logic_auto | Point | base.fgd | atixref, ze_doom_p2_c_gameplay +6 | exact |
| logic_autosave | Point | base.fgd |  | no sample |
| logic_branch | Point | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p | exact |
| logic_branch_listener | Point | base.fgd |  | no sample |
| logic_case | Point | base.fgd | atixref, ze_doom_p2_c_gameplay +2 | exact |
| logic_choreographed_scene | Point | ai_basenpc.fgd |  | no sample |
| logic_collision_pair | Point | base.fgd |  | no sample |
| logic_compare | Point | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p | exact |
| logic_eventlistener | Point | csgo.fgd | ze_ffvii_mako_reactor_v6_p | exact |
| logic_gamestate_report | Point | base.fgd |  | no sample |
| logic_lineto | Point | base.fgd |  | no sample |
| logic_measure_movement | Point | base.fgd | ze_ffvii_mako_reactor_v6_p | exact |
| logic_multicompare | Point | base.fgd |  | no sample |
| logic_navigation | Point | base.fgd |  | no sample |
| logic_npc_counter_aabb | Point | base.fgd |  | no sample |
| logic_npc_counter_obb | Point | base.fgd |  | no sample |
| logic_npc_counter_radius | Point | base.fgd |  | no sample |
| logic_playerproxy | Point | base.fgd |  | no sample |
| logic_relay | Point | base.fgd | atixref, ze_doom_p2_c_gameplay +2 | exact |
| logic_scene_list_manager | Point | ai_basenpc.fgd |  | no sample |
| logic_script | Point | base.fgd | ze_ffvii_mako_reactor_v6_p | exact |
| logic_timer | Point | base.fgd | atixref, ze_doom_p2_c_gameplay +6 | exact |
| map_preview_camera_path | Path | csgo.fgd |  | no sample |
| map_preview_camera_path_node | PathNode | csgo.fgd |  | no sample |
| markup_group | Solid | markup_volumes.fgd |  | no sample |
| markup_volume | Solid | markup_volumes.fgd |  | no sample |
| markup_volume_tagged | Solid | markup_volumes.fgd |  | no sample |
| markup_volume_with_ref | Solid | markup_volumes.fgd |  | no sample |
| math_colorblend | Point | base.fgd |  | no sample |
| math_counter | Point | base.fgd | atixref, ze_doom_p2_c_gameplay +5 | exact |
| math_remap | Point | base.fgd |  | no sample |
| momentary_rot_button | Solid | base.fgd | c2m2_fairgrounds_csgo_gameplay +1 | exact |
| move_rope | Move | base.fgd |  | no sample |
| move_track | Move | base.fgd |  | no sample |
| npc_bullseye | NPC | ai_defaultnpc.fgd |  | no sample |
| npc_enemyfinder | NPC | ai_defaultnpc.fgd |  | no sample |
| npc_furniture | Point | ai_defaultnpc.fgd |  | no sample |
| npc_maker | Point | ai_basenpc.fgd |  | no sample |
| npc_puppet | Point | ai_basenpc.fgd |  | no sample |
| npc_template_maker | Point | ai_basenpc.fgd |  | no sample |
| npc_vehicledriver | NPC | ai_basenpc.fgd |  | no sample |
| observable_element | Point | csgo.fgd |  | no sample |
| path_particle_rope_clientside | Path | base.fgd | atixref | exact |
| path_simple | Path | base.fgd |  | no sample |
| phys_ballsocket | Point | base.fgd | ze_ffvii_mako_reactor_v6_p | exact |
| phys_constraint | Point | base.fgd |  | no sample |
| phys_genericconstraint | Point | base.fgd |  | no sample |
| phys_hinge | Point | base.fgd |  | no sample |
| phys_hinge_local | Point | base.fgd |  | no sample |
| phys_keepupright | Point | base.fgd | ze_doom_p2_c_gameplay | exact |
| phys_lengthconstraint | Point | base.fgd |  | no sample |
| phys_magnet | Point | base.fgd |  | no sample |
| phys_motor | Point | base.fgd |  | no sample |
| phys_pulleyconstraint | Point | base.fgd |  | no sample |
| phys_ragdollconstraint | Point | base.fgd |  | no sample |
| phys_ragdollmagnet | Point | base.fgd |  | no sample |
| phys_slideconstraint | Point | base.fgd |  | no sample |
| phys_splineconstraint | Point | base.fgd |  | no sample |
| phys_spring | Point | base.fgd |  | no sample |
| phys_thruster | Point | base.fgd | ze_doom_p2_c_gameplay | exact |
| phys_torque | Point | base.fgd |  | no sample |
| point_anglesensor | Point | base.fgd |  | no sample |
| point_angularvelocitysensor | Point | base.fgd |  | no sample |
| point_broadcastclientcommand | Point | base.fgd | c2m2_fairgrounds_csgo_gameplay | exact |
| point_camera_vertical_fov | Point | base.fgd |  | no sample |
| point_clientcommand | Point | base.fgd | ze_ffvii_mako_reactor_v6_p | exact |
| point_clientui_dialog | Point | base.fgd |  | no sample |
| point_clientui_world_panel | Point | base.fgd |  | no sample |
| point_clientui_world_text_panel | Point | base.fgd |  | no sample |
| point_commentary_node | Point | base.fgd |  | no sample |
| point_deathcam_bounds | Point | csgo.fgd |  | no sample |
| point_devshot_camera | Point | base.fgd |  | no sample |
| point_dz_dronegun | Point | csgo.fgd |  | no sample |
| point_dz_weaponspawn | Point | csgo.fgd |  | no sample |
| point_dz_weaponspawn_group | Point | csgo.fgd |  | no sample |
| point_enable_motion_fixup | Point | base.fgd |  | no sample |
| point_entity_finder | Point | base.fgd |  | no sample |
| point_gamestats_counter | Point | base.fgd |  | no sample |
| point_hurt | Point | base.fgd | ze_ffvii_mako_reactor_v6_p | exact |
| point_instructor_event | Point | base.fgd |  | no sample |
| point_message | Point | base.fgd |  | no sample |
| point_orient | Point | base.fgd |  | no sample |
| point_proximity_sensor | Point | base.fgd |  | no sample |
| point_script | Point | csgo.fgd | cs_script_demo | unpinned |
| point_servercommand | Point | base.fgd | atixref, ze_doom_p2_c_gameplay +6 | exact |
| point_soundevent | Point | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p +5 | partial (Mako world-layer instance copies: angles 1 or 2 ulps off (parked)) |
| point_teleport | Point | base.fgd | atixref, ze_doom_p2_c_gameplay +3 | exact |
| point_value_remapper | Point | base.fgd |  | no sample |
| point_velocitysensor | Point | base.fgd |  | no sample |
| point_workplane | Point | base.fgd |  | no sample |
| point_worldtext | Point | base.fgd | cardtest, probe01 +8 | exact |
| post_processing_volume | Solid | postprocessing.fgd | atixref, cardtest +13 | exact |
| postprocess_controller | Point | base.fgd |  | no sample |
| prop_counter | Point | csgo.fgd |  | no sample |
| prop_door_rotating | Point | csgo.fgd | atixref, ze_ffvii_mako_reactor_v6_p +3 | exact |
| prop_dynamic | Point | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p +4 | exact |
| prop_dynamic_ornament | Point | base.fgd |  | no sample |
| prop_dynamic_override | Point | base.fgd | atixref, ze_ffvii_mako_reactor_v6_p +2 | exact |
| prop_exploding_barrel | Point | csgo.fgd |  | no sample |
| prop_magic_carpet | Point | base.fgd |  | no sample |
| prop_physics | Point | base.fgd | ze_ffvii_mako_reactor_v6_p +2 | exact |
| prop_physics_multiplayer | Point | base.fgd | ze_ffvii_mako_reactor_v6_p +1 | exact |
| prop_ragdoll | Point | base.fgd |  | no sample |
| radar_element | Point | csgo.fgd |  | no sample |
| scripted_target | Point | ai_defaultnpc.fgd |  | no sample |
| sky_camera_volume | Point | csgo.fgd |  | no sample |
| sky_camera_volume_target | Point | csgo.fgd |  | no sample |
| skybox_reference | Point | csgo.fgd | atixref, ze_ffvii_mako_reactor_v6_p | exact |
| snd_event_alignedbox | Point | base.fgd |  | no sample |
| snd_event_cone | Point | base.fgd |  | no sample |
| snd_event_oriented_boxes | Point | base.fgd |  | no sample |
| snd_event_orientedbox | Point | base.fgd |  | no sample |
| snd_event_param | Point | base.fgd | ze_ffvii_mako_reactor_v6_p | exact |
| snd_event_path_corner | Point | base.fgd |  | no sample |
| snd_event_point | Point | base.fgd |  | no sample |
| snd_event_sphere | Point | base.fgd |  | no sample |
| snd_opvar_set | Point | base.fgd |  | no sample |
| snd_opvar_set_aabb | Point | base.fgd |  | no sample |
| snd_opvar_set_auto_room | Point | base.fgd |  | no sample |
| snd_opvar_set_dome | Point | base.fgd |  | no sample |
| snd_opvar_set_obb | Point | base.fgd |  | no sample |
| snd_opvar_set_path_corner | Point | base.fgd |  | no sample |
| snd_opvar_set_wind_obb | Point | base.fgd |  | no sample |
| snd_sound_area_obb | Point | base.fgd |  | no sample |
| snd_sound_area_sphere | Point | base.fgd |  | no sample |
| snd_soundscape | Point | base.fgd |  | no sample |
| snd_soundscape_proxy | Point | base.fgd |  | no sample |
| snd_soundscape_triggerable | Point | base.fgd |  | no sample |
| snd_stack_save | Point | base.fgd |  | no sample |
| tanktrain_ai | Point | base.fgd |  | no sample |
| tanktrain_aitarget | Point | base.fgd |  | no sample |
| team_select | Point | csgo.fgd | atixref, cardtest +12 | exact |
| terrorist_rush_intro | Point | csgo.fgd |  | no sample |
| terrorist_team_intro | Point | csgo.fgd | atixref, cardtest +11 | exact |
| terrorist_team_intro_variant2 | Point | csgo.fgd |  | no sample |
| terrorist_wingman_intro | Point | csgo.fgd |  | no sample |
| test_traceline | Point | base.fgd |  | no sample |
| texture_based_animatable | Point | base.fgd |  | no sample |
| trigger_autosave | Solid | base.fgd |  | no sample |
| trigger_bomb_reset | Solid | csgo.fgd |  | no sample |
| trigger_changelevel | Solid | base.fgd |  | no sample |
| trigger_gravity | Solid | base.fgd |  | no sample |
| trigger_hostage_reset | Solid | csgo.fgd |  | no sample |
| trigger_hurt | Solid | base.fgd | atixref, ze_doom_p2_c_gameplay +5 | exact |
| trigger_impact | Solid | base.fgd |  | no sample |
| trigger_look | Solid | base.fgd |  | no sample |
| trigger_multiple | Solid | base.fgd | atixref, ze_doom_p2_c_gameplay +6 | exact |
| trigger_once | Solid | base.fgd | atixref, ze_doom_p2_c_gameplay +5 | exact |
| trigger_proximity | Solid | base.fgd |  | no sample |
| trigger_push | Solid | base.fgd | atixref, ze_doom_p2_c_gameplay +1 | exact |
| trigger_remove | Solid | base.fgd |  | no sample |
| trigger_serverragdoll | Solid | base.fgd |  | no sample |
| trigger_snd_sos_opvar | Solid | base.fgd |  | no sample |
| trigger_soundscape | Solid | base.fgd | atixref | exact |
| trigger_survival_playarea | Solid | csgo.fgd |  | no sample |
| trigger_teleport | Solid | base.fgd | atixref, ze_doom_p2_c_gameplay +6 | exact |
| trigger_tonemap | Solid | base.fgd |  | no sample |
| trigger_transition | Solid | base.fgd |  | no sample |
| trigger_wind | Solid | base.fgd |  | no sample |
| vgui_movie_display | Point | base.fgd |  | no sample |
| vgui_slideshow_display | Point | base.fgd |  | no sample |
| water_lod_control | Point | base.fgd |  | no sample |
| weapon_ak47 | Point | csgo.fgd |  | no sample |
| weapon_aug | Point | csgo.fgd |  | no sample |
| weapon_awp | Point | csgo.fgd |  | no sample |
| weapon_bizon | Point | csgo.fgd |  | no sample |
| weapon_c4 | Point | csgo.fgd |  | no sample |
| weapon_cz75a | Point | csgo.fgd |  | no sample |
| weapon_deagle | Point | csgo.fgd |  | no sample |
| weapon_decoy | Point | csgo.fgd |  | no sample |
| weapon_elite | Point | csgo.fgd | ze_doom_p2_c_gameplay, ze_ffvii_mako_reactor_v6_p | exact |
| weapon_famas | Point | csgo.fgd |  | no sample |
| weapon_fiveseven | Point | csgo.fgd |  | no sample |
| weapon_flashbang | Point | csgo.fgd |  | no sample |
| weapon_g3sg1 | Point | csgo.fgd |  | no sample |
| weapon_galilar | Point | csgo.fgd |  | no sample |
| weapon_glock | Point | csgo.fgd |  | no sample |
| weapon_healthshot | Point | csgo.fgd |  | no sample |
| weapon_hegrenade | Point | csgo.fgd |  | no sample |
| weapon_hkp2000 | Point | csgo.fgd |  | no sample |
| weapon_incgrenade | Point | csgo.fgd |  | no sample |
| weapon_knife | Point | csgo.fgd | ze_ffvii_mako_reactor_v6_p | exact |
| weapon_m249 | Point | csgo.fgd |  | no sample |
| weapon_m4a1 | Point | csgo.fgd |  | no sample |
| weapon_m4a1_silencer | Point | csgo.fgd |  | no sample |
| weapon_mac10 | Point | csgo.fgd |  | no sample |
| weapon_mag7 | Point | csgo.fgd |  | no sample |
| weapon_molotov | Point | csgo.fgd |  | no sample |
| weapon_mp5sd | Point | csgo.fgd |  | no sample |
| weapon_mp7 | Point | csgo.fgd |  | no sample |
| weapon_mp9 | Point | csgo.fgd |  | no sample |
| weapon_negev | Point | csgo.fgd |  | no sample |
| weapon_nova | Point | csgo.fgd |  | no sample |
| weapon_p250 | Point | csgo.fgd |  | no sample |
| weapon_p90 | Point | csgo.fgd |  | no sample |
| weapon_revolver | Point | csgo.fgd |  | no sample |
| weapon_sawedoff | Point | csgo.fgd |  | no sample |
| weapon_scar20 | Point | csgo.fgd |  | no sample |
| weapon_sg556 | Point | csgo.fgd |  | no sample |
| weapon_smokegrenade | Point | csgo.fgd |  | no sample |
| weapon_ssg08 | Point | csgo.fgd |  | no sample |
| weapon_tagrenade | Point | csgo.fgd |  | no sample |
| weapon_taser | Point | csgo.fgd |  | no sample |
| weapon_tec9 | Point | csgo.fgd |  | no sample |
| weapon_ump45 | Point | csgo.fgd |  | no sample |
| weapon_usp_silencer | Point | csgo.fgd |  | no sample |
| weapon_xm1014 | Point | csgo.fgd |  | no sample |

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

### Next

- A probe map holding one of every class that has no sample, compiled by
  resourcecompiler and pinned like the others. That turns the 316 "no sample"
  rows into measured ones.
- The `compile-map` command, routing Hammer's switches to the parts above.
