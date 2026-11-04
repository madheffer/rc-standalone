using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (OPEN 1.c7, ledger 249, CS2_CLOSED_QUEUE A.13;
/// <c>TANGENTFRAME=&lt;capture_tangentframe.py jsonl&gt;</c>, optional
/// <c>TANGENTFRAME_MAP=addon|map</c>): Valve's CompressTangentFrame inputs whose
/// angle lands on an axis step. First our <see cref="TangentFrame.Compress"/> on
/// each of Valve's inputs (does our compressor give Valve's word?); then, with a
/// map, a <c>-world -fshallow</c> compile of it whose packed frames on an axis
/// step are looked up among Valve's by the normal's bits: a frame of ours with
/// no equal frame in Valve's set prints beside Valve's frames of that normal.
/// </summary>
public class TangentFrameCaptureProbe(ITestOutputHelper output)
{
    private static string Hex(float f) => BitConverter.SingleToUInt32Bits(f).ToString("x8");

    private static float F(string hex, int i) =>
        BitConverter.Int32BitsToSingle(BitConverter.ToInt32(Convert.FromHexString(hex.Substring(i * 8, 8))));

    private static uint Angle(uint w) => (w >> 1) & 0x7ff;

    private static bool AxisStep(uint w) => Angle(w) is 0 or 1023 or 1024 or 2047;

    private static string Key(Vector3 n) => Hex(n.X) + Hex(n.Y) + Hex(n.Z);

    private static string Key(Vector4 t) => Hex(t.X) + Hex(t.Y) + Hex(t.Z) + Hex(t.W);

    [Fact]
    public void AgainstValve()
    {
        if (Environment.GetEnvironmentVariable("TANGENTFRAME") is not { Length: > 0 } path)
            return;
        var valve = new List<(Vector3 N, Vector4 T, uint Word)>();
        foreach (var line in File.ReadLines(path))
        {
            var d = JsonDocument.Parse(line).RootElement;
            var n = d.GetProperty("n").GetString()!;
            var t = d.GetProperty("t").GetString()!;
            valve.Add((new Vector3(F(n, 0), F(n, 1), F(n, 2)), new Vector4(F(t, 0), F(t, 1), F(t, 2), F(t, 3)),
                       d.GetProperty("word").GetUInt32()));
        }
        var wrong = valve.Where(v => TangentFrame.Compress(v.N, v.T) != v.Word).ToList();
        output.WriteLine($"Valve's frames: {valve.Count}; our Compress differs on {wrong.Count}");
        foreach (var v in wrong.Take(20))
            output.WriteLine($"  n {Key(v.N)} t {Key(v.T)}: Valve {Angle(v.Word)}, ours {Angle(TangentFrame.Compress(v.N, v.T))}");

        if (Environment.GetEnvironmentVariable("TANGENTFRAME_MAP") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var source = MapFixtures.VmapSource(p[0], p[1]) ?? throw new FileNotFoundException(spec);
        var outRoot = Path.Combine(Path.GetTempPath(), "tangentframe_probe", p[1]);
        var package = Path.Combine(outRoot, "csgo_addons", p[0], "maps", p[1] + ".vpk");
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        File.WriteAllBytes(package, Io.VpkWriter.Write(new Dictionary<string, byte[]>()));
        var ours = new ConcurrentDictionary<string, (Vector3 N, Vector4 T, uint Word, string Draw)>();
        WorldNodeModels.ObserveTangentFrame = (n, t, w) =>
        {
            if (AxisStep(w))
                ours.TryAdd(Key(n) + Key(t), (n, t, w, WorldNodeModels.ObservedDraw ?? "?"));
        };
        try
        {
            var args = MapCompileArgs.Parse(["-i", source, "-outroot", outRoot, "-world", "-fshallow"]);
            var code = MapCompile.Run(args, new StringWriter(), acceptGaps: true, skipUnported: true);
            output.WriteLine($"compile-map -world -fshallow: exit {code}");
        }
        finally
        {
            WorldNodeModels.ObserveTangentFrame = null;
        }
        var valveFrames = valve.Select(v => Key(v.N) + Key(v.T)).ToHashSet();
        var byNormal = valve.ToLookup(v => Key(v.N));
        var missing = ours.Values.Where(o => !valveFrames.Contains(Key(o.N) + Key(o.T))).ToList();
        output.WriteLine($"our axis-step frames: {ours.Count}; not among Valve's: {missing.Count}");
        foreach (var group in missing.GroupBy(o => (Angle(o.Word), byNormal[Key(o.N)].Any())).OrderBy(g => g.Key.Item1))
            output.WriteLine($"  our angle {group.Key.Item1}, Valve has this normal: {group.Key.Item2}: {group.Count()}");
        // Those whose nearest Valve frame of the same normal packs another angle: the words that differ.
        var differ = missing.Where(o => byNormal[Key(o.N)].OrderBy(v => Vector4.DistanceSquared(v.T, o.T)).FirstOrDefault() is var v
                                         && v.Word != 0 && Angle(v.Word) != Angle(o.Word)).ToList();
        output.WriteLine($"frames whose nearest Valve frame packs another angle: {differ.Count}");
        foreach (var g in differ.GroupBy(o => o.Draw.Split('|')[0] + (o.Draw.EndsWith("|renormalised") ? " (renormalised)" : "")).OrderByDescending(g => g.Count()))
            output.WriteLine($"  {g.Count(),4} {g.Key}");
        foreach (var g in missing.GroupBy(o => o.Draw.EndsWith("|renormalised")))
            output.WriteLine($"  not among Valve's, renormalised={g.Key}: {g.Count()}");
        foreach (var o in differ.Take(30))
        {
            output.WriteLine($"ours  n {Key(o.N)} t {Key(o.T)} angle {Angle(o.Word)} {o.Draw}");
            var v = byNormal[Key(o.N)].OrderBy(v => Vector4.DistanceSquared(v.T, o.T)).First();
            output.WriteLine($"valve n {Key(v.N)} t {Key(v.T)} angle {Angle(v.Word)}");
        }
        foreach (var o in missing.Take(0))
        {
            output.WriteLine($"ours  n {Key(o.N)} t {Key(o.T)} angle {Angle(o.Word)}");
            foreach (var v in byNormal[Key(o.N)].OrderBy(v => Vector4.DistanceSquared(v.T, o.T)).Take(2))
                output.WriteLine($"valve n {Key(v.N)} t {Key(v.T)} angle {Angle(v.Word)}");
        }
    }
}
