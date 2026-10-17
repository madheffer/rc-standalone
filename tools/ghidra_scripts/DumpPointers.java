// Print the pointers stored at an address, and the function each one lands in.
//
// A thread pool job is dispatched through a functor whose vtable is a plain data
// address in the caller. The work itself is one of those pointers, so reading them
// is how the job body is found when nothing about it is a string.
//
// headless usage:
//   analyzeHeadless <proj dir> <proj> -process visbuilder.dll -noanalysis \
//       -scriptPath D:\tools\ghidra_scripts -postScript DumpPointers.java <addr> <count> <outfile>
//@category Analysis

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;

import java.io.File;
import java.io.PrintWriter;

public class DumpPointers extends GhidraScript
{
    @Override
    public void run() throws Exception
    {
        String[] args = getScriptArgs();
        if (args.length < 3)
        {
            println("DumpPointers: need <addr> <count> <outfile>");
            return;
        }

        Address base = currentProgram.getAddressFactory().getAddress(args[0]);
        int count = Integer.parseInt(args[1]);
        PrintWriter out = new PrintWriter(new File(args[2]), "UTF-8");

        for (int i = 0; i < count; i++)
        {
            Address at = base.add((long) i * 8);
            long value;
            try
            {
                value = currentProgram.getMemory().getLong(at);
            }
            catch (Exception problem)
            {
                out.printf("%s\tunreadable%n", at);
                continue;
            }
            Address points = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(value);
            Function f = getFunctionContaining(points);
            out.printf("%s\t%016x\t%s%n", at, value,
                       f == null ? "-" : f.getName() + " @ " + f.getEntryPoint());
        }
        out.close();
        println("DumpPointers: wrote " + args[2]);
    }
}
