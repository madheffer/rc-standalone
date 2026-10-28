namespace Source2.Compiler.SteamAudio;

/// <summary>
/// The pathing bake, as phonon 0923 (Steam Audio 4.9) runs iplPathBakerBake (the validation layer 180acb910, then
/// 180ab5650): the API sets asymmetricVisRange, down (0, -1, 0), visRangeRealTime = visRange and no pruning, and
/// BakedPathData's constructor (180a978e0) builds
/// <list type="bullet">
/// <item>the visibility graph (180a93db0, one job per probe, 180a93fe0): for each i and each j &lt; i, unless the
/// probes are too far apart (180a93c70: the offset i - j less its component along down, longer than visRange) or
/// not visible (180a939f0: with no samples or radius &lt;= 0 the one ray isOccluded(p[i], p[j]), 180a71c40), the edge
/// (j, |p[i] - p[j]|); the last job then appends each edge's reverse (i, cost) to row j, rows in order;</item>
/// <item>per probe, Dijkstra (180a954f0, MSVC's push_heap / pop_heap on a min-heap of (index, cost)): an edge
/// relaxes v when cost[u] + c &lt; cost[v] and the straight distance |p[start] - p[v]| is at most pathRange (4.9's
/// test; 4.8.1 tested the path cost); each path the parents back to the start, reversed (180a986a0);</item>
/// <item>the entries with j &gt; i cleared, every entry std::sorted (invalid, then direct, then node lists
/// lexicographically: a total order on what the entries hold), one SoundPath (180a96a30) per run of equal entries,
/// and each valid entry's ref (start, end) set to its path's index;</item>
/// <item>serialized with the visibility lists' j &lt; i edges, every unique path, and (i n + j, ref) for each ref
/// whose path is valid (direct, or first and last probe set).</item>
/// </list>
/// No FMA anywhere (MSVC, /fp:precise): lengths are sqrt(((x x + 0) + y y) + z z).
/// </summary>
public static class PathBaker
{
    private readonly record struct Edge(int Index, float Cost);

    private sealed class ProbePath
    {
        public bool Valid;
        public int Start = -1, End = -1;
        public List<int> Nodes = [];
    }

    public static BakedPathing Bake(TriangleScene scene, IReadOnlyList<Sphere> probes, int numSamples, float radius, float threshold,
                                    float visRange, float pathRange)
    {
        // 180a939f0 samples spheres only with more than one sample and a positive radius.
        if (numSamples > 1 && radius > 0f)
            throw new NotSupportedException("sampled probe visibility (numSamples > 1 with a radius) is not ported");
        var n = probes.Count;
        var adjacent = new List<Edge>[n];
        for (var i = 0; i < n; i++)
            adjacent[i] = [];
        for (var i = 0; i < n; i++)
            for (var j = 0; j < i; j++)
            {
                if (TooFar(probes[i], probes[j], visRange) || Occluded(scene, probes[i], probes[j]))
                    continue;
                adjacent[i].Add(new Edge(j, Length(probes[i].X - probes[j].X, probes[i].Y - probes[j].Y, probes[i].Z - probes[j].Z)));
            }
        for (var i = 0; i < n; i++)
        {
            var count = adjacent[i].Count;
            for (var k = 0; k < count; k++)
                adjacent[adjacent[i][k].Index].Add(new Edge(i, adjacent[i][k].Cost));
        }

        var paths = new ProbePath[n * n];
        var parents = new int[n];
        var costs = new float[n];
        for (var start = 0; start < n; start++)
        {
            ShortestPaths(probes, adjacent, start, pathRange, parents, costs);
            for (var i = 0; i < n; i++)
            {
                var path = paths[start * n + i] = new ProbePath { Start = start, End = i };
                if (parents[i] < 0)
                    continue;
                path.Valid = true;
                for (var p = parents[i]; p >= 0 && p != start; p = parents[p])
                    path.Nodes.Add(p);
                path.Nodes.Reverse();
            }
        }
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
                paths[i * n + j] = new ProbePath();

        var sorted = paths.ToList();
        sorted.Sort(Compare);
        var result = new BakedPathing();
        var refs = new int[n * n];
        for (var k = 0; k < sorted.Count; k++)
        {
            if (k == 0 || !Same(sorted[k], sorted[k - 1]))
                result.UniquePaths.Add(SoundPathOf(sorted[k], probes));
            var last = result.UniquePaths[^1];
            if (IsValid(last))
                refs[sorted[k].Start * n + sorted[k].End] = result.UniquePaths.Count - 1;
        }
        for (var i = 0; i < n; i++)
            result.VisGraph.Add([.. adjacent[i].Where(e => e.Index < i).Select(e => e.Index)]);
        var indices = new List<int>();
        var refList = new List<int>();
        for (var k = 0; k < n * n; k++)
            if (IsValid(result.UniquePaths[refs[k]]))
            {
                indices.Add(k);
                refList.Add(refs[k]);
            }
        result.PathIndices = [.. indices];
        result.Paths = [.. refList];
        return result;
    }

    private static bool IsValid(SoundPath p) => p.Direct || (p.FirstProbe >= 0 && p.LastProbe >= 0);

    // The sort's predicate (180a9ae80): an invalid entry before a valid one, else the node lists lexicographically.
    private static int Compare(ProbePath a, ProbePath b)
    {
        if (Less(a, b))
            return -1;
        return Less(b, a) ? 1 : 0;
    }

    private static bool Less(ProbePath a, ProbePath b)
    {
        if (!a.Valid && b.Valid)
            return true;
        for (var i = 0; i < Math.Min(a.Nodes.Count, b.Nodes.Count); i++)
            if (a.Nodes[i] != b.Nodes[i])
                return a.Nodes[i] < b.Nodes[i];
        return a.Nodes.Count < b.Nodes.Count;
    }

    private static bool Same(ProbePath a, ProbePath b) => a.Valid == b.Valid && a.Nodes.SequenceEqual(b.Nodes);

    /// <summary>180a93c70 with asymmetricVisRange and down (0, -1, 0): the offset i - j less its part along down, longer than the range.</summary>
    private static bool TooFar(Sphere i, Sphere j, float visRange)
    {
        float dx = i.X - j.X, dy = i.Y - j.Y, dz = i.Z - j.Z;
        const float downX = 0f, downY = -1f, downZ = 0f;
        var dot = ((dx * downX) + 0.0f + (dy * downY)) + (dz * downZ);
        dx -= downX * dot;
        dy -= downY * dot;
        dz -= downZ * dot;
        return Length(dx, dy, dz) > visRange;
    }

    /// <summary>
    /// 180a71c40: the ray from <paramref name="from"/> towards <paramref name="to"/> (the offset times 1 / length when
    /// the length is above 1e-5, else zero) through rtcOccluded1 in (0, length].
    /// </summary>
    private static bool Occluded(TriangleScene scene, Sphere from, Sphere to)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y, dz = to.Z - from.Z;
        var length = Length(dx, dy, dz);
        float ux = 0f, uy = 0f, uz = 0f;
        if (1e-05f < length)
        {
            var r = 1.0f / length;
            (ux, uy, uz) = (r * dx, dy * r, dz * r);
        }
        else
            throw new NotSupportedException("two probes at one point (kZero's direction is not read)");
        return scene.Occluded1(from.X, from.Y, from.Z, ux, uy, uz, 0.0f, length);
    }

    private static float Length(float x, float y, float z)
    {
        var s = ((x * x) + 0.0f + (y * y)) + (z * z);
        if (s < 0f)
            throw new NotSupportedException("a negative squared length (the CRT sqrtf error path)");
        return MathF.Sqrt(s);
    }

    /// <summary>180a954f0: Dijkstra from <paramref name="start"/> on MSVC's binary heap (std::priority_queue, less = cost &gt;).</summary>
    private static void ShortestPaths(IReadOnlyList<Sphere> probes, List<Edge>[] adjacent, int start, float pathRange, int[] parents, float[] costs)
    {
        Array.Fill(parents, -1);
        Array.Fill(costs, float.PositiveInfinity);
        costs[start] = 0.0f;
        var heap = new List<(int Index, float Cost)>();
        Push(heap, (start, costs[start]));
        var s = probes[start];
        while (heap.Count > 0)
        {
            var u = heap[0].Index;
            Pop(heap);
            foreach (var e in adjacent[u])
            {
                var v = e.Index;
                var newCost = costs[u] + e.Cost;
                if (!(newCost < costs[v]))
                    continue;
                var p = probes[v];
                if (!(Length(s.X - p.X, s.Y - p.Y, s.Z - p.Z) <= pathRange))
                    continue;
                costs[v] = newCost;
                parents[v] = u;
                Push(heap, (v, newCost));
            }
        }
    }

    // std::push_heap: the new last element sifted up while its parent's cost is above its own.
    private static void Push(List<(int Index, float Cost)> heap, (int Index, float Cost) value)
    {
        heap.Add(value);
        var hole = heap.Count - 1;
        while (hole > 0)
        {
            var parent = (hole - 1) >> 1;
            if (heap[parent].Cost <= value.Cost)
                break;
            heap[hole] = heap[parent];
            hole = parent;
        }
        heap[hole] = value;
    }

    // std::pop_heap: the last element held, the hole walked down from the top to the smaller child (the right one
    // unless its cost is above the left's), a lone last child taken, then the held element sifted up.
    private static void Pop(List<(int Index, float Cost)> heap)
    {
        var count = heap.Count;
        if (count >= 2)
        {
            var value = heap[count - 1];
            heap[count - 1] = heap[0];
            var bottom = count - 1;
            var hole = 0;
            var maxNonLeaf = (bottom - 1) >> 1;
            while (hole < maxNonLeaf)
            {
                var child = 2 * hole + 2;
                if (heap[child].Cost > heap[child - 1].Cost)
                    child--;
                heap[hole] = heap[child];
                hole = child;
            }
            if (hole == maxNonLeaf && bottom % 2 == 0)
            {
                heap[hole] = heap[bottom - 1];
                hole = bottom - 1;
            }
            while (hole > 0)
            {
                var parent = (hole - 1) >> 1;
                if (heap[parent].Cost <= value.Cost)
                    break;
                heap[hole] = heap[parent];
                hole = parent;
            }
            heap[hole] = value;
        }
        heap.RemoveAt(count - 1);
    }

    /// <summary>
    /// 180a96a30: an invalid entry gives the default (probes -1, not direct); an empty valid one is direct; otherwise
    /// the first and last nodes (as shorts), the second and next to last with two or more, the summed segment
    /// lengths, and the summed turning angles (with three or more nodes).
    /// </summary>
    private static SoundPath SoundPathOf(ProbePath path, IReadOnlyList<Sphere> probes)
    {
        if (!path.Valid)
            return new SoundPath(-1, -1, -1, -1, false, 0f, 0f);
        if (path.Nodes.Count == 0)
            return new SoundPath(-1, -1, -1, -1, true, 0f, 0f);
        var nodes = path.Nodes;
        short after = -1, before = -1;
        if (nodes.Count > 1)
            (after, before) = ((short)nodes[1], (short)nodes[^2]);
        var distance = 0.0f;
        for (var i = 1; i < nodes.Count; i++)
        {
            Sphere a = probes[nodes[i - 1]], b = probes[nodes[i]];
            distance = Length(b.X - a.X, b.Y - a.Y, b.Z - a.Z) + distance;
        }
        // Each turn: Vector3::angleBetween(unitVector(cur - prev), unitVector(next - cur)) as atan2f(|perpendicular|,
        // |parallel|), the parallel part dot(prev, next) prev; a negative angle (none here) gains 2 pi.
        var deviation = 0.0f;
        for (var i = 2; i < nodes.Count; i++)
        {
            Sphere a = probes[nodes[i - 2]], b = probes[nodes[i - 1]], c = probes[nodes[i]];
            var (px, py, pz) = Unit(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
            var (nx, ny, nz) = Unit(c.X - b.X, c.Y - b.Y, c.Z - b.Z);
            var dot = ((px * nx) + 0.0f + (py * ny)) + (pz * nz);
            float ax = px * dot, ay = py * dot, az = pz * dot;
            var parallel = Length(ax, ay, az);
            var perpendicular = Length(nx - ax, ny - ay, nz - az);
            var angle = CrtMath.Atan2(perpendicular, parallel);
            if (0.0f > angle)
                angle += 6.2831855f;
            deviation = angle + deviation;
        }
        return new SoundPath((short)nodes[0], (short)nodes[^1], after, before, false, distance, deviation);
    }

    /// <summary>Vector3::unitVector: times 1 / length above 1e-5 (kZero below is refused).</summary>
    private static (float, float, float) Unit(float x, float y, float z)
    {
        var length = Length(x, y, z);
        if (!(1e-05f < length))
            throw new NotSupportedException("a zero-length segment (kZero is not read)");
        var r = 1.0f / length;
        return (x * r, y * r, r * z);
    }
}
