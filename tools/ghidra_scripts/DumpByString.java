// Decompile every function that references a given string.
//
// vrad3.dll is stripped, so there are no function names to match: what it does
// carry is the script-command literals ("lightmap_load_block_gpu") and its RTTI
// type descriptors. Both are strings, and the function that uses one is the
// function that implements it.
//
// headless usage:
//   analyzeHeadless <proj dir> <proj> -process vrad3.dll -noanalysis \
//       -scriptPath D:\tools\ghidra_scripts -postScript DumpByString.java <substring> <outfile>
//@category Analysis

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.DataIterator;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;

import java.io.File;
import java.io.PrintWriter;
import java.util.LinkedHashSet;
import java.util.Set;

public class DumpByString extends GhidraScript
{
    @Override
    public void run() throws Exception
    {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            println("usage: DumpByString <substring> <output file>");
            return;
        }
        String needle = args[0].toLowerCase();

        Set<Function> functions = new LinkedHashSet<>();
        StringBuilder found = new StringBuilder();

        DataIterator data = currentProgram.getListing().getDefinedData(true);
        while (data.hasNext() && !monitor.isCancelled()) {
            Data item = data.next();
            Object value = item.getValue();
            if (!(value instanceof String) || !value.toString().toLowerCase().contains(needle)) {
                continue;
            }
            found.append("// string @ ").append(item.getAddress()).append("  \"").append(value).append("\"\n");
            for (Reference ref : getReferencesTo(item.getAddress())) {
                Function f = getFunctionContaining(ref.getFromAddress());
                if (f != null) {
                    functions.add(f);
                    found.append("//   used by ").append(f.getEntryPoint()).append("\n");
                }
            }
        }

        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        try (PrintWriter out = new PrintWriter(new File(args[1]))) {
            out.println("// " + currentProgram.getName() + "  string: " + args[0]);
            out.print(found);
            for (Function f : functions) {
                out.println();
                out.println("// ======== " + f.getEntryPoint() + " (" + f.getName() + ")");
                DecompileResults result = decompiler.decompileFunction(f, 240, monitor);
                out.println(result.decompileCompleted()
                    ? result.getDecompiledFunction().getC()
                    : "// decompile failed: " + result.getErrorMessage());
            }
        }
        println("DumpByString: " + functions.size() + " function(s) -> " + args[1]);
    }
}
