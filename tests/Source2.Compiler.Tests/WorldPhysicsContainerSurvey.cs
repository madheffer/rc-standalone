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

    /// <summary>
    /// <c>WPSHAPES=1</c>: each local compile's world_physics sphere and capsule
    /// counts, and whether its .vmap source is on disk.
    /// </summary>
    [Fact]
    public void SpheresAndCapsules()
    {
        if (Environment.GetEnvironmentVariable("WPSHAPES") != "1")
            return;
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        foreach (var addon in Directory.GetDirectories(Path.Combine(cs2, "game", "csgo_addons")))
        {
            if (!Directory.Exists(Path.Combine(addon, "maps")))
                continue;
            foreach (var vpk in Directory.GetFiles(Path.Combine(addon, "maps"), "*.vpk"))
            {
                try
                {
                    using var package = new Package();
                    package.Read(vpk);
                    var entry = package.Entries?.SelectMany(kv => kv.Value).FirstOrDefault(e => e.GetFullPath().EndsWith("/world_physics.vmdl_c", StringComparison.Ordinal));
                    if (entry == null)
                        continue;
                    package.ReadEntry(entry, out var bytes);
                    using var res = new ValveResourceFormat.Resource();
                    res.Read(new MemoryStream(bytes));
                    var phys = ((ValveResourceFormat.ResourceTypes.Model)res.DataBlock!).GetEmbeddedPhys()!;
                    var spheres = phys.Parts.Sum(p => p.Shape.Spheres.Length);
                    var capsules = phys.Parts.Sum(p => p.Shape.Capsules.Length);
                    var map = Path.GetFileNameWithoutExtension(vpk);
                    var source = File.Exists(Path.Combine(cs2, "content", "csgo_addons", Path.GetFileName(addon), "maps", map + ".vmap"));
                    output.WriteLine($"{Path.GetFileName(addon)}/{map} {File.GetLastWriteTime(vpk):yyyy-MM-dd}: spheres {spheres} capsules {capsules} hulls {phys.Parts.Sum(p => p.Shape.Hulls.Length)} source {(source ? "yes" : "no")}");
                }
                catch (Exception ex)
                {
                    output.WriteLine($"{vpk}: {ex.GetType().Name} {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// <c>WPSHAPES_WORKSHOP=&lt;Port-Vmaps dir&gt;</c>: the same for every installed
    /// workshop map, and whether Port-Vmaps holds its source.
    /// </summary>
    [Fact]
    public void WorkshopSpheresAndCapsules()
    {
        if (Environment.GetEnvironmentVariable("WPSHAPES_WORKSHOP") is not { Length: > 0 } sources)
            return;
        var vmaps = Directory.GetFiles(sources, "*.vmap", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase);
        foreach (var (map, bytes) in MapFixtures.AllResources("/world_physics.vmdl_c"))
        {
            try
            {
                using var res = new ValveResourceFormat.Resource();
                res.Read(new MemoryStream(bytes));
                var phys = ((ValveResourceFormat.ResourceTypes.Model)res.DataBlock!).GetEmbeddedPhys()!;
                var spheres = phys.Parts.Sum(p => p.Shape.Spheres.Length);
                var capsules = phys.Parts.Sum(p => p.Shape.Capsules.Length);
                output.WriteLine($"{map}: spheres {spheres} capsules {capsules} hulls {phys.Parts.Sum(p => p.Shape.Hulls.Length)} source {(vmaps.ContainsKey(map) ? "yes" : "no")}");
            }
            catch (Exception ex)
            {
                output.WriteLine($"{map}: {ex.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// <c>MODELSHAPES=&lt;game folder&gt;[;...]</c> (or <c>pak</c> for the stock pak01's
    /// models/props): models whose physics has spheres or capsules.
    /// </summary>
    [Fact]
    public void ModelsWithSpheresOrCapsules()
    {
        if (Environment.GetEnvironmentVariable("MODELSHAPES") is not { Length: > 0 } spec)
            return;
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        IEnumerable<(string Name, Func<byte[]> Bytes)> Models(string where)
        {
            if (where != "pak")
                return Directory.GetFiles(where, "*.vmdl_c", SearchOption.AllDirectories).Select(f => (f[(where.Length + 1)..], (Func<byte[]>)(() => File.ReadAllBytes(f))));
            var pak = new Package();
            pak.Read(Path.Combine(cs2, "game", "csgo", "pak01_dir.vpk"));
            return pak.Entries!["vmdl_c"].Where(e => e.GetFullPath().StartsWith(Environment.GetEnvironmentVariable("MODELSHAPES_PREFIX") ?? "models/props", StringComparison.Ordinal))
                .Select(e => (e.GetFullPath(), (Func<byte[]>)(() => { pak.ReadEntry(e, out var b); return b; })));
        }
        foreach (var where in spec.Split(';'))
        {
            var found = 0;
            foreach (var (name, bytes) in Models(where))
            {
                try
                {
                    using var res = new ValveResourceFormat.Resource();
                    res.Read(new MemoryStream(bytes()));
                    if ((res.DataBlock as ValveResourceFormat.ResourceTypes.Model)?.GetEmbeddedPhys() is not { } phys)
                        continue;
                    var spheres = phys.Parts.Sum(p => p.Shape.Spheres.Length);
                    var capsules = phys.Parts.Sum(p => p.Shape.Capsules.Length);
                    if (spheres + capsules == 0)
                        continue;
                    found++;
                    output.WriteLine($"{where}: {name} parts {phys.Parts.Length} spheres {spheres} capsules {capsules} hulls {phys.Parts.Sum(p => p.Shape.Hulls.Length)} meshes {phys.Parts.Sum(p => p.Shape.Meshes.Length)}");
                }
                catch
                {
                }
            }
            output.WriteLine($"{where}: {found} models");
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
