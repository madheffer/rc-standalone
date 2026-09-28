using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>Scratch (LUMPDUMP=vpk|keys): every entity of every lump in a package, with the listed keys.</summary>
public class LumpDumpProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("LUMPDUMP") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        using var pkg = new ValvePak.Package();
        pkg.Read(parts[0]);
        foreach (var entry in pkg.Entries.GetValueOrDefault("vents_c") ?? [])
            foreach (var e in EntityLumpComparison.Read(Io.VpkEntries.Read(pkg, entry), entry.GetFullPath()))
                output.WriteLine($"{entry.FileName} {e.ClassName} " + string.Join(" ", parts[1].Split(',').Select(k => $"{k}={(e.Values.TryGetValue(k, out var v) ? v.Value : "-")}")));
    }
}
