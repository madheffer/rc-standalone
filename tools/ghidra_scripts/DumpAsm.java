// Print the instructions of one function, with operands resolved.
//
// The decompiler is a reading of the code, not the code. A condition with three
// terms and two short circuits is exactly where its parenthesisation has to be
// checked against what the CPU actually does.
//
// headless usage:
//   analyzeHeadless <proj dir> <proj> -process visbuilder.dll -noanalysis \
//       -scriptPath tools\ghidra_scripts -postScript DumpAsm.java <outfile> <addr>...
//@category Analysis

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;

import java.io.File;
import java.io.PrintWriter;

public class DumpAsm extends GhidraScript
{
    @Override
    public void run() throws Exception
    {
        String[] args = getScriptArgs();
        if (args.length < 2)
        {
            println("DumpAsm: need <outfile> <addr>...");
            return;
        }

        PrintWriter out = new PrintWriter(new File(args[0]), "UTF-8");
        for (int i = 1; i < args.length; i++)
        {
            Address at = currentProgram.getAddressFactory().getAddress(args[i]);
            Function f = at == null ? null : getFunctionContaining(at);
            if (f == null)
            {
                out.printf("// ======== no function at %s%n%n", args[i]);
                continue;
            }
            out.printf("// ======== %s @ %s  (%d bytes)%n", f.getName(), f.getEntryPoint(),
                       f.getBody().getNumAddresses());
            InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
            while (it.hasNext())
            {
                Instruction insn = it.next();
                out.printf("%s  %s%n", insn.getAddress(), insn.toString());
            }
            out.println();
        }
        out.close();
        println("DumpAsm: wrote " + args[0]);
    }
}
