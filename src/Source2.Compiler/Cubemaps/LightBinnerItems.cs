using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Source2.Compiler.Cubemaps;

/// <summary>
/// The light binner's per-item inputs for a view (scenesystem 20261015 / 20261023, ledger 151): each visible item's
/// screen bounds, depth range and the edge lines of its screen footprint, which the cull dispatches read
/// (compute_depthbin_cullbits and compute_tile_cullbits, set 0 bindings 30 and 31). Ported for light probe volumes
/// (the only items the cubemap view bins on the captured maps):
/// <list type="bullet">
/// <item>1801ac1b0: the volume's box as six inward planes and eight corners;</item>
/// <item>1801abf30 / 1801aac90: the camera and view direction in the volume's frame, the eye-outside plane mask,
/// the depth range along the view direction, and the screen footprint: the silhouette of the box for that mask
/// (the table at 1805fe400 in 20261015, 1805fe430 in 20261023) projected, or clipped in clip space when the box
/// crosses the camera plane, then widened by half a tile (the polygon's Minkowski sum with the tile rectangle);</item>
/// <item>1801ad810: the footprint's edges as lines (n, d) at least two pixel diagonals long, convex, d offset by a
/// quarter pixel diagonal; the length uses rsqrtss with one Newton step, so the result follows the CPU as the
/// game's does;</item>
/// <item>1801b2850: the 80-byte item records (first edge, inside flag, edge count, bounds widened by a tile in
/// each direction, depth range), the items padded to a multiple of 32 with bounds -100.</item>
/// </list>
/// </summary>
public static class LightBinnerItems
{
    /// <summary>The view as the binner reads it (the scenesystem view +0x10, +0x194, +0x298, +0xc7c / +0xc80, +0xf370).</summary>
    /// <param name="WorldToProjection">Row-major 4 x 4.</param>
    /// <param name="Mirrored">The view's +0xf353 flag: the footprint is walked the other way round.</param>
    /// <param name="Orthographic">The view's +0x28 is -1: back-facing planes leave the silhouette mask.</param>
    /// <param name="DepthOffset">1801b52b0's value (the view attribute 0x6e59bbf1).</param>
    public sealed record View(Vector3 Camera, Vector3 Forward, float[] WorldToProjection, int Width, int Height, int TilePixels,
                              bool Mirrored = false, bool Orthographic = false, float DepthOffset = 0f);

    /// <summary>One item's footprint (1801aac90's output record).</summary>
    public sealed class Footprint
    {
        public bool Inside;
        public float MinDepth, MaxDepth;
        public float MinX = -1f, MinY = -1f, MaxX = 1f, MaxY = 1f;
        public List<Vector2> Silhouette = [];
        public List<Vector2> Hull = [];
    }

    /// <summary>The DAT_180673b20 signs: corner k takes the box's max where its sign is positive, else its min.</summary>
    private static readonly float[] CornerSigns = [-1, 1, -1, -1, -1, -1, -1, 1, 1, -1, -1, 1, 1, 1, -1, 1, -1, -1, 1, 1, 1, 1, -1, 1];

    /// <summary>The silhouette table: for each eye-outside plane mask, the corners of the box's outline in order.</summary>
    private static readonly int[][] Silhouettes = BuildSilhouettes();

    private static int[][] BuildSilhouettes()
    {
        var t = new int[64][];
        void S(int m, params int[] c) => t[m] = c;
        S(1, 1, 5, 7, 3); S(2, 0, 2, 6, 4); S(4, 2, 3, 7, 6); S(5, 1, 5, 7, 6, 2, 3); S(6, 0, 2, 3, 7, 6, 4); S(7, 0, 2, 3, 1, 5, 7, 6, 4);
        S(8, 0, 4, 5, 1); S(9, 0, 4, 5, 7, 3, 1); S(10, 0, 2, 6, 4, 5, 1); S(11, 0, 2, 6, 4, 5, 7, 3, 1); S(13, 0, 4, 5, 7, 6, 2, 3, 1);
        S(14, 0, 2, 3, 7, 6, 4, 5, 1); S(16, 0, 1, 3, 2); S(17, 0, 1, 5, 7, 3, 2); S(18, 0, 1, 3, 2, 6, 4); S(19, 0, 1, 5, 7, 3, 2, 6, 4);
        S(20, 0, 1, 3, 7, 6, 2); S(21, 0, 1, 5, 7, 6, 2); S(22, 0, 1, 3, 7, 6, 4); S(23, 0, 1, 5, 7, 6, 4); S(24, 0, 4, 5, 1, 3, 2);
        S(25, 0, 4, 5, 7, 3, 2); S(26, 1, 3, 2, 6, 4, 5); S(27, 2, 6, 4, 5, 7, 3); S(28, 0, 4, 5, 1, 3, 7, 6, 2); S(29, 0, 4, 5, 7, 6, 2);
        S(30, 1, 3, 7, 6, 4, 5); S(31, 4, 5, 7, 6); S(32, 4, 6, 7, 5); S(33, 1, 5, 4, 6, 7, 3); S(34, 0, 2, 6, 7, 5, 4);
        S(35, 0, 2, 6, 7, 3, 1, 5, 4); S(36, 2, 3, 7, 5, 4, 6); S(37, 1, 5, 4, 6, 2, 3); S(38, 0, 2, 3, 7, 5, 4); S(39, 0, 2, 3, 1, 5, 4);
        S(40, 0, 4, 6, 7, 5, 1); S(41, 0, 4, 6, 7, 3, 1); S(42, 0, 2, 6, 7, 5, 1); S(43, 0, 2, 6, 7, 3, 1); S(44, 0, 4, 6, 2, 3, 7, 5, 1);
        S(45, 0, 4, 6, 2, 3, 1); S(46, 0, 2, 3, 7, 5, 1); S(47, 0, 2, 3, 1); S(49, 0, 1, 5, 4, 6, 7, 3, 2); S(50, 0, 1, 3, 2, 6, 7, 5, 4);
        S(52, 0, 1, 3, 7, 5, 4, 6, 2); S(53, 0, 1, 5, 4, 6, 2); S(54, 0, 1, 3, 7, 5, 4); S(55, 0, 1, 5, 4); S(56, 0, 4, 6, 7, 5, 1, 3, 2);
        S(57, 0, 4, 6, 7, 3, 2); S(58, 1, 3, 2, 6, 7, 5); S(59, 2, 6, 7, 3); S(61, 0, 4, 6, 2); S(62, 1, 3, 7, 5);
        return t;
    }

    /// <summary>
    /// 1801ac1b0: the box's planes (normal, d) in the order +y at min, -y at max, -z at max, +z at min, +x at min, -x
    /// at max (each normal through 180053800, d its dot with that corner), and its corners by the sign table.
    /// </summary>
    public static (float[] Planes, Vector3[] Corners) Box(Vector3 mins, Vector3 maxs)
    {
        var corners = new Vector3[8];
        for (var k = 0; k < 8; k++)
            corners[k] = new Vector3(CornerSigns[k * 3] > 0f ? maxs.X : mins.X, CornerSigns[k * 3 + 1] > 0f ? maxs.Y : mins.Y, CornerSigns[k * 3 + 2] > 0f ? maxs.Z : mins.Z);
        var planes = new float[24];
        void P(int i, Vector3 n, Vector3 at, bool yFirst)
        {
            n = CubemapShadowCascades.Normalize(n);
            planes[i * 4] = n.X;
            planes[i * 4 + 1] = n.Y;
            planes[i * 4 + 2] = n.Z;
            planes[i * 4 + 3] = yFirst ? (n.Y * at.Y + n.Z * at.Z) + n.X * at.X : (n.Z * at.Z + n.Y * at.Y) + n.X * at.X;
        }
        P(0, new(0f, 1f, 0f), mins, true);
        P(1, new(-0f, -1f, -0f), maxs, false);
        P(2, new(-0f, -0f, -1f), maxs, false);
        P(3, new(0f, 0f, 1f), mins, false);
        P(5, new(-1f, -0f, -0f), maxs, false);
        P(4, new(1f, 0f, 0f), mins, false);
        return (planes, corners);
    }

    /// <summary>1801abcf0: bit i set where the point is more than 1e-4 behind plane i ((z nz + y ny) + x nx - d).</summary>
    public static int OutsideMask(float[] planes, Vector3 p)
    {
        var mask = 0;
        for (var i = 0; i < 6; i++)
            if ((p.Z * planes[i * 4 + 2] + planes[i * 4 + 1] * p.Y + p.X * planes[i * 4]) - planes[i * 4 + 3] < -0.0001f)
                mask |= 1 << i;
        return mask;
    }

    /// <summary>
    /// 1801abf30 then 1801aac90 for a box in a frame (<paramref name="transform"/>, row-major 3 x 4, the scene
    /// object's +0x30): null when the item is rejected (behind the camera, or clipped away), else its footprint.
    /// </summary>
    public static Footprint? Bounds(View view, Vector3 mins, Vector3 maxs, float[] transform)
    {
        var (planes, corners) = Box(mins, maxs);
        return Bounds(view, planes, corners, transform);
    }

    /// <summary>
    /// 1801abf30 then 1801aac90 for any convex item given as six planes (n, d) and eight corners in its frame: a light
    /// probe volume's box (<see cref="Box"/>), a barn light's frustum or its illumination box (1801aa3f0).
    /// </summary>
    public static Footprint? Bounds(View view, float[] planes, Vector3[] corners, float[] transform)
    {
        var t = transform;
        float dx = view.Camera.X - t[3], dy = view.Camera.Y - t[7], dz = view.Camera.Z - t[11];
        var camera = new Vector3((dz * t[8] + dy * t[4]) + dx * t[0], (dz * t[9] + dy * t[5]) + dx * t[1], (dz * t[10] + dy * t[6]) + dx * t[2]);
        var f = view.Forward;
        var direction = new Vector3((f.X * t[0] + f.Y * t[4]) + f.Z * t[8], (f.X * t[1] + f.Y * t[5]) + f.Z * t[9], (f.X * t[2] + f.Y * t[6]) + f.Z * t[10]);
        var m = Mul(view.WorldToProjection, [t[0], t[1], t[2], t[3], t[4], t[5], t[6], t[7], t[8], t[9], t[10], t[11], 0f, 0f, 0f, 1f]);
        float tileX = (float)(uint)view.TilePixels / view.Width, tileY = (float)(uint)view.TilePixels / view.Height;
        return Footprint1801aac90(view.Mirrored, tileX, tileY, camera, direction, m, view.Orthographic, view.DepthOffset, planes, corners);
    }

    /// <summary>1804983c0: a b for row-major 4 x 4s, each element a DPPS of a's row and b's column.</summary>
    private static float[] Mul(float[] a, float[] b)
    {
        var r = new float[16];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                r[i * 4 + j] = CubemapFaceViews.Dot4(a[i * 4], a[i * 4 + 1], a[i * 4 + 2], a[i * 4 + 3], b[j], b[4 + j], b[8 + j], b[12 + j]);
        return r;
    }

    private static Footprint? Footprint1801aac90(bool mirrored, float tileX, float tileY, Vector3 camera, Vector3 direction, float[] m,
                                                bool orthographic, float depthOffset, float[] planes, Vector3[] corners)
    {
        var out1 = new Footprint();
        var mask = OutsideMask(planes, camera);
        if (orthographic)
        {
            // Only planes facing the view direction (more than 1e-5) keep their bit.
            var facing = 0;
            for (var i = 0; i < 6; i++)
                if (1e-05f < (planes[i * 4 + 2] * direction.Z + direction.Y * planes[i * 4 + 1]) + planes[i * 4] * direction.X)
                    facing |= 1 << i;
            mask &= facing;
        }
        var d = CubemapShadowCascades.Normalize(direction);
        var at = ((d.Z * camera.Z + d.Y * camera.Y) + camera.X * d.X) + depthOffset;
        float lo = float.MaxValue, hi = -float.MaxValue;
        foreach (var c in corners)
        {
            var v = ((d.Z * c.Z + d.Y * c.Y) + c.X * d.X) - at;
            lo = v <= lo ? v : lo;
            hi = hi <= v ? v : hi;
        }
        if (hi < 0f)
            return null;
        out1.MaxDepth = hi + depthOffset;
        out1.MinDepth = lo + depthOffset;
        if (mask == 0)
        {
            out1.Inside = true;
            return out1;
        }
        var outline = Silhouettes[mask] ?? throw new InvalidDataException($"no silhouette for mask {mask}");
        if (0f <= lo)
        {
            foreach (var i in outline)
                out1.Silhouette.Add(Project(m, corners[i]));
        }
        else
        {
            var poly = outline.Select(i =>
            {
                var c = corners[i];
                return new Vector4(((c.X * m[0] + c.Y * m[1]) + c.Z * m[2]) + m[3], ((c.Y * m[5] + c.X * m[4]) + c.Z * m[6]) + m[7],
                    ((c.Y * m[9] + c.X * m[8]) + c.Z * m[10]) + m[11], ((c.Y * m[13] + c.X * m[12]) + c.Z * m[14]) + m[15]);
            }).ToList();
            poly = Clip(poly, p => p.W < 0.0001f, (p, c) => { var t = -(0.0001f - p.W) / (p.W - c.W); return Lerp(p, c, t); });
            poly = ClipScaled(poly, 0f, 2);
            poly = ClipScaled(poly, 1f, 1);
            poly = ClipScaled(poly, -1f, 1);
            poly = ClipScaled(poly, 1f, 0);
            poly = ClipScaled(poly, -1f, 0);
            if (poly.Count < 1)
                return null;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = -float.MaxValue, maxY = -float.MaxValue;
            var points = new List<Vector2>();
            foreach (var p in poly)
            {
                var w = 0.0001f <= p.W ? p.W : 0.0001f;
                var (x, y) = (p.X / w, p.Y / w);
                points.Add(new Vector2(x, y));
                minX = x <= minX ? x : minX;
                minY = y <= minY ? y : minY;
                maxX = maxX <= x ? x : maxX;
                maxY = maxY <= y ? y : maxY;
            }
            if (1f < minX || 1f < minY || maxX < -1f || maxY < -1f)
                return null;
            var big = minX <= minY ? minY : minX;
            var small = maxY <= maxX ? maxY : maxX;
            // A quad that covers the whole screen leaves no footprint (the item covers every tile).
            if (!(points.Count == 4 && !(-0.999f < big) && !(small < 0.999f)))
                out1.Silhouette = points;
        }
        Expand(out1, mirrored, tileX, tileY);
        return out1;
    }

    private static Vector4 Lerp(Vector4 p, Vector4 c, float t)
        => new((c.X - p.X) * t + p.X, (c.Y - p.Y) * t + p.Y, (c.Z - p.Z) * t + p.Z, (c.W - p.W) * t + p.W);

    /// <summary>
    /// 1801c65c0 (axis 2), 1801c6b60 (axis 1), 1801c7100 (axis 0): clip against w &gt;= k x the axis coordinate,
    /// a = w - axis k at each end, t = a_prev / (a_prev - a_cur).
    /// </summary>
    private static List<Vector4> ClipScaled(List<Vector4> poly, float k, int axis)
    {
        static float A(Vector4 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
        return Clip(poly, p => p.W < A(p, axis) * k, (p, c) =>
        {
            var a = p.W - A(p, axis) * k;
            var t = a / (a - (c.W - A(c, axis) * k));
            return Lerp(p, c, t);
        });
    }

    /// <summary>
    /// The clips' common loop (1801c6050 and its kin): from the last point, a crossing point where the inside
    /// changes, then the point when it is inside. The result replaces the polygon only when a crossing was found.
    /// </summary>
    private static List<Vector4> Clip(List<Vector4> poly, Func<Vector4, bool> outside, Func<Vector4, Vector4, Vector4> cross)
    {
        if (poly.Count == 0)
            return poly;
        var result = new List<Vector4>();
        var crossed = false;
        var prev = poly[^1];
        var prevOut = outside(prev);
        foreach (var cur in poly)
        {
            var curOut = outside(cur);
            if (prevOut != curOut)
            {
                result.Add(cross(prev, cur));
                crossed = true;
            }
            if (!curOut)
                result.Add(cur);
            (prev, prevOut) = (cur, curOut);
        }
        return crossed ? result : poly;
    }

    /// <summary>180499860: a point through a row-major 4 x 4 with the divide (1 / w when w is not 0).</summary>
    private static Vector2 Project(float[] m, Vector3 p)
    {
        var w = ((p.X * m[12] + p.Y * m[13]) + p.Z * m[14]) + m[15];
        if (w != 0f)
            w = 1f / w;
        var x = ((p.Y * m[1] + p.X * m[0]) + p.Z * m[2]) + m[3];
        var y = ((p.X * m[4] + p.Y * m[5]) + p.Z * m[6]) + m[7];
        return new Vector2(x * w, y * w);
    }

    /// <summary>
    /// 1801aac90 from 1801ab75d: the silhouette, from its lowest vertex (the first of equals), walked forward (or
    /// backward when mirrored) without repeated points, summed with the rectangle of half a tile: each vertex takes
    /// the rectangle corners (-, -), (+, -), (+, +), (-, +) in turn while the edge out of it turns past them. The
    /// bounds are the result's.
    /// </summary>
    private static void Expand(Footprint fp, bool mirrored, float tileX, float tileY)
    {
        var n = fp.Silhouette.Count;
        if (n == 0)
            return;
        Vector2[] corner = [new(-tileX, -tileY), new(tileX, -tileY), new(tileX, tileY), new(-tileX, tileY)];
        var best = 0;
        for (var i = 1; i < n; i++)
            if (!(fp.Silhouette[best].Y < fp.Silhouette[i].Y || fp.Silhouette[best].Y == fp.Silhouette[i].Y))
                best = i;
        var step = mirrored ? n - 1 : 1;
        var list = new List<Vector2>();
        var cur = best;
        var visited = 0;
        while (true)
        {
            var v = fp.Silhouette[cur];
            list.Add(v);
            var closed = false;
            do
            {
                cur = (cur + step) % n;
                if (++visited >= n)
                {
                    closed = true;
                    break;
                }
            }
            while (fp.Silhouette[cur].X == v.X && fp.Silhouette[cur].Y == v.Y);
            if (closed)
                break;
        }
        var m = list.Count;
        list.Add(list[0]);
        var hull = new List<Vector2>();
        int j = 0, q = 0;
        while (true)
        {
            if (q > 3)
            {
                for (; j < m; j++)
                    hull.Add(list[j] + corner[0]);
                break;
            }
            hull.Add(new Vector2(list[j].X + corner[q].X, list[j].Y + corner[q].Y));
            var e = list[j + 1].X - list[j].X;
            if ((q & 1) == 0)
                e = -(list[j + 1].Y - list[j].Y);
            if ((q & 2) != 0)
                e = -e;
            if (e <= 0f)
            {
                if (0f <= e)
                    j++;
                q++;
            }
            else
                j++;
            if (m <= j)
            {
                for (; q < 4; q++)
                    hull.Add(new Vector2(list[j].X + corner[q].X, list[j].Y + corner[q].Y));
                break;
            }
        }
        fp.Hull = hull;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = -float.MaxValue, maxY = -float.MaxValue;
        foreach (var p in hull)
        {
            minX = p.X <= minX ? p.X : minX;
            minY = p.Y <= minY ? p.Y : minY;
            maxX = maxX <= p.X ? p.X : maxX;
            maxY = maxY <= p.Y ? p.Y : maxY;
        }
        (fp.MinX, fp.MinY, fp.MaxX, fp.MaxY) = (minX, minY, maxX, maxY);
    }

    /// <summary>1801ad810: the hull's edges as (nx, ny, d, 0) lines; <paramref name="pixelDiagonal"/> is 1801b2850's sqrt(1/w^2 + 1/h^2).</summary>
    public static List<Vector4> Edges(IReadOnlyList<Vector2> hull, float pixelDiagonal)
    {
        var edges = new List<Vector4>();
        var n = hull.Count;
        if (n == 0)
            return edges;
        var quarter = pixelDiagonal * 0.25f;
        var twice = pixelDiagonal + pixelDiagonal;
        var min2 = twice * twice;
        for (var i = 0; i < n; i++)
        {
            var prev = hull[i == 0 ? n - 1 : i - 1];
            var dx = hull[i].X - prev.X;
            var ndy = -(hull[i].Y - prev.Y);
            var len2 = ndy * ndy + dx * dx;
            if (!(min2 <= len2))
                continue;
            var r = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(len2)).ToScalar();
            var inv = (3f - r * len2 * r) * (r * 0.5f);   // 1801ad916: the half of r taken first
            var nx = ndy * inv;
            var ny = dx * inv;
            if (edges.Count == 0 || 0f <= ny * edges[^1].X - nx * edges[^1].Y)
                edges.Add(new Vector4(nx, ny, quarter - (ny * hull[i].Y + nx * hull[i].X), 0f));
        }
        if (edges.Count > 2 && edges[0].Y * edges[^1].X - edges[^1].Y * edges[0].X < 0f)
        {
            edges[0] = edges[^1];
            edges.RemoveAt(edges.Count - 1);
        }
        return edges;
    }

    /// <summary>
    /// 1801b2850's item records for barn lights (the binner's first items): per light the edges of its frustum's
    /// footprint, then of its illumination box's when it has one (counts at words 2 and 3), the inside flags ANDed,
    /// the depth range and screen bounds of the two intersected (the larger minimum, the smaller maximum), the bounds
    /// widened by a tile; the items padded to a multiple of 32 with records of bounds -100.
    /// </summary>
    public static (byte[] Records, byte[] Edges) PackLights(View view, IReadOnlyList<(Footprint Frustum, Footprint? Box)> lights)
    {
        float iw = 1f / view.Width, ih = 1f / view.Height;
        var diagonal = MathF.Sqrt(iw * iw + ih * ih);
        float tileX = (float)(uint)view.TilePixels / view.Width, tileY = (float)(uint)view.TilePixels / view.Height;
        var padded = (lights.Count + 31) / 32 * 32;
        var records = new byte[padded * 80];
        var edges = new List<Vector4>();
        for (var i = 0; i < lights.Count; i++)
        {
            var (f, box) = lights[i];
            var r = records.AsSpan(i * 80);
            BinaryPrimitives.WriteInt32LittleEndian(r, edges.Count);
            var inside = f.Inside;
            var frustumEdges = Edges(f.Hull, diagonal);
            edges.AddRange(frustumEdges);
            BinaryPrimitives.WriteInt32LittleEndian(r[8..], frustumEdges.Count);
            float minDepth = f.MinDepth, maxDepth = f.MaxDepth, minX = f.MinX, minY = f.MinY, maxX = f.MaxX, maxY = f.MaxY;
            if (box != null)
            {
                minDepth = minDepth <= box.MinDepth ? box.MinDepth : minDepth;
                maxDepth = box.MaxDepth <= maxDepth ? box.MaxDepth : maxDepth;
                inside &= box.Inside;
                var boxEdges = Edges(box.Hull, diagonal);
                edges.AddRange(boxEdges);
                BinaryPrimitives.WriteInt32LittleEndian(r[12..], boxEdges.Count);
                minX = minX <= box.MinX ? box.MinX : minX;
                minY = minY <= box.MinY ? box.MinY : minY;
                maxX = box.MaxX <= maxX ? box.MaxX : maxX;
                maxY = box.MaxY <= maxY ? box.MaxY : maxY;
            }
            BinaryPrimitives.WriteInt32LittleEndian(r[4..], inside ? 1 : 0);
            BinaryPrimitives.WriteSingleLittleEndian(r[16..], minX - tileX);
            BinaryPrimitives.WriteSingleLittleEndian(r[20..], minY - tileY);
            BinaryPrimitives.WriteSingleLittleEndian(r[24..], tileX + maxX);
            BinaryPrimitives.WriteSingleLittleEndian(r[28..], tileY + maxY);
            BinaryPrimitives.WriteSingleLittleEndian(r[32..], minDepth);
            BinaryPrimitives.WriteSingleLittleEndian(r[36..], maxDepth);
        }
        for (var i = lights.Count; i < padded; i++)
            for (var k = 0; k < 4; k++)
                BinaryPrimitives.WriteSingleLittleEndian(records.AsSpan(i * 80 + 16 + k * 4), -100f);
        var edgeBytes = new byte[edges.Count * 16];
        for (var i = 0; i < edges.Count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(edgeBytes.AsSpan(i * 16), edges[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(edgeBytes.AsSpan(i * 16 + 4), edges[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(edgeBytes.AsSpan(i * 16 + 8), edges[i].Z);
            BinaryPrimitives.WriteSingleLittleEndian(edgeBytes.AsSpan(i * 16 + 12), edges[i].W);
        }
        return (records, edgeBytes);
    }

    /// <summary>
    /// 1801b2850's item records (80 bytes each) and edge lines (16 bytes each) for the light probe volumes, after
    /// <paramref name="itemsBefore"/> items (lights, environment maps) with <paramref name="edgesBefore"/> edges:
    /// first edge, inside flag, edge count, 0, the bounds widened by a tile, the depth range, the rest 0; the items
    /// padded to a multiple of 32 with records of bounds -100. The edges' pixel diagonal is sqrt(1/w^2 + 1/h^2).
    /// </summary>
    public static (byte[] Records, byte[] Edges) Pack(View view, IReadOnlyList<Footprint> volumes, int itemsBefore = 0, int edgesBefore = 0)
    {
        float iw = 1f / view.Width, ih = 1f / view.Height;
        var diag2 = iw * iw + ih * ih;
        var diagonal = MathF.Sqrt(diag2);
        float tileX = (float)(uint)view.TilePixels / view.Width, tileY = (float)(uint)view.TilePixels / view.Height;
        var items = itemsBefore + volumes.Count;
        var padded = (items + 31) / 32 * 32;
        var records = new byte[(padded - itemsBefore) * 80];
        var edges = new List<Vector4>();
        for (var i = 0; i < volumes.Count; i++)
        {
            var fp = volumes[i];
            var r = records.AsSpan(i * 80);
            var e = Edges(fp.Hull, diagonal);
            BinaryPrimitives.WriteInt32LittleEndian(r, edgesBefore + edges.Count);
            BinaryPrimitives.WriteInt32LittleEndian(r[4..], fp.Inside ? 1 : 0);
            BinaryPrimitives.WriteInt32LittleEndian(r[8..], e.Count);
            BinaryPrimitives.WriteSingleLittleEndian(r[16..], fp.MinX - tileX);
            BinaryPrimitives.WriteSingleLittleEndian(r[20..], fp.MinY - tileY);
            BinaryPrimitives.WriteSingleLittleEndian(r[24..], tileX + fp.MaxX);
            BinaryPrimitives.WriteSingleLittleEndian(r[28..], tileY + fp.MaxY);
            BinaryPrimitives.WriteSingleLittleEndian(r[32..], fp.MinDepth);
            BinaryPrimitives.WriteSingleLittleEndian(r[36..], fp.MaxDepth);
            edges.AddRange(e);
        }
        for (var i = volumes.Count; i < padded - itemsBefore; i++)
            for (var k = 0; k < 4; k++)
                BinaryPrimitives.WriteSingleLittleEndian(records.AsSpan(i * 80 + 16 + k * 4), -100f);
        var edgeBytes = new byte[edges.Count * 16];
        for (var i = 0; i < edges.Count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(edgeBytes.AsSpan(i * 16), edges[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(edgeBytes.AsSpan(i * 16 + 4), edges[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(edgeBytes.AsSpan(i * 16 + 8), edges[i].Z);
            BinaryPrimitives.WriteSingleLittleEndian(edgeBytes.AsSpan(i * 16 + 12), edges[i].W);
        }
        return (records, edgeBytes);
    }
}
