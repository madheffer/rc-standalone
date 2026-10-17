"""Where the reverse-engineering tools read and write.

Everything we write lives in this repository: the scripts here and in
tools/ghidra_scripts, the name lists, and (gitignored) the copies of Valve's
DLL builds, patch_check's state and reports, and tool output. Only the
third-party installs stay outside: Ghidra, its project databases and the
pyghidra venv, under S2C_GHIDRA_ROOT (default D:/tools).
"""
import os

RE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(RE))

# Copies of each analysed DLL build, <dll>_<yyyymmdd>.dll (gitignored).
BINARIES = os.environ.get("S2C_BINARIES", os.path.join(RE, "binaries"))
# Builds imported into Ghidra under their original names.
STAGING = os.path.join(BINARIES, "staging")
NAMES = os.path.join(RE, "names")
PATCH_STATE = os.path.join(RE, "patch_state")
PATCH_REPORTS = os.path.join(RE, "patch_reports")
# Tool output: logs, decompile dumps (gitignored).
OUT = os.path.join(RE, "out")
DUMPS = os.path.join(OUT, "dumps")
REVA_CALL = os.path.join(RE, "reva_call.py")
GHIDRA_SCRIPTS = os.path.join(REPO, "tools", "ghidra_scripts")

GHIDRA_ROOT = os.environ.get("S2C_GHIDRA_ROOT", r"D:\tools")
GHIDRA_INSTALL = os.environ.get("GHIDRA_INSTALL_DIR", os.path.join(GHIDRA_ROOT, "ghidra_12.1.3_PUBLIC"))
GHIDRA_PROJECTS = os.path.join(GHIDRA_ROOT, "ghidra_projects")
REVA_PYTHON = os.path.join(GHIDRA_ROOT, "reva-venv", "Scripts", "python.exe")


def binary(name):
    """A DLL by file name (resourcecompiler_20260923.dll) or alias (rc), else the path as given."""
    name = ALIASES.get(name, name)
    for d in (BINARIES, STAGING):
        p = os.path.join(d, name)
        if os.path.exists(p):
            return p
    return name


# Short names for the builds most read, as ReVa programs and as files.
ALIASES = {
    "rc": "resourcecompiler_20260923.dll",
    "vis": "visbuilder_20260923.dll",
    "ms": "meshsystem_20260923.dll",
    "ms1001": "meshsystem_20261001.dll",
    "pb": "physicsbuilder_20260924.dll",
    "pv": "vphysics2_20260924.dll",
    "sp": "smartprops_20260923.dll",
    "t0": "tier0_20260923.dll",
    "vrad3": "vrad3_20261001.dll",
    "ss": "scenesystem_20261015.dll",
    "mat": "materialsystem2_20261015.dll",
}
