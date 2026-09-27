using Source2.Compiler.Io;
using ValvePak;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="VpkWriter"/>: what it writes reads back through ValvePak, and a
/// map package Valve wrote comes back byte for byte
/// (<c>VPKROUNDTRIP=&lt;map.vpk&gt;[;&lt;map.vpk&gt;...]</c>).
/// </summary>
public class VpkWriterTests(ITestOutputHelper output)
{
    [Fact]
    public void ReadsBack()
    {
        var entries = new Dictionary<string, byte[]>
        {
            ["maps/x/world_physics.vmdl_c"] = [1, 2, 3],
            ["maps/x.vmap_c"] = [4],
            ["maps/x/lightmaps/lightmap_query_data.kv3"] = [5, 6],
            ["README"] = [7],
            ["maps/x/empty.txt"] = [],
        };
        var path = Path.Combine(Path.GetTempPath(), $"vpkwriter_{Environment.ProcessId}.vpk");
        try
        {
            File.WriteAllBytes(path, VpkWriter.Write(entries));
            var back = VpkWriter.ReadAll(path);
            Assert.Equal(entries.Keys.Order(), back.Keys.Order());
            foreach (var (name, data) in entries)
                Assert.Equal(data, back[name]);
            using var package = new Package();
            package.Read(path);
            package.VerifyHashes();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RoundTripsValvesPackages()
    {
        if (Environment.GetEnvironmentVariable("VPKROUNDTRIP") is not { Length: > 0 } spec)
            return;
        var differ = new List<string>();
        foreach (var vpk in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var original = File.ReadAllBytes(vpk);
            var mine = VpkWriter.Write(VpkWriter.ReadAll(vpk));
            var first = Enumerable.Range(0, Math.Min(original.Length, mine.Length)).FirstOrDefault(i => original[i] != mine[i], -1);
            output.WriteLine($"{Path.GetFileName(vpk)}: {original.Length:n0} vs {mine.Length:n0} bytes, first difference {first}");
            if (!original.AsSpan().SequenceEqual(mine))
                differ.Add(vpk);
        }
        Assert.Empty(differ);
    }
}
