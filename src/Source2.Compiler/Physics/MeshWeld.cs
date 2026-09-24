namespace Source2.Compiler.Physics;

/// <summary>
/// The CMesh weld the map builder runs at 1/32 on each per-material piece of
/// a map mesh before its physics and render paths see it (FUN_1812d80c0).
///
/// <para>Every float of a vertex has a tolerance: 1/32 by default, 1/2048
/// for texcoords, 0.001 for a tangent's direction, exact for lightmap
/// coordinates and integer streams, and anything for a stream flagged as
/// ignored. Vertices are visited in order. Each unassigned one joins the
/// first earlier vertex's cluster whose first vertex is within every
/// tolerance of it (a zero tolerance asks for equality), or starts a new
/// cluster (FUN_1812e5880, FUN_1812e2530). A cluster keeps its first
/// vertex's data. Triangles that lose a corner are dropped and the
/// vertices renumbered by first use (FUN_1812dd2f0).</para>
///
/// <para>The earlier vertices come from tier0's CVertexKDTree, a box query
/// on position (<see cref="KdTree"/>), and are tried in the order it returns
/// them: when two clusters would both take a vertex, that order decides.</para>
/// </summary>
internal static class MeshWeld
{
    /// <summary>A vertex stream: its name, first float, float count, whether welds ignore it, and its type.</summary>
    public readonly record struct Stream(string Name, int First, int Count, bool Ignored, int Type);

    /// <summary>The welded vertices and triangles.</summary>
    public static (float[] Vertices, int[] Indices) Weld(float[] vertices, int stride, int[] indices, IReadOnlyList<Stream> streams, float tolerance)
        => Weld(vertices, stride, indices, streams, tolerance, true);

    /// <summary>
    /// With <paramref name="renumberByUse"/> false, the clusters keep the order
    /// they formed in and every triangle is kept, degenerate or not (the
    /// caller drops them).
    /// </summary>
    public static (float[] Vertices, int[] Indices) Weld(float[] vertices, int stride, int[] indices, IReadOnlyList<Stream> streams, float tolerance, bool renumberByUse)
        => Weld(vertices, stride, indices, streams, tolerance, renumberByUse, null);

    /// <summary>As above, also listing the input triangles kept, in order.</summary>
    public static (float[] Vertices, int[] Indices) Weld(float[] vertices, int stride, int[] indices, IReadOnlyList<Stream> streams, float tolerance, bool renumberByUse, List<int>? keptTriangles)
    {
        var tol = Tolerances(stride, streams, tolerance);
        var count = vertices.Length / stride;
        var remap = new int[count];
        Array.Fill(remap, -1);
        var reps = new List<int>();
        // FUN_1812e5880: more than 100 vertices at exactly the origin, in a
        // mesh with more than position, are clustered first on the three
        // floats after the position.
        var zero = new List<int>();
        for (var i = 0; i < count; i++)
        {
            if (vertices[i * stride] == 0f && vertices[i * stride + 1] == 0f && vertices[i * stride + 2] == 0f)
                zero.Add(i);
        }
        if (zero.Count > 100 && stride > 5)
            Cluster(vertices, stride, tol, 3, zero, remap, reps);
        Cluster(vertices, stride, tol, 0, Enumerable.Range(0, count).ToList(), remap, reps);
        if (!renumberByUse)
        {
            // Clusters in the order they formed, each at its first vertex.
            var clusters = new float[reps.Count * stride];
            for (var r = 0; r < reps.Count; r++)
                Array.Copy(vertices, reps[r] * stride, clusters, r * stride, stride);
            return (clusters, [.. indices.Select(i => remap[i])]);
        }
        var welded = new int[indices.Length];
        for (var i = 0; i < indices.Length; i++)
            welded[i] = remap[indices[i]];
        // FUN_1812dd2f0
        var renumber = new int[reps.Count];
        Array.Fill(renumber, -1);
        var used = 0;
        var kept = new List<int>();
        for (var t = 0; t + 2 < welded.Length; t += 3)
        {
            int a = welded[t], b = welded[t + 1], c = welded[t + 2];
            if (a == b || b == c || a == c)
                continue;
            keptTriangles?.Add(t / 3);
            foreach (var v in new[] { a, b, c })
            {
                if (renumber[v] == -1)
                    renumber[v] = used++;
            }
            kept.Add(renumber[a]);
            kept.Add(renumber[b]);
            kept.Add(renumber[c]);
        }
        var output = new float[used * stride];
        for (var r = 0; r < reps.Count; r++)
        {
            if (renumber[r] != -1)
                Array.Copy(vertices, reps[r] * stride, output, renumber[r] * stride, stride);
        }
        return (output, [.. kept]);
    }

    // FUN_1812d80c0
    private static float[] Tolerances(int stride, IReadOnlyList<Stream> streams, float tolerance)
    {
        var tol = new float[stride];
        Array.Fill(tol, tolerance);
        foreach (var s in streams)
        {
            if (s.Name.StartsWith("texcoord", StringComparison.OrdinalIgnoreCase))
                Array.Fill(tol, 1f / 2048f, s.First, s.Count);
        }
        foreach (var s in streams)
        {
            if (s.Name.StartsWith("lightmap", StringComparison.OrdinalIgnoreCase))
                Array.Fill(tol, 0f, s.First, s.Count);
        }
        foreach (var s in streams)
        {
            if (!s.Name.StartsWith("tangent", StringComparison.OrdinalIgnoreCase))
                continue;
            tol[s.First] = tol[s.First + 1] = tol[s.First + 2] = 0.001f;
            if (s.Count == 4)
                tol[s.First + 3] = 0f;
        }
        foreach (var s in streams)
        {
            if (s.Ignored)
                Array.Fill(tol, 1e9f, s.First, s.Count);
        }
        foreach (var s in streams)
        {
            if (s.Type == 0x22)
                Array.Fill(tol, 0f, s.First, s.Count);
        }
        return tol;
    }

    // FUN_1812e2530 over the given vertices, the box query on the three
    // floats at <paramref name="axis"/>.
    private static void Cluster(float[] v, int stride, float[] tol, int axis, List<int> order, int[] remap, List<int> reps)
    {
        var tree = new KdTree(v, stride, axis, order);
        var found = new List<int>();
        foreach (var i in order)
        {
            if (remap[i] != -1)
                continue;
            var pi = i * stride;
            found.Clear();
            tree.FindInBox(found,
                v[pi + axis] - tol[axis], v[pi + axis + 1] - tol[axis + 1], v[pi + axis + 2] - tol[axis + 2],
                v[pi + axis] + tol[axis], v[pi + axis + 1] + tol[axis + 1], v[pi + axis + 2] + tol[axis + 2]);
            foreach (var j in found)
            {
                if (j >= i)
                    continue;
                var r = reps[remap[j]] * stride;
                var same = true;
                for (var k = 0; k < stride && same; k++)
                {
                    var t = tol[k];
                    same = t != 0f ? !(t < MathF.Abs(v[pi + k] - v[r + k])) : v[pi + k] == v[r + k];
                }
                if (same)
                {
                    remap[i] = remap[j];
                    break;
                }
            }
            if (remap[i] == -1)
            {
                remap[i] = reps.Count;
                reps.Add(i);
            }
        }
    }

    /// <summary>
    /// tier0's CVertexKDTree over three floats of each vertex: leaves of at
    /// most 8, an inner node splitting its bounds' longest axis at the
    /// midpoint (BuildNode). A box query walks left before right and returns
    /// a leaf's vertices in the order the partitions left them.
    /// </summary>
    private sealed class KdTree
    {
        private readonly float[] _v;
        private readonly int _stride, _axis;
        private readonly int[] _pts;
        private readonly List<(int A, int B, int Axis, float Split)> _nodes = [];

        public KdTree(float[] v, int stride, int axis, List<int> vertices)
        {
            _v = v;
            _stride = stride;
            _axis = axis;
            _pts = [.. vertices];
            Build(0, _pts.Length);
        }

        private float C(int p, int k) => _v[(_pts[p] * _stride) + _axis + k];

        // BuildNode
        private int Build(int start, int count)
        {
            if (count > 8)
            {
                float minX = C(start, 0), minY = C(start, 1), minZ = C(start, 2);
                float maxX = minX, maxY = minY, maxZ = minZ;
                for (var p = start + 1; p < start + count; p++)
                {
                    float x = C(p, 0), y = C(p, 1), z = C(p, 2);
                    if (x <= minX) minX = x;
                    if (maxX <= x) maxX = x;
                    if (y <= minY) minY = y;
                    if (maxY <= y) maxY = y;
                    if (z <= minZ) minZ = z;
                    if (maxZ <= z) maxZ = z;
                }
                float dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
                int axis;
                if (dx < dy)
                    axis = dy <= dz ? 2 : 1;
                else
                    axis = dz < dx ? 0 : 2;
                float[] lo = [minX, minY, minZ], hi = [maxX, maxY, maxZ];
                var split = (lo[axis] + hi[axis]) * 0.5f;
                var mid = Partition(start, count, axis, split);
                var rest = start - mid + count;
                if (mid - start != 0 && rest != 0)
                {
                    var node = _nodes.Count;
                    _nodes.Add(default);
                    var left = Build(start, mid - start);
                    var right = Build(mid, rest);
                    _nodes[node] = (left, right, axis, split);
                    return node;
                }
            }
            _nodes.Add((start, count, 0xff, 0f));
            return _nodes.Count - 1;
        }

        // FUN_18017ebf0: from the middle up, anything below the split is
        // swapped down; then from there down, anything at or above it is
        // swapped up. Returns the first index at or above the split.
        private int Partition(int start, int count, int axis, float split)
        {
            var w = start + count / 2;
            for (var i = w; i < start + count; i++)
            {
                var c = C(i, axis);
                if (c <= split && split != c)
                {
                    (_pts[w], _pts[i]) = (_pts[i], _pts[w]);
                    w++;
                }
            }
            var j = w - 1;
            for (var i = w - 1; i >= start; i--)
            {
                if (split <= C(i, axis))
                {
                    (_pts[j], _pts[i]) = (_pts[i], _pts[j]);
                    w--;
                    j--;
                }
            }
            return w;
        }

        // FindVertsInBox
        public void FindInBox(List<int> found, float x0, float y0, float z0, float x1, float y1, float z1)
        {
            float[] lo = [x0, y0, z0], hi = [x1, y1, z1];
            Find(found, lo, hi, 0);
        }

        private void Find(List<int> found, float[] lo, float[] hi, int node)
        {
            while (true)
            {
                var (a, b, axis, split) = _nodes[node];
                if (axis == 0xff)
                {
                    for (var p = a; p < a + b; p++)
                    {
                        float x = C(p, 0), y = C(p, 1), z = C(p, 2);
                        if (lo[0] <= x && x <= hi[0] && lo[1] <= y && y <= hi[1] && lo[2] <= z && z <= hi[2])
                            found.Add(_pts[p]);
                    }
                    return;
                }
                if (lo[axis] <= split)
                    Find(found, lo, hi, a);
                if (hi[axis] < split)
                    return;
                node = b;
            }
        }
    }
}
