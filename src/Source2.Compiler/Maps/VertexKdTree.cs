using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// tier0's <c>CVertexKDTree</c>: <c>BuildMidpoint</c> (180180730) and
/// <c>FindVertsInBox</c> (18017ef60). What callers depend on is the ORDER a box
/// query returns points in, which is the order the build's partition leaves
/// them in its leaves, so the build is ported swap for swap.
/// </summary>
public sealed class VertexKdTree
{
    private readonly Vector3[] _points;
    private readonly int[] _order;
    private readonly List<(int A, int B, int Axis, float Split)> _nodes = [];

    /// <summary><c>BuildMidpoint</c>: every point, in index order, then <c>BuildNode(0, n)</c>.</summary>
    public VertexKdTree(Vector3[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        _points = points;
        _order = Enumerable.Range(0, points.Length).ToArray();
        BuildNode(0, points.Length);
    }

    /// <summary>
    /// <c>BuildNode</c> (18017feb0): more than eight points split at the middle
    /// of the box's longest side (ties to the later axis as written), unless the
    /// partition leaves a side empty; the node is numbered before its children.
    /// </summary>
    private int BuildNode(int start, int count)
    {
        if (count > 8)
        {
            var (lo, hi) = Bounds(start, count);
            float dx = hi.X - lo.X, dy = hi.Y - lo.Y, dz = hi.Z - lo.Z;
            var axis = dx < dy ? (dy <= dz ? 2 : 1) : (dz < dx ? 0 : 2);
            var split = (Get(lo, axis) + Get(hi, axis)) * 0.5f;
            var mid = Partition(start, count, axis, split);
            var right = start - mid + count;
            if (mid - start != 0 && right != 0)
            {
                var node = _nodes.Count;
                _nodes.Add(default);
                var a = BuildNode(start, mid - start);
                var b = BuildNode(mid, right);
                _nodes[node] = (a, b, axis, split);
                return node;
            }
        }
        _nodes.Add((start, count, 0xff, 0f));
        return _nodes.Count - 1;
    }

    // 18017fc30: the points' box, min and max kept on ties.
    private (Vector3 Lo, Vector3 Hi) Bounds(int start, int count)
    {
        var lo = _points[_order[start]];
        var hi = lo;
        for (var k = start + 1; k < start + count; k++)
        {
            var p = _points[_order[k]];
            lo = new(p.X <= lo.X ? p.X : lo.X, p.Y <= lo.Y ? p.Y : lo.Y, p.Z <= lo.Z ? p.Z : lo.Z);
            hi = new(hi.X <= p.X ? p.X : hi.X, hi.Y <= p.Y ? p.Y : hi.Y, hi.Z <= p.Z ? p.Z : hi.Z);
        }
        return (lo, hi);
    }

    // 18017ebf0: from the middle up, a point below the split swaps down to the
    // boundary; then from the boundary down, a point at or above it swaps up.
    private int Partition(int start, int count, int axis, float split)
    {
        var mid = start + (count / 2);
        for (var k = mid; k < start + count; k++)
            if (Get(_points[_order[k]], axis) < split)
            {
                (_order[mid], _order[k]) = (_order[k], _order[mid]);
                mid++;
            }
        for (var k = mid - 1; k >= start; k--)
            if (split <= Get(_points[_order[k]], axis))
            {
                (_order[mid - 1], _order[k]) = (_order[k], _order[mid - 1]);
                mid--;
            }
        return mid;
    }

    /// <summary>
    /// <c>FindVertsInBox</c>: the indices of the points inside [lo, hi], in the
    /// order the walk meets them (left child when lo reaches the split, right
    /// child unless hi stops short of it).
    /// </summary>
    public void FindInBox(Vector3 lo, Vector3 hi, List<int> found, int node = 0)
    {
        ArgumentNullException.ThrowIfNull(found);
        while (true)
        {
            var (a, b, axis, split) = _nodes[node];
            if (axis == 0xff)
            {
                for (var k = a; k < a + b; k++)
                {
                    var p = _points[_order[k]];
                    if (lo.X <= p.X && p.X <= hi.X && lo.Y <= p.Y && p.Y <= hi.Y && lo.Z <= p.Z && p.Z <= hi.Z)
                        found.Add(_order[k]);
                }
                return;
            }
            if (Get(lo, axis) <= split)
                FindInBox(lo, hi, found, a);
            if (Get(hi, axis) < split)
                return;
            node = b;
        }
    }

    private static float Get(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
}
