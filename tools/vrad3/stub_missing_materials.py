"""Write placeholder materials for the ones a ported map references and the game
does not have, so resourcecompiler will get as far as the lighting stage.

A community map port names materials that were compiled into its published VPK;
their SOURCES are not in anybody's content tree, and resourcecompiler treats a
missing material as fatal. For lighting work the material only has to exist - a
stub changes albedo, which matters for bounce colour but not for the shape of the
compile or where the time goes.

    python stub_missing_materials.py <addon> <rc log>

Prints how many it wrote. Re-run resourcecompiler afterwards; it usually takes a
couple of rounds, because the compile stops at the first mesh that fails.
"""
import os
import re
import sys

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")

# Both spellings the compiler uses for the same problem.
PATTERNS = [
    re.compile(r"referencing missing material '([^']+\.vmat)'"),
    re.compile(r'Failed loading resource "([^"]+\.vmat)_c"'),
]

STUB = """// Placeholder written by stub_missing_materials.py so the map compiles.
Layer0
{
\tshader "csgo_simple.vfx"
\tF_TRANSLUCENT 0
\t$color "[1.000000 1.000000 1.000000 0.000000]"
}
"""


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    addon, log = sys.argv[1], sys.argv[2]
    content = os.path.join(CS2, "content", "csgo_addons", addon)

    wanted = set()
    with open(log, encoding="utf-8", errors="replace") as f:
        for line in f:
            for pattern in PATTERNS:
                wanted.update(pattern.findall(line))

    written = 0
    for material in sorted(wanted):
        path = os.path.join(content, material.replace("/", os.sep))
        if os.path.exists(path):
            continue
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(STUB)
        written += 1

    print(f"{len(wanted)} referenced, {written} stub(s) written under {content}")


if __name__ == "__main__":
    main()
