# Labels a captured constant block with a cbuffer layout from dxbc_rdef.py's output (Valve's DX11 reflection),
# printing each member's value; members that are all zero are listed in one line at the end.
#   python rdc_label.py <rdef.txt> "<cbuffer name>" <dump dir> <sha1 prefix or "Buffer N@eid"> [byte offset of the block]
import re
import struct
import sys

import rdc_blocks


def layout(path, name):
    members, on = [], False
    for line in open(path):
        if " cbuffer " in line:
            on = line.split(" cbuffer ")[1].startswith(name + ",")
            continue
        if on:
            m = re.match(r"\s+\+(\d+)\s+(\S+)\s+(\d+) bytes\s+(.*?)(\s+\(unused\))?$", line.rstrip())
            if m:
                members.append((int(m.group(1)), m.group(2), int(m.group(3)), m.group(4)))
    return members


def value(data, offset, size, kind):
    words = [data[offset + i:offset + i + 4] for i in range(0, size, 4) if offset + i + 4 <= len(data)]
    if " int " in kind or " uint " in kind or " bool " in kind:
        return " ".join(str(struct.unpack("<i", w)[0]) for w in words)
    return " ".join("%.9g" % struct.unpack("<f", w)[0] for w in words)


def main():
    members = layout(sys.argv[1], sys.argv[2])
    data = open(rdc_blocks.find(sys.argv[3], sys.argv[4]), "rb").read()
    base = int(sys.argv[5]) if len(sys.argv) > 5 else 0
    data = data[base:]
    zero = []
    for offset, name, size, kind in members:
        chunk = data[offset:offset + size]
        if not chunk.strip(b"\0"):
            zero.append(name)
            continue
        if size > 64:  # arrays of structs: the non-zero 16-byte rows only
            for r in range(0, size, 16):
                row = chunk[r:r + 16]
                if row.strip(b"\0"):
                    print("  +%-6d %s[+%d]: %s" % (offset + r, name, r, value(data, offset + r, 16, kind)))
            continue
        print("  +%-6d %-44s %s" % (offset, name, value(data, offset, size, kind)))
    print("  zero: " + ", ".join(zero))


if __name__ == "__main__":
    main()
