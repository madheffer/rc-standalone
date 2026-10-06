using System.Buffers.Binary;
using K4os.Compression.LZ4;
using ValvePak;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>LZ4LEVELS=&lt;vpk&gt;|&lt;path substring&gt;</c>): each KV3 v5 block's two
/// LZ4 buffers decompressed and recompressed at every K4os level, naming the
/// levels whose output equals Valve's bytes.
/// </summary>
public class Lz4LevelProbe(ITestOutputHelper output)
{
    [Fact]
    public void Levels()
    {
        if (Environment.GetEnvironmentVariable("LZ4LEVELS") is not { Length: > 0 } spec || spec.Split('|') is not [var vpk, var part])
            return;
        using var pkg = new Package();
        pkg.Read(vpk);
        var shown = 0;
        foreach (var e in pkg.Entries!.SelectMany(kv => kv.Value).Where(e => e.GetFullPath().Contains(part, StringComparison.OrdinalIgnoreCase)))
        {
            if (shown++ >= 6)
                break;
            var bytes = Io.VpkEntries.Read(pkg, e);
            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
            for (var i = 0; i < count; i++)
            {
                var at = 16 + (i * 12);
                var type = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
                var start = at + 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
                var b = bytes.AsSpan(start);
                if (BinaryPrimitives.ReadUInt32LittleEndian(b) != 0x4B563305 || BinaryPrimitives.ReadUInt32LittleEndian(b[20..]) != 1)
                    continue; // KV3 v5 with LZ4 only
                var u1 = BinaryPrimitives.ReadInt32LittleEndian(b[72..]);
                var c1 = BinaryPrimitives.ReadInt32LittleEndian(b[76..]);
                var u2 = BinaryPrimitives.ReadInt32LittleEndian(b[80..]);
                var c2 = BinaryPrimitives.ReadInt32LittleEndian(b[84..]);
                var report = new List<string>();
                var offset = 120;
                foreach (var (u, c, label) in new[] { (u1, c1, "buf1"), (u2, c2, "buf2") })
                {
                    var compressed = b.Slice(offset, c).ToArray();
                    offset += c;
                    var raw = new byte[u];
                    LZ4Codec.Decode(compressed, raw);
                    var matches = new List<string>();
                    foreach (var level in Enum.GetValues<LZ4Level>())
                    {
                        var target = new byte[LZ4Codec.MaximumOutputSize(u)];
                        var n = LZ4Codec.Encode(raw, target, level);
                        if (target.AsSpan(0, n).SequenceEqual(compressed))
                            matches.Add(level.ToString());
                        else if (level == LZ4Level.L00_FAST)
                            matches.Add($"(fast {n} vs {c})");
                    }
                    report.Add($"{label} {u}->{c}: {string.Join(",", matches)}");
                }
                output.WriteLine($"{e.GetFileName()} {type}: {string.Join(" | ", report)}");
            }
        }
    }
}
