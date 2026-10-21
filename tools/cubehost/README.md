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
