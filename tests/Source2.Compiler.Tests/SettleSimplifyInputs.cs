using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Maps;
using Source2.Compiler.Physics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The pre-settle world's simplified soups (<see cref="SettleWorld"/> through
/// <see cref="WorldCollision.Simplify(float, Vector3[], int[])"/>) against
/// resourcecompiler's CMesh_Simplify calls in a compile
/// (<c>tools/physics/capture_mapsimplify.py</c>), matched by content after the
/// 1/32 weld: <c>SETTLESIMPLIFY=&lt;addon&gt;|&lt;map&gt;|&lt;capture&gt;</c>.
/// </summary>
public class SettleSimplifyInputs(ITestOutputHelper output)
{
    /// <summary>CMesh_Simplify's call to the loop in the installed build (rc 0923: 1813cb476).</summary>
    private const int CMeshCaller = 20760555;

    [Fact]
    public void SoupsMatchTheCapture()
    {
        if (Environment.GetEnvironmentVariable("SETTLESIMPLIFY") is not { Length: > 0 } spec || CS2Fixtures.StockPak() is not { } pak || MapFixtures.GameSchema() is not { } schema)
            return;
        var parts = spec.Split('|');
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var vmap = Path.Combine(cs2, "content", "csgo_addons", parts[0], "maps", parts[1] + ".vmap");
        var document = DmxBinary.ReadFile(vmap);
        MapPrefabs.Attach(document, MapPrefabs.FromContent(Path.Combine(cs2, "content", "csgo_addons", parts[0])));
        using var models = new SettleBuildTests.PakModels(pak, Path.Combine(cs2, "game", "csgo_addons", parts[0]));
        var ours = new List<string>();
        WorldCollision.SimplifyTrace = (v, stride, idx, e) =>
        {
            var (welded, wi) = v.Length == 0 ? (v, idx) : MeshWeld.Weld(v, stride, idx, [new MeshWeld.Stream("position", 0, 3, false, 42)], 1f / 32f);
            ours.Add(Key(welded, wi, e));
        };
        try
        {
            SettleWorld.Build(document, models, schema, SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators),
                d => SmartProps.NodesCreatedOnLoad(d, MapFixtures.SmartPropLocators), new SettleWorld.CollisionRules());
        }
        finally
        {
            WorldCollision.SimplifyTrace = null;
        }
        var records = SimplifyReplay.Records(parts[2]).ToList();
        var valve = new List<string>();
        for (var k = 0; k < records.Count; k++)
            if (records[k].Head.GetProperty("ev").GetString() == "run" && records[k].Head.GetProperty("caller").GetInt32() == CMeshCaller)
                valve.Add(Key(MemoryMarshal.Cast<byte, float>(records[k - 1].Blob).ToArray(), MemoryMarshal.Cast<byte, int>(records[k - 2].Blob).ToArray(),
                              records[k].Head.GetProperty("error").GetDouble()));
        var unmatched = ours.Where(k => !valve.Contains(k)).ToList();
        output.WriteLine($"{ours.Count} ours, {valve.Count} Valve's; {ours.Count - unmatched.Count} of ours found, {valve.Count(k => !ours.Contains(k))} of Valve's not ours");
        foreach (var k in unmatched.Take(5))
            output.WriteLine("  ours only: " + (k.Length > 200 ? k[..200] : k));
        output.WriteLine($"distinct: ours {ours.Distinct().Count()}, Valve's {valve.Distinct().Count()}");
        foreach (var g in valve.Where(k => !ours.Contains(k)).GroupBy(k => k).Take(30))
            output.WriteLine($"  Valve's only x{g.Count()}: error {g.Key.Split('|')[0]}, {g.Key.Split('|')[1].Split(',').Length / 3} triangles, first floats {string.Join(",", g.Key.Split('|')[2].Split(',').Take(3).Select(x => BitConverter.Int32BitsToSingle(int.Parse(x))))}");
        Assert.Empty(unmatched);
    }

    private static string Key(float[] p, int[] t, double e)
        => $"{e}|{string.Join(",", t)}|{string.Join(",", p.Select(BitConverter.SingleToInt32Bits))}";
}
