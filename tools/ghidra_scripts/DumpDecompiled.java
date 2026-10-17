// Dump decompiled C for every function whose name matches a regex.
//
// Written for reading Valve's vrad3 without a GUI: the binary carries RTTI, so a
// pattern like "lightmap_load_block" reaches the command class that parses a
// format we need to read ourselves.
//
// headless usage:
//   analyzeHeadless <proj dir> <proj> -process vrad3.dll -noanalysis \
//       -scriptPath D:\tools\ghidra_scripts -postScript DumpDecompiled.java <regex> <outfile>
//@category Analysis

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;

import java.io.File;
import java.io.PrintWriter;
import java.util.regex.Pattern;

public class DumpDecompiled extends GhidraScript
{
    @Override
    public void run() throws Exception
    {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            println("usage: DumpDecompiled <name regex> <output file>");
            return;
        }
        Pattern pattern = Pattern.compile(args[0], Pattern.CASE_INSENSITIVE);

        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);

        int matched = 0;
        try (PrintWriter out = new PrintWriter(new File(args[1]))) {
            out.println("// " + currentProgram.getName() + "  pattern: " + args[0]);
            for (Function f : currentProgram.getFunctionManager().getFunctions(true)) {
                if (!pattern.matcher(f.getName(true)).find()) {
                    continue;
                }
                matched++;
                out.println();
                out.println("// ======== " + f.getName(true) + "  @ " + f.getEntryPoint());
                DecompileResults result = decompiler.decompileFunction(f, 180, monitor);
                out.println(result.decompileCompleted()
                    ? result.getDecompiledFunction().getC()
                    : "// decompile failed: " + result.getErrorMessage());
            }
        }
        println("DumpDecompiled: " + matched + " function(s) -> " + args[1]);
    }
}
