using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="WorldNodeModelAuthor"/> against Valve's node models: each one
/// re-authored from its own blocks (encoded buffers, decoded trees, RERL
/// names) must give the same container facts, trees, buffers and references.
/// </summary>
public class WorldNodeModelAuthorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("probe01")]
    [InlineData("cardtest")]
    [InlineData("atixref")]
    public void ReauthorsEveryNodeModel(string map)
    {
        var vpk = Path.Combine(@"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe\maps", map + ".vpk");
        if (!File.Exists(vpk))
            return;
        using var package = new Package();
        package.Read(vpk);
        var failures = new List<string>();
        var count = 0;
        var identical = 0;
        foreach (var entry in package.Entries.GetValueOrDefault("vmdl_c") ?? [])
        {
            var path = entry.GetFullPath();
            if (!path.Contains("/worldnodes/", StringComparison.Ordinal))
                continue;
            package.ReadEntry(entry, out var valve);
            var trees = WorldPhysicsAuthorTests.Trees(valve);
            var refs = References(valve);
            var mine = WorldNodeModelAuthor.Container(BufferBlocks(valve), trees["MDAT"], trees["CTRL"], refs, trees["RED2"], trees["DATA"]);
            count++;
            var report = WorldPhysicsAuthorTests.Compare(valve, mine);
            // RERL's offset field depends on where the block lands (after the
            // compressed KV3 blocks), so it is compared by its references.
            var fa = WorldPhysicsAuthorTests.Facts(valve).Split(' ').Select(x => x.StartsWith("RERL", StringComparison.Ordinal) ? "RERL" : x).ToArray();
            var fb = WorldPhysicsAuthorTests.Facts(mine).Split(' ').Select(x => x.StartsWith("RERL", StringComparison.Ordinal) ? "RERL" : x).ToArray();
            if (fa.SequenceEqual(fb))
                report.RemoveAll(r => r.StartsWith("container:", StringComparison.Ordinal));
            report = report.Select(r => r.StartsWith("container:", StringComparison.Ordinal)
                ? "container: " + string.Join(" ", fa.Zip(fb).Where(p => p.First != p.Second).Select(p => $"{p.First} vs {p.Second}"))
                  + (fa.Length != fb.Length ? $" (blocks {fa.Length} vs {fb.Length}: {string.Join(",", fa.Select(x => x.Split(':')[0]))} vs {string.Join(",", fb.Select(x => x.Split(':')[0]))})" : "")
                : r).ToList();
            foreach (var type in new[] { "MVTX", "MIDX" })
            {
                var a = MeshoptEncoderTests.RawBlocks(valve, type);
                var b = MeshoptEncoderTests.RawBlocks(mine, type);
                if (a.Count != b.Count || a.Zip(b).Any(p => !p.First.AsSpan().SequenceEqual(p.Second)))
                    report.Add($"{type} differs");
            }
            if (!References(valve).SequenceEqual(References(mine)))
                report.Add($"RERL {string.Join(",", References(valve))} vs {string.Join(",", References(mine))}");
            failures.AddRange(report.Take(5).Select(r => $"{Path.GetFileName(path)}: {r}"));
            if (valve.AsSpan().SequenceEqual(mine))
                identical++;
        }
        output.WriteLine($"{map}: {count} node models, {identical} byte-identical, {failures.Count} difference lines");
        foreach (var f in failures.Take(40))
            output.WriteLine(f);
        Assert.Empty(failures);
    }

    /// <summary>The MVTX and MIDX blocks in file order.</summary>
    internal static List<(BlockType Type, byte[] Bytes)> BufferBlocks(byte[] file)
    {
        var blockOffset = BitConverter.ToUInt32(file, 8);
        var count = BitConverter.ToUInt32(file, 12);
        var list = new List<(BlockType, byte[])>();
        for (int i = 0, at = 8 + (int)blockOffset; i < count; i++, at += 12)
        {
            var t = System.Text.Encoding.ASCII.GetString(file, at, 4);
            if (t is not ("MVTX" or "MIDX"))
                continue;
            var offset = BitConverter.ToUInt32(file, at + 4);
            var size = BitConverter.ToUInt32(file, at + 8);
            list.Add((t == "MVTX" ? BlockType.MVTX : BlockType.MIDX, file.AsSpan(at + 4 + (int)offset, (int)size).ToArray()));
        }
        return list;
    }

    internal static List<string> References(byte[] bytes)
    {
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        return resource.ExternalReferences?.ResourceRefInfoList.Select(r => r.Name).ToList() ?? [];
    }
}
