using System.Buffers.Binary;
using Source2.Compiler.Cubemaps;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The cube array's container against de_dust2's (the installed game's): RED2 authored from the inputs its
/// own RED2 lists (paths, CRCs) and Valve's DATA, serialized as a compile writes them, equal to the file up
/// to its payload.
/// </summary>
public sealed class CubemapArrayVtexTests(ITestOutputHelper output)
{
    [Fact]
    public void ContainerIsDust2s()
    {
        if (CS2Fixtures.StockPak() is not { } stock)
            return;
        var vpk = Path.Combine(Path.GetDirectoryName(stock)!, "maps", "de_dust2.vpk");
        if (!File.Exists(vpk))
            return;
        var valve = Io.VpkWriter.ReadAll(vpk)["maps/de_dust2/cubemaps/env_cubemap_array.vtex_c"];
        var red2 = WorldPhysicsAuthorTests.Trees(valve)["RED2"];
        var exrs = new List<(string, uint)>();
        string? vtex = null;
        uint vtexCrc = 0;
        foreach (var dep in red2["m_InputDependencies"].Values)
        {
            var path = (string)dep["m_RelativeFilename"]!;
            var crc = Convert.ToUInt32(dep["m_nFileCRC"], System.Globalization.CultureInfo.InvariantCulture);
            if (path.EndsWith(".exr", StringComparison.Ordinal))
                exrs.Add((path, crc));
            else if (path.EndsWith(".vtex", StringComparison.Ordinal))
                (vtex, vtexCrc) = (path, crc);
        }
        Assert.NotNull(vtex);
        var searchPath = (string)red2["m_InputDependencies"].Values.First()["m_SearchPath"]!;
        var ours = CubemapArrayVtex.EditInfo(exrs, vtex!, vtexCrc, searchPath, CubemapArrayVtex.DefaultArguments);
        // de_dust2 predates the 10-15 tier0, whose KV3 writer stores an unsigned 0 or 1 as types 26 / 27
        // (ledger 61); the older writer stored them as the signed 15 / 16.
        OldZeros(ours);

        using var resource = new Resource();
        Source2ContainerAuthor.SetResourceVersion(resource, 1);
        resource.Blocks.Add(AuthoredKv3.Block(ours, KV3IDLookup.Get("generic"), BlockType.RED2, resource));
        resource.Blocks.Add(ResourceBuilder.RawBlock(BlockType.DATA, Data(valve), resource));
        var container = ValveLayout.Serialize(resource);
        output.WriteLine($"{exrs.Count} cubes; container {container.Length} bytes, Valve's file {valve.Length}");
        var first = 0;
        while (first < container.Length && container[first] == valve[first])
            first++;
        if (first < container.Length)
        {
            output.WriteLine($"first difference at {first}: ours {container[first]:x2}, Valve's {valve[first]:x2}");
            var mine = Leaves("", WorldPhysicsAuthorTests.Trees([.. container, .. new byte[valve.Length - container.Length]])["RED2"]).ToList();
            var theirs = Leaves("", red2).ToList();
            foreach (var (a, b) in mine.Zip(theirs).Where(p => p.First != p.Second).Take(10))
                output.WriteLine($"  ours {a} | Valve's {b}");
            if (mine.Count != theirs.Count)
                output.WriteLine($"  leaves: ours {mine.Count}, Valve's {theirs.Count}");
        }
        Assert.Equal(container.Length, first);
    }

    /// <summary>DATA rebuilt from de_dust2's own values (reflectivity, mip sizes, SH floats) is its DATA.</summary>
    [Fact]
    public void DataIsDust2s()
    {
        if (CS2Fixtures.StockPak() is not { } stock)
            return;
        var vpk = Path.Combine(Path.GetDirectoryName(stock)!, "maps", "de_dust2.vpk");
        if (!File.Exists(vpk))
            return;
        var data = Data(Io.VpkWriter.ReadAll(vpk)["maps/de_dust2/cubemaps/env_cubemap_array.vtex_c"]);
        var s = data.AsSpan();
        var reflectivity = Enumerable.Range(0, 4).Select(c => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(4 + 4 * c))).ToArray();
        int size = BinaryPrimitives.ReadUInt16LittleEndian(s[20..]), depth = BinaryPrimitives.ReadUInt16LittleEndian(s[24..]), mips = data[27];
        var method = BinaryPrimitives.ReadUInt32LittleEndian(s[64..]);
        var sizes = Enumerable.Range(0, mips).Select(m => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(76 + 4 * m))).ToArray();
        var shAt = 76 + 4 * mips;
        var count = BinaryPrimitives.ReadInt32LittleEndian(s[(shAt + 4)..]);
        var sh = Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(shAt + 8 + 4 * i))).ToArray();
        output.WriteLine($"{size} x {size} x {depth}, {mips} mips, method {method}, {count} SH floats");
        Assert.Equal(depth * CubemapRadianceSh.FloatsPerCube, count);
        Assert.Equal(data, CubemapArrayVtex.Data(size, depth, mips, reflectivity, sizes, method != 0, sh));
    }

    private static void OldZeros(KVObject node)
    {
        if (node.IsCollection)
        {
            foreach (var key in node.Children.Select(c => c.Key).ToList())
            {
                var child = node[key]!;
                if (!child.IsCollection && !child.IsArray && child.ValueType == KVValueType.UInt32 && (uint)child is 0 or 1)
                    node[key] = new KVObject((long)(uint)child);
                else
                    OldZeros(child);
            }
        }
        else if (node.IsArray)
            foreach (var child in node.Values)
                OldZeros(child);
    }

    private static IEnumerable<string> Leaves(string path, KVObject node)
    {
        if (node.IsCollection)
            return node.Children.SelectMany(c => Leaves($"{path}.{c.Key}", c.Value));
        if (node.IsArray)
            return node.Values.SelectMany((v, i) => Leaves($"{path}[{i}]", v));
        return [$"{path} {node.ValueType} {Convert.ToString(node, System.Globalization.CultureInfo.InvariantCulture)}"];
    }

    private static byte[] Data(byte[] file)
    {
        var blockOffset = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(8));
        var count = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(12));
        for (var i = 0; i < count; i++)
        {
            var at = 8 + blockOffset + i * 12;
            if (System.Text.Encoding.ASCII.GetString(file, at, 4) == "DATA")
                return file.AsSpan(at + 4 + BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 4)), BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 8))).ToArray();
        }
        throw new InvalidDataException("no DATA");
    }
}
