using System.Buffers.Binary;
using ValvePak;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: every local compiled map's world_physics.vmdl_c, its block
/// order and each KV3 block's version, compression and uncompressed size.
/// <c>WPSURVEY=1</c> (scans game/csgo_addons/*/maps/*.vpk).
/// </summary>
public class WorldPhysicsContainerSurvey(ITestOutputHelper output)
{
    [Fact]
    public void Survey()
    {
        if (Environment.GetEnvironmentVariable("WPSURVEY") != "1")
            return;
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var vpks = Directory.GetDirectories(Path.Combine(cs2, "game", "csgo_addons"))
            .SelectMany(a => Directory.Exists(Path.Combine(a, "maps")) ? Directory.GetFiles(Path.Combine(a, "maps"), "*.vpk") : [])
            .Where(f => !f.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase) || true);
        foreach (var vpk in vpks)
        {
            try
            {
                using var package = new Package();
                package.Read(vpk);
                var entry = package.Entries?.SelectMany(kv => kv.Value).FirstOrDefault(e => e.GetFullPath().EndsWith("/world_physics.vmdl_c", StringComparison.Ordinal));
                if (entry == null)
                    continue;
                package.ReadEntry(entry, out var bytes);
                output.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vpk)))}/{Path.GetFileName(vpk)} {File.GetLastWriteTime(vpk):yyyy-MM-dd}: {Describe(bytes)}");
            }
            catch (Exception ex)
            {
                output.WriteLine($"{vpk}: {ex.GetType().Name}");
            }
        }
    }

    // The block table, and for each KV3 v5 block its compression method and sizes.
    internal static string Describe(byte[] bytes)
    {
        var parts = new List<string> { $"v{BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6))}" };
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        for (var b = 0; b < count; b++)
        {
            var at = 16 + (b * 12);
            var fourCc = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            var offset = at + 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 8));
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            var version = magic & 0xff;
            var method = size >= 24 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 20)) : 0;
            var payload = size >= 52 ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 48)) : 0;
            var h = Convert.ToHexString(bytes, offset + 20, Math.Min(64, size - 20));
            parts.Add($"{fourCc}(m{method} {size}b payload {payload} hdr {h})");
        }
        return string.Join(" ", parts);
    }
}
