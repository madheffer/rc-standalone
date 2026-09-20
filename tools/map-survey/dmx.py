"""Binary DMX reader (encoding binary 9), mirroring src/Source2.Compiler/Dmx/DmxBinary.cs."""
import struct

ARRAY_BASE = 32
SIZES = {2:4, 3:4, 4:1, 7:4, 8:4, 9:8, 10:12, 11:16, 12:12, 13:16, 14:64, 15:8, 16:1}

class Element:
    __slots__ = ('type', 'name', 'id', 'attrs')
    def __init__(self, type_, name, id_):
        self.type, self.name, self.id, self.attrs = type_, name, id_, {}
    def __repr__(self): return f'<{self.type} "{self.name}" {len(self.attrs)} attrs>'

def read(data):
    off = data.index(b'\0'); header = data[:off].decode(); off += 1
    def u32(o): return struct.unpack_from('<I', data, o)[0], o+4
    def i32(o): return struct.unpack_from('<i', data, o)[0], o+4
    def f32(o): return struct.unpack_from('<f', data, o)[0], o+4
    def cstr(o):
        e = data.index(b'\0', o); return data[o:e].decode('utf-8', 'replace'), e+1
    def scalar(o, t, strings, inline):
        if t == 1: v, o = i32(o); return ('#elem', v), o
        if t == 5: return cstr(o) if inline else (strings[i32(o)[0]], i32(o)[1])
        if t == 6: n, o = u32(o); return data[o:o+n], o+n
        if t == 2: return i32(o)
        if t == 3: return f32(o)
        if t == 4: return data[o] != 0, o+1
        if t in (10, 12): return (f32(o)[0], f32(o+4)[0], f32(o+8)[0]), o+12
        if t == 9: return (f32(o)[0], f32(o+4)[0]), o+8
        if t in (11, 13): return tuple(f32(o+4*i)[0] for i in range(4)), o+16
        if t == 8: return tuple(data[o:o+4]), o+4
        if t == 15: return struct.unpack_from('<Q', data, o)[0], o+8
        if t == 16: return data[o], o+1
        if t in SIZES: return None, o+SIZES[t]
        raise ValueError(f'unknown DMX type {t}')
    def value(o, t, strings, inline):
        if t > ARRAY_BASE:
            base = t - ARRAY_BASE; n, o = u32(o); out = []
            for _ in range(n):
                v, o = scalar(o, base, strings, base == 5)   # array strings are inline
                out.append(v)
            return out, o
        return scalar(o, t, strings, inline)

    prefix_count, off = u32(off)
    prefix = []
    for _ in range(prefix_count):
        el = Element('$prefix$', '', None)
        n, off = u32(off)
        for _ in range(n):
            name, off = cstr(off); t = data[off]; off += 1
            el.attrs[name], off = value(off, t, [], True)
        prefix.append(el)
    nstr, off = u32(off); strings = []
    for _ in range(nstr):
        s, off = cstr(off); strings.append(s)
    nelem, off = u32(off); elements = []
    for _ in range(nelem):
        ti, off = i32(off); ni, off = i32(off)
        elements.append(Element(strings[ti], strings[ni], data[off:off+16])); off += 16
    for el in elements:
        n, off = u32(off)
        for _ in range(n):
            ni, off = i32(off); t = data[off]; off += 1
            v, off = value(off, t, strings, False)
            el.attrs[strings[ni]] = v
    # resolve element references
    def resolve(v):
        if isinstance(v, tuple) and len(v) == 2 and v[0] == '#elem':
            return elements[v[1]] if 0 <= v[1] < len(elements) else None
        if isinstance(v, list): return [resolve(x) for x in v]
        return v
    for el in elements:
        for k in list(el.attrs): el.attrs[k] = resolve(el.attrs[k])
    return {'header': header, 'elements': elements, 'prefix': prefix, 'trailing': len(data) - off}

def read_file(path): return read(open(path, 'rb').read())
