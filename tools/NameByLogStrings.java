// Name a stripped function after the log string only it references.
//
// Asserts name a function outright and there are seven of them. Log strings do
// not name a function, they name what it DOES -- "Voxelize (%.f units) took %.2f
// seconds (%s nodes)" -- and there are over a hundred. A string referenced from
// exactly ONE function is evidence about that function and nothing else, so the
// name goes on with a Logs_ prefix that says where it came from: nobody should
// read Logs_VoxelizeUnitsTookSeconds as a symbol Valve shipped.
//
// It never overwrites. A function that already carries a real name -- from an
// assert, from RTTI, or from our own signature manifest -- keeps it, because
// those are better evidence than a format string.
//
// This is a Ghidra script, so it has to be copied into the Ghidra scripts
// directory to run; the copy here is the one under version control.
//
// headless usage:
//   copy tools\NameByLogStrings.java <ghidra scripts dir>
//   analyzeHeadless <proj dir> <proj> -process <program> -noanalysis \
//       -scriptPath <ghidra scripts dir> -postScript NameByLogStrings.java <outfile>
//@category Analysis

import ghidra.app.script.GhidraScript;
import ghidra.program.model.data.StringDataInstance;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.DataIterator;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.SourceType;

import java.io.File;
import java.io.PrintWriter;
import java.util.HashSet;
import java.util.Set;

public class NameByLogStrings extends GhidraScript
{
    /** Shorter than this and a string says nothing worth naming a function after. */
    private static final int Shortest = 10;

    /** Characters of the string that reach the name. */
    private static final int Longest = 44;

    @Override
    public void run() throws Exception
    {
        String[] args = getScriptArgs();
        PrintWriter out = args.length > 0
            ? new PrintWriter(new File(args[0]), "UTF-8") : null;

        Set<String> taken = new HashSet<>();
        int named = 0, skippedNamed = 0, skippedShared = 0;
        DataIterator data = currentProgram.getListing().getDefinedData(true);
        while (data.hasNext())
        {
            Data item = data.next();
            String text = Text(item);
            if (text == null || text.length() < Shortest)
                continue;

            // One function, or it is not evidence about any one of them.
            Function only = null;
            boolean shared = false;
            for (Reference ref : getReferencesTo(item.getAddress()))
            {
                Function from = getFunctionContaining(ref.getFromAddress());
                if (from == null)
                    continue;
                if (only == null)
                    only = from;
                else if (!only.equals(from))
                {
                    shared = true;
                    break;
                }
            }
            if (only == null)
                continue;
            if (shared)
            {
                skippedShared++;
                continue;
            }
            if (!only.getName().startsWith("FUN_"))
            {
                skippedNamed++;
                continue;
            }

            String name = Name(text);
            if (name.isEmpty())
                continue;
            String unique = name;
            for (int n = 2; !taken.add(unique); n++)
                unique = name + "_" + n;

            try
            {
                only.setName(unique, SourceType.ANALYSIS);
                named++;
                if (out != null)
                    out.printf("%s\t%s\t%s%n", only.getEntryPoint(), unique, text.trim());
            }
            catch (Exception e)
            {
                println("NameByLogStrings: " + only.getEntryPoint() + ": " + e.getMessage());
            }
        }

        if (out != null)
            out.close();
        println("NameByLogStrings: " + named + " named, " + skippedNamed
              + " already had a better name, " + skippedShared + " strings shared by several");
    }

    private String Text(Data item)
    {
        if (!item.hasStringValue())
            return null;
        Object value = item.getValue();
        if (value instanceof StringDataInstance instance)
            return instance.getStringValue();
        return value == null ? null : value.toString();
    }

    /** 'Voxelize (%.f units) took %.2f seconds' -> 'Logs_VoxelizeUnitsTook'. */
    private String Name(String text)
    {
        StringBuilder made = new StringBuilder("Logs_");
        boolean upper = true;
        for (int i = 0; i < text.length() && made.length() < Longest; i++)
        {
            char c = text.charAt(i);
            if (c == '%')
            {
                // Skip the whole conversion; a name full of d and s says nothing.
                while (i + 1 < text.length() && "-+ #0123456789.*lhz".indexOf(text.charAt(i + 1)) >= 0)
                    i++;
                if (i + 1 < text.length())
                    i++;
                upper = true;
                continue;
            }
            if (Character.isLetterOrDigit(c))
            {
                made.append(upper ? Character.toUpperCase(c) : c);
                upper = false;
            }
            else
            {
                upper = true;
            }
        }
        return made.length() <= "Logs_".length() ? "" : made.toString();
    }
}
