using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// ProjectPolygonsOntoTriangles (meshutils/triangulatepolygon.cpp, 18136e6b0
/// with its job 181367ed0) for one overlay face against one target's world
/// triangles: the target triangles facing the overlay
/// (ProjectPolygonOntoTriangles, 18136a930), clipped by the overlay's plane,
/// by the plane <c>far</c> beyond it, and by a plane through each side of the
/// face along the projection, then cut into triangles again. Every point
/// carries a tag: its source triangle and barycentric weights.
/// </summary>
internal static class OverlayProjector
{
    public sealed record Result(List<Vector3> Points, List<Vector4> Tags);

    /// <summary>
    /// The projection direction and plane of the faces (18136e6b0): the
    /// normalised sum of the faces' Newell normals, normalised again, and its
    /// dot with the mean of the face centroids (the first face's, once per
    /// face, as the binary has it).
    /// </summary>
    public static (Vector3 Normal, float W) Plane(IReadOnlyList<Vector3[]> faces)
    {
        float nx = 0f, ny = 0f, nz = 0f, cx = 0f, cy = 0f, cz = 0f;
        var first = faces[0];
        var inv = 1f / first.Length;
        foreach (var face in faces)
        {
            var n = PolygonTriangulator.Newell(face);
            nx += n.X;
            ny += n.Y;
            nz += n.Z;
            var (sx, sy, sz) = Sum(first);
            cx += inv * sx;
            cy += inv * sy;
            cz += inv * sz;
        }
        var normal = TJunctionFix.Normalise(TJunctionFix.Normalise(new Vector3(nx, ny, nz)));
        var k = 1f / faces.Count;
        var w = (cy * k * normal.Y) + (cz * k * normal.Z) + (cx * k * normal.X);
        return (normal, w);
    }

    // The binary's unrolled sums over a face's corners, four at a time.
    private static (float X, float Y, float Z) Sum(Vector3[] p)
    {
        float x = 0f, y = 0f, z = 0f;
        var k = 0;
        for (; k + 3 < p.Length; k += 4)
        {
            x = (((p[k + 2].X + p[k + 1].X) + x) + p[k].X) + p[k + 3].X;
            y = (((p[k + 2].Y + p[k + 1].Y) + p[k].Y) + y) + p[k + 3].Y;
            z = (((p[k + 3].Z + p[k + 2].Z) + p[k + 1].Z) + p[k].Z) + z;
        }
        for (; k < p.Length; k++)
        {
            z += p[k].Z;
            y += p[k].Y;
            x += p[k].X;
        }
        return (x, y, z);
    }

    /// <summary>
    /// 18136a930 on polygon <paramref name="poly"/> (projecting along
    /// <paramref name="dir"/>) and the triangles <paramref name="tris"/>
    /// (three points each), the first numbered <paramref name="firstTriangle"/>.
    /// </summary>
    public static Result Project(Vector3[] poly, Vector3 dir, float far, bool cullBackFaces, float angle, IReadOnlyList<Vector3> tris, int firstTriangle)
    {
        var counts = new List<int>();
        var pts = new List<Vector3>();
        var tags = new List<Vector4>();
        var triangles = tris.Count / 3;
        if (!cullBackFaces)
        {
            for (var t = 0; t < triangles; t++)
            {
                counts.Add(3);
                pts.Add(tris[t * 3]);
                pts.Add(tris[(t * 3) + 1]);
                pts.Add(tris[(t * 3) + 2]);
                AddTags(tags, firstTriangle + t);
            }
        }
        else
        {
            angle = MathF.Max(angle, 0f);
            if (180f <= angle)
                angle = 180f;
            var cos = MathF.Cos(angle * 0.017453292f) + 1e-5f;
            if (cos <= -1f)
                cos = -1f;
            if (0.99999f <= cos)
                cos = 0.99999f;
            for (var t = 0; t < triangles; t++)
            {
                Vector3 a = tris[t * 3], b = tris[(t * 3) + 1], c = tris[(t * 3) + 2];
                var nx = ((c.Z - a.Z) * (b.Y - a.Y)) - ((b.Z - a.Z) * (c.Y - a.Y));
                var ny = ((b.Z - a.Z) * (c.X - a.X)) - ((c.Z - a.Z) * (b.X - a.X));
                var nz = ((c.Y - a.Y) * (b.X - a.X)) - ((c.X - a.X) * (b.Y - a.Y));
                var n = TJunctionFix.Normalise(new Vector3(nx, ny, nz));
                if (cos < (((-dir.Y) * n.Y) - (n.Z * dir.Z)) - (n.X * dir.X))
                {
                    counts.Add(3);
                    pts.Add(a);
                    pts.Add(b);
                    pts.Add(c);
                    AddTags(tags, firstTriangle + t);
                }
            }
        }

        var p0 = poly[0];
        if (0f < far)
        {
            var n1 = TJunctionFix.Normalise(-dir);
            var w1 = (n1.Z * p0.Z) + (n1.Y * p0.Y) + (p0.X * n1.X);
            (pts, tags) = ClipAll(counts, pts, tags, new Vector4(n1, -w1));
            var n2 = TJunctionFix.Normalise(dir);
            var w2 = (((dir.Z * far) + p0.Z) * n2.Z) + (((dir.Y * far) + p0.Y) * n2.Y) + (((dir.X * far) + p0.X) * n2.X);
            (pts, tags) = ClipAll(counts, pts, tags, new Vector4(n2, -w2));
        }
        for (var i = 0; i < poly.Length; i++)
        {
            var prev = poly[(i + poly.Length - 1) % poly.Length];
            var cur = poly[i];
            var e = TJunctionFix.Normalise(cur - prev);
            var n = TJunctionFix.Normalise(new Vector3((dir.Y * e.Z) - (dir.Z * e.Y), (e.X * dir.Z) - (dir.X * e.Z), (dir.X * e.Y) - (e.X * dir.Y)));
            var w = (n.Y * prev.Y) + (n.Z * prev.Z) + (n.X * prev.X);
            (pts, tags) = ClipAll(counts, pts, tags, new Vector4(n, -w));
        }
        return Triangulate(counts, pts, tags);
    }

    public sealed record Mesh(int Stride, List<Physics.MeshWeld.Stream> Streams, float[] Vertices);

    /// <summary>
    /// 181361000: one vertex per projected point. The target's streams are
    /// carried over, each float its source triangle's corners weighted
    /// ((v1 b1) + (v0 b0)) + v2 b2; position, normal and texcoord are added
    /// when missing, then VertexGenericIntegerData (when
    /// <paramref name="integerData"/> is not zero) and
    /// OverlayProjectionDirection (when <paramref name="dir"/> is not zero),
    /// each at the end. The position is the point, the first texcoord the
    /// overlay's, the normal the weighted one normalised and turned by the
    /// target's matrix, the direction dir * 0.5 + 0.5 with w the normal's dot
    /// with dir, likewise.
    /// </summary>
    public static Mesh Build(Result projected, IReadOnlyList<Vector2> texcoords, float[] target, int targetStride, IReadOnlyList<Physics.MeshWeld.Stream> targetStreams,
                             IReadOnlyList<int> targetIndices, float[] matrix, int integerData, Vector3 dir)
    {
        var streams = new List<Physics.MeshWeld.Stream>(targetStreams);
        int Add(string name, int count, int type)
        {
            foreach (var s in streams)
                if (s.Name == name)
                    return s.First;
            var first = streams.Count == 0 ? 0 : streams.Max(s => s.First + s.Count);
            streams.Add(new Physics.MeshWeld.Stream(name, first, count, false, type));
            return first;
        }
        var position = Add("position", 3, 0x2a);
        var normal = Add("normal", 3, 0x2a);
        var texcoord = Add("texcoord", 2, 0x29);
        var integer = integerData != 0 ? Add("VertexGenericIntegerData", 1, 0x22) : -1;
        var direction = dir != Vector3.Zero ? Add("OverlayProjectionDirection", 4, 0x2b) : -1;
        var stride = streams.Max(s => s.First + s.Count);
        var v = new float[projected.Points.Count * stride];
        for (var k = 0; k < projected.Points.Count; k++)
        {
            var at = k * stride;
            var tag = projected.Tags[k];
            var tri = BitConverter.SingleToInt32Bits(tag.X);
            int i0 = targetIndices[tri * 3] * targetStride, i1 = targetIndices[(tri * 3) + 1] * targetStride, i2 = targetIndices[(tri * 3) + 2] * targetStride;
            for (var j = 0; j < targetStride; j++)
                v[at + j] = (target[i1 + j] * tag.Z) + (target[i0 + j] * tag.Y) + (target[i2 + j] * tag.W);
            var p = projected.Points[k];
            (v[at + position], v[at + position + 1], v[at + position + 2]) = (p.X, p.Y, p.Z);
            (v[at + texcoord], v[at + texcoord + 1]) = (texcoords[k].X, texcoords[k].Y);
            if (integer >= 0)
                v[at + integer] = BitConverter.Int32BitsToSingle(integerData);
            var n = NodeMeshEntries.Rotate(matrix, TJunctionFix.Normalise(new Vector3(v[at + normal], v[at + normal + 1], v[at + normal + 2])));
            (v[at + normal], v[at + normal + 1], v[at + normal + 2]) = (n.X, n.Y, n.Z);
            if (direction >= 0)
            {
                v[at + direction] = (dir.X * 0.5f) + 0.5f;
                v[at + direction + 1] = (dir.Y * 0.5f) + 0.5f;
                v[at + direction + 2] = (dir.Z * 0.5f) + 0.5f;
                v[at + direction + 3] = ((((n.Z * dir.Z) + (n.Y * dir.Y)) + (dir.X * n.X)) * 0.5f) + 0.5f;
            }
        }
        // CMesh_ComputeTangents on the unindexed mesh: one vertex per corner.
        var count = projected.Points.Count;
        var tangent = streams.FindIndex(s => s.Name.Contains("tangent", StringComparison.OrdinalIgnoreCase) && s.Count == 4);
        if (tangent < 0)
            throw new NotSupportedException("an overlay target without a tangent stream");
        var tangentAt = streams[tangent].First;
        var tex = streams.First(s => s.Name.Contains("tex", StringComparison.OrdinalIgnoreCase) && s.Count == 2).First;
        var tangents = MeshTangents.Corners(
            [.. Enumerable.Range(0, count).Select(k => new Vector3(v[(k * stride) + position], v[(k * stride) + position + 1], v[(k * stride) + position + 2]))],
            [.. Enumerable.Range(0, count).Select(k => new Vector3(v[(k * stride) + normal], v[(k * stride) + normal + 1], v[(k * stride) + normal + 2]))],
            [.. Enumerable.Range(0, count).Select(k => new Vector2(v[(k * stride) + tex], v[(k * stride) + tex + 1]))],
            [.. Enumerable.Range(0, count)]);
        for (var k = 0; k < count; k++)
            (v[(k * stride) + tangentAt], v[(k * stride) + tangentAt + 1], v[(k * stride) + tangentAt + 2], v[(k * stride) + tangentAt + 3]) = (tangents[k].X, tangents[k].Y, tangents[k].Z, tangents[k].W);
        return new Mesh(stride, streams, v);
    }

    /// <summary>
    /// The job's drop of a point onto the faces' plane:
    /// p - (((p.z n.z + n.y p.y) + p.x n.x) - w) n.
    /// </summary>
    public static Vector3 Drop(Vector3 p, Vector3 n, float w)
    {
        var d = ((p.Z * n.Z) + (n.Y * p.Y) + (p.X * n.X)) - w;
        return new Vector3(p.X - (d * n.X), p.Y - (d * n.Y), p.Z - (d * n.Z));
    }

    /// <summary>Which path <see cref="Texcoord"/> took, for diagnosis.</summary>
    public static string LastPath = "";

    /// <summary>
    /// OverlayFace_Texcoord (18136cd60) at a point on the face's plane: a
    /// convex quad solves for its bilinear coordinates (18129ba60) and blends
    /// the corner texcoords (18129c490); otherwise not ported (NaN).
    /// </summary>
    public static Vector2 Texcoord(Vector3[] face, Vector2[] uvs, Vector3 point)
    {
        if (uvs.Length < face.Length)
        {
            LastPath = "no texcoords";
            return Vector2.Zero;
        }
        if (face.Length == 4 && Convex(face) && InverseBilinear(face[0], face[1], face[2], face[3], point, out var st) == 1)
        {
            LastPath = "bilinear";
            var a = Lerp(uvs[0], uvs[3], st.Y);
            var b = Lerp(uvs[1], uvs[2], st.Y);
            return new Vector2(((b.X - a.X) * st.X) + a.X, ((b.Y - a.Y) * st.X) + a.Y);
        }
        // 18136c270: the first triangle of the face's cut holding the point.
        var cut = PolygonTriangulator.Triangulate(face);
        for (var t = 0; t + 2 < cut.Length; t += 3)
        {
            if (!InTriangle(face[cut[t]], face[cut[t + 1]], face[cut[t + 2]], point, out var b0, out var b1, out var b2))
                continue;
            LastPath = "triangle";
            Vector2 a = uvs[cut[t]], b = uvs[cut[t + 1]], c = uvs[cut[t + 2]];
            return new Vector2((b0 * a.X) + (b1 * b.X) + (b2 * c.X), (b0 * a.Y) + (b1 * b.Y) + (b2 * c.Y));
        }
        LastPath = "other";
        return new Vector2(float.NaN);
    }

    /// <summary>
    /// 181259470: the point's weights as the areas of the triangles it makes
    /// with each side over the triangle's area; inside when they sum to 1
    /// within 0.001.
    /// </summary>
    private static bool InTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 p, out float b0, out float b1, out float b2)
    {
        float bx = b.X - a.X, by = b.Y - a.Y, bz = b.Z - a.Z, cx = c.X - a.X, cy = c.Y - a.Y, cz = c.Z - a.Z;
        var nx = (by * cz) - (bz * cy);
        var nz = (cy * bx) - (cx * by);
        var ny = (bz * cx) - (bx * cz);
        var area = MathF.Sqrt((nz * nz) + (ny * ny) + (nx * nx)) * 0.5f;
        var inv = area != 0f ? 1f / area : 0f;
        {
            float px = b.X - p.X, py = b.Y - p.Y, pz = b.Z - p.Z, qx = c.X - p.X, qy = c.Y - p.Y, qz = c.Z - p.Z;
            var x = (qz * py) - (pz * qy);
            var y = (pz * qx) - (qz * px);
            var z = (qy * px) - (qx * py);
            b0 = MathF.Sqrt((z * z) + (y * y) + (x * x)) * 0.5f * inv;
        }
        {
            float qx = c.X - p.X, qy = c.Y - p.Y, qz = c.Z - p.Z, ax = a.X - p.X, ay = a.Y - p.Y, az = a.Z - p.Z;
            var x = (qy * az) - (qz * ay);
            var z = (qx * ay) - (qy * ax);
            var y = (qz * ax) - (qx * az);
            b1 = MathF.Sqrt((z * z) + (y * y) + (x * x)) * 0.5f * inv;
        }
        {
            float qx = b.X - p.X, qy = b.Y - p.Y, qz = b.Z - p.Z, ax = a.X - p.X, ay = a.Y - p.Y, az = a.Z - p.Z;
            var u = (qx * az) - (qz * ax);
            var v = (qy * ax) - (qx * ay);
            var w = (qz * ay) - (az * qy);
            b2 = MathF.Sqrt((u * u) + (v * v) + (w * w)) * 0.5f * inv;
        }
        return MathF.Abs(1f - (b0 + b1 + b2)) < 0.001f;
    }

    private static Vector2 Lerp(Vector2 a, Vector2 b, float t) => new(((b.X - a.X) * t) + a.X, ((b.Y - a.Y) * t) + a.Y);

    /// <summary>OverlayFace_IsConvex (18136e370) with the face's Newell normal.</summary>
    internal static bool Convex(Vector3[] p)
    {
        var n = PolygonTriangulator.Newell(p);
        var count = p.Length;
        var positive = false;
        for (var i = 0; i < count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % count];
            var c = p[(i + 2) % count];
            float e1x = b.X - a.X, e1y = b.Y - a.Y, e1z = b.Z - a.Z;
            float e2x = c.X - b.X, e2y = c.Y - b.Y, e2z = c.Z - b.Z;
            var d = (((e1x * e2z) - (e2x * e1z)) * n.Y) + (((e2x * e1y) - (e1x * e2y)) * n.Z) + (((e1z * e2y) - (e2z * e1y)) * n.X);
            if (i == 0)
                positive = 0f < d;
            else
            {
                bool ok;
                if (positive)
                {
                    d += 1e-07f;
                    ok = 0f < d;
                }
                else
                {
                    d -= 1e-07f;
                    ok = d < 0f;
                }
                if (!ok && d != 0f)
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 18129ba60: the bilinear coordinates of <paramref name="point"/> in
    /// quad p0 p1 p2 p3; 1 when both lie in [0, 1]. A near parallelogram
    /// (p1 - p0 within 0.99 of p2 - p3 in direction and 0.1 in length)
    /// solves each axis as a line meeting (18129c2e0); otherwise not ported (2).
    /// </summary>
    internal static int InverseBilinear(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 point, out Vector2 st)
    {
        var u = p1 - p0;
        var opposite = p2 - p3;
        var v = p3 - p0;
        var lu = Length(u);
        var lo = Length(opposite);
        var lv = Length(v);
        u = Unit(u, lu);
        opposite = Unit(opposite, lo);
        v = Unit(v, lv);
        if (0.99f < (u.Z * opposite.Z) + (u.Y * opposite.Y) + (u.X * opposite.X) && MathF.Abs(lu - lo) < 0.1f)
        {
            var s = LineMeet(p0, u, point, -(lv * 10f) * v) / lu;
            var t = LineMeet(p0, v, point, -(lu * 10f) * u) / lv;
            st = new Vector2(s, t);
            return s < 0f || 1f < s || t < 0f ? 0 : (t <= 1f ? 1 : 0);
        }
        // The general quad: a quadratic in double, on the two axes the quad
        // spans most, u and v swapped when p3 - p0 runs along p2 - p1.
        var w = Unit(p2 - p1, Length(p2 - p1));
        var lw = Length(p2 - p1);
        var vw = (v.Z * w.Z) + (v.Y * w.Y) + (v.X * w.X);
        Vector3 e1 = p1 - p0, eo = p2 - p3, ev = p3 - p0, ew = p2 - p1;
        Vector3 A = e1, B = ev, C = eo;
        if (0.99f < vw)
            (A, B, C) = (ev, e1, ew);
        var nx = MathF.Abs((A.Y * B.Z) - (B.Y * A.Z));
        var nz = MathF.Abs((A.X * B.Y) - (B.X * A.Y));
        var ny = MathF.Abs((B.X * A.Z) - (A.X * B.Z));
        int i, j;
        if (nx <= ny)
        {
            j = 0;
            i = nz < ny ? 2 : 1;
        }
        else
        {
            j = nz < nx ? 1 : 0;
            i = j + 1;
        }
        if (MathF.Abs(At(A, i)) <= MathF.Abs(At(A, j)))
            (i, j) = (j, i);
        float ai = At(A, i), aj = At(A, j), bi = At(B, i), bj = At(B, j), ci = At(C, i), cj = At(C, j);
        float pi = At(point, i), pj = At(point, j), oi = At(p0, i), oj = At(p0, j);
        var biaj = bi * aj;
        var aibj = ai * bj;
        var qa = (double)(((biaj - aibj) - (cj * bi)) + (ci * bj));
        var qc = (double)(((oj * ai) - (pj * ai)) - (oi * aj) + (pi * aj));
        var qb = ((((qc - (double)(oj * ci)) + (double)(pj * ci)) + (double)(oi * cj)) - (double)(pi * cj) + (double)biaj) - (double)aibj;
        if (-0.10000000149011612 < qa && qa < 0.10000000149011612)
        {
            var uAvg = ((u + opposite) * 0.5f);
            var vAvg = ((v + w) * 0.5f);
            var lU = (lu + lo) * 0.5f;
            var lV = (lv + lw) * 0.5f;
            var s = LineMeet(p0, uAvg, point, -(lV * 10f) * vAvg) / lU;
            var t = LineMeet(p0, vAvg, point, -(lU * 10f) * uAvg) / lV;
            st = new Vector2(s, t);
            return s < 0f || 1f < s || t < 0f ? 0 : (t <= 1f ? 1 : 0);
        }
        var disc = (qb * qb) - (qa * 4.0 * qc);
        if (disc < 0.0)
        {
            st = new Vector2(-99999f, -99999f);
            return 2;
        }
        var sq = Math.Sqrt(disc);
        var r1 = (sq + qb) / (qa + qa);
        var r2 = (qb - sq) / (qa + qa);
        var den1 = ((1.0 - r1) * ai) + (ci * r1);
        var den2 = ((1.0 - r2) * ai) + (ci * r2);
        var s1 = -99999.0;
        var s2 = -99999.0;
        if (1e-05 <= Math.Abs(den1))
            s1 = ((double)(pi - oi) - ((double)bi * r1)) / den1;
        if (1e-05 <= Math.Abs(den2))
            s2 = ((double)(pi - oi) - ((double)bi * r2)) / den2;
        double sOut = s1, rOut = r1;
        if (r1 < 0.0 || 1.0 < r1 || s1 < 0.0 || 1.0 < s1)
        {
            var keepFirst = false;
            if (r2 < 0.0 || 1.0 < r2 || s2 < 0.0 || 1.0 < s2)
            {
                double d1 = 1.0 <= s1 ? s1 - 1.0 : s1, e1d = 1.0 <= r1 ? r1 - 1.0 : r1;
                double d2 = 1.0 <= s2 ? s2 - 1.0 : s2, e2d = 1.0 <= r2 ? r2 - 1.0 : r2;
                var m1 = Math.Abs(e1d);
                var m2 = Math.Abs(e2d);
                if (m1 <= Math.Abs(d1))
                    m1 = Math.Abs(d1);
                if (m2 <= Math.Abs(d2))
                    m2 = Math.Abs(d2);
                keepFirst = m1 <= m2;
            }
            if (!keepFirst)
            {
                sOut = s2;
                rOut = r2;
            }
        }
        var fr = (float)rOut;
        var fs = (float)sOut;
        st = vw <= 0.99f ? new Vector2(fs, fr) : new Vector2(fr, fs);
        return st.X < 0f || 1f < st.X || st.Y < 0f ? 0 : (st.Y <= 1f ? 1 : 0);
    }

    private static float At(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

    // 18013e340's length and scale, kept apart as the caller has them.
    private static float Length(Vector3 d) => MathF.Sqrt((d.Y * d.Y) + (d.Z * d.Z) + (d.X * d.X));

    private static Vector3 Unit(Vector3 d, float len)
    {
        if (1e-17f <= len && len <= 1e17f)
        {
            var inv = 1f / len;
            return new Vector3(inv * d.X, inv * d.Y, inv * d.Z);
        }
        return TJunctionFix.Normalise(d);
    }

    /// <summary>
    /// 1812a5bf0: the distance along the first line (origin <paramref name="o1"/>,
    /// direction <paramref name="d1"/>) to its closest approach to the second.
    /// </summary>
    private static float LineMeet(Vector3 o1, Vector3 d1, Vector3 o2, Vector3 d2)
    {
        var a = TJunctionFix.Normalise(d1);
        var b = TJunctionFix.Normalise(d2);
        var cx = (a.Y * b.Z) - (b.Y * a.Z);
        var cy = (a.Z * b.X) - (b.Z * a.X);
        var cz = (b.Y * a.X) - (a.Y * b.X);
        var cc = (cy * cy) + (cx * cx) + (cz * cz);
        if (cc == 0f)
            return 0f;
        var qx = (cz * (o2.Y - o1.Y)) - (cy * (o2.Z - o1.Z));
        var qy = (cx * (o2.Z - o1.Z)) - (cz * (o2.X - o1.X));
        var qz = (cy * (o2.X - o1.X)) - (cx * (o2.Y - o1.Y));
        return ((((-qz) * b.Z) + ((-qy) * b.Y)) + ((-qx) * b.X)) / cc;
    }

    private static void AddTags(List<Vector4> tags, int triangle)
    {
        var id = BitConverter.Int32BitsToSingle(triangle);
        tags.Add(new Vector4(id, 1f, 0f, 0f));
        tags.Add(new Vector4(id, 0f, 1f, 0f));
        tags.Add(new Vector4(id, 0f, 0f, 1f));
    }

    private static (List<Vector3>, List<Vector4>) ClipAll(List<int> counts, List<Vector3> pts, List<Vector4> tags, Vector4 plane)
    {
        var outPts = new List<Vector3>();
        var outTags = new List<Vector4>();
        var at = 0;
        for (var k = 0; k < counts.Count; k++)
        {
            var n = counts[k];
            counts[k] = Clip(pts, tags, at, n, plane, outPts, outTags);
            at += n;
        }
        return (outPts, outTags);
    }

    /// <summary>
    /// 181369250: the polygon's part with plane distance at most zero; a
    /// distance whose square is under the longest side's square times
    /// 9.999999e-09 (kept within [1.4210855e-14, 1]) counts as zero.
    /// </summary>
    private static int Clip(List<Vector3> pts, List<Vector4> tags, int at, int n, Vector4 plane, List<Vector3> outPts, List<Vector4> outTags)
    {
        var eps = 0f;
        for (var k = 0; k < n; k++)
        {
            var a = pts[at + ((k + n - 1) % n)];
            var b = pts[at + k];
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            var d2 = (dy * dy) + (dx * dx) + (dz * dz);
            if (eps <= d2)
                eps = d2;
        }
        eps *= 9.999999e-09f;
        if (eps <= 1.4210855e-14f)
            eps = 1.4210855e-14f;
        if (1f <= eps)
            eps = 1f;
        if (n < 1)
            return 0;
        var made = 0;
        float Distance(Vector3 p) => (plane.Z * p.Z) + (plane.Y * p.Y) + (plane.X * p.X) + plane.W;
        for (var k = 0; k < n; k++)
        {
            var prev = pts[at + ((k + n - 1) % n)];
            var cur = pts[at + k];
            var prevTag = tags[at + ((k + n - 1) % n)];
            var curTag = tags[at + k];
            var dp = Distance(prev);
            var dc = Distance(cur);
            if (dp * dp < eps)
                dp = 0f;
            if (dc * dc < eps)
                dc = 0f;
            if (dp <= 0f)
            {
                outPts.Add(prev);
                outTags.Add(prevTag);
                made++;
            }
            if (dc * dp < 0f)
            {
                var t = -dp / (dc - dp);
                outPts.Add(new Vector3(((cur.X - prev.X) * t) + prev.X, ((cur.Y - prev.Y) * t) + prev.Y, ((cur.Z - prev.Z) * t) + prev.Z));
                outTags.Add(new Vector4(prevTag.X, ((curTag.Y - prevTag.Y) * t) + prevTag.Y, ((curTag.Z - prevTag.Z) * t) + prevTag.Z, ((curTag.W - prevTag.W) * t) + prevTag.W));
                made++;
            }
        }
        return made;
    }

    /// <summary>18136fe00: each clipped polygon of three or more points cut by PolygonTriangulator.</summary>
    private static Result Triangulate(List<int> counts, List<Vector3> pts, List<Vector4> tags)
    {
        var outPts = new List<Vector3>();
        var outTags = new List<Vector4>();
        var at = 0;
        foreach (var n in counts)
        {
            if (n > 2)
            {
                var poly = pts.GetRange(at, n).ToArray();
                foreach (var i in PolygonTriangulator.Triangulate(poly))
                {
                    outPts.Add(poly[i]);
                    outTags.Add(tags[at + i]);
                }
            }
            at += n;
        }
        return new Result(outPts, outTags);
    }
}
