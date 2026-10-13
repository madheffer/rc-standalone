import struct, numpy as np
from PIL import Image
import os
HERE = os.path.dirname(os.path.abspath(__file__))
S = os.environ.get("S2C_TEXTURE_PROBES", HERE)  # holds rctex/ (resourcecompiler outputs)
tab = np.load(os.path.join(HERE, "g2l_table.npy"))
f = np.float32
def enc(v):
    x = np.minimum(f(1), np.maximum(f(0), v)).astype(f)
    with np.errstate(all="ignore"):
        s1 = np.sqrt(x).astype(f); s2 = np.sqrt(s1).astype(f); s3 = np.sqrt(s2).astype(f); s4 = np.sqrt(s3).astype(f)
        m = (s2 * s3).astype(f); s5 = np.sqrt(s4).astype(f); m = (m * s4).astype(f)
        s6 = np.sqrt(s5).astype(f); m = (m * s6).astype(f); s7 = np.sqrt(s6).astype(f)
        d = (s5 * s7).astype(f); r = (f(1) / d).astype(f); r = (r * m).astype(f)
        hi = (f(1.055) * r - f(0.055)).astype(f)
    lo = (f(12.92) * x).astype(f)
    return np.where(x <= f(0.0031308), lo, hi).astype(f)
def rc_mips(name, w, h):
    b = open(S + "/rctex/%s.vtex_c" % name, "rb").read(); size = struct.unpack_from("<I", b, 0)[0]; tail = b[size:]
    dims = [(w, h)]
    while dims[-1] != (1, 1): dims.append((max(1, dims[-1][0] // 2), max(1, dims[-1][1] // 2)))
    out = {}; at = 0
    for (x, y) in dims[::-1]:
        n = x * y * 4; out[(x, y)] = np.frombuffer(tail[at:at + n], np.uint8).reshape(y, x, 4).astype(np.int32); at += n
    return dims, out
def box_drop(a, nw, nh):
    a = a[:nh * 2, :nw * 2] if a.shape[0] >= 2 and a.shape[1] >= 2 else a
    if a.shape[0] == 1: a = np.concatenate([a, a], 0)
    if a.shape[1] == 1: a = np.concatenate([a, a], 1)
    a = a[:nh * 2, :nw * 2]
    p, q, r, s = a[0::2, 0::2], a[0::2, 1::2], a[1::2, 0::2], a[1::2, 1::2]
    return (((p + q) + (r + s)) * f(0.25)).astype(f)

def quarter(a):
    """FloatBitMap_t::QuarterSize: SIMD 2x2 box when both sides are even, else the 3-tap odd filter."""
    H, W = a.shape[:2]
    n = W // 2 if W > 3 else 1
    m = max(1, H // 2)
    if W % 2 == 0 and H % 2 == 0:
        p, q, r, s = a[0::2, 0::2], a[0::2, 1::2], a[1::2, 0::2], a[1::2, 1::2]
        # column pairs first; the SSE path (output width a multiple of 4, at least 8) adds the two
        # pairs, the scalar path adds left to right after the first pair (captured, 1812764c0)
        if n % 4 == 0 and n >= 8:
            return (((p + r) + (q + s)) * f(0.25)).astype(f)
        return ((((p + r) + q) + s) * f(0.25)).astype(f)
    if not (n * 2 + 1 == W or n * 2 == W) or not (m * 2 + 1 == H or m * 2 == H):
        out = np.zeros((m, n) + a.shape[2:], f)
        for y in range(m):
            r0, r1 = min(2 * y, H - 1), min(2 * y + 1, H - 1)
            for x in range(n):
                c0, c1 = min(2 * x, W - 1), min(2 * x + 1, W - 1)
                out[y, x] = ((((a[r0, c1] + a[r0, c0]) + a[r1, c0]) + a[r1, c1]) * f(0.25)).astype(f)
        return out
    iw, ih = f(1) / f(W), f(1) / f(H)
    out = np.zeros((m, n) + a.shape[2:], f)
    for y in range(m):
        r0, r1, r2 = min(2 * y, H - 1), min(2 * y + 1, H - 1), min(2 * y + 2, H - 1)
        for x in range(n):
            c0, c1, c2 = min(2 * x, W - 1), min(2 * x + 1, W - 1), min(2 * x + 2, W - 1)
            w0 = f(n - x) * iw; w1 = f(n) * iw; w2 = f(x + 1) * iw
            P = lambda r, c: a[r, c]
            # captured (180c42690): every row adds c0, c1 then c2; each row factor is one product
            top = ((w0 * P(r0, c0) + w1 * P(r0, c1)) + w2 * P(r0, c2)).astype(f)
            mid = ((w0 * P(r1, c0) + w1 * P(r1, c1)) + w2 * P(r1, c2)).astype(f)
            bot = ((w0 * P(r2, c0) + w1 * P(r2, c1)) + w2 * P(r2, c2)).astype(f)
            ft, fm, fb = f(m - y) * ih, f(m) * ih, f(y + 1) * ih
            out[y, x] = ((top * ft + mid * fm) + bot * fb).astype(f)
    return out

def write8(v):
    """FloatBitMap_t::WriteToBuffer RGBA8888: SIMD (+2^23 rounding) when the width is a multiple of 4, else scalar."""
    w = v.shape[1]
    if w >= 4 and w % 4 == 0:
        return np.rint(np.minimum(f(255), np.maximum(f(0), (v * f(255)).astype(f))))
    return np.trunc((np.minimum(f(1), np.maximum(f(0), v)) * f(255) + f(0.5)).astype(f))

def sample(img, fx, fy, clamp):
    """FloatBitMap_t bilinear sample (FUN_181279940): wrap, or clamp to the edge."""
    H, W = img.shape[:2]
    fx = f(fx - f(0.5))
    if not clamp:
        if fx < 0: fx = f(fx + f(W))
        fy = f(fy - f(0.5))
        if fy < 0: fy = f(fy + f(H))
        x0 = int(np.floor(fx)); x1 = x0 + 1; tx = f(fx - f(x0))
        if x1 >= W: x1 -= W
        y0 = int(np.floor(fy)); y1 = y0 + 1; ty = f(fy - f(y0))
        if y1 >= H: y1 -= H
    else:
        x1 = W - 1; tx = max(f(0), fx); y_hi = H - 1
        ty = max(f(0), f(fy - f(0.5)))
        tx = min(tx, f(x1)); ty = min(ty, f(y_hi))
        x0 = int(np.floor(tx)); x1 = min(x0 + 1, W - 1); tx = f(tx - f(x0))
        y0 = int(np.floor(ty)); y1 = min(y0 + 1, H - 1); ty = f(ty - f(y0))
    top = ((img[y0, x1] - img[y0, x0]) * tx + img[y0, x0]).astype(f)
    bot = ((img[y1, x1] - img[y1, x0]) * tx + img[y1, x0]).astype(f)
    return (((bot - top) * ty) + top).astype(f)

def resize(img, nw, nh, clamp=False):
    H, W = img.shape[:2]
    if (nw, nh) == (W, H): return img
    out = np.zeros((nh, nw) + img.shape[2:], f)
    sx, sy = f(f(W) / f(nw)), f(f(H) / f(nh))
    fy = f(f(f(H) / f(nh)) * f(0.5))
    for y in range(nh):
        fx = f(f(f(W) / f(nw)) * f(0.5))
        for x in range(nw):
            out[y, x] = sample(img, fx, fy, clamp)
            fx = f(fx + sx)
        fy = f(fy + sy)
    return out

def chain(src_rgba, srgb=True):
    """Mips as floats: (rgb gamma-encoded, alpha) per level."""
    lin = tab[src_rgba[..., :3]] if srgb else (src_rgba[..., :3].astype(f) / f(255))
    al = src_rgba[..., 3].astype(f) / f(255)
    levels = [(lin, al)]
    while lin.shape[0] > 1 or lin.shape[1] > 1:
        lin = quarter(lin); al = quarter(al[..., None])[..., 0]
        levels.append((lin, al))
    return levels
