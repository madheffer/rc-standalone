# cubehost

CS2's engine (engine2.dll's `Source2Main`, as cs2.exe calls it) in our own
process, with no Steam client and no Steam account. Milestone of 2026-10-21:
with `-tools` it brings up the Workshop Tools (the asset browser, Hammer's
environment) this way.

Built for the cubemap work (docs/CUBEMAPS.md): the game's `-buildcubemaps`
run where it can be observed and captured without touching cs2.exe.

## How it works

- `out/bin/win64/cubehost.exe`: the game's own layout (`<root>/bin/win64/<exe>`),
  because the engine takes `EXECUTABLE_PATH` and `GAMEBIN` from where the exe
  sits; `run_probe.py` copies game/bin's text files (assettypes_common.txt and
  the rest) into `out/bin`. tier0's `Plat_SetGameDirectory` points it at
  `<CS2>/game`.
- `steam_api64.dll` beside it is our stub (`steam_api_stub.c`, built by
  `build_stub.ps1` with MSVC), loaded before the engine so every import of
  steam_api binds to it; the host refuses to run if the game's steam_api
  loaded instead. No Steam code runs:
  - init reports failure, `SteamAPI_IsSteamRunning` is false, callbacks and
    crash-dump calls do nothing;
  - `ISteamApps` is absent (client.dll's `-nosteam` path requires it);
  - networksystem and steamnetworkingsockets see no Steam (their non-Steam
    path);
  - other interfaces are dummies whose methods return a zeroed block at a
    4 GB-aligned address (0 as an int or bool, an empty string or struct as a
    pointer); `ISteamUser::GetSteamID` writes a zero CSteamID;
  - every `ISteamClient` method answers NULL (it hands out interfaces; a zeroed
    block taken for one crashed server.dll's Steamworks stats after BeginMatch);
  - the listen server's game server init succeeds locally.

## Safety (enforced by the host)

- Refuses to start while steam.exe, steamwebhelper.exe or cs2.exe runs.
- Requires `-insecure -nosteam -nobreakpad` on its own command line.
- Valve's steam_api and steamclient are never loaded; there is no account
  session and nothing for VAC to attach to.

Running the tools while CS2 itself is open (mapping while playing) is not
allowed by these guards; it needs its own safety review first (roadmap:
docs/ROADMAP_TOOLS_WITHOUT_STEAM.md, out of scope for the compile port).

## Running

```
python tools/cubehost/run_probe.py [--map probe01] [--timeout 300] -- -tools
```

Copies the map's compiled package into the scratch addon `s2c_cubeprobe`,
runs the engine, keeps changed files, host output and the console log (the
user's own console.log is restored) under `%TEMP%/cubehost_run/<map>`.
`dbwin.py` captures OutputDebugString of a run. Steam must be closed.
`--out NAME` keeps a run beside another of the same map, `--from s2c_cubeprobe`
reruns the package already in the scratch addon, `--settle 20` waits after the
load so texture streaming finishes (unsettled builds sample whichever mips are
resident and differ run to run; docs/CUBEMAPS.md, ledger 144) and `--before CMD`
sends console commands just before `buildcubemaps`.

The game's mode configs put bots back on every map load; the runner writes
`gamemode_*_server.cfg` overrides into the scratch addon to keep them out.

## RenderDoc capture of a cubemap

```
python tools/cubehost/run_probe.py --renderdoc 0
```

Adds `-renderdoc` (the engine's own hook loads the installed RenderDoc into
cubehost.exe; never into cs2.exe) and passes `--renderdoc-capture 0` to the
host, which sets the development-only convar `buildcubemaps_renderdoc_capture`
in its own process (release builds hide it from the console and command
line). BuildCubemaps captures that cube's six faces; the runner closes the
qrenderdoc window the engine opens and moves the .rdc next to the run's logs.
After a CS2 update, add client.dll's time stamp to `ClientConvarGlobals` in Program.cs AND rebuild
(`dotnet build tools/cubehost/cubehost.csproj`): host.log saying "has no known buildcubemaps_renderdoc_capture
global" means the exe is older than the table, and the build then runs with no capture.
`rdc_summary.py` lists a capture's passes from RenderDoc's own Python:

```
RDC_IN=<capture.rdc> RDC_OUT=<summary.txt> "C:\Program Files\RenderDoc\qrenderdoc.exe" --python tools/cubehost/rdc_summary.py
```
