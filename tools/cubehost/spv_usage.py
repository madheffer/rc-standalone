"""spv_usage.py <module.spv>...: each SPIR-V module's uniform / storage blocks (set, binding, size) and the
members it reads (byte offset, through OpAccessChain with constant indices), for working out which fields of
the game's constant buffers a face render depends on. Valve's SPIR-V carries no names, so offsets are all
there is; match them against a captured buffer (rdc_stream.py) to give them meaning.
"""
import struct
import sys

OP_NAME = {5: "OpName", 71: "OpDecorate", 72: "OpMemberDecorate", 30: "OpTypeStruct", 32: "OpTypePointer",
           59: "OpVariable", 65: "OpAccessChain", 66: "OpInBoundsAccessChain", 43: "OpConstant", 28: "OpTypeArray",
           21: "OpTypeInt", 22: "OpTypeFloat", 23: "OpTypeVector", 24: "OpTypeMatrix", 29: "OpTypeRuntimeArray"}
DEC_BLOCK, DEC_BUFFER_BLOCK, DEC_OFFSET, DEC_BINDING, DEC_SET, DEC_ARRAY_STRIDE, DEC_MATRIX_STRIDE = 2, 3, 35, 33, 34, 6, 7
STORAGE = {2: "Uniform", 12: "StorageBuffer", 9: "PushConstant", 0: "UniformConstant"}


def parse(path):
    words = struct.unpack("<%dI" % (len(open(path, "rb").read()) // 4), open(path, "rb").read())
    assert words[0] == 0x07230203
    i = 5
    ins = []
    while i < len(words):
        n, op = words[i] >> 16, words[i] & 0xFFFF
        ins.append((op, words[i + 1:i + n]))
        i += n
    return ins


def analyse(path):
    ins = parse(path)
    deco, member_off, types, consts, ptrs, vars_ = {}, {}, {}, {}, {}, {}
    for op, a in ins:
        if op == 71:
            deco.setdefault(a[0], {})[a[1]] = a[2] if len(a) > 2 else True
        elif op == 72 and a[2] == DEC_OFFSET:
            member_off.setdefault(a[0], {})[a[1]] = a[3]
        elif op in (21, 22, 23, 24, 28, 29, 30):
            types[a[0]] = (op, a[1:])
        elif op == 43:
            consts[a[1]] = a[2]
        elif op == 32:
            ptrs[a[0]] = (a[1], a[2])
        elif op == 59:
            vars_[a[1]] = (a[0], a[2])
    out = {}
    for var, (ptype, storage) in vars_.items():
        if storage not in (2, 12, 9):
            continue
        st = ptrs[ptype][1]
        d = deco.get(var, {})
        out[var] = {"storage": STORAGE[storage], "set": d.get(DEC_SET), "binding": d.get(DEC_BINDING), "struct": st,
                    "offsets": member_off.get(st, {}), "reads": set()}
    for op, a in ins:
        if op in (65, 66) and a[2] in out:
            v = out[a[2]]
            if len(a) > 3 and a[3] in consts:
                m = consts[a[3]]
                v["reads"].add(v["offsets"].get(m, -1))
    return out


def size_of(types, t):
    return None


def main():
    for path in sys.argv[1:]:
        print("==", path)
        for var, v in sorted(analyse(path).items(), key=lambda kv: (kv[1]["set"] or 0, kv[1]["binding"] or 0)):
            offs = sorted(v["offsets"].values())
            print("  %s set %s binding %s: %d members (last at +%s), reads %s" % (
                v["storage"], v["set"], v["binding"], len(offs), offs[-1] if offs else "-",
                " ".join("+%d" % r for r in sorted(v["reads"]))))


if __name__ == "__main__":
    main()
