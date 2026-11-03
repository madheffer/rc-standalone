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

    /// <summary>
    /// SubdivisionNormalGrid.Grid (1813c3cf0) on every subdivided patch of a .vmap (<c>SUBDIVGRIDS_VMAP</c>) against
    /// the normal grid Valve wrote for the call whose position grid is the same bits.
    /// </summary>
    [Fact]
    public void NormalGridIsValves()
    {
        if (Environment.GetEnvironmentVariable("SUBDIVGRIDS") is not { Length: > 0 } path
            || Environment.GetEnvironmentVariable("SUBDIVGRIDS_VMAP") is not { Length: > 0 } vmap)
            return;
        static float F(JsonElement e) => BitConverter.Int32BitsToSingle(unchecked((int)Convert.ToUInt32(e.GetString(), 16)));
        static string Key(IEnumerable<Vector3> ps) => string.Join(",", ps.Select(p => $"{BitConverter.SingleToInt32Bits(p.X):x}{BitConverter.SingleToInt32Bits(p.Y):x}{BitConverter.SingleToInt32Bits(p.Z):x}"));
        var valve = new Dictionary<string, float[]>();
        foreach (var line in File.ReadLines(path))
        {
            var d = JsonDocument.Parse(line).RootElement;
            if (d.GetProperty("kind").GetString() != "normal")
                continue;
            var p = d.GetProperty("positions").EnumerateArray().Select(F).ToArray();
            var key = Key(Enumerable.Range(0, p.Length / 3).Select(i => new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2])));
            valve.TryAdd(key, [.. d.GetProperty("normals").EnumerateArray().Select(F)]);
        }
        var doc = DmxBinary.ReadFile(vmap);
        int patches = 0, matched = 0, exact = 0, points = 0, same = 0, shown = 0;
        foreach (var mesh in doc.OfType("CMapMesh"))
        {
            if (mesh.Get<DmxBinary.Element>("meshData") is not { } data)
                continue;
            var a = new FaceArrays(data);
            if (!a.HasDisplacement)
                continue;
            var cos = Source2.Compiler.Simulation.CrtMath.Cos(MathF.Min(mesh.GetValue<float>("smoothingAngle") ?? 40f, 180f) * 0.017453292f);
            var grid = new SubdivisionNormalGrid(a, cos);
            for (var f = 0; f < a.First.Length; f++)
            {
                var hs = a.Loop(f);
                for (var i = 0; i < hs.Count; i++)
                {
                    var level = a.Level(hs[i]);
                    if (level == 0)
                        continue;
                    var positions = a.Grid(hs, i, level);
                    a.Displace(positions, hs, i, f);
                    patches++;
                    if (!valve.TryGetValue(Key(positions), out var v))
                        continue;
                    matched++;
                    var ours = grid.Grid(hs[i], positions)!;
                    var g = (1 << (level - 1)) + 1;
                    var bad = 0;
                    for (var k = 0; k < ours.Length; k++)
                    {
                        points++;
                        var eq = Enumerable.Range(0, 3).All(c => BitConverter.SingleToInt32Bits(ours[k][c]) == BitConverter.SingleToInt32Bits(v[k * 3 + c]));
                        if (eq)
                            same++;
                        else
                        {
                            bad++;
                            if (shown++ < 12)
                                output.WriteLine($"face {f} corner {i} point ({k / g},{k % g}): ours {ours[k].X:R},{ours[k].Y:R},{ours[k].Z:R} valve {v[k * 3]:R},{v[k * 3 + 1]:R},{v[k * 3 + 2]:R}");
                        }
                    }
                    if (bad == 0)
                        exact++;
                }
            }
        }
        output.WriteLine($"{patches} patches, {matched} matched to a capture, {exact} exact; {same} of {points} points bit for bit");
        Assert.True(matched > 0);
        Assert.Equal(matched, exact);
    }

    /// <summary>
    /// The whole tangent stream of each subdivided patch from the .vmap (<c>SUBDIVGRIDS_VMAP</c>): the corners' stored
    /// tangents gathered (1813bdf80), our normal grid and the tangent grid (1813c2090), against the tangent grid Valve
    /// wrote for the patch whose position grid is the same bits.
    /// </summary>
    [Fact]
    public void TangentStreamIsValves()
    {
        if (Environment.GetEnvironmentVariable("SUBDIVGRIDS") is not { Length: > 0 } path
            || Environment.GetEnvironmentVariable("SUBDIVGRIDS_VMAP") is not { Length: > 0 } vmap)
            return;
        static float F(JsonElement e) => BitConverter.Int32BitsToSingle(unchecked((int)Convert.ToUInt32(e.GetString(), 16)));
        static string Key(IEnumerable<Vector3> ps) => string.Join(",", ps.Select(p => $"{BitConverter.SingleToInt32Bits(p.X):x}{BitConverter.SingleToInt32Bits(p.Y):x}{BitConverter.SingleToInt32Bits(p.Z):x}"));
        // Each patch's records come normal grid, gather, tangent grid (each sent as its call returns); the position
        // grid of the normal call names the patch.
        var valve = new Dictionary<string, (float[] Gather, float[] Tangents)>();
        float[]? gather = null;
        string? patch = null;
        foreach (var line in File.ReadLines(path))
        {
            var d = JsonDocument.Parse(line).RootElement;
            var kind = d.GetProperty("kind").GetString();
            if (kind == "normal")
            {
                var p = d.GetProperty("positions").EnumerateArray().Select(F).ToArray();
                patch = Key(Enumerable.Range(0, p.Length / 3).Select(i => new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2])));
                gather = null;
            }
            if (kind == "gather")
                gather = [.. d.GetProperty("corners").EnumerateArray().Select(F)];
            if (kind != "tangent" || gather == null || patch == null)
                continue;
            valve.TryAdd(patch, (gather, [.. d.GetProperty("tangents").EnumerateArray().Select(F)]));
            gather = null;
            patch = null;
        }
        var doc = DmxBinary.ReadFile(vmap);
        int patches = 0, matched = 0, exact = 0, gathers = 0, shown = 0;
        var wrongGather = new SortedDictionary<int, int>();
        foreach (var mesh in doc.OfType("CMapMesh"))
        {
            if (mesh.Get<DmxBinary.Element>("meshData") is not { } data)
                continue;
            var a = new FaceArrays(data);
            if (!a.HasDisplacement)
                continue;
            var (layout, corner) = MapMeshCorners.BakeStreams(data);
            var to = 0;
            foreach (var (_, name, count) in layout)
            {
                if (name == "tangent")
                    break;
                to += count;
            }
            var cos = Source2.Compiler.Simulation.CrtMath.Cos(MathF.Min(mesh.GetValue<float>("smoothingAngle") ?? 40f, 180f) * 0.017453292f);
            var grid = new SubdivisionNormalGrid(a, cos);
            for (var f = 0; f < a.First.Length; f++)
            {
                var hs = a.Loop(f);
                for (var i = 0; i < hs.Count; i++)
                {
                    var level = a.Level(hs[i]);
                    if (level == 0)
                        continue;
                    var positions = a.Grid(hs, i, level);
                    a.Displace(positions, hs, i, f);
                    patches++;
                    var normals = grid.Grid(hs[i], positions)!;
                    if (!valve.TryGetValue(Key(positions), out var v))
                        continue;
                    matched++;
                    var loop = Enumerable.Range(0, hs.Count).Select(k => corner(a.CornerData[hs[(i + k) % hs.Count]])).ToArray();
                    var slots = SubdivisionTangents.Gather([.. loop.Select(t => new Vector3(t[to], t[to + 1], t[to + 2]))]);
                    if (Enumerable.Range(0, 12).All(k => BitConverter.SingleToInt32Bits(slots[k / 3][k % 3]) == BitConverter.SingleToInt32Bits(v.Gather[k])))
                        gathers++;
                    else
                    {
                        wrongGather[mesh.GetValue<int>("nodeID") ?? -1] = wrongGather.GetValueOrDefault(mesh.GetValue<int>("nodeID") ?? -1) + 1;
                        if (shown++ < 10)
                        {
                            var fd = (data.Get<object?[]>("faceDataIndices") ?? [])[f] is int fi ? fi : -1;
                            string Fs(string stream) => data.Get<DmxBinary.Element>("faceData")?.GetElements("streams").FirstOrDefault(x => x.Name.StartsWith(stream, StringComparison.Ordinal))?.Get<object?[]>("data") is { } arr && fd >= 0 ? $"{arr[fd]}" : "-";
                            var nAt = 0;
                            foreach (var (_, name, count) in layout)
                            {
                                if (name == "normal")
                                    break;
                                nAt += count;
                            }
                            var c0 = loop[0];
                            output.WriteLine($"node {mesh.GetValue<int>("nodeID")} face {f} corner {i}: stored t {c0[to]:R},{c0[to + 1]:R},{c0[to + 2]:R},{c0[to + 3]:R} n {c0[nAt]:R},{c0[nAt + 1]:R},{c0[nAt + 2]:R}; axisU {Fs("textureAxisU")} axisV {Fs("textureAxisV")} scale {Fs("textureScale")}; valve slot0 {v.Gather[0]:R},{v.Gather[1]:R},{v.Gather[2]:R} w {v.Tangents[3]:R}");
                        }
                    }
                    var n = (1 << (level - 1));
                    var g = n + 1;
                    var uv = Enumerable.Range(0, g * g).Select(k => new Vector2((float)(k % g) / n, (float)(k / g) / n)).ToArray();
                    var ours = SubdivisionTangents.Grid(slots, loop[0][to + 3], normals, uv);
                    var bad = 0;
                    for (var k = 0; k < ours.Length; k++)
                        if (Enumerable.Range(0, 4).Any(c => BitConverter.SingleToInt32Bits(ours[k][c]) != BitConverter.SingleToInt32Bits(v.Tangents[k * 4 + c])))
                        {
                            bad++;
                            if (shown++ < 8)
                                output.WriteLine($"face {f} corner {i} point {k}: ours {ours[k]} valve {v.Tangents[k * 4]:R},{v.Tangents[k * 4 + 1]:R},{v.Tangents[k * 4 + 2]:R},{v.Tangents[k * 4 + 3]:R}");
                        }
                    if (bad == 0)
                        exact++;
                }
            }
        }
        output.WriteLine($"{patches} patches, {matched} matched by position grid, {gathers} gathers and {exact} tangent grids bit for bit");
        output.WriteLine($"gathers that differ, by node: {string.Join(", ", wrongGather.Select(x => $"{x.Key} {x.Value}"))}");
        Assert.True(matched > 0);
        Assert.Equal(matched, exact);
    }

    /// <summary>
    /// SubdivisionTangents.PositionNormals (1813be620) on each captured normal grid call's position grid, against the
    /// normal grid Valve wrote, by region: inside, border rows and columns, corners (the normal grid's border pass,
    /// 1813c3cf0, changes the borders afterwards).
    /// </summary>
    [Fact]
    public void PositionNormalsMatchTheInterior()
    {
        if (Environment.GetEnvironmentVariable("SUBDIVGRIDS") is not { Length: > 0 } path)
            return;
        static float F(JsonElement e) => BitConverter.Int32BitsToSingle(unchecked((int)Convert.ToUInt32(e.GetString(), 16)));
        static float[] Fs(JsonElement a) => [.. a.EnumerateArray().Select(F)];
        var tally = new Dictionary<string, (int Same, int All)>();
        foreach (var line in File.ReadLines(path))
        {
            var d = JsonDocument.Parse(line).RootElement;
            if (d.GetProperty("kind").GetString() != "normal")
                continue;
            var count = d.GetProperty("count").GetInt32();
            var g = (int)Math.Round(Math.Sqrt(count));
            if (g * g != count || g < 2)
                continue;
            var p = Fs(d.GetProperty("positions"));
            var v = Fs(d.GetProperty("normals"));
            var grid = Enumerable.Range(0, count).Select(i => new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2])).ToArray();
            var ours = SubdivisionTangents.PositionNormals(grid, g - 1);
            for (var i = 0; i < count; i++)
            {
                int r = i / g, c = i % g;
                var region = (r == 0 || r == g - 1) && (c == 0 || c == g - 1) ? $"corner({(r == 0 ? 0 : 1)},{(c == 0 ? 0 : 1)})"
                    : r == 0 ? "row0" : r == g - 1 ? "rown" : c == 0 ? "col0" : c == g - 1 ? "coln" : "inside";
                var same = Enumerable.Range(0, 3).All(k => BitConverter.SingleToInt32Bits(ours[i][k]) == BitConverter.SingleToInt32Bits(v[i * 3 + k]));
                var t = tally.GetValueOrDefault(region);
                tally[region] = (t.Same + (same ? 1 : 0), t.All + 1);
            }
        }
        foreach (var (k, (same, all)) in tally.OrderBy(x => x.Key))
            output.WriteLine($"{k}: {same} of {all} bit for bit");
        Assert.True(tally.Count > 0);
        Assert.Equal(tally["inside"].All, tally["inside"].Same);
    }
}
