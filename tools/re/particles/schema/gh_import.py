import os, sys, pyghidra
os.environ.setdefault("GHIDRA_INSTALL_DIR", os.path.join(os.environ.get("S2C_GHIDRA_ROOT", "D:/tools"), "ghidra_12.1.3_PUBLIC"))
pyghidra.start()
here = os.path.dirname(os.path.abspath(__file__))
binp = os.path.join(here, sys.argv[1])
with pyghidra.open_program(binp, project_location=os.path.join(here, "gh", sys.argv[2]), project_name=sys.argv[2], analyze=True) as api:
    prog = api.getCurrentProgram()
    print("imported", prog.getName(), prog.getFunctionManager().getFunctionCount())
