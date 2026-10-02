using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>MATATTRS=&lt;out file&gt;</c>): every int, float, vector and
/// string attribute name the compiled materials in the stock paks carry, one
/// per line, for resolving the murmur keys the builders ask materials for.
/// </summary>
public class MaterialAttributeNamesProbe(ITestOutputHelper output)
{
    [Fact]
    public void Collect()
    {
        if (Environment.GetEnvironmentVariable("MATATTRS") is not { } file || CS2Fixtures.StockPak() is not { } pak)
            return;
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in new[] { pak, Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(pak))!, "core", "pak01_dir.vpk") })
        {
            if (!File.Exists(path))
                continue;
            using var package = new Package();
            package.Read(path);
            if (!package.Entries!.TryGetValue("vmat_c", out var entries))
                continue;
            foreach (var entry in entries)
            {
                try
                {
                    package.ReadEntry(entry, out var bytes);
                    using var resource = new Resource();
                    resource.Read(new MemoryStream(bytes));
                    if (resource.DataBlock is not Material m)
                        continue;
                    foreach (var k in m.IntAttributes.Keys.Concat(m.FloatAttributes.Keys).Concat(m.VectorAttributes.Keys).Concat(m.StringAttributes.Keys))
                        names.Add(k);
                }
                catch (Exception)
                {
                }
            }
        }
        File.WriteAllLines(file, names);
        output.WriteLine($"{names.Count} names");
    }
}
