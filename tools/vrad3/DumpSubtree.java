// Decompile everything reachable from a root function.
//
// Reading one function at a time answers one question and keeps the shape of the
// thing hidden. A stage of a compiler is a subtree of the call graph, so dumping
// the subtree is how the stage gets read as a whole: the driver, the job it
// dispatches, the per item worker, and the maths underneath.
//
// Stops at a size ceiling so a call into the CRT or protobuf does not drag the
// whole binary in.
//
// headless usage:
//   analyzeHeadless <proj dir> <proj> -process visbuilder.dll -noanalysis \
//       -scriptPath D:\tools\ghidra_scripts -postScript DumpSubtree.java <root> <maxFuncs> <outfile>
//@category Analysis

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayDeque;
import java.util.Deque;
import java.util.LinkedHashSet;
import java.util.Set;

public class DumpSubtree extends GhidraScript
{
    @Override
    public void run() throws Exception
    {
        String[] args = getScriptArgs();
        if (args.length < 3)
        {
            println("DumpSubtree: need <root> <maxFuncs> <outfile>");
            return;
        }

        Address root = currentProgram.getAddressFactory().getAddress(args[0]);
        int limit = Integer.parseInt(args[1]);
        Function start = getFunctionContaining(root);
        if (start == null)
        {
            println("DumpSubtree: no function at " + args[0]);
            return;
        }

        // Breadth first, so the stage's own shape comes out before the leaves it
        // shares with everything else.
        Set<Function> reached = new LinkedHashSet<>();
        Deque<Function> queue = new ArrayDeque<>();
        queue.add(start);
        reached.add(start);
        while (!queue.isEmpty() && reached.size() < limit)
        {
            Function f = queue.poll();
            for (Function callee : f.getCalledFunctions(monitor))
            {
                if (reached.size() >= limit || reached.contains(callee))
                    continue;
                // A thunk or an import is noise; so is anything enormous, which is
                // always the CRT rather than the stage.
                if (callee.isThunk() || callee.isExternal())
                    continue;
                if (callee.getBody().getNumAddresses() > 20000)
                    continue;
                reached.add(callee);
                queue.add(callee);
            }
        }

        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        PrintWriter out = new PrintWriter(new File(args[2]), "UTF-8");
        out.printf("// %s subtree of %s, %d functions%n%n",
                   currentProgram.getName(), start.getEntryPoint(), reached.size());

        int written = 0;
        for (Function f : reached)
        {
            if (monitor.isCancelled())
                break;
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
        println(String.format("DumpSubtree: %d functions from %s -> %s", written, start.getEntryPoint(), args[2]));
    }
}
