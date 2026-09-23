using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// <c>Logs_RaytracedSunVisibilityInMsVisibleCluste</c>: which clusters see a
/// sunlit surface. Rays start every 4 units over each face of the scene's
/// bounds (grown by 1) that faces the sun and run away from it; a front face
/// they meet, backed off 0.1, is traced back toward the sun, and every cluster
/// the segment between the two crosses is marked.
/// </summary>
public static class VisSun
{
    /// <summary>What the first trace passes through, the job's <c>0x5811</c>.</summary>
    public const ushort Ignored = 0x5811;

    /// <summary>
    /// Per leaf, the open cells as they stood when the border stage began
    /// (<c>sampler+0x60</c>), which the sun's walk is cut to.
    /// </summary>
    public static ulong[] OpenCells(VisPvs.State s)
    {
        var open = new ulong[s.NodeWords.Length];
        for (var node = 0; node < open.Length; node++)
        {
            var word = s.NodeWords[node];
            if ((word & 1) == 0)
                continue;
            var first = (int)(word >> 1);
            for (var k = 0; k < s.NodeCounts[node]; k++)
            {
                var e = s.Entries[first + k];
                if (e.Kind == VisVisibility.Open)
                    open[node] |= e.Cells;
            }
        }
        return open;
    }

    /// <summary>One bit per cluster.</summary>
    public static uint[] Visible(VisPvs.State s, RayTraceEnvironment rte, Vector3 sun, ulong[] open)
    {
        var (tmin, tmax) = rte.TracedBounds;
        var lo = new Vector3(tmin.X - 1f, tmin.Y - 1f, tmin.Z - 1f);
        var hi = new Vector3(tmax.X + 1f, tmax.Y + 1f, tmax.Z + 1f);
        float ex = hi.X - lo.X, ey = hi.Y - lo.Y, ez = hi.Z - lo.Z;
        var reach = MathF.Sqrt((ez * ez) + (ey * ey) + (ex * ex));

        var points = new List<Vector3>();
        for (var face = 0; face < 6; face++)
        {
            var axis = face % 3;
            var n = Vector3.Zero;
            n = With(n, axis, face < 3 ? -1f : 1f);
            if (!((sun.Z * n.Z) + (sun.Y * n.Y) + (n.X * sun.X) < 0f))
                continue;
            int a1 = (face + 1) % 3, a2 = (face + 2) % 3;
            var u = With(Vector3.Zero, a1, 1f);
            var v = With(Vector3.Zero, a2, 1f);
            var basePoint = With(Vector3.Zero, axis, face < 3 ? At(hi, axis) : At(lo, axis));
            for (var f1 = At(lo, a1); f1 < At(hi, a1); f1 += 4f)
            {
                var row = new Vector3((u.X * f1) + basePoint.X, (u.Y * f1) + basePoint.Y, (u.Z * f1) + basePoint.Z);
                for (var f2 = At(lo, a2); f2 < At(hi, a2); f2 += 4f)
                    points.Add(new Vector3((v.X * f2) + row.X, (v.Y * f2) + row.Y, (v.Z * f2) + row.Z));
            }
        }

        var words = (s.Clusters + 31) >> 5;
        var visible = new uint[words];
        float ax = -sun.X, ay = -sun.Y, az = -sun.Z;
        float lx = reach * ax, ly = reach * ay, lz = reach * az;
        Parallel.ForEach(points, p =>
        {
            var end = new Vector3(lx + p.X, ly + p.Y, lz + p.Z);
            if (rte.Segment(p, end, Ignored) is not { } hit)
                return;
            var nn = hit.Normal;
            if (!((az * nn.Z) + (ay * nn.Y) + (nn.X * ax) < 0f))
                return;
            var back = hit.Distance - 0.1f;
            var from = new Vector3((back * ax) + p.X, (back * ay) + p.Y, (back * az) + p.Z);
            var to = new Vector3(from.X - lx, from.Y - ly, from.Z - lz);
            if (rte.Segment(from, to, 0) is { } second)
            {
                var ahead = second.Distance - 0.1f;
                to = new Vector3((ahead * sun.X) + from.X, (ahead * sun.Y) + from.Y, (ahead * sun.Z) + from.Z);
            }
            if (VisPvs.Walk(s, from, to, through: true, open) is not { Count: > 0 } ids)
                return;
            foreach (var id in ids)
            {
                if ((uint)id < (uint)s.Clusters)
                    Interlocked.Or(ref visible[id >> 5], 1u << (id & 31));
            }
        });
        return visible;
    }

    private static float At(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static Vector3 With(Vector3 v, int axis, float value)
        => axis == 0 ? v with { X = value } : axis == 1 ? v with { Y = value } : v with { Z = value };
}
