"""package_guard.py: move a map's compiled package aside for a capture, and put it back.

A current .vpk makes resourcecompiler skip the compile, so the capture tools
remove it first. Other tools and tests read that package as Valve's reference
output, so it is copied to a backup before it goes and copied back, byte
checked, once the compile process has exited, whether the capture finished
or failed:

    with PackageGuard(vpk, out_path + ".vpk.bak") as guard:
        pid = device.spawn(...)
        guard.pid = pid
        ...

On the way out it waits for guard.pid to exit (killing it when the block
raised, so a suspended compile cannot hold the package), then restores. The
backup is deleted only when the restored copy compares equal; otherwise it is
kept and its path printed.
"""
import filecmp
import os
import shutil
import subprocess
import time


def alive(pid):
    out = subprocess.run(["tasklist", "/FI", "PID eq %d" % pid, "/NH"], capture_output=True, text=True).stdout
    return any(line.split()[1:2] == [str(pid)] for line in out.splitlines() if line.strip())


class PackageGuard:
    def __init__(self, vpk, backup):
        self.vpk = vpk
        self.backup = backup
        self.pid = None
        self.moved = False

    def __enter__(self):
        if os.path.exists(self.vpk):
            os.makedirs(os.path.dirname(self.backup) or ".", exist_ok=True)
            shutil.copyfile(self.vpk, self.backup)
            if not filecmp.cmp(self.vpk, self.backup, shallow=False):
                raise SystemExit("backup of %s did not compare equal; not starting" % self.vpk)
            os.remove(self.vpk)
            self.moved = True
        return self

    def __exit__(self, kind, value, trace):
        if self.pid is not None:
            if kind is not None and alive(self.pid):
                subprocess.run(["taskkill", "/F", "/PID", str(self.pid)], capture_output=True)
            while alive(self.pid):
                time.sleep(1)
        if not self.moved:
            return False
        shutil.copyfile(self.backup, self.vpk)
        if filecmp.cmp(self.backup, self.vpk, shallow=False):
            os.remove(self.backup)
            print("restored", self.vpk)
        else:
            print("RESTORE CMP FAILED", self.vpk, "backup kept at", self.backup)
        return False
