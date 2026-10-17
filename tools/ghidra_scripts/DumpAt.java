// Decompile a named list of addresses.
//
// The name-regex dumper cannot be driven from a batch launcher when the pattern
// needs an alternation: analyzeHeadless.bat is cmd, and a bare '|' ends the
// command line. Virtual methods are exactly the case that needs a list, because
// a vtable slot is a pointer and never a call edge, so nothing else reaches it.
//
// headless usage:
//   analyzeHeadless <proj dir> <proj> -process visbuilder.dll -noanalysis \
//       -scriptPath tools\ghidra_scripts -postScript DumpAt.java <outfile> <addr>...
//@category Analysis

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;

import java.io.File;
import java.io.PrintWriter;

public class DumpAt extends GhidraScript
{
    @Override
    public void run() throws Exception
    {
        String[] args = getScriptArgs();
        if (args.length < 2)
        {
            println("DumpAt: need <outfile> <addr>...");
            return;
        }

        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        PrintWriter out = new PrintWriter(new File(args[0]), "UTF-8");
        out.printf("// %s, %d requested address(es)%n%n", currentProgram.getName(), args.length - 1);

        int written = 0;
        for (int i = 1; i < args.length; i++)
        {
            Address at = currentProgram.getAddressFactory().getAddress(args[i]);
            Function f = at == null ? null : getFunctionContaining(at);
            if (f == null && at != null)
            {
                // A vtable slot that nothing else calls is left undefined by the
                // analyser, and those are precisely the ones worth reading.
                disassemble(at);
                f = createFunction(at, null);
            }
            if (f == null)
            {
                out.printf("// ======== no function at %s%n%n", args[i]);
                continue;
            }
            DecompileResults result = decompiler.decompileFunction(f, 90, monitor);
            out.printf("// ======== %s @ %s  (%d bytes, %d callers)%n",
                       f.getName(), f.getEntryPoint(), f.getBody().getNumAddresses(),
                       f.getCallingFunctions(monitor).size());
            out.println(result.decompileCompleted()
                        ? result.getDecompiledFunction().getC()
                        : "// decompilation failed: " + result.getErrorMessage());
            out.println();
            written++;
        }
        out.close();
        decompiler.dispose();
        println(String.format("DumpAt: %d function(s) -> %s", written, args[0]));
    }
}
