"""cubemap_oracle.py [--out dir] [--size n] [--cubes n] [--only step,...]: resourcecompiler on a synthetic _buildcubemaps folder.

What docs/CUBEMAPS.md leaves open needs Valve's compile of inputs we know: the face split of a CUBE input
read from one EXR, GGXCubeMapBlur bit for bit, the radiance SH and the container. This writes a scratch
addon s2c_cubeprobe (content/csgo_addons/s2c_cubeprobe/_buildcubemaps/: EXRs and env_cubemap_array.vtex
from client.dll's template) and compiles it with the installed resourcecompiler, keeping every .vtex_c and
log under --out (default %TEMP%/cubemap_oracle). Steps:

  layout   one cube, mip algorithm None, output RGBA32323232F, for each candidate layout of the six faces
           in one image (1x6 strip, 6x1 strip, 4:3 cross, 3:4 cross); each face's texels hold
           (face + 1, x, y) so the output shows the order and orientation. The compiles that succeed tell
           the layout.
  float    --cubes cubes of HDR content (smooth gradients plus small very bright spots), the layout
           found, GGXCubeMapBlur, output RGBA32323232F: the filtered mips as floats, and the SH.
  bc6h     the same input, output BC6H: the encoder and the container as the game ships them.

Each compile runs twice, the default (DX11) and with -vulkan. Refuses while CS2 or a compile runs.
Needs no capture tooling; CubemapOracleTests (CUBEORACLE=<out>) reads the folder.
"""
import argparse
import json
import math
import os
import random
import shutil
import struct
import subprocess
import sys
import time

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
ADDON = "s2c_cubeprobe"
CONTENT = os.path.join(CS2, "content", "csgo_addons", ADDON)
GAME = os.path.join(CS2, "game", "csgo_addons", ADDON)
FOLDER = "_buildcubemaps"

# client.dll's template (file offset 0x1ad0c70 in the 10-15 build), filled as Valve's output reads.
HEAD = '<!-- dmx encoding keyvalues2_noids 4 format vtex 1 -->\n"CDmeVtex"\n{\n\t"m_inputTextureArray" "element_array"\n\t[\n'
INPUT = ('\t\t"CDmeInputTexture"\n\t\t{\n\t\t\t"m_name" "string" "inputTexture%i"\n\t\t\t"m_fileName" "string" "%s"\n'
         '\t\t\t"m_colorSpace" "string" "linear"\n\t\t\t"m_typeString" "string" "CUBE"\n'
         '\t\t\t"m_imageProcessorArray" "element_array"\n\t\t\t[\n\t\t\t]\n\t\t}%s\n')
MIDDLE = ('\t]\n\t"m_outputTypeString" "string" "%s"\n\t"m_outputFormat" "string" "%s"\n'
          '\t"m_textureOutputChannelArray" "element_array"\n\t[\n\t\t"CDmeTextureOutputChannel"\n\t\t{\n'
          '\t\t\t"m_srcChannels" "string" "rgba"\n\t\t\t"m_dstChannels" "string" "rgba"\n'
          '\t\t\t"m_mipAlgorithm" "CDmeImageProcessor"\n\t\t\t{\n\t\t\t\t"m_algorithm" "string" "%s"\n\t\t\t}\n'
          '\t\t\t"m_outputColorSpace" "string" "%s"\n\t\t\t"m_inputTextureArray" "string_array"\n\t\t\t[\n')
ENTRY = '\t\t\t\t"inputTexture%i"%s\n'
TAIL = '\t\t\t]\n\t\t}\n\t]\n\t"m_bHiddenAssetFlag" "bool" "1"\n}\n'


def vtex(files, output_type, fmt, algorithm, colour_space="linear"):
    text = HEAD
    for i, f in enumerate(files):
        text += INPUT % (i, f, "," if i + 1 < len(files) else "")
    text += MIDDLE % (output_type, fmt, algorithm, colour_space)
    for i in range(len(files)):
        text += ENTRY % (i, "," if i + 1 < len(files) else "")
    return text + TAIL


def half(x):
    return struct.unpack("<H", struct.pack("<e", x))[0]


def write_exr(path, width, height, rgba, pixel_type=1):
    """Scanline, uncompressed EXR, channels A B G R (name order), pixel_type 1 half or 2 float.
    rgba: a function (x, y) -> (r, g, b, a)."""
    channels = b""
    for name in (b"A", b"B", b"G", b"R"):
        channels += name + b"\0" + struct.pack("<iB3xii", pixel_type, 0, 1, 1)
    channels += b"\0"

    def attr(name, kind, data):
        return name + b"\0" + kind + b"\0" + struct.pack("<i", len(data)) + data

    header = struct.pack("<ii", 20000630, 2)
    header += attr(b"channels", b"chlist", channels)
    header += attr(b"compression", b"compression", b"\0")
    header += attr(b"dataWindow", b"box2i", struct.pack("<4i", 0, 0, width - 1, height - 1))
    header += attr(b"displayWindow", b"box2i", struct.pack("<4i", 0, 0, width - 1, height - 1))
    header += attr(b"lineOrder", b"lineOrder", b"\0")
    header += attr(b"pixelAspectRatio", b"float", struct.pack("<f", 1.0))
    header += attr(b"screenWindowCenter", b"v2f", struct.pack("<2f", 0, 0))
    header += attr(b"screenWindowWidth", b"float", struct.pack("<f", 1.0))
    header += b"\0"
    size = 2 if pixel_type == 1 else 4
    line_bytes = width * 4 * size
    table_at = len(header)
    first = table_at + 8 * height
    out = bytearray(header)
    for y in range(height):
        out += struct.pack("<Q", first + y * (8 + line_bytes))
    for y in range(height):
        out += struct.pack("<ii", y, line_bytes)
        pixels = [rgba(x, y) for x in range(width)]
        for c in (3, 2, 1, 0):
            for p in pixels:
                out += struct.pack("<H", half(p[c])) if pixel_type == 1 else struct.pack("<f", p[c])
    with open(path, "wb") as f:
        f.write(out)


# The six faces in one image: per layout, each face's tile (column, row) in a grid of (columns, rows).
LAYOUTS = {
    # client.dll composites the readback storage * 6 wide (4 x 3 with a flag), so the strip goes first.
    "strip_6x1": ((6, 1), [(f, 0) for f in range(6)]),
    "strip_1x6": ((1, 6), [(0, f) for f in range(6)]),
    "cross_4x3": ((4, 3), [(2, 1), (0, 1), (1, 0), (1, 2), (1, 1), (3, 1)]),
    "cross_3x4": ((3, 4), [(2, 1), (0, 1), (1, 0), (1, 2), (1, 1), (1, 3)]),
}


def marker_image(size, layout):
    (cols, rows), tiles = LAYOUTS[layout]
    where = {t: f for f, t in enumerate(tiles)}

    def px(x, y):
        face = where.get((x // size, y // size))
        if face is None:
            return (0.0, 0.0, 0.0, 1.0)
        return (float(face + 1), (x % size + 0.5) / size, (y % size + 0.5) / size, 1.0)
    return cols * size, rows * size, px


def hdr_image(size, layout, seed):
    """Smooth content per face with a few small, very bright spots (a sun), as probes see."""
    (cols, rows), tiles = LAYOUTS[layout]
    where = {t: f for f, t in enumerate(tiles)}
    rng = random.Random(seed)
    spots = [(rng.randrange(6), rng.random(), rng.random(), rng.uniform(50, 4000)) for _ in range(4)]
    tint = [(rng.uniform(0.1, 2), rng.uniform(0.1, 2), rng.uniform(0.1, 2)) for _ in range(6)]

    def px(x, y):
        face = where.get((x // size, y // size))
        if face is None:
            return (0.0, 0.0, 0.0, 1.0)
        u, v = (x % size + 0.5) / size, (y % size + 0.5) / size
        r, g, b = tint[face]
        k = 0.5 + 0.5 * math.sin(6.2831 * (u * (face + 1) + v * 0.7))
        r, g, b = r * k, g * (1 - 0.5 * k), b * (0.3 + v)
        for f, su, sv, power in spots:
            if f == face and (u - su) ** 2 + (v - sv) ** 2 < (1.5 / size) ** 2:
                r, g, b = r + power, g + power * 0.9, b + power * 0.7
        return (r, g, b, 1.0)
    return cols * size, rows * size, px


def running(name):
    return name.lower() in subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower()


def compile_vtex(name, text, out_dir, vulkan):
    path = os.path.join(CONTENT, FOLDER, "env_cubemap_array.vtex")
    with open(path, "w", newline="\n") as f:
        f.write(text)
    compiled = os.path.join(GAME, FOLDER, "env_cubemap_array.vtex_c")
    if os.path.exists(compiled):
        os.remove(compiled)
    tag = name + ("_vulkan" if vulkan else "_dx11")
    cmd = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"), "-i", path]
    if vulkan:
        cmd.append("-vulkan")
    with open(os.path.join(out_dir, tag + ".log"), "wb") as log:
        code = subprocess.run(cmd, cwd=BIN, stdout=log, stderr=subprocess.STDOUT).returncode
    ok = os.path.exists(compiled)
    if ok:
        shutil.copyfile(compiled, os.path.join(out_dir, tag + ".vtex_c"))
    shutil.copyfile(path, os.path.join(out_dir, tag + ".vtex"))
    print(f"{tag}: exit {code}, {'written' if ok else 'no output'}")
    return ok


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--out", default=os.path.join(os.environ.get("TEMP", "."), "cubemap_oracle"))
    p.add_argument("--size", type=int, default=256)  # gameinfo WorldRenderer/EnvironmentMapFaceSize
    p.add_argument("--cubes", type=int, default=3)
    p.add_argument("--only", default="layout,float,bc6h")
    p.add_argument("--layout", help="skip the search: the layout to use for float and bc6h")
    p.add_argument("--pixel", choices=("half", "float"), default="half")
    a = p.parse_args()
    if running("cs2.exe") or running("resourcecompiler.exe"):
        raise SystemExit("CS2 or a compile is running; not starting")
    if shutil.disk_usage(os.path.splitdrive(CS2)[0] + "\\").free < 10 * 1024 ** 3:
        raise SystemExit("under 10 GB free on the game drive; not starting")
    steps = set(a.only.split(","))
    pixel_type = 1 if a.pixel == "half" else 2
    os.makedirs(a.out, exist_ok=True)
    os.makedirs(os.path.join(CONTENT, FOLDER), exist_ok=True)
    os.makedirs(GAME, exist_ok=True)
    results = {"size": a.size, "pixel": a.pixel, "layouts": {}}
    started = time.time()

    found = a.layout
    if "layout" in steps:
        for layout in LAYOUTS:
            w, h, px = marker_image(a.size, layout)
            name = f"env_cubemap_{layout}.exr"
            write_exr(os.path.join(CONTENT, FOLDER, name), w, h, px, pixel_type)
            shutil.copyfile(os.path.join(CONTENT, FOLDER, name), os.path.join(a.out, name))
            text = vtex([f"{FOLDER}/{name}"], "CUBEARRAY", "RGBA32323232F", "None")
            ok = [compile_vtex(f"layout_{layout}", text, a.out, v) for v in (False, True)]
            results["layouts"][layout] = ok
            if ok[0] and found is None:
                found = layout
    results["layout"] = found

    if found and ({"float", "bc6h"} & steps):
        files = []
        for c in range(a.cubes):
            w, h, px = hdr_image(a.size, found, 1000 + c)
            name = f"env_cubemap_{1000 + c}.exr"
            write_exr(os.path.join(CONTENT, FOLDER, name), w, h, px, pixel_type)
            shutil.copyfile(os.path.join(CONTENT, FOLDER, name), os.path.join(a.out, name))
            files.append(f"{FOLDER}/{name}")
        for step, fmt in (("float", "RGBA32323232F"), ("bc6h", "BC6H")):
            if step in steps:
                text = vtex(files, "CUBEARRAY", fmt, "GGXCubeMapBlur")
                results[step] = [compile_vtex(step, text, a.out, v) for v in (False, True)]
    results["seconds"] = round(time.time() - started)
    with open(os.path.join(a.out, "results.json"), "w") as f:
        json.dump(results, f, indent=1)
    print(json.dumps(results))


if __name__ == "__main__":
    main()
