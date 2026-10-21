# Reads a RenderDoc capture (.rdc) without RenderDoc or a GPU: the file header, the section table and the frame
# capture section's LZ4 page stream, decompressed one 1 MB page at a time (low memory, so it can run while the
# PC is busy). Layout from RenderDoc's serialise/rdcfile.cpp and lz4io.cpp (v1.4x captures, file version 0x102).
#   python rdc_stream.py <capture.rdc>            prints the header and section table
import struct
import sys

import lz4.block

PAGE = 1024 * 1024


class Section:
    def __init__(self, f):
        hdr = f.read(4 + 4 + 8 + 8 + 8 + 4 + 4)
        (self.ascii, self.type, self.compressed, self.uncompressed, self.version, self.flags,
         name_len) = struct.unpack("<4sIQQQII", hdr)
        self.name = f.read(name_len).rstrip(b"\0").decode()
        self.offset = f.tell()


class Capture:
    def __init__(self, path):
        self.f = open(path, "rb")
        magic, self.version, header_len = struct.unpack("<QII", self.f.read(16))
        if magic != 0x434f4452:
            raise ValueError("not an rdc")
        self.program = self.f.read(16).rstrip(b"\0").decode()
        self.f.seek(header_len)
        self.sections = []
        size = self.f.seek(0, 2)
        pos = header_len
        while pos < size:
            self.f.seek(pos)
            s = Section(self.f)
            self.sections.append(s)
            pos = s.offset + s.compressed

    def frame(self):
        return next(s for s in self.sections if s.type == 1)

    def pages(self, section=None):
        """The section's uncompressed bytes, page by page. LZ4 pages are a stream: each one may refer back into
        the previous page (RenderDoc's LZ4_decompress_safe_continue with a two-page ring)."""
        s = section or self.frame()
        if not s.flags & 2:
            raise NotImplementedError("section flags %x" % s.flags)
        self.f.seek(s.offset)
        left = s.uncompressed
        prev = b""
        while left > 0:
            (n,) = struct.unpack("<i", self.f.read(4))
            page = lz4.block.decompress(self.f.read(n), uncompressed_size=min(PAGE, left), dict=prev)
            left -= len(page)
            prev = page
            yield page


class Stream:
    """Sequential reader over the page iterator (read, skip, tell)."""

    def __init__(self, pages):
        self.it = pages
        self.buf = b""
        self.pos = 0
        self.base = 0

    def _fill(self, n):
        while len(self.buf) - self.pos < n:
            nxt = next(self.it, None)
            if nxt is None:
                return False
            self.base += self.pos
            self.buf = self.buf[self.pos:] + nxt
            self.pos = 0
        return True

    def read(self, n):
        if not self._fill(n):
            raise EOFError
        b = self.buf[self.pos:self.pos + n]
        self.pos += n
        return b

    def skip(self, n):
        while n > 0:
            have = len(self.buf) - self.pos
            if n <= have:
                self.pos += n
                return
            n -= have
            self.base += len(self.buf)
            self.buf = next(self.it)
            self.pos = 0

    def tell(self):
        return self.base + self.pos

    def u32(self):
        return struct.unpack("<I", self.read(4))[0]

    def u64(self):
        return struct.unpack("<Q", self.read(8))[0]


if __name__ == "__main__":
    c = Capture(sys.argv[1])
    print("version %x, %s" % (c.version, c.program))
    for s in c.sections:
        print("type %d flags %x version %x %d -> %d %s" % (s.type, s.flags, s.version, s.compressed, s.uncompressed, s.name))


class Chunk:
    __slots__ = ("id", "flags", "thread", "duration", "timestamp", "size", "offset", "data")


def chunks(stream, keep=lambda cid, size: False):
    """Chunk headers (serialise/serialiser.cpp): a u32 id with flags (callstack 0x10000, thread 0x20000,
    duration 0x40000, timestamp 0x80000, 64-bit size 0x100000), the optional fields, the size, then the
    payload padded to 64 bytes. keep(id, size) says which payloads to read; the rest are skipped."""
    while True:
        start = stream.tell()
        try:
            word = stream.u32()
        except EOFError:
            return
        c = Chunk()
        c.id = word & 0xffff
        c.flags = word & 0xffff0000
        if c.flags & 0x10000:
            n = stream.u32()
            stream.skip(8 * n)
        c.thread = stream.u64() if c.flags & 0x20000 else 0
        c.duration = stream.u64() if c.flags & 0x40000 else 0
        c.timestamp = stream.u64() if c.flags & 0x80000 else 0
        c.size = stream.u64() if c.flags & 0x100000 else stream.u32()
        c.offset = stream.tell()
        c.data = stream.read(c.size) if keep(c.id, c.size) else None
        if c.data is None:
            stream.skip(c.size)
        pad = (-stream.tell()) % 64
        stream.skip(pad)
        yield c
