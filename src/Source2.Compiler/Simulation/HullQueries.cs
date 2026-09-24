using Source2.Compiler.Physics;

namespace Source2.Compiler.Simulation;

public static partial class HullCollision
{
    /// <summary>
    /// One hull's frame seen from another's: M[i, j] = column i of the other's
    /// rotation dotted with column j of this one's, and D the translation
    /// difference in the other's axes. Every dot sums x, y, then z.
    /// </summary>
    private readonly struct Frame
    {
        private readonly float _m00, _m01, _m02, _m10, _m11, _m12, _m20, _m21, _m22;
        private readonly float _d0, _d1, _d2;

        /// <summary>Maps <paramref name="from"/>'s local space into <paramref name="to"/>'s.</summary>
        public Frame(in RnTransform from, in RnTransform to)
        {
            ref readonly var a = ref from.R;
            ref readonly var b = ref to.R;
            _m00 = Col(b, 0, a, 0); _m01 = Col(b, 0, a, 1); _m02 = Col(b, 0, a, 2);
            _m10 = Col(b, 1, a, 0); _m11 = Col(b, 1, a, 1); _m12 = Col(b, 1, a, 2);
            _m20 = Col(b, 2, a, 0); _m21 = Col(b, 2, a, 1); _m22 = Col(b, 2, a, 2);
            var dx = from.T.X - to.T.X;
            var dy = from.T.Y - to.T.Y;
            var dz = from.T.Z - to.T.Z;
            _d0 = (b.M0 * dx + b.M1 * dy) + b.M2 * dz;
            _d1 = (b.M3 * dx + b.M4 * dy) + b.M5 * dz;
            _d2 = (b.M6 * dx + b.M7 * dy) + b.M8 * dz;
        }

        private static float Col(in Mat3 b, int i, in Mat3 a, int j)
            => (b[3 * i] * a[3 * j] + b[3 * i + 1] * a[3 * j + 1]) + b[3 * i + 2] * a[3 * j + 2];

        public Vec3 Rotate(float x, float y, float z) => new(
            (_m00 * x + _m01 * y) + _m02 * z,
            (_m10 * x + _m11 * y) + _m12 * z,
            (_m20 * x + _m21 * y) + _m22 * z);

        public Vec3 Transform(float x, float y, float z) => new(
            ((_m00 * x + _m01 * y) + _m02 * z) + _d0,
            ((_m10 * x + _m11 * y) + _m12 * z) + _d1,
            ((_m20 * x + _m21 * y) + _m22 * z) + _d2);

        /// <summary>The translation term: where the from-origin sits in to-space.</summary>
        public Vec3 Offset => new(_d0, _d1, _d2);
    }

    /// <summary>
    /// The hull vertex furthest along a local direction (FUN_18028b1c0); the
    /// first of equals wins, and an empty hull gives 0.
    /// </summary>
    internal static int Support(RnHull hull, float x, float y, float z)
    {
        var best = -float.MaxValue;
        var index = 0;
        var v = hull.VertexPositions;
        for (var i = 0; i < v.Length; i++)
        {
            var d = (z * v[i].Z + y * v[i].Y) + x * v[i].X;
            if (d > best)
            {
                best = d;
                index = i;
            }
        }
        return index;
    }

    /// <summary>
    /// The deepest face of <paramref name="reference"/> against
    /// <paramref name="incident"/> (FUN_18028cc00): per face, the incident
    /// support point's distance above the face plane; the first maximum wins.
    /// </summary>
    internal static SatQuery FaceQuery(in RnTransform xfRef, HullRef reference, in RnTransform xfInc, HullRef incident)
    {
        var frame = new Frame(xfRef, xfInc);
        var best = new SatQuery { Separation = -float.MaxValue };
        var faces = reference.Hull.Faces.Length;
        for (var face = 0; face < faces; face++)
        {
            var s = FaceSeparation(frame, reference, face, incident, out var vertex);
            if (s > best.Separation)
                best = new SatQuery { Separation = s, Index1 = face, Index2 = vertex };
        }
        return best;
    }

    /// <summary>One face's separation, as FUN_18028cc00 and FUN_1802f62d0 compute it.</summary>
    internal static float FaceSeparation(in RnTransform xfRef, HullRef reference, int face,
                                         in RnTransform xfInc, HullRef incident, out int vertex)
        => FaceSeparation(new Frame(xfRef, xfInc), reference, face, incident, out vertex);

    private static float FaceSeparation(in Frame frame, HullRef reference, int face, HullRef incident, out int vertex)
    {
        var (normal, offset) = reference.Hull.Planes[face];
        var n = frame.Rotate(normal.X, normal.Y, normal.Z);
        vertex = Support(incident.Hull, -n.X, -n.Y, -n.Z);
        var v = incident.Hull.VertexPositions[vertex];
        var s = incident.Scale;
        var p = frame.Offset;
        return (((s * v.Y) * n.Y + (s * v.Z) * n.Z) + (s * v.X) * n.X)
             - (((n.X * p.X + n.Y * p.Y) + n.Z * p.Z) + offset * reference.Scale);
    }

    /// <summary>An edge of A in B's space, with its two faces' normals.</summary>
    private readonly record struct EdgeInB(Vec3 P, Vec3 E, Vec3 NormalA, Vec3 NormalB);

    private static EdgeInB EdgeOfA(in Frame frame, HullRef a, int edge, int other)
    {
        var h = a.Hull;
        var s = a.Scale;
        var v0 = h.VertexPositions[h.Edges[edge].Origin];
        var v1 = h.VertexPositions[h.Edges[other].Origin];
        var p = frame.Transform(s * v0.X, s * v0.Y, s * v0.Z);
        var q = frame.Transform(s * v1.X, s * v1.Y, s * v1.Z);
        var na = h.Planes[h.Edges[edge].Face].Normal;
        var nb = h.Planes[h.Edges[other].Face].Normal;
        return new EdgeInB(p, new Vec3(q.X - p.X, q.Y - p.Y, q.Z - p.Z),
                           frame.Rotate(na.X, na.Y, na.Z), frame.Rotate(nb.X, nb.Y, nb.Z));
    }

    /// <summary>
    /// The best edge pair (FUN_18028bbd0), in B's space. Half-edges are taken
    /// in pairs (i, i + 1); a pair counts only if its arcs cross on the Gauss
    /// map, and parallel edges give -FLT_MAX. The first maximum wins.
    /// </summary>
    internal static SatQuery EdgeQuery(in RnTransform xfA, HullRef a, in RnTransform xfB, HullRef b)
    {
        var frame = new Frame(xfA, xfB);
        var c = a.Hull.Centroid;
        var centre = frame.Transform(a.Scale * c.X, a.Scale * c.Y, a.Scale * c.Z);
        var best = new SatQuery { Separation = -float.MaxValue };
        var edgesA = a.Hull.Edges.Length;
        var edgesB = b.Hull.Edges.Length;
        for (var i = 0; i < edgesA; i += 2)
        {
            var ea = EdgeOfA(frame, a, i, i + 1);
            for (var j = 0; j < edgesB; j += 2)
            {
                if (!EdgePairSeparation(ea, centre, b, j, j + 1, out var s))
                    continue;
                if (s > best.Separation)
                    best = new SatQuery { Separation = s, Index1 = i, Index2 = j };
            }
        }
        return best;
    }

    /// <summary>
    /// The cached edge pair's separation (the edge half of FUN_1802f62d0):
    /// edges i1 of A and i2 of B with their twins. False when they no longer
    /// form a face of the Minkowski difference.
    /// </summary>
    internal static bool EdgeSeparation(in RnTransform xfA, HullRef a, int edgeA, in RnTransform xfB, HullRef b,
                                        int edgeB, out float separation)
    {
        var frame = new Frame(xfA, xfB);
        var c = a.Hull.Centroid;
        var centre = frame.Transform(a.Scale * c.X, a.Scale * c.Y, a.Scale * c.Z);
        var ea = EdgeOfA(frame, a, edgeA, a.Hull.Edges[edgeA].Twin);
        return EdgePairSeparation(ea, centre, b, edgeB, b.Hull.Edges[edgeB].Twin, out separation);
    }

    private static bool EdgePairSeparation(in EdgeInB ea, Vec3 centre, HullRef b, int edge, int other,
                                           out float separation)
    {
        separation = 0f;
        var h = b.Hull;
        var s = b.Scale;
        var v0 = h.VertexPositions[h.Edges[edge].Origin];
        var v1 = h.VertexPositions[h.Edges[other].Origin];
        var p = new Vec3(s * v0.X, s * v0.Y, s * v0.Z);
        var ebx = s * v1.X - p.X;
        var eby = s * v1.Y - p.Y;
        var ebz = s * v1.Z - p.Z;
        var c = h.Planes[h.Edges[edge].Face].Normal;
        var d = h.Planes[h.Edges[other].Face].Normal;

        // Gauss map arcs a-b (A's faces) and -c..-d (B's) must cross.
        var e = ea.E;
        var cba = ((-c.Y) * (-e.Y) - c.X * (-e.X)) - c.Z * (-e.Z);
        var bdc = ((-eby) * ea.NormalB.Y + (-ebx) * ea.NormalB.X) + (-ebz) * ea.NormalB.Z;
        if (!(bdc * cba > 0f))
            return false;
        var dba = ((-e.Y) * (-d.Y) + (-e.X) * (-d.X)) + (-e.Z) * (-d.Z);
        if (!(0f > dba * cba))
            return false;
        var adc = ((-eby) * ea.NormalA.Y + (-ebx) * ea.NormalA.X) + (-ebz) * ea.NormalA.Z;
        if (!(0f > adc * bdc))
            return false;

        var nx = ebz * e.Y - eby * e.Z;
        var ny = ebx * e.Z - ebz * e.X;
        var nz = eby * e.X - ebx * e.Y;
        var length = MathF.Sqrt((nx * nx + ny * ny) + nz * nz);
        var limit = MathF.Sqrt(((e.X * e.X + e.Y * e.Y) + e.Z * e.Z) * ((ebx * ebx + eby * eby) + ebz * ebz));
        if (limit * 0.005f >= length)
        {
            separation = -float.MaxValue;
            return true;
        }
        var inv = 1f / length;
        nx *= inv;
        ny *= inv;
        nz *= inv;
        var q = ea.P;
        if (0f > ((q.X - centre.X) * nx + (q.Y - centre.Y) * ny) + (q.Z - centre.Z) * nz)
        {
            nx = -nx;
            ny = -ny;
            nz = -nz;
        }
        separation = ((p.X - q.X) * nx + (p.Y - q.Y) * ny) + (p.Z - q.Z) * nz;
        return true;
    }
}
