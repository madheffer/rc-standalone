"""Minimal self-contained VPK v2 reader: directory tree + lazy entry reads."""
import struct

class Vpk:
    def __init__(self, src, base=0):
        """src: a path, an open file handle, or a bytes buffer. base is the
        byte offset of this VPK inside the handle (for a nested map VPK)."""
        self.base = base
        if isinstance(src, (bytes, bytearray)):
            self.buf, self.fh = bytes(src), None
            head = self.buf[:28]
        else:
            self.buf = None
            self.fh = src if hasattr(src, 'read') else open(src, 'rb')
            self.fh.seek(base)
            head = self.fh.read(28)
        sig, ver, treesz, fdss, amd5, omd5, sigsz = struct.unpack_from('<7I', head, 0)
        assert sig == 0x55aa1234, hex(sig)
        self.version = ver
        hdr = 28 if ver == 2 else 12
        self.data_off = hdr + treesz
        if self.buf is not None:
            tree = self.buf[hdr:hdr+treesz]
        else:
            self.fh.seek(base + hdr)
            tree = self.fh.read(treesz)
        self.entries = {}
        p = 0
        def rs(p):
            e = tree.index(b'\0', p)
            return tree[p:e].decode('utf-8', 'replace'), e + 1
        while p < len(tree):
            ext, p = rs(p)
            if not ext: break
            while True:
                folder, p = rs(p)
                if not folder: break
                while True:
                    name, p = rs(p)
                    if not name: break
                    crc, pre, ai, ao, el = struct.unpack_from('<IHHII', tree, p); p += 16
                    p += 2
                    preload = tree[p:p+pre]; p += pre
                    path = ('' if folder == ' ' else folder + '/') + name + ('' if ext == ' ' else '.' + ext)
                    self.entries[path] = (ao, el, preload, ai)

    def read(self, path):
        ao, el, preload, ai = self.entries[path]
        if el == 0: return preload
        assert ai == 0x7fff, f'entry lives in archive {ai}'
        if self.buf is not None:
            return preload + self.buf[self.data_off+ao : self.data_off+ao+el]
        self.fh.seek(self.base + self.data_off + ao)
        return preload + self.fh.read(el)

    def size(self, path):
        ao, el, preload, ai = self.entries[path]
        return el + len(preload)

    def nested(self, path):
        """A Vpk for a VPK stored as an entry of this one, without materializing it."""
        ao, el, preload, ai = self.entries[path]
        if self.fh is None or preload:
            return Vpk(self.read(path))
        return Vpk(self.fh, base=self.base + self.data_off + ao)

    def close(self):
        if self.fh: self.fh.close()

def container_blocks(buf):
    if len(buf) < 16: return None
    fs, hv, rv, bio, bc = struct.unpack_from('<IHHII', buf, 0)
    if hv != 12 or bc > 16 or bc == 0: return None
    base = 8 + bio
    blocks = {}
    for i in range(bc):
        off = base + i * 12
        if off + 12 > len(buf): return None
        fourcc = buf[off:off+4].decode('ascii', 'replace')
        rel, size = struct.unpack_from('<II', buf, off + 4)
        blocks[fourcc] = (off + 4 + rel, size)
    return hv, rv, blocks

def kv3_facts(buf, off):
    if off + 24 > len(buf): return None
    if buf[off+1:off+4] != b'3VK': return None
    ver = buf[off]
    if ver < 4: return (ver, None)
    comp, = struct.unpack_from('<I', buf, off + 20)
    return (ver, comp)
