"""Transpile a decompiled vrad3 hit shader (SPIRV-Cross GLSL, as ShaderDumpProbe writes it) to C#.

    python tools/re/glsl_to_cs.py <shader dir name> <static combo> [<shader dir name> <combo> ...]

Reads tools/re/out/shaders/<shader>/s<combo>_f0.hlsl (the closest hit) and, when the
shader's file has one, writes src/Source2.Compiler/Lighting/Bake/Glsl/Shaders/<Class>.g.cs
plus HitShaders.g.cs, the registry of every transpiled variant in that folder.

The C# keeps the GLSL statement for statement: Glsl.GlslVectors/GlslRuntime give the
types and built-ins, HitContext the ray tracing built-ins and the buffers (the
instance, geometry, vertex, index and material data buffers, bindless textures).
Shared structs (Payload_t, MaterialDataMinimal_t, PackedRtxInstanceData_t_natural,
GeometryInfo_t_natural) live in HitContext.cs; the others nest in the class.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SHADERS = os.path.join(HERE, "out", "shaders")
OUT = os.path.join(HERE, "..", "..", "src", "Source2.Compiler", "Lighting", "Bake", "Glsl", "Shaders")

SHARED = {"Payload_t", "MaterialDataMinimal_t", "PackedRtxInstanceData_t_natural", "GeometryInfo_t_natural",
          "_Array_natural_vector_int_4_8", "BuiltInTriangleIntersectionAttributes", "PayloadShadow_t"}
VECTORS = [f"{p}vec{n}" for p in ("", "i", "u", "b") for n in (2, 3, 4)]
MATRICES = ["mat3", "mat4", "mat3x4", "mat4x3"]
SCALARS = ["float", "int", "uint", "bool"]
TYPE_MAP = {"f16vec2": "vec2", "f16vec3": "vec3", "f16vec4": "vec4", "float16_t": "float", "u16vec2": "uvec2"}


def class_name(shader, combo):
    base = shader.replace("_vulkan_60_rtx", "")
    return "".join(p.capitalize() for p in base.split("_")) + f"S{combo}"


def balanced(text, start):
    """Index just past the parenthesis group opening at text[start] == '('."""
    depth = 0
    for i in range(start, len(text)):
        if text[i] == "(":
            depth += 1
        elif text[i] == ")":
            depth -= 1
            if depth == 0:
                return i + 1
    raise ValueError("unbalanced")


def split_args(text):
    out, depth, cur = [], 0, ""
    for ch in text:
        if ch in "([":
            depth += 1
        elif ch in ")]":
            depth -= 1
        if ch == "," and depth == 0:
            out.append(cur.strip())
            cur = ""
        else:
            cur += ch
    if cur.strip():
        out.append(cur.strip())
    return out


def rewrite_textures(body):
    """textureLod / textureGrad on sampler2D(texture, sampler) to HitContext calls."""
    for fn, target in (("textureLod", "ctx.TextureLod"), ("textureGrad", "ctx.TextureGrad"), ("texture", "ctx.Texture")):
        pat = re.compile(r"\b" + fn + r"\(")
        pos = 0
        while True:
            m = pat.search(body, pos)
            if not m:
                break
            open_ = m.end() - 1
            close = balanced(body, open_)
            args = split_args(body[open_ + 1:close - 1])
            sampler = args[0]
            sampler = re.sub(r"^nonuniformEXT\((.*)\)$", r"\1", sampler.strip())
            sm = re.match(r"sampler2D\((.*)\)$", sampler.strip())
            if not sm:
                pos = close
                continue
            tex, samp = split_args(sm.group(1))
            ti = re.match(r"g_bindless_Texture2D(?:_float4)?\[(.*)\]$", tex.strip())
            si = re.match(r"g_bindless_Sampler\[(.*)\]$", samp.strip())
            texture_index = ti.group(1) if ti else tex
            sampler_index = si.group(1) if si else "-1"
            new = f"{target}((int)({texture_index}), (int)({sampler_index}), {', '.join(args[1:])})"
            body = body[:m.start()] + new + body[close:]
            pos = m.start() + len(new)
    return body


def parse_structs(src):
    structs = {}
    for m in re.finditer(r"struct (\w+)\s*\{(.*?)\};", src, re.S):
        fields = []
        for line in m.group(2).split(";"):
            line = line.strip()
            if not line:
                continue
            fm = re.match(r"(\w+)\s+(\w+)(?:\[(\d+)\])?$", line)
            if not fm:
                raise ValueError(f"field: {line}")
            t = TYPE_MAP.get(fm.group(1), fm.group(1))
            fields.append((t, fm.group(2), fm.group(3)))
        structs[m.group(1)] = fields
    return structs


def cs_struct(name, fields):
    lines = [f"    public struct {name}", "    {"]
    for t, f, n in fields:
        lines.append(f"        public {t}{'[]' if n else ''} {f};")
    params = ", ".join(f"{t}{'[]' if n else ''} {f}" for t, f, n in fields)
    assigns = " ".join(f"this.{f} = {f};" for _, f, _ in fields)
    lines.append(f"        public {name}({params}) {{ {assigns} }}")
    lines.append("    }")
    return lines


FLOAT = re.compile(r"(?<![\w.])(\d+\.\d*(?:[eE][+-]?\d+)?|\d+[eE][+-]?\d+)(?![\w.])")


# instance name -> (block name, element type), from the file's buffer declarations
BUFFERS = {}


def parse_buffers(src):
    BUFFERS.clear()
    for m in re.finditer(r"buffer (\w+)\s*\{\s*(\w+) _member0\[\];\s*\} (\w+)(?:\[\])?;", src):
        BUFFERS[m.group(3)] = (m.group(1), m.group(2))


def array_ctors(text):
    """GLSL array constructors, T[](a, b, c), as C# array creations: new T[] { a, b, c }."""
    pat = re.compile(r"\b(\w+)\[\]\(")
    pos = 0
    while True:
        m = pat.search(text, pos)
        if not m:
            return text
        close = balanced(text, m.end() - 1)
        t = TYPE_MAP.get(m.group(1), m.group(1))
        new = f"new {t}[] {{ {text[m.end():close - 1]} }}"
        text = text[:m.start()] + new + text[close:]
        pos = m.start() + len(new)


def rewrite_expr(text, structs):
    text = array_ctors(text)
    text = rewrite_textures(text)
    text = re.sub(r"\bnonuniformEXT\(", "(", text)
    text = text.replace("g_instanceBuffer._member0[", "ctx.Instances[")
    for inst, (block, elem) in BUFFERS.items():
        if elem not in ("float", "int", "uint"):
            continue
        view = {"float": "F", "int": "I", "uint": "U"}[elem]
        word = r"(?<![\w.])" + re.escape(inst)
        if block.startswith("g_MaterialData"):
            text = re.sub(word + r"\._member0\[", f"ctx.Mat{view}[", text)
        elif block.startswith("g_Vertices"):
            text = re.sub(word + r"\[", f"ctx.Vertices{view}[", text)
        elif block.startswith("g_Indices"):
            text = re.sub(word + r"\[", "ctx.Indices[", text)
    text = text.replace("g_Geometries_1._member0[", "ctx.Geometries[")
    text = text.replace("]._member0[", "][")
    text = text.replace("PerViewConstantBuffer_t.g_flGameTime", "ctx.GameTime")
    for k, v in {"gl_InstanceCustomIndexEXT": "ctx.InstanceCustomIndex", "gl_InstanceID": "ctx.InstanceID",
                 "gl_PrimitiveID": "ctx.PrimitiveID", "gl_ObjectToWorldEXT": "ctx.ObjectToWorld",
                 "gl_WorldToObjectEXT": "ctx.WorldToObject", "gl_RayTmaxEXT": "ctx.RayTmax",
                 "gl_HitKindEXT": "ctx.HitKind", "attrs.barycentrics": "ctx.Barycentrics",
                 "gl_WorldRayDirectionEXT": "ctx.WorldRayDirection", "gl_WorldRayOriginEXT": "ctx.WorldRayOrigin"}.items():
        text = re.sub(r"\b" + re.escape(k) + r"\b", v, text)
    # constructors and casts
    text = re.sub(r"\b(" + "|".join(VECTORS + MATRICES + [s for s in structs if s not in ("BuiltInTriangleIntersectionAttributes",)]) + r")\(", r"new \1(", text)
    text = re.sub(r"\bfloat16_t\(", "f16(", text)
    text = re.sub(r"\b(uint|int|float|bool)\(", r"\1_(", text)
    text = FLOAT.sub(lambda m: m.group(1) + "f", text)
    # shift counts: C# wants an int
    text = re.sub(r"(<<|>>) uint_\((\d+)\)", r"\1 \2", text)
    text = re.sub(r"(<<|>>) (\d+)u\b", r"\1 \2", text)
    return text


DECL = re.compile(r"^(\s*)(?:const\s+)?(\w+)\s+(\w+)(\[(\d+)\])?\s*(=.*)?;\s*$")


def known_type(t, structs):
    return t in VECTORS or t in MATRICES or t in SCALARS or t in TYPE_MAP or t in structs


def transpile(shader, combo):
    path = os.path.join(SHADERS, shader, f"s{combo}_f0.hlsl")
    src = open(path, encoding="utf-8").read()
    structs = parse_structs(src)
    parse_buffers(src)
    main = src.index("void main()")
    head, body = src[:main], src[main:]
    body = body[body.index("{") + 1: body.rindex("}")]
    out = []
    cls = class_name(shader, combo)
    out.append(f"// <auto-generated> by tools/re/glsl_to_cs.py from {shader}/s{combo}_f0.hlsl (vrad3's closest hit). </auto-generated>")
    out.append("#pragma warning disable IDE0059, IDE1006, CS0162, CS0164, CS0168, CS0219, CS8981")
    out.append("using static Source2.Compiler.Lighting.Bake.Glsl.GL;")
    out.append("")
    out.append("namespace Source2.Compiler.Lighting.Bake.Glsl.Shaders;")
    out.append("")
    out.append(f"public sealed class {cls} : HitShader")
    out.append("{")
    out.append(f"    public override string Name => \"{shader}\";")
    out.append(f"    public override long Combo => {combo};")
    for name, fields in structs.items():
        if name in SHARED:
            continue
        out.extend(cs_struct(name, fields))
    # globals: constant arrays and undefined placeholders
    for line in head.split("\n"):
        cm = re.match(r"const (\w+) (\w+)\[(\d+)\] = \w+\[\]\((.*)\);$", line.strip())
        if cm:
            t = TYPE_MAP.get(cm.group(1), cm.group(1))
            items = rewrite_expr(cm.group(4), structs)
            out.append(f"    private static readonly {t}[] {cm.group(2)} = [{items}];")
            continue
        gm = re.match(r"(\w+) (_\d+);$", line.strip())
        if gm and known_type(gm.group(1), structs):
            t = TYPE_MAP.get(gm.group(1), gm.group(1))
            out.append(f"    private static readonly {t} {gm.group(2)} = default;")
    # The material data layout: each _RTX_MATERIAL_DATA_ field and the byte offset its constructor argument reads.
    fields = structs.get("_RTX_MATERIAL_DATA_", [])
    cm = re.search(r"_RTX_MATERIAL_DATA_ (_\d+) = _RTX_MATERIAL_DATA_\(", body)
    layout = []
    if cm and fields:
        open_ = cm.end() - 1
        args = split_args(body[open_ + 1:balanced(body, open_) - 1])
        if len(args) != len(fields):
            raise ValueError(f"{len(args)} constructor arguments for {len(fields)} fields")
        for (t, f, n), a in zip(fields, args):
            offs = [int(k) for k in re.findall(r"\(_\d+ \+ (\d+)u\) / 4u", a)]
            if re.search(r"\[_\d+ / 4u\]", a):
                offs.append(0)
            off = min(offs) if offs else 0
            if f.startswith("_g_fl_AutoPad"):
                continue
            layout.append(f"new(\"{f}\", \"{t}\", {off})")
    out.append(f"    public override MaterialField[] Layout {{ get; }} = [{', '.join(layout)}];")
    out.append("")
    out.append("    public override void ClosestHit(HitContext ctx)")
    out.append("    {")
    out.append("        ref Payload_t payload = ref ctx.Payload;")
    for raw in body.split("\n"):
        line = raw.rstrip()
        if not line.strip() or line.strip().startswith("SPIRV_CROSS_"):
            continue
        dm = DECL.match(line)
        if dm and known_type(dm.group(2), structs) and not line.strip().startswith("return"):
            indent, t, var, arr, n, init = dm.groups()
            t = TYPE_MAP.get(t, t)
            if arr and init:
                line = f"{indent}{t}[] {var} {rewrite_expr(init, structs)};"
            elif arr:
                line = f"{indent}{t}[] {var} = new {t}[{n}];"
            elif init:
                line = f"{indent}{t} {var} {rewrite_expr(init, structs)};"
            else:
                line = f"{indent}{t} {var} = default;"
            out.append("    " + line)
            continue
        line = rewrite_expr(line, structs)
        line = line.replace("ignoreIntersectionEXT;", "{ ctx.Ignored = true; return; }")
        out.append("    " + line)
    out.append("    }")
    out.append("}")
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, cls + ".g.cs"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(out) + "\n")
    return cls


RAYGEN_OUT = os.path.join(HERE, "..", "..", "src", "Source2.Compiler", "Lighting", "Bake", "Glsl", "Raygen")


def rewrite_raygen(text, structs):
    """A raygen line: textures, images and ray tracing calls to RaygenShader members, then the common rewrites."""
    text = array_ctors(text)
    # textures: sampler2D / samplerCube over the bindless arrays
    for fn in ("textureLod",):
        pat = re.compile(r"\b" + fn + r"\(")
        pos = 0
        while True:
            m = pat.search(text, pos)
            if not m:
                break
            open_ = m.end() - 1
            close = balanced(text, open_)
            args = split_args(text[open_ + 1:close - 1])
            sampler = re.sub(r"^nonuniformEXT\((.*)\)$", r"\1", args[0].strip())
            sm = re.match(r"(sampler2D|samplerCube)\((.*)\)$", sampler)
            if not sm:
                pos = close
                continue
            tex, samp = split_args(sm.group(2))
            ti = re.match(r"g_bindless_Texture(?:2D|Cube)(?:_float4)?\[(.*)\]$", tex.strip())
            index = ti.group(1) if ti else tex
            if sm.group(1) == "samplerCube":
                new = f"TextureLodCube((int)({index}), {', '.join(args[1:])})"
            else:
                sid = "1" if samp.strip() == "g_CookieSampler" else "0"
                new = f"TextureLod2D((int)({index}), {sid}, {', '.join(args[1:])})"
            text = text[:m.start()] + new + text[close:]
            pos = m.start() + len(new)
    text = re.sub(r"\bimageLoad\(g_RandomSamples, ", "ImageLoadRandom(", text)
    text = re.sub(r"\bimageLoad\(g_(?:Lightmap|Lpv)BlockConverged, ", "ImageLoadConverged(", text)
    # one invocation per subgroup on the CPU: all lanes agree with this one
    text = re.sub(r"\bsubgroupAll\(", "(", text)
    text = re.sub(r"\bimageSize\(g_RandomSamples\)", "RandomSamplesSize", text)
    text = re.sub(r"\btexelFetch\(g_LuxelChartIDs, ([^,]+), 0\)", r"TexelFetchChartIDs(\1)", text)
    text = re.sub(r"\btraceRayEXT\(g_AccelStruct, ", "TraceRay(", text)
    text = re.sub(r"\bsubgroupOr\(", "(", text)
    text = re.sub(r"\bgl_LaunchIDEXT\b", "LaunchID", text)
    text = re.sub(r"\bgl_LaunchSizeEXT\b", "LaunchSize", text)
    text = re.sub(r"\bnonuniformEXT\(", "(", text)
    # constructors, casts, literals, shifts
    text = re.sub(r"\b(" + "|".join(VECTORS + MATRICES + list(structs)) + r")\(", r"new \1(", text)
    text = re.sub(r"\bfloat16_t\(", "f16(", text)
    text = re.sub(r"\b(uint|int|float|bool)\(", r"\1_(", text)
    text = FLOAT.sub(lambda m: m.group(1) + "f", text)
    text = re.sub(r"(<<|>>) uint_\((\d+)\)", r"\1 \2", text)
    text = re.sub(r"(<<|>>) (\d+)u\b", r"\1 \2", text)
    return text


def transpile_raygen(shader, combo, file):
    path = os.path.join(SHADERS, shader, f"s{combo}_f{file}.hlsl")
    src = open(path, encoding="utf-8").read()
    structs = parse_structs(src)
    main = src.index("void main()")
    head, body = src[:main], src[main:]
    body = body[body.index("{") + 1: body.rindex("}")]
    cls = class_name(shader, combo) + f"F{file}"
    out = [f"// <auto-generated> by tools/re/glsl_to_cs.py from {shader}/s{combo}_f{file}.hlsl (vrad3's ray generation). </auto-generated>",
           "#pragma warning disable IDE0059, IDE1006, CS0162, CS0164, CS0168, CS0219, CS8981",
           "using static Source2.Compiler.Lighting.Bake.Glsl.GL;", "",
           "namespace Source2.Compiler.Lighting.Bake.Glsl.Raygen;", "",
           f"public abstract class {cls} : RaygenShader", "{"]
    # structs: one file each in Raygen/Structs, shared by every raygen that declares the same one
    struct_dir = os.path.join(RAYGEN_OUT, "Structs")
    os.makedirs(struct_dir, exist_ok=True)
    for name, fields in structs.items():
        if name in SHARED:
            continue
        text = "\n".join([f"// <auto-generated> by tools/re/glsl_to_cs.py from vrad3's ray generation shaders. </auto-generated>",
                          "namespace Source2.Compiler.Lighting.Bake.Glsl.Raygen;", ""] + [l[4:] for l in cs_struct(name, fields)]) + "\n"
        file = os.path.join(struct_dir, f"{name}.g.cs")
        if os.path.exists(file) and open(file, encoding="utf-8").read() != text:
            raise SystemExit(f"{shader}: struct {name} differs from {file}; give it a shader-specific name")
        with open(file, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
    # buffers: a class per block with its _member0 array, the field under the instance name
    for m in re.finditer(r"buffer (\w+)\s*\{\s*(?:layout\(row_major\)\s*)?(\w+) _member0\[\];\s*\} (\w+);", head):
        t = TYPE_MAP.get(m.group(2), m.group(2))
        out.append(f"    public sealed class Buffer_{m.group(3)} {{ public {t}[] _member0 = []; }}")
        out.append(f"    public readonly Buffer_{m.group(3)} {m.group(3)} = new();")
    # uniform structs and the push constants
    for m in re.finditer(r"layout\(set = \d+\) uniform (\w+) (\w+);", head):
        out.append(f"    public {m.group(1)} {m.group(2)};")
    pm = re.search(r"layout\(push_constant[^)]*\) uniform (\w+)\s*\{(.*?)\} (\w+);", head, re.S)
    if pm:
        fields = []
        for line in pm.group(2).split(";"):
            line = line.strip()
            if line:
                # a matrix's layout(row_major) only changes how the host packs it
                ft, fn = re.sub(r"^layout\([^)]*\)\s*", "", line).split()
                fields.append((TYPE_MAP.get(ft, ft), fn, None))
        out.extend(cs_struct(pm.group(1), fields))
        out.append(f"    public {pm.group(1)} {pm.group(3)};")
    for m in re.finditer(r"rayPayloadEXT (\w+) (\w+);", head):
        out.append(f"    public {m.group(1)} {m.group(2)};")
    for line in head.split("\n"):
        cm = re.match(r"const (\w+) (\w+)\[(\d+)\] = \w+\[\]\((.*)\);$", line.strip())
        if cm:
            t = TYPE_MAP.get(cm.group(1), cm.group(1))
            out.append(f"    private static readonly {t}[] {cm.group(2)} = [{rewrite_raygen(cm.group(4), structs)}];")
            continue
        gm = re.match(r"(\w+) (_\d+);$", line.strip())
        if gm and known_type(gm.group(1), structs):
            out.append(f"    private static readonly {TYPE_MAP.get(gm.group(1), gm.group(1))} {gm.group(2)} = default;")
    out.append("")
    out.append("    public override void Main()")
    out.append("    {")
    first_line = src[:main].count("\n") + 2
    raw_line_no = [0]
    for k, raw in enumerate(body.split("\n")):
        raw_line_no[0] = first_line + k
        line = raw.rstrip()
        if not line.strip() or line.strip().startswith("SPIRV_CROSS_"):
            continue
        dm = DECL.match(line)
        if dm and known_type(dm.group(2), structs) and not line.strip().startswith("return"):
            indent, t, var, arr, n, init = dm.groups()
            t = TYPE_MAP.get(t, t)
            if arr and init:
                line = f"{indent}{t}[] {var} {rewrite_raygen(init, structs)};"
            elif arr:
                line = f"{indent}{t}[] {var} = new {t}[{n}];"
            elif init:
                line = f"{indent}{t} {var} {rewrite_raygen(init, structs)};"
            else:
                line = f"{indent}{t} {var} = default;"
            out.append("    " + line)
            if init and t in ("float", "vec2", "vec3", "vec4"):
                out.append("#if RGTRAP")
                out.append(f"        Trap({var}, {raw_line_no[0]});")
                out.append("#endif")
            continue
        out.append("    " + rewrite_raygen(line, structs))
        # NaN trap (compiled with RGTRAP): after an assignment to a float or vector name, check it.
        am = re.match(r"^\s*(?:(?:float|vec[234]) )?(_\d+|[a-zA-Z]\w*)(?:\.[xyzw])? = .*;\s*$", line)
        if am and not line.strip().startswith(("if", "for", "return")):
            out.append("#if RGTRAP")
            out.append(f"        Trap({am.group(1)}, {raw_line_no[0]});")
            out.append("#endif")
        raw_line_no[0] += 0
    out.append("    }")
    out.append("}")
    os.makedirs(RAYGEN_OUT, exist_ok=True)
    with open(os.path.join(RAYGEN_OUT, cls + ".g.cs"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(out) + "\n")
    return cls


def registry():
    classes = sorted(f[:-5] for f in os.listdir(OUT) if f.endswith(".g.cs") and f != "HitShaders.g.cs")
    lines = ["// <auto-generated> by tools/re/glsl_to_cs.py: every transpiled hit shader variant. </auto-generated>",
             "namespace Source2.Compiler.Lighting.Bake.Glsl.Shaders;", "",
             "public static class HitShaders", "{",
             "    public static readonly HitShader[] All =", "    ["]
    lines += [f"        new {c}()," for c in classes]
    lines += ["    ];", "}"]
    with open(os.path.join(OUT, "HitShaders.g.cs"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")


if __name__ == "__main__":
    args = sys.argv[1:]
    if args and args[0] == "--raygen":
        # --raygen <shader> <combo> <file>
        print(transpile_raygen(args[1], args[2], args[3]))
        sys.exit(0)
    for i in range(0, len(args), 2):
        print(transpile(args[i], args[i + 1]))
    registry()
