"""Generate the GLSL vector types the transpiled hit shaders use (Lighting/Bake/Glsl/GlslVectors.g.cs).

    python tools/re/glsl_types_gen.py

vec2-4 (float), ivec2-4 (int), uvec2-4 (uint), bvec2-4 (bool): public fields x y z w,
every read swizzle as a property, constructors for every split of the components
(scalars and smaller vectors of the same family), splats, truncation and family
conversions, and the GLSL arithmetic and bit operators.
"""
import itertools
import os

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "..", "src", "Source2.Compiler", "Lighting", "Bake", "Glsl", "GlslVectors.g.cs")

FAMILIES = [("vec", "float"), ("ivec", "int"), ("uvec", "uint"), ("bvec", "bool")]
LETTERS = "xyzw"


def name(prefix, n):
    return f"{prefix}{n}"


def type_of(prefix, scalar, n):
    return scalar if n == 1 else name(prefix, n)


def splits(n):
    """Ordered compositions of n into parts of 1..4 (more than one part, or one scalar)."""
    if n == 0:
        yield []
        return
    for k in range(1, min(4, n) + 1):
        for rest in splits(n - k):
            yield [k] + rest


def gen_vector(prefix, scalar, n, out):
    t = name(prefix, n)
    comps = LETTERS[:n]
    out.append(f"public struct {t}")
    out.append("{")
    out.append(f"    public {scalar} {', '.join(comps)};")
    # constructors
    seen = set()
    for parts in splits(n):
        if parts == [n]:
            continue
        sig = tuple(parts)
        if sig in seen:
            continue
        seen.add(sig)
        params = []
        body = []
        i = 0
        for j, k in enumerate(parts):
            pt = type_of(prefix, scalar, k)
            params.append(f"{pt} a{j}")
            if k == 1:
                body.append(f"{comps[i]} = a{j};")
                i += 1
            else:
                for c in range(k):
                    body.append(f"{comps[i]} = a{j}.{LETTERS[c]};")
                    i += 1
        out.append(f"    public {t}({', '.join(params)}) {{ {' '.join(body)} }}")
    out.append(f"    public {t}({scalar} s) {{ {' '.join(f'{c} = s;' for c in comps)} }}")
    out.append(f"    public {t}({t} v) {{ {' '.join(f'{c} = v.{c};' for c in comps)} }}")
    for m in range(n + 1, 5):
        out.append(f"    public {t}({name(prefix, m)} v) {{ {' '.join(f'{c} = v.{c};' for c in comps)} }}")
    conv = {"float": "(float)", "int": "(int)", "uint": "(uint)", "bool": ""}
    for p2, s2 in FAMILIES:
        if p2 == prefix:
            continue
        for m in range(n, 5):
            src = name(p2, m)
            if scalar == "bool":
                exprs = [f"{c} = v.{c} != 0;" if s2 != "bool" else f"{c} = v.{c};" for c in comps]
            elif s2 == "bool":
                exprs = [f"{c} = v.{c} ? ({scalar})1 : ({scalar})0;" for c in comps]
            else:
                exprs = [f"{c} = {conv[scalar]}v.{c};" for c in comps]
            out.append(f"    public {t}({src} v) {{ {' '.join(exprs)} }}")
    # indexer
    out.append(f"    public {scalar} this[int i]")
    out.append("    {")
    out.append(f"        readonly get => i switch {{ {', '.join(f'{k} => {c}' for k, c in enumerate(comps))}, _ => throw new System.IndexOutOfRangeException() }};")
    out.append("        set { switch (i) { " + " ".join(f"case {k}: {c} = value; break;" for k, c in enumerate(comps)) + " default: throw new System.IndexOutOfRangeException(); } }")
    out.append("    }")
    out.append(f"    public {scalar} this[uint i] {{ readonly get => this[(int)i]; set => this[(int)i] = value; }}")
    # swizzles (reads)
    for k in range(2, 5):
        for combo in itertools.product(comps, repeat=k):
            sw = "".join(combo)
            rt = name(prefix, k)
            out.append(f"    public readonly {rt} {sw} => new({', '.join(combo)});")
    # operators
    if scalar in ("float", "int", "uint"):
        ops = ["+", "-", "*", "/"]
        if scalar != "float":
            ops += ["%", "&", "|", "^"]
        for op in ops:
            out.append(f"    public static {t} operator {op}({t} a, {t} b) => new({', '.join(f'a.{c} {op} b.{c}' for c in comps)});")
            out.append(f"    public static {t} operator {op}({t} a, {scalar} b) => new({', '.join(f'a.{c} {op} b' for c in comps)});")
            out.append(f"    public static {t} operator {op}({scalar} a, {t} b) => new({', '.join(f'a {op} b.{c}' for c in comps)});")
        if scalar != "uint":
            out.append(f"    public static {t} operator -({t} a) => new({', '.join(f'-a.{c}' for c in comps)});")
        if scalar != "float":
            out.append(f"    public static {t} operator ~({t} a) => new({', '.join(f'~a.{c}' for c in comps)});")
            for st in ("int", t if True else ""):
                pass
            out.append(f"    public static {t} operator <<({t} a, int b) => new({', '.join(f'a.{c} << b' for c in comps)});")
            out.append(f"    public static {t} operator >>({t} a, int b) => new({', '.join(f'a.{c} >> b' for c in comps)});")
            for st in ("uvec", "ivec"):
                out.append(f"    public static {t} operator <<({t} a, {st}{n} b) => new({', '.join(f'a.{c} << (int)b.{c}' for c in comps)});")
                out.append(f"    public static {t} operator >>({t} a, {st}{n} b) => new({', '.join(f'a.{c} >> (int)b.{c}' for c in comps)});")
    out.append(f"    public override readonly string ToString() => $\"{t}({', '.join('{' + c + '}' for c in comps)})\";")
    out.append("}")
    out.append("")


def main():
    out = [
        "// <auto-generated> by tools/re/glsl_types_gen.py: the GLSL vector types of the transpiled hit shaders. </auto-generated>",
        "#pragma warning disable IDE1006, CS8981 // GLSL's lower-case type names",
        "namespace Source2.Compiler.Lighting.Bake.Glsl;",
        "",
    ]
    for prefix, scalar in FAMILIES:
        for n in (2, 3, 4):
            gen_vector(prefix, scalar, n, out)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(out))
    print(OUT, len(out), "lines")


if __name__ == "__main__":
    main()
