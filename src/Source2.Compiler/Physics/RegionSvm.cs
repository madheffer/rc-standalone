using System.Numerics;

namespace Source2.Compiler.Physics;

/// <summary>
/// A hull's region SVM (<c>CRegionSVM</c>, <c>m_pRegionSVM</c>): a plane tree
/// that takes a point to the hull feature nearest it. Its leaves are the
/// hull's Voronoi regions: the inside (0), a vertex (0x20000000, its first
/// outgoing half-edge shifted 8, the vertex), an edge (0x40000000 and the
/// even half-edge) or a face (0x60000000 and the face). A split node is
/// 0x8000 and the plane in its top half and, in its bottom half, how far past
/// the next node its front child sits; the back child follows the front one.
/// </summary>
public sealed class RegionSvm
{
    public (Vector3 Normal, float Offset)[] Planes = [];
    public uint[] Nodes = [];
}

/// <summary>
/// The map builder's region SVM build (FUN_180c253c0 runs it on a new hull
/// before moving it by the shape's matrix): FUN_1819d08a0 lays out the
/// regions and their planes, FUN_1819d2250 splits them and FUN_1819d25c0
/// writes the tree.
///
/// <para>Everything is relative to the hull's centroid. A face region is
/// bounded by its face plane and one plane per edge, an edge region by the
/// two planes of each of its half-edges, and each region is a point list in
/// homogeneous form: a hull vertex has w -1, a face normal (a direction the
/// region runs off to) w 0.</para>
///
/// <para>Not ported: the clipping of a region a split crosses, which a flag
/// no code sets turns off, and the normalising of a degenerate edge plane
/// (FUN_18125d000), which unit face normals never reach.</para>
/// </summary>
public static class RegionSvmBuilder
{
    private sealed class Region
    {
        public uint Kind;
        public int[] Points = [];
        // A plane per bounding face: twice the plane index, plus 1 when the
        // region lies behind the plane as stored (0: behind it negated); -2
        // for the plane at infinity.
        public int[] Codes = [];
        // Half-edges in twin pairs: the point each starts at and its face.
        public (int Point, int Face)[] Edges = [];
    }

    private sealed class Node(List<int> regions)
    {
        public List<int> Regions = regions;
        public int Plane = int.MaxValue;
        public int Front = -1;
    }

    /// <summary>The hull's region SVM.</summary>
    public static RegionSvm Build(RnHull hull)
    {
        var c = hull.Centroid;
        var positions = hull.VertexPositions;
        var edges = hull.Edges;
        int faceCount = hull.Faces.Length, vertexCount = positions.Length, edgeCount = edges.Length;
        var planes = new List<Vector4>(faceCount + (edgeCount * 2));
        var points = new Vector4[faceCount + vertexCount];

        // Face planes through the mean of their corners.
        for (var f = 0; f < faceCount; f++)
        {
            var n = hull.Planes[f].Normal;
            var sum = 0f;
            var count = 0;
            var e = hull.Faces[f];
            do
            {
                var p = positions[edges[e].Origin];
                sum += (((p.Z - c.Z) * n.Z) + ((p.Y - c.Y) * n.Y)) + ((p.X - c.X) * n.X);
                count++;
                e = edges[e].Next;
            } while (e != hull.Faces[f]);
            planes.Add(new Vector4(n, sum / count));
            points[f] = new Vector4(n, 0f);
        }
        for (var v = 0; v < vertexCount; v++)
        {
            var p = positions[v];
            points[faceCount + v] = new Vector4(p.X - c.X, p.Y - c.Y, p.Z - c.Z, -1f);
        }

        // Two planes per half-edge: one along the edge (or across its faces,
        // whichever vector is longer) and one square to that and its face.
        var faceEdges = new int[faceCount];
        var vertexFirst = new int[vertexCount];
        var vertexEdges = new int[vertexCount];
        for (var i = 0; i < edgeCount; i++)
        {
            var e = edges[i];
            var t = edges[i ^ 1];
            faceEdges[e.Face]++;
            if (vertexEdges[e.Origin]++ == 0)
                vertexFirst[e.Origin] = i;
            var pa = positions[e.Origin];
            var pb = positions[t.Origin];
            float ax = pa.X - c.X, ay = pa.Y - c.Y, az = pa.Z - c.Z;
            float dx = ax - (pb.X - c.X), dy = ay - (pb.Y - c.Y), dz = az - (pb.Z - c.Z);
            var lengthD = MathF.Sqrt(((dz * dz) + (dy * dy)) + (dx * dx));
            var f = hull.Planes[e.Face].Normal;
            var g = hull.Planes[t.Face].Normal;
            var cx = (f.Z * g.Y) - (g.Z * f.Y);
            var cy = (f.X * g.Z) - (g.X * f.Z);
            var cz = (g.X * f.Y) - (f.X * g.Y);
            var lengthC = MathF.Sqrt(((cz * cz) + (cy * cy)) + (cx * cx));
            float nx, ny, nz;
            if (lengthD > lengthC)
            {
                var inv = 1f / lengthD;
                (nx, ny, nz) = (inv * dx, inv * dy, dz * inv);
            }
            else
            {
                var inv = 1f / lengthC;
                (nx, ny, nz) = (inv * cx, inv * cy, inv * cz);
            }
            planes.Add(new Vector4(nx, ny, nz, ((nz * az) + (ny * ay)) + (nx * ax)));
            var mx = (ny * f.Z) - (nz * f.Y);
            var mz = (nx * f.Y) - (f.X * ny);
            var my = (f.X * nz) - (nx * f.Z);
            var lengthM = MathF.Sqrt(((my * my) + (mz * mz)) + (mx * mx));
            if (1e-17f > lengthM || lengthM > 1e17f)
                throw new NotSupportedException("region SVM: a degenerate edge plane (FUN_18125d000) is not ported");
            var invM = 1f / lengthM;
            (mx, my, mz) = (invM * mx, my * invM, mz * invM);
            planes.Add(new Vector4(mx, my, mz, ((az * mz) + (my * ay)) + (mx * ax)));
        }

        var regions = new List<Region>
        {
            new()
            {
                Kind = 0,
                Points = [.. Enumerable.Range(faceCount, vertexCount)],
                Codes = [.. Enumerable.Range(0, faceCount).Select(f => (f * 2) + 1)],
                Edges = [.. edges.Select(x => (x.Origin + faceCount, (int)x.Face))],
            },
        };
        for (var v = 0; v < vertexCount; v++)
        {
            var around = new List<int>();
            var start = vertexFirst[v] ^ 1;
            var b = start;
            do
            {
                around.Add(b);
                b = edges[b].Next ^ 1;
            } while (b != start);
            regions.Add(new Region
            {
                Kind = 0x20000000u | ((uint)vertexFirst[v] << 8) | (uint)v,
                Points = [.. around.Select(x => (int)edges[x ^ 1].Face), faceCount + v],
                Codes = [.. around.Select(x => (faceCount + ((x ^ 1) * 2)) * 2), -2],
                Edges = [.. Enumerable.Range(0, around.Count).SelectMany(k => new[]
                {
                    ((int)edges[around[k]].Face, around.Count),
                    (edges[around[k] ^ 1].Face, k),
                    (edges[around[k] ^ 1].Face, (k + around.Count - 1) % around.Count),
                    (faceCount + v, k),
                })],
            });
        }
        for (var e = 0; e < edgeCount; e += 2)
        {
            var t = e ^ 1;
            regions.Add(new Region
            {
                Kind = 0x40000000u | (uint)e,
                Points = [edges[e].Origin + faceCount, edges[t].Origin + faceCount, edges[e].Face, edges[t].Face],
                Codes = [((faceCount + (e * 2)) * 2) + 1, ((faceCount + (e * 2) + 1) * 2) + 1, ((faceCount + (t * 2)) * 2) + 1, ((faceCount + (t * 2) + 1) * 2) + 1],
                Edges = EdgeRegionEdges(edges[e].Face, edges[t].Face, edges[e].Origin + faceCount, edges[t].Origin + faceCount),
            });
        }
        for (var f = 0; f < faceCount; f++)
        {
            var loop = new List<int>();
            var e = (int)hull.Faces[f];
            do
            {
                loop.Add(e);
                e = edges[e].Next;
            } while (e != hull.Faces[f]);
            regions.Add(new Region
            {
                Kind = 0x60000000u | (uint)f,
                Points = [.. loop.Select(x => edges[x].Origin + faceCount), f],
                Codes = [.. loop.Select(x => ((faceCount + (x * 2)) * 2) + 2), f * 2],
                Edges = [.. Enumerable.Range(0, loop.Count).SelectMany(k => new[]
                {
                    (edges[loop[(k + 1) % loop.Count]].Origin + faceCount, loop.Count),
                    (edges[loop[k]].Origin + faceCount, k),
                    (edges[loop[(k + 1) % loop.Count]].Origin + faceCount, k),
                    (f, (k + 1) % loop.Count),
                })],
            });
        }

        // FUN_1819d2250: split breadth first until each node holds one region.
        var nodes = new List<Node> { new([.. Enumerable.Range(0, regions.Count)]) };
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node.Regions.Count <= 1)
                continue;
            var sides = new byte[node.Regions.Count];
            var plane = Choose(node, regions, planes, points, sides);
            if (plane == int.MaxValue)
                plane = Separate(node, regions, planes, points, sides);
            node.Plane = plane;
            node.Front = nodes.Count;
            var front = new Node([]);
            var back = new Node([]);
            nodes.Add(front);
            nodes.Add(back);
            for (var k = 0; k < sides.Length; k++)
            {
                if (sides[k] != 2)
                    back.Regions.Add(node.Regions[k]);
                if (sides[k] != 0)
                    front.Regions.Add(node.Regions[k]);
            }
        }

        // FUN_1819d25c0: planes back off the centroid.
        var svm = new RegionSvm { Planes = new (Vector3, float)[planes.Count], Nodes = new uint[nodes.Count] };
        for (var i = 0; i < planes.Count; i++)
        {
            var p = planes[i];
            svm.Planes[i] = (new Vector3(p.X, p.Y, p.Z), (((c.Z * p.Z) + (p.Y * c.Y)) + (c.X * p.X)) + p.W);
        }
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            svm.Nodes[i] = node.Regions.Count switch
            {
                0 => 0u,
                1 => regions[node.Regions[0]].Kind,
                _ => (uint)(node.Front - i - 1) | ((uint)(node.Plane | unchecked((int)0xffff8000)) << 16),
            };
        }
        return svm;
    }

    /// <summary>
    /// FUN_1819d2c80: of the planes bounding the node's regions, the one
    /// with the largest product of regions wholly in front and wholly behind.
    /// Planes that bound regions on both sides are tried first, the rest only
    /// when none of those splits. Ties keep the lower plane.
    /// </summary>
    private static int Choose(Node node, List<Region> regions, List<Vector4> planes, Vector4[] points, byte[] best)
    {
        var used = new bool[planes.Count * 2];
        foreach (var r in node.Regions)
            foreach (var code in regions[r].Codes)
                if ((code & ~1) != -2)
                    used[code] = true;
        var both = new List<int>();
        var one = new List<int>();
        for (var bit = 0; bit < used.Length; bit += 2)
        {
            if (used[bit] && used[bit + 1])
                both.Add(bit / 2);
            else if (used[bit] || used[bit + 1])
                one.Add(bit / 2);
        }
        var sides = new byte[node.Regions.Count];
        var bestScore = long.MinValue;
        var bestPlane = int.MaxValue;
        foreach (var list in new[] { both, one })
        {
            foreach (var p in list)
            {
                int behind = 0, front = 0;
                for (var k = 0; k < node.Regions.Count; k++)
                {
                    sides[k] = Side(regions[node.Regions[k]], planes[p], points);
                    if (sides[k] == 0)
                        behind++;
                    else if (sides[k] == 2)
                        front++;
                }
                if (front == 0 || behind == 0 || front * behind <= bestScore)
                    continue;
                bestScore = front * behind;
                bestPlane = p;
                sides.CopyTo(best, 0);
            }
            if (bestPlane != int.MaxValue)
                break;
        }
        return bestPlane;
    }

    /// <summary>
    /// FUN_1819d2740: when no region plane splits the node, the pair of its
    /// regions whose separating plane leaves the most regions wholly on
    /// either side, the later region of the pair behind. Pairs run from the
    /// last region back, each against those before it; the plane is added.
    /// </summary>
    private static int Separate(Node node, List<Region> regions, List<Vector4> planes, Vector4[] points, byte[] best)
    {
        var list = node.Regions;
        var sides = new byte[list.Count];
        var bestScore = long.MinValue;
        var bestPlane = Vector4.Zero;
        for (var a = list.Count - 1; a >= 1; a--)
        {
            for (var b = a - 1; b >= 0; b--)
            {
                var plane = Separator(regions[list[b]], regions[list[a]], planes, points);
                sides[b] = 2;
                sides[a] = 0;
                int behind = 1, front = 1;
                for (var k = 0; k < list.Count; k++)
                {
                    if (k == a || k == b)
                        continue;
                    sides[k] = Side(regions[list[k]], plane, points);
                    if (sides[k] == 0)
                        behind++;
                    else if (sides[k] == 2)
                        front++;
                }
                if (front * behind <= bestScore)
                    continue;
                bestScore = front * behind;
                bestPlane = plane;
                sides.CopyTo(best, 0);
            }
        }
        planes.Add(bestPlane);
        return planes.Count - 1;
    }

    /// <summary>
    /// FUN_1819d3600: a plane with <paramref name="front"/> in front and
    /// <paramref name="back"/> behind. The widest gap wins among planes
    /// across pairs of their edges (through the midpoint of the two edge
    /// points, if the edges face each other) and, unless that gap is already
    /// clear of -1.19e-7, the regions' own bounding planes.
    /// </summary>
    private static Vector4 Separator(Region front, Region back, List<Vector4> planes, Vector4[] points)
    {
        var best = -float.MaxValue;
        var result = Vector4.Zero;
        if (front.Edges.Length > 0)
        {
            for (var i = 0; i < front.Edges.Length; i += 2)
            {
                if (!EdgeOf(front, i, planes, points, out var p1, out var p2, out var frontDir, out var frontOrigin))
                    continue;
                for (var j = 0; j < back.Edges.Length; j += 2)
                {
                    if (!Bound(back, back.Edges[j].Face, planes, out var fa) || !Bound(back, back.Edges[j ^ 1].Face, planes, out var ga))
                        continue;
                    if (!Span(points[back.Edges[j].Point], points[back.Edges[j ^ 1].Point], out var dir, out var origin))
                        continue;
                    var mask = (Negative(Dot3(dir, p1)) ? 1 : 0) | (Negative(Dot3(dir, p2)) ? 2 : 0)
                        | (Negative(Dot3(frontDir, fa)) ? 4 : 0) | (Negative(Dot3(frontDir, ga)) ? 8 : 0);
                    Vector3 n;
                    if (mask == 5)
                        n = new((dir.Z * frontDir.Y) - (dir.Y * frontDir.Z), (dir.X * frontDir.Z) - (dir.Z * frontDir.X), (dir.Y * frontDir.X) - (dir.X * frontDir.Y));
                    else if (mask == 10)
                        n = new((dir.Y * frontDir.Z) - (dir.Z * frontDir.Y), (dir.Z * frontDir.X) - (dir.X * frontDir.Z), (dir.X * frontDir.Y) - (dir.Y * frontDir.X));
                    else
                        continue;
                    var length = MathF.Sqrt((n.Z * n.Z) + ((n.X * n.X) + (n.Y * n.Y)));
                    if (1e-4f > length)
                        continue;
                    n = new(n.X / length, n.Y / length, n.Z / length);
                    var r = frontOrigin - origin;
                    var gap = ((n.Y * r.Y) + (n.X * r.X)) + (n.Z * r.Z);
                    if (!(gap > best))
                        continue;
                    best = gap;
                    var s = frontOrigin + origin;
                    result = new Vector4(n, (((n.Y * s.Y) + (n.X * s.X)) + (n.Z * s.Z)) * 0.5f);
                }
            }
            if (!(-1.1920929e-07f > best))
                return result;
        }
        foreach (var code in front.Codes)
        {
            if (!Plane(code, planes, out var plane))
                continue;
            var gap = Clearance(plane, back, points);
            if (gap > best)
            {
                result = Vector4.Zero - plane;
                best = gap;
            }
        }
        foreach (var code in back.Codes)
        {
            if (!Plane(code, planes, out var plane))
                continue;
            var gap = Clearance(plane, front, points);
            if (gap > best)
            {
                result = plane;
                best = gap;
            }
        }
        return result;
    }

    // How far the region's points sit in front of the plane (minss), or
    // -FLT_MAX once a direction points back past -0.001.
    private static float Clearance(Vector4 plane, Region region, Vector4[] points)
    {
        var min = float.MaxValue;
        foreach (var i in region.Points)
        {
            var d = Dpps(plane, points[i]);
            if (points[i].W != 0f)
                min = min < d ? min : d;
            else if (-0.001f > d)
                return -float.MaxValue;
        }
        return min;
    }

    // FUN_1819d34c0: a half-edge's two face planes, its direction and start.
    private static bool EdgeOf(Region region, int i, List<Vector4> planes, Vector4[] points, out Vector4 p1, out Vector4 p2, out Vector4 dir, out Vector4 origin)
    {
        dir = origin = p2 = default;
        return Bound(region, region.Edges[i].Face, planes, out p1) && Bound(region, region.Edges[i ^ 1].Face, planes, out p2)
            && Span(points[region.Edges[i].Point], points[region.Edges[i ^ 1].Point], out dir, out origin);
    }

    // A half-edge from p to q as a start and a direction; false when both
    // ends are directions (the edge lies at infinity).
    private static bool Span(Vector4 p, Vector4 q, out Vector4 dir, out Vector4 origin)
    {
        (dir, origin) = (default, default);
        if (q.W != 0f)
        {
            if (p.W == 0f)
                (origin, dir) = (q, Vector4.Zero - p);
            else
                (origin, dir) = (p, q - p);
            return true;
        }
        if (p.W == 0f)
            return false;
        (origin, dir) = (p, q);
        return true;
    }

    private static bool Bound(Region region, int face, List<Vector4> planes, out Vector4 plane) => Plane(region.Codes[face], planes, out plane);

    // A face code's plane, negated as 0 - x on sign 0; false for infinity.
    private static bool Plane(int code, List<Vector4> planes, out Vector4 plane)
    {
        plane = default;
        var index = (uint)code >> 1;
        if (index == 0x7fffffff)
            return false;
        plane = (code & 1) == 0 ? Vector4.Zero - planes[(int)index] : planes[(int)index];
        return true;
    }

    // The twelve half-edges FUN_1819d3e90 gives an edge region: its two hull
    // points and two face normals, bounded by the four edge planes.
    private static (int, int)[] EdgeRegionEdges(int fe, int ft, int oe, int ot) =>
    [
        (fe, 2), (ft, 0), (ot, 2), (fe, 1), (ft, 2), (ot, 3),
        (ot, 1), (oe, 3), (oe, 1), (fe, 0), (ft, 3), (oe, 0),
    ];

    private static float Dot3(Vector4 a, Vector4 b) => ((a.X * b.X) + (a.Y * b.Y)) + (a.Z * b.Z);

    // movmskps reads the sign bit, so -0 counts as negative.
    private static bool Negative(float x) => BitConverter.SingleToInt32Bits(x) < 0;

    // 0 behind the plane, 2 in front, 1 across it by more than 1e-4 both ways.
    private static byte Side(Region region, Vector4 plane, Vector4[] points)
    {
        float back = 0f, front = 0f;
        foreach (var i in region.Points)
        {
            var d = Dpps(points[i], plane);
            back = back > -d ? back : -d;
            front = front > d ? front : d;
            if (1e-4f < back && 1e-4f < front)
                return 1;
        }
        return back < front ? (byte)2 : (byte)0;
    }

    // dpps with mask 0xff: lanes multiplied, summed as (0 + 1) + (2 + 3).
    private static float Dpps(Vector4 a, Vector4 b) => ((a.X * b.X) + (a.Y * b.Y)) + ((a.Z * b.Z) + (a.W * b.W));
}
