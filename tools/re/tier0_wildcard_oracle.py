"""tier0_wildcard_oracle.py: tier0's V_CompareNameWithWildcards(pattern, name, flag) on the cases the nav
link collector's search (resourcecompiler 180fc7220, flag 0) can meet: case, wildcards, empty names.

Loads the installed tier0.dll with ctypes (no CS2 or RC needed) and prints one line per case: the
pattern, the name, the flag and the return value (0 is a match).
"""
import ctypes
import os

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

CASES = [
    ("ai_nav_link_area", "ai_nav_link_area"),
    ("ai_nav_link_area", "AI_NAV_LINK_AREA"),
    ("ai_nav_link_area", "Ai_Nav_Link_Area"),
    ("ai_nav_link_area", "ai_nav_link_area2"),
    ("ai_nav_link_area", "ai_nav_link_are"),
    ("ai_nav_link_area", "ai_nav_link_area "),
    ("ai_nav_link_area", "*"),
    ("ai_nav_link_area", "ai_nav*"),
    ("ai_nav_link_area", ""),
]


def main():
    os.add_dll_directory(BIN)
    f = ctypes.CDLL(os.path.join(BIN, "tier0.dll")).V_CompareNameWithWildcards
    f.restype = ctypes.c_int
    f.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_int]
    for pattern, name in CASES:
        for flag in (0, 1):
            print(repr(pattern), repr(name), flag, f(pattern.encode(), name.encode(), flag))


if __name__ == "__main__":
    main()
