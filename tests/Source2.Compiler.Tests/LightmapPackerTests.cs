using System.Buffers.Binary;
using System.Text.Json;
using Source2.Compiler.Lighting;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="LightmapPacker"/> against Valve's NUVChartPacker, captured by
/// tools/vis/capture_packer.py (<c>PACKCAP=&lt;jsonl&gt;</c>), stage by stage
/// from the packing geometry PackCharts received.
/// </summary>
public class LightmapPackerTests(ITestOutputHelper output)
{
    internal static List<JsonElement> Load(string path)
        => [.. File.ReadLines(path).Select(l => JsonDocument.Parse(l).RootElement.Clone())];

    internal static (PackingGeometry Geometry, float[] Uvs) Geometry(JsonElement e)
    {
        var head = Hex(e, "head");
        int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(o));
        var records = Hex(e, "charts");
        var charts = new PackingGeometry.Chart[records.Length / 0x1c];
        for (var i = 0; i < charts.Length; i++)
        {
            uint U(int k) => BinaryPrimitives.ReadUInt32LittleEndian(records.AsSpan(i * 0x1c + k * 4));
            charts[i] = new PackingGeometry.Chart(U(0), U(1), (int)U(2), (int)U(3), (int)U(4), (int)U(5), U(6));
        }
        var geometry = new PackingGeometry
        {
            VertexStride = I(0x20), Vertices = Hex(e, "vertices"), Indices = UInts(Hex(e, "indices")),
            TriangleCharts = UInts(Hex(e, "triangle_charts")), Charts = charts,
            Offsets = [(uint)I(0x58), (uint)I(0x5c), (uint)I(0x60), (uint)I(0x64), 0, 0],
        };
        return (geometry, Floats(Hex(e, "uvs")));
    }

    /// <summary>Stage 1: each chart's triangles, bounds, flags, colour and best rotation, as InitializeChartUVs first sees them.</summary>
    [Fact]
    public void ChartsArePreparedAsValves()
    {
        if (Environment.GetEnvironmentVariable("PACKCAP") is not { Length: > 0 } path)
            return;
        var lines = Load(path);
        var (geometry, uvs) = Geometry(lines.First(l => l.GetProperty("stage").GetString() == "pack_enter"));
        var valve = lines.First(l => l.GetProperty("stage").GetString() == "init_leave").GetProperty("charts");
        var ours = LightmapPacker.Prepare(geometry, uvs);
        var misses = new SortedDictionary<string, int>();
        var shown = 0;
        for (var i = 0; i < ours.Count; i++)
        {
            var rec = Convert.FromHexString(valve[i].GetProperty("rec").GetString()!);
            uint U(int o) => BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(o));
            float F(int o) => BinaryPrimitives.ReadSingleLittleEndian(rec.AsSpan(o));
            var c = ours[i];
            var diffs = new List<string>();
            if (U(0x20) != (uint)c.Index) diffs.Add("index");
            if (U(0x00) != (uint)c.Triangles.Count) diffs.Add("triangles");
            if (U(0x24) != c.Color) diffs.Add($"colour {U(0x24):x8} vs {c.Color:x8}");
            if (F(0x28) != c.Min.X || F(0x2c) != c.Min.Y || F(0x30) != c.Min.Z || F(0x34) != c.Max.X || F(0x38) != c.Max.Y || F(0x3c) != c.Max.Z)
                diffs.Add("bounds");
            if (U(0x64) != BitConverter.SingleToUInt32Bits(c.Angle)) diffs.Add($"angle {F(0x64):R} vs {c.Angle:R}");
            if ((U(0x6c) & ~0xfu) != (c.Flags & ~0xfu)) diffs.Add($"flags {U(0x6c):x} vs {c.Flags:x}");
            foreach (var d in diffs)
                misses[d.Split(' ')[0]] = misses.GetValueOrDefault(d.Split(' ')[0]) + 1;
            if (diffs.Count > 0 && shown++ < 10)
                output.WriteLine($"chart {i}: {string.Join(", ", diffs)}");
        }
        output.WriteLine($"{ours.Count} charts; misses: {string.Join(", ", misses.Select(kv => $"{kv.Key} {kv.Value}"))}");
        Assert.Empty(misses);
    }

    /// <summary>Stage 2: every InitializeChartUVs pass, at Valve's scale: each chart's rect, gutter, luxels, padding, flags and uvs, and the packing order.</summary>
    [Fact]
    public void EveryChartUvPassIsValves()
    {
        if (Environment.GetEnvironmentVariable("PACKCAP") is not { Length: > 0 } path)
            return;
        var lines = Load(path);
        var enter = lines.First(l => l.GetProperty("stage").GetString() == "pack_enter");
        var (geometry, uvs) = Geometry(enter);
        var charts = LightmapPacker.Prepare(geometry, uvs);
        var scales = lines.Where(l => l.GetProperty("stage").GetString() == "init_enter").ToList();
        var leaves = lines.Where(l => l.GetProperty("stage").GetString() == "init_leave").ToList();
        var misses = new SortedDictionary<string, int>();
        var shown = 0;
        for (var p = 0; p < scales.Count; p++)
        {
            var scale = BitConverter.Int32BitsToSingle(unchecked((int)scales[p].GetProperty("scale").GetUInt32()));
            var order = LightmapPacker.InitializeChartUvs(charts, uvs, geometry.Indices, scale,
                scales[p].GetProperty("width").GetInt32(), scales[p].GetProperty("height").GetInt32());
            var valve = leaves[p].GetProperty("charts");
            for (var i = 0; i < charts.Count; i++)
            {
                var rec = Convert.FromHexString(valve[i].GetProperty("rec").GetString()!);
                int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(o));
                var c = charts[i];
                var diffs = new List<string>();
                if (I(0x40) != c.X0 || I(0x44) != c.Y0) diffs.Add($"origin {I(0x40)},{I(0x44)} vs {c.X0},{c.Y0}");
                float F(int o) => BinaryPrimitives.ReadSingleLittleEndian(rec.AsSpan(o));
                if (I(0x48) != BitConverter.SingleToInt32Bits(c.Width) || I(0x4c) != BitConverter.SingleToInt32Bits(c.Height))
                    diffs.Add($"size {F(0x48):R}x{F(0x4c):R} vs {c.Width:R}x{c.Height:R}");
                if (I(0x50) != c.GutterX || I(0x54) != c.GutterY) diffs.Add("gutter");
                if (I(0x58) != c.Luxels) diffs.Add($"luxels {I(0x58)} vs {c.Luxels}");
                if (I(0x5c) != c.PaddedWidth || I(0x60) != c.PaddedHeight) diffs.Add($"padded {I(0x5c)}x{I(0x60)} vs {c.PaddedWidth}x{c.PaddedHeight}");
                if (I(0x68) != BitConverter.SingleToInt32Bits(c.CurrentAngle)) diffs.Add("angle");
                if ((uint)I(0x6c) != c.Flags) diffs.Add($"flags {I(0x6c):x} vs {c.Flags:x}");
                var vu = Floats(Convert.FromHexString(valve[i].GetProperty("uvs").GetString()!));
                var same = vu.Length == c.Uvs.Count * 2;
                for (var k = 0; same && k < c.Uvs.Count; k++)
                    same = BitConverter.SingleToInt32Bits(vu[k * 2]) == BitConverter.SingleToInt32Bits(c.Uvs[k].X)
                           && BitConverter.SingleToInt32Bits(vu[k * 2 + 1]) == BitConverter.SingleToInt32Bits(c.Uvs[k].Y);
                if (!same) diffs.Add("uvs");
                foreach (var d in diffs)
                    misses[d.Split(' ')[0]] = misses.GetValueOrDefault(d.Split(' ')[0]) + 1;
                if (diffs.Count > 0 && shown++ < 12)
                    output.WriteLine($"pass {p} chart {i}: {string.Join(", ", diffs)}");
            }
            var valveOrder = leaves[p].GetProperty("order").EnumerateArray().Select(x => x.GetInt32()).ToList();
            if (!valveOrder.SequenceEqual(order.Select(c => c.Index)))
                misses["order"] = misses.GetValueOrDefault("order") + 1;
        }
        output.WriteLine($"{scales.Count} passes x {charts.Count} charts; misses: {string.Join(", ", misses.Select(kv => $"{kv.Key} {kv.Value}"))}");
        Assert.Empty(misses);
    }

    /// <summary>Stages 3 and 4: every TryPack attempt's outcome and placements, then the chart records and atlas uvs PackCharts leaves.</summary>
    [Fact]
    public void PackChartsIsValves()
    {
        if (Environment.GetEnvironmentVariable("PACKCAP") is not { Length: > 0 } path)
            return;
        var lines = Load(path);
        var enter = lines.First(l => l.GetProperty("stage").GetString() == "pack_enter");
        var leave = lines.First(l => l.GetProperty("stage").GetString() == "pack_leave");
        var (geometry, uvs) = Geometry(enter);
        var attempts = new List<LightmapPacker.Attempt>();
        var ok = LightmapPacker.Pack(geometry, uvs, enter.GetProperty("width").GetInt32(), enter.GetProperty("height").GetInt32(),
                                     enter.GetProperty("mip").GetInt32(), enter.GetProperty("passes").GetInt32(), attempts);
        var tries = lines.Where(l => l.GetProperty("stage").GetString() == "try_leave").ToList();
        var misses = new List<string>();
        for (var p = 0; p < tries.Count; p++)
        {
            var valveOk = tries[p].GetProperty("result").GetInt32() != 0;
            if (p >= attempts.Count) { misses.Add($"attempt {p}: missing"); continue; }
            if (valveOk != attempts[p].Packed)
                misses.Add($"attempt {p}: packed {valveOk} vs {attempts[p].Packed}");
            else if (valveOk)
            {
                var vp = Convert.FromHexString(tries[p].GetProperty("placements").GetString()!);
                var wrong = Enumerable.Range(0, attempts[p].Placements.Length / 2)
                    .Where(i => BinaryPrimitives.ReadInt32LittleEndian(vp.AsSpan(i * 8)) != attempts[p].Placements[i * 2]
                             || BinaryPrimitives.ReadInt32LittleEndian(vp.AsSpan(i * 8 + 4)) != attempts[p].Placements[i * 2 + 1]).ToList();
                if (wrong.Count > 0)
                    misses.Add($"attempt {p}: {wrong.Count} placements differ, first chart {wrong[0]}: valve "
                               + $"{BinaryPrimitives.ReadInt32LittleEndian(vp.AsSpan(wrong[0] * 8))},{BinaryPrimitives.ReadInt32LittleEndian(vp.AsSpan(wrong[0] * 8 + 4))}"
                               + $" ours {attempts[p].Placements[wrong[0] * 2]},{attempts[p].Placements[wrong[0] * 2 + 1]}");
            }
        }
        var (valveGeometry, valveUvs) = Geometry(leave);
        var chartDiffs = Enumerable.Range(0, geometry.Charts.Length).Count(i => geometry.Charts[i] != valveGeometry.Charts[i]);
        var uvDiffs = Enumerable.Range(0, uvs.Length).Count(i => BitConverter.SingleToInt32Bits(uvs[i]) != BitConverter.SingleToInt32Bits(valveUvs[i]));
        if (chartDiffs > 0) misses.Add($"{chartDiffs} chart records differ, first {geometry.Charts.Select((c, i) => (c, i)).First(x => x.c != valveGeometry.Charts[x.i])}");
        if (uvDiffs > 0) misses.Add($"{uvDiffs} uv floats differ");
        output.WriteLine($"packed {ok}, {attempts.Count} attempts ({tries.Count} captured), {geometry.Charts.Length} charts");
        foreach (var m in misses)
            output.WriteLine("  " + m);
        Assert.Empty(misses);
    }

    internal static byte[] Hex(JsonElement e, string name) => Convert.FromHexString(e.GetProperty(name).GetString() ?? "");

    internal static uint[] UInts(byte[] b)
    {
        var r = new uint[b.Length / 4];
        for (var i = 0; i < r.Length; i++)
            r[i] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i * 4));
        return r;
    }

    internal static float[] Floats(byte[] b)
    {
        var r = new float[b.Length / 4];
        for (var i = 0; i < r.Length; i++)
            r[i] = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(i * 4));
        return r;
    }
}
