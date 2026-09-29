using System.Buffers.Binary;
using Source2.Compiler.Maps;
using ValvePak;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="TraceScene"/> against the .rte of the same compile: every one of
/// our triangles is found by its geometry, and its flag word and id (its
/// material's resource id) must be the file's. Mismatches are listed by material, ours against the file's.
/// </summary>
public class TraceSceneTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("s2probe", "probe01", "s2c_rc_probe", true)]
    [InlineData("ze_doom_p2", "cardtest", "s2c_rc_probe", true)]
    [InlineData("s2c_lighting", "ze_hold_em_p", "s2c_lighting", true)]
    [InlineData("s2probe", "atixref", "s2probe", true)]
    [InlineData("s2c_big", "ze_ffvii_mako_reactor_v6_p", "s2c_big", true)]
    public void FlagsAreTheFiles(string addon, string map, string compiledIn, bool flags)
    {
        var source = MapFixtures.VmapSource(addon, map);
        var rtePath = Path.Combine(Path.GetTempPath(), "csgo_addons", compiledIn, "maps", map + ".rte");
        if (source is null || !File.Exists(rtePath) || CS2Fixtures.StockPak() is not { } pak || MapFixtures.GameSchema() is not { } schema)
        {
            MapFixtures.Skip($"{map}'s source and .rte");
            return;
        }
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        var packages = new[] { "csgo", "core" }.Select(d => { var p = new Package(); p.Read(Path.Combine(game, d, "pak01_dir.vpk")); return p; }).ToList();
        var visFlags = new MaterialVisFlags.Source([Path.Combine(game, "csgo_addons", addon)], packages);
        bool RendersAsWorld(string c) => schema.IsSolidClass(c) && schema.HasFlag(c, "render_as_world_but_physics_as_entity");
        var meshes = MapMeshes.Read(DmxBinary.ReadFile(source));
        var ours = TraceScene.Triangles(meshes, content.Material, m => visFlags[m], RendersAsWorld);

        var rte = RayTraceEnvironment.ReadFile(rtePath);
        var file = new Dictionary<string, Queue<int>>();
        for (var i = 0; i < rte.TriangleCount; i++)
        {
            var key = Key(rte.FileRecord(i));
            if (!file.TryGetValue(key, out var q))
                file[key] = q = new Queue<int>();
            q.Enqueue(i);
        }
        var record = new byte[48];
        int found = 0, same = 0, sameId = 0;
        var wrongIds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var wrong = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in ours)
        {
            if (!RayTraceEnvironment.RecordFor(t.A, t.B, t.C, t.Flags, record) || !file.TryGetValue(Key(record), out var q) || q.Count == 0)
                continue;
            var at = q.Dequeue();
            found++;
            var theirs = rte.RawFlags(at);
            if (rte.TriangleId(at) == Source2ResourceId.ForPath(t.Material))
                sameId++;
            else
            {
                var k = $"id 0x{rte.TriangleId(at):x16} for {t.Material}";
                wrongIds[k] = wrongIds.GetValueOrDefault(k) + 1;
            }
            if (theirs == t.Flags)
                same++;
            else
            {
                var k = $"ours 0x{t.Flags:x4} file 0x{theirs:x4} {Path.GetFileNameWithoutExtension(t.Material)}";
                if (Environment.GetEnvironmentVariable("TRACE_WHY") == "1" && content.Material(t.Material) is { } info)
                    k += $" [{info.Shader}; {string.Join(" ", info.Ints.Select(p => $"{p.Key}={p.Value}"))}; {string.Join(" ", info.Params.Where(p => p.Key.StartsWith("F_", StringComparison.Ordinal)).Select(p => $"{p.Key}={p.Value}"))}]"
                         + $" node {t.Node} disableShadows={meshes.First(m => m.NodeId == t.Node).Element?.GetValue<int>("disableShadows")}";
                wrong[k] = wrong.GetValueOrDefault(k) + 1;
            }
        }
        foreach (var (k, n) in wrong)
            output.WriteLine($"  {k} x{n}");
        foreach (var (k, n) in wrongIds.Take(10))
            output.WriteLine($"  {k} x{n}");
        output.WriteLine($"{map}: ours {ours.Count}, found {found}, flags same {same}, ids same {sameId}; file {rte.TriangleCount}");
        Assert.Equal(found, sameId);
        if (flags)
            Assert.Equal(found, same);
    }

    private static string Key(ReadOnlySpan<byte> r)
    {
        var parts = new List<string>(13);
        foreach (var k in new[] { 0, 1, 2, 3, 5, 6, 7, 8, 9, 10 })
            parts.Add(BinaryPrimitives.ReadUInt32LittleEndian(r[(k * 4)..]).ToString("x8"));
        parts.Add(r[0x2c].ToString());
        parts.Add(r[0x2d].ToString());
        return string.Join(",", parts);
    }
}

public class MaterialParamsProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("MATPARAMS") is not { Length: > 0 } spec || CS2Fixtures.StockPak() is not { } pak)
            return;
        var parts = spec.Split('|');
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", parts[0]));
        foreach (var m in parts[1].Split(','))
        {
            var info = content.Material(m);
            output.WriteLine($"{m}: shader {info?.Shader}; params {string.Join(" ", info?.Params.Where(p => p.Key.StartsWith("F_")).Select(p => $"{p.Key}={p.Value}") ?? [])}; ints {string.Join(" ", info?.Ints.Select(p => $"{p.Key}={p.Value}") ?? [])}");
        }
    }
}

public class LumpKeyOrderProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("LUMPKEYS") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var source = MapFixtures.VmapSource(p[0], p[1])!;
        var valve = MapFixtures.RcCompiledLumps(source) ?? MapFixtures.AddonLumps(p[0], p[1])!;
        foreach (var (path, bytes) in valve)
            foreach (var e in EntityLumpComparison.Read(bytes, path).Where(e => e.ClassName == p[2]).Take(2))
                output.WriteLine($"{path} {e.ClassName}: {string.Join(" | ", e.KeyOrder.Where(k => Environment.GetEnvironmentVariable("LUMPKEYS_ALL") != null || k.Contains("light") || k.Contains("cube") || k.Contains("handshake") || k.Contains("array") || k.Contains("precomputed") || k.Contains("bright") || k.Contains("direct") || k.Contains("unique") || k.Contains("shadow")).Select(k => $"{k}={e.Values[k].Type}:{e.Values[k].Value}"))}");
    }
}
