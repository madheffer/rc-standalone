using System.Buffers.Binary;
using Source2.Compiler.Lighting;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The post-process port against vrad3 itself. VRAD3ORACLE names a _vrad3
/// folder. The first run writes a synthetic 256 x 256 atlas there
/// (oracle_packing_geometry.dat and the image oracle_in) and skips;
/// `python tools/vrad3/post_oracle.py oracle &lt;addon&gt;` (GPU: CS2 closed) then
/// runs each command on it into oracle_out_&lt;name&gt;, and the next run
/// compares: luxel and shadow records byte for byte, channels as half floats.
/// </summary>
public class LightmapPostOracleTests(ITestOutputHelper output)
{
    public const int Size = 256;

    private static string? Folder() =>
        Environment.GetEnvironmentVariable("VRAD3ORACLE") is { Length: > 0 } f && Directory.Exists(f) ? f : null;

    public static TheoryData<string> Commands() =>
        ["consolidate_2", "fireflies", "median_7", "jfa", "encode_ahd", "gutters_1", "gutters_2"];

    [Theory]
    [MemberData(nameof(Commands))]
    public void MatchesVrad3(string name)
    {
        var folder = Folder();
        if (folder is null)
        {
            MapFixtures.Skip("VRAD3ORACLE (a _vrad3 folder)");
            return;
        }
        var input = Path.Combine(folder, "oracle_in");
        if (!File.Exists(Path.Combine(input, "lightmap_data.dat")))
        {
            WriteInput(folder);
            MapFixtures.Skip("vrad3's outputs: run tools/vrad3/post_oracle.py oracle <addon>");
            return;
        }
        var expected = Path.Combine(folder, "oracle_out_" + name);
        if (!File.Exists(Path.Combine(expected, "lightmap_data.dat")))
        {
            MapFixtures.Skip($"{expected}: run tools/vrad3/post_oracle.py oracle <addon>");
            return;
        }
        var charts = PackingGeometry.Read(File.ReadAllBytes(Path.Combine(folder, "oracle_packing_geometry.dat"))).Charts;
        var image = LightmapImage.Load(input);
        switch (name)
        {
            case "consolidate_2": LightmapPost.ConsolidateSmallCharts(image, charts, 2); break;
            case "fireflies": LightmapPost.RemoveFireflies(image, charts, 4f); break;
            case "median_7": LightmapPost.FilterMedian(image, charts, 7); break;
            case "jfa": LightmapPost.DilateOverInvalidJfa(image); break;
            case "encode_ahd": LightmapAhd.Encode(image); break;
            case "gutters_1": LightmapPost.FillGutters(image, charts, 1); break;
            case "gutters_2": LightmapPost.FillGutters(image, charts, 2); break;
        }
        Compare(image, LightmapImage.Load(expected));
    }

    private void Compare(LightmapImage ours, LightmapImage theirs)
    {
        var problems = new List<string>();
        void Bytes(string what, byte[] a, byte[] b, int stride)
        {
            var bad = Enumerable.Range(0, a.Length / stride).Where(i => !a.AsSpan(i * stride, stride).SequenceEqual(b.AsSpan(i * stride, stride))).ToList();
            if (bad.Count > 0)
                problems.Add($"{what}: {bad.Count} luxels differ, first at {bad[0] % Size},{bad[0] / Size}: "
                    + $"{Convert.ToHexString(a, bad[0] * stride, stride)} vs {Convert.ToHexString(b, bad[0] * stride, stride)}");
        }
        Bytes("luxel records", ours.LuxelRecords, theirs.LuxelRecords, 12);
        Bytes("shadow records", ours.ShadowRecords, theirs.ShadowRecords, 20);
        for (var c = 0; c < LightmapImage.ChannelCount; c++)
        {
            var a = ours.Channels[c];
            var b = theirs.Channels[c];
            if (a is null || b is null)
            {
                if (a is not null || b is not null)
                    problems.Add($"channel {c}: present in {(a is null ? "vrad3 only" : "ours only")}");
                continue;
            }
            if (a.Mask != b.Mask)
                problems.Add($"channel {c}: mask {a.Mask} vs {b.Mask}");
            int ulp = 0, worse = 0, first = -1;
            for (var i = 0; i < a.Data.Length; i++)
            {
                if ((b.Mask >> (i & 3) & 1) == 0)
                    continue;
                var x = BitConverter.HalfToInt16Bits((Half)a.Data[i]);
                var y = BitConverter.HalfToInt16Bits((Half)b.Data[i]);
                if (x == y)
                    continue;
                if (Math.Abs(x - y) == 1)
                    ulp++;
                else
                {
                    worse++;
                    if (first < 0)
                        first = i;
                }
            }
            if (ulp + worse > 0)
                problems.Add($"channel {c}: {worse} values differ" + (first >= 0
                    ? $" (first luxel {first / 4 % Size},{first / 4 / Size} component {first & 3}: {a.Data[first]} vs {b.Data[first]})"
                    : "") + $", {ulp} by one half ulp");
        }
        foreach (var p in problems)
            output.WriteLine(p);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// A 5 x 5 grid of charts in a 256 x 256 atlas, three of them small (x, y,
    /// both), each a rect whose border ring and ~3% of its inside are
    /// uncovered, ~85% of covered luxels valid, with plausible SH, normals,
    /// tangents and shadow slots, and ~1% fireflies.
    /// </summary>
    public static void WriteInput(string folder)
    {
        var random = new Random(20261008);
        float R(float lo, float hi) => lo + (float)random.NextDouble() * (hi - lo);
        var charts = new List<PackingGeometry.Chart>();
        for (var i = 0; i < 25; i++)
        {
            uint flags = i switch { 3 => 1u << 28, 7 => 1u << 29, 11 => 3u << 28, _ => 0 };
            charts.Add(new PackingGeometry.Chart(0xff000000 | (uint)random.Next(0x1000000), (uint)i,
                i % 5 * 50 + 2, i / 5 * 50 + 2, 8 + random.Next(38), 8 + random.Next(38), flags));
        }
        File.WriteAllBytes(Path.Combine(folder, "oracle_packing_geometry.dat"), Geometry(charts).Write());

        var image = new LightmapImage(Size, Size);
        uint[] masks = [7, 15, 15, 15, 15, 15, 15, 7, 1, 7, 1, 15, 1];
        for (var c = 0; c < masks.Length; c++)
            image.AddChannel(c, masks[c]);
        for (var p = 0; p < Size * Size; p++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.LuxelRecords.AsSpan(p * 12), 0xffffffff);
            image.ShadowRecords.AsSpan(p * 20, 4).Fill(0xff);
        }
        foreach (var chart in charts)
        {
            var normal = Unit(R(-0.5f, 0.5f), R(-0.5f, 0.5f), R(0.5f, 1f));
            var tangent = Unit(normal[2], 0, -normal[0]);
            var sign = random.Next(2) == 0 ? 1f : -1f;
            float[] tint = [R(0.2f, 2f), R(0.2f, 2f), R(0.2f, 2f)];
            var lights = Enumerable.Range(0, 4).Select(_ => random.Next(3) == 0 ? (byte)0xff : (byte)random.Next(8)).ToArray();
            for (var y = chart.Y + 1; y < chart.Y + chart.Height - 1; y++)
                for (var x = chart.X + 1; x < chart.X + chart.Width - 1; x++)
                {
                    if (random.NextDouble() < 0.03)
                        continue;
                    var p = y * Size + x;
                    BinaryPrimitives.WriteUInt32LittleEndian(image.LuxelRecords.AsSpan(p * 12), chart.Index);
                    BinaryPrimitives.WriteUInt32LittleEndian(image.LuxelRecords.AsSpan(p * 12 + 4), (uint)random.Next(1, 200));
                    image.LuxelRecords[p * 12 + 8] = (byte)((random.NextDouble() < 0.85 ? 1 : 0)
                        | (int)(chart.Flags >> 29 & 1) << 1 | (int)(chart.Flags >> 28 & 1) << 2);
                    var spike = random.NextDouble() < 0.01 ? 40f : 1f;
                    for (var k = 0; k < 3; k++)
                    {
                        var irradiance = tint[k] * R(0.5f, 1.5f) * spike;
                        image.Channels[0]!.Data[p * 4 + k] = irradiance;
                        var low = image.Channels[1 + k]!.Data;
                        low[p * 4] = irradiance * 0.9f;
                        for (var j = 1; j < 4; j++)
                            low[p * 4 + j] = R(-0.4f, 0.4f) * irradiance;
                        for (var j = 0; j < 4; j++)
                            image.Channels[4 + k]!.Data[p * 4 + j] = R(-0.2f, 0.2f) * irradiance;
                        image.Channels[7]!.Data[p * 4 + k] = R(-0.2f, 0.2f) * irradiance;
                    }
                    image.Channels[8]!.Data[p * 4] = R(0.3f, 1f);
                    var n = Unit(normal[0] + R(-0.1f, 0.1f), normal[1] + R(-0.1f, 0.1f), normal[2]);
                    for (var k = 0; k < 3; k++)
                    {
                        image.Channels[9]!.Data[p * 4 + k] = n[k];
                        image.Channels[11]!.Data[p * 4 + k] = tangent[k];
                    }
                    image.Channels[11]!.Data[p * 4 + 3] = sign;
                    image.Channels[10]!.Data[p * 4] = R(0f, 0.2f);
                    image.Channels[12]!.Data[p * 4] = R(0f, 0.2f);
                    lights.CopyTo(image.ShadowRecords, p * 20);
                    for (var s = 0; s < 4; s++)
                        BinaryPrimitives.WriteSingleLittleEndian(image.ShadowRecords.AsSpan(p * 20 + 4 + s * 4), R(-0.1f, 1.1f));
                }
        }
        image.Save(Path.Combine(folder, "oracle_in"));
    }

    // One quad per chart over its rect; vertices are position, normal, tangent and sign.
    private static PackingGeometry Geometry(List<PackingGeometry.Chart> charts)
    {
        var vertices = new MemoryStream();
        var w = new BinaryWriter(vertices);
        var uv = new List<float>();
        var indices = new List<uint>();
        var triangles = new List<uint>();
        foreach (var chart in charts)
        {
            var first = (uint)uv.Count / 2;
            (int X, int Y)[] corners = [(chart.X, chart.Y), (chart.X + chart.Width, chart.Y),
                (chart.X + chart.Width, chart.Y + chart.Height), (chart.X, chart.Y + chart.Height)];
            foreach (var (x, y) in corners)
            {
                foreach (var f in new float[] { x, y, 0, 0, 0, 1, 1, 0, 0, 1 })
                    w.Write(f);
                uv.Add(x / (float)Size);
                uv.Add(y / (float)Size);
            }
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
            triangles.AddRange([chart.Index, chart.Index]);
        }
        return new PackingGeometry
        {
            VertexStride = 40, Vertices = vertices.ToArray(), AtlasUv = [.. uv], Indices = [.. indices],
            Charts = [.. charts], TriangleCharts = [.. triangles], Offsets = [0, 0, 12, 24, 0, 0],
        };
    }

    private static float[] Unit(float x, float y, float z)
    {
        var l = MathF.Sqrt(x * x + y * y + z * z);
        return [x / l, y / l, z / l];
    }
}
