# Compile service roadmap

Compiler work the compile service (its repo's docs/POD_PIPELINE.md) asked for. Nothing here
is built yet. Parity with Valve comes first: an item that would change output bytes waits for
the user's go (GROUND_TRUTH, parity before speed).

## S1. Stock textures at 512 px (optimisation; decide later)

The blend captures suggest a preload-512 rule: the bake samples no texture level larger than
512 px. If that holds for the whole lighting path, a compile machine needs only that much of
each stock texture.

- **Measured** over the 40 Port-Vmaps maps, by the service: a map needs a median of 60 MB of
  stock files (mako_reactor 2.6 GB; all 40 together 4.6 GB, against 50 GB of csgo + core
  pak01). Materials and textures are about 98% of those bytes.
- **First:** settle the rule from the DLLs for every stage that reads a stock texture: the
  lighting bake, the material sampler and world node compiles. Stock textures are not
  recompiled, so the question is only which levels are read.
- **Changes no output bytes today.** The decision is the user's.

## S2. Each stage's input set

Given a .vmap and the maps it loads (prefabs, instances), list which files each compile stage
reads, custom and stock. The stages are: addon asset compile, world, vis, physics, nav,
lighting and the post-build steps. The service orders its transfers to the rented pod by this
list, so a stage's files arrive before it starts.

- Take the list from the code paths the port already follows (GameContent reads per stage),
  checked against a capture of resourcecompiler's file opens. Do not estimate it.
- Output: a manifest per stage, with path, source (addon or stock pak) and size.

## S3. Starting before every file has arrived

A stage starts when its own inputs are present. The compile accepts files that arrive while it
runs: the content source waits for a missing file, with a timeout, instead of failing. Addon
assets compile one by one as their sources complete. This needs S2 first.

## S4. A map loaded into itself (found 2026-10-20)

ze_lotr_minas_tirith_skybox.vmap holds a CMapPrefab of its own file. Valve's tools compile it,
but MapPrefabs.Attach throws "prefabs nested more than 32 deep". The service's pre-check now
skips a map loaded into itself.

- **To settle:** what resourcecompiler does with such a node. It may skip it, load it once, or
  stop at a depth. Capture or decompile the prefab loader, then match it in MapPrefabs (ledger
  entry when settled).
