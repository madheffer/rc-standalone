// Decompile every function that CALLS the function at a given address.
//
// DumpByString finds the function that uses a string, which is usually the one
// implementing a behaviour. When that behaviour never happens at run time the
// question moves up a level: who was supposed to call it, and what did they check
// first. This answers that.
//
// headless usage:
//   analyzeHeadless <proj dir> <proj> -process visbuilder.dll -noanalysis \
//       -scriptPath D:\tools\ghidra_scripts -postScript DumpCallers.java 180049370 <outfile>
//@category Analysis

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;

import java.io.File;
import java.io.PrintWriter;
import java.util.LinkedHashSet;
import java.util.Set;

public class DumpCallers extends GhidraScript
{
    @Override
    public void run() throws Exception
    {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            println("usage: DumpCallers <hex address> <output file>");
            return;
        }

        Address target = currentProgram.getAddressFactory().getAddress(args[0]);
        Function callee = getFunctionContaining(target);
        Set<Function> callers = new LinkedHashSet<>();

        for (Reference ref : getReferencesTo(callee == null ? target : callee.getEntryPoint())) {
            Function f = getFunctionContaining(ref.getFromAddress());
            if (f != null) {
                callers.add(f);
            }
        }

        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);

        try (PrintWriter out = new PrintWriter(new File(args[1]))) {
            out.println("// callee: " + (callee == null ? args[0] : callee.getName() + " @ " + callee.getEntryPoint()));
            out.println("// callers: " + callers.size());
            for (Function caller : callers) {
                out.println();
                out.println("// ======== " + caller.getName() + " @ " + caller.getEntryPoint());
                DecompileResults result = decompiler.decompileFunction(caller, 120, monitor);
                out.println(result.decompileCompleted()
                        ? result.getDecompiledFunction().getC()
                        : "// decompile failed: " + result.getErrorMessage());
            }
        }
        decompiler.dispose();
        println("wrote " + args[1] + " (" + callers.size() + " callers)");
    }
}
