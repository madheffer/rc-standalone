"""map_installed.py <rva> [<rva> ...] [--dll resourcecompiler]: installed-build RVAs (a capture's return
addresses) to the 0923 analysis build: the function holding each (from .pdata) and its 0923 address when
patch_check's matcher finds the function there, with the offset inside it."""
import argparse
import os
import sys

import pefile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import patch_check as pc  # noqa: E402
from rva_map import installed  # noqa: E402


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("rvas", nargs="+")
    p.add_argument("--dll", default="resourcecompiler")
    a = p.parse_args()
    pe = pefile.PE(os.path.join(pc.BIN, pc.SUBDIR.get(a.dll, ""), a.dll + ".dll"), fast_load=True)
    pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_EXCEPTION"]])
    starts = sorted((e.struct.BeginAddress, e.struct.EndAddress) for e in pe.DIRECTORY_ENTRY_EXCEPTION)
    old = pc.Image(pc.baselines(a.dll)["20260923"])
    olds = sorted(set(old.functions)) if hasattr(old, "functions") else None
    for text in a.rvas:
        rva = int(text, 16)
        begin = next((b for b, e in starts if b <= rva < e), None)
        found = None
        if begin is not None:
            # The 0923 function the matcher maps onto this installed one, searched near it.
            for cand in range(begin - 0x4000, begin + 0x4000, 0x10):
                try:
                    if installed(a.dll, cand, build="20260923") == begin:
                        found = cand
                        break
                except BaseException:
                    pass
        print(f"{rva:#x}: installed fn {begin:#x}" if begin is not None else f"{rva:#x}: no function",
              f"-> 0923 18{found:07x} +{rva - begin:#x}" if found is not None else "-> 0923 not matched")


if __name__ == "__main__":
    main()
