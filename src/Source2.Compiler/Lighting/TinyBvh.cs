using System.Numerics;

namespace Source2.Compiler.Lighting;

/// <summary>
/// TinyBVH's binned SAH builder as resourcecompiler links it (thirdparty\
/// tinybvh\tiny_bvh.h; 0923 181ed77b0 BVH::Build(customGetAABB, count),
/// 181ed64a0 the build): 16 bins on each axis, an axis skipped unless its
/// extent beats the root's times 1e-20, the cost c_trav + c_int * (ANL +
/// ANR) / SA against count * c_int, and an in-place partition that sends
/// a primitive right by swapping it with the range's end. Only the final
/// primitive order is kept; the lightmap packer (180225020) reads it.
/// </summary>
internal static class TinyBvh
{
    private const int Bins = 16;
    private const float Far = 1e30f;

    /// <summary>The primitive indices in the order the build leaves them.</summary>
    public static int[] Order(IReadOnlyList<(Vector3 Min, Vector3 Max)> boxes, float cTrav, float cInt)
    {
        var n = boxes.Count;
        if (n == 0)
            throw new ArgumentException("BVH::Build with no primitives", nameof(boxes));
        var idx = new int[n];
        var rootMin = new Vector3(Far);
        var rootMax = new Vector3(-Far);
        for (var i = 0; i < n; i++)
        {
            idx[i] = i;
            rootMin = Min(boxes[i].Min, rootMin);
            rootMax = Max(rootMax, boxes[i].Max);
        }
        var minDim = (rootMax - rootMin) * 1e-20f;
        var stack = new Stack<(Vector3 Min, Vector3 Max, int First, int Count)>();
        var node = (Min: rootMin, Max: rootMax, First: 0, Count: n);
        var binMin = new Vector3[3, Bins];
        var binMax = new Vector3[3, Bins];
        var count = new int[3, Bins];
        Span<float> anl = stackalloc float[Bins - 1], anr = stackalloc float[Bins - 1];
        var lMin = new Vector3[Bins - 1];
        var lMax = new Vector3[Bins - 1];
        var rMin = new Vector3[Bins - 1];
        var rMax = new Vector3[Bins - 1];
        while (true)
        {
            while (true)
            {
                for (var a = 0; a < 3; a++)
                    for (var b = 0; b < Bins; b++)
                    {
                        binMin[a, b] = new Vector3(Far);
                        binMax[a, b] = new Vector3(-Far);
                        count[a, b] = 0;
                    }
                var ext = node.Max - node.Min;
                var rpd = new Vector3(Bins / ext.X, Bins / ext.Y, Bins / ext.Z);
                for (var i = 0; i < node.Count; i++)
                {
                    var (bmin, bmax) = boxes[idx[node.First + i]];
                    for (var a = 0; a < 3; a++)
                    {
                        var b = Bin((bmin[a] + bmax[a]) * 0.5f, node.Min[a], rpd[a]);
                        count[a, b]++;
                        binMin[a, b] = Min(bmin, binMin[a, b]);
                        binMax[a, b] = Max(binMax[a, b], bmax);
                    }
                }
                var best = Far;
                int bestAxis = 0, bestPos = 0;
                Vector3 bestLMin = default, bestLMax = default, bestRMin = default, bestRMax = default;
                for (var a = 0; a < 3; a++)
                {
                    if (!(ext[a] > minDim[a]))
                        continue;
                    Vector3 l1 = new(Far), l2 = new(-Far), r1 = new(Far), r2 = new(-Far);
                    int ln = 0, rn = 0;
                    for (var i = 0; i < Bins - 1; i++)
                    {
                        lMin[i] = l1 = Min(binMin[a, i], l1);
                        lMax[i] = l2 = Max(l2, binMax[a, i]);
                        rMin[Bins - 2 - i] = r1 = Min(binMin[a, Bins - 1 - i], r1);
                        rMax[Bins - 2 - i] = r2 = Max(r2, binMax[a, Bins - 1 - i]);
                        ln += count[a, i];
                        rn += count[a, Bins - 1 - i];
                        anl[i] = ln == 0 ? Far : ln * HalfArea(l2 - l1);
                        anr[Bins - 2 - i] = rn == 0 ? Far : rn * HalfArea(r2 - r1);
                    }
                    for (var i = 0; i < Bins - 1; i++)
                    {
                        var c = anr[i] + anl[i];
                        if (c < best)
                        {
                            (best, bestAxis, bestPos) = (c, a, i);
                            (bestLMin, bestLMax, bestRMin, bestRMax) = (lMin[i], lMax[i], rMin[i], rMax[i]);
                        }
                    }
                }
                var sa = ext.Z * ext.Y + ext.Y * ext.X + ext.Z * ext.X;
                if (!(cInt * (1f / sa) * best + cTrav < node.Count * cInt))
                    break;
                int src = node.First, j = node.First + node.Count;
                for (var i = 0; i < node.Count; i++)
                {
                    var p = idx[src];
                    var (bmin, bmax) = boxes[p];
                    if (Bin((bmax[bestAxis] + bmin[bestAxis]) * 0.5f, node.Min[bestAxis], rpd[bestAxis]) <= bestPos)
                        src++;
                    else
                    {
                        j--;
                        idx[src] = idx[j];
                        idx[j] = p;
                    }
                }
                var left = src - node.First;
                if (left == 0 || node.Count - left == 0)
                    break;
                if (stack.Count == 0x100)
                    throw new NotSupportedException("a TinyBVH build deeper than its 256-entry task stack");
                stack.Push((bestRMin, bestRMax, j, node.Count - left));
                node = (bestLMin, bestLMax, node.First, left);
            }
            if (stack.Count == 0)
                return idx;
            node = stack.Pop();
        }
    }

    // ((c - min) * rpd) truncated, clamped to the bins; a NaN or a negative
    // value lands in bin 0, as does one past int range (cvttss2si's 0x80000000).
    private static int Bin(float centre, float min, float rpd)
    {
        var v = (centre - min) * rpd;
        if (!(v >= 1f) || v >= 2147483648f)
            return 0;
        return v >= Bins - 1 ? Bins - 1 : (int)v;
    }

    private static float HalfArea(Vector3 e)
        => e.X < -Far ? 0f : e.Z * e.Y + e.Y * e.X + e.Z * e.X;

    // tinybvh_min / tinybvh_max per lane: the first operand on a tie.
    private static Vector3 Min(Vector3 v, Vector3 b)
        => new(v.X <= b.X ? v.X : b.X, v.Y <= b.Y ? v.Y : b.Y, v.Z <= b.Z ? v.Z : b.Z);

    private static Vector3 Max(Vector3 b, Vector3 v)
        => new(b.X <= v.X ? v.X : b.X, b.Y <= v.Y ? v.Y : b.Y, b.Z <= v.Z ? v.Z : b.Z);
}
