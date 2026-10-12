import ctypes, struct, numpy as np
dll = ctypes.CDLL(r"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\ati_compress_wrapper.dll")
class Tex(ctypes.Structure):
    _fields_ = [("dwSize", ctypes.c_uint32), ("dwWidth", ctypes.c_uint32), ("dwHeight", ctypes.c_uint32), ("dwPitch", ctypes.c_uint32),
                ("format", ctypes.c_uint32), ("dwDataSize", ctypes.c_uint32), ("pData", ctypes.c_void_p)]
class Opt(ctypes.Structure):
    _fields_ = [("dwSize", ctypes.c_uint32), ("bUseChannelWeighting", ctypes.c_int32), ("fR", ctypes.c_double), ("fG", ctypes.c_double), ("fB", ctypes.c_double),
                ("bUseAdaptiveWeighting", ctypes.c_int32), ("bDXT1UseAlpha", ctypes.c_int32), ("nAlphaThreshold", ctypes.c_uint8), ("pad", ctypes.c_uint8 * 3),
                ("bDisableMultiThreading", ctypes.c_int32), ("nCompressionSpeed", ctypes.c_int32)]
def compress(bgra, fmt, perceptual, dxt1alpha=0):
    h, w = bgra.shape[:2]
    bgra = np.ascontiguousarray(bgra)
    s = Tex(32, w, h, w * 4, 1, bgra.nbytes, bgra.ctypes.data)
    d = Tex(32, w, h, 0, fmt, 0, None)
    d.dwDataSize = dll.ATI_TC_CalculateBufferSize_Wrapper(ctypes.byref(d))
    buf = ctypes.create_string_buffer(d.dwDataSize); d.pData = ctypes.cast(buf, ctypes.c_void_p)
    wts = (0.3086, 0.6094, 0.0820) if perceptual else (1.0, 1.0, 1.0)
    o = Opt(0x38, 1, wts[0], wts[1], wts[2], 1, dxt1alpha, 0, (ctypes.c_uint8 * 3)(), 0, 0)
    r = dll.ATI_TC_ConvertTexture_Wrapper(ctypes.byref(s), ctypes.byref(d), ctypes.byref(o), None, None, None)
    assert r == 0, r
    return buf.raw
