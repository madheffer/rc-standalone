using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The kd tree the compile's tracer builds for itself when it loads the
/// <c>.rte</c>, and the box query the voxelizer runs through it.
///
/// <para>The file carries a kd tree of its own and the compile does not use it:
/// <c>CVisibilityMesh::LoadRTEFromFile</c> re-adds the triangles to a fresh
/// environment whose setup builds a new one (<c>RefineNode</c>). On probe01
/// that is 65 nodes against the file's 181. It matters because occupancy is
/// only ever asked THROUGH the tree: a box whose minimum lies on a split plane
/// descends the upper side only, so a triangle filed solely below that plane is
/// never tested against it, however it touches the box. That is a boundary
/// rule, and walls meet voxel faces on exactly such planes.</para>
///
/// <para>Node numbering in the compile depends on its thread pool; the shape,
/// the splits and each leaf's triangles in order do not, and those are all a
/// query can observe.</para>
/// </summary>
internal sealed class TracerKd
{
    /// <summary>A leaf holds at most this many triangles before a split is tried.</summary>
    public const int LeafSize = 10;

    /// <summary>Depth at which splitting stops.</summary>
    public const int MaxDepth = 22;

    /// <summary>Cost of testing a triangle, and of stepping a node.</summary>
    public const float Intersect = 167f, Traverse = 75f;

    private readonly RayTraceEnvironment _rte;
    private readonly float[] _bounds;   // per slot: minx maxx miny maxy minz maxz
    private readonly int[] _slots;      // slot -> file triangle
    private readonly List<Node> _nodes = [];

    /// <summary>One node: a split with its two children, or a leaf's slots.</summary>
    /// <param name="Axis">0 to 2, or 3 for a leaf.</param>
    /// <param name="Split">Where the plane lies.</param>
    /// <param name="Lower">The child below the plane; the one above is next to it.</param>
    /// <param name="Slots">A leaf's triangles, as tracer slots, in order.</param>
    public sealed record Node(int Axis, float Split, int Lower, int[] Slots);

    public IReadOnlyList<Node> Nodes => _nodes;

    public TracerKd(RayTraceEnvironment rte)
    {
        _rte = rte;
        _slots = rte.TracerOrder;
        _bounds = new float[_slots.Length * 6];
        for (var s = 0; s < _slots.Length; s++)
        {
            var p = rte.LoaderCorners(_slots[s]);
            for (var axis = 0; axis < 3; axis++)
            {
                // The setup's own order of <= swaps, v0 then v1 then v2.
                float v0 = p[axis], v1 = p[3 + axis], v2 = p[6 + axis];
                var lo = v0;
                var hi = v0;
                if (v0 <= v1)
                    hi = v1;
                if (v1 <= v0)
                    lo = v1;
                if (hi <= v2)
                    hi = v2;
                if (v2 <= lo)
                    lo = v2;
                _bounds[(s * 6) + (axis * 2)] = lo;
                _bounds[(s * 6) + (axis * 2) + 1] = hi;
            }
        }

        var (mins, maxs) = rte.TracedBounds;
        _nodes.Add(null!);
        Refine(0, [.. Enumerable.Range(0, _slots.Length)], mins, maxs, 0);
    }

    // RefineNode: the best of twelve candidate planes, split if it pays, else a leaf.
    private void Refine(int at, int[] list, Vector3 mins, Vector3 maxs, int depth)
    {
        if (LeafSize < list.Length && depth < MaxDepth)
        {
            var best = (Cost: 1e23f, Split: 0f, Left: 0, Right: 0, Both: 0, Axis: 0);
            for (var axis = 0; axis < 3; axis++)
                for (var corner = -1; corner < 3; corner++)
                {
                    var found = Candidate(axis, corner, list, mins, maxs);
                    if (found.Cost < best.Cost)
                        best = found;
                }

            if (best.Cost < list.Length * Intersect && (list.Length > 19 || (best.Left != 0 && best.Right != 0)))
            {
                var lower = new List<int>(best.Left + best.Both);
                var upper = new List<int>(best.Right + best.Both);
                foreach (var s in list)
                {
                    var lo = _bounds[(s * 6) + (best.Axis * 2)];
                    var hi = _bounds[(s * 6) + (best.Axis * 2) + 1];
                    if (best.Split <= lo)
                    {
                        upper.Add(s);
                    }
                    else if (best.Split < hi)
                    {
                        if (lo == hi)
                        {
                            upper.Add(s);
                        }
                        else
                        {
                            lower.Add(s);
                            upper.Add(s);
                        }
                    }
                    else
                    {
                        lower.Add(s);
                    }
                }

                var child = _nodes.Count;
                _nodes.Add(null!);
                _nodes.Add(null!);
                _nodes[at] = new Node(best.Axis, best.Split, child, []);
                var lowerMaxs = With(maxs, best.Axis, best.Split);
                var upperMins = With(mins, best.Axis, best.Split);
                Refine(child, [.. lower], mins, lowerMaxs, depth + 1);
                Refine(child + 1, [.. upper], upperMins, maxs, depth + 1);
                return;
            }
        }
        _nodes[at] = new Node(3, 0f, -1, list);
    }

    // FUN_180119600: one candidate kind on one axis. -1 is the box's middle,
    // tried once; 0 to 2 are that corner of every (n / 10 + 1)th triangle.
    private (float Cost, float Split, int Left, int Right, int Both, int Axis) Candidate(
        int axis, int corner, int[] list, Vector3 mins, Vector3 maxs)
    {
        var best = (Cost: 1e23f, Split: 0f, Left: 0, Right: 0, Both: 0, Axis: axis);
        var lo = Axis(mins, axis);
        var hi = Axis(maxs, axis);
        for (var k = 0; k < list.Length; k += (list.Length / 10) + 1)
        {
            float split;
            if (corner == -1)
            {
                split = (lo + hi) * 0.5f;
            }
            else
            {
                split = _rte.LoaderCorners(_slots[list[k]])[(corner * 3) + axis];
                if (!(split <= hi && lo <= split))
                    continue;
            }
            var found = Cost(axis, split, list, mins, maxs);
            if (found.Cost < best.Cost)
                best = found;
            if (corner == -1)
                break;
        }
        return best;
    }

    // FUN_180118a90: the surface area cost of a plane, which also moves a plane
    // that leaves one side empty onto the edge of what it holds.
    private (float Cost, float Split, int Left, int Right, int Both, int Axis) Cost(
        int axis, float split, int[] list, Vector3 mins, Vector3 maxs)
    {
        int left = 0, right = 0, both = 0;
        var lowest = 1e23f;
        var highest = -1e23f;
        foreach (var s in list)
        {
            var lo = _bounds[(s * 6) + (axis * 2)];
            var hi = _bounds[(s * 6) + (axis * 2) + 1];
            if (lo <= lowest)
                lowest = lo;
            if (highest <= hi)
                highest = hi;
            if (lo < split)
            {
                if (split < hi)
                {
                    if (lo != hi)
                        both++;
                    else
                        right++;
                }
                else
                {
                    left++;
                }
            }
            else
            {
                right++;
            }
        }

        var moved = split;
        if (left == 0 || both != 0)
        {
            if (right != 0 && both == 0 && left == 0)
                moved = 0f <= lowest ? (lowest != 0f ? (int)lowest - 1 : -1.1920929e-07f) : (int)lowest + 1;
        }
        else if (right == 0)
        {
            moved = highest;
            if (highest <= split)
                moved = split;
        }

        var lowerMaxs = With(maxs, axis, moved);
        var upperMins = With(mins, axis, moved);
        var full = Half(maxs - mins);
        var below = Half(lowerMaxs - mins);
        var above = Half(maxs - upperMins);
        var scale = 1f / (full + full);
        var cost = (((below + below) * scale * left) + both + ((above + above) * scale * right))
                 * Intersect + Traverse;
        return (cost, moved, left, right, both, axis);
    }

    // Half a box's surface, summed as the binary sums it: dz*dx + dy*dx + dz*dy.
    private static float Half(Vector3 d) => (d.Z * d.X) + (d.Y * d.X) + (d.Z * d.Y);

    /// <summary>
    /// Whether any triangle not masked out reaches the box, which is the
    /// voxelizer's query: the walk the root takes in <c>Voxelize</c> and
    /// <c>LeafEntries</c>, then <c>BoxTraversal</c>, then <c>BoxOverlap</c>.
    /// The clipped box only steers the walk; every triangle is tested
    /// against the original.
    /// </summary>
    public bool Occupied(Vector3 mins, Vector3 maxs, ushort mask)
    {
        var centre = new Vector3((maxs.X + mins.X) * 0.5f, (maxs.Y + mins.Y) * 0.5f, (maxs.Z + mins.Z) * 0.5f);
        var half = new Vector3(maxs.X - centre.X, maxs.Y - centre.Y, maxs.Z - centre.Z);
        return Walk(0, mins, maxs, centre, half, mask);
    }

    private bool Walk(int at, Vector3 clipMins, Vector3 clipMaxs, Vector3 centre, Vector3 half, ushort mask)
    {
        var node = _nodes[at];
        if (node.Axis == 3)
        {
            foreach (var s in node.Slots)
            {
                var triangle = _slots[s];
                if ((_rte.Flags(triangle) & mask) != 0 || _rte.TracedCorners(triangle) is not { } p)
                    continue;
                if (VisVoxelizer.Overlaps(p[0], p[1], p[2], centre, half))
                    return true;
            }
            return false;
        }
        if (node.Split <= Axis(clipMins, node.Axis))
            return Walk(node.Lower + 1, clipMins, clipMaxs, centre, half, mask);
        if (Axis(clipMaxs, node.Axis) < node.Split)
            return Walk(node.Lower, clipMins, clipMaxs, centre, half, mask);
        return Walk(node.Lower + 1, With(clipMins, node.Axis, node.Split), clipMaxs, centre, half, mask)
            || Walk(node.Lower, clipMins, With(clipMaxs, node.Axis, node.Split), centre, half, mask);
    }

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static Vector3 With(Vector3 v, int axis, float value)
        => axis == 0 ? v with { X = value } : axis == 1 ? v with { Y = value } : v with { Z = value };
}
