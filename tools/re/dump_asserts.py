"""Recover the source layout of a Valve binary from its own assert strings.

Valve ships these stripped, but every assert carries the fully qualified name of
the function it sits in, the source file and the line:

    CVoxelSampler3::MergeClusterSet(), C:/.../src/utils/visbuilder/vis3.cpp:3206

So a binary will tell you its own class list, which file implements what, and how
big those files are, with no disassembler at all. That is most of the value of
naming a stripped binary and it costs a second.

    python dump_asserts.py <binary> [<binary> ...] [--out <file.md>]

What this does NOT give you is addresses. For those, Ghidra's NameByAsserts.java
does the same match and applies real names in the project. Prefer this when the
binary is too large for Ghidra to analyse: resourcecompiler.dll is 56 MB and the
headless analyser dies saving it at the default 2 GB heap.
"""
import argparse
import collections
import os
import re
import sys

# "Class::Method(), <path>.cpp:1234". The trailing file and line are what make it
# an assert rather than any other message that happens to name a function.
ASSERT = re.compile(
    rb"([A-Za-z_][A-Za-z0-9_]*(?:::[~A-Za-z_][A-Za-z0-9_]*)+)"
    rb"\(\),\s*([A-Za-z]:\\[^\r\n\"]*?\.(?:cpp|h)):(\d+)")

# Source paths appear in their own right too, even where no assert names a
# function: they map the build tree.
SOURCE = re.compile(rb"[A-Za-z]:\\buildworker\\[^\r\n\"]*?\.(?:cpp|h)")


def scan(path):
    """(function, file, line) triples and every source path the binary mentions."""
    with open(path, "rb") as f:
        buf = f.read()

    found = {}
    for hit in ASSERT.finditer(buf):
        name = hit.group(1).decode("ascii", "replace")
        source = hit.group(2).decode("ascii", "replace").replace("\\", "/")
        found[(name, source)] = int(hit.group(3))

    files = {m.group().decode("ascii", "replace").replace("\\", "/") for m in SOURCE.finditer(buf)}
    return found, files


def tail(path, keep=3):
    """The last few path segments, which is what identifies a file usefully."""
    parts = path.split("/")
    return "/".join(parts[-keep:]) if len(parts) > keep else path


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("binaries", nargs="+")
    ap.add_argument("--out", help="write markdown here instead of stdout")
    args = ap.parse_args()

    out = open(args.out, "w", encoding="utf-8", newline="\n") if args.out else sys.stdout
    try:
        print("# Valve binaries, mapped from their own assert strings", file=out)
        print(file=out)
        print("Recovered by `tools/re/dump_asserts.py`. Every assert names the function it", file=out)
        print("sits in, its source file and its line, so a stripped binary still reports its", file=out)
        print("own structure. No addresses here; Ghidra's `NameByAsserts.java` adds those.", file=out)

        for binary in args.binaries:
            if not os.path.exists(binary):
                print(f"missing: {binary}", file=sys.stderr)
                continue
            found, files = scan(binary)
            size = os.path.getsize(binary)

            print(file=out)
            print(f"## {os.path.basename(binary)}", file=out)
            print(file=out)
            print(f"{size:,} bytes, {len(found)} functions named by asserts, "
                  f"{len(files)} source files referenced.", file=out)

            by_file = collections.defaultdict(list)
            for (name, source), line in found.items():
                by_file[source].append((line, name))

            if by_file:
                print(file=out)
                print("| source file | function | line |", file=out)
                print("|---|---|---|", file=out)
                for source in sorted(by_file):
                    for line, name in sorted(by_file[source]):
                        print(f"| `{tail(source)}` | `{name}` | {line} |", file=out)

            # Files with no assert still say which subsystems exist.
            silent = sorted(f for f in files if f not in by_file)
            if silent:
                print(file=out)
                print(f"<details><summary>{len(silent)} source files referenced with no "
                      "assert naming a function</summary>", file=out)
                print(file=out)
                for source in silent:
                    print(f"- `{tail(source, 4)}`", file=out)
                print(file=out)
                print("</details>", file=out)
    finally:
        if args.out:
            out.close()
            print(f"wrote {args.out}")


if __name__ == "__main__":
    main()
