"""schema_fields.py <dll> <field name> [more field names...]: the schema field table around a field.

A DLL's schema class bindings keep their fields as static SchemaClassFieldData_t arrays (0x20 bytes: name
pointer, type pointer, offset i32, metadata count i32, metadata pointer). This finds the record whose name is
the given field and prints its whole table, walking back and forward while the records have a readable name.
<dll> is a path or a binaries/ prefix such as vphysics2 (the newest archived build).
"""
import glob
import os
import struct
import sys

import pefile


def load(dll):
    if not os.path.exists(dll):
        dll = sorted(glob.glob(os.path.join(os.path.dirname(__file__), "binaries", dll + "*.dll")))[-1]
    pe = pefile.PE(dll, fast_load=True)
    return dll, pe, open(dll, "rb").read()


def main():
    dll, pe, data = load(sys.argv[1])
    base = pe.OPTIONAL_HEADER.ImageBase

    def to_rva(o):
        for s in pe.sections:
            if s.PointerToRawData <= o < s.PointerToRawData + s.SizeOfRawData:
                return o - s.PointerToRawData + s.VirtualAddress
        return None

    def string(va):
        r = va - base
        for s in pe.sections:
            if s.VirtualAddress <= r < s.VirtualAddress + s.Misc_VirtualSize:
                o = r - s.VirtualAddress + s.PointerToRawData
                end = data.find(b"\0", o)
                text = data[o:end]
                if 0 < len(text) < 128 and all(32 <= c < 127 for c in text):
                    return text.decode()
        return None

    def record(at):
        name, _type, offset, count, _meta = struct.unpack_from("<QQiiQ", data, at)
        return string(name), offset, count

    print(dll)
    for field in sys.argv[2:]:
        i = data.find(field.encode() + b"\0")
        while i >= 0 and data[i - 1] != 0:
            i = data.find(field.encode() + b"\0", i + 1)
        ptr = struct.pack("<Q", base + to_rva(i))
        at = data.find(ptr)
        if at < 0:
            print(field, "no table")
            continue
        start = at
        while record(start - 0x20)[0] is not None:
            start -= 0x20
        print("--", field, "table at", hex(base + to_rva(start)))
        while (r := record(start))[0] is not None:
            print("  %-36s 0x%x" % (r[0], r[1]))
            start += 0x20


if __name__ == "__main__":
    main()
