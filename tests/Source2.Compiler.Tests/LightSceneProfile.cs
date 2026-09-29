using System.Diagnostics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// How long the editor trace scene takes to build and to trace one light
/// (LIGHTPROF=&lt;addon&gt;|&lt;map&gt;): the scene's instance count and triangle
/// count, then the first few shadow-casting lights' precomputed keys, each timed.
/// </summary>
public class LightSceneProfile(ITestOutputHelper output)
{
    [Fact]
    public void Profile()
    {
        if (Environment.GetEnvironmentVariable("LIGHTPROF") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        if (MapFixtures.VmapSource(parts[0], parts[1]) is not { } source || MapFixtures.GameSchema() is not { } schema)
            return;
        var document = DmxBinary.ReadFile(source);
        var clock = Stopwatch.StartNew();
        ILightTracer scene = parts.Length > 2 && parts[2] == "empty" ? EmptyLightScene.Instance : SettleLumpTests.LightScene(document, source)!;
        if (scene is EditorTraceScene editor)
            output.WriteLine($"scene: {editor.Instances.Count} instances, {editor.Instances.Sum(i => i.Records.Length / 22)} triangles, built in {clock.ElapsedMilliseconds} ms");
        var lights = MapEntities.From(document).Where(e => e.ClassName is "light_barn" or "light_omni2" && !e.Hidden);
        var shown = 0;
        foreach (var e in lights)
        {
            string? Key(string name) => e.Keys.FirstOrDefault(k => k.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value
                                        ?? schema.KeyOf(e.ClassName, name)?.Default;
            clock.Restart();
            var keys = LightPrecompute.Keys(e.ClassName, Key, LightPrecompute.World(e.Origin, e.Angles), scene);
            if (keys.Count == 0)
                continue;
            output.WriteLine($"{e.ClassName}#{e.NodeId}: {keys.Count} keys in {clock.ElapsedMilliseconds} ms");
            // One record's sampling loop, taken apart.
            var world = LightPrecompute.World(e.Origin, e.Angles);
            foreach (var split in new[] { false, true })
            {
                var records = LightPrecompute.Records(e.ClassName, Key, world, split);
                if (records.Length == 0)
                    continue;
                var light = records[0];
                if (split)
                    LightBuild.SphereLuminaire(light, 1e-6f);
                var falloff = light.Clone();
                falloff[0xac] = 0f;
                falloff[0xb0] = 0f;
                long tHalton = 0, tSample = 0, tRest = 0, attempts = 0;
                var sw = new Stopwatch();
                int i2 = 1, i3 = 1, i5 = 1, i7 = 1;
                Span<float> h = stackalloc float[4];
                for (var k = 0; k < 2000; k++)
                    for (var attempt = 0; ; attempt++)
                    {
                        attempts++;
                        sw.Restart();
                        h[3] = LightTrace.Halton(7, i7++); h[2] = LightTrace.Halton(5, i5++);
                        h[1] = LightTrace.Halton(3, i3++); h[0] = LightTrace.Halton(2, i2++);
                        tHalton += sw.ElapsedTicks; sw.Restart();
                        var tt = LightSampler.Sample(light, h, out var s, out var d);
                        tSample += sw.ElapsedTicks; sw.Restart();
                        var ok = false;
                        if (tt > 0f)
                        {
                            var en = new System.Numerics.Vector3(tt * d.X + s.X, tt * d.Y + s.Y, tt * d.Z + s.Z);
                            var mid = (en + s) * 0.5f;
                            var clip = LightTrace.Project(light, mid);
                            ok = !(LightTrace.Falloff(falloff, clip, mid) * LightTrace.Shape(light, clip) <= 0f);
                        }
                        tRest += sw.ElapsedTicks;
                        if (ok || attempt >= 0x65)
                            break;
                    }
                double ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
                output.WriteLine($"  split {split}: 2000 rays, {attempts} attempts; halton {ms(tHalton):F0} ms, sample {ms(tSample):F0} ms, falloff+shape {ms(tRest):F0} ms");
            }
            if (++shown == 1)
                break;
        }
    }
}
