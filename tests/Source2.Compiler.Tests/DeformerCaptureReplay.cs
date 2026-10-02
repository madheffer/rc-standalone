using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="LatticeDeformer"/> against PropDeformer_Transform as a compile
/// called it (<c>DEFORMCAP=&lt;tools/physics/capture_deformer.py output&gt;</c>):
/// each call's deformer rebuilt from its captured struct, the captured points
/// deformed with the captured matrix, and compared bit for bit with Valve's.
/// </summary>
public class DeformerCaptureReplay(ITestOutputHelper output)
{
    internal sealed record Call(LatticeDeformer Deformer, float[] Matrix, Vector3[] In, Vector3[] Out, JsonElement Meta);

    internal static List<Call> Read(string path)
    {
        var data = File.ReadAllBytes(path);
        var calls = new List<Call>();
        for (var at = 0; at < data.Length;)
        {
            var n = BitConverter.ToInt32(data, at);
            var head = JsonDocument.Parse(data.AsMemory(at + 4, n)).RootElement.Clone();
            at += 4 + n;
            var m = BitConverter.ToInt32(data, at);
            var blob = data.AsSpan(at + 4, m).ToArray();
            at += 4 + m;
            var sizes = head.GetProperty("sizes").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var offsets = new int[sizes.Length];
            for (var i = 1; i < sizes.Length; i++)
                offsets[i] = offsets[i - 1] + sizes[i - 1];
            float F(int o) => BitConverter.ToSingle(blob, o);
            Vector3[] V(int part) => [.. Enumerable.Range(0, sizes[part] / 12).Select(i => new Vector3(F(offsets[part] + i * 12), F(offsets[part] + i * 12 + 4), F(offsets[part] + i * 12 + 8)))];
            var d = new LatticeDeformer
            {
                Transform = new CTransform(new Vector3(F(0), F(4), F(8)), F(12), new Quaternion(F(16), F(20), F(24), F(28))),
                Size = new Vector3(F(32), F(36), F(40)),
                Segments = BitConverter.ToInt32(blob, 0x2c),
                DivisionsY = BitConverter.ToInt32(blob, 0x30),
                DivisionsZ = BitConverter.ToInt32(blob, 0x34),
                Mode = BitConverter.ToInt32(blob, 0x38),
                Mirror = blob[0x70] != 0,
                Points = V(1),
                Handles = V(2),
            };
            var matrix = Enumerable.Range(0, 12).Select(i => F(offsets[3] + i * 4)).ToArray();
            calls.Add(new Call(d, matrix, V(4), V(5), head));
        }
        return calls;
    }

    [Fact]
    public void PointsAgainstCapture()
    {
        if (Environment.GetEnvironmentVariable("DEFORMCAP") is not { } path)
            return;
        var calls = Read(path);
        int exact = 0, points = 0, pointsExact = 0, shown = 0;
        foreach (var c in calls)
        {
            var e = c.Deformer.For(c.Matrix);
            var same = 0;
            for (var i = 0; i < c.In.Length; i++)
            {
                var got = e.Deform(c.In[i]);
                var want = c.Out[i];
                if (BitConverter.SingleToInt32Bits(got.X) == BitConverter.SingleToInt32Bits(want.X)
                    && BitConverter.SingleToInt32Bits(got.Y) == BitConverter.SingleToInt32Bits(want.Y)
                    && BitConverter.SingleToInt32Bits(got.Z) == BitConverter.SingleToInt32Bits(want.Z))
                    same++;
                else if (shown++ < 6)
                    output.WriteLine($"mode {c.Deformer.Mode} segs {c.Deformer.Segments}: in {c.In[i]} ours {got:R} valve {want:R} (diff {got - want})");
            }
            points += c.In.Length;
            pointsExact += same;
            if (same == c.In.Length)
                exact++;
        }
        output.WriteLine($"{calls.Count} calls, {exact} exact; {pointsExact}/{points} points");
        Assert.Equal(calls.Count, exact);
    }
}
