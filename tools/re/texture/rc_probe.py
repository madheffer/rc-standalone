"""rc_probe.py <src_dir> <content_rel_dir> <out_dir> [file ...]: compile probes with Valve's RC.

Copies every file of <src_dir> (or only the named ones) into the probe addon's content tree at
<content_rel_dir> (for example materials/s2calg), compiles each .vtex / .vmat / .vdata /
.vsndevts / .vpcf there with the installed resourcecompiler.exe, and copies what RC wrote under
the game tree's <content_rel_dir> into <out_dir>. The addon (vpkstuff_rc_probe, where the RcReference fixtures were compiled: RC
records its search path) is left as it was: every game-side file the compile would overwrite is
backed up first and restored afterwards (cmp-checked), and files the compile newly wrote are
removed, as are content files this script copied in.

resourcecompiler on its own touches no game process, so it may run with CS2 open (user,
2026-11-02); it then runs at below-normal priority. Refuses while another resourcecompiler runs.
"""
import filecmp
import os
import shutil
import subprocess
import sys

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
ADDON = os.environ.get("RC_PROBE_ADDON", "vpkstuff_rc_probe")


def running(name):
    return name.lower() in subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower()


def snapshot(root):
    files = {}
    for d, _, names in os.walk(root):
        for n in names:
            p = os.path.join(d, n)
            files[p] = os.stat(p).st_mtime_ns
    return files


def main():
    if len(sys.argv) < 4:
        raise SystemExit(__doc__)
    src_dir, rel, out_dir = sys.argv[1], sys.argv[2].replace("\\", "/").strip("/"), sys.argv[3]
    only = sys.argv[4:]
    if running("resourcecompiler.exe"):
        raise SystemExit("a resourcecompiler is running; not starting")
    import ctypes
    ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)  # BELOW_NORMAL, inherited

    content = os.path.join(CS2, "content", "csgo_addons", ADDON, *rel.split("/"))
    game_addon = os.path.join(CS2, "game", "csgo_addons", ADDON)
    game = os.path.join(game_addon, *rel.split("/"))
    made_content = not os.path.isdir(content)
    made_game = not os.path.isdir(game)
    os.makedirs(content, exist_ok=True)
    os.makedirs(out_dir, exist_ok=True)

    names = only or sorted(os.listdir(src_dir))
    copied = []
    for n in names:
        dst = os.path.join(content, n)
        if os.path.exists(dst) and not filecmp.cmp(os.path.join(src_dir, n), dst, shallow=False):
            raise SystemExit(f"{dst} exists with other bytes; not overwriting the user's content")
        if not os.path.exists(dst):
            shutil.copyfile(os.path.join(src_dir, n), dst)
            copied.append(dst)

    before = snapshot(game_addon)
    backup = os.path.join(out_dir, "_backup")
    for p in before:
        rp = os.path.relpath(p, game_addon)
        b = os.path.join(backup, rp)
        os.makedirs(os.path.dirname(b), exist_ok=True)
        shutil.copy2(p, b)

    try:
        for n in names:
            if not n.lower().endswith((".vtex", ".vmat", ".vdata", ".vsndevts", ".vpcf")):
                continue
            src = os.path.join(content, n)
            cmd = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-f", "-game", os.path.join(CS2, "game", "csgo"), "-i", src]
            r = subprocess.run(cmd, cwd=BIN, capture_output=True, text=True, errors="replace")
            with open(os.path.join(out_dir, n + ".log"), "w", encoding="utf-8") as log:
                log.write(r.stdout)
                log.write(r.stderr)
            print(n, "rc exit", r.returncode)
        after = snapshot(game_addon)
        for p, t in after.items():
            if before.get(p) == t:
                continue
            rp = os.path.relpath(p, game_addon)
            if os.path.normcase(os.path.dirname(p)).startswith(os.path.normcase(game)):
                dst = os.path.join(out_dir, os.path.relpath(p, game))
                os.makedirs(os.path.dirname(dst), exist_ok=True)
                shutil.copy2(p, dst)
                print("kept", rp)
            else:
                # A material's children for shader default images land in materials/default.
                dst = os.path.join(out_dir, "_other", rp)
                os.makedirs(os.path.dirname(dst), exist_ok=True)
                shutil.copy2(p, dst)
                print("also written (kept under _other):", rp)
    finally:
        # Restore the addon: files RC overwrote come back from the backup, new ones go.
        after = snapshot(game_addon)
        for p in after:
            if p not in before:
                os.remove(p)
        for p in before:
            b = os.path.join(backup, os.path.relpath(p, game_addon))
            if not os.path.exists(p) or not filecmp.cmp(b, p, shallow=False):
                shutil.copy2(b, p)
            assert filecmp.cmp(b, p, shallow=False), p
        shutil.rmtree(backup)
        for p in copied:
            os.remove(p)
        # Folders this run made, once empty again.
        for made, d in ((made_content, content), (made_game, game)):
            if made and os.path.isdir(d) and not os.listdir(d):
                os.rmdir(d)
        print("addon restored")


if __name__ == "__main__":
    main()
