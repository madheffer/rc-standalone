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
    /// The cluster boxes over <paramref name="centroids"/> (FUN_180282bf0),
    /// each grown by 1/32 a side. A run is split along its box's longest axis
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
        var boxes = new List<(Vector3, Vector3)>();
        if (centroids.Length > 0)
            Split(boxes, minTriangles, size, sizeSplit, centroids, 0, centroids.Length, 0);
        const float grow = 0.03125f;
        return [.. boxes.Select(b => (b.Item1 - new Vector3(grow), b.Item2 + new Vector3(grow)))];
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

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
}
