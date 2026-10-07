using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// A world node's render clusters (<c>Step_BuildingRenderClusters</c>): boxes
/// split out of the node's triangle centroids, then each mesh's triangles
/// handed to a box (FUN_181366f30), which becomes the <c>_c&lt;index&gt;</c>
/// of the models built from them.
/// </summary>
public static class RenderClusters
{
    /// <summary>
    /// A triangle's centroid as the builder takes it: each axis summed second
    /// corner, first, third, times 0.33333334.
    /// </summary>
    public static Vector3 Centroid(Vector3 a, Vector3 b, Vector3 c)
        => new((b.X + a.X + c.X) * 0.33333334f, (b.Y + a.Y + c.Y) * 0.33333334f, (b.Z + a.Z + c.Z) * 0.33333334f);

    /// <summary>
    /// Whether an entry's triangles join the centroids the boxes are built from
    /// (Step_BuildingRenderClusters, 180282ef0): unless its attribute word's
    /// low half masked by 0x131 is exactly 1 (bit 0 without bits 4, 5 and 8).
    /// atixref: Valve's 88,857 centroids are those of exactly these entries,
    /// in entry order (capture_rclusters.py).
    /// </summary>
    public static bool Contributes(ulong attributes) => ((uint)attributes & 0x131) != 1;

    /// <summary>
    /// The cluster boxes over <paramref name="centroids"/> (FUN_180282bf0),
    /// each then grown by 1/32 a side (Step_BuildingRenderClusters); exact
    /// against Valve's 20 atixref boxes from its own centroids. A run is split along its box's longest axis
    /// while it holds at least twice <paramref name="minTriangles"/> and, with
    /// <paramref name="sizeSplit"/> off, that axis is at least
    /// <paramref name="size"/>; with it on, a smaller run still splits while
    /// the axis is at least <paramref name="size"/>. The split falls at the
    /// first centroid past the average of the box's middle and the median,
    /// kept between min(half, minTriangles) (at least 1) from either end; the
    /// lower part recurses, the upper part splits on in the same call, and
    /// the recursion stops 64 deep. The centroids are reordered.
    /// </summary>
    public static List<(Vector3 Min, Vector3 Max)> Build(Vector3[] centroids, int minTriangles, float size, bool sizeSplit)
    {
        const float grow = 0.03125f;
        return [.. Split(centroids, minTriangles, size, sizeSplit).Select(b => (b.Min - new Vector3(grow), b.Max + new Vector3(grow)))];
    }

    /// <summary>The boxes of FUN_180282bf0 alone, not grown (Step_BuildingRenderClusters grows every box it holds after each call).</summary>
    public static List<(Vector3 Min, Vector3 Max)> Split(Vector3[] centroids, int minTriangles, float size, bool sizeSplit)
    {
        var boxes = new List<(Vector3, Vector3)>();
        if (centroids.Length > 0)
            Split(boxes, minTriangles, size, sizeSplit, centroids, 0, centroids.Length, 0);
        return boxes;
    }

    private static void Split(List<(Vector3, Vector3)> boxes, int minTriangles, float size, bool sizeSplit,
                              Vector3[] points, int start, int count, int depth)
    {
        var (min, max) = Bounds(points, start, count);
        while (count > 1)
        {
            if (depth > 63)
                break;
            var ex = max.X - min.X;
            var ey = max.Y - min.Y;
            var ez = max.Z - min.Z;
            int axis;
            float extent;
            if (ex < ey || ex < ez)
                (axis, extent) = ex <= ey && ez <= ey ? (1, ey) : (2, ez);
            else
                (axis, extent) = (0, ex);
            if (!sizeSplit)
            {
                if (count < minTriangles * 2 || extent < size)
                    break;
            }
            else if (count < minTriangles * 2 && extent < size)
                break;
            // std::sort on the axis (FUN_180290c20, 180291040, 180291480): MSVC's,
            // so equal coordinates keep the binary's order.
            var slice = points[start..(start + count)];
            MsvcSort.Sort(slice, (p, q) => Axis(p, axis) < Axis(q, axis));
            Array.Copy(slice, 0, points, start, count);
            var half = count >> 1;
            var threshold = ((Axis(max, axis) + Axis(min, axis)) * 0.5f + Axis(points[start + half], axis)) * 0.5f;
            var at = -1;
            for (var i = 0; i < count; i++)
                if (threshold < Axis(points[start + i], axis))
                {
                    at = i;
                    break;
                }
            var clamp = Math.Min(half, minTriangles);
            if (clamp < 1)
                clamp = 1;
            var split = Math.Max(at, clamp);
            if (split > count - clamp)
                split = count - clamp;
            depth++;
            Split(boxes, minTriangles, size, sizeSplit, points, start, split, depth);
            start += split;
            count -= split;
            (min, max) = Bounds(points, start, count);
        }
        boxes.Add((min, max));
    }

    /// <summary>
    /// The cluster for each triangle of one mesh (FUN_181366f30): the box that
    /// took the mesh's previous triangle if it holds this centroid, else the
    /// first box that holds it, else the nearest box (first of equals).
    /// </summary>
    public static int[] Assign(IReadOnlyList<(Vector3 Min, Vector3 Max)> boxes, IEnumerable<Vector3> centroids)
    {
        var result = new List<int>();
        var last = -1;
        foreach (var p in centroids)
        {
            if (last < 0 || !Holds(boxes[last], p))
            {
                last = -1;
                for (var i = 0; i < boxes.Count; i++)
                    if (Holds(boxes[i], p))
                    {
                        last = i;
                        break;
                    }
                if (last < 0)
                {
                    var best = float.MaxValue;
                    for (var i = 0; i < boxes.Count; i++)
                    {
                        var d = Distance(boxes[i], p);
                        if (d < best)
                        {
                            best = d;
                            last = i;
                        }
                    }
                }
            }
            result.Add(last);
        }
        return [.. result];
    }

    private static bool Holds((Vector3 Min, Vector3 Max) b, Vector3 p)
        => b.Min.X <= p.X && b.Min.Y <= p.Y && b.Min.Z <= p.Z && p.X <= b.Max.X && p.Y <= b.Max.Y && p.Z <= b.Max.Z;

    // FUN_181256da0: the gap outside the box on each axis, summed z, y, x.
    private static float Distance((Vector3 Min, Vector3 Max) b, Vector3 p)
    {
        var dx = MathF.Max(p.X - b.Max.X, 0f) + MathF.Max(b.Min.X - p.X, 0f);
        var dy = MathF.Max(p.Y - b.Max.Y, 0f) + MathF.Max(b.Min.Y - p.Y, 0f);
        var dz = MathF.Max(p.Z - b.Max.Z, 0f) + MathF.Max(b.Min.Z - p.Z, 0f);
        return MathF.Sqrt(dz * dz + dy * dy + dx * dx);
    }

    private static (Vector3, Vector3) Bounds(Vector3[] points, int start, int count)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(-float.MaxValue);
        for (var i = start; i < start + count; i++)
        {
            min = Vector3.Min(min, points[i]);
            max = Vector3.Max(max, points[i]);
        }
        return (min, max);
    }

    /// <summary>
    /// The node's box grid (rc 180283520): cells of <paramref name="size"/>
    /// (the builder's m_flMinDrawVolumeSize) over the node bounds, the far
    /// corner moved to whole cells; past 0x1000 cells the bounds stay and each
    /// axis takes floor(extent / cbrt(volume / 4096)) cells, 1 to 64. The
    /// bounds grow by 0.01 and the boxes run x fastest, then y, then z.
    /// CompileNode clusters a node without vis (or with two vis clusters or
    /// fewer) by these, and makes them its render boxes when nothing else does.
    /// </summary>
    public static List<(Vector3 Min, Vector3 Max)> Grid(Vector3 min, Vector3 max, float size)
    {
        var (x0, y0, z0, x1, y1, z1) = (min.X, min.Y, min.Z, max.X, max.Y, max.Z);
        var (ex, ey, ez) = (x1 - x0, y1 - y0, z1 - z0);
        var inv = 1f / size;
        static int AtLeastOne(float f) => (int)f < 1 ? 1 : (int)f;
        long nx = AtLeastOne(MathF.Ceiling(inv * ex)), ny = AtLeastOne(MathF.Ceiling(inv * ey)), nz = AtLeastOne(MathF.Ceiling(inv * ez));
        if (nx * ny * nz < 0x1001)
        {
            z1 = z0 + (nz * size);
            x1 = (nx * size) + x0;
            y1 = (ny * size) + y0;
        }
        else
        {
            var cell = LightPow.Pow(ex * ey * ez * 0.00024414062f, 0.33333334f);
            var per = 1f / cell;
            static int Cells(float f) => Math.Min(AtLeastOne(MathF.Floor(f)), 64);
            (nx, ny, nz) = (Cells(per * ex), Cells(per * ey), Cells(per * ez));
        }
        var fz = z0 - 0.01f;
        var dz = ((z1 + 0.01f) - fz) / nz;
        var dy = ((y1 + 0.01f) - (y0 + -0.01f)) / ny;
        var dx = ((x1 + 0.01f) - (x0 + -0.01f)) / nx;
        var boxes = new List<(Vector3 Min, Vector3 Max)>((int)(nx * ny * nz));
        for (var k = 0; k < nz; k++, fz += dz)
        {
            var fy = y0 + -0.01f;
            for (var j = 0; j < ny; j++, fy += dy)
            {
                var fx = x0 + -0.01f;
                for (var i = 0; i < nx; i++)
                {
                    var minX = fx;
                    fx = dx + fx;
                    boxes.Add((new Vector3(minX, fy, fz), new Vector3(fx, dy + fy, dz + fz)));
                }
            }
        }
        return boxes;
    }

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
}
