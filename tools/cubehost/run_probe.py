"""run_probe.py [--map probe01] [--from s2c_rc_probe] [--timeout 600] [-- extra engine args]: the game's cubemap build in cubehost.

Copies <from>'s compiled <map>.vpk into the scratch addon s2c_cubeprobe (game/csgo_addons/s2c_cubeprobe/maps), so
the user's own addons are never written, runs tools/cubehost (engine2's Source2Main in our own process, never cs2.exe)
with -insecure -nosteam -buildcubemaps, and keeps what the build leaves under %TEMP%/cubehost_run/<map>: every file
under the scratch addon's content and game folders that changed, and the console output.

Refuses unless Steam (steam.exe, steamwebhelper.exe) and CS2 are closed: the host checks this again itself.
"""
import argparse
import os
import shutil
import subprocess
import sys
import time

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
HERE = os.path.dirname(os.path.abspath(__file__))
HOST = os.path.join(HERE, "out", "bin", "win64", "cubehost.exe")
ADDON = "s2c_cubeprobe"


def running():
    out = subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower()
    return [n for n in ("steam.exe", "steamwebhelper.exe", "cs2.exe") if n in out]


def snapshot(root):
    files = {}
    for d, _, names in os.walk(root):
        for n in names:
            p = os.path.join(d, n)
            st = os.stat(p)
            files[p] = (st.st_size, st.st_mtime_ns)
    return files


def keep_intermediates(roots, out, stop, seen):
    """Copies every file that appears under a _buildcubemaps folder of the roots into out/intermediates while the
    build runs (the game deletes them after compiling); nothing in the engine is hooked."""
    import threading
    keep = os.path.join(out, "intermediates")
    os.makedirs(keep, exist_ok=True)
    copied = {}

    def watch():
        while not stop.is_set():
            for root in roots:
                for d, _, names in os.walk(root):
                    if "_buildcubemaps" not in d.lower():
                        continue
                    for n in names:
                        f = os.path.join(d, n)
                        try:
                            st = os.stat(f)
                            if copied.get(f) != (st.st_size, st.st_mtime_ns):
                                shutil.copy2(f, os.path.join(keep, n))
                                copied[f] = (st.st_size, st.st_mtime_ns)
                                seen.append(f"kept {f} ({st.st_size} bytes)")
                        except OSError:
                            pass
            stop.wait(0.05)

    t = threading.Thread(target=watch, daemon=True)
    t.start()
    return t


def drive(host, a, seen):
    """The console steps once the engine listens: load the map (tools mode needs nomapvalidation), build the
    cubemaps, quit. Returns how it ended."""
    sys.path.insert(0, os.path.join(os.path.dirname(HERE), "pipeline"))
    from cs2_console import Console
    deadline = time.time() + a.timeout
    console = None
    while time.time() < deadline and host.poll() is None and console is None:
        try:
            console = Console(port=a.netcon)
        except OSError:
            time.sleep(2)
    if console is None:
        return f"no console (exit {host.poll()})"

    def wait_for(words, seconds):
        until = min(deadline, time.time() + seconds)
        while time.time() < until and host.poll() is None:
            try:
                lines = console.drain(2)
            except OSError:
                return None
            for line in lines:
                seen.append(line)
                for w in words:
                    if w.lower() in line.lower():
                        return line
        return None

    wait_for(["Source2Init OK", "Host activate"], 180)
    # No bots: they joined probe maps without a nav mesh and the engine crashed after BeginMatch.
    for c in ("bot_quota 0", "bot_quota_mode normal", "bot_kick"):
        console.send(c)
        time.sleep(0.3)
    console.send(f"map {a.map} nomapvalidation=true")
    loaded = wait_for([f'Map: "{a.map}"', "invalid map name"], 300)
    if not loaded or "invalid" in loaded:
        return f"map did not load ({loaded})"
    console.send("bot_kick")
    wait_for(["OnSwitchLoopModeFinished"], 60)
    console.send("bot_kick")
    console.send("buildcubemaps")
    done = wait_for(["Finished building cubemaps", "Failed to compile cubemaps", "Finished ("], a.timeout)
    wait_for(["Finished building cubemaps"], 20)
    try:
        console.send("quit")
    except OSError:
        pass
    try:
        host.wait(60)
    except subprocess.TimeoutExpired:
        pass
    return f"build: {done}"


def attach_agent(pid, path, out):
    """Loads a Frida agent into cubehost.exe (our own process, never cs2.exe); each message is a JSON line."""
    import json
    import frida
    log = open(os.path.join(out, "frida.jsonl"), "w", encoding="utf-8")

    def on_message(m, _data):
        log.write(json.dumps(m["payload"] if m["type"] == "send" else m) + "\n")
        log.flush()

    session = frida.get_local_device().attach(pid)
    script = session.create_script(open(path, encoding="utf-8").read())
    script.on("message", on_message)
    script.load()

    class Agent:
        def close(self):
            try:
                log.write(json.dumps({"kind": "counts", "counts": script.exports_sync.counts()}) + "\n")
            except Exception:  # the process is usually gone by now
                pass
            log.close()
    return Agent()


def main():
    argv = sys.argv[1:]
    extra = argv[argv.index("--") + 1:] if "--" in argv else []
    argv = argv[:argv.index("--")] if "--" in argv else argv
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--map", default="probe01")
    p.add_argument("--from", dest="source", default="s2c_rc_probe")
    p.add_argument("--timeout", type=int, default=600)
    p.add_argument("--netcon", type=int, default=29000)
    p.add_argument("--renderdoc", type=int, metavar="CUBEMAP",
                   help="capture that cubemap's render with RenderDoc (inside cubehost.exe only; Steam closed)")
    p.add_argument("--frida", metavar="AGENT.js",
                   help="attach a Frida agent to cubehost.exe as it starts; its messages go to <out>/frida.jsonl "
                        "(cascade_sources.js: where the cascade inputs come from)")
    p.add_argument("--keep-intermediates", action="store_true",
                   help="copy the game's _buildcubemaps EXRs and .vtex as they appear (into <out>/intermediates)")
    a = p.parse_args(argv)
    if (r := running()):
        raise SystemExit(f"close {', '.join(r)} first (Steam must be fully closed)")
    # The game/bin files the engine looks for beside its executable's folder (assettypes_common.txt and the rest).
    for name in os.listdir(os.path.join(CS2, "game", "bin")):
        source = os.path.join(CS2, "game", "bin", name)
        if os.path.isfile(source):
            shutil.copy2(source, os.path.join(HERE, "out", "bin", name))
    game = os.path.join(CS2, "game", "csgo_addons", ADDON)
    content = os.path.join(CS2, "content", "csgo_addons", ADDON)
    os.makedirs(os.path.join(game, "maps"), exist_ok=True)
    os.makedirs(content, exist_ok=True)
    shutil.copy2(os.path.join(CS2, "game", "csgo_addons", a.source, "maps", a.map + ".vpk"), os.path.join(game, "maps", a.map + ".vpk"))
    # The game mode's cfg runs on every map load and puts the bots back; its _server.cfg runs after it and
    # takes them out again (in the scratch addon only, never the game's cfg folder).
    os.makedirs(os.path.join(game, "cfg"), exist_ok=True)
    for name in os.listdir(os.path.join(CS2, "game", "csgo", "cfg")):
        if name.startswith("gamemode_") and name.endswith(".cfg") and not name.endswith("_server.cfg"):
            with open(os.path.join(game, "cfg", name[:-4] + "_server.cfg"), "w") as f:
                f.write("bot_quota 0\nbot_quota_mode normal\nbot_kick\n")
    out =os.path.join(os.environ.get("TEMP", "."), "cubehost_run", a.map)
    os.makedirs(out, exist_ok=True)
    before = {**snapshot(game), **snapshot(content)}
    # -condebug appends to the game's console.log (the user's own): keep it aside and restore it after.
    console = os.path.join(CS2, "game", "csgo", "console.log")
    console_backup = os.path.join(out, "console.log.user")
    had_console = os.path.exists(console)
    if had_console:
        shutil.copy2(console, console_backup)
    console_start = os.path.getsize(console) if had_console else 0
    cmd = [HOST, *(["--renderdoc-capture", str(a.renderdoc)] if a.renderdoc is not None else []), "--", "-insecure", "-nosteam", "-nobreakpad", "-vulkan", "-novid", "-console", "-condebug",
           "-addon", ADDON, "-tools", "-netconport", str(a.netcon), *(["-renderdoc"] if a.renderdoc is not None else []), *extra]
    # RenderDoc loads into cubehost.exe only (the game's own -renderdoc support), never cs2.exe; its captures land
    # in %TEMP%/RenderDoc and are collected after the run. buildcubemaps_renderdoc_capture is development-only
    # (client 10-15 1800538d8 registers it with flags 2): neither the console nor the command line sets it, so
    # the host writes it in its own process (--renderdoc-capture, Program.cs).
    rdc_dir = os.path.join(os.environ.get("TEMP", "."), "RenderDoc")
    rdc_before = set(os.listdir(rdc_dir)) if os.path.isdir(rdc_dir) else set()
    print(" ".join(cmd))
    started = time.time()
    seen = []
    with open(os.path.join(out, "host.log"), "wb") as log:
        host = subprocess.Popen(cmd, stdout=log, stderr=subprocess.STDOUT)
        agent = attach_agent(host.pid, a.frida, out) if a.frida else None
        import threading
        stop = threading.Event()
        keeper = keep_intermediates([game, content], out, stop, seen) if a.keep_intermediates else None
        try:
            try:
                code = drive(host, a, seen)
            except OSError as e:
                code = f"console lost ({e}); exit {host.poll()}"
        finally:
            if host.poll() is None:
                subprocess.run(["taskkill", "/F", "/IM", "cubehost.exe"], capture_output=True)
                host.wait(30)
            stop.set()
            if agent:
                agent.close()
    with open(os.path.join(out, "netcon.log"), "w", encoding="utf-8") as f:
        f.write("\n".join(seen) + "\n")
    if os.path.isdir(rdc_dir):
        # The engine opens each capture it writes in RenderDoc's UI (qrenderdoc, several GB for a cubemap): close
        # the ones it opened on this run's captures (and their crash handlers) so the files can be moved.
        subprocess.run(["powershell", "-NoProfile", "-Command",
                        "Get-CimInstance Win32_Process -Filter \"Name='qrenderdoc.exe'\" | "
                        "Where-Object { $_.CommandLine -like '*RenderDoc\\cubehost_*.rdc*' } | "
                        "ForEach-Object { Get-CimInstance Win32_Process -Filter \"ParentProcessId=$($_.ProcessId)\" | "
                        "ForEach-Object { Stop-Process -Id $_.ProcessId -Force }; Stop-Process -Id $_.ProcessId -Force }"],
                       capture_output=True)
        time.sleep(1)
        for n in sorted(set(os.listdir(rdc_dir)) - rdc_before):
            if n.endswith(".rdc"):
                shutil.move(os.path.join(rdc_dir, n), os.path.join(out, n))
                print("  capture " + n)
    after = {**snapshot(game), **snapshot(content)}
    changed = [f for f, v in after.items() if before.get(f) != v]
    for f in changed:
        rel = os.path.relpath(f, os.path.join(CS2))
        dest = os.path.join(out, rel.replace(os.sep, "__"))
        shutil.copy2(f, dest)
    if os.path.exists(console):
        with open(console, "rb") as f:
            f.seek(console_start if os.path.getsize(console) >= console_start else 0)
            with open(os.path.join(out, "console.log"), "wb") as o:
                o.write(f.read())
        if had_console:
            shutil.copy2(console_backup, console)
            os.remove(console_backup)
        else:
            os.remove(console)
    print(f"result: {code} after {time.time() - started:.0f} s; {len(changed)} files changed, kept in {out}")
    for f in changed:
        print("  " + os.path.relpath(f, CS2))


if __name__ == "__main__":
    main()
