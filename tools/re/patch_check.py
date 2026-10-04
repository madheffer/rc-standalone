"""patch_check.py [--no-state] -- what a CS2 patch changed that the port depends on.

Run after every CS2 update. It checks three things and writes a report to
D:/tools/patch_reports/<date>_<patch>.md, exiting 2 when something the port
depends on needs attention:

1. The toolchain DLLs (resourcecompiler, vphysics2, physicsbuilder, tier0,
   visbuilder). Each installed DLL is hashed and compared with the builds in
   D:/tools/binaries (and D:/tools/ghidra_staging); a new build is copied
   there as <dll>_<yyyymmdd>.dll so it can be analysed.

2. Every address the port relies on, found again in the installed build:
   - hand-kept names (tools/re/names/<dll>_<build>.manual.json),
   - the entity side's lists (D:/tools/names/*_<dll>.txt, the analysis build),
   - script RVAs (tools/re/tracked_rvas.json; script RVAs not listed there
     are reported so the list stays complete),
   - addresses cited next to a DLL's name in src/ comments.
   Each function is taken from the build it was recorded against (its .pdata
   entry), its RIP-relative displacements and branch targets masked, and
   compared with the installed build:
     identical   same RVA, same bytes
     relocated   same RVA, same code, only relocated operands differ
     moved       same code at another RVA (the new address is given)
     changed     no function with the same code; the code needs re-reading
                 (candidates matching its first 48 bytes are listed)
     missing     nothing matches
   Any address that is not identical or relocated means the scripts, names
   or ports that use it must be updated.

3. Game data the compile reads: the FGDs (hash) and the pak01 archives
   (every entry's CRC). The state is kept in D:/tools/patch_state; each run
   diffs against the last one and saves its own (--no-state skips saving).
"""
import argparse
import datetime
import glob
import hashlib
import json
import os
import re
import shutil
import struct
import sys

import capstone
import pefile

sys.path.insert(0, os.path.dirname(__file__))
import harvest_names as hn  # noqa: E402

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
# rendersystemvulkan and rendersystemdx11: the material sampler's render (texture preload cap).
DLLS = ["resourcecompiler", "vphysics2", "physicsbuilder", "tier0", "visbuilder", "smartprops", "hammer",
        "rendersystemvulkan", "rendersystemdx11"]
# DLLs not directly in bin/win64: Hammer's build dialog (compile-map follows its command line).
SUBDIR = {"hammer": "tools"}
BASELINE_DIRS = [r"D:\tools\binaries", r"D:\tools\ghidra_staging"]
ENTITY_NAMES = r"D:\tools\names"
STATE_DIR = r"D:\tools\patch_state"
REPORT_DIR = r"D:\tools\patch_reports"
BASE = 0x180000000


def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def baselines(dll):
    """{build: path} of the builds we keep for a DLL."""
    found = {}
    for d in BASELINE_DIRS:
        for p in glob.glob(os.path.join(d, f"{dll}_*.dll")):
            found.setdefault(os.path.splitext(os.path.basename(p))[0].split("_")[-1], p)
    return found


class Image:
    """A PE's functions (.pdata primary chunks) and code, with masked bodies."""

    def __init__(self, path):
        pe = pefile.PE(path, fast_load=True)
        self.base, chunks = hn.functions(pe)
        self.img = pe.get_memory_mapped_image()
        self.bounds = {s: t for s, t, p in chunks if s == p}
        self.by_length = {}
        for s, t in self.bounds.items():
            self.by_length.setdefault(t - s, []).append(s)
        text = next(s for s in pe.sections if s.Name.rstrip(b"\0") == b".text")
        self.text = (text.VirtualAddress, text.VirtualAddress + text.Misc_VirtualSize)
        self.md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        self.md.detail = True
        self.cache = {}

    def masked(self, rva, length=None):
        """The function's bytes with RIP-relative displacements and rel32 targets zeroed, and the mask."""
        end = rva + length if length else self.bounds.get(rva)
        if end is None:
            return None, None
        key = (rva, end)
        if key in self.cache:
            return self.cache[key]
        raw = bytes(self.img[rva:end])
        mask = bytearray(b"\x01" * len(raw))
        for ins in self.md.disasm(raw, rva):
            off = ins.address - rva
            if ins.disp_offset and "rip" in ins.op_str:
                for k in range(4):
                    if off + ins.disp_offset + k < len(mask):
                        mask[off + ins.disp_offset + k] = 0
            if (ins.mnemonic in ("call", "jmp") or ins.mnemonic.startswith("j")) and ins.size >= 5:
                for k in range(ins.size - 4, ins.size):
                    if off + k < len(mask):
                        mask[off + k] = 0
        body = bytes(b if m else 0 for b, m in zip(raw, mask))
        self.cache[key] = (body, bytes(mask))
        return self.cache[key]


def compare(old, new, rva):
    """Where old's function at rva is in new, and how it differs."""
    if rva not in old.bounds:
        # Inside a function, a leaf function without unwind data, or data:
        # compare a 64-byte window at the address (masked as code).
        body, _ = old.masked(rva, 64)
        if bytes(new.img[rva:rva + 64]) == bytes(old.img[rva:rva + 64]):
            return "identical", rva, "64-byte window (no .pdata entry here)"
        if new.masked(rva, 64)[0] == body:
            return "relocated", rva, "64-byte window, relocated operands differ"
        lo, hi = new.text
        hits = [lo + m.start() for m in re.finditer(re.escape(bytes(old.img[rva:rva + 32])), bytes(new.img[lo:hi]))][:5]
        if len(hits) == 1:
            return "moved", hits[0], "64-byte window found elsewhere by its first 32 bytes"
        # A moved leaf's RIP-relative operands change with it: search the
        # window again with those operands masked.
        _, mask = old.masked(rva, 64)
        pat = b"".join(re.escape(bytes([b])) if m else b"." for b, m in zip(body, mask))
        masked_hits = [lo + m.start() for m in re.finditer(pat, bytes(new.img[lo:hi]), re.S)][:5]
        if len(masked_hits) == 1:
            return "moved", masked_hits[0], "64-byte window found elsewhere with relocated operands masked"
        if len(masked_hits) > 1:
            return "ambiguous", None, "64-byte window (operands masked) at " + ", ".join(hex(new.base + h) for h in masked_hits)
        return "changed", None, "the 64 bytes at the address differ" + (f"; first 32 bytes at {', '.join(hex(new.base + h) for h in hits)}" if hits else "")
    length = old.bounds[rva] - rva
    body, mask = old.masked(rva)
    if new.bounds.get(rva) == old.bounds[rva]:
        if bytes(new.img[rva:rva + length]) == bytes(old.img[rva:rva + length]):
            return "identical", rva, ""
        if new.masked(rva)[0] == body:
            return "relocated", rva, "same code, relocated operands differ"
    hits = [s for s in new.by_length.get(length, []) if new.img[s] == old.img[rva] and new.masked(s)[0] == body]
    if len(hits) == 1:
        return "moved", hits[0], f"same code at +0x{hits[0] - rva:x}" if hits[0] > rva else f"same code at -0x{rva - hits[0]:x}"
    if len(hits) > 1:
        return "ambiguous", None, "same code at " + ", ".join(hex(new.base + h) for h in hits[:6])
    # Changed code in place: a function still starts at the address.
    if rva in new.bounds:
        nb, _ = new.masked(rva)
        diff = sum(1 for x, y in zip(body, nb) if x != y) + abs(len(nb) - len(body))
        return "changed", rva, f"code at the same address differs ({diff} of {length} masked bytes, new length {len(nb)})"
    # Otherwise candidates that start the same way, on 48 then 16 bytes.
    lo, hi = new.text
    for n in (48, 16):
        n = min(n, length)
        pat = b"".join(re.escape(bytes([b])) if m else b"." for b, m in zip(body[:n], mask[:n]))
        cands = [lo + m.start() for m in re.finditer(pat, bytes(new.img[lo:hi]), re.S)][:5]
        if cands:
            return "changed", None, f"code differs; functions starting with the same {n} bytes: " + ", ".join(hex(new.base + c) for c in cands)
    return "missing", None, "no function with the same start"


def tracked(dll, analysis_build):
    """[(addr, build, source, name)] the port relies on in a DLL."""
    out = []
    for p in glob.glob(os.path.join(REPO, "tools", "re", "names", f"{dll}_*.manual.json")):
        build = os.path.basename(p).split("_")[1].split(".")[0]
        for f in json.load(open(p, encoding="utf-8"))["functions"]:
            out.append((int(f["addr"], 16), build, "manual", f["name"]))
    for p in glob.glob(os.path.join(ENTITY_NAMES, f"*_{dll}.txt")):
        for line in open(p, encoding="utf-8"):
            parts = line.split()
            if len(parts) >= 2 and re.fullmatch(r"18[0-9a-f]{7}", parts[0]):
                out.append((int(parts[0], 16), analysis_build, "entity:" + os.path.basename(p), parts[1]))
    reg = json.load(open(os.path.join(REPO, "tools", "re", "tracked_rvas.json"), encoding="utf-8"))
    for r in reg["rvas"]:
        if r["dll"] == dll:
            out.append((BASE + int(r["rva"], 16), r["build"], "script:" + r["script"], r["use"]))
    rx = re.compile(dll + r"[^\n]{0,80}?\b(18[0-9a-f]{7})\b")
    for p in glob.glob(os.path.join(REPO, "src", "**", "*.cs"), recursive=True):
        text = open(p, encoding="utf-8", errors="replace").read()
        for m in rx.finditer(text):
            around = m.group(0)
            build = "20260923" if "0923" in around else "20260924" if "0924" in around else analysis_build
            out.append((int(m.group(1), 16), build, "src:" + os.path.relpath(p, REPO).replace("\\", "/"), ""))
    seen = set()
    result = []
    for a in out:
        if (a[0], a[1]) not in seen:
            seen.add((a[0], a[1]))
            result.append(a)
    return result


def unregistered_script_rvas():
    """Script RVAs not in tracked_rvas.json."""
    reg = {(r["script"], int(r["rva"], 16)) for r in json.load(open(os.path.join(REPO, "tools", "re", "tracked_rvas.json"), encoding="utf-8"))["rvas"]}
    missing = []
    for p in glob.glob(os.path.join(REPO, "tools", "**", "*.py"), recursive=True):
        rel = os.path.relpath(p, REPO).replace("\\", "/")
        if rel.startswith("tools/re/"):
            continue
        for m in re.finditer(r"(?:_RVA\s*=\s*\(?|base\.add\()(0x[0-9a-fA-F]{4,})", open(p, encoding="utf-8", errors="replace").read()):
            if (rel, int(m.group(1), 16)) not in reg:
                missing.append(f"{rel}: {m.group(1)}")
    return missing


def vpk_manifest(path):
    """{entry path: crc} of a VPK directory."""
    f = open(path, "rb")
    sig, ver, _ = struct.unpack("<III", f.read(12))
    if ver == 2:
        f.read(16)

    def rs():
        b = bytearray()
        while True:
            c = f.read(1)
            if c in (b"\0", b""):
                return b.decode(errors="replace")
            b += c
    out = {}
    while True:
        ext = rs()
        if not ext:
            break
        while True:
            d = rs()
            if not d:
                break
            while True:
                n = rs()
                if not n:
                    break
                crc, pre, _, _, _, _ = struct.unpack("<IHHIIH", f.read(18))
                f.read(pre)
                out[f"{d}/{n}.{ext}".lstrip(" /")] = crc
    return out


def selftest():
    """The classifications on edited copies of an installed image."""
    import copy
    old = Image(os.path.join(BIN, "vphysics2.dll"))
    ok = True

    def expect(label, want, new, rva):
        nonlocal ok
        got = compare(old, new, rva)[0]
        print(f"{label}: {got} (want {want})")
        ok &= got == want

    funcs = sorted((t - s, s) for s, t in old.bounds.items() if t - s > 64)
    length, a = funcs[len(funcs) // 2]
    # A body byte changed (not a relocated operand): changed.
    new = copy.copy(old)
    new.img = bytearray(old.img)
    new.cache = {}
    _, mask = old.masked(a)
    k = next(i for i in range(8, length) if mask[i])
    new.img[a + k] ^= 0x01
    expect("edited byte", "changed", new, a)
    # The same code at another place: moved.
    new = copy.copy(old)
    new.img = bytearray(old.img)
    new.cache = {}
    b = next(s for n, s in reversed(funcs) if n >= length and s != a)
    body = bytes(old.img[a:a + length])
    new.img[b:b + length] = body
    new.img[a:a + length] = b"\xcc" * length
    new.bounds = dict(old.bounds)
    new.bounds[b] = b + length
    new.by_length = {}
    for s, t in new.bounds.items():
        new.by_length.setdefault(t - s, []).append(s)
    rel = compare(old, new, a)
    print(f"moved copy: {rel[0]} (want moved or changed: a moved copy's relative operands change)")
    ok &= rel[0] in ("moved", "changed")
    # Only a call target changed: relocated.
    new = copy.copy(old)
    new.img = bytearray(old.img)
    new.cache = {}
    calls = [ins for ins in old.md.disasm(bytes(old.img[a:a + length]), a) if ins.mnemonic == "call" and ins.size == 5]
    if calls:
        c = calls[0].address
        new.img[c + 1] ^= 0x10
        expect("call target", "relocated", new, a)
    print("selftest", "passed" if ok else "FAILED")
    return ok


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--no-state", action="store_true", help="do not save this run as the new state")
    ap.add_argument("--selftest", action="store_true", help="check the classifications on edited copies of an image")
    a = ap.parse_args()
    if a.selftest:
        sys.exit(0 if selftest() else 1)
    inf = dict(line.strip().split("=", 1) for line in open(os.path.join(CS2, "game", "csgo", "steam.inf")) if "=" in line)
    patch = inf.get("PatchVersion", "unknown")
    today = datetime.date.today().strftime("%Y%m%d")
    lines = [f"# CS2 patch check: {patch} ({datetime.datetime.now():%Y-%m-%d %H:%M})", ""]
    attention = []

    # 1 and 2: DLLs and addresses.
    lines += ["## Toolchain DLLs", ""]
    state = {"patch": patch, "dlls": {}, "fgd": {}, "pak": {}}
    for dll in DLLS:
        inst = os.path.join(BIN, SUBDIR.get(dll, ""), dll + ".dll")
        if not os.path.exists(inst):
            continue
        h = md5(inst)
        state["dlls"][dll] = h
        builds = baselines(dll)
        same = [b for b, p in builds.items() if md5(p) == h]
        names = sorted(glob.glob(os.path.join(REPO, "tools", "re", "names", f"{dll}_*.json")))
        analysis = max([os.path.basename(p).split("_")[1].split(".")[0] for p in names] or sorted(builds) or [today])
        if same:
            lines.append(f"- {dll}: installed build is {', '.join(sorted(same))} (analysis build {analysis})")
        else:
            date = datetime.datetime.fromtimestamp(os.path.getmtime(inst)).strftime("%Y%m%d")
            dest = os.path.join(BASELINE_DIRS[0], f"{dll}_{date}.dll")
            if not os.path.exists(dest):
                shutil.copy2(inst, dest)
            lines.append(f"- {dll}: NEW BUILD, copied to {dest} (analysis build {analysis})")
            attention.append(f"{dll} has a new build: import it into Ghidra and rerun harvest_names.py")
        new = Image(inst)
        entries = tracked(dll, analysis)
        by_build = {}
        for e in entries:
            by_build.setdefault(e[1], []).append(e)
        counts = {}
        rows = []
        for build, es in sorted(by_build.items()):
            if build not in builds:
                rows.append(f"  - {len(es)} addresses recorded against build {build}, which is not kept: not checked")
                attention.append(f"{dll}: keep build {build} in D:/tools/binaries to check its addresses")
                continue
            old = Image(builds[build])
            for addr, _, source, name in es:
                status, at, note = compare(old, new, addr - old.base)
                counts[status] = counts.get(status, 0) + 1
                if status not in ("identical", "relocated"):
                    where = f" -> {new.base + at:x}" if at is not None else ""
                    rows.append(f"  - {status}: {addr:x} {name} ({source}, build {build}){where}: {note}")
                    attention.append(f"{dll} {addr:x} {name}: {status}")
        lines.append(f"  - {len(entries)} tracked addresses: " + ", ".join(f"{k} {v}" for k, v in sorted(counts.items())))
        lines += rows
    extra = unregistered_script_rvas()
    if extra:
        lines += ["", "Script RVAs missing from tools/re/tracked_rvas.json:"] + [f"- {x}" for x in extra]
        attention.append(f"{len(extra)} script RVAs are not registered in tracked_rvas.json")

    # 3: game data.
    lines += ["", "## Game data", ""]
    for p in sorted(glob.glob(os.path.join(CS2, "game", "csgo", "*.fgd")) + glob.glob(os.path.join(CS2, "game", "core", "*.fgd"))):
        state["fgd"][os.path.relpath(p, CS2).replace("\\", "/")] = md5(p)
    for game in ("csgo", "core"):
        p = os.path.join(CS2, "game", game, "pak01_dir.vpk")
        state["pak"][game] = vpk_manifest(p)
    # The programs the GPU material sampler runs (and their DX11 build, which a default compile runs).
    for key, name in (("shaders_vulkan", "shaders_vulkan_dir.vpk"), ("shaders_pc", "shaders_pc_dir.vpk")):
        p = os.path.join(CS2, "game", "csgo", name)
        if os.path.exists(p):
            state["pak"][key] = vpk_manifest(p)
    prev_path = os.path.join(STATE_DIR, "latest.json")
    if os.path.exists(prev_path):
        prev = json.load(open(prev_path, encoding="utf-8"))
        lines.append(f"Compared with the state saved for patch {prev.get('patch')}.")
        for k, h in state["fgd"].items():
            if prev["fgd"].get(k) != h:
                lines.append(f"- FGD changed: {k}")
                attention.append(f"FGD changed: {k} (entity lump: FgdSchema)")
        for game, man in state["pak"].items():
            old = prev["pak"].get(game, {})
            added = [k for k in man if k not in old]
            removed = [k for k in old if k not in man]
            changed = [k for k in man if k in old and old[k] != man[k]]
            title = game if game.startswith("shaders") else f"pak01 {game}"
            lines.append(f"- {title}: {len(added)} added, {len(removed)} removed, {len(changed)} changed")
            for label, ks in (("added", added), ("removed", removed), ("changed", changed)):
                interesting = [k for k in ks if k.endswith((".vmat_c", ".vmdl_c", ".vphys_c", ".vdata_c", ".vpulse_c", ".vsndevts_c", ".vcs"))]
                for k in interesting[:40]:
                    lines.append(f"  - {label}: {k}")
                if len(interesting) > 40:
                    lines.append(f"  - ... and {len(interesting) - 40} more {label}")
            if changed or removed:
                what = "shader programs the material sampler runs" if game.startswith("shaders") else "materials/models the compile reads"
                attention.append(f"{title}: {len(changed)} entries changed, {len(removed)} removed ({what})")
    else:
        lines.append("No earlier state: this run records the baseline.")

    lines += ["", "## Needs attention", ""] + ([f"- {x}" for x in attention] or ["- nothing"])
    os.makedirs(REPORT_DIR, exist_ok=True)
    report = os.path.join(REPORT_DIR, f"{today}_{patch}.md")
    with open(report, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines) + "\n")
    if not a.no_state:
        os.makedirs(STATE_DIR, exist_ok=True)
        if os.path.exists(prev_path):
            prev = json.load(open(prev_path, encoding="utf-8"))
            shutil.copy2(prev_path, os.path.join(STATE_DIR, f"{prev.get('patch', 'unknown')}.json"))
        json.dump(state, open(prev_path, "w", encoding="utf-8"))
    print("\n".join(lines))
    print(f"\nreport: {report}")
    sys.exit(2 if attention else 0)


if __name__ == "__main__":
    main()
