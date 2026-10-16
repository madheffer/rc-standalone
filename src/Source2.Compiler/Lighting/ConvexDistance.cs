using System.Numerics;

namespace Source2.Compiler.Lighting;

/// <summary>
/// The distance between the convex hulls of two point sets, by GJK in double
/// precision with an exhaustive simplex reduction (every face of the current
/// simplex tried, the nearest feature whose barycentrics are all non-negative
/// kept). Zero when the hulls touch or overlap. It stands in for vphysics'
/// GJK queries the lightmap importance scales use (resourcecompiler 181266fc0,
/// 181267a70), which agree with it to float rounding.
/// </summary>
internal static class ConvexDistance
{
    /// <summary>The distance from <paramref name="p"/> to the hull of <paramref name="hull"/>.</summary>
    public static float PointToHull(Vector3 p, IReadOnlyList<Vector3> hull) => HullToHull([p], hull);

    /// <summary>The corners of a box, as a point set.</summary>
    public static Vector3[] Box(Vector3[] corners) => corners;

    public static float HullToHull(IReadOnlyList<Vector3> a, IReadOnlyList<Vector3> b)
    {
        var simplex = new List<Vector3d> { Support(a, b, new Vector3d(1, 0, 0)) };
        var v = simplex[0];
        for (var iteration = 0; iteration < 128; iteration++)
        {
            var vv = v.Dot(v);
            if (vv < 1e-24)
                return 0f;
            var w = Support(a, b, -v);
            if (vv - v.Dot(w) <= 1e-12 * vv)
                break;
            if (simplex.Any(s => (s - w).Dot(s - w) < 1e-24))
                break;
            simplex.Add(w);
            (v, simplex) = Closest(simplex);
            if (simplex.Count == 4)
                return 0f;
        }
        return (float)Math.Sqrt(v.Dot(v));
    }

    // A point of the Minkowski difference a - b furthest along d.
    private static Vector3d Support(IReadOnlyList<Vector3> a, IReadOnlyList<Vector3> b, Vector3d d)
        => Furthest(a, d) - Furthest(b, -d);

    private static Vector3d Furthest(IReadOnlyList<Vector3> s, Vector3d d)
    {
        var best = new Vector3d(s[0]);
        var bestDot = best.Dot(d);
        for (var i = 1; i < s.Count; i++)
        {
            var p = new Vector3d(s[i]);
            var x = p.Dot(d);
            if (x > bestDot)
                (best, bestDot) = (p, x);
        }
        return best;
    }

    // The point of the simplex nearest the origin and the smallest face holding it.
    private static (Vector3d Point, List<Vector3d> Face) Closest(List<Vector3d> s)
    {
        var bestPoint = default(Vector3d);
        List<Vector3d>? bestFace = null;
        var bestNorm = double.MaxValue;
        for (var mask = 1; mask < 1 << s.Count; mask++)
        {
            var face = new List<Vector3d>();
            for (var i = 0; i < s.Count; i++)
                if ((mask & (1 << i)) != 0)
                    face.Add(s[i]);
            if (Affine(face) is not { } p)
                continue;
            var n = p.Dot(p);
            if (n < bestNorm - 1e-18 || (bestFace != null && Math.Abs(n - bestNorm) <= 1e-18 && face.Count < bestFace.Count))
                (bestPoint, bestFace, bestNorm) = (p, face, n);
        }
        return (bestPoint, bestFace ?? [s[0]]);
    }

    // The origin's projection onto the face's affine hull when it falls inside
    // the face (all barycentrics non-negative), else null.
    private static Vector3d? Affine(List<Vector3d> f)
    {
        if (f.Count == 1)
            return f[0];
        var k = f.Count - 1;
        var e = new Vector3d[k];
        for (var i = 0; i < k; i++)
            e[i] = f[i + 1] - f[0];
        var g = new double[k, k + 1];
        for (var i = 0; i < k; i++)
        {
            for (var j = 0; j < k; j++)
                g[i, j] = e[i].Dot(e[j]);
            g[i, k] = -e[i].Dot(f[0]);
        }
        for (var c = 0; c < k; c++)
        {
            var pivot = c;
            for (var r = c + 1; r < k; r++)
                if (Math.Abs(g[r, c]) > Math.Abs(g[pivot, c]))
                    pivot = r;
            if (Math.Abs(g[pivot, c]) < 1e-18)
                return null;
            for (var j = 0; j <= k; j++)
                (g[c, j], g[pivot, j]) = (g[pivot, j], g[c, j]);
            for (var r = 0; r < k; r++)
            {
                if (r == c)
                    continue;
                var x = g[r, c] / g[c, c];
                for (var j = 0; j <= k; j++)
                    g[r, j] -= x * g[c, j];
            }
        }
        var mu = new double[k];
        var sum = 0.0;
        for (var i = 0; i < k; i++)
        {
            mu[i] = g[i, k] / g[i, i];
            if (mu[i] < -1e-12)
                return null;
            sum += mu[i];
        }
        if (1 - sum < -1e-12)
            return null;
        var p = f[0];
        for (var i = 0; i < k; i++)
            p += e[i] * mu[i];
        return p;
    }

    private readonly record struct Vector3d(double X, double Y, double Z)
    {
        public Vector3d(Vector3 v) : this(v.X, v.Y, v.Z) { }
        public double Dot(Vector3d o) => X * o.X + Y * o.Y + Z * o.Z;
        public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3d operator -(Vector3d a) => new(-a.X, -a.Y, -a.Z);
        public static Vector3d operator *(Vector3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    }
}
