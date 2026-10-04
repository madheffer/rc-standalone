"""rva_map.py: the installed build's RVA for an address recorded on an analysis build.

    from rva_map import installed
    rva = installed("resourcecompiler", 0x10c65c0)            # 0923 RVA -> installed RVA

Uses patch_check's matcher (masked function bodies over .pdata): an address
whose function is identical, relocated or moved maps to its installed place;
one whose code changed, or that is ambiguous or missing, raises, so a capture
script never hooks the middle of some other function after a game update.

    python tools/re/rva_map.py resourcecompiler 0x10c65c0 0x13baa40
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import patch_check as pc  # noqa: E402

ANALYSIS = "20260923"
# The analysis build each DLL's names were recorded on.
BUILDS = {"physicsbuilder": "20260924", "vphysics2": "20260924"}
_images = {}


def _image(path):
    if path not in _images:
        _images[path] = pc.Image(path)
    return _images[path]


def installed(dll, rva, build=None):
    """The installed RVA of the function at <rva> in <dll>_<build>.dll."""
    build = build or BUILDS.get(dll, ANALYSIS)
    found = pc.baselines(dll)
    if build not in found:
        raise SystemExit(f"no {dll}_{build}.dll in {pc.BASELINE_DIRS}")
    old = _image(found[build])
    new = _image(os.path.join(pc.BIN, pc.SUBDIR.get(dll, ""), dll + ".dll"))
    status, at, note = pc.compare(old, new, rva)
    if status not in ("identical", "relocated", "moved") or at is None:
        raise SystemExit(f"{dll} 0x{rva:x}: {status} in the installed build ({note}); re-read it before hooking")
    return at


def require(dll, rvas, build=None):
    """Exit unless every address in <rvas> is where the analysis build had it.

    For a capture script whose hooks, return addresses or globals are
    literals: on a build that moved any of them it stops, naming each one's
    new place, rather than hook the wrong code."""
    moved = []
    for rva in rvas:
        try:
            at = installed(dll, rva, build)
        except SystemExit as e:
            moved.append(str(e))
            continue
        if at != rva:
            moved.append(f"{dll} 0x{rva:x} is now 0x{at:x}")
    if moved:
        raise SystemExit("this script's addresses do not match the installed build; update them first:\n  "
                         + "\n  ".join(moved))


def rebase(agent):
    """A Frida agent with each m.base.add(0x...) moved to the installed build.

    The DLL of each literal is the one named by the nearest findModuleByName
    before it in the text, as the capture scripts are written."""
    import re
    out, at, dll = [], 0, None
    for m in re.finditer(r"findModuleByName\('([a-z0-9_]+)\.dll'\)|base\.add\((0x[0-9a-fA-F]+)\)", agent):
        out.append(agent[at:m.start()])
        if m.group(1):
            dll = m.group(1)
            out.append(m.group(0))
        else:
            if dll is None:
                raise SystemExit("rebase: base.add before any findModuleByName")
            out.append(f"base.add(0x{installed(dll, int(m.group(2), 16)):x})")
        at = m.end()
    out.append(agent[at:])
    return "".join(out)


if __name__ == "__main__":
    for a in sys.argv[2:]:
        r = int(a, 16)
        print(f"0x{r:x} -> 0x{installed(sys.argv[1], r):x}")
