"""In-game collision check of a map over netcon.

Launch CS2 as `cs2.exe -tools -insecure -netconport 29000 -novid` (never without
-insecure). The map's .vpk has to be in game/csgo/maps before the game starts:
the list of loadable maps is read at launch, and -addon does not mount an
addon here. Logs and screenshots go to the working directory.

    python map_test.py <label> <map> <x,y,z drop> [<x,y,z trigger>=<x,y,z destination> ...] [--cubemaps]

Loads the map, logs the console to <label>.log, drops the player at the given
point and reports where they stop, then for each trigger stands the player at
its position and reports where they end up next to the destination expected.

--cubemaps runs `buildcubemaps` after the load, as Hammer's "Build cubemaps on
load" post-build action does. It only writes for an addon map (CS2 launched
with -addon, map loaded with nomapvalidation=true; see docs/CAVEATS.md); from
a copy in game/csgo/maps the write is blocked. Cubemaps do not fix the purple
look seen in our tests (CAVEATS: open).
"""
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from cs2_console import Console

cubemaps = "--cubemaps" in sys.argv
args = [a for a in sys.argv[1:] if a != "--cubemaps"]
label, mapname, drop = args[0], args[1], args[2]
triggers = [a.split("=") for a in args[3:]]
here = os.getcwd()
FG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "cs2_front.ps1")
log = open(os.path.join(here, f"{label}.log"), "w", encoding="utf-8")


def front(shot=None):
    """CS2 runs badly in the background: bring its game window to the front."""
    subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", FG]
                   + (["-Shot", os.path.join(here, shot)] if shot else []), capture_output=True)


def run(console, commands, wait):
    for c in commands:
        console.send(c)
        time.sleep(0.3)
    lines = console.drain(wait)
    for l in lines:
        log.write(l + "\n")
    log.flush()
    return lines


def pos(lines):
    found = None
    for l in lines:
        m = re.search(r"setpos\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)", l)
        if m:
            found = tuple(float(x) for x in m.groups())
    return found


front()
c = Console()
c.drain(1)
run(c, ["engine_no_focus_sleep 0"], 1)
lines = run(c, [f"map {mapname}"], 60)
front()
lines += run(c, ["echo load_done"], 10)
problems = [l for l in lines if re.search(r"error|fail|missing|could not|invalid|physics", l, re.I)]
print(f"{label}: {len(lines)} lines while loading, {len(problems)} flagged")
for l in problems[:60]:
    print("   ", l[:180])

if cubemaps:
    front()
    built = run(c, ["sv_cheats 1", "buildcubemaps"], 120)
    print(f"{label}: buildcubemaps, {len(built)} lines; " + "; ".join(l[:120] for l in built if re.search(r"cubemap|envmap", l, re.I))[:600])

front()
run(c, ["sv_cheats 1", "mp_warmup_start", "mp_warmuptime 9999", "mp_autoteambalance 0", "mp_limitteams 0", "jointeam 3 1"], 8)
run(c, ["noclip 0", "setpos " + drop.replace(",", " "), "setang 0 0 0"], 5)
print(f"{label}: dropped at {drop}, eye at {pos(run(c, ['getpos'], 2))}")
front(f"{label}_drop.png")
for where, dest in triggers:
    run(c, ["setpos " + where.replace(",", " ")], 4)
    print(f"{label}: stood at {where}, eye at {pos(run(c, ['getpos'], 2))} (destination {dest})")
    run(c, ["setpos " + drop.replace(",", " ")], 3)
c.close()
log.close()
