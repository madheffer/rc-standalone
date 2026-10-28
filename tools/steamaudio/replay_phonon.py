"""replay_phonon.py <capture_dir> [--phonon <phonon.dll>]: replay Valve's reverb bake (a capture_phonon_api.py --seed 0
capture) through phonon.dll in this process and compare the probe batch with the one in Valve's .sareverb.

The calls are Valve's, in Valve's order (steamaudio.dll 1023: the context at 180063f8f, the bake scene at
1800852a7 / 18008549f, the probe batch at 18008864a..1800887c9, the bake at 180087e90, the save at 18007128b),
with the inputs the capture recorded: the static mesh arrays, the 73 probe spheres, the bake parameters (0x80
bytes, 180087e90's local_88 block). Pointers in them are replaced by this process' objects. Every rdtsc that
seeds the Embree simulator (capture_sareverb.RDTSC) is patched to xor eax, eax, i.e. seed 0, as the capture's
pinned run had.

Prints whether the replayed probe batch equals Valve's byte for byte.
"""
import argparse
import base64
import ctypes
import json
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(__file__))
from capture_sareverb import BIN, RDTSC  # noqa: E402

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.VirtualProtect.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32)]
k32.GetModuleHandleW.restype = ctypes.c_void_p
k32.GetModuleHandleW.argtypes = [ctypes.c_wchar_p]
vp = ctypes.c_void_p


def patch_seeds(base):
    for rva in RDTSC:
        at = base + rva
        if ctypes.string_at(at, 2) != b"\x0f\x31":
            raise SystemExit(f"no rdtsc at phonon+{rva:x}: another phonon build")
        old = ctypes.c_uint32()
        k32.VirtualProtect(at, 2, 0x40, ctypes.byref(old))
        ctypes.memmove(at, b"\x31\xc0", 2)
        k32.VirtualProtect(at, 2, old.value, ctypes.byref(old))


def load_capture(d):
    calls = [json.loads(line) for line in open(os.path.join(d, "api.jsonl"), encoding="utf-8")]
    calls = [c for c in calls if c["op"] == "call"]
    meshes = [i for i, c in enumerate(calls) if c["name"] == "iplStaticMeshCreate"]
    bake = next(i for i, c in enumerate(calls) if c["name"] == "iplReflectionsBakerBake")
    # The bake scene is the last one built before the bake; its probes are the SetProbe calls after it.
    mesh_index = max(n for n, i in enumerate(meshes) if i < bake)
    probes = [base64.b64decode(c["struct"]) for c in calls[meshes[mesh_index]:bake] if c["name"] == "iplProbeArraySetProbe"]
    params = base64.b64decode(calls[bake]["struct"])
    mesh = {k: open(os.path.join(d, f"mesh_{mesh_index}_{k}.bin"), "rb").read()
            for k in ("vertices", "triangles", "materialindices", "materials")}
    return mesh, probes, params


def valve_batch(d):
    """The m_pProbeBatch blob of Valve's .sareverb (uncompressed KV3: the blob bytes are stored as they are)."""
    name = next(f for f in os.listdir(d) if f.endswith(".sareverb"))
    return open(os.path.join(d, name), "rb").read()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("capture_dir")
    p.add_argument("--phonon", default=os.path.join(BIN, "phonon.dll"))
    p.add_argument("--out", help="write the replayed probe batch here")
    a = p.parse_args()
    mesh, probes, params = load_capture(a.capture_dir)
    os.add_dll_directory(os.path.dirname(a.phonon))
    ph = ctypes.CDLL(a.phonon)
    patch_seeds(k32.GetModuleHandleW(os.path.basename(a.phonon)))

    def fn(name, *args):
        f = getattr(ph, name)
        f.restype = ctypes.c_int
        f.argtypes = list(args)
        return f

    ctx = vp()
    settings = struct.pack("<IIQQQII", 0x40900, 0, 0, 0, 0, 3, 0)
    assert fn("iplContextCreate", ctypes.c_char_p, ctypes.POINTER(vp))(settings, ctypes.byref(ctx)) == 0
    # The context variables 180063f8f sets (the HRTF normalisation float is left: it only reaches the HRTF).
    setb = fn("iplContextSetVariableBool", vp, ctypes.c_char_p, ctypes.c_int)
    seti = fn("iplContextSetVariableInt32", vp, ctypes.c_char_p, ctypes.c_int)
    setf = fn("iplContextSetVariableFloat32", vp, ctypes.c_char_p, ctypes.c_float)
    setb(ctx, b"enable_dc_correction_for_phase_interpolation", 1)
    setb(ctx, b"enable_iir_order_switching", 1)
    setb(ctx, b"override_reconstruction_type", 1)
    seti(ctx, b"precached_ambisonics_order", 3)
    setb(ctx, b"enable_optix_verbose_logging", 1)

    dev = vp()
    reserved = ctypes.c_char_p(b"\0" * 8)
    assert fn("iplEmbreeDeviceCreate", vp, ctypes.c_char_p, ctypes.POINTER(vp))(ctx, reserved, ctypes.byref(dev)) == 0

    scene = vp()
    scene_settings = struct.pack("<I4xQQQQQQQQ", 1, 0, 0, 0, 0, 0, dev.value, 0, 0)
    assert fn("iplSceneCreate", vp, ctypes.c_char_p, ctypes.POINTER(vp))(ctx, scene_settings, ctypes.byref(scene)) == 0
    bufs = {k: ctypes.create_string_buffer(v, len(v)) for k, v in mesh.items()}
    nv, nt, nm = len(mesh["vertices"]) // 12, len(mesh["triangles"]) // 12, len(mesh["materials"]) // 28
    mesh_settings = struct.pack("<iiii4Q", nv, nt, nm, 0, *(ctypes.addressof(bufs[k]) for k in ("vertices", "triangles", "materialindices", "materials")))
    smesh = vp()
    assert fn("iplStaticMeshCreate", vp, ctypes.c_char_p, ctypes.POINTER(vp))(scene, mesh_settings, ctypes.byref(smesh)) == 0
    fn("iplStaticMeshAdd", vp, vp)(smesh, scene)
    fn("iplSceneCommit", vp)(scene)

    batch, arr = vp(), vp()
    assert fn("iplProbeBatchCreate", vp, ctypes.POINTER(vp))(ctx, ctypes.byref(batch)) == 0
    assert fn("iplProbeArrayCreate", vp, ctypes.POINTER(vp))(ctx, ctypes.byref(arr)) == 0
    fn("iplProbeArrayResize", vp, ctypes.c_int)(arr, len(probes))
    for i, sphere in enumerate(probes):
        fn("iplProbeArraySetProbe", vp, ctypes.c_int, ctypes.c_char_p)(arr, i, sphere)
    fn("iplProbeBatchAddProbeArray", vp, vp)(batch, arr)
    fn("iplProbeArrayRelease", ctypes.POINTER(vp))(ctypes.byref(arr))

    setf(ctx, b"ground_reflection_min_angle", 0.0)
    setb(ctx, b"disable_specular_shadow_rays", 0)
    block = bytearray(params)
    struct.pack_into("<QQ", block, 0, scene.value, batch.value)
    struct.pack_into("<QQ", block, 0x58, 0, 0)  # OpenCL / Radeon Rays devices
    struct.pack_into("<QQ", block, 0x70, 0, 0)  # GPU devices (180087e90 sets them only with a GPU)
    fn("iplReflectionsBakerBake", vp, ctypes.c_char_p, vp, vp)(ctx, bytes(block), None, None)
    setf(ctx, b"ground_reflection_min_angle", 0.0)
    setb(ctx, b"disable_specular_shadow_rays", 0)

    so = vp()
    so_settings = struct.pack("<QQ", 0, 0)
    assert fn("iplSerializedObjectCreate", vp, ctypes.c_char_p, ctypes.POINTER(vp))(ctx, so_settings, ctypes.byref(so)) == 0
    fn("iplProbeBatchSave", vp, vp)(batch, so)
    get_size = ph.iplSerializedObjectGetSize
    get_size.restype, get_size.argtypes = ctypes.c_size_t, [vp]
    get_data = ph.iplSerializedObjectGetData
    get_data.restype, get_data.argtypes = vp, [vp]
    ours = ctypes.string_at(get_data(so), get_size(so))
    if a.out:
        open(a.out, "wb").write(ours)
    valve = valve_batch(a.capture_dir)
    at = valve.find(ours)
    print(f"replayed probe batch: {len(ours)} bytes; in Valve's .sareverb: {'yes, at %d' % at if at >= 0 else 'no'}")
    if at < 0:
        # where the replay first differs from the blob, aligned on the blob's start (same root offset bytes)
        start = valve.find(ours[:64])
        if start >= 0:
            diff = next((i for i in range(len(ours)) if ours[i] != valve[start + i]), None)
            print(f"blob starts at {start}; first difference at blob byte {diff}")
    return 0 if at >= 0 else 1


if __name__ == "__main__":
    sys.exit(main())
