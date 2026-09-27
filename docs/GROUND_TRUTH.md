# Ground truth ledger

Every rule the port follows must come from Valve's code: a decompile, a
capture of the compiler running, or Valve's code called in place by a test.
A rule fitted to Valve's output is a lead, not a fact. This ledger lists the
rules that were inferred, assumed or only measured, and how each is being
settled. When one is settled, it moves to the resolved list with its source.

Tools for settling:
- **Decompile** (Ghidra through ReVa) of resourcecompiler, physicsbuilder,
  vphysics2, tier0 and the other tool DLLs.
- **In-process oracle**: a test loads the installed DLL and calls the function
  itself on many inputs (ResourceCompilerOracle, Vphysics2Oracle). Used
  whenever the function is pure enough to call.
- **Capture**: Frida on resourcecompiler during a compile of a small test map,
  dumping a function's inputs and outputs (the vis capture harness).

The game itself is not attached to. Nothing here needs it; a question only the
game can answer is raised with the user first, and runs with -insecure.

## Open

### World physics
1. **Convex world hull input.** A convex world mesh is hulled from every
   vertex of its per-material mesh, in that mesh's vertex order. Joining the
   triangle corners by .vmap vertex matches every hull but three hard-edged
   pipes on Mako. The per-material mesh's vertex key (normals from the
   smoothing angle, shifted texcoords) is not read. Settle by reading the
   per-material mesh build and capturing the hull builder's input.
2. **Painted convex pieces.** The split builds a positions-only mesh per
   layer, welded at 1/32, and each is hulled on its own. The port keeps the
   unsplit hull when one layer takes everything and notes the rest. Port the
   split mesh literally.
3. **PHYS block compression.** The Zstd threshold was bracketed from sizes
   (512 KiB assumed). Read the block writer's decision.
4. **Face order around a vertex** in the brush hull neighbour set: triangle
   order stands in (it only matters on a hash collision).
5. **Instance path for brush entity meshes**: applied after the node's own
   move, or composed first. Both place atixref the same; not read.
6. **Stitched subdivided faces**: the bake splits half-edge chains by arc
   length. Port the bake's face and edge splits (in progress).
7. **Collision names registered by the game** (csgo_*) keep the model's
   spelling: assumed, no counter-example.
8. **Material list splitting**: empty pieces of V_SplitString taken as
   dropped. Check against tier0's export.
9. **Texcoord transform defaults** for parameters a material leaves out
   (UV set 0, scale 1, rotation 0).
10. **Hammer's re-projected texcoords and normals** (about 1e-5 off on some
    faces). They do not change a hull, but the source is not read.
11. **Surface table and RED2 listing rules** (entries keyed by the name as
    spelled; RED2 lists the named ones with counts; a layer-split mesh names
    "default"): fitted to cardtest, c2m2 and Mako output. Read the writer.
12. **VPK v2 layout** (tree order, chunk hashes, signature section): byte
    exact on four packages, the packer not read.

### Map resources
13. The angles of instance copies inside a world layer (a round trip off).
14. Mako's cable_dynamic rendercolor written as text.
15. What picks Zstd or LZ4 for map output.

### Other modules
16. Visibility: what triggers the large-scale ray generator; the compile's
    normalise guard against our plain normalise.
17. RTE: what the per-triangle id hashes.
18. vrad3 trace cost law (performance only; not output).
19. Legacy-GUID particles in game (needs the user's go-ahead for a game run).
20. The vmat_c field order note ("likely NTRO positional"): the order is
    measured; the loader's reason is not read.

## Resolved

- **Split pieces carry no tool material** (2026-09-28, decompile). The
  blend split builds new meshes with positions only and never names their
  material; physicsbuilder sets each node's tool material from its mesh's
  material name, so a split piece's is empty and its hash is 0.
- **Tool material hash** (2026-09-28, decompile and oracle). The node's
  tool material goes through FixupResourceName for "vmat" (absolute paths,
  a leading '/' and other extensions fail and hash as 0; a missing extension
  is added; separators, "./" and ".." are folded; A-Z lowered) and then a
  lowercase MurmurHash2 with seed 0x31415926. Checked against
  resourcecompiler's own functions on 4,048 names, and the tier0 path helpers
  against tier0's exports on the same names.
- **Soup tool hash and name** (2026-09-28, decompile). Members without
  triangles are skipped outright. The tool hash is kept when exactly one
  hash has triangles. The name joins non-empty names with "; ", is long past
  50 characters, and the next name seals it with "; ...".
