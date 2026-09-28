"""Find visbuilder.dll's addresses again after a game update.

Every visbuilder address was an offset into one build. CS2 updated on
2026-09-23 and all of them moved, which is not work to do twice by hand. This
reads docs/visbuilder.signatures.json -- byte patterns with the linker-moved
bytes blanked out, generated from the reference build by
D:/tools/ghidra_scripts/MakeSignatures.java -- and resolves each one against
whatever DLL you point it at.

A symbol resolves when its pattern matches EXACTLY ONCE. A pattern that matches
zero times is a function Valve changed; one that matches twice has been made
ambiguous by new code and needs re-signing. Both are reported rather than
guessed at, because a wrong address is worse than a missing one.

    python tools/sigscan.py <visbuilder.dll>            # report every symbol
    python tools/sigscan.py <dll> --names MergeCost ZLimit
    python tools/sigscan.py <dll> --json out.json       # machine readable
"""

import argparse
import json
import os
import struct
import sys


def sections(image):
    """The PE section table, as (virtual address, virtual size, raw offset, raw size)."""
    pe = struct.unpack_from("<i", image, 0x3C)[0]
    if image[pe:pe + 4] != b"PE\0\0":
        raise ValueError("not a PE image")
    count = struct.unpack_from("<H", image, pe + 6)[0]
    optional = struct.unpack_from("<H", image, pe + 20)[0]
    base = struct.unpack_from("<Q", image, pe + 24 + 24)[0]
    at = pe + 24 + optional
    found = []
    for _ in range(count):
        size, address, raw, offset = struct.unpack_from("<IIII", image, at + 8)
        found.append((address, size, offset, raw))
        at += 40
    return base, found


def executable(image):
    """The bytes of the biggest executable section, and where it starts."""
    pe = struct.unpack_from("<i", image, 0x3C)[0]
    count = struct.unpack_from("<H", image, pe + 6)[0]
    optional = struct.unpack_from("<H", image, pe + 20)[0]
    base = struct.unpack_from("<Q", image, pe + 24 + 24)[0]
    at = pe + 24 + optional
    best = None
    for _ in range(count):
        flags = struct.unpack_from("<I", image, at + 36)[0]
        size, address, raw, offset = struct.unpack_from("<IIII", image, at + 8)
        if flags & 0x20000000 and (best is None or raw > best[3]):
            best = (address, size, offset, raw)
        at += 40
    if best is None:
        raise ValueError("no executable section")
    address, _, offset, raw = best
    return image[offset:offset + raw], base + address


def compile_pattern(text):
    """'F2 0F ?? 0D' -> [0xF2, 0x0F, None, 0x0D]."""
    return [None if part == "??" else int(part, 16) for part in text.split()]


def matches(haystack, wanted, limit=3):
    """Every offset the pattern occurs at, stopping once ambiguity is proven."""
    first = wanted[0]
    width = len(wanted)
    found = []
    start = 0
    while True:
        # Anchor on the first literal byte; it is never a wildcard in practice
        # because a pattern always begins at an opcode.
        at = haystack.find(bytes([first]), start) if first is not None else start
        if at < 0 or at + width > len(haystack):
            return found
        for i, byte in enumerate(wanted):
            if byte is not None and haystack[at + i] != byte:
                break
        else:
            found.append(at)
            if len(found) >= limit:
                return found
        start = at + 1


def resolve(image, manifest, names=None):
    """Resolve every symbol against an image, reporting what each one did."""
    code, code_base = executable(image)
    base, table = sections(image)

    def offset_of(address):
        rva = address - base
        for va, size, raw, raw_size in table:
            if va <= rva < va + max(size, raw_size):
                return raw + (rva - va)
        return None

    results = []
    for symbol in manifest["symbols"]:
        if names and symbol["name"] not in names:
            continue
        row = {"name": symbol["name"], "kind": symbol["kind"], "was": symbol["was"]}

        # Every site that still resolves. They must agree; a constant read from
        # two untouched places is the strongest confirmation available, and two
        # sites disagreeing means a pattern has landed somewhere it should not.
        answers, ambiguous, resolved = set(), 0, 0
        for site in symbol["sites"]:
            hits = matches(code, compile_pattern(site["pattern"]))
            if len(hits) > 1:
                ambiguous += 1
                continue
            if not hits:
                continue
            found = code_base + hits[0]
            if symbol["kind"] == "code":
                answers.add(found)
                resolved += 1
                continue
            at = offset_of(found + site["disp"])
            if at is None:
                continue
            answers.add(found + site["next"] + struct.unpack_from("<i", image, at)[0])
            resolved += 1

        row["sites"] = "%d/%d" % (resolved, len(symbol["sites"]))
        if len(answers) > 1:
            row["status"] = "split"
            row["now"] = ", ".join("%x" % a for a in sorted(answers))
        elif len(answers) == 1:
            row["now"] = "%x" % answers.pop()
            row["status"] = "moved" if row["now"] != symbol["was"] else "same"
        else:
            row["status"] = "ambiguous" if ambiguous else "gone"
        results.append(row)
    return results


def main():
    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dll", help="the visbuilder.dll to scan")
    parser.add_argument("--signatures",
                        default=os.path.join(here, "docs", "visbuilder.signatures.json"))
    parser.add_argument("--names", nargs="*", help="only these symbols")
    parser.add_argument("--json", help="write the result here as well")
    args = parser.parse_args()

    with open(args.signatures, encoding="utf-8") as handle:
        manifest = json.load(handle)
    with open(args.dll, "rb") as handle:
        image = handle.read()

    results = resolve(image, manifest, set(args.names) if args.names else None)
    width = max(len(r["name"]) for r in results)
    for row in results:
        where = row.get("now", "-")
        note = "" if row["status"] in ("same", "moved") else "  <-- RE-SIGN"
        print("%-*s  %-4s  sites %-5s  was %-10s  now %-10s  %s%s"
              % (width, row["name"], row["kind"], row["sites"], row["was"],
                 where, row["status"], note))

    kept = sum(1 for r in results if r["status"] in ("same", "moved"))
    print("\n%d of %d resolved (%d unmoved, %d moved, %d gone, %d ambiguous)"
          % (kept, len(results),
             sum(1 for r in results if r["status"] == "same"),
             sum(1 for r in results if r["status"] == "moved"),
             sum(1 for r in results if r["status"] == "gone"),
             sum(1 for r in results if r["status"] == "ambiguous")))

    if args.json:
        with open(args.json, "w", encoding="utf-8") as handle:
            json.dump(results, handle, indent=2)
    return 0 if kept == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
