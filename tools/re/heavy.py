"""heavy.py -- one heavy job at a time on this PC, first come first served, across sessions and worktrees.

    python tools/re/heavy.py run --who <session> --what "<label>" [--gpu] -- <command> [args...]
    python tools/re/heavy.py status
    python tools/re/heavy.py clear-stale

Heavy means: any resourcecompiler run (compile-map with RC, capture_*.py, call_navsystem.py), a GPU
bake (-s2cbake, vrad3), a dotnet test run over whole maps, anything over ~4 GB RAM or a minute of all
cores. Quick builds and filtered unit tests do not need it.

How it works: each waiter writes a ticket (<dir>/tickets/<time>_<pid>.json); the oldest live ticket
whose process still exists runs when the lock file is free, holding an exclusive msvcrt lock on
<dir>/heavy.lock for the command's lifetime (released by the OS even if the process dies). The holder
is described in <dir>/current.json. <dir> is %LOCALAPPDATA%/s2c-heavy, shared by every checkout.
Refuses to start an RC job while cs2.exe runs (the rule for resourcecompiler on this PC); pass
--allow-cs2 for jobs that do not touch the game.
"""
import argparse
import json
import msvcrt
import os
import subprocess
import sys
import time

DIR = os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "s2c-heavy")
TICKETS = os.path.join(DIR, "tickets")


def alive(pid):
    out = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/NH"], capture_output=True, text=True).stdout
    return str(pid) in out


def running(name):
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq " + name], capture_output=True, text=True).stdout
    return name.lower() in out.lower()


def tickets():
    os.makedirs(TICKETS, exist_ok=True)
    out = []
    for f in sorted(os.listdir(TICKETS)):
        try:
            with open(os.path.join(TICKETS, f)) as h:
                t = json.load(h)
            out.append((f, t))
        except (OSError, ValueError):
            pass
    return out


def status():
    cur = os.path.join(DIR, "current.json")
    if os.path.exists(cur):
        with open(cur) as h:
            c = json.load(h)
        state = "running" if alive(c["pid"]) else "stale (process gone)"
        print(f"current: {c['who']}: {c['what']} (pid {c['pid']}, since {c['since']}, {state})")
    else:
        print("current: none")
    for f, t in tickets():
        print(f"waiting: {t['who']}: {t['what']} (pid {t['pid']}, queued {t['queued']}{'' if alive(t['pid']) else ', stale'})")


def clear_stale():
    for f, t in tickets():
        if not alive(t["pid"]):
            os.remove(os.path.join(TICKETS, f))
            print("removed", f)


def run(a, cmd):
    os.makedirs(TICKETS, exist_ok=True)
    me = f"{time.time():017.6f}_{os.getpid()}.json"
    now = time.strftime("%Y-%m-%d %H:%M:%S")
    info = {"who": a.who, "what": a.what, "pid": os.getpid(), "queued": now, "gpu": a.gpu, "cmd": cmd}
    with open(os.path.join(TICKETS, me), "w") as h:
        json.dump(info, h)
    lock = open(os.path.join(DIR, "heavy.lock"), "a+")
    told = False
    try:
        while True:
            live = [f for f, t in tickets() if alive(t["pid"])]
            if live and live[0] == me:
                try:
                    msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
                    break
                except OSError:
                    pass
            if not told:
                print(f"heavy: waiting ({max(0, live.index(me)) if me in live else 0} ahead in the queue, plus any running job)", flush=True)
                told = True
            time.sleep(3)
        while not a.allow_cs2 and running("cs2.exe"):
            print("heavy: cs2.exe is running; waiting for it to close (--allow-cs2 to skip)", flush=True)
            time.sleep(30)
        os.remove(os.path.join(TICKETS, me))
        info["since"] = time.strftime("%Y-%m-%d %H:%M:%S")
        with open(os.path.join(DIR, "current.json"), "w") as h:
            json.dump(info, h)
        print(f"heavy: running {a.what}", flush=True)
        code = subprocess.call(cmd)
        return code
    finally:
        try:
            os.remove(os.path.join(TICKETS, me))
        except OSError:
            pass
        try:
            os.remove(os.path.join(DIR, "current.json"))
        except OSError:
            pass
        try:
            msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
        except OSError:
            pass
        lock.close()


def main():
    argv = sys.argv[1:]
    cmd = []
    if "--" in argv:
        i = argv.index("--")
        argv, cmd = argv[:i], argv[i + 1:]
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("action", choices=["run", "status", "clear-stale"])
    p.add_argument("--who", default="unknown")
    p.add_argument("--what", default="")
    p.add_argument("--gpu", action="store_true")
    p.add_argument("--allow-cs2", action="store_true")
    a = p.parse_args(argv)
    if a.action == "status":
        status()
    elif a.action == "clear-stale":
        clear_stale()
    else:
        if not cmd:
            raise SystemExit("run needs a command after --")
        sys.exit(run(a, cmd))


if __name__ == "__main__":
    main()
