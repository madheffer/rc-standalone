"""harvest_names.py <dll> --out <names.json> [--scope <hex>...] -- name a stripped Valve DLL's functions.

Works from the PE alone, no disassembler database. Functions come from
.pdata, a chained unwind entry folded into the function it continues. Each
function is then named from the strongest evidence it carries:

  1. assert     an assert string "Class::Method(), <file>.cpp:<line>" it
                references (Valve compiles asserts with __FUNCTION__)
  2. qualified  a bare "Class::Method" string it references, when exactly one
                function references it (error and log messages)
  3. scope      the name a profiler or timing scope is opened with, a string
                passed in rdx to one of the --scope functions (in
                resourcecompiler 0923, 181373580 is the scope constructor)
  4. export     an export's name
  5. rtti       "Class::vf<slot>" for a function found in a class's vtable
                through RTTI, the most derived class that lists it first

References are found by scanning code for RIP-relative lea (48/4C 8D modrm
05-3D disp32) whose target starts a string. A byte match inside another
instruction could fake one; requiring the target to begin a printable,
NUL-terminated string keeps that rare, and every name keeps its evidence.

Output: {"dll", "imageBase", "functions": [{"addr", "name", "source",
"evidence"}], "counts"} sorted by address. tools/re/apply_names.py writes
them into the Ghidra project.
"""
import argparse
import bisect
import collections
import json
import re
import struct
import sys

import pefile

ASSERT = re.compile(rb"^([A-Za-z_][A-Za-z0-9_]*(?:::[~A-Za-z_][A-Za-z0-9_<>]*)+)\(\),\s*[A-Za-z]:[\\/][^\r\n]*?\.(?:cpp|h|inl):\d+")
QUALIFIED = re.compile(rb"^(C[A-Za-z0-9_]+(?:::[~A-Za-z_][A-Za-z0-9_]*)+)(?:\(\))?$")
STEP = re.compile(rb"^\s*(Building|Creating|Computing|Compiling|Writing|Generating|Baking|Merging|Saving|Loading|Converting|Processing|Finding|Collecting|Optimizing|Splitting|Assigning|Packing|Clustering|Tracing|Sorting|Removing|Welding|Culling|Placing) [A-Za-z][^%\n\r]{2,60}")
SCOPE_NAME = re.compile(rb"^[A-Za-z_][A-Za-z0-9_:]{2,79}$")


def functions(pe):
    """Function starts and ends from .pdata, chained entries folded into their parent."""
    base = pe.OPTIONAL_HEADER.ImageBase
    pe.parse_data_directories([pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_EXCEPTION"]])
    entries = list(getattr(pe, "DIRECTORY_ENTRY_EXCEPTION", []))
    by_start = {e.struct.BeginAddress: e for e in entries}
    chunks = []
    for e in entries:
        s, t = e.struct.BeginAddress, e.struct.EndAddress
        parent, cur = s, e
        for _ in range(16):
            u = getattr(cur, "unwindinfo", None)
            fe = getattr(u, "FunctionEntry", None) if u is not None and (u.Flags & 4) else None
            if fe is None:
                break
            parent = fe if isinstance(fe, int) else fe.BeginAddress
            cur = by_start.get(parent)
            if cur is None:
                break
        chunks.append((s, t, parent))
    chunks.sort()
    return base, chunks


def strings_at(img):
    """Offsets where a printable, NUL-terminated string of at least 3 bytes begins."""
    found = {}
    for m in re.finditer(rb"[\x20-\x7e]{3,300}\x00", img):
        found[m.start()] = m.group()[:-1]
    return found


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("dll")
    ap.add_argument("--out", required=True)
    ap.add_argument("--scope", nargs="*", default=[], help="hex addresses of scope functions taking a name in rdx")
    a = ap.parse_args()

    pe = pefile.PE(a.dll, fast_load=True)
    base, chunks = functions(pe)
    img = pe.get_memory_mapped_image()
    starts = [c[0] for c in chunks]

    def owner(rva):
        i = bisect.bisect_right(starts, rva) - 1
        if i >= 0 and chunks[i][0] <= rva < chunks[i][1]:
            return chunks[i][2]
        return None

    text = next(s for s in pe.sections if s.Name.rstrip(b"\0") == b".text")
    t0, t1 = text.VirtualAddress, text.VirtualAddress + text.Misc_VirtualSize
    strs = strings_at(img)
    scopes = {int(x, 16) - base for x in a.scope}

    refs = collections.defaultdict(set)      # string rva -> functions referencing it
    scope_names = collections.defaultdict(list)
    lea = re.compile(rb"[\x48\x4c]\x8d[\x05\x0d\x15\x1d\x25\x2d\x35\x3d]", re.S)
    code = img[t0:t1]
    for m in lea.finditer(code):
        at = t0 + m.start()
        disp = struct.unpack_from("<i", img, at + 3)[0]
        target = at + 7 + disp
        if target not in strs:
            continue
        f = owner(at)
        if f is None:
            continue
        refs[target].add(f)
        # rdx (reg field 2, REX.R clear): a name passed to a scope function
        # called within the next 24 bytes.
        if scopes and img[at] == 0x48 and img[at + 2] == 0x15:
            window = img[at + 7:at + 31]
            for k in range(len(window) - 4):
                if window[k] == 0xE8:
                    call = at + 7 + k + 5 + struct.unpack_from("<i", window, k + 1)[0]
                    if call in scopes:
                        scope_names[f].append(strs[target])
                        break

    names = {}

    def put(f, name, source, evidence, rank):
        cur = names.get(f)
        if cur is None or rank < cur[3]:
            names[f] = (name, source, evidence, rank)

    for s_rva, fs in refs.items():
        s = strs[s_rva]
        m = ASSERT.match(s)
        if m:
            for f in fs:
                put(f, m.group(1).decode(), "assert", s.decode(errors="replace")[:160], 1)
            continue
        m = QUALIFIED.match(s)
        if m and len(fs) == 1:
            n = m.group(1).decode()
            # A schema type name ("Class::Member_t") names the function that binds it.
            put(next(iter(fs)), ("SchemaBind_" + n) if n.endswith("_t") else n, "qualified", s.decode(errors="replace"), 2)
    # A progress message ("Building ray trace environment...") names the step
    # that prints it, when one function alone prints it.
    for s_rva, fs in refs.items():
        m = STEP.match(strs[s_rva])
        if m and len(fs) == 1:
            words = re.findall(rb"[A-Za-z0-9]+", m.group(0))[:6]
            put(next(iter(fs)), "Step_" + "".join(w.decode()[:1].upper() + w.decode()[1:] for w in words), "log", strs[s_rva].decode(errors="replace"), 3.5)
    for f, ns in scope_names.items():
        good = [n for n in ns if SCOPE_NAME.match(n)]
        if good:
            put(f, good[0].decode(), "scope", " | ".join(n.decode() for n in good[:4]), 3)

    pe.parse_data_directories([pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_EXPORT"]])
    for e in getattr(getattr(pe, "DIRECTORY_ENTRY_EXPORT", None), "symbols", []) or []:
        if e.name and t0 <= e.address < t1:
            put(owner(e.address) or e.address, e.name.decode(), "export", "", 4)

    # RTTI: CompleteObjectLocator (sig 1, x64) -> TypeDescriptor name; the
    # vtable is the qword after a pointer to the locator.
    rdata = bytes(img)
    classes = {}
    for m in re.finditer(rb"\.\?A[VU][^\x00]{1,200}@@\x00", rdata):
        td = m.start() - 16                       # TypeDescriptor: vftable, spare, name
        classes[td] = m.group()[:-1]
    cols = {}
    for m in re.finditer(rb"\x01\x00\x00\x00", rdata):
        off = m.start()
        if off % 4 or off + 24 > len(rdata):
            continue
        td = struct.unpack_from("<I", rdata, off + 12)[0]
        if td in classes:
            cols[base + off] = classes[td]
    # One pass over 8-aligned qwords: a pointer to a locator sits just before its vtable.
    col_ptrs = {}
    rd = rdata[: len(rdata) - len(rdata) % 8]
    words = struct.unpack("<%dQ" % (len(rd) // 8), rd)
    for i, w in enumerate(words):
        if w in cols:
            col_ptrs[(i + 1) * 8] = cols[w]
    for vt, mangled in sorted(col_ptrs.items()):
        cls = demangle_class(mangled)
        for slot in range(512):
            q = struct.unpack_from("<Q", rdata, vt + slot * 8)[0] - base
            if not (t0 <= q < t1):
                break
            if slot > 0 and (vt + slot * 8) in col_ptrs:
                break
            f = owner(q)
            if f is None or f != q:
                continue
            put(f, f"{cls}::vf{slot}", "rtti", f"vtable {base + vt:x} slot {slot}", 5)

    out = {
        "dll": a.dll.replace("\\", "/").split("/")[-1],
        "imageBase": hex(base),
        "counts": dict(collections.Counter(v[1] for v in names.values())),
        "functions": [
            {"addr": f"{base + f:x}", "name": n, "source": src, "evidence": ev}
            for f, (n, src, ev, _) in sorted(names.items())
        ],
    }
    with open(a.out, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(out, fh, indent=1)
    print(a.out, out["counts"], f"of {len(set(c[2] for c in chunks))} functions")


def demangle_class(m):
    """.?AVCFoo@Bar@@ -> Bar::CFoo; templates keep their mangled argument list."""
    body = m[4:-2].decode(errors="replace")
    if "?$" in body:
        return body.replace("@", "_")
    parts = [p for p in body.split("@") if p]
    return "::".join(reversed(parts))


if __name__ == "__main__":
    main()
