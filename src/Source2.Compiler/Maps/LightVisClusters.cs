using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Source2.Compiler.Maps;

/// <summary>
/// A light's <c>precomputed_vis_clusters</c> (<c>WRB_PrecomputeLightVisMembership</c>,
/// 180247d40): written for light_barn, light_rect and light_omni2 when the
/// compile bakes <c>direct_light_shadows</c>. The light precompute's trace
/// hands its callback (180248440) the light's bounds and its rays as they
/// end (cut at their hits); every vis cluster with a box overlapping the
/// bounds, edges included, is a candidate, and a candidate one of whose
/// boxes a ray segment reaches (<see cref="SegmentHitsBox"/> at 0.001,
/// the "TestClusters" job 18024ee30) goes in. The list is sorted ascending.
/// </summary>
public static class LightVisClusters
{
    public static int[] Compute(Vector3 mins, Vector3 maxs, Vector3[] starts, Vector3[] ends, List<(Vector3 Min, Vector3 Max)>[] flat)
    {
        var found = new List<int>();
        for (var c = 0; c < flat.Length; c++)
        {
            var candidate = false;
            foreach (var (bmin, bmax) in flat[c])
            {
                if (bmax.X < mins.X || maxs.X < bmin.X || bmax.Y < mins.Y || maxs.Y < bmin.Y || bmax.Z < mins.Z || maxs.Z < bmin.Z)
                    continue;
                candidate = true;
                break;
            }
            if (!candidate)
                continue;
            var hit = false;
            foreach (var (bmin, bmax) in flat[c])
            {
                for (var k = 0; k < starts.Length && !hit; k++)
                {
                    var s = starts[k];
                    var e = ends[k];
                    var d = new Vector3(e.X - s.X, e.Y - s.Y, e.Z - s.Z);
                    hit = SegmentHitsBox(bmin, bmax, s, d, 0.001f);
                }
                if (hit)
                    break;
            }
            if (hit)
                found.Add(c);
        }
        return [.. found];
    }

    /// <summary>
    /// FUN_18129b7b0: the segment from <paramref name="s"/> along
    /// <paramref name="d"/> against the box grown by <paramref name="eps"/>.
    /// An axis where both ends lie past one face rejects; an axis whose slab
    /// the segment crosses gives entry and exit times through a refined
    /// rcpps of its delta (a delta under FLT_MIN is OR-ed with 2^-23 first);
    /// the rest give no bound. Exit is capped at 1, entry floored at 0, and
    /// it hits unless exit &lt; entry.
    /// </summary>
    public static bool SegmentHitsBox(Vector3 bmin, Vector3 bmax, Vector3 s, Vector3 d, float eps)
    {
        Span<float> lo = [(bmin.X - s.X) - eps, (bmin.Y - s.Y) - eps, (bmin.Z - s.Z) - eps];
        Span<float> hi = [(bmax.X - s.X) + eps, (bmax.Y - s.Y) + eps, (bmax.Z - s.Z) + eps];
        Span<float> dd = [d.X, d.Y, d.Z];
        for (var a = 0; a < 3; a++)
        {
            if ((dd[a] < lo[a] && 0f < lo[a]) || (hi[a] < dd[a] && hi[a] < 0f))
                return false;
        }
        Span<float> enter = stackalloc float[3];
        Span<float> exit = stackalloc float[3];
        for (var a = 0; a < 3; a++)
        {
            var straddle = ((dd[a] < lo[a]) ^ (0f < lo[a])) || ((hi[a] < dd[a]) ^ (hi[a] < 0f));
            var x = dd[a];
            if (MathF.Abs(x) < 1.17549435e-38f)
                x = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(x) | 0x34000000);
            var r = Rcp(x);
            r = (r + r) - r * r * x;
            var tLo = straddle ? r * lo[a] : -float.MaxValue;
            var tHi = straddle ? r * hi[a] : float.MaxValue;
            exit[a] = tLo > tHi ? tLo : tHi;
            enter[a] = tLo < tHi ? tLo : tHi;
        }
        var tExit = exit[0] < exit[1] ? exit[0] : exit[1];
        tExit = tExit < exit[2] ? tExit : exit[2];
        tExit = tExit < 1f ? tExit : 1f;
        var tEnter = enter[0] > enter[1] ? enter[0] : enter[1];
        tEnter = tEnter > enter[2] ? tEnter : enter[2];
        tEnter = tEnter > 0f ? tEnter : 0f;
        return !(tExit < tEnter);
    }

    static float Rcp(float x) => Sse.IsSupported
        ? Sse.ReciprocalScalar(Vector128.CreateScalarUnsafe(x)).ToScalar() : 1f / x;
}
