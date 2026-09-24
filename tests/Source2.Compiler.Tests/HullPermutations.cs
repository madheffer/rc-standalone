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
/// Exploration: for small non-box shipped hulls, every ordering of the hull's
/// own vertices fed to <c>RnHullCreate</c>, counting the orderings that rebuild
/// the shipped hull exactly (order, floats, topology). None at all means the
/// port loses order by itself. <c>HULLPERM=&lt;map .vpk&gt;</c>, <c>HULLPERM_MAX</c>
/// (vertices, default 7).
/// </summary>
public class HullPermutations(ITestOutputHelper output)
{
    [Fact]
    public void SmallHullsFromEveryOrdering()
    {
        if (Environment.GetEnvironmentVariable("HULLPERM") is not { Length: > 0 } path)
            return;
        var max = int.TryParse(Environment.GetEnvironmentVariable("HULLPERM_MAX"), out var m) ? m : 7;
        using var package = new Package();
        package.Read(path);
        var tally = new Dictionary<string, int>();
        var shown = 0;
        foreach (var entry in package.Entries.SelectMany(kv => kv.Value).Where(e => e.TypeName == "vmdl_c" && e.GetFullPath().Contains("/entities/", StringComparison.Ordinal)))
        {
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            if (resource.DataBlock is not Model model || model.GetEmbeddedPhys() is not { } phys)
                continue;
            foreach (var hull in phys.Parts.SelectMany(p => p.Shape.Hulls).Select(h => h.Shape))
            {
                if (hull.Data.GetIntegerProperty("m_nFlags") == 3)
                    continue;
                var pts = hull.GetVertexPositions().ToArray();
                if (pts.Length > max)
                    continue;
                int hits = 0, total = 0;
                foreach (var perm in Permutations(pts.Length))
                {
                    total++;
                    var input = perm.Select(i => pts[i]).ToArray();
                    RnHull? ours;
                    try { ours = RnHullBuilder.Create(input, null, out _); }
                    catch (NotSupportedException) { continue; }
                    if (ours != null && Same(hull, ours))
                        hits++;
                }
                var key = hits > 0 ? "some ordering rebuilds it" : "no ordering rebuilds it";
                tally[key] = tally.GetValueOrDefault(key) + 1;
                if (hits == 0 && shown++ < 8)
                    output.WriteLine($"{entry.GetFullPath()}: {pts.Length} verts, 0 of {total}");
            }
        }
        foreach (var (k, v) in tally)
            output.WriteLine($"{k}: {v}");
    }

    private static bool Same(ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes.Hull valve, RnHull ours)
    {
        var loose = Environment.GetEnvironmentVariable("HULLPERM_LOOSE") == "1";
        var vp = valve.GetVertexPositions().ToArray();
        if (vp.Length != ours.VertexPositions.Length)
            return false;
        for (var i = 0; i < vp.Length; i++)
        {
            if (loose ? Vector3.Distance(vp[i], ours.VertexPositions[i]) > 0.05f : vp[i] != ours.VertexPositions[i])
                return false;
        }
        var e = valve.GetEdges().ToArray();
        if (e.Length != ours.Edges.Length)
            return false;
        for (var i = 0; i < e.Length; i++)
        {
            if ((e[i].Next, e[i].Twin, e[i].Origin, e[i].Face) != ours.Edges[i])
                return false;
        }
        if (Environment.GetEnvironmentVariable("HULLPERM_LOOSE") == "1")
            return true;
        var mass = valve.Data.GetArray<double>("m_MassProperties");
        for (var i = 0; i < 12; i++)
        {
            if (BitConverter.SingleToInt32Bits((float)mass[i]) != BitConverter.SingleToInt32Bits(ours.MassProperties[i]))
                return false;
        }
        return true;
    }

    private static IEnumerable<int[]> Permutations(int n)
    {
        var a = Enumerable.Range(0, n).ToArray();
        var c = new int[n];
        yield return (int[])a.Clone();
        var i = 0;
        while (i < n)
        {
            if (c[i] < i)
            {
                if (i % 2 == 0) (a[0], a[i]) = (a[i], a[0]);
                else (a[c[i]], a[i]) = (a[i], a[c[i]]);
                yield return (int[])a.Clone();
                c[i]++;
                i = 0;
            }
            else
            {
                c[i] = 0;
                i++;
            }
        }
    }
}
