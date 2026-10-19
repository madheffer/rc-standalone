namespace Source2.Compiler.Maps.NavGen;

/// <summary>GenerateConnections (navsystem 20261015, 18006ea50) and what it needs.</summary>
public sealed partial class NavGenPolys
{
    /// <summary>
    /// 1800802b0: the point of triangle a, b, c nearest p (Ericson's regions: a, b, c, the edges, the
    /// face), in doubles.
    /// </summary>
    public static (double X, double Y, double Z) ClosestOnTriangle((double X, double Y, double Z) p, (double X, double Y, double Z) a,
        (double X, double Y, double Z) b, (double X, double Y, double Z) c)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
        double acx = c.X - a.X, acy = c.Y - a.Y, acz = c.Z - a.Z;
        double apx = p.X - a.X, apy = p.Y - a.Y, apz = p.Z - a.Z;
        var d2 = apy * acy + apx * acx + apz * acz;
        var d1 = apy * aby + apx * abx + apz * abz;
        if (!(0.0 < d1) && !(0.0 < d2))
            return a;
        double bpx = p.X - b.X, bpy = p.Y - b.Y, bpz = p.Z - b.Z;
        var d4 = bpy * acy + bpx * acx + bpz * acz;
        var d3 = bpy * aby + bpx * abx + bpz * abz;
        if (!(d3 < 0.0) && !(d3 < d4))
            return b;
        var vc = d4 * d1 - d3 * d2;
        if (!(0.0 < vc) && !(d1 < 0.0) && !(0.0 < d3))
        {
            var v = d1 / (d1 - d3);
            return (v * abx + a.X, v * aby + a.Y, v * abz + a.Z);
        }
        double cpx = p.X - c.X, cpy = p.Y - c.Y, cpz = p.Z - c.Z;
        var d5 = cpy * aby + cpx * abx + cpz * abz;
        var d6 = cpy * acy + cpx * acx + cpz * acz;
        if (0.0 <= d6 && d5 <= d6)
            return c;
        var vb = d5 * d2 - d6 * d1;
        if (!(0.0 < vb) && !(d2 < 0.0) && !(0.0 < d6))
        {
            var w = d2 / (d2 - d6);
            return (w * acx + a.X, w * acy + a.Y, w * acz + a.Z);
        }
        var va = d6 * d3 - d5 * d4;
        if (va <= 0.0 && 0.0 <= d4 - d3 && 0.0 <= d5 - d6)
        {
            var w = (d4 - d3) / ((d5 - d6) + (d4 - d3));
            return ((c.X - b.X) * w + b.X, (c.Y - b.Y) * w + b.Y, (c.Z - b.Z) * w + b.Z);
        }
        var denom = 1.0 / (va + vb + vc);
        var vv = denom * vb;
        var ww = denom * vc;
        return (vv * abx + a.X + ww * acx, vv * aby + a.Y + ww * acy, vv * abz + a.Z + ww * acz);
    }

    private static double Length((double X, double Y, double Z) v) => Math.Sqrt(v.Y * v.Y + v.X * v.X + v.Z * v.Z);

    /// <summary>
    /// 180077130: each polygon's +0x18 (the largest |corner|, from -FLT_MAX) and +0x10 (|nearest
    /// point of triangle 0-1-2 to the origin|, for a quad the smaller with triangle 0-2-3).
    /// </summary>
    public void ComputeSortKeys()
    {
        var origin = (0.0, 0.0, 0.0);
        foreach (var p in Polys)
        {
            var reach = (double)-float.MaxValue;
            // Corners in the four-wide loop sum x² + y² + z², the rest y² + x² + z².
            var n = p.Indices.Count;
            var wide = n >= 4 ? (((n - 4) >> 2) + 1) * 4 : 0;
            for (var k = 0; k < n; k++)
            {
                var q = Pool[p.Indices[k]];
                var d = k < wide ? Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z) : Math.Sqrt(q.Y * q.Y + q.X * q.X + q.Z * q.Z);
                if (!(d <= reach))
                    reach = d;
            }
            p.Reach = reach;
            var c = p.Indices.Select(i => Pool[i]).ToArray();
            var key = Length(ClosestOnTriangle(origin, c[0], c[1], c[2]));
            if (c.Length == 4)
            {
                var k2 = Length(ClosestOnTriangle(origin, c[0], c[2], c[3]));
                if (!(key <= k2))
                    key = k2;
            }
            p.SortKey = key;
        }
    }

    /// <summary>GenerateConnections' first steps: the keys (180077130), then MSVC std::sort on +0x10 (180067920).</summary>
    public void SortForConnections()
    {
        ComputeSortKeys();
        var arr = Polys.ToArray();
        MsvcSort.Sort(arr, (a, b) => a.SortKey < b.SortKey);
        Polys = [.. arr];
    }
}
