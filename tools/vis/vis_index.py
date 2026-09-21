"""Read the vis index (the DATA block of world_visibility.vvis_c) out of every map.

The index is a small KV3 tree that describes the layout of the big opaque VXVS
block beside it, as six (byte offset, element count) pairs. Nothing here decodes
VXVS; this is the map of it, and the strides it implies are what the decoders are
written against.

Decoding KV3 is the C# library's job, so each DATA payload is rewrapped as a
one-block container and handed to the CLI. That keeps 546 MB of VXVS off disk:
the payloads are a few hundred bytes each.
"""
import os
import re
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "map-survey"))
from vpk import Vpk                                      # noqa: E402

WORKSHOP = os.environ.get("WORKSHOP_DIR", r"D:\Steam\steamapps\workshop\content\730")
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))

# VXVS is six arrays laid end to end. The KV3 gives each one's byte offset and
# element count; these are the strides that fall out of offset differences, and
# vis_survey.py re-derives them from every map rather than trusting this table.
STRIDES = {
    "m_NodeBlock": 8,
    "m_RegionBlock": 8,
    "m_EnclosedClusterListBlock": 8,
    "m_EnclosedClustersBlock": 2,
    "m_MasksBlock": 8,
    "m_nVisBlocks": 1,
}
ORDER = list(STRIDES)


def container_header(buf):
    """(resourceVersion, {fourcc: (offset, size)}) for a Source 2 container."""
    fs, hv, rv, bio, bc = struct.unpack_from("<IHHII", buf, 0)
    if hv != 12 or not 0 < bc <= 16:
        return None
    blocks = {}
    for i in range(bc):
        off = 8 + bio + i * 12
        rel, size = struct.unpack_from("<II", buf, off + 4)
        blocks[buf[off:off + 4].decode("ascii", "replace")] = (off + 4 + rel, size)
    return rv, blocks


def rewrap(payload):
    """A minimal one-block container holding payload as its DATA block, so the
    C# decompiler can read it without the 17 MB of VXVS that followed it."""
    return (struct.pack("<IHHII", 28 + len(payload), 12, 1, 8, 1)
            + b"DATA" + struct.pack("<II", 8, len(payload)) + payload)


def decompile(path):
    done = subprocess.run(
        ["dotnet", "run", "--project", os.path.join(REPO, "src", "Source2.Compiler.Cli"),
         "--no-build", "--", "decompile", path, "--block", "DATA"],
        capture_output=True, text=True, errors="replace", cwd=REPO)
    if done.returncode != 0:
        raise RuntimeError(done.stderr.strip()[:200])
    return done.stdout


NUMBER = re.compile(r"(\w+) = (-?[\d.]+)")
BLOCK = re.compile(r"(\w+) = \s*\{\s*m_nOffset = (\d+)\s*m_nElementCount = (\d+)\s*\}")


def parse(text):
    """The decompiled index as plain numbers: scalars, plus block -> (offset, count)."""
    out = {k: float(v) if "." in v else int(v) for k, v in NUMBER.findall(text)
           if k not in ("m_nOffset", "m_nElementCount")}
    out["blocks"] = {k: (int(o), int(c)) for k, o, c in BLOCK.findall(text)}
    return out


def maps(limit=None):
    """Yield (map name, vis index dict, VXVS byte size) for every installed map."""
    seen = set()
    for item in sorted(os.listdir(WORKSHOP)):
        outer_path = os.path.join(WORKSHOP, item, item + ".vpk")
        if not os.path.exists(outer_path):
            continue
        try:
            outer = Vpk(outer_path)
        except Exception:
            continue
        for nested in [k for k in outer.entries if k.startswith("maps/") and k.endswith(".vpk")]:
            name = nested.split("/")[-1][:-4]
            if name in seen:
                continue
            try:
                archive = outer.nested(nested)
            except Exception:
                continue
            for path in archive.entries:
                if not path.endswith("world_visibility.vvis_c"):
                    continue
                offset, length, preload, index = archive.entries[path]
                head = preload[:4096]
                if len(head) < 4096:
                    archive.fh.seek(archive.base + archive.data_off + offset)
                    head = preload + archive.fh.read(4096)
                header = container_header(head)
                if not header:
                    continue
                _, blocks = header
                data_at, data_size = blocks["DATA"]
                if data_at + data_size > len(head):
                    archive.fh.seek(archive.base + archive.data_off + offset + data_at - len(preload))
                    payload = archive.fh.read(data_size)
                else:
                    payload = head[data_at:data_at + data_size]
                seen.add(name)
                yield name, payload, blocks["VXVS"][1]
                if limit and len(seen) >= limit:
                    return


def vis_digest(container):
    """A hash of the VISIBILITY in a .vvis_c, ignoring compile identity.

    Two compiles of one map from different install roots produce byte-identical
    DATA and VXVS but a different RED2 m_nFingerprint, so hashing the whole file
    reports a difference that is not one. Measured on ze_hold_em_p compiled from
    the real install and from the shadow: RED2 differs in that one field, the
    other two blocks match to the byte.
    """
    import hashlib
    header = container_header(container)
    if not header:
        return None
    _, blocks = header
    digest = hashlib.sha256()
    for fourcc in ("DATA", "VXVS"):
        if fourcc not in blocks:
            continue
        offset, size = blocks[fourcc]
        digest.update(container[offset:offset + size])
    return digest.hexdigest()
