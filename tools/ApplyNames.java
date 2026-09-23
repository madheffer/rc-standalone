// Put our names on a freshly analysed visbuilder.dll.
//
// A rebuilt DLL comes out of analysis as FUN_<address> all the way down, and
// re-deriving every name by hand is the work this whole signature setup exists
// to avoid. tools/sigscan.py resolves the manifest against the new build and
// writes "<name> <address>" lines; this applies them, so the new database opens
// with everything we have already identified already named.
//
// It renames rather than guesses: an address that is not the start of a function
// gets a label instead, an address that already carries one of OUR names is left
// alone, and anything it cannot place is reported rather than forced.
//
// This is a Ghidra script, so it has to be copied into the Ghidra scripts
// directory to run; the copy here is the one under version control.
//
// headless usage:
//   copy tools\ApplyNames.java <ghidra scripts dir>
//   python tools\sigscan.py <new dll> --json scan.json     (then make names.txt)
//   analyzeHeadless <proj dir> <proj> -process <program> -noanalysis \
//       -scriptPath <ghidra scripts dir> -postScript ApplyNames.java names.txt
//@category Analysis

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;
import ghidra.program.model.symbol.Symbol;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileReader;

public class ApplyNames extends GhidraScript
{
    @Override
    public void run() throws Exception
    {
        String[] args = getScriptArgs();
        if (args.length < 1)
        {
            println("ApplyNames: need <names.txt>, lines of '<name> <hex address>'");
            return;
        }

        int functions = 0, labels = 0, already = 0, failed = 0;
        try (BufferedReader in = new BufferedReader(new FileReader(new File(args[0]))))
        {
            String line;
            while ((line = in.readLine()) != null)
            {
                String[] parts = line.trim().split("\\s+");
                if (parts.length != 2)
                    continue;
                String name = parts[0];
                Address at = currentProgram.getAddressFactory().getAddress(parts[1]);
                if (at == null)
                {
                    println("ApplyNames: cannot parse " + parts[1] + " for " + name);
                    failed++;
                    continue;
                }

                Function f = getFunctionAt(at);
                if (f == null && currentProgram.getMemory().getBlock(at) != null
                    && currentProgram.getMemory().getBlock(at).isExecute())
                {
                    // A vtable slot nothing calls is left undefined by analysis,
                    // and those are exactly the ones worth naming.
                    try { disassemble(at); f = createFunction(at, name); } catch (Exception ignored) { }
                }

                try
                {
                    if (f != null)
                    {
                        if (name.equals(f.getName()))
                            already++;
                        else
                        {
                            f.setName(name, SourceType.USER_DEFINED);
                            functions++;
                        }
                        continue;
                    }
                    Symbol existing = getSymbolAt(at);
                    if (existing != null && name.equals(existing.getName()))
                    {
                        already++;
                        continue;
                    }
                    createLabel(at, name, true, SourceType.USER_DEFINED);
                    labels++;
                }
                catch (Exception e)
                {
                    println("ApplyNames: " + name + " @ " + at + ": " + e.getMessage());
                    failed++;
                }
            }
        }
        println("ApplyNames: " + functions + " functions named, " + labels + " labels, "
              + already + " already right, " + failed + " failed");
    }
}
