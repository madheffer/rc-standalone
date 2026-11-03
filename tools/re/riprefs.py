"""riprefs.py <dll> <target hex | "string"> -- every RIP-relative lea/mov in .text that addresses the target.

For builds not imported into Ghidra: finds the code that references a string or global by scanning
for the 7-byte REX forms (48/4c 8d/8b ModRM-rip disp32) whose displacement lands on the target, with
numpy over the whole section. A quoted argument is located as a NUL-terminated string first.
"""
import os
import sys

import numpy as np
import pefile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import paths  # noqa: E402

pe = pefile.PE(paths.binary(sys.argv[1]), fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
img = pe.get_memory_mapped_image()
arg = sys.argv[2]
if arg.startswith('"') or not all(c in "0123456789abcdefx" for c in arg.lower()):
    s = arg.strip('"').encode()
    at = img.find(b"\0" + s + b"\0")
    if at < 0:
        raise SystemExit("string not found")
    target = base + at + 1
else:
    target = int(arg, 16)
text = next(s for s in pe.sections if s.Name.rstrip(b"\0") == b".text")
lo = text.VirtualAddress
buf = np.frombuffer(img[lo:lo + text.Misc_VirtualSize], dtype=np.uint8)
n = len(buf) - 7
rex = (buf[:n] & 0xFB) == 0x48
op = (buf[1:n + 1] == 0x8D) | (buf[1:n + 1] == 0x8B)
modrm = (buf[2:n + 2] & 0xC7) == 0x05
idx = np.nonzero(rex & op & modrm)[0]
disp = buf[idx + 3].astype(np.int64) | (buf[idx + 4].astype(np.int64) << 8) | (buf[idx + 5].astype(np.int64) << 16) | (buf[idx + 6].astype(np.int64) << 24)
disp = np.where(disp >= 1 << 31, disp - (1 << 32), disp)
hit = idx[(base + lo + idx + 7 + disp) == target]
print(f"target {target:#x}")
for h in hit:
    print(f"{base + lo + h:#x}")
