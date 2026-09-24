using System.Numerics;
using Source2.Compiler.Physics;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: every convex hull in a compiled map's entity models, fed back
/// through <see cref="RnHullBuilder"/> from its own vertex positions, and
/// compared field by field with what Valve shipped. A box hull depends only on
/// its bounds, so it must match bit for bit. Any other hull is rebuilt from
/// points in Valve's output order rather than the compile's input order, so
/// its vertex, edge and face order may legitimately differ; its floats are
/// still compared. <c>HULL=&lt;map .vpk&gt;</c>, <c>HULL_LIMIT</c>, <c>HULL_SHOW</c>.
/// </summary>
public class HullReplay(ITestOutputHelper output)
{
    [Fact]
    public void RebuildShippedHulls()
    {
        if (Environment.GetEnvironmentVariable("HULL") is not { Length: > 0 } path)
            return;
        var limit = int.TryParse(Environment.GetEnvironmentVariable("HULL_LIMIT"), out var l) ? l : int.MaxValue;
        var show = int.TryParse(Environment.GetEnvironmentVariable("HULL_SHOW"), out var s) ? s : 10;
        using var package = new Package();
        package.Read(path);
        var tally = new Dictionary<string, int>();
        var shown = 0;
        var seen = 0;
        var filter = Environment.GetEnvironmentVariable("HULL_FILTER") ?? "/entities/";
        foreach (var entry in package.Entries.SelectMany(kv => kv.Value).Where(e => e.TypeName == "vmdl_c" && e.GetFullPath().Contains(filter, StringComparison.Ordinal)))
        {
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            if (resource.DataBlock is not Model model || model.GetEmbeddedPhys() is not { } phys)
                continue;
            foreach (var part in phys.Parts)
            {
                foreach (var desc in part.Shape.Hulls)
                {
                    if (seen++ >= limit)
                        break;
                    var hull = desc.Shape;
                    var kind = hull.Data.GetIntegerProperty("m_nFlags") == 3 ? "box" : "hull";
                    var diffs = Compare(hull, out var note);
                    var key = $"{kind} {(diffs.Count == 0 ? "exact" : note)}";
                    tally[key] = tally.GetValueOrDefault(key) + 1;
                    if (diffs.Count > 0 && shown++ < show)
                        output.WriteLine($"{entry.GetFullPath()} {kind}: {string.Join("; ", diffs.Take(8))}");
                }
            }
        }
        foreach (var (k, v) in tally.OrderBy(kv => kv.Key))
            output.WriteLine($"{k}: {v}");
    }

    private static List<string> Compare(Hull valve, out string note)
    {
        var diffs = new List<string>();
        note = "";
        var points = valve.GetVertexPositions().ToArray();
        RnHull? ours;
        try
        {
            ours = RnHullBuilder.Create(points, null, out var error);
            if (ours == null)
            {
                note = $"failed {error}";
                diffs.Add(note);
                return diffs;
            }
        }
        catch (NotSupportedException ex)
        {
            note = "unported: " + ex.Message;
            diffs.Add(note);
            return diffs;
        }
        var d = valve.Data;
        void F(string name, float a, float b)
        {
            if (BitConverter.SingleToInt32Bits(a) != BitConverter.SingleToInt32Bits(b))
                diffs.Add($"{name} valve {a:R} ours {b:R}");
        }
        void V(string name, Vector3 a, Vector3 b)
        {
            F(name + ".x", a.X, b.X);
            F(name + ".y", a.Y, b.Y);
            F(name + ".z", a.Z, b.Z);
        }
        V("centroid", valve.Centroid, ours.Centroid);
        F("maxAngularRadius", valve.MaxAngularRadius, ours.MaxAngularRadius);
        F("minCentroidRadius", d.GetFloatProperty("m_flMinCentroidRadius"), ours.MinCentroidRadius);
        V("min", valve.Min, ours.BoundsMin);
        V("max", valve.Max, ours.BoundsMax);
        V("ortho", valve.OrthographicAreas, ours.OrthographicAreas);
        var mass = d.GetArray<double>("m_MassProperties");
        for (var i = 0; i < 12; i++)
            F($"mass[{i}]", (float)mass[i], ours.MassProperties[i]);
        F("volume", valve.Volume, ours.Volume);
        F("area", d.GetFloatProperty("m_flSurfaceArea"), ours.SurfaceArea);
        if ((uint)d.GetIntegerProperty("m_nFlags") != ours.Flags)
            diffs.Add($"flags valve {d.GetIntegerProperty("m_nFlags")} ours {ours.Flags}");
        var sameOrder = points.Length == ours.VertexPositions.Length;
        if (!sameOrder)
        {
            diffs.Add($"vertices valve {points.Length} ours {ours.VertexPositions.Length}");
        }
        else
        {
            for (var i = 0; i < points.Length; i++)
            {
                if (points[i] != ours.VertexPositions[i])
                {
                    sameOrder = false;
                    break;
                }
            }
            if (!sameOrder)
            {
                var set = new HashSet<Vector3>(points);
                var missing = ours.VertexPositions.Count(p => !set.Contains(p));
                diffs.Add(missing == 0 ? "vertex order" : $"{missing} vertices moved");
            }
        }
        if (sameOrder)
        {
            if (!valve.GetVertices().SequenceEqual(ours.Vertices))
                diffs.Add("vertex edges");
            var edges = valve.GetEdges();
            if (edges.Length != ours.Edges.Length)
                diffs.Add($"edges valve {edges.Length} ours {ours.Edges.Length}");
            else
            {
                for (var i = 0; i < edges.Length; i++)
                {
                    if ((edges[i].Next, edges[i].Twin, edges[i].Origin, edges[i].Face) != ours.Edges[i])
                    {
                        diffs.Add($"edge {i}");
                        break;
                    }
                }
            }
            var faces = valve.GetFaces();
            if (!faces.ToArray().Select(f => f.Edge).SequenceEqual(ours.Faces))
                diffs.Add("faces");
            var planes = valve.GetPlanes();
            for (var i = 0; i < Math.Min(planes.Length, ours.Planes.Length); i++)
            {
                V($"plane{i}.n", planes[i].Normal, ours.Planes[i].Normal);
                F($"plane{i}.d", planes[i].Offset, ours.Planes[i].Offset);
            }
        }
        if (note.Length == 0 && diffs.Count > 0)
            note = diffs.Count == 1 ? diffs[0].Split(' ')[0] : "differs";
        return diffs;
    }
}
