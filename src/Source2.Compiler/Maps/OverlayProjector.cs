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
