"""gh_dec.py <program> <addr>... : decompile functions (creating them if needed) into decomp/<program>_<addr>.c"""
import os, sys, pyghidra
os.environ.setdefault("GHIDRA_INSTALL_DIR", os.path.join(os.environ.get("S2C_GHIDRA_ROOT", "D:/tools"), "ghidra_12.1.3_PUBLIC"))
pyghidra.start()
here = os.path.dirname(os.path.abspath(__file__))
prog_name = sys.argv[1]; addrs = sys.argv[2:]
if len(addrs) == 1 and os.path.exists(addrs[0]): addrs = open(addrs[0]).read().split()
with pyghidra.open_project(os.environ.get("GHPDIR", os.path.join(here, "gh", "schema")), os.environ.get("GHP","schema")) as project:
    with pyghidra.program_context(project, "/" + prog_name) as prog:
        from ghidra.app.decompiler import DecompInterface
        from ghidra.util.task import ConsoleTaskMonitor
        from ghidra.app.cmd.function import CreateFunctionCmd
        di = DecompInterface(); di.openProgram(prog)
        af = prog.getAddressFactory(); fm = prog.getFunctionManager()
        mon = ConsoleTaskMonitor()
        for a in addrs:
            ad = af.getAddress(a)
            f = fm.getFunctionAt(ad) or fm.getFunctionContaining(ad)
            if f is None:
                tx = prog.startTransaction("mk")
                CreateFunctionCmd(ad).applyTo(prog, mon)
                prog.endTransaction(tx, True)
                f = fm.getFunctionAt(ad) or fm.getFunctionContaining(ad)
            if f is None: print("nofunc", a); continue
            r = di.decompileFunction(f, 180, mon)
            c = r.getDecompiledFunction().getC() if r.decompileCompleted() else "FAILED " + str(r.getErrorMessage())
            out = os.path.join(here, "decomp", f"{prog_name}_{a.lower().replace('0x','')}.c")
            open(out, "w", encoding="utf-8").write(f"// {prog_name} {a} {f.getName()} entry {f.getEntryPoint()}\n" + c)
            print("ok", a, f.getName(), len(c))
