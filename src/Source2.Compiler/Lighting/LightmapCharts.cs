using System.Buffers.Binary;
using System.Numerics;
using Source2.Compiler.Maps;

namespace Source2.Compiler.Lighting;

/// <summary>
/// resourcecompiler's lightmap charting of one mesh, StaticLighting_ChartMesh
/// (0923 18022d920) with its triangle records (18022c740), the join test
/// (18022d6e0) and the fold test (18022d1e0) on the mesh-uv path
/// (UnwrapUsingUvsFromMesh, CS2's default). docs/BAKED.md "Lightmap charts".
///
/// <para>Charts grow from each unvisited triangle in order: the edges to
/// its unvisited neighbours go into a max-heap keyed by the dot product of
/// the two triangles' normals, and a popped neighbour joins when its
/// resolution scale is within the ratio window of the chart's, both uv areas
/// are non-zero, and it does not fold over a neighbour already in the chart.
/// Each chart then splits its vertices where their chart uvs differ, and its
/// uvs become (uv - chart min) * scale * sqrt(world area / uv area) * the
/// chart's scale. The output lists the triangles chart by chart.</para>
/// </summary>
public static class LightmapCharts
{
    /// <summary>A chart mesh going in: what StaticLighting_PrepareChartMesh hands the core.</summary>
    public sealed class Input
    {
        public required byte[] Vertices { get; init; }
        public required int Stride { get; init; }
        public required int PositionOffset { get; init; }
        public required int TexcoordOffset { get; init; }
        public required int[] Indices { get; init; }

        /// <summary>Per corner, the neighbouring triangle's corner across the edge it starts, or -1.</summary>
        public required int[] CornerNeighbours { get; init; }

        /// <summary>Per vertex, the source vertex it stands for (the weld's remap; identity without one).</summary>
        public required int[] SourceVertex { get; init; }

        /// <summary>Per triangle, its resolution scale (empty: 1 each).</summary>
        public float[] TriangleScales { get; init; } = [];

        /// <summary>The uv aspect (xmm1): above 1 it scales u, below 1 divides v.</summary>
        public float Aspect { get; init; } = 1f;

        /// <summary>The uv scale (xmm2) the final chart uvs take.</summary>
        public float Scale { get; init; } = 1f;

        /// <summary>The scale ratio window's bound (xmm3; 1.5: a triangle within 2/3 to 3/2 of its chart's).</summary>
        public float Ratio { get; init; } = 1.5f;

        /// <summary>The flag byte: the mesh's own uvs, overlap tested against neighbours only.</summary>
        public bool MeshUvs { get; init; } = true;
    }

    /// <summary>A chart mesh coming out.</summary>
    public sealed class Result
    {
        public required byte[] Vertices { get; init; }
        public required int VertexCount { get; init; }
        public required int[] Indices { get; init; }
        public required int[] TriangleCharts { get; init; }
        public required float[] ChartUvs { get; init; }
        public required int[] SourceVertex { get; init; }
        public required int ChartCount { get; init; }

        /// <summary>The charts' uv areas summed (+0x68) and world areas (+0x6c).</summary>
        public required float UvArea { get; init; }
        public required float WorldArea { get; init; }
    }

    private sealed class Triangle
    {
        public readonly Vector3[] P = new Vector3[3];
        public Vector3 Normal;
        public readonly Vector2[] Uv = new Vector2[3];
        public readonly Vector2[] Placed = new Vector2[3];
        public readonly int[] Corner = new int[3];
        public int Index;
        public readonly int[] Neighbour = new int[3];
        public float Scale;
        public int Projection;
        public float UvArea, WorldArea;
        public readonly bool[] Open = [true, true, true];
        public Chart? Chart;
    }

    private sealed class Chart(bool meshUvs)
    {
        public readonly List<Triangle> Triangles = [];
        public readonly bool MeshUvs = meshUvs;
        public float FirstScale, Scale, UvArea, WorldArea;
    }

    private struct Entry(int corner, int from, float key)
    {
        public int Corner = corner, From = from;
        public float Key = key;
    }

    public static Result Run(Input input)
    {
        if (!input.MeshUvs)
            throw new NotSupportedException("lightmap charts without the mesh's uvs (the box projection, 18022c740 and 18022cef0) are not ported");
        // The ratio window (DAT_183badef0 / DAT_183badef4): the bound and its inverse, ordered.
        var inverse = 1f / input.Ratio;
        var hi = input.Ratio <= inverse ? inverse : input.Ratio;
        var lo = inverse <= input.Ratio ? inverse : input.Ratio;

        // The uv aspect: u scaled above 1, v divided below 1.
        float su = 1f, sv = 1f;
        var aspect = input.Aspect;
        if (aspect > 1f)
            su = aspect;
        else if (aspect < 1f && aspect >= 0f)
            sv = 1f / aspect;

        var triangleCount = input.Indices.Length / 3;
        var triangles = new Triangle[triangleCount];
        for (var t = 0; t < triangleCount; t++)
        {
            var c = new[] { input.Indices[t * 3], input.Indices[t * 3 + 1], input.Indices[t * 3 + 2] };
            var p = new Vector3[3];
            var uv = new Vector2[3];
            for (var k = 0; k < 3; k++)
            {
                p[k] = ReadVector3(input.Vertices, c[k] * input.Stride + input.PositionOffset);
                var raw = ReadVector2(input.Vertices, c[k] * input.Stride + input.TexcoordOffset);
                uv[k] = new Vector2(su * raw.X, sv * raw.Y);
            }
            var scale = input.TriangleScales.Length == 0 ? 1f : input.TriangleScales[t];
            triangles[t] = Record(p, uv, c, input.CornerNeighbours.AsSpan(t * 3, 3), t, scale);
        }

        var visited = new bool[triangleCount];
        var charts = new List<Chart>();
        var vertices = new List<byte>();
        var chartUvs = new List<Vector2>();
        var source = new List<int>();
        var vertexCount = 0;
        var heap = new List<Entry>();
        for (var seed = 0; seed < triangleCount; seed++)
        {
            if (visited[seed])
                continue;
            Chart? chart = null;
            heap.Clear();
            Push(heap, new Entry(seed * 3, -1, 0f));
            var split = new Dictionary<(byte[] Bytes, int U, int V), int>(SplitKeyComparer.Instance);
            while (heap.Count > 0)
            {
                var top = heap[0];
                if (heap.Count > 1)
                    heap[0] = heap[^1];
                heap.RemoveAt(heap.Count - 1);
                SiftDown(heap);
                var t = triangles[top.Corner / 3];
                if (visited[t.Index])
                    continue;
                if (chart is null)
                {
                    chart = new Chart(input.MeshUvs);
                    Join(chart, t);
                    chart.FirstScale = t.Scale;
                    chart.Scale = t.Scale;
                    chart.UvArea = t.UvArea;
                    chart.WorldArea = t.WorldArea;
                    charts.Add(chart);
                    t.Placed[0] = t.Uv[0];
                    t.Placed[1] = t.Uv[1];
                    t.Placed[2] = t.Uv[2];
                }
                else
                {
                    var from = triangles[top.From / 3];
                    Place(t);
                    if (!TryJoin(chart, t, triangles, lo, hi))
                        continue;
                    chart.UvArea = t.UvArea + chart.UvArea;
                    chart.WorldArea = t.WorldArea + chart.WorldArea;
                    var s = from.Scale;
                    if (s <= chart.Scale)
                        s = chart.Scale;
                    chart.Scale = s;
                }
                visited[t.Index] = true;
                for (var k = 0; k < 3; k++)
                {
                    var n = input.CornerNeighbours[t.Index * 3 + k];
                    if (n == -1 || visited[n / 3])
                        continue;
                    var o = triangles[n / 3];
                    var key = o.Normal.Z * t.Normal.Z + t.Normal.Y * o.Normal.Y + o.Normal.X * t.Normal.X;
                    Push(heap, new Entry(n, t.Index * 3 + k, key));
                }
                // Split the triangle's corners by their chart uv, within this chart.
                for (var k = 0; k < 3; k++)
                {
                    var bytes = input.Vertices.AsSpan(t.Corner[k] * input.Stride, input.Stride).ToArray();
                    var key = (bytes, BitConverter.SingleToInt32Bits(t.Placed[k].X), BitConverter.SingleToInt32Bits(t.Placed[k].Y));
                    if (float.IsNaN(t.Placed[k].X) || float.IsNaN(t.Placed[k].Y) || !split.TryGetValue(key, out var id))
                    {
                        id = vertexCount++;
                        split[key] = id;
                        vertices.AddRange(bytes);
                        chartUvs.Add(t.Placed[k]);
                        source.Add(input.SourceVertex[t.Corner[k]]);
                    }
                    t.Corner[k] = id;
                }
            }
        }

        // Each chart's uvs: from its min, at world texel density.
        var uvs = chartUvs.ToArray();
        var done = new bool[vertexCount];
        foreach (var chart in charts)
        {
            float minU = float.MaxValue, minV = float.MaxValue;
            foreach (var t in chart.Triangles)
                for (var k = 0; k < 3; k++)
                {
                    if (t.Placed[k].X <= minU)
                        minU = t.Placed[k].X;
                    if (t.Placed[k].Y <= minV)
                        minV = t.Placed[k].Y;
                }
            var density = chart.UvArea <= 5.9604645e-08f ? 0f : MathF.Sqrt(chart.WorldArea) / MathF.Sqrt(chart.UvArea) * chart.Scale;
            foreach (var t in chart.Triangles)
                for (var k = 0; k < 3; k++)
                {
                    var id = t.Corner[k];
                    if (done[id])
                        continue;
                    uvs[id] = new Vector2((t.Placed[k].X - minU) * input.Scale * density, (t.Placed[k].Y - minV) * input.Scale * density);
                    done[id] = true;
                }
        }

        var indices = new int[triangleCount * 3];
        var triangleCharts = new int[triangleCount];
        float uvArea = 0f, worldArea = 0f;
        var at = 0;
        for (var c = 0; c < charts.Count; c++)
        {
            foreach (var t in charts[c].Triangles)
            {
                triangleCharts[at / 3] = c;
                indices[at++] = t.Corner[0];
                indices[at++] = t.Corner[1];
                indices[at++] = t.Corner[2];
            }
            worldArea = charts[c].WorldArea + worldArea;
            uvArea = charts[c].UvArea + uvArea;
        }
        var flat = new float[uvs.Length * 2];
        for (var i = 0; i < uvs.Length; i++)
            (flat[i * 2], flat[i * 2 + 1]) = (uvs[i].X, uvs[i].Y);
        return new Result
        {
            Vertices = [.. vertices], VertexCount = vertexCount, Indices = indices, TriangleCharts = triangleCharts,
            ChartUvs = flat, SourceVertex = [.. source], ChartCount = charts.Count, UvArea = uvArea, WorldArea = worldArea,
        };
    }

    // 18022c740 on the mesh-uv path: the unit normal, the uvs as they are
    // (projection 6), the areas (both zero when the uv area is under 2^-24).
    private static Triangle Record(Vector3[] p, Vector2[] uv, int[] corners, ReadOnlySpan<int> neighbours, int index, float scale)
    {
        var t = new Triangle { Index = index, Scale = scale, Projection = 6 };
        p.CopyTo(t.P, 0);
        uv.CopyTo(t.Uv, 0);
        corners.CopyTo(t.Corner, 0);
        float e1x = p[1].X - p[0].X, e1y = p[1].Y - p[0].Y, e1z = p[1].Z - p[0].Z;
        float e2x = p[2].X - p[1].X, e2y = p[2].Y - p[1].Y, e2z = p[2].Z - p[1].Z;
        var nx = e2z * e1y - e2y * e1z;
        var ny = e2x * e1z - e2z * e1x;
        var nz = e2y * e1x - e2x * e1y;
        var length = MathF.Sqrt(ny * ny + nz * nz + nx * nx);
        if (length < 1e-17f || length > 1e17f)
            t.Normal = length != 0f ? VectorNormalizeSlow.Normalise(new Vector3(nx, ny, nz)) : Vector3.Zero;
        else
        {
            var inv = 1f / length;
            t.Normal = new Vector3(inv * nx, ny * inv, nz * inv);
        }
        for (var k = 0; k < 3; k++)
            t.Neighbour[k] = neighbours[k] == -1 ? -1 : neighbours[k] / 3;
        t.UvArea = MathF.Abs((uv[2].Y - uv[0].Y) * (uv[1].X - uv[0].X) - (uv[1].Y - uv[0].Y) * (uv[2].X - uv[0].X)) * 0.5f;
        float f1x = p[1].X - p[0].X, f1y = p[1].Y - p[0].Y, f1z = p[1].Z - p[0].Z;
        float f2x = p[2].X - p[0].X, f2y = p[2].Y - p[0].Y, f2z = p[2].Z - p[0].Z;
        var cx = f1y * f2z - f1z * f2y;
        var cy = f1z * f2x - f2z * f1x;
        var cz = f2y * f1x - f1y * f2x;
        t.WorldArea = MathF.Sqrt(cz * cz + cy * cy + cx * cx) * 0.5f;
        if (t.UvArea < 5.9604645e-08f)
            (t.UvArea, t.WorldArea) = (0f, 0f);
        return t;
    }

    // 18022cef0 on the mesh-uv path: the triangle keeps its own uvs.
    private static void Place(Triangle t)
    {
        t.Placed[0] = t.Uv[0];
        t.Placed[1] = t.Uv[1];
        t.Placed[2] = t.Uv[2];
    }

    private static void Join(Chart chart, Triangle t)
    {
        t.Chart = chart;
        chart.Triangles.Add(t);
    }

    // 18022d6e0: the scale ratio window, non-zero uv areas, and no fold over a
    // neighbour already in the chart; the shared edges close.
    private static bool TryJoin(Chart chart, Triangle t, Triangle[] all, float lo, float hi)
    {
        var ratio = t.Scale / chart.FirstScale;
        if (!(ratio <= hi && lo <= ratio))
            return false;
        if (chart.Triangles[0].UvArea == 0f || t.UvArea == 0f)
            return false;
        Span<int> touching = stackalloc int[3];
        var count = 0;
        for (var k = 0; k < 3; k++)
        {
            var n = t.Neighbour[k];
            if (n == -1)
                continue;
            var o = all[n];
            if (o.Chart != chart || !(o.Open[0] || o.Open[1] || o.Open[2]))
                continue;
            if (Folds(o, t))
                return false;
            touching[count++] = n;
        }
        for (var i = 0; i < count; i++)
        {
            var n = touching[i];
            var seen = false;
            for (var j = 0; j < i; j++)
                seen |= touching[j] == n;
            if (seen)
                continue;
            var o = all[n];
            for (var k = 0; k < 3; k++)
                if (o.Projection == 6 && o.Neighbour[k] == t.Index)
                    o.Open[k] = false;
            for (var k = 0; k < 3; k++)
                if (t.Projection == 6 && t.Neighbour[k] == n)
                    t.Open[k] = false;
        }
        Join(chart, t);
        return true;
    }

    // 18022d1e0 on the mesh-uv path: across their shared edge, the two third
    // corners on the same side means the new triangle folds over.
    private static bool Folds(Triangle o, Triangle t)
    {
        var edge = o.Neighbour[0] != t.Index ? -1 : 0;
        var back = t.Neighbour[0] != o.Index ? -1 : 0;
        if (t.Neighbour[1] == o.Index)
            back = 1;
        if (o.Neighbour[1] == t.Index)
            edge = 1;
        if (t.Neighbour[2] == o.Index)
            back = 2;
        if (o.Neighbour[2] == t.Index)
            edge = 2;
        else if (edge == -1)
            return false;
        if (back == -1)
            return false;
        var a = o.Uv[edge];
        var d = o.Uv[(edge + 1) % 3] - a;
        var oc = o.Uv[(edge + 2) % 3];
        var tc = t.Uv[(back + 2) % 3];
        var s1 = (oc.Y - a.Y) * d.X - (oc.X - a.X) * d.Y < 0f ? -1f : 1f;
        var s2 = (tc.Y - a.Y) * d.X - (tc.X - a.X) * d.Y < 0f ? -1f : 1f;
        return s1 == s2;
    }

    // The heap: an entry goes in at the end and rises past every parent whose
    // key is not above its own (equal keys rise).
    private static void Push(List<Entry> heap, Entry e)
    {
        heap.Add(e);
        var i = heap.Count - 1;
        while (i != 0)
        {
            var parent = (i + 1) / 2 - 1;
            if (heap[i].Key < heap[parent].Key)
                break;
            (heap[i], heap[parent]) = (heap[parent], heap[i]);
            i = parent;
        }
    }

    // 18022f610: the root sinks to the larger child (left first, a child
    // strictly above).
    private static void SiftDown(List<Entry> heap)
    {
        var n = heap.Count;
        if (n / 2 < 1)
            return;
        var i = 0;
        while (true)
        {
            var best = i;
            var left = i * 2 + 1;
            if (left < n && heap[i].Key < heap[left].Key)
                best = left;
            if (i * 2 + 2 < n && heap[best].Key < heap[i * 2 + 2].Key)
                best = i * 2 + 2;
            if (best == i)
                return;
            (heap[i], heap[best]) = (heap[best], heap[i]);
            i = best;
            if (n / 2 <= i)
                return;
        }
    }

    private sealed class SplitKeyComparer : IEqualityComparer<(byte[] Bytes, int U, int V)>
    {
        public static readonly SplitKeyComparer Instance = new();

        public bool Equals((byte[] Bytes, int U, int V) a, (byte[] Bytes, int U, int V) b)
            => a.U == b.U && a.V == b.V && a.Bytes.AsSpan().SequenceEqual(b.Bytes);

        public int GetHashCode((byte[] Bytes, int U, int V) k)
        {
            var h = new HashCode();
            h.AddBytes(k.Bytes);
            h.Add(k.U);
            h.Add(k.V);
            return h.ToHashCode();
        }
    }

    private static Vector3 ReadVector3(byte[] b, int at)
        => new(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 4)),
               BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 8)));

    private static Vector2 ReadVector2(byte[] b, int at)
        => new(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 4)));
}
