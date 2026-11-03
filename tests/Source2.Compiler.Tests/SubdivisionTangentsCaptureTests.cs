using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Ledger 38: SubdivisionTangents.Grid on Valve's own inputs, from tools/vis/capture_subdivgrids.py
/// (<c>SUBDIVGRIDS=&lt;jsonl&gt;</c>): each tangent grid call's patch tangents (the gather record just before it),
/// normal grid and (u, v), against the tangents Valve wrote, bit for bit.
/// </summary>
public class SubdivisionTangentsCaptureTests(ITestOutputHelper output)
{
    [Fact]
    public void TangentGridIsValves()
    {
        if (Environment.GetEnvironmentVariable("SUBDIVGRIDS") is not { Length: > 0 } path)
            return;
        static float F(JsonElement e) => BitConverter.Int32BitsToSingle(unchecked((int)Convert.ToUInt32(e.GetString(), 16)));
        static float[] Fs(JsonElement a) => [.. a.EnumerateArray().Select(F)];
        float[]? gather = null;
        int calls = 0, points = 0, wrong = 0, wrongCalls = 0;
        foreach (var line in File.ReadLines(path))
        {
            var d = JsonDocument.Parse(line).RootElement;
            var kind = d.GetProperty("kind").GetString();
            if (kind == "gather")
            {
                gather = Fs(d.GetProperty("corners"));
                continue;
            }
            if (kind != "tangent" || gather == null)
                continue;
            var count = d.GetProperty("count").GetInt32();
            var n = Fs(d.GetProperty("normals"));
            var uv = Fs(d.GetProperty("uv"));
            var valve = Fs(d.GetProperty("tangents"));
            var slots = Enumerable.Range(0, 4).Select(k => new Vector3(gather[k * 3], gather[k * 3 + 1], gather[k * 3 + 2])).ToArray();
            var normals = Enumerable.Range(0, count).Select(i => new Vector3(n[i * 3], n[i * 3 + 1], n[i * 3 + 2])).ToArray();
            var uvs = Enumerable.Range(0, count).Select(i => new Vector2(uv[i * 2], uv[i * 2 + 1])).ToArray();
            var ours = SubdivisionTangents.Grid(slots, valve.Length > 3 ? valve[3] : 0f, normals, uvs);
            var bad = 0;
            for (var i = 0; i < count; i++)
                for (var c = 0; c < 4; c++)
                    if (BitConverter.SingleToInt32Bits(ours[i][c]) != BitConverter.SingleToInt32Bits(valve[i * 4 + c]))
                    {
                        bad++;
                        if (wrong + bad <= 5)
                            output.WriteLine($"call {calls} point {i} comp {c}: ours {ours[i][c]:R} valve {valve[i * 4 + c]:R}");
                    }
            calls++;
            points += count;
            wrong += bad;
            if (bad > 0)
                wrongCalls++;
            gather = null;
        }
        output.WriteLine($"{calls} calls, {points} points, {wrong} components differ in {wrongCalls} calls");
        Assert.True(calls > 0);
        Assert.Equal(0, wrong);
    }
}
