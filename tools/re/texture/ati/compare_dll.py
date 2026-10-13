"""Compare atitest.exe with Valve's ati_compress_wrapper.dll block for block.

    python compare_dll.py <image.png>
Needs CS2 installed (the DLL is loaded from game/bin/win64) and a built atitest.exe.
"""
import ctypes, os, subprocess, sys, numpy as np
from PIL import Image
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
import ati
img = np.array(Image.open(sys.argv[1]).convert("RGBA"))
h, w = img.shape[:2]
bgra = np.ascontiguousarray(img[..., [2, 1, 0, 3]])
raw = os.path.join(HERE, "probe.bgra"); bgra.tofile(raw)
for fmt, code, perc in [("dxt1", 0xF, 1), ("dxt5", 0x11, 1), ("ati1n", 0x1F, 0), ("ati2n", 0x20, 0)]:
    ref = ati.compress(bgra, code, perc)
    out = os.path.join(HERE, "out.bin")
    subprocess.run([os.path.join(HERE, "atitest.exe"), raw, str(w), str(h), fmt, str(perc), out, "1", "1", "1"], check=True)
    o = open(out, "rb").read(); k = 8 if fmt in ("dxt1", "ati1n") else 16
    n = len(ref) // k
    print(fmt, sum(o[i*k:i*k+k] == ref[i*k:i*k+k] for i in range(n)), "/", n)
