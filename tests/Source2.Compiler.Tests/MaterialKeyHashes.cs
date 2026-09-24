using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: names for material keys a DLL looks up by hash. Hashes every
/// int, float, vector and texture parameter and attribute name of the
/// materials whose path contains a filter (MurmurHash2 of the lowercase name,
/// seed 0x31415926, the key hash the map builder uses) and prints the ones in
/// the list. <c>MATHASH=&lt;hex,hex,...&gt;|&lt;path filter&gt;</c>.
/// </summary>
public class MaterialKeyHashes(ITestOutputHelper output)
{
    [Fact]
    public void NamesForHashes()
    {
        if (Environment.GetEnvironmentVariable("MATHASH") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var wanted = parts[0].Split(',').Select(h => Convert.ToUInt32(h, 16)).ToHashSet();
        var game = Path.Combine(Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive", "game");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var seen = 0;
        foreach (var dir in new[] { "csgo", "core" })
        {
            using var package = new Package();
            package.Read(Path.Combine(game, dir, "pak01_dir.vpk"));
            foreach (var entry in package.Entries.GetValueOrDefault("vmat_c") ?? [])
            {
                if (!entry.GetFullPath().Contains(parts[1], StringComparison.OrdinalIgnoreCase) || seen++ > 400)
                    continue;
                package.ReadEntry(entry, out var bytes);
                using var resource = new Resource();
                resource.Read(new MemoryStream(bytes));
                if (resource.DataBlock is not Material mat)
                    continue;
                foreach (var k in mat.IntParams.Keys.Concat(mat.FloatParams.Keys).Concat(mat.VectorParams.Keys).Concat(mat.TextureParams.Keys)
                                     .Concat(mat.IntAttributes.Keys).Concat(mat.StringAttributes.Keys).Concat(mat.FloatAttributes.Keys).Concat(mat.VectorAttributes.Keys))
                    names.Add(k);
            }
        }
        output.WriteLine($"{names.Count} names from {seen} materials");
        foreach (var n in names.Order(StringComparer.Ordinal))
            if (wanted.Contains(Murmur2(n.ToLowerInvariant())))
                output.WriteLine($"{Murmur2(n.ToLowerInvariant()):x8} {n}");
    }

    private static uint Murmur2(string s, uint seed = 0x31415926)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(s);
        const uint m = 0x5bd1e995;
        var h = seed ^ (uint)data.Length;
        var i = 0;
        for (; data.Length - i >= 4; i += 4)
        {
            var k = BitConverter.ToUInt32(data, i) * m;
            k ^= k >> 24;
            h = (h * m) ^ (k * m);
        }
        switch (data.Length - i)
        {
            case 3: h ^= (uint)data[i + 2] << 16; goto case 2;
            case 2: h ^= (uint)data[i + 1] << 8; goto case 1;
            case 1: h ^= data[i]; h *= m; break;
        }
        h ^= h >> 13;
        h *= m;
        return h ^ (h >> 15);
    }
}
