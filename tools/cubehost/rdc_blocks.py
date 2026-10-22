# Prints captured constant blocks / buffers from an rdc_replay.py dump as rows of four words (float and hex),
# runs of zero rows collapsed, so a block's fields can be read and matched against their sources.
#   python rdc_blocks.py <dump dir> <sha1 prefix or "Buffer N@eid"> [first byte] [length]
import json
import os
import struct
import sys


def find(dump, what):
    blobs = os.path.join(dump, "blobs")
    if "@" in what:  # a whole buffer as the action saw it
        name, eid = what.split("@")
        for line in open(os.path.join(dump, "actions.jsonl")):
            r = json.loads(line)
            if r["eid"] == int(eid):
                return os.path.join(blobs, r["contents"]["before"][name]["sha1"] + ".bin")
        raise KeyError(what)
    hits = [f for f in os.listdir(blobs) if f.startswith(what)]
    if len(hits) != 1:
        raise KeyError("%s: %d matches" % (what, len(hits)))
    return os.path.join(blobs, hits[0])


def show(data, first=0, length=None):
    end = len(data) if length is None else min(len(data), first + length)
    zero_from = None
    for o in range(first, end, 16):
        row = data[o:o + 16].ljust(16, b"\0")
        if row == bytes(16):
            if zero_from is None:
                zero_from = o
            continue
        if zero_from is not None:
            print("  +%-6d .. +%-6d zero" % (zero_from, o - 1))
            zero_from = None
        f = struct.unpack("<4f", row)
        u = struct.unpack("<4I", row)
        print("  +%-6d %s   %s" % (o, " ".join("%15.8g" % x for x in f), " ".join("%08x" % x for x in u)))
    if zero_from is not None:
        print("  +%-6d .. +%-6d zero" % (zero_from, end - 1))


if __name__ == "__main__":
    d = open(find(sys.argv[1], sys.argv[2]), "rb").read()
    print("%d bytes" % len(d))
    show(d, int(sys.argv[3]) if len(sys.argv) > 3 else 0, int(sys.argv[4]) if len(sys.argv) > 4 else None)
