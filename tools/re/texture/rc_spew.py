"""rc_spew.py make | run <src> [args...] | remove: resourcecompiler that logs each generated .vtex's logical-CRC text.

docs/MATERIALS.md "The logical CRC": the child-naming function logs the text it hashes under
logicalCRCDebugSpew when a static flag is set, which nothing sets. `make` builds a side copy of
game/bin/win64 (hard links; resourcecompiler.dll a real copy) whose dll has the two branches that
skip the log replaced by NOPs. For the 20261103 build (sha256 14bea9c5...) those are file offsets
0x10b05c (`cmovz r9, rsi`, 4 bytes) and 0x10b148 (`je`, 6 bytes): the 20261009 build's 0x10afbc /
0x10b0a8 moved by 0xa0, found by their surrounding bytes. Any other build is refused. `run`
compiles with it (below-normal priority, -nop4 -f), `remove` deletes the side folder (only links
and the copy). The copy writes the same bytes as the stock dll (checked on the 10-09 build).
"""
import hashlib
import os
import shutil
import subprocess
import sys

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
SIDE = os.path.join(CS2, "game", "bin", "win64_s2cspew")
BUILD = "14bea9c54b59e7638cf15f846dda8fdba145e3a242eacc5a9f7eba0518afeb7c"
PATCHES = [(0x10b05c, bytes.fromhex("4c0f44ce")), (0x10b148, bytes.fromhex("0f8410010000"))]


def make():
    dll = os.path.join(BIN, "resourcecompiler.dll")
    if hashlib.sha256(open(dll, "rb").read()).hexdigest() != BUILD:
        raise SystemExit("resourcecompiler.dll is not the 20261103 build these offsets are for")
    if os.path.exists(SIDE):
        raise SystemExit(f"{SIDE} exists; remove it first")
    for d, dirs, files in os.walk(BIN):
        rel = os.path.relpath(d, BIN)
        os.makedirs(os.path.join(SIDE, rel), exist_ok=True)
        for f in files:
            src, dst = os.path.join(d, f), os.path.join(SIDE, rel, f)
            if rel == "." and f == "resourcecompiler.dll":
                shutil.copyfile(src, dst)
            else:
                os.link(src, dst)
    data = bytearray(open(os.path.join(SIDE, "resourcecompiler.dll"), "rb").read())
    for off, expect in PATCHES:
        assert data[off:off + len(expect)] == expect, hex(off)
        data[off:off + len(expect)] = b"\x90" * len(expect)
    open(os.path.join(SIDE, "resourcecompiler.dll"), "wb").write(data)
    print("made", SIDE)


def run(args):
    import ctypes
    ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)
    cmd = [os.path.join(SIDE, "resourcecompiler.exe"), "-nop4", "-f", "-game", os.path.join(CS2, "game", "csgo"), "-i"] + args
    r = subprocess.run(cmd, cwd=SIDE, capture_output=True, text=True, errors="replace")
    print(r.stdout)
    print(r.stderr)


def remove():
    if os.path.isdir(SIDE):
        shutil.rmtree(SIDE)
    print("removed", SIDE)


if __name__ == "__main__":
    {"make": lambda: make(), "run": lambda: run(sys.argv[2:]), "remove": lambda: remove()}[sys.argv[1]]()
