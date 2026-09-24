using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

/// <summary>
/// A point of the clipped contact polygon (0x14 bytes): world position, its
/// signed distance above the reference plane, and the feature id bytes
/// {type in, index in, type out, index out} (type 1 incident edge, 0 reference).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 0x14)]
public struct ClipPoint
{
    public float X, Y, Z, W;
    public uint Feature;
}

/// <summary>A plane n · x = d (0x10 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Size = 0x10)]
public struct ClipPlane
{
    public float X, Y, Z, D;

    public ClipPlane(float x, float y, float z, float d) { X = x; Y = y; Z = z; D = d; }
}

public static partial class HullCollision
{
    /// <summary>Valve's polygon buffers hold 256 points; ours hold twice that.</summary>
    private const int PolygonCapacity = 512;

    /// <summary>Degenerate squared length (0x1803fdcd4).</summary>
    private const float TinySquared = 1.17549435e-35f;

    /// <summary>
    /// The face contact (FUN_1802ee580): the incident face of the other hull is
    /// clipped by the reference face's side planes (pushed out by the
    /// speculative distance), culled and reduced to four points. With
    /// <paramref name="flip"/> the reference hull is B and the normal and
    /// features are turned round so the manifold still reads A to B.
    /// </summary>
    internal static bool FaceContact(ReadOnlySpan<CachedManifold> old, ref CachedManifold result,
                                     in RnTransform xfRef, HullRef reference, in RnTransform xfInc, HullRef incident,
                                     in SatQuery query, bool flip, ref SatCache cache, int triangle)
    {
        var hull = reference.Hull;
        var (pn, pd) = hull.Planes[query.Index1];
        ref readonly var r = ref xfRef.R;
        var nx = (pn.X * r.M0 + pn.Y * r.M3) + pn.Z * r.M6;
        var ny = (pn.X * r.M1 + pn.Y * r.M4) + pn.Z * r.M7;
        var nz = (pn.X * r.M2 + pn.Y * r.M5) + pn.Z * r.M8;
        var plane = new ClipPlane(nx, ny, nz, ((nx * xfRef.T.X + ny * xfRef.T.Y) + nz * xfRef.T.Z) + pd * reference.Scale);

        var face = IncidentFace(xfInc, incident, nx, ny, nz, query.Index2);
        Span<ClipPoint> src = stackalloc ClipPoint[PolygonCapacity];
        Span<ClipPoint> dst = stackalloc ClipPoint[PolygonCapacity];
        var count = IncidentPolygon(xfInc, incident, face, plane, src);

        // Clip by each side plane of the reference face; stop below a triangle.
        var s = reference.Scale;
        var start = hull.Faces[query.Index1];
        int edge = start;
        do
        {
            var next = hull.Edges[edge].Next;
            var v0 = hull.VertexPositions[hull.Edges[edge].Origin];
            var v1 = hull.VertexPositions[hull.Edges[next].Origin];
            var p = ToWorld(xfRef, s * v0.X, s * v0.Y, s * v0.Z);
            var q = ToWorld(xfRef, s * v1.X, s * v1.Y, s * v1.Z);
            var ex = q.X - p.X;
            var ey = q.Y - p.Y;
            var ez = q.Z - p.Z;
            var lengthSquared = (ex * ex + ey * ey) + ez * ez;
            if (lengthSquared > TinySquared)
            {
                var inv = 1f / MathF.Sqrt(lengthSquared);
                ex *= inv;
                ey *= inv;
                ez *= inv;
            }
            else
            {
                ex = ey = ez = 0f;
            }
            var sx = ey * nz - ez * ny;
            var sy = ez * nx - ex * nz;
            var sz = ex * ny - ey * nx;
            var side = new ClipPlane(sx, sy, sz, ((p.Z * sz + p.Y * sy) + p.X * sx) + Speculative);
            count = Clip(dst, src[..count], side, edge, plane);
            var swap = src;
            src = dst;
            dst = swap;
            edge = next;
        }
        while (count >= 3 && edge != start);

        count = Reduce(src, count, plane, Speculative);
        if (count == 0)
        {
            cache.Type = 0;
            cache.Separation = 0f;
            return false;
        }

        var normal = flip ? new Vec3(-nx, -ny, -nz) : new Vec3(nx, ny, nz);
        result.Normal = normal;
        result.Centre = Centroid(src[..count]);
        (result.T1, result.T2) = Tangents(normal);
        TransferFriction(ref result, old);
        result.PointCount = 0;
        for (var k = 0; k < count; k++)
        {
            ref readonly var c = ref src[k];
            var w = c.W;
            var ix = nx * w + c.X;
            var iy = ny * w + c.Y;
            var iz = nz * w + c.Z;
            var feature = flip ? FlipFeature(c.Feature) : c.Feature;
            var impulse = WarmImpulse(old, feature);
            ref var point = ref result.Points[result.PointCount++];
            point.LocalA = flip ? ToLocal(xfInc, ix, iy, iz) : ToLocal(xfRef, c.X, c.Y, c.Z);
            point.LocalB = flip ? ToLocal(xfRef, c.X, c.Y, c.Z) : ToLocal(xfInc, ix, iy, iz);
            SetPoint(ref point, impulse, feature, triangle);
        }
        cache.Type = flip ? 2 : 1;
        cache.Index1 = query.Index1;
        cache.Index2 = query.Index2;
        cache.Separation = query.Separation;
        return true;
    }

    /// <summary>
    /// The edge contact (FUN_1802ed320): one point at the closest points of
    /// the two edges' lines, if both lie within their segments.
    /// </summary>
    internal static bool EdgeContact(ReadOnlySpan<CachedManifold> old, ref CachedManifold result,
                                     in RnTransform xfA, HullRef a, in RnTransform xfB, HullRef b,
                                     in SatQuery query, ref SatCache cache, int triangle)
    {
        if (query.Separation != -float.MaxValue)
        {
            var (pa, ea) = WorldEdge(xfA, a, query.Index1);
            var (pb, eb) = WorldEdge(xfB, b, query.Index2);
            var nx = ea.Y * eb.Z - ea.Z * eb.Y;
            var ny = ea.Z * eb.X - ea.X * eb.Z;
            var nz = ea.X * eb.Y - ea.Y * eb.X;
            var lengthSquared = (nx * nx + ny * ny) + nz * nz;
            if (lengthSquared > TinySquared)
            {
                var inv = 1f / MathF.Sqrt(lengthSquared);
                nx *= inv;
                ny *= inv;
                nz *= inv;
            }
            else
            {
                nx = ny = nz = 0f;
            }
            var c = a.Hull.Centroid;
            var centre = ToWorld(xfA, a.Scale * c.X, a.Scale * c.Y, a.Scale * c.Z);
            if (0f > ((pa.X - centre.X) * nx + (pa.Y - centre.Y) * ny) + (pa.Z - centre.Z) * nz)
            {
                nx = -nx;
                ny = -ny;
                nz = -nz;
            }
            var (ca, sa, cb, sb) = ClosestPoints(pa, ea, pb, eb);
            if (sa >= 0f && 1f >= sa && sb >= 0f && 1f >= sb)
            {
                var feature = Feature(0, query.Index1, 1, query.Index2);
                var impulse = WarmImpulse(old, feature);
                var normal = new Vec3(nx, ny, nz);
                result.Centre = ca;
                result.Normal = normal;
                (result.T1, result.T2) = Tangents(normal);
                TransferFriction(ref result, old);
                result.PointCount = 1;
                ref var point = ref result.P0;
                point.LocalA = ToLocal(xfA, ca.X, ca.Y, ca.Z);
                point.LocalB = ToLocal(xfB, cb.X, cb.Y, cb.Z);
                SetPoint(ref point, impulse, feature, triangle);
                cache.Type = 3;
                cache.Index1 = query.Index1;
                cache.Index2 = query.Index2;
                cache.Separation = query.Separation;
                return true;
            }
        }
        cache.Type = 0;
        cache.Separation = 0f;
        return false;
    }

    /// <summary>An edge's world start and its vector to the twin's origin.</summary>
    private static (Vec3 P, Vec3 E) WorldEdge(in RnTransform xf, HullRef h, int edge)
    {
        var hull = h.Hull;
        var s = h.Scale;
        var v0 = hull.VertexPositions[hull.Edges[edge].Origin];
        var v1 = hull.VertexPositions[hull.Edges[hull.Edges[edge].Twin].Origin];
        var p = ToWorld(xf, s * v0.X, s * v0.Y, s * v0.Z);
        var q = ToWorld(xf, s * v1.X, s * v1.Y, s * v1.Z);
        return (p, new Vec3(q.X - p.X, q.Y - p.Y, q.Z - p.Z));
    }

    /// <summary>
    /// The old point with the same feature, first match (FUN_1802ee580's scan):
    /// the first old point is tested even when the old count is 0. -1 for none.
    /// </summary>
    private static float WarmImpulse(ReadOnlySpan<CachedManifold> old, uint feature)
    {
        if (old.IsEmpty)
            return -1f;
        var points = MemoryMarshal.CreateReadOnlySpan(in old[0].P0, 4);
        var k = 0;
        do
        {
            if ((uint)points[k].Feature == feature)
                return points[k].Impulse;
            k++;
        }
        while (k < old[0].PointCount);
        return -1f;
    }

    /// <summary>The point's impulse, feature and flags; bytes 0x26..0x27 are left alone.</summary>
    private static void SetPoint(ref CachedPoint point, float impulse, uint feature, int triangle)
    {
        point.Impulse = 0f > impulse ? 0f : impulse;
        point.Feature = (int)feature;
        point.SubShape = triangle;
        ref var flags = ref Unsafe.As<int, byte>(ref point.Reserved);
        flags = 0;
        Unsafe.Add(ref flags, 1) = (byte)(0f > impulse ? 1 : 0);
    }

    /// <summary>Feature id bytes {a, b, c, d} (FUN_1803bca10).</summary>
    internal static uint Feature(int a, int b, int c, int d)
        => (uint)(byte)a | (uint)(byte)b << 8 | (uint)(byte)c << 16 | (uint)(byte)d << 24;

    /// <summary>The same feature seen from the other shape (FUN_1803bc9d0): {1 - b2, b3, 1 - b0, b1}.</summary>
    internal static uint FlipFeature(uint f)
        => Feature(1 - (int)(f >> 16 & 0xff), (int)(f >> 24), 1 - (int)(f & 0xff), (int)(f >> 8 & 0xff));

    internal static Vec3 ToWorld(in RnTransform xf, float x, float y, float z)
    {
        ref readonly var r = ref xf.R;
        return new(((x * r.M0 + y * r.M3) + z * r.M6) + xf.T.X,
                   ((x * r.M1 + y * r.M4) + z * r.M7) + xf.T.Y,
                   ((x * r.M2 + y * r.M5) + z * r.M8) + xf.T.Z);
    }

    internal static Vec3 ToLocal(in RnTransform xf, float x, float y, float z)
    {
        ref readonly var r = ref xf.R;
        var dx = x - xf.T.X;
        var dy = y - xf.T.Y;
        var dz = z - xf.T.Z;
        return new((dx * r.M0 + dy * r.M1) + dz * r.M2,
                   (dx * r.M3 + dy * r.M4) + dz * r.M5,
                   (dx * r.M6 + dy * r.M7) + dz * r.M8);
    }

    /// <summary>
    /// The incident face (FUN_1803372b0): around the support vertex, the edge
    /// most perpendicular to the reference normal (first minimum), then of its
    /// two faces the one more against the normal (its twin's on a tie).
    /// </summary>
    internal static int IncidentFace(in RnTransform xf, HullRef h, float nx, float ny, float nz, int vertex)
    {
        ref readonly var r = ref xf.R;
        var lx = (nx * r.M0 + ny * r.M1) + nz * r.M2;
        var ly = (nx * r.M3 + ny * r.M4) + nz * r.M5;
        var lz = (nx * r.M6 + ny * r.M7) + nz * r.M8;
        var hull = h.Hull;
        var s = h.Scale;
        int start = hull.Vertices[vertex];
        var o = hull.VertexPositions[hull.Edges[start].Origin];
        var ox = s * o.X;
        var oy = s * o.Y;
        var oz = s * o.Z;
        var best = float.MaxValue;
        var bestEdge = 0;
        var edge = start;
        do
        {
            var twin = hull.Edges[edge].Twin;
            var v = hull.VertexPositions[hull.Edges[twin].Origin];
            var ux = s * v.X - ox;
            var uy = s * v.Y - oy;
            var uz = s * v.Z - oz;
            var lengthSquared = (ux * ux + uy * uy) + uz * uz;
            if (lengthSquared > TinySquared)
            {
                var inv = 1f / MathF.Sqrt(lengthSquared);
                ux *= inv;
                uy *= inv;
                uz *= inv;
            }
            else
            {
                ux = uy = uz = 0f;
            }
            var d = MathF.Abs((lx * ux + ly * uy) + lz * uz);
            if (best > d)
            {
                best = d;
                bestEdge = edge;
            }
            edge = hull.Edges[twin].Next;
        }
        while (edge != start);

        int faceA = hull.Edges[bestEdge].Face;
        int faceB = hull.Edges[hull.Edges[bestEdge].Twin].Face;
        var a = hull.Planes[faceA].Normal;
        var b = hull.Planes[faceB].Normal;
        var dotA = (lx * a.X + ly * a.Y) + lz * a.Z;
        var dotB = (lx * b.X + ly * b.Y) + lz * b.Z;
        return dotB > dotA ? faceA : faceB;
    }

    /// <summary>
    /// The incident face's polygon in world space (FUN_180336c80): per edge e,
    /// the end vertex, its height above the reference plane, and feature
    /// {1, e, 1, next(e)}. Returns the count.
    /// </summary>
    internal static int IncidentPolygon(in RnTransform xf, HullRef h, int face, in ClipPlane plane, Span<ClipPoint> polygon)
    {
        var hull = h.Hull;
        var s = h.Scale;
        var count = 0;
        int start = hull.Faces[face];
        var edge = start;
        do
        {
            int next = hull.Edges[edge].Next;
            var v = hull.VertexPositions[hull.Edges[next].Origin];
            var p = ToWorld(xf, s * v.X, s * v.Y, s * v.Z);
            polygon[count++] = new ClipPoint
            {
                X = p.X, Y = p.Y, Z = p.Z,
                W = ((plane.Z * p.Z + plane.Y * p.Y) + p.X * plane.X) - plane.D,
                Feature = Feature(1, edge, 1, next),
            };
            edge = next;
        }
        while (edge != start);
        return count;
    }

    private static float Distance(in ClipPoint p, in ClipPlane plane)
        => ((p.Y * plane.Y + p.Z * plane.Z) + p.X * plane.X) - plane.D;

    /// <summary>
    /// Sutherland-Hodgman against one side plane, keeping distance &lt;= 0
    /// (FUN_1803bb310). Crossings get the reference plane height recomputed
    /// and a feature naming the clipping edge. Returns the count.
    /// </summary>
    internal static int Clip(Span<ClipPoint> output, ReadOnlySpan<ClipPoint> input, in ClipPlane side, int edge,
                             in ClipPlane plane)
    {
        var count = 0;
        if (input.Length <= 0)
            return 0;
        var prev = input[^1];
        var dPrev = Distance(prev, side);
        for (var i = 0; i < input.Length; i++)
        {
            var cur = input[i];
            var dCur = Distance(cur, side);
            if (dPrev <= 0f && dCur <= 0f)
            {
                output[count++] = cur;
            }
            else if (dPrev <= 0f && dCur > 0f)
            {
                output[count++] = Cut(prev, cur, dPrev, dCur, (cur.Feature & 0xffff) | (uint)(byte)edge << 24, plane);
            }
            else if (dCur <= 0f && dPrev > 0f)
            {
                output[count++] = Cut(prev, cur, dPrev, dCur, (prev.Feature & 0xffff0000) | (uint)(byte)edge << 8, plane);
                output[count++] = cur;
            }
            prev = cur;
            dPrev = dCur;
        }
        return count;
    }

    private static ClipPoint Cut(in ClipPoint prev, in ClipPoint cur, float dPrev, float dCur, uint feature, in ClipPlane plane)
    {
        var t = dPrev / (dPrev - dCur);
        var p = new ClipPoint
        {
            X = (cur.X - prev.X) * t + prev.X,
            Y = (cur.Y - prev.Y) * t + prev.Y,
            Z = (cur.Z - prev.Z) * t + prev.Z,
            Feature = feature,
        };
        p.W = Distance(p, plane);
        return p;
    }

    /// <summary>The mean of the points (FUN_1803bb1f0).</summary>
    internal static Vec3 Centroid(ReadOnlySpan<ClipPoint> points)
    {
        float x = 0f, y = 0f, z = 0f;
        foreach (ref readonly var p in points)
        {
            x += p.X;
            y += p.Y;
            z += p.Z;
        }
        var inv = 1f / points.Length;
        return new(x * inv, y * inv, z * inv);
    }

    /// <summary>
    /// The friction basis of a unit normal (FUN_180321cc0): t1 lies in the
    /// plane of the normal's two smaller-weighted axes, t2 = n x t1.
    /// </summary>
    internal static (Vec3 T1, Vec3 T2) Tangents(Vec3 n)
    {
        if (MathF.Abs(n.Z) > MathF.Sqrt(0.5f))
        {
            var lengthSquared = n.Z * n.Z + n.Y * n.Y;
            var inv = 1f / MathF.Sqrt(lengthSquared);
            var t1 = new Vec3(0f, -n.Z * inv, n.Y * inv);
            return (t1, new Vec3(inv * lengthSquared, -n.X * t1.Z, n.X * t1.Y));
        }
        else
        {
            var lengthSquared = n.Y * n.Y + n.X * n.X;
            var inv = 1f / MathF.Sqrt(lengthSquared);
            var t1 = new Vec3(-n.Y * inv, n.X * inv, 0f);
            return (t1, new Vec3(-n.Z * t1.Y, n.Z * t1.X, inv * lengthSquared));
        }
    }

    /// <summary>
    /// Carries last step's friction impulses into the new basis
    /// (FUN_1802f61a0): the old tangent impulse vector projected on the new
    /// tangents, and the twist scaled by the normals' cosine.
    /// </summary>
    internal static void TransferFriction(ref CachedManifold m, ReadOnlySpan<CachedManifold> old)
    {
        if (old.IsEmpty)
        {
            m.Impulse1 = 0f;
            m.Impulse2 = 0f;
            m.TwistImpulse = 0f;
            return;
        }
        ref readonly var o = ref old[0];
        var i1 = o.Impulse1;
        var i2 = o.Impulse2;
        var fx = i2 * o.T2.X + i1 * o.T1.X;
        var fy = i2 * o.T2.Y + i1 * o.T1.Y;
        var fz = i2 * o.T2.Z + i1 * o.T1.Z;
        m.Impulse1 = (fx * m.T1.X + fy * m.T1.Y) + fz * m.T1.Z;
        m.Impulse2 = (fx * m.T2.X + fy * m.T2.Y) + fz * m.T2.Z;
        var tw = o.TwistImpulse;
        m.TwistImpulse = ((o.Normal.Y * tw) * m.Normal.Y + (tw * o.Normal.X) * m.Normal.X) + (o.Normal.Z * tw) * m.Normal.Z;
    }

    /// <summary>The smallest separation of the manifold's points (FUN_1802f59c0).</summary>
    internal static float MinSeparation(in RnTransform xfA, in RnTransform xfB, in CachedManifold m)
    {
        var min = float.MaxValue;
        var points = MemoryMarshal.CreateReadOnlySpan(in m.P0, 4);
        for (var k = 0; k < m.PointCount; k++)
        {
            var la = points[k].LocalA;
            var lb = points[k].LocalB;
            var pb = ToWorld(xfB, lb.X, lb.Y, lb.Z);
            var pa = ToWorld(xfA, la.X, la.Y, la.Z);
            var s = ((pb.X - pa.X) * m.Normal.X + (pb.Y - pa.Y) * m.Normal.Y) + (pb.Z - pa.Z) * m.Normal.Z;
            min = min < s ? min : s;
        }
        return min;
    }

    /// <summary>
    /// Closest points of the lines p1 + s d1 and p2 + t d2 (FUN_180321270);
    /// near-parallel lines take t = 0 and project p2 on the first.
    /// </summary>
    internal static (Vec3 A, float S, Vec3 B, float T) ClosestPoints(Vec3 p1, Vec3 d1, Vec3 p2, Vec3 d2)
    {
        var a = (d1.X * d1.X + d1.Y * d1.Y) + d1.Z * d1.Z;
        var b = (d2.X * d1.X + d2.Y * d1.Y) + d2.Z * d1.Z;
        var negB = -b;
        var negE = -((d2.X * d2.X + d2.Y * d2.Y) + d2.Z * d2.Z);
        var rx = p1.X - p2.X;
        var ry = p1.Y - p2.Y;
        var rz = p1.Z - p2.Z;
        var c = -((d1.X * rx + d1.Y * ry) + d1.Z * rz);
        var f = -((d2.X * rx + d2.Y * ry) + d2.Z * rz);
        var denom = negE * a - b * negB;
        float s, t;
        Vec3 onB;
        if (TinySquared > denom * denom)
        {
            t = 0f;
            onB = new Vec3(d2.X * 0f + p2.X, d2.Y * 0f + p2.Y, d2.Z * 0f + p2.Z);
            s = (((p2.X - p1.X) * d1.X + (p2.Y - p1.Y) * d1.Y) + (p2.Z - p1.Z) * d1.Z) / a;
        }
        else
        {
            t = (f * a - c * b) / denom;
            s = (c * negE - f * negB) / denom;
            onB = new Vec3(d2.X * t + p2.X, d2.Y * t + p2.Y, d2.Z * t + p2.Z);
        }
        return (new Vec3(d1.X * s + p1.X, d1.Y * s + p1.Y, d1.Z * s + p1.Z), s, onB, t);
    }
}
