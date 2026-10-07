"""vtables.py <dll> [--out <json>] [--match <substring>]: every RTTI class's vtables, slot by slot.

Offline (pefile only). Each CompleteObjectLocator (signature 1, pTypeDescriptor
an RVA to a ".?AV"/".?AU" name) is found, then every vtable whose -8 word points
at it; the vtable runs while its entries point into executable sections. For
each class: its mangled name, and per vtable the object offset and the slot
addresses. --match keeps classes whose name contains the substring.

    python tools/re/vtables.py D:/tools/binaries/vrad3_20261001.dll --match CVrad3Command_
"""
import argparse
import json
import struct

import pefile


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("dll")
    p.add_argument("--out")
    p.add_argument("--match")
    a = p.parse_args()
    pe = pefile.PE(a.dll, fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    img = pe.get_memory_mapped_image()
    code = [(s.VirtualAddress, s.VirtualAddress + s.Misc_VirtualSize) for s in pe.sections if s.Characteristics & 0x20000000]

    def is_code(va):
        r = va - base
        return any(lo <= r < hi for lo, hi in code)

    # Every 8-byte aligned pointer in the image, by target, for the vtable search.
    by_target = {}
    for off in range(0, len(img) - 8, 8):
        v = struct.unpack_from("<Q", img, off)[0]
        if base <= v < base + len(img):
            by_target.setdefault(v, []).append(off)

    classes = {}
    for off in range(0, len(img) - 24, 4):
        sig, obj_off, cd, td = struct.unpack_from("<IIII", img, off)
        if sig != 1 or td == 0 or td + 0x14 >= len(img):
            continue
        name = img[td + 0x10: td + 0x10 + 512].split(b"\0")[0]
        if not (name.startswith(b".?AV") or name.startswith(b".?AU")):
            continue
        name = name.decode("latin1")
        if a.match and a.match not in name:
            continue
        for ref in by_target.get(base + off, []):
            vt = ref + 8
            slots = []
            while vt + 8 * len(slots) + 8 <= len(img):
                e = struct.unpack_from("<Q", img, vt + 8 * len(slots))[0]
                if not is_code(e):
                    break
                slots.append(f"{e:x}")
                if len(slots) > 512:
                    break
            if slots:
                classes.setdefault(name, []).append({"vtable": f"{base + vt:x}", "offset": obj_off, "slots": slots})
    out = {"dll": a.dll, "classes": classes}
    if a.out:
        with open(a.out, "w") as f:
            json.dump(out, f, indent=1)
    for n, vts in sorted(classes.items()):
        for v in vts:
            print(f"{n} vtable {v['vtable']} +{v['offset']:x}: {len(v['slots'])} slots")


if __name__ == "__main__":
    main()
