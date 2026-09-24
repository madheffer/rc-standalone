using System.Numerics;
using Source2.Compiler.Physics;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: the mass, area and centroid radius passes run on shipped hulls'
/// own topology, so they are checked apart from hull order.
/// <c>HULLPASS=&lt;map .vpk&gt;</c>.
/// </summary>
public class HullPasses(ITestOutputHelper output)
{
    [Fact]
    public void PassesOnShippedTopology()
    {
        if (Environment.GetEnvironmentVariable("HULLPASS") is not { Length: > 0 } path)
            return;
        using var package = new Package();
        package.Read(path);
        var tally = new Dictionary<string, int>();
        var shown = 0;
        foreach (var entry in package.Entries.SelectMany(kv => kv.Value).Where(e => e.TypeName == "vmdl_c"))
        {
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            if (resource.DataBlock is not Model model || model.GetEmbeddedPhys() is not { } phys)
                continue;
            foreach (var v in phys.Parts.SelectMany(p => p.Shape.Hulls).Select(h => h.Shape))
            {
                if (v.Data.GetIntegerProperty("m_nFlags") == 3)
                    continue;
                var h = new RnHull
                {
                    Centroid = v.Centroid,
                    BoundsMin = v.Min,
                    BoundsMax = v.Max,
                    VertexPositions = v.GetVertexPositions().ToArray(),
                    Edges = [.. v.GetEdges().ToArray().Select(e => (e.Next, e.Twin, e.Origin, e.Face))],
                    Faces = [.. v.GetFaces().ToArray().Select(f => f.Edge)],
                    Planes = [.. v.GetPlanes().ToArray().Select(p => (p.Normal, p.Offset))],
                };
                RnHullBuilder.MassProperties(h);
                RnHullBuilder.Areas(h);
                RnHullBuilder.CentroidRadius(h);
                RnHullBuilder.Transform(h, RnHullBuilder.Identity);
                var d = v.Data;
                var diffs = new List<string>();
                void F(string name, float a, float b)
                {
                    if (BitConverter.SingleToInt32Bits(a) != BitConverter.SingleToInt32Bits(b))
                        diffs.Add($"{name} valve {a:R} ours {b:R}");
                }
                var mass = d.GetArray<double>("m_MassProperties");
                for (var i = 0; i < 12; i++)
                    F($"mass[{i}]", (float)mass[i], h.MassProperties[i]);
                F("volume", v.Volume, h.Volume);
                F("area", d.GetFloatProperty("m_flSurfaceArea"), h.SurfaceArea);
                F("ortho.x", v.OrthographicAreas.X, h.OrthographicAreas.X);
                F("minRadius", d.GetFloatProperty("m_flMinCentroidRadius"), h.MinCentroidRadius);
                var key = diffs.Count == 0 ? "exact" : string.Join(",", diffs.Select(x => x.Split(' ')[0]).Distinct().Take(4));
                tally[key] = tally.GetValueOrDefault(key) + 1;
                if (diffs.Count > 0 && shown++ < 8)
                {
                    output.WriteLine($"{entry.GetFullPath()}: {string.Join("; ", diffs.Take(5))}");
                }
            }
        }
        foreach (var (k, n) in tally.OrderByDescending(kv => kv.Value).Take(15))
            output.WriteLine($"{k}: {n}");
    }
}
