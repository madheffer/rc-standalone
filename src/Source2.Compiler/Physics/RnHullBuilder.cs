using System.Numerics;

namespace Source2.Compiler.Physics;

/// <summary>A cooked convex hull, laid out as <c>RnHull_t</c> (0xf8 bytes).</summary>
public sealed class RnHull
{
    public Vector3 Centroid;
    public float MaxAngularRadius;
    public float MinCentroidRadius;
    public Vector3 BoundsMin;
    public Vector3 BoundsMax;
    public Vector3 OrthographicAreas;
    /// <summary>Row-major 3x4: inertia in the first three columns, the mass centre in the fourth.</summary>
    public float[] MassProperties = new float[12];
    public float Volume;
    public float SurfaceArea;
    public Vector3[] VertexPositions = [];
    public (Vector3 Normal, float Offset)[] Planes = [];
    /// <summary>Per vertex, one outgoing half-edge.</summary>
    public byte[] Vertices = [];
    public (byte Next, byte Twin, byte Origin, byte Face)[] Edges = [];
    /// <summary>Per face, one half-edge.</summary>
    public byte[] Faces = [];
    public uint Flags;
}

/// <summary>
/// vphysics2's <c>RnHullCreate</c> with the options the resource compiler
/// passes for a hull shape (see <c>docs/HULLS.md</c>).
///
/// <para>Eight to 36 points sitting on their bounding box's corners become
/// <c>RnHullCreateBox</c>. Otherwise the points are centred on the box
/// midpoint and scaled by a power of two, hulled (<see cref="QuickHull"/>),
/// checked against the 256 limits, sharpened, checked for a 0.01 inner
/// margin, scaled and moved back, and converted.</para>
///
/// <para>Not ported: the extrusion retry for an invalid hull and the
/// simplifiers, which the compile's options only reach past 256.</para>
/// </summary>
public static class RnHullBuilder
{
    /// <summary>The <c>options</c> block <c>RnHullCreate</c> takes.</summary>
    public sealed record Options
    {
        public float Angle { get; init; }
        public float Tolerance { get; init; }
        public int MaxFaces { get; init; } = 256;
        public int MaxEdges { get; init; } = 256;
        public int MaxVertices { get; init; } = 256;
        public int Iterations { get; init; }
        public int Algorithm { get; init; }
        public bool Sharpen { get; init; } = true;
        public bool RelativeTolerance { get; init; } = true;
        public bool Warn { get; init; }
        public float MinThickness { get; init; }
        public float PointScale { get; init; } = 1f;
        public float InnerMargin { get; init; } = 0.01f;
        public float MinRadius { get; init; }

        /// <summary>What resourcecompiler passes for a hull shape.</summary>
        public static Options Compile { get; } = new();

        /// <summary>What the map builder passes when it hulls a map mesh (FUN_1801ff720).</summary>
        public static Options MapBuilder { get; } = new() { Angle = 5f, MinThickness = 1f };
    }

    /// <summary>The hull, or null with the failure in <paramref name="error"/>.</summary>
    public static RnHull? Create(ReadOnlySpan<Vector3> points, Options? options, out int error)
    {
        error = 0;
        options ??= Options.Compile;
        var n = points.Length;
        if (n >= 8 && n <= 36)
        {
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = -float.MaxValue, maxY = -float.MaxValue, maxZ = -float.MaxValue;
            foreach (var p in points)
            {
                if (p.X <= minX) minX = p.X;
                if (p.Y <= minY) minY = p.Y;
                if (p.Z <= minZ) minZ = p.Z;
                if (maxX <= p.X) maxX = p.X;
                if (maxY <= p.Y) maxY = p.Y;
                if (maxZ <= p.Z) maxZ = p.Z;
            }
            var hx = (maxX - minX) * 0.5f;
            var hy = (maxY - minY) * 0.5f;
            var hz = (maxZ - minZ) * 0.5f;
            var cx = (maxX + minX) * 0.5f;
            var cy = (maxY + minY) * 0.5f;
            var cz = (maxZ + minZ) * 0.5f;
            var mask = 0;
            foreach (var p in points)
            {
                var dx = p.X - cx;
                var dy = p.Y - cy;
                var dz = p.Z - cz;
                if (MathF.Abs(MathF.Abs(dx) - hx) <= 0.03125f && MathF.Abs(MathF.Abs(dy) - hy) <= 0.03125f
                    && MathF.Abs(MathF.Abs(dz) - hz) <= 0.03125f)
                {
                    var corner = (SignBit(dx) ? 1 : 0) | (SignBit(dy) ? 2 : 0) | (SignBit(dz) ? 4 : 0);
                    mask |= 1 << corner;
                }
            }
            if (mask == 0xff)
                return CreateBox(new Vector3(hx, hy, hz), new Vector3(cx, cy, cz));
        }
        return Build(points, options, out error);
    }

    private static bool SignBit(float f) => BitConverter.SingleToInt32Bits(f) < 0;

    // FUN_1803843f0 then FUN_1801a7310
    private static RnHull? Build(ReadOnlySpan<Vector3> points, Options o, out int error)
    {
        var qh = BuildHull(points, o, out error);
        return qh == null ? null : Convert(qh);
    }

    /// <summary>
    /// FUN_1803843f0 (and the map builder's copy, FUN_18131efc0): the hull,
    /// scaled and moved back, before any conversion.
    /// </summary>
    internal static QuickHull? BuildHull(ReadOnlySpan<Vector3> points, Options o, out int error)
    {
        error = 0;
        var n = points.Length;
        if (n < 1)
        {
            error = 1;
            return null;
        }
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = -float.MaxValue, maxY = -float.MaxValue, maxZ = -float.MaxValue;
        foreach (var p in points)
        {
            minX = p.X < minX ? p.X : minX;
            minY = p.Y < minY ? p.Y : minY;
            minZ = p.Z < minZ ? p.Z : minZ;
            maxX = p.X > maxX ? p.X : maxX;
            maxY = p.Y > maxY ? p.Y : maxY;
            maxZ = p.Z > maxZ ? p.Z : maxZ;
        }
        var s = o.PointScale;
        var cx = ((minX * s) + (maxX * s)) * 0.5f;
        var cy = ((minY * s) + (maxY * s)) * 0.5f;
        var cz = ((minZ * s) + (maxZ * s)) * 0.5f;
        var ex = MathF.Abs((s * maxX) - cx);
        var ey = MathF.Abs((s * maxY) - cy);
        var ez = MathF.Abs((s * maxZ) - cz);
        var m = ey > ex ? ey : ex;
        m = ez > m ? ez : m;
        var shift = Frexp(m) - 6;
        if (shift < 1)
            shift = 0;
        var k = (float)(1 << shift);
        var inv = 1f / k;
        var pts = new float[n * 3];
        for (var i = 0; i < n; i++)
        {
            pts[i * 3] = ((s * points[i].X) - cx) * inv;
            pts[i * 3 + 1] = ((s * points[i].Y) - cy) * inv;
            pts[i * 3 + 2] = ((s * points[i].Z) - cz) * inv;
        }
        var tolerance = o.Tolerance;
        if (!o.RelativeTolerance)
            tolerance = tolerance / k;
        var qh = new QuickHull();
        var buildTolerance = 1.1920929e-05f > tolerance ? 1.1920929e-05f : tolerance;
        qh.Build(n, pts, buildTolerance, o.RelativeTolerance);
        if (!qh.IsValid())
        {
            // FUN_180384ab0: a flat or degenerate set is pushed out by the
            // minimum thickness along +x, +y and +z and hulled again.
            var e = o.MinThickness / k;
            if (!(0f < e))
            {
                error = qh.Error + 5;
                return null;
            }
            var extruded = new float[n * 12];
            for (var i = 0; i < n; i++)
            {
                float x = pts[i * 3], y = pts[i * 3 + 1], z = pts[i * 3 + 2];
                var o12 = i * 12;
                extruded[o12] = x;
                extruded[o12 + 1] = y;
                extruded[o12 + 2] = z;
                extruded[o12 + 3] = x + e;
                extruded[o12 + 4] = y + 0f;
                extruded[o12 + 5] = z + 0f;
                extruded[o12 + 6] = x + 0f;
                extruded[o12 + 7] = y + e;
                extruded[o12 + 8] = z + 0f;
                extruded[o12 + 9] = x + 0f;
                extruded[o12 + 10] = y + 0f;
                extruded[o12 + 11] = z + e;
            }
            var retry = new QuickHull();
            retry.Build(n * 4, extruded, buildTolerance, o.RelativeTolerance);
            if (!retry.IsValid())
            {
                error = qh.Error + 5;
                return null;
            }
            qh = retry;
        }
        if (!Limits(qh, o, tolerance, out error))
            return null;
        if (!Check(qh, o, out error))
            return null;
        qh.Scale(k);
        qh.Translate(cx, cy, cz);
        return qh;
    }

    // frexpf's exponent: x = m 2^e with m in [0.5, 1).
    private static int Frexp(float x)
    {
        if (x == 0f || !float.IsFinite(x))
            return 0;
        var bits = BitConverter.SingleToInt32Bits(x);
        var exp = (bits >> 23) & 0xff;
        if (exp == 0)
        {
            var e = Frexp(x * 16777216f);
            return e - 24;
        }
        return exp - 126;
    }

    // FUN_180385370
    private static bool Limits(QuickHull qh, Options o, float tolerance, out int error)
    {
        error = 0;
        // Valve loops Iterations + 1 times, simplifying while a limit is broken.
        if (o.Iterations >= 0)
        {
            var faces = qh.HullFaces.Count();
            var edges = qh.HullFaces.Sum(f => QuickHull.Loop(f).Count());
            var verts = qh.HullVertices.Count();
            if (faces > o.MaxFaces || edges > o.MaxEdges || verts > o.MaxVertices || NeedsSimplify(qh, o.Angle, tolerance))
                throw new NotSupportedException("the hull needs simplifying, which is not ported");
        }
        if (o.Sharpen)
        {
            qh.Sharpen(1e-4f, 0.01f);
            var faces = qh.HullFaces.Count();
            var edges = qh.HullFaces.Sum(f => QuickHull.Loop(f).Count());
            var verts = qh.HullVertices.Count();
            if (faces > 256 || edges > 256 || verts > 256)
            {
                error = 2;
                return false;
            }
        }
        return true;
    }

    // FUN_180390040: two neighbouring faces within the angle of each other,
    // or an edge shorter than the minimum, asks for simplification.
    private static bool NeedsSimplify(QuickHull qh, float angle, float minEdge)
    {
        var a = angle * 0.017453292f;
        if (1.5697963f <= a)
            a = 1.5697963f;
        var tan = MathF.Tan(a);
        if (tan == 0f && minEdge == 0f)
            return false;
        foreach (var f in qh.HullFaces)
        {
            foreach (var e in QuickHull.Loop(f))
            {
                var g = e.Twin!.Face;
                var dot = ((f.NZ * g.NZ) + (g.NY * f.NY)) + (g.NX * f.NX);
                if (0f < dot)
                {
                    var cx = (f.NY * g.NZ) - (g.NY * f.NZ);
                    var cy = (f.NZ * g.NX) - (g.NZ * f.NX);
                    var cz = (g.NY * f.NX) - (f.NY * g.NX);
                    var len = MathF.Sqrt(((cy * cy) + (cz * cz)) + (cx * cx));
                    if (len < dot * tan)
                        return true;
                }
                var w = e.Twin.Origin;
                var dx = e.Origin.X - w.X;
                var dy = e.Origin.Y - w.Y;
                var dz = e.Origin.Z - w.Z;
                if (((dx * dx) + (dy * dy)) + (dz * dz) < minEdge * minEdge)
                    return true;
            }
        }
        return false;
    }

    // FUN_180385100
    private static bool Check(QuickHull qh, Options o, out int error)
    {
        error = 0;
        if (0f < o.MinRadius)
            throw new NotSupportedException("the minimum radius check is not ported");
        if (0f < o.InnerMargin)
        {
            float x = 0f, y = 0f, z = 0f;
            var count = 0;
            foreach (var v in qh.HullVertices)
            {
                x = x + v.X;
                y = y + v.Y;
                z = z + v.Z;
                count++;
            }
            if (count > 0)
            {
                var c = (float)count;
                x = x / c;
                y = y / c;
                z = z / c;
            }
            var min = float.MaxValue;
            foreach (var f in qh.HullFaces)
            {
                var d = -((((y * f.NY) + (x * f.NX)) + (z * f.NZ)) - f.D);
                if (d <= min)
                    min = d;
            }
            if (min < o.InnerMargin)
            {
                error = 4;
                return false;
            }
        }
        return true;
    }

    // FUN_1801a7310
    private static RnHull? Convert(QuickHull qh)
    {
        var verts = qh.HullVertices.ToList();
        if (verts.Count > 256)
            return null;
        var faces = qh.HullFaces.ToList();
        var edges = new List<QuickHull.HalfEdge>();
        foreach (var f in faces)
            edges.AddRange(QuickHull.Loop(f));
        if (faces.Count > 256 || edges.Count > 256)
            return null;
        for (var i = 0; i + 1 < edges.Count; i += 2)
        {
            for (var j = i + 1; j < edges.Count; j++)
            {
                if (edges[i].Twin == edges[j])
                {
                    (edges[i + 1], edges[j]) = (edges[j], edges[i + 1]);
                    break;
                }
            }
        }
        var hull = new RnHull
        {
            OrthographicAreas = new Vector3(0.25f, 0.25f, 0.25f),
            VertexPositions = new Vector3[verts.Count],
            Vertices = new byte[verts.Count],
            Edges = new (byte, byte, byte, byte)[edges.Count],
            Faces = new byte[faces.Count],
            Planes = new (Vector3, float)[faces.Count],
        };
        float sx = 0f, sy = 0f, sz = 0f;
        for (var i = 0; i < verts.Count; i++)
        {
            hull.VertexPositions[i] = new Vector3(verts[i].X, verts[i].Y, verts[i].Z);
            sx = sx + verts[i].X;
            sy = sy + verts[i].Y;
            sz = sz + verts[i].Z;
        }
        var r = 1f / (float)verts.Count;
        hull.Centroid = new Vector3(sx * r, sy * r, sz * r);
        var radius = 0f;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = -float.MaxValue, maxY = -float.MaxValue, maxZ = -float.MaxValue;
        foreach (var v in verts)
        {
            var dx = (sx * r) - v.X;
            var dy = (sy * r) - v.Y;
            var dz = (sz * r) - v.Z;
            var d = MathF.Sqrt(((dy * dy) + (dx * dx)) + (dz * dz));
            if (radius <= d) radius = d;
            if (v.Y <= minY) minY = v.Y;
            if (v.X <= minX) minX = v.X;
            if (maxY <= v.Y) maxY = v.Y;
            if (v.Z <= minZ) minZ = v.Z;
            if (maxZ <= v.Z) maxZ = v.Z;
            if (maxX <= v.X) maxX = v.X;
        }
        hull.BoundsMin = new Vector3(minX, minY, minZ);
        hull.BoundsMax = new Vector3(maxX, maxY, maxZ);
        hull.MaxAngularRadius = radius;
        for (var i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            var origin = verts.IndexOf(e.Origin);
            hull.Edges[i] = ((byte)edges.IndexOf(e.Next), (byte)edges.IndexOf(e.Twin!), (byte)origin, (byte)faces.IndexOf(e.Face));
            hull.Vertices[origin] = (byte)i;
        }
        for (var i = 0; i < faces.Count; i++)
        {
            var f = faces[i];
            hull.Planes[i] = (new Vector3(f.NX, f.NY, f.NZ), f.D);
            hull.Faces[i] = (byte)edges.IndexOf(f.Edge!);
        }
        MassProperties(hull);
        Areas(hull);
        CentroidRadius(hull);
        return hull;
    }

    // RnHullCreateBox
    public static RnHull CreateBox(Vector3 e, Vector3 c)
    {
        var hull = new RnHull
        {
            OrthographicAreas = new Vector3(1f, 1f, 1f),
            VertexPositions =
            [
                new(e.X + c.X, e.Y + c.Y, e.Z + c.Z),
                new(c.X - e.X, e.Y + c.Y, c.Z + e.Z),
                new(e.X + c.X, c.Y - e.Y, c.Z + e.Z),
                new(c.X - e.X, c.Y - e.Y, c.Z + e.Z),
                new(e.X + c.X, e.Y + c.Y, c.Z - e.Z),
                new(c.X - e.X, e.Y + c.Y, c.Z - e.Z),
                new(e.X + c.X, c.Y - e.Y, c.Z - e.Z),
                new(c.X - e.X, c.Y - e.Y, c.Z - e.Z),
            ],
            Planes =
            [
                (new(0f, 1f, 0f), e.Y + c.Y),
                (new(0f, 0f, -1f), e.Z - c.Z),
                (new(0f, -1f, 0f), e.Y - c.Y),
                (new(1f, 0f, 0f), e.X + c.X),
                (new(0f, 0f, 1f), c.Z + e.Z),
                (new(-1f, 0f, 0f), e.X - c.X),
            ],
            Edges = BoxEdges,
            Faces = [0, 4, 8, 12, 16, 11],
            Vertices = [20, 18, 16, 22, 14, 4, 6, 10],
            Centroid = c,
            MaxAngularRadius = MathF.Sqrt(((e.Z * e.Z) + (e.Y * e.Y)) + (e.X * e.X)),
            BoundsMin = new Vector3(c.X - e.X, c.Y - e.Y, c.Z - e.Z),
            BoundsMax = new Vector3(e.X + c.X, e.Y + c.Y, e.Z + c.Z),
            Volume = e.X * 8f * e.Y * e.Z,
        };
        var ax = e.X - -e.X;
        var az = e.Z - -e.Z;
        var ay = e.Y - -e.Y;
        var t = ((ay * ax) + (ax * az)) + (ay * az);
        hull.SurfaceArea = t + t;
        CentroidRadius(hull);
        var third = hull.Volume * 0.33333334f;
        var mp = hull.MassProperties;
        mp[0] = ((e.Y * e.Y) + (e.Z * e.Z)) * third;
        mp[5] = ((e.X * e.X) + (e.Z * e.Z)) * third;
        mp[10] = ((e.X * e.X) + (e.Y * e.Y)) * third;
        mp[3] = c.X;
        mp[7] = c.Y;
        mp[11] = c.Z;
        hull.Flags |= 3;
        return hull;
    }

    private static readonly (byte, byte, byte, byte)[] BoxEdges =
    [
        (21, 1, 5, 0), (11, 0, 1, 5), (5, 3, 0, 0), (17, 2, 4, 3), (14, 5, 5, 1), (0, 4, 4, 0),
        (10, 7, 6, 1), (13, 6, 7, 2), (22, 9, 2, 2), (16, 8, 3, 4), (4, 11, 7, 1), (23, 10, 5, 5),
        (15, 13, 2, 3), (8, 12, 6, 2), (6, 15, 4, 1), (3, 14, 6, 3), (20, 17, 2, 4), (12, 16, 0, 3),
        (9, 19, 1, 4), (1, 18, 3, 5), (18, 21, 0, 4), (2, 20, 1, 0), (7, 23, 3, 2), (19, 22, 7, 5),
    ];

    /// <summary>The identity matrix3x4 a map builder hull shape carries.</summary>
    public static readonly float[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];

    /// <summary>
    /// resourcecompiler's FUN_1819595d0: the cooked hull moved by its shape's
    /// matrix (row-major 3x4). Even the identity matters, because adding its
    /// zero terms turns a -0 into +0. The region SVM planes it also moves are
    /// not built here.
    /// </summary>
    public static void Transform(RnHull hull, float[] m)
    {
        hull.Centroid = Point(m, hull.Centroid);
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = -float.MaxValue, maxY = -float.MaxValue, maxZ = -float.MaxValue;
        for (var i = 0; i < hull.VertexPositions.Length; i++)
        {
            var v = Point(m, hull.VertexPositions[i]);
            hull.VertexPositions[i] = v;
            if (maxY <= v.Y) maxY = v.Y;
            if (maxZ <= v.Z) maxZ = v.Z;
            if (v.X <= minX) minX = v.X;
            if (v.Y <= minY) minY = v.Y;
            if (v.Z <= minZ) minZ = v.Z;
            if (maxX <= v.X) maxX = v.X;
        }
        if (hull.VertexPositions.Length > 0)
        {
            hull.BoundsMin = new Vector3(minX, minY, minZ);
            hull.BoundsMax = new Vector3(maxX, maxY, maxZ);
        }
        else
        {
            hull.BoundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            hull.BoundsMax = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
        }
        for (var i = 0; i < hull.Planes.Length; i++)
        {
            var (n, d) = hull.Planes[i];
            var r = Rotate(m, n);
            hull.Planes[i] = (r, (((m[7] * r.Y) + (m[3] * r.X)) + (m[11] * r.Z)) + d);
        }
        Inertia(m, hull.MassProperties);
        var c = Point(m, new Vector3(hull.MassProperties[3], hull.MassProperties[7], hull.MassProperties[11]));
        hull.MassProperties[3] = c.X;
        hull.MassProperties[7] = c.Y;
        hull.MassProperties[11] = c.Z;
        hull.OrthographicAreas = Ortho(hull.OrthographicAreas, m);
        if ((hull.Flags & 2) != 0)
        {
            var mask = 0;
            foreach (var v in hull.VertexPositions)
            {
                var dx = v.X - ((maxX + minX) * 0.5f);
                var dy = v.Y - ((maxY + minY) * 0.5f);
                var dz = v.Z - ((maxZ + minZ) * 0.5f);
                if (MathF.Abs(MathF.Abs(dx) - ((maxX - minX) * 0.5f)) <= 0.03125f && MathF.Abs(MathF.Abs(dy) - ((maxY - minY) * 0.5f)) <= 0.03125f
                    && MathF.Abs(MathF.Abs(dz) - ((maxZ - minZ) * 0.5f)) <= 0.03125f)
                    mask |= 1 << ((SignBit(dx) ? 1 : 0) | (SignBit(dy) ? 2 : 0) | (SignBit(dz) ? 4 : 0));
            }
            hull.Flags = mask == 0xff ? hull.Flags | 1 : hull.Flags & ~1u;
        }
    }

    // FUN_18125d1f0: (t + y r1) + (x r0 + z r2) per row.
    private static Vector3 Point(float[] m, Vector3 v)
        => new(((m[1] * v.Y) + m[3]) + ((m[0] * v.X) + (m[2] * v.Z)),
               ((m[5] * v.Y) + m[7]) + ((m[4] * v.X) + (m[6] * v.Z)),
               ((m[9] * v.Y) + m[11]) + ((m[8] * v.X) + (m[10] * v.Z)));

    // FUN_18125d1b0: (x r0 + y r1) + z r2 per row.
    private static Vector3 Rotate(float[] m, Vector3 v)
        => new(((v.X * m[0]) + (v.Y * m[1])) + (v.Z * m[2]),
               ((v.X * m[4]) + (v.Y * m[5])) + (v.Z * m[6]),
               ((v.X * m[8]) + (v.Y * m[9])) + (v.Z * m[10]));

    // FUN_181a09ad0: R I R^T on the 3x4 mass block, grouped as the binary groups it.
    private static void Inertia(float[] m, float[] p)
    {
        float i1 = p[1], m4 = m[4], i5 = p[5], i0 = p[0], i6 = p[6], i2 = p[2], i10 = p[10];
        float m0 = m[0], m5 = m[5], m6 = m[6], m8 = m[8], m1 = m[1], m9 = m[9], m2 = m[2], m10 = m[10];
        var a24 = ((m5 * i5) + (m4 * i1)) + (m6 * i6);
        var a23 = ((m5 * i1) + (m4 * i0)) + (m6 * i2);
        var a22 = ((m5 * i6) + (m4 * i2)) + (m6 * i10);
        var a21 = ((m8 * a23) + (m9 * a24)) + (m10 * a22);
        var a20 = ((m0 * i1) + (m1 * i5)) + (m2 * i6);
        var a19 = ((m1 * i1) + (m0 * i0)) + (m2 * i2);
        var a18 = ((m0 * i2) + (m1 * i6)) + (m2 * i10);
        var a16 = ((m4 * a19) + (m5 * a20)) + (m6 * a18);
        var a17 = ((m8 * a19) + (m9 * a20)) + (m10 * a18);
        p[0] = ((m0 * a19) + (m1 * a20)) + (m2 * a18);
        p[1] = a16;
        p[2] = a17;
        p[4] = a16;
        p[5] = ((m4 * a23) + (m5 * a24)) + (m6 * a22);
        p[6] = a21;
        p[8] = a17;
        p[9] = a21;
        p[10] = (((((m9 * i5) + (m8 * i1)) + (m10 * i6)) * m9) + ((((m9 * i1) + (m8 * i0)) + (m10 * i2)) * m8))
                + ((((m9 * i6) + (m8 * i2)) + (m10 * i10)) * m10);
    }

    // FUN_181a09e20
    private static Vector3 Ortho(Vector3 o, float[] m)
    {
        var sum = (o.X + o.Y) + o.Z;
        if (!(1.1920929e-07f <= sum))
            return o;
        var d = MathF.Abs(o.X * m[4]) + MathF.Abs(o.Y * m[5]) + MathF.Abs(o.Z * m[6]) + MathF.Abs(o.Y * m[1])
                + MathF.Abs(o.X * m[0]) + MathF.Abs(o.Z * m[2]) + MathF.Abs(o.X * m[8]) + MathF.Abs(o.Y * m[9])
                + MathF.Abs(o.Z * m[10]);
        var f = sum / d;
        return new Vector3(o.X * f, o.Y * f, o.Z * f);
    }

    // FUN_1801302f0
    internal static void CentroidRadius(RnHull hull)
    {
        var min = float.MaxValue;
        var c = hull.Centroid;
        foreach (var (n, d) in hull.Planes)
        {
            var dist = -((((n.Z * c.Z) + (n.Y * c.Y)) + (n.X * c.X)) - d);
            if (dist <= min)
                min = dist;
        }
        hull.MinCentroidRadius = min;
    }

    // FUN_18012ffd0: area by fanning each face from its first corner.
    internal static void Areas(RnHull hull)
    {
        float area = 0f, ox = 0f, oy = 0f, oz = 0f;
        var p = hull.VertexPositions;
        var edges = hull.Edges;
        foreach (var fe in hull.Faces)
        {
            var first = fe;
            var a = p[edges[first].Origin];
            var b = edges[first].Next;
            var c = edges[b].Next;
            while (true)
            {
                var pb = p[edges[b].Origin];
                var pc = p[edges[c].Origin];
                var ux = pb.X - a.X;
                var uy = pb.Y - a.Y;
                var uz = pb.Z - a.Z;
                var vx = pc.X - a.X;
                var vy = pc.Y - a.Y;
                var vz = pc.Z - a.Z;
                var cxv = ((uy * vz) - (uz * vy)) * 0.5f;
                var cyv = ((vx * uz) - (ux * vz)) * 0.5f;
                var czv = ((ux * vy) - (vx * uy)) * 0.5f;
                var len = MathF.Sqrt(((czv * czv) + (cyv * cyv)) + (cxv * cxv));
                if (cyv <= 0f) cyv = 0f;
                if (czv <= 0f) czv = 0f;
                if (cxv <= 0f) cxv = 0f;
                area = area + len;
                var next = edges[c].Next;
                oy = oy + cyv;
                oz = oz + czv;
                ox = ox + cxv;
                b = c;
                c = next;
                if (first == next)
                    break;
            }
        }
        var dy = hull.BoundsMax.Y - hull.BoundsMin.Y;
        var dx = hull.BoundsMax.X - hull.BoundsMin.X;
        hull.SurfaceArea = area;
        var dz = hull.BoundsMax.Z - hull.BoundsMin.Z;
        ox = (1f / (dz * dy)) * ox;
        oy = (1f / (dz * dx)) * oy;
        if (1f <= ox) ox = 1f;
        oz = (1f / (dy * dx)) * oz;
        if (1f <= oy) oy = 1f;
        if (1f <= oz) oz = 1f;
        hull.OrthographicAreas = new Vector3(ox, oy, oz);
    }

    // FUN_180292700: volume, mass centre and inertia from the faces' fans.
    internal static void MassProperties(RnHull hull)
    {
        var c = hull.Centroid;
        float vol6 = 0f, sx = 0f, sy = 0f, sz = 0f, xx = 0f, yy = 0f, zz = 0f, xy = 0f, xz = 0f, yz = 0f;
        var p = hull.VertexPositions;
        var edges = hull.Edges;
        foreach (var first in hull.Faces)
        {
            var pa = p[edges[first].Origin];
            var ax = pa.X - c.X;
            var az = pa.Z - c.Z;
            var ay = pa.Y - c.Y;
            var e1 = edges[first].Next;
            var e2 = edges[e1].Next;
            byte cur;
            do
            {
                cur = e2;
                var pb = p[edges[e1].Origin];
                var pc = p[edges[cur].Origin];
                var bx = pb.X - c.X;
                var bz = pb.Z - c.Z;
                var by = pb.Y - c.Y;
                var cx = pc.X - c.X;
                var cz = pc.Z - c.Z;
                var cy = pc.Y - c.Y;
                var sumY = (ay + by) + cy;
                var sumZ = (az + bz) + cz;
                var det = ((((cz * by) - (cy * bz)) * ax) - (((ay * cz) - (az * cy)) * bx)) + (((ay * bz) - (az * by)) * cx);
                vol6 = vol6 + det;
                var sumX = (ax + bx) + cx;
                sx = sx + (sumX * det);
                sy = sy + (sumY * det);
                sz = sz + (sumZ * det);
                xx = xx + (((((bx * bx) + (ax * ax)) + (cx * cx)) + (sumX * sumX)) * det);
                e2 = edges[cur].Next;
                yy = yy + (((((by * by) + (ay * ay)) + (cy * cy)) + (sumY * sumY)) * det);
                zz = zz + (((((bz * bz) + (az * az)) + (cz * cz)) + (sumZ * sumZ)) * det);
                xy = xy + (((((by * bx) + (ax * ay)) + (cy * cx)) + (sumY * sumX)) * det);
                xz = xz + (((((bz * bx) + (ax * az)) + (cz * cx)) + (sumZ * sumX)) * det);
                yz = yz + (((((bz * by) + (az * ay)) + (cz * cy)) + (sumZ * sumY)) * det);
                e1 = cur;
            } while (first != e2);
        }
        var volume = vol6 / 6f;
        var r = 1f / (vol6 * 4f);
        sx = sx * r;
        sy = sy * r;
        sz = sz * r;
        var t = -volume * sx;
        var pxy = (-xy * 0.008333334f) - (t * sy);
        var pxz = (-xz * 0.008333334f) - (t * sz);
        var mp = hull.MassProperties;
        mp[0] = ((zz + yy) * 0.008333334f) - (((sy * sy) + (sz * sz)) * volume);
        var pyz = (-yz * 0.008333334f) - ((-volume * sy) * sz);
        mp[4] = pxy;
        mp[8] = pxz;
        mp[1] = pxy;
        mp[9] = pyz;
        mp[5] = ((zz + xx) * 0.008333334f) - (((sx * sx) + (sz * sz)) * volume);
        mp[2] = pxz;
        mp[6] = pyz;
        mp[10] = ((yy + xx) * 0.008333334f) - (((sx * sx) + (sy * sy)) * volume);
        mp[3] = c.X + sx;
        mp[11] = c.Z + sz;
        mp[7] = c.Y + sy;
        hull.Volume = volume;
    }
}
