"""build_diff.py <old.dll> <new.dll> [--names report.md] [--out file]: which functions a new build really changed.

Every function (.pdata primary chunk) of both builds is masked as patch_check
masks it (RIP-relative displacements and rel32 branch targets zeroed) and
hashed. Functions whose masked code occurs in both builds are the same code,
however far they moved. The rest are paired in address order between unique
matches (anchors): an old function and a new one at the same place in their
run are taken as the same function, changed. Printed: counts, then each
changed pair with its sizes and how many masked bytes differ, and the
functions only one build has.

--names takes a patch report (patch_check's .md) and labels old functions
with the tracked names its "moved"/"identical" rows place in the old build.
"""
import argparse
import bisect
import collections
import hashlib
import os
import re
import sys

import pefile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from patch_check import Image  # noqa: E402


def hashes(img):
    out = {}
    for s in sorted(img.bounds):
        body, _ = img.masked(s)
        out[s] = hashlib.sha1(body).hexdigest()
    return out


def names_from_report(path):
    """{old address: name} from a patch report's rows (the address the name has in the installed build)."""
    found = {}
    for line in open(path, encoding="utf-8"):
        m = re.match(r"\s*- (moved|identical|relocated): ([0-9a-f]+) (\S+) .*?-> ([0-9a-f]+)", line)
        if m:
            found[int(m.group(4), 16)] = m.group(3)
            continue
        m = re.match(r"\s*- (identical|relocated): ([0-9a-f]+) (\S+)", line)
        if m:
            found[int(m.group(2), 16)] = m.group(3)
    return found


def relocation_only(old, new, x, y):
    """Whether two same-length functions differ only in image-relative constants:
    instruction for instruction the same mnemonics and operand shapes, the
    numbers that differ both within the image (switch-table RVAs and the like),
    up to the end or to where decoding loses step after a ret or jmp (a jump
    table of RVAs inline after the code)."""
    if old.bounds[x] - x != new.bounds[y] - y:
        return False
    size = old.bounds[x] - x
    oi = list(old.md.disasm(bytes(old.img[x:x + size]), x))
    ni = list(new.md.disasm(bytes(new.img[y:y + size]), y))
    def table_tail(img, start, end):
        # From the first 4-aligned address at or before the desync: image offsets or int3 padding.
        at = start - (start % 4)
        values = [int.from_bytes(bytes(img[k:k + 4]), "little") for k in range(at, end - 3, 4)]
        return all(v == 0xcccccccc or 0x1000 <= v < 0x10000000 or (v & 0xffffff00) == 0xcccccc00 or v < 0x100 for v in values)

    for a, b in zip(oi, ni):
        if a.mnemonic != b.mnemonic or a.size != b.size:
            return table_tail(old.img, a.address, x + size) and table_tail(new.img, b.address, y + size)
        if a.op_str == b.op_str or "rip" in a.op_str or a.mnemonic == "call" or a.mnemonic.startswith("j"):
            continue
        if re.sub(r"0x[0-9a-f]+", "N", a.op_str) != re.sub(r"0x[0-9a-f]+", "N", b.op_str):
            return False
        for u, v in zip(re.findall(r"0x[0-9a-f]+", a.op_str), re.findall(r"0x[0-9a-f]+", b.op_str)):
            if u != v and not (0x1000 <= int(u, 16) < 0x10000000 and 0x1000 <= int(v, 16) < 0x10000000):
                return False
    if len(oi) != len(ni):
        k = min(len(oi), len(ni))
        tail_o = oi[k].address if k < len(oi) else x + size
        tail_n = ni[k].address if k < len(ni) else y + size
        return table_tail(old.img, tail_o, x + size) and table_tail(new.img, tail_n, y + size)
    return True


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("old")
    p.add_argument("new")
    p.add_argument("--names")
    p.add_argument("--out")
    a = p.parse_args()
    old, new = Image(a.old), Image(a.new)
    ho, hn_ = hashes(old), hashes(new)
    count_o = collections.Counter(ho.values())
    count_n = collections.Counter(hn_.values())
    same = sum(min(c, count_n[h]) for h, c in count_o.items())
    # Anchors: code unique in both builds.
    unique_new = {h: s for s, h in hn_.items() if count_n[h] == 1}
    anchors = sorted((s, unique_new[h]) for s, h in ho.items() if count_o[h] == 1 and h in unique_new)
    # Keep anchors increasing in both builds (drop crossings).
    kept = []
    for o, n in anchors:
        if not kept or n > kept[-1][1]:
            kept.append((o, n))
    olds = sorted(ho)
    news = sorted(hn_)
    shared = set(ho.values()) & set(hn_.values())
    changed, only_old, only_new = [], [], []
    bounds = [(-1, -1)] + kept + [(1 << 40, 1 << 40)]
    for (o0, n0), (o1, n1) in zip(bounds, bounds[1:]):
        run_o = [s for s in olds[bisect.bisect_right(olds, o0):bisect.bisect_left(olds, o1)] if ho[s] not in shared]
        run_n = [s for s in news[bisect.bisect_right(news, n0):bisect.bisect_left(news, n1)] if hn_[s] not in shared]
        for x, y in zip(run_o, run_n):
            bo, _ = old.masked(x)
            bn, _ = new.masked(y)
            diff = sum(1 for u, v in zip(bo, bn) if u != v) + abs(len(bo) - len(bn))
            changed.append((x, y, len(bo), len(bn), diff, relocation_only(old, new, x, y)))
        only_old += run_o[len(run_n):]
        only_new += run_n[len(run_o):]
    labels = names_from_report(a.names) if a.names else {}
    real = [c for c in changed if not c[5]]
    lines = [f"{a.old} -> {a.new}",
             f"functions: old {len(ho)}, new {len(hn_)}; same code {same}; changed {len(changed)} "
             f"({len(changed) - len(real)} only in image-relative constants, {len(real)} real); only old {len(only_old)}; only new {len(only_new)}"]
    for x, y, lo, ln, d, reloc in sorted(changed, key=lambda r: (r[5], -r[4])):
        name = labels.get(old.base + x, "")
        lines.append(f"  {'relocated' if reloc else 'changed'} {old.base + x:#x} -> {new.base + y:#x}  {lo} -> {ln} bytes, {d} masked bytes differ  {name}")
    for x in only_old:
        lines.append(f"  only old {old.base + x:#x} ({old.bounds[x] - x} bytes) {labels.get(old.base + x, '')}")
    for y in only_new:
        lines.append(f"  only new {new.base + y:#x} ({new.bounds[y] - y} bytes)")
    text = "\n".join(lines)
    if a.out:
        open(a.out, "w", encoding="utf-8").write(text + "\n")
    print("\n".join(lines[:2]))
    if not a.out:
        print("\n".join(lines[2:60]))


if __name__ == "__main__":
    main()
