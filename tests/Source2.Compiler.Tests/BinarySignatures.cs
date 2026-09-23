using System.Buffers.Binary;
using System.Text.Json;

namespace Source2.Compiler.Tests;

/// <summary>
/// Finds visbuilder.dll's addresses in whatever build is installed, from the
/// byte signatures in <c>docs/visbuilder.signatures.json</c>.
///
/// <para>Quoting a raw address only works until Valve rebuilds. CS2 updated on
/// 2026-09-23 and every address in the vis notes moved at once, so anything that
/// wants to read a constant out of the binary has to find it by its SHAPE
/// instead: the bytes around it, with the ones the linker moves blanked out.
/// <c>tools/sigscan.py</c> is the same resolver for the command line, and
/// MakeSignatures.java in the Ghidra scripts directory is what writes the
/// manifest.</para>
///
/// <para>A symbol is only reported when its pattern matches exactly once and
/// every site that resolved agrees on the answer. Anything else is left out, on
/// the grounds that a wrong address is worse than a missing one.</para>
/// </summary>
internal static class BinarySignatures
{
    private sealed record Site(string Pattern, int Disp, int Next);

    private sealed record Symbol(string Name, string Kind, IReadOnlyList<Site> Sites);

    /// <summary>
    /// Every symbol the manifest resolves in <paramref name="dll"/>, by name.
    /// Null when the manifest itself cannot be found.
    /// </summary>
    /// <param name="dll">The visbuilder.dll to search.</param>
    public static IReadOnlyDictionary<string, ulong>? Resolve(string dll)
    {
        ArgumentNullException.ThrowIfNull(dll);
        if (Manifest() is not { } symbols)
            return null;

        var image = File.ReadAllBytes(dll);
        var (imageBase, code, codeBase) = Layout(image);

        var found = new Dictionary<string, ulong>();
        foreach (var symbol in symbols)
        {
            var answers = new HashSet<ulong>();
            foreach (var site in symbol.Sites)
            {
                if (Only(code, Compile(site.Pattern)) is not { } hit)
                    continue;
                var at = codeBase + (ulong)hit;
                if (symbol.Kind == "code")
                {
                    answers.Add(at);
                    continue;
                }
                if (Offset(image, imageBase, at + (ulong)site.Disp) is not { } file)
                    continue;
                var displacement = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(file));
                answers.Add((ulong)((long)at + site.Next + displacement));
            }
            if (answers.Count == 1)
                found[symbol.Name] = answers.Single();
        }
        return found;
    }

    /// <summary>Where the manifest is, walking up from the test binary.</summary>
    public static string? ManifestPath()
    {
        var at = new DirectoryInfo(AppContext.BaseDirectory);
        while (at is not null)
        {
            var candidate = Path.Combine(at.FullName, "docs", "visbuilder.signatures.json");
            if (File.Exists(candidate))
                return candidate;
            at = at.Parent;
        }
        return null;
    }

    private static IReadOnlyList<Symbol>? Manifest()
    {
        if (ManifestPath() is not { } path)
            return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var symbols = new List<Symbol>();
        foreach (var entry in document.RootElement.GetProperty("symbols").EnumerateArray())
        {
            var sites = new List<Site>();
            foreach (var site in entry.GetProperty("sites").EnumerateArray())
                sites.Add(new Site(
                    site.GetProperty("pattern").GetString()!,
                    site.TryGetProperty("disp", out var disp) ? disp.GetInt32() : 0,
                    site.TryGetProperty("next", out var next) ? next.GetInt32() : 0));
            symbols.Add(new Symbol(entry.GetProperty("name").GetString()!,
                                   entry.GetProperty("kind").GetString()!, sites));
        }
        return symbols;
    }

    private static int?[] Compile(string pattern)
    {
        var parts = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wanted = new int?[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            wanted[i] = parts[i] == "??" ? null : Convert.ToInt32(parts[i], 16);
        return wanted;
    }

    /// <summary>The single offset a pattern occurs at, or null when it is not exactly one.</summary>
    private static int? Only(byte[] haystack, int?[] wanted)
    {
        int? at = null;
        for (var i = 0; i + wanted.Length <= haystack.Length; i++)
        {
            var j = 0;
            while (j < wanted.Length && (wanted[j] is null || wanted[j] == haystack[i + j]))
                j++;
            if (j != wanted.Length)
                continue;
            if (at is not null)
                return null;
            at = i;
        }
        return at;
    }

    private static (ulong Base, byte[] Code, ulong CodeBase) Layout(byte[] image)
    {
        var pe = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3c));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 6));
        var optional = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 20));
        var imageBase = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(pe + 48));

        byte[]? code = null;
        ulong codeBase = 0;
        var widest = 0;
        for (var i = 0; i < count; i++)
        {
            var header = pe + 24 + optional + (i * 40);
            var address = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(header + 12));
            var rawSize = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(header + 16));
            var rawOffset = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(header + 20));
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(header + 36));
            if ((flags & 0x20000000) == 0 || rawSize <= widest)
                continue;
            widest = rawSize;
            code = image.AsSpan(rawOffset, rawSize).ToArray();
            codeBase = imageBase + (ulong)address;
        }
        return (imageBase, code ?? [], codeBase);
    }

    /// <summary>The file offset of a virtual address, or null when it is not mapped.</summary>
    public static int? Offset(byte[] image, ulong imageBase, ulong address)
    {
        ArgumentNullException.ThrowIfNull(image);
        var pe = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3c));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 6));
        var optional = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 20));
        var rva = (long)(address - imageBase);
        for (var i = 0; i < count; i++)
        {
            var header = pe + 24 + optional + (i * 40);
            var virtualSize = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(header + 8));
            var virtualAddress = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(header + 12));
            var rawSize = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(header + 16));
            var rawOffset = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(header + 20));
            if (rva >= virtualAddress && rva < virtualAddress + Math.Max(virtualSize, rawSize))
                return (int)(rawOffset + (rva - virtualAddress));
        }
        return null;
    }

    /// <summary>The image base of a PE, which every address is relative to.</summary>
    public static ulong BaseOf(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var pe = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3c));
        return BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(pe + 48));
    }
}
