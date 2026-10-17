# Ghidra (PyGhidra) script: dump every ConVar / ConCommand registration in the current program.
#
# CS2 registers convars from small static initialisers of the shape
#     RegisterConVar(&g_cvar, "name", <default bits>, <flags>, "help", &desc)   ; desc+0x60 = EConVarType
# and concommands as
#     RegisterConCommand(&g_cmd, "name", callback, "help", flags, ...)
# We decompile every small function that references a convar-shaped string and pull the
# call's arguments out of the C text. Output: TSV at OUT (set below from the program name).
import re
from ghidra.app.decompiler import DecompInterface
from ghidra.program.model.data import StringDataInstance

OUT = r"D:\_cursor projects\cs2-voice-research\dumps\convars_%s.tsv" % currentProgram.getName()

listing = currentProgram.getListing()
fm = currentProgram.getFunctionManager()
refm = currentProgram.getReferenceManager()

name_rx = re.compile(r"^[a-z][a-z0-9_]{2,63}$")
cands = set()
for d in listing.getDefinedData(True):
    if monitor.isCancelled():
        break
    if not d.hasStringValue():
        continue
    v = str(d.getValue())
    if not name_rx.match(v):
        continue
    for r in refm.getReferencesTo(d.getAddress()):
        f = fm.getFunctionContaining(r.getFromAddress())
        if f is not None and f.getBody().getNumAddresses() < 600:
            cands.add(f)

di = DecompInterface()
di.openProgram(currentProgram)

call_rx = re.compile(r'(FUN_[0-9a-f]+)\((&?[A-Za-z_0-9]+),\s*"([a-z][a-z0-9_]+)",(.*?)\);', re.S)
str_rx = re.compile(r'"((?:[^"\\]|\\.)*)"')
type_rx = re.compile(r"local_28 = (\d+);")

rows = []
for f in sorted(cands, key=lambda f: f.getEntryPoint().getOffset()):
    if monitor.isCancelled():
        break
    res = di.decompileFunction(f, 10, monitor)
    if not res.decompileCompleted():
        continue
    c = res.getDecompiledFunction().getC()
    tm = type_rx.search(c)
    for m in call_rx.finditer(c):
        fn, obj, name, rest = m.groups()
        rest = " ".join(rest.split())
        helps = str_rx.findall(rest)
        help_ = max(helps, key=len) if helps else ""
        args = [a.strip() for a in re.sub(r'"(?:[^"\\]|\\.)*"', '"S"', rest).split(",")]
        rows.append((name, fn, obj, tm.group(1) if tm else "", " | ".join(args), help_,
                     str(f.getEntryPoint())))

with open(OUT, "w") as o:
    o.write("name\tregister_fn\tstorage\ttype_local28\targs_after_name\thelp\tinit_fn\n")
    for r in rows:
        o.write("\t".join(x.replace("\t", " ").replace("\n", "\\n") for x in r) + "\n")
print("candidates %d rows %d -> %s" % (len(cands), len(rows), OUT))
