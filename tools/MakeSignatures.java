// Turn a list of addresses into byte signatures that survive a game update.
//
// Every address we quote in the vis notes is a file offset into ONE build. CS2
// updated on 2026-09-23 and every one of them moved, which is not a thing to fix
// by hand a second time. A signature is the bytes AROUND the thing instead, with
// the parts that move blanked out, so the address can be found again in whatever
// build is installed.
//
// What gets blanked is not guessed. Ghidra knows which BITS of an instruction
// encode each operand, so for any operand that carries a reference -- a call's
// rel32, a RIP relative displacement -- getOperandValueMask says exactly which
// bytes to wildcard and the rest of the instruction is kept verbatim.
//
// A data address is not code and has no bytes of its own worth matching, so it
// is signed by the INSTRUCTION that reads it: the pattern covers that
// instruction, and the record says where the displacement sits inside it and
// where the instruction ends, which is all you need to recover the target.
//
// This is the generator for docs/visbuilder.signatures.json. It is a Ghidra
// script, so it has to be copied into the Ghidra scripts directory to run; the
// copy here is the one under version control and the two must not drift.
//
// headless usage:
//   copy tools\MakeSignatures.java <ghidra scripts dir>
//   analyzeHeadless <proj dir> <proj> -process visbuilder.dll -noanalysis \
//       -scriptPath <ghidra scripts dir> -postScript MakeSignatures.java \
//       <outfile.json> <name> <addr> [<name> <addr>...]
//
// Name and address may be joined by '=' or given as two arguments; the batch
// launcher splits on '=' either way.
//@category Analysis

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.lang.Mask;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;

public class MakeSignatures extends GhidraScript
{
    /** Bytes a pattern must reach before uniqueness is even worth testing. */
    private static final int Shortest = 12;

    /** Bytes past which a pattern is not worth carrying, unique or not. */
    private static final int Longest = 512;

    private byte[] text;
    private long textStart;

    @Override
    public void run() throws Exception
    {
        String[] args = getScriptArgs();
        if (args.length < 2)
        {
            println("MakeSignatures: need <outfile.json> <name=addr>...");
            return;
        }

        MemoryBlock code = null;
        for (MemoryBlock block : currentProgram.getMemory().getBlocks())
            if (block.isExecute() && (code == null || block.getSize() > code.getSize()))
                code = block;
        if (code == null)
        {
            println("MakeSignatures: no executable block");
            return;
        }
        text = new byte[(int) code.getSize()];
        code.getBytes(code.getStart(), text);
        textStart = code.getStart().getOffset();

        // The batch launcher splits an argument on '=', so name and address may
        // arrive joined or already apart. Flatten either into one token list and
        // read it in pairs.
        List<String> tokens = new ArrayList<>();
        for (int i = 1; i < args.length; i++)
            for (String piece : args[i].split("="))
                if (!piece.isEmpty())
                    tokens.add(piece);

        List<String> records = new ArrayList<>();
        int made = 0, failed = 0;
        for (int i = 0; i + 1 < tokens.size(); i += 2)
        {
            String name = tokens.get(i);
            String where = tokens.get(i + 1);
            Address at = currentProgram.getAddressFactory().getAddress(where);
            if (at == null)
            {
                println("MakeSignatures: " + name + ": cannot parse " + where);
                failed++;
                continue;
            }


            String record = code.contains(at) ? Code(name, at) : Data(name, at);
            if (record == null)
            {
                println("MakeSignatures: " + name + " @ " + where + ": no unique pattern");
                failed++;
                continue;
            }
            records.add(record);
            made++;
        }

        PrintWriter out = new PrintWriter(new File(args[0]), "UTF-8");
        out.println("{");
        out.printf("  \"image\": \"%s\",%n", currentProgram.getName());
        out.printf("  \"bytes\": %d,%n", currentProgram.getMemory().getSize());
        out.println("  \"symbols\": [");
        for (int i = 0; i < records.size(); i++)
            out.printf("    %s%s%n", records.get(i), i + 1 < records.size() ? "," : "");
        out.println("  ]");
        out.println("}");
        out.close();
        println("MakeSignatures: " + made + " signed, " + failed + " failed -> " + args[0]);
    }

    /** A function or any other code address: sign the code that IS the thing. */
    private String Code(String name, Address at)
    {
        Signature made = Grow(at, 0);
        return made == null ? null
            : String.format("{ \"name\": \"%s\", \"kind\": \"code\", \"was\": \"%s\","
                          + " \"pattern\": \"%s\" }",
                            name, at, made.Pattern);
    }

    /** How many independent read sites to sign one constant from. */
    private static final int Alternates = 4;

    /**
     * A data address: sign the instructions that READ it, and record where the
     * displacement sits so the target can be recovered from a match.
     *
     * <p>Several sites are signed, not one. A constant like FLT_MAX is read from
     * all over the binary, and an update that rewrites one of those functions
     * takes its pattern with it; the next site is usually untouched. Two sites
     * resolving to the same address is also the strongest check available that
     * the answer is right.</p>
     */
    private String Data(String name, Address at)
    {
        List<String> sites = new ArrayList<>();
        ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(at);
        while (refs.hasNext() && sites.size() < Alternates)
        {
            Reference ref = refs.next();
            Instruction from = getInstructionAt(ref.getFromAddress());
            if (from == null)
                continue;

            int[] field = Field(from, ref.getOperandIndex());
            if (field == null || field[1] != 4)
                continue;

            Signature made = Grow(from.getAddress(), 0);
            if (made == null)
                continue;
            sites.add(String.format("{ \"pattern\": \"%s\", \"disp\": %d, \"next\": %d }",
                                    made.Pattern, field[0], from.getLength()));
        }
        if (sites.isEmpty())
            return null;

        StringBuilder all = new StringBuilder();
        for (int i = 0; i < sites.size(); i++)
            all.append(i == 0 ? "" : ", ").append(sites.get(i));
        return String.format("{ \"name\": \"%s\", \"kind\": \"data\", \"was\": \"%s\","
                           + " \"sites\": [%s] }", name, at, all);
    }

    private static final class Signature
    {
        String Pattern;
    }

    /**
     * Append whole instructions from <code>at</code> until the pattern matches
     * exactly once in the code block, blanking every operand that carries a
     * reference because those are the bytes a rebuild moves.
     */
    private Signature Grow(Address at, int unusedDepth)
    {
        StringBuilder pattern = new StringBuilder();
        Address cursor = at;
        int length = 0;
        while (length < Longest)
        {
            Instruction instr = getInstructionAt(cursor);
            if (instr == null)
            {
                try { disassemble(cursor); } catch (Exception ignored) { }
                instr = getInstructionAt(cursor);
            }
            if (instr == null)
                return null;

            byte[] bytes;
            try { bytes = instr.getBytes(); } catch (Exception e) { return null; }
            boolean[] wild = new boolean[bytes.length];
            for (int op = 0; op < instr.getNumOperands(); op++)
            {
                if (instr.getOperandReferences(op).length == 0)
                    continue;
                int[] field = Field(instr, op);
                if (field == null)
                    continue;
                for (int i = field[0]; i < field[0] + field[1] && i < wild.length; i++)
                    wild[i] = true;
            }
            for (int i = 0; i < bytes.length; i++)
            {
                if (pattern.length() > 0)
                    pattern.append(' ');
                pattern.append(wild[i] ? "??" : String.format("%02X", bytes[i]));
            }

            length += bytes.length;
            cursor = cursor.add(bytes.length);
            if (length >= Shortest && Unique(pattern.toString()))
            {
                Signature made = new Signature();
                made.Pattern = pattern.toString();
                return made;
            }
        }
        return null;
    }

    /** Which bytes of an instruction one operand occupies, as {offset, length}. */
    private int[] Field(Instruction instr, int operand)
    {
        Mask mask;
        try { mask = instr.getPrototype().getOperandValueMask(operand); }
        catch (Exception e) { return null; }
        if (mask == null)
            return null;
        byte[] bits = mask.getBytes();
        int first = -1, last = -1;
        for (int i = 0; i < bits.length; i++)
        {
            if (bits[i] == 0)
                continue;
            if (first < 0)
                first = i;
            last = i;
        }
        return first < 0 ? null : new int[] { first, last - first + 1 };
    }

    /** Whether a pattern matches exactly once in the executable block. */
    private boolean Unique(String pattern)
    {
        String[] parts = pattern.split(" ");
        int[] want = new int[parts.length];
        for (int i = 0; i < parts.length; i++)
            want[i] = parts[i].equals("??") ? -1 : Integer.parseInt(parts[i], 16);

        int hits = 0;
        for (int i = 0; i + want.length <= text.length; i++)
        {
            int j = 0;
            while (j < want.length && (want[j] < 0 || want[j] == (text[i + j] & 0xff)))
                j++;
            if (j != want.length)
                continue;
            if (++hits > 1)
                return false;
        }
        return hits == 1;
    }
}
