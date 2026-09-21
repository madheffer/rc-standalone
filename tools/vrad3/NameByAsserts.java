// Recover real C++ function names from MSVC assert strings, and apply them.
//
// Valve ships these binaries stripped, but every assert carries the fully
// qualified name of the function it sits in plus the source file and line:
//
//   "CVoxelSampler3::MergeClusterSet(), C:/.../src/utils/visbuilder/vis3.cpp:3206"
//
// (written with forward slashes deliberately: javac expands backslash-u as a
// unicode escape even inside a comment, so a literal Windows path here is a
// compile error.)
//
// The function containing a reference to that string IS that function, so a
// whole binary can be named from its own asserts. This renames them in the
// project so every later decompile reads with real names, and writes an
// inventory of what was recovered.
//
// headless usage:
//   analyzeHeadless <proj dir> <proj> -process visbuilder.dll -noanalysis \
//       -scriptPath D:\tools\ghidra_scripts -postScript NameByAsserts.java <outfile>
//@category Analysis

import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.DataIterator;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.SourceType;

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public class NameByAsserts extends GhidraScript
{
    // "Name::Method()" or "Name::Method(), <path>.cpp:1234". The trailing file and
    // line are what make it an assert rather than an ordinary message.
    private static final Pattern ASSERT = Pattern.compile(
            "^([A-Za-z_][A-Za-z0-9_]*(?:<[^>]*>)?(?:::[~A-Za-z_][A-Za-z0-9_]*)+)\\(\\),\\s*(.*\\.(?:cpp|h)):(\\d+)");

    private record Named(String name, String file, String line, String address) { }

    @Override
    public void run() throws Exception
    {
        String[] args = getScriptArgs();
        String outPath = args.length > 0 ? args[0] : null;

        List<Named> recovered = new ArrayList<>();
        Set<String> used = new HashSet<>();
        int renamed = 0;
        int collisions = 0;

        DataIterator data = currentProgram.getListing().getDefinedData(true);
        while (data.hasNext() && !monitor.isCancelled()) {
            Data item = data.next();
            Object value = item.getValue();
            if (!(value instanceof String)) {
                continue;
            }
            Matcher hit = ASSERT.matcher(value.toString().trim());
            if (!hit.find()) {
                continue;
            }

            String qualified = hit.group(1);
            String file = hit.group(2).replace('\\', '/');
            file = file.substring(file.lastIndexOf('/') + 1);

            for (Reference ref : getReferencesTo(item.getAddress())) {
                Function f = getFunctionContaining(ref.getFromAddress());
                if (f == null) {
                    continue;
                }
                // Ghidra symbols cannot hold "::"; keep it readable and unique.
                String symbol = qualified.replace("::", "__").replaceAll("[^A-Za-z0-9_]", "_");
                if (!used.add(symbol + "@" + f.getEntryPoint())) {
                    continue;
                }
                if (!f.getName().startsWith("FUN_")) {
                    collisions++;      // already named by an earlier assert
                    continue;
                }
                try {
                    f.setName(symbol, SourceType.ANALYSIS);
                    renamed++;
                    recovered.add(new Named(qualified, file, hit.group(3), f.getEntryPoint().toString()));
                }
                catch (Exception ignored) {
                    collisions++;
                }
            }
        }

        recovered.sort(Comparator.comparing(Named::file).thenComparing(Named::name));
        println("renamed " + renamed + " functions from asserts (" + collisions + " already named)");

        if (outPath == null) {
            return;
        }
        try (PrintWriter out = new PrintWriter(new File(outPath))) {
            out.println("# " + currentProgram.getName() + " functions recovered from assert strings");
            out.println();
            out.println("Valve ships this stripped, but every assert names the function it sits in,");
            out.println("its source file and its line. " + renamed + " functions were recovered this way.");
            out.println();
            String file = null;
            for (Named row : recovered) {
                if (!row.file().equals(file)) {
                    file = row.file();
                    out.println();
                    out.println("## " + file);
                    out.println();
                    out.println("| address | function | line |");
                    out.println("|---|---|---|");
                }
                out.println("| `" + row.address() + "` | `" + row.name() + "` | " + row.line() + " |");
            }
        }
        println("wrote " + outPath);
    }
}
