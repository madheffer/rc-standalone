"""import_calls.py <start hex> <end hex> [dll]: the imported functions a range of an archived build calls.

Disassembles the range (default dll rc, resourcecompiler_20260923.dll, as disasm.py) and names every
`call [rip + x]` whose target is an import address table slot, with the call's address; also lists
direct calls so a comparator's helpers can be followed one level down.
"""
import os
import sys

import capstone
import pefile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import paths  # noqa: E402

dll = paths.binary(sys.argv[3] if len(sys.argv) > 3 else "rc")
pe = pefile.PE(dll, fast_load=True)
pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"]])
iat = {}
for entry in pe.DIRECTORY_ENTRY_IMPORT:
    for imp in entry.imports:
        iat[imp.address] = f"{entry.dll.decode()}!{imp.name.decode() if imp.name else imp.ordinal}"
a, b = int(sys.argv[1], 16), int(sys.argv[2], 16)
base = pe.OPTIONAL_HEADER.ImageBase
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
for i in md.disasm(pe.get_data(a - base, b - a), a):
    if i.mnemonic != "call":
        continue
    if i.op_str.startswith("qword ptr [rip + "):
        target = i.address + i.size + int(i.op_str[len("qword ptr [rip + "):-1], 16)
        print("%x: import %s" % (i.address, iat.get(target, hex(target))))
    else:
        print("%x: call %s" % (i.address, i.op_str))
