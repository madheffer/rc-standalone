"""Replace individual resources inside a compiled map VPK.

The container proof (docs/CONTAINERS.md): everything this project authors has so far only been
judged by our own comparison and has never been loaded by CS2. This swaps our
re-authored resources into a map Valve's compiler produced, so the result is
Valve's map in every respect except the files under test.

    python splice_map.py <map.vpk> --replace <path-in-vpk>=<file> [...] -o <out.vpk>
    python splice_map.py <map.vpk> --list

`resourcecompiler -novpk` would avoid needing this, but it hangs: 35 minutes at a
constant 532 MB producing no output on a map that compiles in 45 seconds. Writing
the VPK ourselves is both the working route and something the compiler needs
anyway.

Superseded by Source2.Compiler's VpkWriter (`s2c map-physics`): current map
packages also carry a BLAKE3 chunk section, a signature section and a tree in
reverse first-appearance order, none of which this script writes.

VPK v2 is a directory tree of null terminated strings followed by the file data.
Only single file packages are written here, which is what a map VPK is: every
entry lives in this file rather than in numbered side archives.
"""
import argparse
import hashlib
import os
import struct
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "map-survey"))
from vpk import Vpk                                       # noqa: E402

SIGNATURE = 0x55AA1234
IN_THIS_FILE = 0x7FFF      # archive index meaning "the data is in this file"


def split(path):
    """A VPK path as the tree stores it: extension, folder, base name."""
    folder, _, name = path.rpartition("/")
    base, _, ext = name.rpartition(".")
    if not base:                       # no extension at all
        base, ext = name, " "
    return ext or " ", folder or " ", base


def build_tree(entries, offsets):
    """The directory tree. entries maps path to bytes; offsets maps path to
    where that file's data starts in the data section."""
    layout = {}
    for path in entries:
        ext, folder, base = split(path)
        layout.setdefault(ext, {}).setdefault(folder, []).append((base, path))

    out = bytearray()
    for ext in sorted(layout):
        out += ext.encode("utf-8") + b"\0"
        for folder in sorted(layout[ext]):
            out += folder.encode("utf-8") + b"\0"
            for base, path in sorted(layout[ext][folder]):
                data = entries[path]
                out += base.encode("utf-8") + b"\0"
                out += struct.pack("<IHHII",
                                   zlib.crc32(data) & 0xFFFFFFFF,
                                   0,                      # no preload bytes
                                   IN_THIS_FILE,
                                   offsets[path],
                                   len(data))
                out += struct.pack("<H", 0xFFFF)           # end of entry
            out += b"\0"                                   # end of folder
        out += b"\0"                                       # end of extension
    out += b"\0"                                           # end of tree
    return bytes(out)


def write_vpk(path, entries):
    """A single file VPK v2 holding exactly these entries."""
    # The tree stores each file's offset, and the tree's own size depends on
    # those offsets only through their digit-free fixed width, so one pass to
    # assign offsets and a second to lay out the tree is enough.
    offsets, at = {}, 0
    for name in sorted(entries):
        offsets[name] = at
        at += len(entries[name])
    tree = build_tree(entries, offsets)
    data = b"".join(entries[name] for name in sorted(entries))

    # v2 trails three checksums: of the tree, of the (absent) archive section,
    # and of everything before the signature section.
    header = struct.pack("<7I", SIGNATURE, 2, len(tree), len(data), 0, 48, 0)
    body = header + tree + data
    tree_md5 = hashlib.md5(tree).digest()
    archive_md5 = hashlib.md5(b"").digest()
    whole = hashlib.md5(body + tree_md5 + archive_md5).digest()

    with open(path, "wb") as f:
        f.write(body + tree_md5 + archive_md5 + whole)
    return len(body) + 48


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("vpk")
    ap.add_argument("--replace", action="append", default=[],
                    metavar="PATH=FILE", help="swap this entry for this file's bytes")
    ap.add_argument("-o", "--out")
    ap.add_argument("--list", action="store_true")
    args = ap.parse_args()

    source = Vpk(args.vpk)
    entries = {path: source.read(path) for path in source.entries}
    source.close()

    if args.list:
        for path in sorted(entries):
            print(f"{len(entries[path]):>12,}  {path}")
        print(f"{len(entries)} entries")
        return

    if not args.out:
        sys.exit("-o is required unless --list")

    for pair in args.replace:
        path, _, filename = pair.partition("=")
        if path not in entries:
            sys.exit(f"{path} is not in {args.vpk}")
        replacement = open(filename, "rb").read()
        print(f"  {path}\n    {len(entries[path]):,} -> {len(replacement):,} bytes  ({filename})")
        entries[path] = replacement

    size = write_vpk(args.out, entries)
    print(f"wrote {args.out}  {size:,} bytes, {len(entries)} entries")

    # Read it back with the same reader the survey tools use: a package we
    # cannot parse is one the game certainly cannot.
    check = Vpk(args.out)
    try:
        bad = [p for p in entries if p not in check.entries]
        assert not bad, f"missing after write: {bad[:3]}"
        for path in list(entries)[:50]:
            assert check.read(path) == entries[path], f"content differs for {path}"
        print(f"verified: {len(check.entries)} entries readable, spot checked 50")
    finally:
        check.close()


if __name__ == "__main__":
    main()
