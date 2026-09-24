"""audit_names.py <dll> <names.json> [--manual <json>...] -- find harvested names that are likely wrong.

For every function harvest_names.py named from a string, this lists every
name-like string the function references, and flags these cases:

  shared-assert   the same assert string names several functions: it was
                  inlined into callers, and at most one of them is the named one
  header-assert   the assert comes from a .h/.inl file, so the named function
                  was probably inlined here
  many-names      the function references strings naming several different
                  functions (a caller, or a dispatcher)
  label           a qualified string that is only passed along as an argument
                  (a job, scope or log label), not the function's own name
  vtable-class    the function also sits in a vtable of a class that the name's
                  class is not
  manual          a hand-kept name for the same address disagrees

Writes a JSON report next to the names file (<names>.audit.json) and prints a
summary.
"""
import argparse
import collections
import json
import os
import re
import struct
import sys

import pefile

sys.path.insert(0, os.path.dirname(__file__))
import harvest_names as hn  # noqa: E402

FUNCNAME = re.compile(rb"^([A-Za-z_][A-Za-z0-9_]*(?:::[~A-Za-z_][A-Za-z0-9_<>]*)+)")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("dll")
    ap.add_argument("names")
    ap.add_argument("--manual", nargs="*", default=[])
    a = ap.parse_args()

    pe = pefile.PE(a.dll, fast_load=True)
    base, chunks = hn.functions(pe)
    img = pe.get_memory_mapped_image()
    import bisect
    starts = [c[0] for c in chunks]

    def owner(rva):
        i = bisect.bisect_right(starts, rva) - 1
        if i >= 0 and chunks[i][0] <= rva < chunks[i][1]:
            return chunks[i][2]
        return None

    text = next(s for s in pe.sections if s.Name.rstrip(b"\0") == b".text")
    t0, t1 = text.VirtualAddress, text.VirtualAddress + text.Misc_VirtualSize
    strs = hn.strings_at(img)
    refs = collections.defaultdict(set)          # string rva -> functions
    uses = collections.defaultdict(list)         # function -> [(string rva, lea rva, reg)]
    lea = re.compile(rb"[\x48\x4c]\x8d[\x05\x0d\x15\x1d\x25\x2d\x35\x3d]", re.S)
    code = img[t0:t1]
    for m in lea.finditer(code):
        at = t0 + m.start()
        target = at + 7 + struct.unpack_from("<i", img, at + 3)[0]
        if target not in strs:
            continue
        f = owner(at)
        if f is None:
            continue
        refs[target].add(f)
        reg = ((img[at + 2] >> 3) & 7) + (8 if img[at] == 0x4C else 0)
        uses[f].append((target, at, reg))

    # RTTI: function -> classes whose vtables hold it.
    vt_classes = collections.defaultdict(set)
    rdata = bytes(img)
    classes = {}
    for m in re.finditer(rb"\.\?A[VU][^\x00]{1,200}@@\x00", rdata):
        classes[m.start() - 16] = m.group()[:-1]
    cols = {}
    for m in re.finditer(rb"\x01\x00\x00\x00", rdata):
        off = m.start()
        if off % 4 or off + 24 > len(rdata):
            continue
        td = struct.unpack_from("<I", rdata, off + 12)[0]
        if td in classes:
            cols[base + off] = classes[td]
    rd = rdata[: len(rdata) - len(rdata) % 8]
    words = struct.unpack("<%dQ" % (len(rd) // 8), rd)
    col_ptrs = {(i + 1) * 8: cols[w] for i, w in enumerate(words) if w in cols}
    for vt, mangled in col_ptrs.items():
        cls = hn.demangle_class(mangled)
        for slot in range(512):
            q = struct.unpack_from("<Q", rdata, vt + slot * 8)[0] - base
            if not (t0 <= q < t1) or (slot > 0 and (vt + slot * 8) in col_ptrs):
                break
            if owner(q) == q:
                vt_classes[q].add(cls)

    names = json.load(open(a.names, encoding="utf-8"))["functions"]
    manual = {}
    for path in a.manual:
        for f in json.load(open(path, encoding="utf-8"))["functions"]:
            manual[int(f["addr"], 16) - base] = f["name"]

    report = []
    flags = collections.Counter()
    for f in names:
        if f["source"] == "rtti":
            continue
        rva = int(f["addr"], 16) - base
        issues = []
        mentioned = collections.OrderedDict()
        for s_rva, lea_at, reg in uses.get(rva, []):
            m = FUNCNAME.match(strs[s_rva])
            if m:
                mentioned.setdefault(m.group(1).decode(), (s_rva, lea_at, reg))
        name = f["name"]
        bare = name.removeprefix("Inl_").removeprefix("SchemaBind_")
        if f["source"] == "assert":
            s_rva = next((s for s, _, _ in uses.get(rva, []) if strs[s].startswith(name.encode() + b"()")), None)
            if s_rva is not None and len(refs[s_rva]) > 1:
                issues.append(f"shared-assert x{len(refs[s_rva])}")
            if re.search(r"\.(h|inl):\d+", f["evidence"]):
                issues.append("header-assert")
        others = [n for n in mentioned if n != bare]
        if others:
            issues.append("many-names: " + ", ".join(others[:6]))
        if f["source"] == "qualified" and bare in mentioned:
            _, lea_at, reg = mentioned[bare]
            # rcx/rdx/r8/r9 loaded right before a call: passed as an argument.
            window = img[lea_at + 7:lea_at + 7 + 40]
            if reg in (1, 2, 8, 9) and 0xE8 in window[:24]:
                issues.append(f"label (argument in {['rax','rcx','rdx','rbx','rsp','rbp','rsi','rdi','r8','r9'][reg] if reg < 10 else reg})")
        cls = bare.rsplit("::", 1)[0] if "::" in bare and f["source"] != "inlined" else None
        vcls = vt_classes.get(rva)
        if cls and vcls and not any(c == cls or c.endswith("::" + cls) for c in vcls):
            issues.append("vtable-class: " + ", ".join(sorted(vcls)[:4]))
        if rva in manual and manual[rva].replace("::", "_") != name.replace("::", "_"):
            issues.append(f"manual: {manual[rva]}")
        for i in issues:
            flags[i.split(":")[0].split(" x")[0].split(" (")[0]] += 1
        report.append({"addr": f["addr"], "name": name, "source": f["source"], "issues": issues,
                       "mentions": list(mentioned), "vtables": sorted(vcls or [])})

    out = os.path.splitext(a.names)[0] + ".audit.json"
    json.dump({"dll": os.path.basename(a.dll), "flags": flags, "functions": report}, open(out, "w", encoding="utf-8"), indent=1)
    print(out, dict(flags), f"{sum(1 for r in report if r['issues'])} of {len(report)} flagged")


if __name__ == "__main__":
    main()
