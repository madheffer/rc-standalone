using System.Buffers.Binary;
using Source2.Compiler.Maps;
using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The walkable seeds (CNavGenInput +0x78, 180f21760) from the .vmap against the navinput_seeds.bin
/// tools/re/capture_navgen.py dumps, for every s2c_rc_probe map captured under %TEMP%/navgen_capture/&lt;map&gt;_in.
/// </summary>
public sealed class NavGenSeedTests(ITestOutputHelper output)
{
    private const string Content = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\s2c_rc_probe\maps\";

    [Fact]
    public void SeedsMatchValve()
    {
        var root = Path.Combine(Path.GetTempPath(), "navgen_capture");
        if (!Directory.Exists(root))
            return;
        var bad = 0;
        foreach (var dir in Directory.GetDirectories(root, "*_in"))
        {
            var dump = Path.Combine(dir, "navinput_seeds.bin");
            var map = Path.GetFileName(dir)[..^3];
            var vmap = Path.Combine(Content, map + ".vmap");
            if (!File.Exists(dump) || !File.Exists(vmap))
                continue;
            var b = File.ReadAllBytes(dump);
            var count = BinaryPrimitives.ReadInt32LittleEndian(b);
            if (count < 0 || b.Length != 4 + count * 12)
            {
                output.WriteLine($"{map}: dump is not a seed list ({b.Length} bytes, count {count}); recapture");
                continue;
            }
            var valve = Enumerable.Range(0, count).Select(i => (BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(4 + i * 12)),
                BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(8 + i * 12)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(12 + i * 12)))).ToList();
            var document = DmxBinary.ReadFile(vmap);
            MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(vmap))!));
            var ours = NavGenMapInput.Seeds(document);
            static string Bits((float X, float Y, float Z) p) => $"{BitConverter.SingleToInt32Bits(p.X):x8} {BitConverter.SingleToInt32Bits(p.Y):x8} {BitConverter.SingleToInt32Bits(p.Z):x8}";
            var same = ours.Count == valve.Count && ours.Zip(valve).All(t => Bits(t.First) == Bits(t.Second));
            output.WriteLine($"{map}: Valve {count} [{string.Join("; ", valve)}], ours {ours.Count} [{string.Join("; ", ours)}]{(same ? "" : "  DIFFER")}");
            if (!same)
                bad++;
        }
        Assert.Equal(0, bad);
    }
}
