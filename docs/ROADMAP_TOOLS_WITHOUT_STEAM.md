# Roadmap: CS2 Workshop Tools without Steam, and mapping while playing

Out of scope for the map compile port. Recorded here so the milestone is not
lost and can be picked up as its own project.

## Where it stands (2026-10-21)

`tools/cubehost` (README there) runs CS2's engine (engine2.dll's
`Source2Main`) in our own executable with a stub `steam_api64.dll`: no Steam
client, no Steam account, Valve's steam_api never loaded. With `-tools` it
brings up the Workshop Tools asset browser; a map loads over the console and
the game's own `buildcubemaps` runs. For mappers this answers a standing
annoyance: the tools need Steam running and lock out the game.

## Goal

Map in the Workshop Tools (Hammer, asset browser, compile) while CS2 itself
runs for play, without any risk to the player's account.

## Steps

1. **Hammer in the host.** Open Hammer from the asset browser, load and save a
   .vmap, run a compile (resourcecompiler is launched as its own process),
   play the map in the tools' game view. Note every Steam interface the tools
   touch (the stub's CUBEHOST_STEAMLOG) and every crash; extend the stub only
   with local, inert answers.
2. **Isolation from a running CS2** (the blocker; read-only review first):
   - the single-instance mutex (`Plat_CreateGameInstanceMutex`, engine2
     18020c9e0) and any other named objects both processes would share;
   - files both write: `game/csgo/console.log`, `cfg/*.vcfg`, the addon's
     tools caches, shader caches; give the host its own write paths;
   - the GPU and driver (two Vulkan devices on one GPU), and the OBS game
     capture hook that injects into new processes;
   - anything that could let Steam or VAC associate our process with the
     player's session (Steam must not see it: no steam_api, no steamclient,
     no Steam IPC, nothing in the game's process).
3. **Safety gate.** A written VAC/ban review of step 2 with evidence; go only
   on a clear "completely safe" (the user's bar), with the user's explicit OK.
   Until then the host keeps refusing to start while Steam or cs2.exe runs.
4. **Packaging for mappers.** A launcher with the stub and layout (exe in
   `bin/win64`, game/bin's text files one level up), documented limits
   (no Workshop upload, no Steam features), and an update check against CS2
   patches (tools/re/patch_check.py).

## Not doing

- Touching cs2.exe or its memory, or loading anything into the player's game.
- Workshop upload or any Steam service from the host.
