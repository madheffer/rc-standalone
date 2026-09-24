"""read_weld.py <capture> [--show n] -- summarise a capture_weld.py stream."""
import json
import struct
import sys


def records(path):
    with open(path, "rb") as h:
        data = h.read()
    at = 0
    while at < len(data):
        (n,) = struct.unpack_from("<I", data, at)
        head = json.loads(data[at + 4: at + 4 + n])
        at += 4 + n
        (m,) = struct.unpack_from("<I", data, at)
        blob = data[at + 4: at + 4 + m]
        at += 4 + m
        yield head, blob


def mesh(head, blob):
    nv, stride, ni = head["nv"], head["stride"], head["ni"]
    floats = struct.unpack_from("<%df" % (nv * stride), blob, 0)
    idx = struct.unpack_from("<%dI" % ni, blob, nv * stride * 4)
    return [floats[i * stride:(i + 1) * stride] for i in range(nv)], idx


def main():
    show = int(sys.argv[sys.argv.index("--show") + 1]) if "--show" in sys.argv else 5
    pending = {}
    layouts = {}
    welded = total = 0
    for head, blob in records(sys.argv[1]):
        if head["ev"] == "in":
            pending[head["id"]] = (head, blob)
        elif head["ev"] == "out":
            h0, b0 = pending.pop(head["id"])
            total += 1
            key = tuple((s["name"], s["first"], s["count"], s["flag"], s["type"]) for s in h0["streams"])
            layouts[key] = layouts.get(key, 0) + 1
            if head["nv"] != h0["nv"]:
                welded += 1
                if show > 0:
                    show -= 1
                    print("weld %d: %d -> %d vertices, %d indices" % (head["id"], h0["nv"], head["nv"], h0["ni"]))
    print("%d welds, %d changed the vertex count" % (total, welded))
    for k, v in layouts.items():
        print(v, "x", k)


if __name__ == "__main__":
    main()
