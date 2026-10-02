using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// Hammer's CLatticeDeformer, the data every CMapDeformer keeps for the props
/// under it (+0x4c0 on the node, copied to a prop entity at +0x2b0 by
/// 181022540) and the deformation of points through it
/// (PropDeformer_Transform, 181288080).
/// <list type="bullet">
/// <item>The data: a CTransform (+0x0), the size (+0x20), segments along x and
/// divisions in y and z (+0x2c..+0x34), the interpolation mode (+0x38: 0
/// linear, 1 B-spline, 2 tangent, 3 explicit handles), the control points
/// ((segments + 1) * (divisionsY + 1) * (divisionsZ + 1), +0x48, segment
/// major) with two handles each in mode 3 (+0x60), and the mirror flag
/// (+0x70).</item>
/// <item>The evaluator (PropDeformer_Init, 18128d980): enabled when every
/// dimension is positive and the smallest absolute size is above 0; one
/// cubic per segment s in -1..segments and cell, its Bezier points from
/// 181288710 turned into power-basis coefficients (1810b80a0); the point
/// matrix the inverse of the transform times the given matrix, and its
/// general inverse back.</item>
/// <item>A point (181288fd0): x picks the segment and t, y and z the cells;
/// each of four z layers blends four y cells (181289250), then the layers
/// blend in z (18128b3e0): linearly, or as a quadratic or cubic Bezier when
/// the mode is not linear, the coordinate lies strictly inside, and there
/// are three or four cells.</item>
/// </list>
/// </summary>
internal sealed class LatticeDeformer
{
    public CTransform Transform;
    public Vector3 Size;
    public int Segments, DivisionsY, DivisionsZ, Mode;
    public Vector3[] Points = [];
    public Vector3[] Handles = [];
    public bool Mirror;

    public int CellsY => DivisionsY + 1;
    public int CellsZ => DivisionsZ + 1;

    /// <summary>PropDeformer_IsSet (18128ea90) and Init's test: every dimension positive and the smallest |size| above 0.</summary>
    public bool Enabled => Segments > 0 && DivisionsY > 0 && DivisionsZ > 0
        && MathF.Min(MathF.Min(MathF.Abs(Size.X), MathF.Abs(Size.Y)), MathF.Abs(Size.Z)) > 0f;

    // 18128c030: a control point, segments outside 0..Segments extended from the end along its direction
    // (normalised to Size.X / Segments when asked).
    private Vector3 Point(int s, int cell, bool normalise)
    {
        var cells = CellsY * CellsZ;
        if (s < 0)
        {
            var p = Points[cell];
            var q = Mode == 3 ? Handles[cell * 2] : Points[cells + cell];
            var d = Extension(p - q, normalise);
            var f = (float)-s;
            return new Vector3(d.X * f + p.X, d.Y * f + p.Y, d.Z * f + p.Z);
        }
        if (s > Segments)
        {
            var p = Points[cells * Segments + cell];
            var back = (Segments - 1) * cells + cell;
            var q = Mode == 3 ? Handles[back * 2 + 1] : Points[back];
            var d = Extension(p - q, normalise);
            var f = (float)(s - Segments);
            return new Vector3(f * d.X + p.X, f * d.Y + p.Y, f * d.Z + p.Z);
        }
        return Points[cell + cells * s];
    }

    private Vector3 Extension(Vector3 d, bool normalise)
    {
        if (!normalise)
            return d;
        var length = Size.X / Segments;
        var n = NormaliseSlowZero(d);
        return new Vector3(n.X * length, n.Y * length, n.Z * length);
    }

    // The engine's VectorNormalize: z, y, x squares; 1 / length when it lies in [1e-17, 1e17]; zero stays zero.
    // (Lengths outside go through VectorNormalize_Slow, not ported: unit-scale data never reaches it.)
    private static Vector3 NormaliseSlowZero(Vector3 d)
    {
        var len = MathF.Sqrt(d.Z * d.Z + d.Y * d.Y + d.X * d.X);
        if (len < 1e-17f || 1e17f < len)
        {
            if (len != 0f)
                throw new NotSupportedException("VectorNormalize_Slow is not ported");
            return Vector3.Zero;
        }
        var inv = 1f / len;
        return new Vector3(d.X * inv, d.Y * inv, d.Z * inv);
    }

    // 181288710: the four Bezier points of segment s at a cell.
    private (Vector3, Vector3, Vector3, Vector3) Bezier(int s, int cell)
    {
        var a = Point(s, cell, true);
        var b = Point(s + 1, cell, true);
        if (Mode == 0 || s < 0 || Segments <= s)
            return (a, new Vector3(b.X * 0.33333334f + a.X * 0.6666666f, b.Y * 0.33333334f + a.Y * 0.6666666f, b.Z * 0.33333334f + a.Z * 0.6666666f),
                    new Vector3(b.X * 0.6666667f + a.X * 0.3333333f, b.Y * 0.6666667f + a.Y * 0.3333333f, b.Z * 0.6666667f + a.Z * 0.3333333f), b);
        if (Mode == 1)
        {
            var c = Point(s - 1, cell, false);
            var d = Point(s + 2, cell, false);
            var p1 = new Vector3(b.X * 0.33333334f + a.X * 0.6666666f, b.Y * 0.33333334f + a.Y * 0.6666666f, b.Z * 0.33333334f + a.Z * 0.6666666f);
            var p2 = new Vector3(a.X * 0.33333334f + b.X * 0.6666666f, a.Y * 0.33333334f + b.Y * 0.6666666f, a.Z * 0.33333334f + b.Z * 0.6666666f);
            var p0 = new Vector3((c.X * 0.33333334f + a.X * 0.6666666f + p1.X) * 0.5f, (a.Y * 0.6666666f + c.Y * 0.33333334f + p1.Y) * 0.5f,
                                 (a.Z * 0.6666666f + c.Z * 0.33333334f + p1.Z) * 0.5f);
            var p3 = new Vector3((d.X * 0.33333334f + b.X * 0.6666666f + p2.X) * 0.5f, (d.Y * 0.33333334f + b.Y * 0.6666666f + p2.Y) * 0.5f,
                                 (d.Z * 0.33333334f + b.Z * 0.6666666f + p2.Z) * 0.5f);
            return (p0, p1, p2, p3);
        }
        if (Mode == 2)
        {
            var c = Point(s - 1, cell, true);
            var d = Point(s + 2, cell, true);
            var third = MathF.Sqrt((a.Z - b.Z) * (a.Z - b.Z) + (a.Y - b.Y) * (a.Y - b.Y) + (a.X - b.X) * (a.X - b.X)) * 0.33333334f;
            var t0 = NormaliseSlowZero(new Vector3(b.X - c.X, b.Y - c.Y, b.Z - c.Z));
            var t1 = NormaliseSlowZero(new Vector3(a.X - d.X, a.Y - d.Y, a.Z - d.Z));
            return (a, new Vector3(a.X + t0.X * third, a.Y + t0.Y * third, a.Z + t0.Z * third),
                    new Vector3(t1.X * third + b.X, t1.Y * third + b.Y, t1.Z * third + b.Z), b);
        }
        // Mode 3: the stored handles.
        var at = (CellsY * CellsZ * s + cell) * 2;
        return (a, Handles[at], Handles[at + 1], b);
    }

    // 1810b80a0: Bezier points to the coefficients of t^3, t^2, t and 1.
    private static (Vector3, Vector3, Vector3, Vector3) PowerBasis((Vector3 P0, Vector3 P1, Vector3 P2, Vector3 P3) b)
    {
        static float A(float p0, float p1, float p2, float p3) => ((p1 * 3f - p0 * 1f) - p2 * 3f) + p3;
        static float B(float p0, float p1, float p2) => (p0 * 3f - p1 * 6f) + p2 * 3f;
        static float C(float p0, float p1) => p1 * 3f - p0 * 3f;
        var (p0, p1, p2, p3) = b;
        return (new Vector3(A(p0.X, p1.X, p2.X, p3.X), A(p0.Y, p1.Y, p2.Y, p3.Y), A(p0.Z, p1.Z, p2.Z, p3.Z)),
                new Vector3(B(p0.X, p1.X, p2.X), B(p0.Y, p1.Y, p2.Y), B(p0.Z, p1.Z, p2.Z)),
                new Vector3(C(p0.X, p1.X), C(p0.Y, p1.Y), C(p0.Z, p1.Z)), p0);
    }

    /// <summary>An evaluator built for one matrix (PropDeformer_Init).</summary>
    public sealed class Evaluator
    {
        private readonly LatticeDeformer _d;
        private readonly (Vector3 A, Vector3 B, Vector3 C, Vector3 D)[] _frames;
        private readonly float[] _toLattice, _fromLattice;

        internal Evaluator(LatticeDeformer d, float[] matrix)
        {
            _d = d;
            var cells = d.CellsY * d.CellsZ;
            _frames = new (Vector3, Vector3, Vector3, Vector3)[(d.Segments + 2) * cells];
            var k = 0;
            for (var s = -1; s <= d.Segments; s++)
                for (var cell = 0; cell < cells; cell++)
                    _frames[k++] = PowerBasis(d.Bezier(s, cell));
            _toLattice = MapMeshes.Concat(d.Transform.Inverse().Matrix(), matrix);
            var inverse = new float[16];
            LightMath.Inverse4(LightMath.To4(_toLattice), inverse);
            _fromLattice = [inverse[0], inverse[1], inverse[2], inverse[3], inverse[4], inverse[5], inverse[6], inverse[7],
                            inverse[8], inverse[9], inverse[10], inverse[11]];
        }

        // Matrix3x4_TransformPoint of a frame: a t^3 + b t^2 + c t + d, as the row dot (t^3, t^2, t) plus d.
        private static Vector3 Cubic((Vector3 A, Vector3 B, Vector3 C, Vector3 D) f, Vector3 t)
            => MapMeshes.Transform([f.A.X, f.B.X, f.C.X, f.D.X, f.A.Y, f.B.Y, f.C.Y, f.D.Y, f.A.Z, f.B.Z, f.C.Z, f.D.Z], t);

        // 18128b3e0: four points blended over count cells at f.
        private static Vector3 Blend(Vector3[] p, int count, float f, bool smooth)
        {
            if (!smooth)
            {
                f = (count - 1) * f;
                var i = Math.Max((int)f, 0);
                if (count - 2 < i)
                    i = count - 2;
                f -= i;
                var g = 1f - f;
                return new Vector3(g * p[i].X + f * p[i + 1].X, g * p[i].Y + f * p[i + 1].Y, g * p[i].Z + f * p[i + 1].Z);
            }
            if (count == 4)
                throw new NotSupportedException("the cubic cell blend (18127fdc0) is not ported");
            if (count == 3)
            {
                var g = 1f - f;
                var gg = g * g;
                var mid = (g + g) * f;
                var ff = f * f;
                return new Vector3(gg * p[0].X + mid * p[1].X + ff * p[2].X, gg * p[0].Y + mid * p[1].Y + ff * p[2].Y, gg * p[0].Z + mid * p[1].Z + ff * p[2].Z);
            }
            return Vector3.Zero;
        }

        // 181288fd0: a point in the lattice's space.
        private Vector3 Evaluate(Vector3 p)
        {
            var d = _d;
            var cy = d.CellsY;
            var cz = d.CellsZ;
            var fz = p.Z / d.Size.Z;
            var fy = p.Y / d.Size.Y;
            var u = (p.X / d.Size.X) * d.Segments + 1f;
            var seg = Math.Max((int)u, 0);
            if (d.Segments + 1 < seg)
                seg = d.Segments + 1;
            var t = u - seg;
            var tv = new Vector3(t * t * t, t * t, t);
            var last = cz * cy - 1;
            var first = seg * cz * cy;
            var smoothY = d.Mode != 0 && 0f < fy && fy < 1f && cy >= 3;
            var smoothZ = d.Mode != 0 && 0f < fz && fz < 1f && cz >= 3;
            var layers = new Vector3[4];
            for (var k = 0; k < 4; k++)
            {
                var row = cy * k;
                var cellsAt = new Vector3[4];
                for (var j = 0; j < 4; j++)
                    cellsAt[j] = Cubic(_frames[first + Math.Min(row + j, last)], tv);
                layers[k] = Blend(cellsAt, cy, fy, smoothY);
            }
            return Blend(layers, cz, fz, smoothZ);
        }

        /// <summary>A point deformed: into the lattice, evaluated, and back (PropDeformer_Transform).</summary>
        public Vector3 Deform(Vector3 p)
        {
            var local = MapMeshes.Transform(_toLattice, p);
            return MapMeshes.Transform(_fromLattice, Evaluate(local));
        }
    }

    /// <summary>The evaluator for points handed over with <paramref name="matrix"/> (identity: points already in the world).</summary>
    public Evaluator For(float[] matrix) => new(this, matrix);

    /// <summary>
    /// The deformer a CMapDeformerLattice or CMapDeformerSimple node keeps at
    /// +0x4c0, as a map load leaves it. The transform is the node's origin and
    /// angles (the map's nodes keep world placements). A lattice takes its
    /// size, segments, divisions, interpolation mode (LINEAR, B_SPLINE,
    /// CATMUL_ROM, BEZIER: 0 to 3) and nodeData's control points; a simple
    /// deformer its size and the bend (<see cref="Bend"/>). The mirror flag is
    /// only ever set by a KV3 load or a copy (18128eaf0, 180246d70); a map's
    /// node leaves it clear. Not ported, and thrown: a scaled node, a lattice
    /// in BEZIER mode (its handles' source is not read), CMapDeformerPath.
    /// Checked against the structs PropDeformer_Transform was handed
    /// (capture_deformer.py on deformerprobe): all three bit for bit.
    /// </summary>
    public static LatticeDeformer FromNode(DmxBinary.Element node)
    {
        if ((node.GetValue<Vector3>("scales") ?? Vector3.One) != Vector3.One)
            throw new NotSupportedException($"{node.Type} {node.GetValue<int>("nodeID")}: a scaled deformer is not ported");
        var d = new LatticeDeformer
        {
            Transform = CTransform.FromNode(node),
            Size = node.GetValue<Vector3>("size") ?? Vector3.Zero,
        };
        switch (node.Type)
        {
            case "CMapDeformerLattice":
                d.Segments = node.GetValue<int>("numSegments") ?? 0;
                d.DivisionsY = node.GetValue<int>("numDivisionsY") ?? 0;
                d.DivisionsZ = node.GetValue<int>("numDivisionsZ") ?? 0;
                d.Mode = (node.Get<string>("interpolationMode") ?? "B_SPLINE") switch
                {
                    "LINEAR" => 0,
                    "B_SPLINE" => 1,
                    "CATMUL_ROM" => 2,
                    var m => throw new NotSupportedException($"lattice deformer {node.GetValue<int>("nodeID")}: interpolation {m} is not ported"),
                };
                var count = (d.Segments + 1) * (d.DivisionsY + 1) * (d.DivisionsZ + 1);
                var points = node.Get<DmxBinary.Element>("nodeData")?.Get<object?[]>("controlPoints") ?? [];
                if (points.Length != count)
                    throw new NotSupportedException($"lattice deformer {node.GetValue<int>("nodeID")}: {points.Length} control points for {count}");
                d.Points = [.. points.Select(p => (Vector3)p!)];
                break;
            case "CMapDeformerSimple":
                var axis = (node.Get<string>("bendAxis") ?? "Y") switch
                {
                    "Y" => 0,
                    "Z" => 1,
                    var a => throw new NotSupportedException($"simple deformer {node.GetValue<int>("nodeID")}: bend axis {a}"),
                };
                // The angle setter (1811781b0) clamps to [-360, 360].
                var angle = Math.Clamp(node.GetValue<float>("bendAngle") ?? 0f, -360f, 360f);
                d.Bend(axis, angle, node.GetValue<float>("bendPoint") ?? 0f, node.GetValue<float>("bendRadius") ?? 0f, Vector3.One);
                break;
            default:
                throw new NotSupportedException($"{node.Type} {node.GetValue<int>("nodeID")}: not ported");
        }
        return d;
    }

    // 181290410: every control point back on the regular grid over the size,
    // then the handles reset (18128cab0).
    private void Grid()
    {
        var cells = CellsY * CellsZ;
        Points = new Vector3[(Segments + 1) * cells];
        for (var i = 0; i < Points.Length; i++)
            Points[i] = GridPoint(i);
        DefaultHandles();
    }

    private Vector3 GridPoint(int i)
    {
        var cells = CellsY * CellsZ;
        var rem = i % cells;
        return new Vector3(((float)(i / cells) / Segments) * Size.X,
                           ((float)(rem % CellsY) / DivisionsY) * Size.Y,
                           ((float)(rem / CellsY) / DivisionsZ) * Size.Z);
    }

    // 18128cab0: in mode 3, two handles per point and segment at the thirds
    // between grid points (not the control points); otherwise none.
    private void DefaultHandles()
    {
        if (Mode != 3)
        {
            Handles = [];
            return;
        }
        var cells = CellsY * CellsZ;
        Handles = new Vector3[Segments * cells * 2];
        for (var i = 0; i < Segments * cells; i++)
        {
            Vector3 a = GridPoint(i), b = GridPoint(i + cells);
            Handles[i * 2] = new Vector3((a.X * 0.6666666f) + (b.X * 0.33333334f), (a.Y * 0.6666666f) + (b.Y * 0.33333334f), (a.Z * 0.6666666f) + (b.Z * 0.33333334f));
            Handles[(i * 2) + 1] = new Vector3((a.X * 0.3333333f) + (b.X * 0.6666667f), (a.Y * 0.3333333f) + (b.Y * 0.6666667f), (a.Z * 0.3333333f) + (b.Z * 0.6666667f));
        }
    }

    // 18128cdf0's axis frames (DAT_183013dc0 for axis 0, Y; DAT_183013df0
    // for Z), three rows of four.
    private static readonly float[] BendY = [0, 1, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0];
    private static readonly float[] BendZ = [0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0];

    /// <summary>
    /// CMapDeformerSimple's lattice (18128cdf0): mode 3, one segment per
    /// started 90 degrees, one division in y and z, reset to the grid; then,
    /// from an angle of 0.1 on, each segment's four corner columns turned
    /// around the bend centre on a circle of the bend radius (by default the
    /// one that keeps the size along x), the handles a cubic Bezier's for the
    /// segment's arc. Each written point times <paramref name="scales"/> (the
    /// node's, +0xb8). Grouped as the binary computes it.
    /// </summary>
    internal void Bend(int axis, float angle, float point, float radius, Vector3 scales)
    {
        Mode = 3;
        var a = MathF.Abs(angle);
        var sign = angle >= 0f ? 1f : -1f;
        var segs = (int)MathF.Ceiling(a / 90f);
        if (segs < 2)
            segs = 1;
        (Segments, DivisionsY, DivisionsZ) = (segs, 1, 1);
        Grid();
        if (!(a >= 0.1f))
            return;
        var t = axis == 0 ? BendY : BendZ;
        float a0 = t[0], a1 = t[4], a2 = t[8];
        if (radius <= 0f)
            radius = Size.X / (a * 0.017453292f);
        if (angle < 0f)
        {
            var f = ((Size.Z * a2) + (Size.Y * a1) + (a0 * Size.X)) / ((a1 * a1) + (a0 * a0) + (a2 * a2));
            radius += MathF.Sqrt((f * a2 * f * a2) + (f * a1 * f * a1) + (f * a0 * f * a0));
        }
        var step = angle / segs;
        var cx = (Size.X * point) - (a0 * radius * sign);
        var cy = 0f - (a1 * radius * sign);
        var cz = 0f - (a2 * radius * sign);
        var start = (90f - (sign * 90f)) - (angle * point);
        var lengthSq = (a0 * a0) + (a1 * a1) + (a2 * a2);
        var cells = CellsY * CellsZ;
        for (var s = 0; s < segs; s++)
        {
            var b = (s * step) + start;
            var span = (b + step) - b;
            if (!(span >= -180f && span <= 180f))
                continue;
            var h = span * 0.017453292f * 0.5f;
            LightCrt.SinCos(h, out var sh, out var ch);
            var neg = -sh;
            var k = (4f - ch) / 3f;
            var mid = ((span * 0.5f) + b) * 0.017453292f;
            var m = ((ch - k) * (1f / LightCrt.Tan(h))) + sh;
            LightCrt.SinCos(mid, out var sm, out var cm);
            var r0x = (cm * ch) - (neg * sm);
            var r0y = (ch * sm) + (neg * cm);
            var r1x = (cm * k) - (-m * sm);
            var r1y = (sm * k) + (-m * cm);
            var r2y = (cm * m) + (sm * k);
            var r2x = (cm * k) - (sm * m);
            var r3y = (sh * cm) + (ch * sm);
            var r3x = (ch * cm) - (sh * sm);
            for (var c = 0; c < 4; c++)
            {
                var g = GridPoint(c + (s * 4));
                var f = ((g.Y * a1) + (g.Z * a2) + (g.X * a0)) / lengthSq;
                var r = (MathF.Sqrt((f * a2 * f * a2) + (f * a1 * f * a1) + (f * a0 * f * a0)) * sign) + radius;
                float b0 = t[2], b1 = t[6], b2 = t[10];
                var proj = (((g.Z - cz) * b2) + ((g.Y - cy) * b1) + ((g.X - cx) * b0)) / ((b1 * b1) + (b0 * b0) + (b2 * b2));
                var q = new Vector3((b0 * proj) + cx, (b1 * proj) + cy, (b2 * proj) + cz);
                Vector3 At(float u, float v) => new(
                    ((t[0] * u * r) + q.X + (t[1] * v * r)) * scales.X,
                    ((t[4] * u * r) + q.Y + (t[5] * v * r)) * scales.Y,
                    ((t[8] * u * r) + q.Z + (t[9] * v * r)) * scales.Z);
                if (c >= cells)
                    continue;
                var i = (s * cells) + c;
                Points[i] = At(r0x, r0y);
                Handles[i * 2] = At(r1x, r1y);
                Handles[(i * 2) + 1] = At(r2x, r2y);
                Points[((s + 1) * cells) + c] = At(r3x, r3y);
            }
        }
    }
}
