using System.Buffers.Binary;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.Lighting;

/// <summary>
/// uber_weld &lt;lightmap_seams.dat&gt; (Run 180050360): a sparse least-squares
/// weld of the lightmap across UV seams, in single precision and vrad3's
/// operation order (docs/VRAD3.md).
/// <list type="number">
/// <item>Constraint rows: per seam record, samples along both edges; each
/// sample far enough apart gives a row of side A's bilinear taps at +4 and
/// side B's at -4, right-hand side 0 (<see cref="BuildRows"/>).</item>
/// <item>The texels the rows name, in first-seen order, each get a data row:
/// itself at weight n (how often the seams name it), or 0.01 when it has no
/// samples, right-hand side its own value times that weight.</item>
/// <item>Channels 0-3, 8 and 13-15 that exist are solved one by one, their
/// masked components as one system, by conjugate gradient on the normal
/// equations from the current values (1e-20, 100 steps).</item>
/// </list>
/// Around the solve the AHD direction (channel 13) goes to world space and
/// back, and channels 0, 14 and 1 to YCoCg and back.
/// </summary>
public static class LightmapWeld
{
    private static readonly float Tolerance = BitConverter.Int32BitsToSingle(0x1e3ce508);

    /// <summary>The rows: flat (texel, weight) entries, each row ending with its right-hand side entry.</summary>
    public sealed class Rows
    {
        public List<(int X, int Y)> Texels { get; } = [];
        public List<float> Weights { get; } = [];
        public List<int> RowEnds { get; } = [];
        public List<(int X, int Y)> Unique { get; } = [];
        public Dictionary<(int X, int Y), int> Index { get; } = [];
    }

    public static void Weld(LightmapImage image, ReadOnlySpan<byte> seams)
    {
        var rows = BuildRows(image, seams);
        var directions = image.Channels[13] is not null && image.Channels[LightmapImage.Ch.Tangent] is not null
            && image.Channels[LightmapImage.Ch.Normal] is not null;
        if (directions)
            TransformDirections(image, toWorld: true);
        foreach (var c in new[] { 0, 14 })
            if (image.Channels[c] is { } colour)
                TransformRgb(colour, ToYCoCg);
        if (image.Channels[1] is { } sh)
            TransformRgb(sh, ToYCoCg);
        foreach (var c in new[] { 0, 1, 2, 3, 8, 13, 14, 15 })
            if (image.Channels[c] is { } channel)
                SolveChannel(image, channel, rows);
        if (image.Channels[1] is { } back)
            TransformRgb(back, FromYCoCg);
        foreach (var c in new[] { 14, 0 })
            if (image.Channels[c] is { } colour)
                TransformRgb(colour, FromYCoCg);
        if (directions)
            TransformDirections(image, toWorld: false);
    }

    /// <summary>
    /// The seam samples (Run, then 18004ed10 per side). Each 32-byte record is
    /// four atlas uv points: edge A from P0 to P1, edge B from P2 to P3, taken to
    /// texels as uv x size - 0.5. n = max(3 x the longer edge, 1), t from 0.5/n
    /// in steps of 1/n while t &lt;= 1 (accumulated). A sample is skipped when
    /// both |dx| &lt; 1/width and |dy| &lt; 1/height.
    /// </summary>
    public static Rows BuildRows(LightmapImage image, ReadOnlySpan<byte> seams)
    {
        var rows = new Rows();
        int w = image.Width, h = image.Height;
        float fw = w, fh = h, invW = 1f / w, invH = 1f / h;
        Span<float> f = stackalloc float[8];
        for (var o = 0; o + 32 <= seams.Length; o += 32)
        {
            for (var i = 0; i < 8; i++)
                f[i] = BinaryPrimitives.ReadSingleLittleEndian(seams[(o + i * 4)..]);
            float p0x = f[0] * fw - 0.5f, p0y = f[1] * fh - 0.5f, p1x = f[2] * fw - 0.5f, p1y = f[3] * fh - 0.5f;
            float p2x = f[4] * fw - 0.5f, p2y = f[5] * fh - 0.5f, p3x = f[6] * fw - 0.5f, p3y = f[7] * fh - 0.5f;
            var la = (p0y - p1y) * (p0y - p1y) + (p0x - p1x) * (p0x - p1x);
            var lb = (p2y - p3y) * (p2y - p3y) + (p2x - p3x) * (p2x - p3x);
            var longer = la > lb ? la : lb;
            var n = MathF.Sqrt(longer) * 3f;
            n = n > 1f ? n : 1f;
            var step = 1f / n;
            for (var t = step * 0.5f; 1f >= t; t += step)
            {
                float ax = (p1x - p0x) * t + p0x, ay = (p1y - p0y) * t + p0y;
                float bx = (p3x - p2x) * t + p2x, by = (p3y - p2y) * t + p2y;
                if (invW > MathF.Abs(ax - bx) && invH > MathF.Abs(ay - by))
                    continue;
                AddTaps(rows, ax, ay, w, h, 4f);
                AddTaps(rows, bx, by, w, h, -4f);
                rows.Weights.Add(0f);
                rows.Texels.Add((-1, -1));
                rows.RowEnds.Add(rows.Weights.Count);
            }
        }
        var counts = new List<int>();
        for (var e = 0; e < rows.Weights.Count; e++)
        {
            if (rows.Weights[e] == 0f)
                continue;
            if (rows.Index.TryGetValue(rows.Texels[e], out var u))
                counts[u]++;
            else
            {
                rows.Index[rows.Texels[e]] = rows.Unique.Count;
                rows.Unique.Add(rows.Texels[e]);
                counts.Add(1);
            }
        }
        for (var u = 0; u < rows.Unique.Count; u++)
        {
            var (x, y) = rows.Unique[u];
            var weight = image.SampleCount(y * w + x) != 0 ? (float)counts[u] : 0.01f;
            for (var k = 0; k < 2; k++)
            {
                rows.Texels.Add((x, y));
                rows.Weights.Add(weight);
            }
            rows.RowEnds.Add(rows.Weights.Count);
        }
        return rows;
    }

    // 18004ed10: the four bilinear taps (clamped to the image) of one side,
    // then within them a nonzero tap absorbs later taps on the same texel and
    // zero taps go, each removal moving the last entry into the hole.
    private static void AddTaps(Rows rows, float x, float y, int w, int h, float weight)
    {
        var ix = (int)MathF.Floor(x);
        var iy = (int)MathF.Floor(y);
        var fy = y - iy;
        var a = (1f - (x - ix)) * weight;
        var b = (x - ix) * weight;
        var start = rows.Weights.Count;
        int Cx(int v) => Math.Min(Math.Max(v, 0), w - 1);
        int Cy(int v) => Math.Min(Math.Max(v, 0), h - 1);
        rows.Weights.AddRange([(1f - fy) * a, b * (1f - fy), a * fy, b * fy]);
        rows.Texels.AddRange([(Cx(ix), Cy(iy)), (Cx(ix + 1), Cy(iy)), (Cx(ix), Cy(iy + 1)), (Cx(ix + 1), Cy(iy + 1))]);
        var weights = rows.Weights;
        var texels = rows.Texels;
        void RemoveFast(int k)
        {
            var last = weights.Count - 1;
            if (k != last)
            {
                weights[k] = weights[last];
                texels[k] = texels[last];
            }
            weights.RemoveAt(last);
            texels.RemoveAt(last);
        }
        for (var i = start; i < weights.Count; i++)
        {
            if (weights[i] != 0f)
            {
                for (var j = i + 1; j < weights.Count; j++)
                    if (texels[i] == texels[j])
                    {
                        weights[i] = weights[j] + weights[i];
                        RemoveFast(j);
                        j--;
                    }
            }
            else
            {
                RemoveFast(i);
                i--;
            }
        }
    }

    private readonly struct TripletLess : ILess<(int Row, int Col, float Value)>
    {
        public bool Less(in (int Row, int Col, float Value) a, in (int Row, int Col, float Value) b) =>
            a.Row != b.Row ? a.Row < b.Row : a.Col < b.Col;
    }

    /// <summary>A CSR matrix as vrad3's (1801f76c0): per row its entries in insertion order, zeros dropped.</summary>
    private sealed class Sparse(int rows)
    {
        public readonly List<(int Col, float Value)>[] Entries =
            Enumerable.Range(0, rows).Select(_ => new List<(int, float)>()).ToArray();

        // 18005f2d0: each row's sum in entry order from 0.
        public void Multiply(float[] v, float[] result)
        {
            for (var r = 0; r < Entries.Length; r++)
            {
                var sum = 0f;
                foreach (var (col, value) in Entries[r])
                    sum += v[col] * value;
                result[r] = sum;
            }
        }

        // 1801f7490: entries of each transposed row in the original row order.
        public Sparse Transpose(int columns)
        {
            var t = new Sparse(columns);
            for (var r = 0; r < Entries.Length; r++)
                foreach (var (col, value) in Entries[r])
                    t.Entries[col].Add((r, value));
            return t;
        }
    }

    // 18004f6d0: one channel's system. Row r component c is matrix row
    // r n + c, texel u component c column u n + c.
    private static void SolveChannel(LightmapImage image, LightmapImage.Channel channel, Rows rows)
    {
        if (image.Width < 1)
            return;
        var comps = Enumerable.Range(0, 4).Where(k => (channel.Mask >> k & 1) != 0).ToArray();
        var n = comps.Length;
        if (n == 0)
            return;
        var w = image.Width;
        var a = new Sparse(rows.RowEnds.Count * n);
        var rhs = new float[rows.RowEnds.Count * n];
        var triplets = new List<(int Row, int Col, float Value)>();
        var start = 0;
        for (var r = 0; r < rows.RowEnds.Count; r++)
        {
            var end = rows.RowEnds[r];
            triplets.Clear();
            for (var e = start; e < end - 1; e++)
            {
                var u = rows.Index[rows.Texels[e]];
                for (var c = 0; c < n; c++)
                    triplets.Add((r * n + c, u * n + c, rows.Weights[e]));
            }
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(triplets);
            MsvcSort.Sort(span, default(TripletLess));
            foreach (var (row, col, value) in triplets)
                if (value != 0f)
                    a.Entries[row].Add((col, value));
            var weight = rows.Weights[end - 1];
            var (lx, ly) = rows.Texels[end - 1];
            for (var c = 0; c < n; c++)
                rhs[r * n + c] = (weight <= 0f ? 0f : channel.Data[(ly * w + lx) * 4 + comps[c]]) * weight;
            start = end;
        }
        var x = new float[rows.Unique.Count * n];
        for (var u = 0; u < rows.Unique.Count; u++)
            for (var c = 0; c < n; c++)
                x[u * n + c] = channel.Data[(rows.Unique[u].Y * w + rows.Unique[u].X) * 4 + comps[c]];
        Solve(a, a.Transpose(x.Length), rhs, x);
        for (var u = 0; u < rows.Unique.Count; u++)
            for (var c = 0; c < n; c++)
                channel.Data[(rows.Unique[u].Y * w + rows.Unique[u].X) * 4 + comps[c]] = x[u * n + c];
    }

    // 18005b2b0 / 180060210: conjugate gradient on A^T A x = A^T b, as the
    // dense LightmapAhd.Solve but on vectors (Dot is 1801f6660).
    private static void Solve(Sparse a, Sparse at, float[] b, float[] x)
    {
        var rows = b.Length;
        var cols = x.Length;
        var atb = new float[cols];
        at.Multiply(b, atb);
        var ax = new float[rows];
        var r = new float[cols];
        a.Multiply(x, ax);
        at.Multiply(ax, r);
        for (var i = 0; i < cols; i++)
            r[i] = r[i] * -1f + atb[i];
        var p = (float[])r.Clone();
        var rs = Dot(r, r);
        if (!(Tolerance < rs))
            return;
        var ap = new float[cols];
        for (var step = 0; step < 100; step++)
        {
            a.Multiply(p, ax);
            at.Multiply(ax, ap);
            var alpha = rs / Dot(ap, p);
            for (var i = 0; i < cols; i++)
            {
                x[i] = p[i] * alpha + x[i];
                r[i] = ap[i] * -alpha + r[i];
            }
            var rsNew = Dot(r, r);
            if (rsNew < Tolerance)
                break;
            var beta = rsNew / rs;
            for (var i = 0; i < cols; i++)
                p[i] = p[i] * beta + r[i];
            rs = rsNew;
        }
    }

    /// <summary>
    /// 1801f6660 (InnerProduct): groups of four in chunks of 32768 groups, each
    /// chunk four SSE lanes reduced as (l2 + l3) + (l0 + l1), the chunks summed
    /// in order from 0, then the 0-3 leftover elements one by one.
    /// </summary>
    public static float Dot(float[] a, float[] b)
    {
        var quads = a.Length / 4;
        var total = 0f;
        var done = 0;
        while (quads > 0)
        {
            var count = Math.Min(quads, 0x8000);
            float l0 = 0, l1 = 0, l2 = 0, l3 = 0;
            for (var k = 0; k < count; k++)
            {
                var i = done + k * 4;
                l0 += a[i] * b[i];
                l1 += a[i + 1] * b[i + 1];
                l2 += a[i + 2] * b[i + 2];
                l3 += a[i + 3] * b[i + 3];
            }
            total += (l2 + l3) + (l0 + l1);
            done += count * 4;
            quads -= count;
        }
        for (var i = done; i < a.Length; i++)
            total += a[i] * b[i];
        return total;
    }

    /// <summary>180042730: RGB to YCoCg.</summary>
    public static (float, float, float) ToYCoCg(float r, float g, float b) =>
        (r * 0.25f + g * 0.5f + b * 0.25f, r * 0.5f - b * 0.5f, g * 0.5f - r * 0.25f - b * 0.25f);

    /// <summary>1800427b0: YCoCg to RGB, each clamped at 0.</summary>
    public static (float, float, float) FromYCoCg(float y, float co, float cg)
    {
        static float Clamp(float v) => 0f <= v ? v : 0f;
        return (Clamp(co + y - cg), Clamp(cg + y), Clamp(y - co - cg));
    }

    // 180060e20 (FloatBitMap_t::TransformRGB): every pixel's RGB, written back
    // only when a component compares different.
    private static void TransformRgb(LightmapImage.Channel channel, Func<float, float, float, (float, float, float)> f)
    {
        var d = channel.Data;
        for (var p = 0; p < d.Length / 4; p++)
        {
            float r = d[p * 4], g = d[p * 4 + 1], b = d[p * 4 + 2];
            var (x, y, z) = f(r, g, b);
            if (x != r || y != g || z != b)
            {
                d[p * 4] = x;
                d[p * 4 + 1] = y;
                d[p * 4 + 2] = z;
            }
        }
    }

    // 180052060 (lambda_2): channel 13's xyz (masked) through the luxel's tangent
    // frame (to world) or its inverse (back to tangent space), written masked.
    private static void TransformDirections(LightmapImage image, bool toWorld)
    {
        var channel = image.Channels[13]!;
        var mask = channel.Mask;
        var d = channel.Data;
        Span<float> frame = stackalloc float[12];
        Span<float> inverse = stackalloc float[12];
        for (var p = 0; p < image.Width * image.Height; p++)
        {
            float x = (mask & 1) != 0 ? d[p * 4] : 0f, y = (mask & 2) != 0 ? d[p * 4 + 1] : 0f;
            var z = (mask & 4) != 0 ? d[p * 4 + 2] : 0f;
            LightmapAhd.Frame(image, p, frame);
            if (!toWorld)
                LightmapAhd.Invert(frame, inverse);
            var (ox, oy, oz) = LightmapAhd.Transform(toWorld ? frame : inverse, x, y, z);
            if ((mask & 1) != 0)
                d[p * 4] = ox;
            if ((mask & 2) != 0)
                d[p * 4 + 1] = oy;
            if ((mask & 4) != 0)
                d[p * 4 + 2] = oz;
        }
    }
}
