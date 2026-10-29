using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using Source2.Compiler.Cubemaps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="BarnLightRecords"/> against (entry, record) pairs captured in cubehost from scenesystem 1801b14b0:
/// <c>CUBEBARN=&lt;frida.jsonl of tools/cubehost/barn_record_io.js&gt;</c>. Every word of every record must be the
/// game's, bit for bit; the words that differ are listed with both values.
/// </summary>
public sealed class BarnLightRecordsTests(ITestOutputHelper output)
{
    [Fact]
    public void RecordsEqualTheCapture()
    {
        if (Environment.GetEnvironmentVariable("CUBEBARN") is not { Length: > 0 } path)
            return;
        var pairs = File.ReadLines(path).Select(l => JsonNode.Parse(l)!).Where(n => (string?)n["kind"] == "pair").ToList();
        Assert.NotEmpty(pairs);
        var wrong = new Dictionary<int, int>();
        var shown = 0;
        foreach (var (pair, index) in pairs.Select((p, i) => (p, i)))
        {
            var entry = Convert.FromHexString((string)pair["entry"]!);
            var expected = Convert.FromHexString((string)pair["record"]!);
            var view = Convert.FromHexString((string)pair["view"]!);
            var e540 = Convert.FromHexString((string)pair["e540"]!);
            float F(byte[] b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o));
            var context = new BarnLightRecords.Context(
                new Vector3(F(e540, 0), F(e540, 4), F(e540, 8)),
                new Vector3(F(view, 0x10), F(view, 0x14), F(view, 0x18)),
                BinaryPrimitives.ReadUInt32LittleEndian(Convert.FromHexString((string)pair["flags"]!)),
                Convert.FromHexString((string)pair["g922570"]!)[0] != 0);
            var actual = BarnLightRecords.Record(entry, context);
            for (var o = 0; o < BarnLightRecords.RecordSize; o += 4)
            {
                var a = BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(o));
                var x = BinaryPrimitives.ReadUInt32LittleEndian(expected.AsSpan(o));
                if (a == x)
                    continue;
                wrong[o] = wrong.GetValueOrDefault(o) + 1;
                if (shown++ < 40)
                    output.WriteLine($"pair {index} shape {entry[0x18 + 0xec]} +0x{o:x}: ours {BitConverter.UInt32BitsToSingle(a):R} ({a:x8}), game {BitConverter.UInt32BitsToSingle(x):R} ({x:x8})");
            }
        }
        output.WriteLine($"{pairs.Count} pairs; words differing (offset: pairs): {string.Join(", ", wrong.OrderBy(w => w.Key).Select(w => $"+0x{w.Key:x}: {w.Value}"))}");
        Assert.Empty(wrong);
    }
}
