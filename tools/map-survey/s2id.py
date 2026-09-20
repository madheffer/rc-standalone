"""MurmurHash64B resource id, ported from Source2ResourceId.cs for corpus checks."""
import struct
M = 0x5bd1e995
def _u32(x): return x & 0xffffffff
def murmur64b(data, seed=0xEDABCDEF):
    h1 = _u32(_u32(seed) ^ len(data))
    h2 = _u32(seed >> 32)
    ln, i = len(data), 0
    while ln >= 8:
        k1, = struct.unpack_from('<I', data, i); i += 4; ln -= 4
        k1 = _u32(k1 * M); k1 ^= k1 >> 24; k1 = _u32(k1 * M)
        h1 = _u32(h1 * M); h1 ^= k1
        k2, = struct.unpack_from('<I', data, i); i += 4; ln -= 4
        k2 = _u32(k2 * M); k2 ^= k2 >> 24; k2 = _u32(k2 * M)
        h2 = _u32(h2 * M); h2 ^= k2
    if ln >= 4:
        k1, = struct.unpack_from('<I', data, i); i += 4; ln -= 4
        k1 = _u32(k1 * M); k1 ^= k1 >> 24; k1 = _u32(k1 * M)
        h1 = _u32(h1 * M); h1 ^= k1
    if ln == 3: h2 ^= data[i+2] << 16
    if ln >= 2: h2 ^= data[i+1] << 8
    if ln >= 1:
        h2 ^= data[i]; h2 = _u32(h2 * M)
    h1 ^= h2 >> 18; h1 = _u32(h1 * M)
    h2 ^= h1 >> 22; h2 = _u32(h2 * M)
    h1 ^= h2 >> 17; h1 = _u32(h1 * M)
    h2 ^= h1 >> 19; h2 = _u32(h2 * M)
    return (h1 << 32) | h2

def id_for_path(p):
    p = p.replace(chr(92), chr(47)).lower()
    if p.endswith('_c'): p = p[:-2]
    return murmur64b(p.encode('utf-8'))

def rerl_entries(buf, off, size):
    """[(id, name)] from a RERL block at absolute offset off."""
    if size < 8: return []
    rel, count = struct.unpack_from('<II', buf, off)
    out = []
    base = off + rel
    for i in range(count):
        e = base + i * 16
        if e + 16 > len(buf): break
        rid, = struct.unpack_from('<Q', buf, e)
        noff, = struct.unpack_from('<q', buf, e + 8)
        np = e + 8 + noff
        if np < 0 or np >= len(buf): continue
        end = buf.index(b'\0', np)
        out.append((rid, buf[np:end].decode('utf-8', 'replace')))
    return out
