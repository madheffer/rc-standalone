using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// meshutils' polygon triangulation (<c>triangulatepolygon.cpp</c>), which the
/// compile runs on every map mesh face: <c>FUN_18136f7a0</c> takes the face's
/// Newell normal (<c>FUN_18125b510</c>) and hands it with the corners to
/// <c>FUN_18136f810</c>. A quad is split along one diagonal (<c>FUN_18136c790</c>
/// scores the two); anything larger is ear clipped (<c>FUN_181370790</c>
/// scores an ear). Every float operation is in the binary's order, because
/// ear scores tie on symmetric faces and the first index wins.
///
/// <para>Scored against Valve's <c>.rte</c> on atixref, cardtest and probe01:
/// 6,087 faces of four or more corners, the same triangles in the same corner
/// order on every one whose mesh is not rotated.</para>
/// </summary>
public static class PolygonTriangulator
{
    /// <summary>
    /// The face's triangles as corner indices, three per triangle, in the
    /// order the compile emits them; empty when the binary gives up (a
    /// degenerate quad, or no ear left to clip).
    /// </summary>
    public static int[] Triangulate(ReadOnlySpan<Vector3> points)
    {
        var n = points.Length;
        if (n < 3)
            return [];
        var normal = Newell(points);
        if (n == 4)
            return Quad(points, normal);

        var list = new List<int>(n);
        var scores = new List<float>(n);
        for (var k = 0; k < n; k++)
            list.Add(k);
        for (var k = 0; k < n; k++)
            scores.Add(Ear(points, k, list, normal));

        var found = new List<int>((n - 2) * 3);
        while (true)
        {
            // The first strictly greatest score above zero; -1 marks a non-ear.
            var best = 0f;
            var at = -1;
            for (var k = 0; k < n; k++)
            {
                if (best < scores[k])
                {
                    best = scores[k];
                    at = k;
                }
            }
            if (at == -1)
                return [];
            found.Add(list[(n - 1 + at) % n]);
            found.Add(list[at]);
            found.Add(list[(at + 1) % n]);
            list.RemoveAt(at);
            scores.RemoveAt(at);
            n--;
            if (n < 3)
                return [.. found];
            scores[(n - 1 + at) % n] = Ear(points, (n - 1 + at) % n, list, normal);
            scores[at % n] = Ear(points, at % n, list, normal);
        }
    }

    // FUN_18125b510: the Newell normal, scaled by 1 / (length + FLT_EPSILON).
    private static Vector3 Newell(ReadOnlySpan<Vector3> p)
    {
        float x = 0f, y = 0f, z = 0f;
        for (var i = 0; i < p.Length; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Length];
            x += (a.Y - b.Y) * (a.Z + b.Z);
            y += (a.X + b.X) * (a.Z - b.Z);
            z += (a.X - b.X) * (a.Y + b.Y);
        }
        var r = 1f / (MathF.Sqrt((y * y) + (z * z) + (x * x)) + 1.1920929e-07f);
        return new Vector3(x * r, y * r, z * r);
    }

    // The quad: v0-v2 unless v1-v3 makes the better pair of triangles, or
    // v0-v2's squared diagonal is over 1.01 times v1-v3's. When only one split
    // is sound, a corner within 1e-7 of the line through its neighbours drops
    // the face.
    private static int[] Quad(ReadOnlySpan<Vector3> p, Vector3 n)
    {
        var (q0, k0) = Split(p, 0, 1, 2, 0, 2, 3, 0, 2, n);
        var (q1, k1) = Split(p, 0, 1, 3, 1, 2, 3, 1, 3, n);
        bool ok0 = (k0 & 2) != 0, ok1 = (k1 & 2) != 0;
        if (!ok0 && !ok1)
            return [];
        if (!(ok0 && ok1))
        {
            for (var k = 2; k < 6; k++)
            {
                if (SegmentDistance(p[k - 2], p[(k - 1) & 3], p[k & 3]) < 1e-7f
                    || SegmentDistance(p[k - 2], p[k & 3], p[(k + 1) & 3]) < 1e-7f)
                    return [];
            }
        }
        return k1 <= k0 && q0 <= q1 * 1.01f ? [0, 1, 2, 0, 2, 3] : [0, 1, 3, 1, 2, 3];
    }

    // FUN_18136c790: one split's squared diagonal, and 3 when both triangles
    // face along the normal, 2 when not, 0 when a triangle or the diagonal is
    // degenerate.
    private static (float Diagonal, int Kind) Split(ReadOnlySpan<Vector3> p, int a, int b, int c, int d, int e, int g,
                                                   int i, int j, Vector3 n)
    {
        float f9 = p[b].Z - p[a].Z, f11 = p[c].X - p[a].X, f14 = p[b].X - p[a].X;
        float f12 = p[b].Y - p[a].Y, f8 = p[c].Z - p[a].Z, f16 = p[c].Y - p[a].Y;
        float firstY = (f11 * f9) - (f8 * f14), firstX = (f8 * f12) - (f16 * f9), firstZ = (f16 * f14) - (f11 * f12);
        float f10 = p[e].Z - p[d].Z, f15 = p[e].X - p[d].X, f13 = p[e].Y - p[d].Y;
        float gx = p[g].X - p[d].X, gy = p[g].Y - p[d].Y, gz = p[g].Z - p[d].Z;
        float secondX = (gz * f13) - (gy * f10), secondY = (gx * f10) - (gz * f15), secondZ = (gy * f15) - (gx * f13);
        float dx = p[i].X - p[j].X, dz = p[i].Z - p[j].Z, dy = p[i].Y - p[j].Y;
        var diagonal = (dy * dy) + (dx * dx) + (dz * dz);
        if ((firstY * firstY) + (firstX * firstX) + (firstZ * firstZ) < 1e-14f
            || (secondY * secondY) + (secondX * secondX) + (secondZ * secondZ) < 1e-14f
            || diagonal < 1e-14f)
            return (0f, 0);
        var facing = !((n.Z * firstZ) + (n.Y * firstY) + (n.X * firstX) <= 0f
                       || (n.Z * secondZ) + (n.Y * secondY) + (n.X * secondX) <= 0f);
        return (diagonal, facing ? 3 : 2);
    }

    // FUN_181370790: an ear's score, or -1 when the corner is reflex (the
    // triangle's area along the normal under 1e-7) or another corner lies in
    // it. The score rewards a large smallest area among this and the two
    // neighbouring ears, and angles that are not sharp.
    private static float Ear(ReadOnlySpan<Vector3> p, int i, List<int> list, Vector3 n)
    {
        var count = list.Count;
        int ip = (i + count - 1) % count, ic = i % count, inext = (i + 1) % count;
        Vector3 prev = p[list[ip]], cur = p[list[ic]], next = p[list[inext]];
        float ax = cur.X - prev.X, ay = cur.Y - prev.Y, az = cur.Z - prev.Z;
        float bx = next.X - prev.X, by = next.Y - prev.Y, bz = next.Z - prev.Z;
        var smallest = ((((bx * az) - (bz * ax)) * n.Y) + (((by * ax) - (bx * ay)) * n.Z)) + (((bz * ay) - (by * az)) * n.X);
        if (smallest < 1e-7f)
            return -1f;
        for (var k = 0; k < count; k++)
        {
            if (k == ip || k == ic || k == inext)
                continue;
            var q = p[list[k]];
            if (Inside(prev, cur, next, q, n) && q != prev && q != cur && q != next)
                return -1f;
        }
        if (count > 3)
        {
            var after = p[list[(inext + 1) % count]];
            float f14 = cur.Z - after.Z, f15 = next.Z - after.Z, f12 = cur.X - after.X;
            float f19 = cur.Y - after.Y, f21 = next.Y - after.Y, f17 = next.X - after.X;
            var area = ((((f14 * f17) - (f12 * f15)) * n.Y) + (((f12 * f21) - (f19 * f17)) * n.Z)) + (((f19 * f15) - (f14 * f21)) * n.X);
            smallest = MinSs(smallest, area);
        }
        if (count > 4)
        {
            var before = p[list[(i + count - 2) % count]];
            float pz = prev.Z - before.Z, cz = cur.Z - before.Z, px = prev.X - before.X, cx = cur.X - before.X;
            float py = prev.Y - before.Y, cy = cur.Y - before.Y;
            var area = ((((pz * cx) - (px * cz)) * n.Y) + (((px * cy) - (py * cx)) * n.Z)) + (((py * cz) - (pz * cy)) * n.X);
            smallest = MinSs(smallest, area);
        }
        var e = Normalise(next.X - cur.X, next.Y - cur.Y, next.Z - cur.Z, zyx: true);
        var a = Normalise(ax, ay, az, zyx: true);
        var b = Normalise(bx, by, bz, zyx: false);
        var cosine = ((b.Y * a.Y) + (b.Z * a.Z)) + (b.X * a.X);
        var ea = ((e.Z * a.Z) + (e.Y * a.Y)) + (a.X * e.X);
        var eb = ((e.Z * b.Z) + (e.Y * b.Y)) + (b.X * e.X);
        var sharpest = MaxSs(cosine, MaxSs(ea, eb));
        var twice = (1f - sharpest) + (1f - sharpest);
        return (1f / MaxSs(smallest, 1f)) + twice;
    }

    // FUN_18136dcb0: the point is on the inner side of all three edges (to
    // -1e-7 along the normal), or within 1e-7 of one of them.
    private static bool Inside(Vector3 a, Vector3 b, Vector3 c, Vector3 q, Vector3 n)
    {
        float f11 = c.X - b.X, f12 = c.Y - b.Y, f15 = a.X - c.X, f16 = a.Y - c.Y, f13 = b.X - a.X, f14 = b.Y - a.Y;
        float f1 = q.X - a.X, f7 = q.Y - a.Y, f2 = q.Z - a.Z, f8 = q.Z - b.Z;
        float f9 = q.X - b.X, f3 = q.X - c.X, f10 = q.Y - b.Y, f6 = q.Y - c.Y;
        float zc = c.Z - b.Z, za = a.Z - c.Z, zb = b.Z - a.Z, qc = q.Z - c.Z;
        var e1 = ((((zc * f9) - (f11 * f8)) * n.Y) + (((f11 * f10) - (f12 * f9)) * n.Z)) + (((f12 * f8) - (zc * f10)) * n.X);
        var e2 = ((((za * f3) - (f15 * qc)) * n.Y) + (((f15 * f6) - (f16 * f3)) * n.Z)) + (((f16 * qc) - (za * f6)) * n.X);
        var e3 = ((((zb * f1) - (f13 * f2)) * n.Y) + (((f13 * f7) - (f14 * f1)) * n.Z)) + (((f14 * f2) - (zb * f7)) * n.X);
        if (!(e1 <= -1e-7f || e2 <= -1e-7f || e3 <= -1e-7f))
            return true;
        return OnEdge(q, a, b) || OnEdge(q, b, c) || OnEdge(q, c, a);
    }

    // FUN_181256bf0 read as the ear test reads it: within 1e-7 of the line,
    // at a parameter from 0 to 1.
    private static bool OnEdge(Vector3 q, Vector3 a, Vector3 b)
    {
        var (squared, t) = LineDistance(q, a, b);
        return squared < 1e-14f && 0f <= t && t <= 1f;
    }

    // FUN_181256bf0: the squared distance from q to the line a-b at the
    // (unclamped) parameter of its nearest point, and that parameter.
    private static (float Squared, float T) LineDistance(Vector3 q, Vector3 a, Vector3 b)
    {
        float d0 = b.X - a.X, d1 = b.Y - a.Y, d2 = b.Z - a.Z;
        var length = (d2 * d2) + (d1 * d1) + (d0 * d0);
        var t = 1e-5f <= length
            ? ((((d2 * q.Z) + (d1 * q.Y)) + (d0 * q.X)) - (((d2 * a.Z) + (d1 * a.Y)) + (d0 * a.X))) / length
            : 0f;
        float ey = q.Y - ((d1 * t) + a.Y), ez = q.Z - ((d2 * t) + a.Z), ex = q.X - ((d0 * t) + a.X);
        return ((ey * ey) + (ex * ex) + (ez * ez), t);
    }

    // FUN_181256f30 through FUN_181256960: the distance from q to the segment a-b.
    private static float SegmentDistance(Vector3 q, Vector3 a, Vector3 b)
    {
        float d0 = b.X - a.X, d1 = b.Y - a.Y, d2 = b.Z - a.Z;
        var length = (d2 * d2) + (d1 * d1) + (d0 * d0);
        var t = 1e-5f <= length
            ? ((((d2 * q.Z) + (d1 * q.Y)) + (d0 * q.X)) - (((d2 * a.Z) + (d1 * a.Y)) + (d0 * a.X))) / length
            : 0f;
        var c = 0f;
        if (0f <= t)
            c = t <= 1f ? t : 1f;
        float x = q.X - ((d0 * c) + a.X), y = q.Y - ((d1 * c) + a.Y), z = q.Z - ((d2 * c) + a.Z);
        return MathF.Sqrt((z * z) + (y * y) + (x * x));
    }

    // The ear score's normalisations: summed z, y, x or y, z, x as the binary
    // sums each; the slow path (under 1e-17 or over 1e17) is approximated in
    // double, as elsewhere in this port.
    private static Vector3 Normalise(float x, float y, float z, bool zyx)
    {
        var length = zyx ? MathF.Sqrt((z * z) + (y * y) + (x * x)) : MathF.Sqrt((y * y) + (z * z) + (x * x));
        if (length < 1e-17f || length > 1e17f)
        {
            if (length == 0f)
                return Vector3.Zero;
            var d = Math.Sqrt(((double)x * x) + ((double)y * y) + ((double)z * z));
            return new Vector3((float)(x / d), (float)(y / d), (float)(z / d));
        }
        var r = 1f / length;
        return new Vector3(x * r, y * r, z * r);
    }

    // minss / maxss with the destination first.
    private static float MinSs(float a, float b) => a < b ? a : b;

    private static float MaxSs(float a, float b) => a > b ? a : b;
}
