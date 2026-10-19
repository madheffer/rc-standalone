using System.Buffers.Binary;
using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// GenerateConnections (stage 023) against the connection blocks tools/re/capture_navgen.py dumps
/// (023_GenerateConnections_out_conn.bin in <c>NAVGEN_INPUT</c>, default %TEMP%/navgen_capture/probe01_in).
/// </summary>
public sealed class NavGenConnectionTests(ITestOutputHelper output)
{
    private static readonly (int Off, int Size)[] Vectors =
        [(0, 0x68), (0xd6 * 4, 4), (0xe4 * 4, 4), (0xf2 * 4, 4), (0x100 * 4, 4), (0x10e * 4, 4), (0x11c * 4, 4)];

    /// <summary>Each polygon's block as its seven vectors' raw element bytes (null when absent).</summary>
    internal static List<byte[][]?> LoadBlocks(string path)
    {
        var b = File.ReadAllBytes(path);
        var at = 0;
        var blocks = new List<byte[][]?>();
        while (at < b.Length)
        {
            var present = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
            at += 4;
            if (present == 0)
            {
                blocks.Add(null);
                continue;
            }
            var block = b.AsSpan(at, 0x4a8).ToArray();
            at += 0x4a8;
            var vecs = new byte[7][];
            for (var k = 0; k < 7; k++)
            {
                var (off, size) = Vectors[k];
                var count = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(off));
                var outside = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
                at += 4;
                if (outside > 0)
                {
                    vecs[k] = b.AsSpan(at, outside * size).ToArray();
                    at += outside * size;
                }
                else
                    vecs[k] = block.AsSpan(off + 0x18, count * size).ToArray();
            }
            blocks.Add(vecs);
        }
        return blocks;
    }

    private static string? Compare(NavLinks? ours, byte[][]? valve)
    {
        if (ours is null || valve is null)
            return ours is null && valve is null ? null : $"block presence: ours {ours is not null}, Valve {valve is not null}";
        var n = valve[0].Length / 0x68;
        if (ours.Connections.Count != n)
            return $"connections: ours {ours.Connections.Count}, Valve {n}";
        for (var i = 0; i < n; i++)
        {
            var r = valve[0].AsSpan(i * 0x68, 0x68).ToArray();
            var c = ours.Connections[i];
            int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(o));
            long D(int o) => BinaryPrimitives.ReadInt64LittleEndian(r.AsSpan(o));
            static long B(double d) => BitConverter.DoubleToInt64Bits(d);
            long[] want = [I(0), I(4), I(8), I(0xc), D(0x10), D(0x18), D(0x20), D(0x28), D(0x30), D(0x38), D(0x40), D(0x48), D(0x50), D(0x58)];
            long[] got = [c.PolyA, c.EdgeA, c.PolyB, c.EdgeB, B(c.OverlapA), B(c.StartA), B(c.MidA.X), B(c.MidA.Y), B(c.MidA.Z),
                B(c.OverlapB), B(c.StartB), B(c.MidB.X), B(c.MidB.Y), B(c.MidB.Z)];
            for (var k = 0; k < want.Length; k++)
                if (want[k] != got[k])
                    return $"connection {i} field {k}: ours {got[k]:x}, Valve {want[k]:x}";
        }
        int[] Ints(byte[] bytes) => [.. Enumerable.Range(0, bytes.Length / 4).Select(k => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(k * 4)))];
        if (!ours.Slots.SequenceEqual(Ints(valve[1])))
            return $"slots: ours [{string.Join(",", ours.Slots)}], Valve [{string.Join(",", Ints(valve[1]))}]";
        for (var e = 0; e < 5; e++)
            if (!ours.PerEdge[e].SequenceEqual(Ints(valve[2 + e])))
                return $"edge {e}: ours [{string.Join(",", ours.PerEdge[e])}], Valve [{string.Join(",", Ints(valve[2 + e]))}]";
        return null;
    }

    [Fact]
    public void ConnectionsMatchValve()
    {
        var dir = Environment.GetEnvironmentVariable("NAVGEN_INPUT") ?? Path.Combine(Path.GetTempPath(), "navgen_capture", "probe01_in");
        var conn = Path.Combine(dir, "023_GenerateConnections_out_conn.bin");
        if (!File.Exists(conn))
            return;
        var g = NavGenInputTests.LoadStage(Path.Combine(dir, "023_GenerateConnections_in.bin"));
        g.GenerateConnections(new NavGenPolys.ConnectSettings());
        Assert.Null(NavGenInputTests.Compare(g, NavGenInputTests.LoadStage(Path.Combine(dir, "023_GenerateConnections_out.bin"))));
        var blocks = LoadBlocks(conn);
        Assert.Equal(blocks.Count, g.Polys.Count);
        var total = 0;
        for (var i = 0; i < blocks.Count; i++)
        {
            Assert.True(Compare(g.Polys[i].Links, blocks[i]) is null, $"polygon {i}: {Compare(g.Polys[i].Links, blocks[i])}");
            total += g.Polys[i].Links?.Connections.Count ?? 0;
        }
        output.WriteLine($"{g.Polys.Count} polygons, {total} connections, Valve's");
    }
}
