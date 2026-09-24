"""harvest_names.py <dll> --out <names.json> [--scope <hex>...] -- name a stripped Valve DLL's functions.

Works from the PE alone, no disassembler database. Functions come from
.pdata, a chained unwind entry folded into the function it continues. Each
function is then named from the strongest evidence it carries:

  0. export     an export's name
  1. assert     an assert naming "Class::Method": the one-string form
                "Class::Method(), <file>.cpp:<line>", or the newer record whose
                file path, function and expression are separate strings
  2. qualified  a bare "Class::Method" string (error and log messages)
  3. scope      the name a profiler or timing scope is opened with, a string
                passed in rdx to one of the --scope functions (in
                resourcecompiler 0923, 181373580 is the scope constructor)
  3.5 log       a progress message, as "Step_..."
  4.5 inlined   "Inl_Class::Method", read "contains Class::Method": the name's
                record comes from a header (.h, .hpp, .inl), or several
                functions reference the string. Inlined code carries its
                asserts into every caller, so at most one of them, and often
                none, is the named function
  5. rtti       "Class::vf<slot>" for a function in a vtable, after the
                least-derived class holding it at that slot (the RTTI class
                hierarchy gives each class's bases); "Folded_Class::vf<slot>"
                when unrelated classes share it (identical-code folding)

A string name is then checked against the vtables holding the function. If
one holder (or a base) is the name's class, or an instantiation of that
template, the name stands, without Inl_: template instantiations share one
assert string. If none is, the vtable name wins and the string name goes
into its evidence as "contains ...".

References are found by scanning code for RIP-relative lea (48/4C 8D modrm
05-3D disp32) whose target starts a string. A byte match inside another
instruction could fake one; requiring the target to begin a printable,
NUL-terminated string keeps that rare, and every name keeps its evidence.

Output: {"dll", "imageBase", "functions": [{"addr", "name", "source",
"evidence"}], "counts"} sorted by address. tools/re/apply_names.py writes
them into the Ghidra project; tools/re/audit_names.py checks them.
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
PATH = re.compile(rb"^[A-Za-z]:[\\/][^\r\n]*\.(?:cpp|c|cc|h|hpp|inl)$", re.I)
HEADER = re.compile(r"\.(?:h|hpp|inl)\b", re.I)


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
    sites = collections.defaultdict(list)    # function -> [(lea rva, string rva)]
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
        sites[f].append((at, target))
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

    # The file path of the assert record a name string sits in: a path
    # string loaded within 48 bytes of it in the same function.
    def record_path(f, s_rva):
        near = [at for at, t in sites[f] if t == s_rva]
        for at, t in sites[f]:
            if PATH.match(strs[t]) and any(abs(at - n) <= 48 for n in near):
                return strs[t].decode(errors="replace")
        return None

    for s_rva, fs in refs.items():
        s = strs[s_rva]
        m = ASSERT.match(s)
        if m:
            n = m.group(1).decode()
            ev = s.decode(errors="replace")[:160]
            for f in fs:
                if len(fs) > 1 or HEADER.search(ev):
                    put(f, "Inl_" + n, "inlined", f"{ev} (referenced by {len(fs)})", 4.5)
                else:
                    put(f, n, "assert", ev, 1)
            continue
        m = QUALIFIED.match(s)
        if not m:
            continue
        n = m.group(1).decode()
        parts = n.split("::")
        if re.fullmatch(r"[A-Z0-9_]+", parts[-1]):
            continue    # a constant ("CBasePulsePort::CMD_RESET_..."), not a function
        # A schema type name ("Class::Member_t") or a nested class ("Class::CData",
        # not a constructor) names the function that binds the type.
        if n.endswith("_t") or (re.fullmatch(r"C[A-Z]\w*", parts[-1]) and parts[-1] != parts[-2]):
            # A type's binders come in small sets (the table, its constructor).
            if len(fs) <= 4:
                for f in fs:
                    put(f, "SchemaBind_" + n, "qualified", s.decode(errors="replace") + (f" (bound by {len(fs)})" if len(fs) > 1 else ""), 2)
            continue
        for f in fs:
            path = record_path(f, s_rva)
            ev = s.decode(errors="replace") + (f" in {path}" if path else "")
            if len(fs) > 1 or (path and HEADER.search(path)):
                put(f, "Inl_" + n, "inlined", f"{ev} (referenced by {len(fs)})", 4.5)
            elif path:
                put(f, n, "assert", ev, 1)
            else:
                put(f, n, "qualified", ev, 2)
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
            put(owner(e.address) or e.address, e.name.decode(), "export", "", 0)

    # RTTI: CompleteObjectLocator (sig 1, x64: offset, cdOffset, type
    # descriptor, class hierarchy descriptor); the vtable is the qword after
    # a pointer to the locator. The hierarchy descriptor's base class array
    # lists the class and every base.
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
        td, chd = struct.unpack_from("<II", rdata, off + 12)
        if td in classes:
            cols[base + off] = (td, chd)
    bases_of = {}
    for td, chd in cols.values():
        if td in bases_of:
            continue
        found = {td}
        if 0 < chd < len(rdata) - 16:
            n, bca = struct.unpack_from("<II", rdata, chd + 8)
            for i in range(min(n, 256)):
                if not 0 < bca + 4 * i < len(rdata) - 4:
                    break
                bcd = struct.unpack_from("<I", rdata, bca + 4 * i)[0]
                if 0 < bcd < len(rdata) - 4:
                    found.add(struct.unpack_from("<I", rdata, bcd)[0])
        bases_of[td] = found
    # One pass over 8-aligned qwords: a pointer to a locator sits just before its vtable.
    col_ptrs = {}
    rd = rdata[: len(rdata) - len(rdata) % 8]
    words = struct.unpack("<%dQ" % (len(rd) // 8), rd)
    for i, w in enumerate(words):
        if w in cols:
            col_ptrs[(i + 1) * 8] = cols[w]
    holders = collections.defaultdict(list)   # function -> [(type descriptor, slot, vtable)]
    for vt, (td, _) in sorted(col_ptrs.items()):
        for slot in range(512):
            q = struct.unpack_from("<Q", rdata, vt + slot * 8)[0] - base
            if not (t0 <= q < t1):
                break
            if slot > 0 and (vt + slot * 8) in col_ptrs:
                break
            f = owner(q)
            if f is None or f != q:
                continue
            holders[f].append((td, slot, vt))
    # A string name checked against the vtables that hold the function. The
    # holders' classes (and their bases) confirm it when one is the name's
    # class or an instantiation of that template: several instantiations
    # share one assert string, so the Inl_ mark comes off. A name whose class
    # no holder has only shows code inlined into another class's method.
    hint = {}

    def matches(cls, holder):
        return holder == cls or holder.endswith("::" + cls) or holder.startswith("?$" + cls.split("::")[-1] + "_")
    for f, hs in holders.items():
        cur = names.get(f)
        if cur is None or cur[1] not in ("assert", "qualified", "inlined"):
            continue
        plain = cur[0].removeprefix("Inl_")
        if "::" not in plain or plain.startswith("SchemaBind_"):
            continue
        cls = plain.rsplit("::", 1)[0]
        known = {demangle_class(classes[b]) for h in hs for b in bases_of.get(h[0], {h[0]}) if b in classes}
        if any(matches(cls, k) for k in known):
            names[f] = (plain, cur[1] if cur[1] != "inlined" else "assert", cur[2] + "; confirmed by vtable", 1)
        else:
            hint[f] = plain
            del names[f]    # the vtable name below; the string name only marks inlined code
    for f, hs in holders.items():
        tds = {h[0] for h in hs}
        slots = {h[1] for h in hs}
        # The least-derived holder: the class every other holder derives from.
        root = next((td for td in sorted(tds) if all(td in bases_of[o] for o in tds)), None)
        if root is not None and len(slots) == 1:
            slot = next(iter(slots))
            vt = next(h[2] for h in hs if h[0] == root)
            put(f, f"{demangle_class(classes[root])}::vf{slot}", "rtti",
                f"vtable {base + vt:x} slot {slot}; held by {len(tds)} classes" + (f"; contains {hint[f]}" if f in hint else ""), 5)
        else:
            td, slot, vt = min(hs, key=lambda h: (len(bases_of[h[0]]), h[2]))
            put(f, f"Folded_{demangle_class(classes[td])}::vf{slot}", "rtti",
                f"vtable {base + vt:x} slot {slot}; shared by {len(tds)} unrelated classes at slots {sorted(slots)[:6]}"
                + (f"; contains {hint[f]}" if f in hint else ""), 5)

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
