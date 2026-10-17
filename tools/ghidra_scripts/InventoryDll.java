// Inventory a whole stripped DLL: every function, what it calls, and every string
// it references.
//
// Chasing one log string answers one question and leaves the rest of the binary
// unknown. This dumps the lot in one pass so the call graph can be walked offline:
// a function that logs "Voxelize" IS the voxelizer, and its callees are the stage
// whether or not any of them says anything about itself.
//
// Output is tab separated so it can be post-processed without a parser:
//   FUNC <addr> <name> <size> <callers> <callees>
//   CALL <from> <to>
//   STR  <addr> <function> <text>
//
// headless usage:
//   analyzeHeadless <proj dir> <proj> -process visbuilder.dll -noanalysis \
//       -scriptPath D:\tools\ghidra_scripts -postScript InventoryDll.java <outfile>
//@category Analysis

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.data.StringDataInstance;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.DataIterator;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionIterator;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.symbol.Reference;

import java.io.File;
import java.io.PrintWriter;

public class InventoryDll extends GhidraScript
{
    @Override
    public void run() throws Exception
    {
        String[] args = getScriptArgs();
        if (args.length < 1)
        {
            println("InventoryDll: need an output path");
            return;
        }

        PrintWriter out = new PrintWriter(new File(args[0]), "UTF-8");
        int functions = 0, calls = 0, strings = 0;

        FunctionIterator all = currentProgram.getFunctionManager().getFunctions(true);
        while (all.hasNext() && !monitor.isCancelled())
        {
            Function f = all.next();
            Address at = f.getEntryPoint();
            out.printf("FUNC\t%s\t%s\t%d\t%d\t%d%n", at, f.getName(),
                       f.getBody().getNumAddresses(),
                       f.getCallingFunctions(monitor).size(),
                       f.getCalledFunctions(monitor).size());
            functions++;

            for (Function callee : f.getCalledFunctions(monitor))
            {
                out.printf("CALL\t%s\t%s%n", at, callee.getEntryPoint());
                calls++;
            }
        }

        // Strings, and the function each reference sits in. A string with no
        // function around it is still worth listing: it may be a vtable's name.
        DataIterator data = currentProgram.getListing().getDefinedData(true);
        while (data.hasNext() && !monitor.isCancelled())
        {
            Data d = data.next();
            if (!(d.getValue() instanceof String) && StringDataInstance.getStringDataInstance(d) == null)
                continue;
            Object value = d.getValue();
            if (!(value instanceof String))
                continue;
            String text = ((String) value).replace("\t", " ").replace("\n", "\\n").replace("\r", "");
            if (text.length() < 4)
                continue;

            for (Reference ref : getReferencesTo(d.getAddress()))
            {
                Function owner = getFunctionContaining(ref.getFromAddress());
                out.printf("STR\t%s\t%s\t%s%n", d.getAddress(),
                           owner == null ? "-" : owner.getEntryPoint().toString(), text);
                strings++;
            }
        }

        out.close();
        println(String.format("InventoryDll: %d functions, %d calls, %d string uses -> %s",
                              functions, calls, strings, args[0]));
    }
}
