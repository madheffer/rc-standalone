# Nav: everything the compile can take and write

The goal is resourcecompiler's nav step reproduced 1:1 for every input the binaries can compile,
not only the inputs our test maps happen to hold. This file is the inventory: what the DLLs read,
what they write, and the state of each piece in the port. The maps on disk are references to check
against, never the source of a rule (memory "ground-truth-ledger", GROUND_TRUTH.md).

How the inventory was made (2026-10-28, re-run after a CS2 update):
- resourcecompiler 20260923 in Ghidra (the 1023 build's nav code is checked by its function bytes
  before a capture; `tools/re/patch_check.py`): the nav step 1800fc370 and each collector it calls,
  decompiled into `tools/re/out/nav_inventory/rc_*.c`.
- navsystem 20261015: generation 1800217b0 and the stage list 180022b60 (`navs_*.c`).
- Every string naming nav, ladder or link in resourcecompiler, hammer and navsystem 20261023
  (`strings_rc_hammer_navs.txt`), and the functions that use them (`rc_string_xrefs.txt`).
- The FGDs (core/markup_volumes.fgd, base.fgd, csgo.fgd) for what Hammer offers a mapper.

Older facts: [BAKED.md](BAKED.md) has no nav section; the nav ledger entries are GROUND_TRUTH 92 to
109 and 142 to 146; LIGHTING_ROADMAP L9, L10 and L22 to L24.

## The nav step (resourcecompiler 1800fc370)

In order:
1. Compile arguments `navdbg` and `navrecastdbg` (both default 0).
2. CNavGenInput constructed (1800fb1c0): bounds FLT_MAX / -FLT_MAX, +0x120 15.0.
3. Settings: gameinfo's NavSystem block (1818c8c60), then the map's CMapNavData (180fafae0) or
   "Nav settings not found, using default settings."
4. The collectors, in this order, each into CNavGenInput (table below).
5. The custom nav factory (1800fbe80: gameinfo CustomNavBuild ModuleName / Interface; CS2's is
   server.dll customnavsystem001) and the mesh (1818c5fc0).
6. The settle world's bodies (180fb6de0, 0x90-byte entries at ctx + 0x18). Each body with a
   non-zero +0x70 becomes a 0x78-byte movable mesh input (180fb6cf0).
7. navsystem vfn 0x58 (generation, with navdbg and navrecastdbg), then the custom nav vfn 0x60
   (CS2: the analysis and its custom data).
8. Writes `maps/<map>.nav` (1818d1320), `.navdbg` when navdbg is set (1818ca680),
   `.navspace` when the nav space builder has records (1818d1c60), `.navflowmap` when the flow
   map KV3 has entries (binary_bc, generic). A file that is not written is deleted (1800fc2c0).

### The collectors

| order | function | input | CNavGenInput | state |
|---|---|---|---|---|
| 1 | 180f21760 | point_nav_walkable origins, every instance copy | +0x78 seeds | ported (ledger 109) |
| 2 | 180f209f0 | func_nav_markup: each `navProperty_<tag>` string key, else a `navProperty_NavAttributes` array | +0x90 markups, tag table +0x30 | ported (ledger 142); a markup inside an instance or prefab (its instance paths, 180f88460) and a scaled markup mesh refuse |
| 3 | 180f1f830 | prop_deformable and block_base_nav_mesh entities as NONAV blocks | +0x90 | not ported |
| 4 | 180f21ac0 | info_gravity_shift with `CreateProjectedNavMesh`, then every info_nav_oriented* entity (180f21930) | +0xa8 oriented volumes | not ported |
| 5 | 180f203b0 | ai_nav_link_area nodes that are CMapNavLink objects: the node's position through each instance path, and that plus the link's vector at +0x185c | +0xc0 links (0x18 each: start, end) | not ported; IslandRemoval already takes links (NavIslandInput.Links) |
| 6 | 180fb5440 | the compile context's AVOID list (filled by 180f4aed0 from `nav_attribute_avoid`) as NavGen AVOID markups | +0x90 | not ported |
| 7 | 180fb15e0 | ladders: meshes with a `mapbuilder.ladder` material, then func_ladder children | +0x60 ladders | ported; a func_ladder in an instance or prefab refuses (below) |
| 8 | 180f20f00 | info_nav_space (`DetailedGenerationForSmallNpcs`, `CoarseGenerationForLargeNpcs`); with one, info_node_air seeds and path_corner, info_node_hint, info_cover_hint, ai_flyer_flanking_route_hint, info_combine_gunship_bomb_target hints | +0x100 / +0x108 nav space | not ported |
| 9 | 180f20e40 | whether any info_nav_oriented_mesh exists | +0x128 | ported (the pipeline's `oriented` flag) |
| 10 | 180f20c70 | info_nav_oriented_mesh_debug_gen_clip and _render_clip (and their `[PR#]` forms) | | not ported |

Ladders (7) in full: the root's children are walked (180fb2830), skipping func_ladder entities; a
mesh with a mapbuilder.ladder material gives its best face per placement (180fb21f0, 180fb1c10).
Then each func_ladder of the root's flat node array whose visibility (+0x370) is set, and each
sub-map's (180fc8c80, recursing with flag 4), gives its direct children (node vfn 0x130) through the
same 180fb21f0. 180fb21f0 still needs a mapbuilder.ladder material on the mesh: with none, its
first-material index is -1 and no face is taken. Ported: both parts (NavGenLadders). Not ported: a
func_ladder inside an instance or prefab (its place in the flat node array, 180fc8c80) refuses.

Movable meshes (6) are marked while the settle world is built (180fb5ce0, called from 180fb5020):
- func_movelinear with `CreateMovableNavMesh`: its mesh ("does not have a mesh" / "any associated
  mesh" warnings).
- func_mover with `CreateMovableNavMesh`: its children, prop_deformable excluded.
- point_nav_movable_mesh: `ref_ent` (exactly one), `navgen_on_ref_ent`, `GravityFollowsRotation`;
  the reference entity's children, prop_deformable excluded.
- prop_deformable with `enable_map_generated_navmesh`.
State: not ported (NavCompile refuses a nav with movable meshes; NavWriter refuses an id path past
five ids). Several local maps carry `CreateMovableNavMesh` keys (Mako, ze_doom_p2, c2m2,
cs_script_demo); which have it set is not checked yet.

### Which bodies the nav world takes

PhysDoc_NodeFilter (180f4d220) with flags 0x16: `GenerateNav`, `AlwaysSolidIgnoreNav`, solidity,
has_static_mesh. Ported (NavGenInput.NavWorldFilter). Refused: a smart prop with +0x688 0 and a
physics pin, and FUN_180f4bb00's part. Material attribute `mapbuilder.navclip` is in both
resourcecompiler and hammer with no direct string reference: where it is read is not found yet.

## navsystem (generation 1800217b0, stage list 180022b60)

"Skipped... no walkable seeds present" when there are no seeds and no markups and island removal
is on. Each movable mesh input gets its own stage input (180048b20) and runs the same list.

The list in its build order, names as navsystem sets them (the port's comments also number some
stages, for example 022 ClipPolysToHullClearance and 029 IslandRemoval, as the capture tools do):

| stages | state |
|---|---|
| ValidateVertices, WeldVertices, RemoveDegens, RemovePolysMarkedNoNav | ported |
| TransformRecastNonZup, Recast, UnTransformRecastNonZup | Recast ported; the two transforms only for Z-up generation (nothing to do), the oriented path (CNavOrientedMesh voxels) not ported |
| WeldVertices, RemoveDegens, RemovePolysMarkedNoNav, RemoveStrayVerticalPolys, WeldVertices, RemoveDegens, MergePolys, LimitMaxPolyEdges, LimitMaxPolyEdgeLen, RemoveStrayVerticalPolys, WeldVertices, RemoveDegens | ported |
| SplitPolysByMarkup, WeldVertices, RemoveDegens | ported |
| ClipPolysToHullClearance | ported; casts against non-mesh shapes partly refused (below) |
| GenerateConnections, SplitBottleneckPolys, SplitPolysWithMultipleConnections, SplitBoundaryPolysWithConnections | ported |
| AddJumps, RemoveSmallAreasOnEdge | ported; AddJumps' cast is vphysics2's cylinder sweep (NavJumpSweepWorld, ledger 174) |
| LadderConnect | ported (ledger 107); rays against spheres and capsules refuse |
| IslandRemoval (twice: one hull, all hulls; nav_gen_island_removal / _all_hulls) | ported in the all-hulls form; links and several hulls not exercised |

Each stage's switch and parameters are nav_gen_* convars read at list build (their defaults are
what the port uses). After the list: JoinNonZup (180072b50, nav_gen_join_nonzup) on flag-8 polygons
refuses; the mesh build (180022200).

Other navsystem outputs: CNavSpaceBuilder (.navspace), CNavFlowMapGenerator (.navflowmap, per hull
with FlowMap_Enabled), the debug stages (NavGenRecastDebugStage_t, .navdbg). None ported.

## The mesh build and the .nav (navsystem, navlib)

CNavMesh::BuildFromNavGenPolys (1818d63c0) and SaveNavMeshToBuffer (1818d1320), version 36.
Ported for one hull with ladders (2026-10-28, ledger 174: s2c_prefabprobe3's .nav from the .vmap is
Valve's up to the custom data, ladder included); no links, movable meshes or place names. NavCompile.Mesh
runs the area build, then the ladders (NavMeshLadders), and NavWriter saves both.

### Ladders in the mesh build (ported 2026-10-28, NavMeshLadders)

After the areas, the grid and vfn 0x60, BuildFromNavGenPolys makes one CNavLadder (0x78 bytes,
1818e6970) per input ladder (1818c78a0: top, bottom, normal, width of CNavGenInput +0x68's 0x28-byte
entries), builds it (1818dc620) with the ladder's 0x1c-byte record from navsystem (seven area
indices, LadderConnect's), and appends it to the mesh's list (+0x108). Ids come from a counter set to
1 in the CNavMesh constructor (1818d4720) and its reset (1818dc3b0).

1818dc620, the build:
- top +0, bottom +0xc, width +0x1c; the direction (+0x58) from the input normal: |nx| <= |ny| gives
  2 when ny > 0 else 0, otherwise 1 when nx > 0 else 3.
- 1818e7300 sets the normal from the direction (0: (0,-1,0), 1: (1,0,0), 2: (0,1,0), 3: (-1,0,0)), then
  traces from the centre plus 5 n to the centre minus 32 n; when the hit's fraction is not 1 and its
  shape's InteractsAs (the 0x28-byte attribute block the trace copies, first qword) has bit 8, the
  normal becomes the hit's.
- The bottom then moves up along the ladder in 10-unit steps until a trace from the point to the
  point plus 32 n has fraction 1 and no start-solid; then the top moves down the same way.
- Length +0x18 = |top - bottom|; 1818e7300 runs again; the seven area slots are cleared.
- 1818e69b0 links the areas: record entries 4..6 are the bottom areas (+0x40, +0x48, +0x50; entry 4
  -1 keeps the old +0x40), 0..3 the top areas (+0x20 .. +0x38). Each area gets the ladder through
  1818ea300: the ladder is first removed from both of the area's lists (1818eb970), then appended to
  list 0 for a bottom area, list 1 for a top area. Then the top's z becomes the highest closest point
  (1818e4490 over the area's fan, 18129b060) of the top areas when one lies above bottom z + 5.

The trace (CNavToolsPhysicsInterface, vtable 182771930; vfn 0x10 = 1818d2b40 -> vfn 0x20 =
1818d2c10 -> 1818d1fa0): world vfn 0x560 with the shape {0, 0, 0, 0.1} (byte 1 at +0x28),
interacts with 0x3021 (DAT_183023858), +0x2f = 1, +0x30 = 4, flags 0x49, the same query bytes as
LadderConnect's ray (GROUND_TRUTH 107) but a cast through vfn 0x560 like the analysis' sphere
(NavSphereWorld). The result's +0xac fraction, +0x90 / +0x98 normal, +0xbb start-solid, +0x28 the
hit's attribute qword with bit 0 set. 1818d1fa0 keeps the first hit and replaces it only by a strictly
nearer one. The hit record (vphysics2 1801c7c70) takes the shape's normal through the body's turn as
(n.x r0 + n.y r3) + n.z r6, and the attributes from CPhysicsShape vfn 0x50 (18003f500, RnShape +0x50).
A cast starting inside a closed mesh reports fraction 0 and a normal from 18016f710, which is not
ported: the ladder refuses when it would take that normal. Start-solid never changes a result here.

The save (1818e71b0), per ladder after the areas: u32 id, f32 width, top, bottom, f32 length,
u32 direction, then the ids (+0x60) of the seven areas (+0x20, +0x28, +0x30, +0x38, +0x40, +0x48,
+0x50; 0 for none). CNavArea::Save (1818cfe80) writes list 0 then list 1, each a count and the
ladders' ids (+0x68).

Area flags written (attributes & 0x7f002) and the NAV_AREA_* / NAV_MESH_* names in the strings
include NAV_LINK, NAV_LINK_TERMINUS, HAS_LADDERS, MOVABLE, DEFORMABLE, NON_ZUP: each needs its
writer read before a map that sets it is called exact.

## The CS2 analysis (server.dll) and bomb damage (modtools.dll)

Ported for the maps measured (ledgers 103, 109, 146). Refusals in the port today:
- the survival play area (trigger_survival_playarea);
- portals of kind 8 and across 0x8000 areas (1814e5480, 1814eada0);
- more than 32 overlapping connections on an edge (1814a9280's introsort);
- normalising outside 1e-17 .. 1e17 (18146bed0);
- a sniper spot with no area under it;
- sphere casts against a hull with a region SVM or a scaled hull, rays past the world limit.

Bomb damage refuses: a func_bomb_target in an instance or prefab, one with no mesh or one that is not
an axis-aligned box, a world with hulls, spheres or capsules, a nav area with no polygon, a site
with no sampled cell.

## Settings

Refused: gameinfo NavAgentNumHulls (the deprecated agent path, 1818ca170),
settingsAgentHullsVDataOverride (1818ca4c0), more than one hull.

## Hammer

What Hammer can put in a .vmap that the nav step reads:
- Entities: point_nav_walkable (Nav tool "Create point_nav_walkable"), func_nav_markup with tags
  WALKABLESEED, NONAV, NONAVNONSOLID, NONAVSPACE, AVOID, SPLIT (and `navProperty_NavAttributesGame`),
  func_nav_gen_proj (NAV_GEN_PROJ), func_ladder, func_movelinear / func_mover / prop_deformable
  movable keys, point_nav_movable_mesh. The FGDs CS2 loads do not offer every class the collectors
  name (info_nav_space, info_gravity_shift, info_nav_oriented*, the info_node_* and hint classes);
  a .vmap can still carry them, so they are in scope.
- CMapNavLink (the Nav Links tool): type (MapNavLinkType_t GENERIC, GROUND, JUMP_DOWN, CLIMB_UP,
  VAULT_OVER), width, movement forward / reverse and previews (scripts/navlinks.vdata), locators
  (CMapNavLinkLocator). The collector takes only its two end points.
- CMapNavData (the map's nav settings, `navData`): ported.
- Materials: mapbuilder.ladder (tools/toolsinvisibleladder), mapbuilder.navclip,
  nav_attribute_avoid.
- Hammer-only: the nav mesh polyclip tool (CToolNavMeshPolyclip, "Generate Nav Debug", "Load Nav From
  VPK") and grid nav (CMapDotaGridnavMarkup, `-gridnav`): whether either reaches a CS2 compile is
  not read.

## Order of work

1. Ladders: done on s2c_prefabprobe3 (2026-10-28). Left: a func_ladder in an instance or prefab,
   the closed-mesh normal (18016f710), and c2m3_coaster_d as a second check.
2. Nav links: the CMapNavLink collector, IslandRemoval with links, whatever the mesh build and the
   analysis do with them. Needs a probe map with every link type.
3. Movable meshes, every source above.
4. AVOID from materials (6) and NONAV blocks (3).
5. Oriented and projected generation (4, 10, stages 005 / 007, JoinNonZup).
6. Nav space and flow map files, navdbg.
7. The analysis and bomb damage refusals, several hulls, the settings paths.
