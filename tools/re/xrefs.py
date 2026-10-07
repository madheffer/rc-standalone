"""xrefs.py <dll> --out <json>: every function's calls and RIP-relative references, from the PE alone.

Functions come from .pdata (a chained unwind entry folded into the function it
continues, as harvest_names.py does). Each is disassembled with capstone; the
index keeps, per function, its end, the direct call and jump targets outside
it, the RIP-relative memory operands (data, vtables, strings, imports, by
target) and the ASCII strings those land on. Load it with xrefs.load() to ask
who calls a function, who references an address or a string.

    python tools/re/xrefs.py D:/tools/binaries/vrad3_20261001.dll --out %TEMP%/vrad3re/xrefs.json
"""
import argparse
import json
import struct

import capstone
import pefile
from capstone.x86 import X86_OP_IMM, X86_OP_MEM


def functions(pe):
    base = pe.OPTIONAL_HEADER.ImageBase
    out = {}
    chained = {}
    for e in pe.DIRECTORY_ENTRY_EXCEPTION:
        s, t, u = e.struct.BeginAddress, e.struct.EndAddress, e.struct.UnwindData
        out[s] = t
        # A chained unwind (flag 4) continues the function whose RUNTIME_FUNCTION it ends with.
        info = pe.get_data(u, 4)
        if info and (info[0] >> 3) & 4:
            count = info[2]
            at = u + 4 + ((count + 1) & ~1) * 2
            parent = struct.unpack("<I", pe.get_data(at, 4))[0]
            chained[s] = parent
    def root(s):
        seen = set()
        while s in chained and s not in seen:
            seen.add(s)
            s = chained[s]
        return s
    ranges = {}
    for s, t in out.items():
        ranges.setdefault(root(s), []).append((base + s, base + t))
    return {base + k: sorted(v) for k, v in ranges.items()}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("dll")
    p.add_argument("--out", required=True)
    a = p.parse_args()
    pe = pefile.PE(a.dll, fast_load=True)
    pe.parse_data_directories([1, 3])
    base = pe.OPTIONAL_HEADER.ImageBase
    img = pe.get_memory_mapped_image()
    imports = {i.address: f"{d.dll.decode()}!{i.name.decode() if i.name else i.ordinal}"
               for d in getattr(pe, "DIRECTORY_ENTRY_IMPORT", []) for i in d.imports}
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True

    def string_at(va):
        o = va - base
        if not (0 <= o < len(img)):
            return None
        t = img[o:o + 200].split(b"\0")[0]
        return t.decode() if len(t) >= 3 and all(32 <= c < 127 or c in (9, 10, 13) for c in t) else None

    index = {}
    for start, parts in functions(pe).items():
        calls, refs, strings = set(), set(), {}
        for lo, hi in parts:
            for i in md.disasm(img[lo - base:hi - base], lo):
                for op in i.operands:
                    if op.type == X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP:
                        t = i.address + i.size + op.mem.disp
                        if t in imports:
                            calls.add(imports[t])
                        else:
                            refs.add(t)
                            if (s := string_at(t)) is not None:
                                strings[f"{t:x}"] = s
                    elif op.type == X86_OP_IMM and i.mnemonic in ("call", "jmp") and not (start <= op.imm < parts[-1][1]):
                        calls.add(f"{op.imm:x}")
        index[f"{start:x}"] = {"end": f"{parts[-1][1]:x}", "calls": sorted(calls), "refs": sorted(f"{r:x}" for r in refs), "strings": strings}
    with open(a.out, "w") as f:
        json.dump({"dll": a.dll, "base": f"{base:x}", "functions": index}, f)
    print(len(index), "functions ->", a.out)


def load(path):
    """The index, plus reverse maps: callers[target] and referers[address]."""
    d = json.load(open(path))
    callers, referers = {}, {}
    for f, v in d["functions"].items():
        for c in v["calls"]:
            callers.setdefault(c, []).append(f)
        for r in v["refs"]:
            referers.setdefault(r, []).append(f)
    return d, callers, referers


if __name__ == "__main__":
    main()
