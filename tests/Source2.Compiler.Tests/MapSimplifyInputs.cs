using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Physics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The triangle meshes the brush entity models hand the simplifier
/// (<see cref="BrushHulls.Simplified"/>), in order, against resourcecompiler's
/// map builder calls in a compile (<c>tools/physics/capture_mapsimplify.py</c>):
/// <c>MAPSIMPLIFY_INPUTS=&lt;addon&gt;|&lt;map&gt;|&lt;capture&gt;</c>.
/// </summary>
public class MapSimplifyInputs(ITestOutputHelper output)
{
    [Fact]
    public void PiecesMatchTheCapture()
    {
        if (Environment.GetEnvironmentVariable("MAPSIMPLIFY_INPUTS") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var game = Path.Combine(cs2, "game");
        var vmap = Path.Combine(cs2, "content", "csgo_addons", parts[0], "maps", parts[1] + ".vmap");
        using var content = new Maps.GameContent(Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "csgo_addons", parts[0]));
        var ours = new List<(Vector3[] P, int[] T, float E)>();
        BrushHulls.SimplifyTrace = (p, t, e) => ours.Add(([.. p], [.. t.SelectMany(x => new[] { x.A, x.B, x.C })], e));
        try
        {
            var doc = DmxBinary.ReadFile(vmap);
            Maps.MapPrefabs.Attach(doc, Maps.MapPrefabs.FromContent(Path.Combine(cs2, "content", "csgo_addons", parts[0])));
            Maps.MapDeformers.Apply(doc);
            EntityPhysicsModels.Build(doc, parts[1], content.Material, content.CollisionProperty, MapFixtures.GameSchema(), null, content.SmartProp);
        }
        finally
        {
            BrushHulls.SimplifyTrace = null;
        }
        var records = SimplifyReplay.Records(parts[2]).ToList();
        var valve = new List<(float[] P, int[] T, double E)>();
        for (var k = 0; k < records.Count; k++)
            if (records[k].Head.GetProperty("ev").GetString() == "run" && records[k].Head.GetProperty("caller").GetInt32() == 19963406)
                valve.Add((MemoryMarshal.Cast<byte, float>(records[k - 1].Blob).ToArray(), MemoryMarshal.Cast<byte, int>(records[k - 2].Blob).ToArray(),
                           records[k].Head.GetProperty("error").GetDouble()));
        output.WriteLine($"{ours.Count} ours, {valve.Count} Valve's");
        // Matched by content: the compile's model order is not ours, and it also
        // simplifies the pieces of models that then fail on a missing material.
        string Key(float[] p, int[] t, double e) => $"{e}|{string.Join(",", t)}|{string.Join(",", p.Select(BitConverter.SingleToInt32Bits))}";
        var valveKeys = valve.Select(v => Key(v.P, v.T, v.E)).ToList();
        var oursKeys = ours.Select(o => Key([.. o.P.SelectMany(x => new[] { x.X, x.Y, x.Z })], o.T, o.E)).ToList();
        var unmatched = oursKeys.Where(k => !valveKeys.Contains(k)).ToList();
        output.WriteLine($"{ours.Count - unmatched.Count} of {ours.Count} ours found among Valve's; {valveKeys.Count(k => !oursKeys.Contains(k))} of Valve's not ours");
        foreach (var k in unmatched.Take(5))
            output.WriteLine("  ours only: " + (k.Length > 200 ? k[..200] : k));
        Assert.Empty(unmatched);
    }
}
