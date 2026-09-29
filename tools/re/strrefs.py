"""strrefs.py <dll> <regex> [<regex>...]: every function (.pdata) that loads a string matching a regex.

Offline (pefile + capstone, no Ghidra): the ASCII strings of the image that match
are collected, then every function's code is scanned for RIP-relative operands
landing on one. Prints function start (chained unwind chunks resolved to their function),
instruction address and the string, with
the name tools/re/names holds for the function when there is one.

    python tools/re/strrefs.py resourcecompiler.dll "env_cubemap" "BuildMapEnvMaps"
"""
import json
import os
import re
import sys

import capstone
import pefile

GAME = r"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64"


def names(dll):
    here = os.path.join(os.path.dirname(__file__), "names")
    stem = os.path.splitext(dll)[0]
    table = {}
    for path in sorted(os.listdir(here)):
        if path.startswith(stem + "_") and path.endswith(".json"):
            for f in json.load(open(os.path.join(here, path), encoding="utf-8"))["functions"]:
                table[int(f["addr"], 16)] = f["name"]
    return table


def main():
    dll = sys.argv[1]
    patterns = [re.compile(p.encode(), re.I) for p in sys.argv[2:]]
    pe = pefile.PE(os.path.join(GAME, dll), fast_load=True)
    pe.parse_data_directories([3])
    base = pe.OPTIONAL_HEADER.ImageBase
    image = pe.get_memory_mapped_image()
    strings = {}
    for m in re.finditer(rb"[\x20-\x7e]{4,300}\x00", image):
        text = m.group()[:-1]
        if any(p.search(text) for p in patterns):
            strings[base + m.start()] = text.decode()
    table = names(dll)

    def primary(begin, unwind):
        # A chained unwind entry (UNW_FLAG_CHAININFO) names its parent's
        # RUNTIME_FUNCTION after the unwind codes: follow it to the real start.
        for _ in range(16):
            flags = image[unwind] >> 3
            if not flags & 4:
                return begin
            count = image[unwind + 2]
            at = unwind + 4 + ((count + 1) & ~1) * 2
            begin = int.from_bytes(image[at:at + 4], "little")
            unwind = int.from_bytes(image[at + 8:at + 12], "little")
        return begin
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    hits = []
    for e in pe.DIRECTORY_ENTRY_EXCEPTION:
        s, t = e.struct.BeginAddress, e.struct.EndAddress
        for x in md.disasm(image[s:t], base + s):
            for op in x.operands:
                if op.type == capstone.x86.X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP:
                    target = x.address + x.size + op.mem.disp
                    if target in strings:
                        hits.append((base + primary(s, e.struct.UnwindData), x.address, strings[target]))
    for fn, at, text in sorted(set(hits)):
        print(f"{fn:x} {table.get(fn, '')} @{at:x}: {text[:140]}")


if __name__ == "__main__":
    main()
