// Decompile the virtual methods of a C++ class, found through its RTTI name.
//
// A stripped MSVC binary still names its polymorphic classes: the type
// descriptor ".?AVCSomething@@" is referenced by a complete object locator, the
// locator is pointed at by the slot just before the vtable, and the vtable is a
// run of function pointers. So a class name is enough to reach its code even
// when nothing calls it by name, which is the case for anything the program only
// ever invokes through a base-class pointer.
//
// headless usage:
//   analyzeHeadless <proj dir> <proj> -process visbuilder.dll -noanalysis \
//       -scriptPath D:\tools\ghidra_scripts -postScript DumpClassByRtti.java CLargeClusterRegions <outfile>
//@category Analysis

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.DataIterator;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.MemoryAccessException;
import ghidra.program.model.symbol.Reference;

import java.io.File;
import java.io.PrintWriter;
import java.util.LinkedHashSet;
import java.util.Set;

public class DumpClassByRtti extends GhidraScript
{
    @Override
    public void run() throws Exception
    {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            println("usage: DumpClassByRtti <class name substring> <output file>");
            return;
        }
        String needle = args[0].toLowerCase();

        Set<Function> methods = new LinkedHashSet<>();
        StringBuilder notes = new StringBuilder();

        DataIterator data = currentProgram.getListing().getDefinedData(true);
        while (data.hasNext() && !monitor.isCancelled()) {
            Data item = data.next();
            Object value = item.getValue();
            if (!(value instanceof String)) {
                continue;
            }
            String text = value.toString();
            if (!text.startsWith(".?AV") || !text.toLowerCase().contains(needle)) {
                continue;
            }
            notes.append("// type descriptor ").append(item.getAddress()).append("  ").append(text).append("\n");

            // x64 MSVC stores RTTI cross references as 32 bit RVAs, not as
            // pointers, so getReferencesTo finds nothing here. Scan for the RVA
            // instead: a hit sits inside a complete object locator, and the slot
            // holding a full pointer to that locator is the word before a vtable.
            long imageBase = currentProgram.getImageBase().getOffset();
            int rva = (int) (item.getAddress().getOffset() - imageBase);
            for (Address hit : findBytes((Address) null, littleEndianRegex(rva, 4), 64, 1)) {
                Address locator = hit.subtract(12);
                notes.append("//   locator ").append(locator).append("\n");
                for (Address slot : findBytes((Address) null, littleEndianRegex(locator.getOffset(), 8), 16, 1)) {
                    Address vtable = slot.add(currentProgram.getDefaultPointerSize());
                    notes.append("//     vtable ").append(vtable).append("\n");
                    methods.addAll(readVtable(vtable, notes));
                }
            }
        }

        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        try (PrintWriter out = new PrintWriter(new File(args[1]))) {
            out.print(notes);
            out.println("// methods: " + methods.size());
            for (Function method : methods) {
                out.println();
                out.println("// ======== " + method.getName() + " @ " + method.getEntryPoint());
                DecompileResults result = decompiler.decompileFunction(method, 180, monitor);
                out.println(result.decompileCompleted()
                        ? result.getDecompiledFunction().getC()
                        : "// decompile failed: " + result.getErrorMessage());
            }
        }
        decompiler.dispose();
        println("wrote " + args[1] + " (" + methods.size() + " methods)");
    }

    /// A byte regex matching a little endian value of the given width.
    private static String littleEndianRegex(long value, int width)
    {
        StringBuilder pattern = new StringBuilder();
        for (int i = 0; i < width; i++) {
            pattern.append(String.format("\\x%02x", (int) ((value >> (8 * i)) & 0xFF)));
        }
        return pattern.toString();
    }

    /// Function pointers from a vtable, stopping at the first slot that is not one.
    private Set<Function> readVtable(Address vtable, StringBuilder notes)
    {
        Set<Function> found = new LinkedHashSet<>();
        int pointer = currentProgram.getDefaultPointerSize();
        for (int slot = 0; slot < 64; slot++) {
            try {
                Address entry = toAddr(currentProgram.getMemory().getLong(vtable.add((long) slot * pointer)));
                Function f = getFunctionAt(entry);
                if (f == null) {
                    f = getFunctionContaining(entry);
                }
                if (f == null) {
                    break;
                }
                found.add(f);
            }
            catch (MemoryAccessException | IllegalArgumentException stop) {
                break;
            }
        }
        notes.append("//       ").append(found.size()).append(" virtual methods\n");
        return found;
    }
}
