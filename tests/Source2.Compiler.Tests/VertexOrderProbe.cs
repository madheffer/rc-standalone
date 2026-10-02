using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>VERTEXORDER=&lt;compiled vpk&gt;</c>): whether each world
/// node draw's vertices sit in the order its index list first uses them
/// (meshopt's optimizeVertexFetch), counted by model kind.
/// </summary>
public class VertexOrderProbe(ITestOutputHelper output)
{
    [Fact]
    public void FirstUseOrder()
    {
        if (Environment.GetEnvironmentVariable("VERTEXORDER") is not { } vpk)
            return;
        using var package = new ValvePak.Package();
        package.Read(vpk);
        var tally = new SortedDictionary<string, int>();
        var shown = 0;
        foreach (var entry in package.Entries!.GetValueOrDefault("vmdl_c") ?? [])
        {
            if (!entry.DirectoryName.Contains("worldnodes", StringComparison.OrdinalIgnoreCase))
                continue;
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            var kind = entry.FileName.Contains("agg_prop") ? "agg_prop" : entry.FileName.Contains("agg_") ? "aggregate" : "plain";
            foreach (var (mesh, _, _, _) in ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD())
                foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                    foreach (var dc in so.GetArray("m_drawCalls"))
                    {
                        var ib = mesh.VBIB.IndexBuffers[dc.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                        int I(int i) => ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4);
                        var start = dc.GetInt32Property("m_nStartIndex");
                        var count = dc.GetInt32Property("m_nIndexCount");
                        var seen = new HashSet<int>();
                        var next = int.MinValue;
                        var firstUse = true;
                        var lo = int.MaxValue;
                        for (var i = start; i < start + count; i++)
                            lo = Math.Min(lo, I(i));
                        next = lo;
                        for (var i = start; i < start + count && firstUse; i++)
                        {
                            var v = I(i);
                            if (seen.Add(v))
                            {
                                firstUse = v == next;
                                next++;
                            }
                        }
                        var key = $"{kind} {(firstUse ? "first-use" : "other")}";
                        tally[key] = tally.GetValueOrDefault(key) + 1;
                        if (!firstUse && shown++ < 5)
                            output.WriteLine($"{entry.FileName}: first indices {string.Join(",", Enumerable.Range(start, Math.Min(24, count)).Select(I))}");
                    }
        }
        output.WriteLine($"TALLY {string.Join(", ", tally.Select(kv => $"{kv.Key} {kv.Value}"))}");
    }
}
