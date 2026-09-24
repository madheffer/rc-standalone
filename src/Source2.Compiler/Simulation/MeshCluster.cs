using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

/// <summary>A normal cluster of per-triangle manifolds (0x28 bytes in FUN_1802fe660).</summary>
public sealed class NormalCluster
{
    public Vec3 Normal;
    public List<int> Members = [];
}

public static partial class MeshCollision
{
    private const float TinySquared = 1.17549435e-35f;

    /// <summary>
    /// k-means of the per-triangle normals (FUN_1802fe660). Seeds: the most
    /// opposed pair below 0.995 (first found), and the manifold most along
    /// their cross product that agrees with neither (FUN_180301c80). No pair
    /// below 0.995 gives one cluster of all, with manifold 0's normal. Up to
    /// eight rounds of assign (first best) and re-average; a run that has not
    /// settled after eight ends with every cluster emptied, as the DLL does.
    /// Empty clusters are dropped, the last taking their place.
    /// </summary>
    internal static List<NormalCluster> Cluster(List<CachedManifold> m)
    {
        var clusters = new List<NormalCluster>();
        var n = m.Count;
        if (n == 0)
            return clusters;
        var best = SameNormal;
        int si = 0, sj = 0;
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                var d = Dot(m[j].Normal, m[i].Normal);
                if (best > d)
                {
                    best = d;
                    si = i;
                    sj = j;
                }
            }
        if (si == sj)
        {
            var all = new NormalCluster { Normal = m[0].Normal };
            for (var k = 0; k < n; k++)
                all.Members.Add(k);
            clusters.Add(all);
            return clusters;
        }
        clusters.Add(new NormalCluster { Normal = m[si].Normal });
        clusters.Add(new NormalCluster { Normal = m[sj].Normal });
        var third = ThirdSeed(si, sj, m);
        if (third >= 0)
            clusters.Add(new NormalCluster { Normal = m[third].Normal });

        var assigned = new int[n];
        Array.Fill(assigned, -1);
        for (var round = 0; round < 8; round++)
        {
            var settled = true;
            for (var k = 0; k < n; k++)
            {
                var top = -float.MaxValue;
                var index = -1;
                for (var c = 0; c < clusters.Count; c++)
                {
                    var d = Dot(clusters[c].Normal, m[k].Normal);
                    if (d > top)
                    {
                        top = d;
                        index = c;
                    }
                }
                clusters[index].Members.Add(k);
                if (assigned[k] != index)
                {
                    assigned[k] = index;
                    settled = false;
                }
            }
            if (settled)
                break;
            foreach (var c in clusters)
            {
                float x = 0f, y = 0f, z = 0f;
                foreach (var k in c.Members)
                {
                    x += m[k].Normal.X;
                    y += m[k].Normal.Y;
                    z += m[k].Normal.Z;
                }
                var lengthSquared = (y * y + x * x) + z * z;
                if (lengthSquared > TinySquared)
                {
                    var inv = 1f / MathF.Sqrt(lengthSquared);
                    c.Normal = new Vec3(x * inv, y * inv, z * inv);
                }
                else
                {
                    c.Normal = default;
                }
                c.Members.Clear();
            }
        }
        for (var i = clusters.Count - 1; i >= 0; i--)
        {
            if (clusters[i].Members.Count != 0)
                continue;
            clusters[i] = clusters[^1];
            clusters.RemoveAt(clusters.Count - 1);
        }
        return clusters;
    }

    /// <summary>(a.z b.z + a.y b.y) + a.x b.x, the clustering's dot.</summary>
    private static float Dot(Vec3 a, Vec3 b) => (a.Z * b.Z + a.Y * b.Y) + a.X * b.X;

    /// <summary>
    /// The third seed (FUN_180301c80): k = the unit ni x nj; among the other
    /// manifolds agreeing with neither seed (dot below 0.995), the one with
    /// the largest |k · n|. -1 for none.
    /// </summary>
    internal static int ThirdSeed(int i, int j, List<CachedManifold> m)
    {
        var a = m[i].Normal;
        var b = m[j].Normal;
        var kx = b.Z * a.Y - b.Y * a.Z;
        var ky = b.X * a.Z - a.X * b.Z;
        var kz = a.X * b.Y - b.X * a.Y;
        var lengthSquared = (ky * ky + kx * kx) + kz * kz;
        if (lengthSquared > TinySquared)
        {
            var inv = 1f / MathF.Sqrt(lengthSquared);
            kx *= inv;
            ky *= inv;
            kz *= inv;
        }
        else
        {
            kx = ky = kz = 0f;
        }
        var best = 0f;
        var index = -1;
        for (var t = 0; t < m.Count; t++)
        {
            if (t == i || t == j)
                continue;
            var n = m[t].Normal;
            var score = MathF.Abs((kz * n.Z + ky * n.Y) + kx * n.X);
            if (score > best && SameNormal > Dot(n, a) && SameNormal > Dot(n, b))
            {
                best = score;
                index = t;
            }
        }
        return index;
    }

    /// <summary>
    /// One manifold per cluster (FUN_1802fd8b0): the members' points in
    /// order, reduced to four (FUN_180300d50). If the reduction lost depth,
    /// the lost part (less 1.19e-5, at most 1/8) is added to the A side along
    /// R_A times the normal, not R_Aᵀ, as the DLL does. The friction basis is
    /// built inline and the friction impulses start at zero.
    /// </summary>
    internal static List<CachedManifold> Merge(in RnTransform xfA, in RnTransform xfB, List<NormalCluster> clusters,
                                               List<CachedManifold> manifolds)
    {
        var result = new List<CachedManifold>(clusters.Count);
        foreach (var cluster in clusters)
        {
            var points = new List<CachedPoint>();
            foreach (var k in cluster.Members)
            {
                var member = manifolds[k];
                for (var i = 0; i < member.PointCount; i++)
                    points.Add(member.Points[i]);
            }
            var n = cluster.Normal;
            var before = float.MaxValue;
            foreach (var p in points)
            {
                var s = Separation(xfA, xfB, p, n);
                before = before < s ? before : s;
            }
            var count = ReducePoints(CollectionsMarshal.AsSpan(points), xfA, n);
            points.RemoveRange(count, points.Count - count);
            var after = float.MaxValue;
            foreach (var p in points)
            {
                var s = Separation(xfA, xfB, p, n);
                after = after < s ? after : s;
            }
            var lost = (after - before) - 1.1920929e-05f;
            if (lost > 0f)
            {
                lost = lost < HullCollision.Speculative ? lost : HullCollision.Speculative;
                var vx = lost * n.X;
                var vy = lost * n.Y;
                var vz = lost * n.Z;
                ref readonly var r = ref xfA.R;
                var sx = (vx * r.M0 + vy * r.M3) + vz * r.M6;
                var sy = (vx * r.M1 + vy * r.M4) + vz * r.M7;
                var sz = (vx * r.M2 + vy * r.M5) + vz * r.M8;
                var span = CollectionsMarshal.AsSpan(points);
                for (var i = 0; i < span.Length; i++)
                    span[i].LocalA = new Vec3(sx + span[i].LocalA.X, sy + span[i].LocalA.Y, sz + span[i].LocalA.Z);
            }
            float cx = 0f, cy = 0f, cz = 0f;
            foreach (var p in points)
            {
                var w = HullCollision.ToWorld(xfA, p.LocalA.X, p.LocalA.Y, p.LocalA.Z);
                cx += w.X;
                cy += w.Y;
                cz += w.Z;
            }
            var inv = 1f / points.Count;
            var t1 = InPlaneTangent(n);
            var manifold = new CachedManifold
            {
                Centre = new Vec3(cx * inv, cy * inv, cz * inv),
                Normal = n,
                T1 = t1,
                T2 = new Vec3(t1.Y * n.Z - t1.Z * n.Y, n.X * t1.Z - t1.X * n.Z, t1.X * n.Y - n.X * t1.Y),
            };
            foreach (var p in points)
                manifold.Points[manifold.PointCount++] = p;
            result.Add(manifold);
        }
        return result;
    }

    /// <summary>
    /// The first tangent of the mesh path (inline in FUN_1802fd8b0 and
    /// FUN_180300d50): in the y-z plane unless n leans to x.
    /// </summary>
    private static Vec3 InPlaneTangent(Vec3 n)
    {
        if (!(MathF.Abs(n.X) >= 0.57735f))
        {
            var length = MathF.Sqrt(n.Z * n.Z + n.Y * n.Y);
            return new Vec3(0f, n.Z / length, -n.Y / length);
        }
        var l = MathF.Sqrt(n.X * n.X + n.Y * n.Y);
        return new Vec3(n.Y / l, -n.X / l, 0f);
    }

    /// <summary>(B's world point - A's) · n, the merge's separation.</summary>
    private static float Separation(in RnTransform xfA, in RnTransform xfB, in CachedPoint p, Vec3 n)
    {
        var b = HullCollision.ToWorld(xfB, p.LocalB.X, p.LocalB.Y, p.LocalB.Z);
        var a = HullCollision.ToWorld(xfA, p.LocalA.X, p.LocalA.Y, p.LocalA.Z);
        return ((b.X - a.X) * n.X + (b.Y - a.Y) * n.Y) + (b.Z - a.Z) * n.Z;
    }
}
