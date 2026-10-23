namespace Source2.Compiler.Physics;

/// <summary>
/// physicsbuilder's mesh simplification (CMesh_Simplify, pb 1800c5b90), run on a
/// map mesh's per-material piece when its physicsSimplificationError is above 0
/// (PhysicsBuilder_MeshPieces, 180016230: the piece is welded at 1/32 first).
///
/// <para>A Garland-Heckbert edge collapse in double (18060xxxx): area-weighted
/// face quadrics, perpendicular planes weighted 1000 x length squared along
/// boundary edges, one min-heap entry per edge. Collapses run while the
/// cheapest edge costs no more than the error, at most vertices - 4 times.
/// What comes out is positions and triangles only, both in their original
/// order with the dead ones left out (18060c990).</para>
///
/// <para>Every float expression keeps Valve's association, read from the
/// disassembly where Ghidra's printer drops parentheses (two of the face
/// quadric's terms are <c>w * (c * a)</c> and <c>w * (a * -d)</c>). Pointer
/// comparisons become vertex index comparisons: the vertices are one array.</para>
/// </summary>
internal static class MeshSimplifier
{
    private const double Tiny = 2.2250738585072014e-305;

    // Comparisons follow the comisd and jump each one compiles to, so a NaN
    // takes the branch Valve's takes: "!(x > tiny)" is comisd x, tiny; jbe.

    private sealed class Vertex
    {
        public int Id;
        public double X, Y, Z;
        public readonly double[] Q = new double[10];
        public double Area;
        public int Mark;
        public bool Used;
        public readonly List<Edge> Edges = [];
        public readonly List<Face> Faces = [];
    }

    private sealed class Face
    {
        public readonly Vertex[] V = new Vertex[3];
        public double Nx, Ny, Nz;
        public bool Valid;
    }

    private sealed class Edge
    {
        public double Cost;
        public int Heap = -1;
        public Vertex Lo = null!, Hi = null!;
        public readonly List<Face> Faces = [];
        public double Tx, Ty, Tz;
        public bool Collected;
        public bool Alive = true;
    }

    /// <summary>
    /// The simplified mesh, or null when CMesh_Simplify copies its input instead
    /// (fewer than 4 vertices or 6 indices, or an error at or below 0).
    /// <paramref name="error"/> is the float physicsbuilder passes (the mesh's
    /// error squared).
    /// </summary>
    public static (float[] Positions, int[] Indices)? Simplify(float[] positions, int[] indices, float error)
    {
        if (positions.Length / 3 < 4 || indices.Length < 6 || error <= 0f)
            return null;
        var (points, triangles) = Run(positions, indices, error, -1);
        var result = new float[points.Length];
        for (var i = 0; i < points.Length; i++)
            result[i] = (float)points[i];
        return (result, triangles);
    }

    /// <summary>Each edge after the setup, in creation order: ends, cost and heap slot (tests).</summary>
    internal static List<(int Lo, int Hi, double Cost, int Heap, int Faces)> Edges(float[] positions, int[] indices, double error)
        => [.. new State(positions, indices, error).Edges.Select(e => (e.Lo.Id, e.Hi.Id, e.Cost, e.Heap, e.Faces.Count))];

    /// <summary>18060fdf0 between the input (18060aa70, 18060ab00) and the output (18060c990).</summary>
    internal static (double[] Positions, int[] Indices) Run(float[] positions, int[] indices, double error, int collapses)
    {
        var s = new State(positions, indices[..(indices.Length - (indices.Length % 3))], error);
        if (collapses < 0)
            collapses = Math.Max(s.Vertices.Length - 4, 0);
        for (var i = 0; i < collapses; i++)
        {
            // Valve reads heap[0] without a size check; an emptied heap would
            // collapse a stale edge. No mesh with collapses left gets there.
            if (s.HeapList.Count == 0)
                break;
            var top = s.HeapList[0];
            if (top.Cost > error)
                break;
            s.Collapse(top);
        }
        return s.Output();
    }

    private sealed class State
    {
        public readonly Vertex[] Vertices;
        public readonly List<Face> Faces = [];
        public readonly List<Edge> Edges = [];
        public readonly List<Edge> HeapList = [];
        private readonly double _error;

        // 18060d6c0
        public State(float[] positions, int[] indices, double error)
        {
            _error = error;
            Vertices = new Vertex[positions.Length / 3];
            for (var i = 0; i < Vertices.Length; i++)
                Vertices[i] = new Vertex { Id = i, X = positions[i * 3], Y = positions[(i * 3) + 1], Z = positions[(i * 3) + 2] };
            for (var t = 0; t < indices.Length; t += 3)
            {
                Vertex a = Vertices[indices[t]], b = Vertices[indices[t + 1]], c = Vertices[indices[t + 2]];
                a.Used = b.Used = c.Used = true;
                var f = new Face { Valid = true };
                (f.V[0], f.V[1], f.V[2]) = (a, b, c);
                Faces.Add(f);
                SetNormal(f);
                AddFaceQuadric(a, b, c);
                a.Faces.Add(f);
                b.Faces.Add(f);
                c.Faces.Add(f);
                Link(a, b, f);
                Link(b, c, f);
                Link(c, a, f);
            }
            foreach (var e in Edges)
                if (IsBoundary(e))
                    AddBoundaryQuadric(e);
            foreach (var e in Edges)
            {
                Cost(e);
                HeapList.Add(e);
                e.Heap = HeapList.Count - 1;
                SiftUp(e.Heap);
            }
        }

        private void Link(Vertex p, Vertex q, Face f)
        {
            var e = Find(p, q);
            if (e == null)
            {
                e = new Edge { Lo = p.Id < q.Id ? p : q, Hi = q.Id < p.Id ? p : q };
                Edges.Add(e);
                p.Edges.Add(e);
                q.Edges.Add(e);
            }
            e.Faces.Add(f);
        }

        // 180610ab0: searches the endpoint with more edges.
        private static Edge? Find(Vertex p, Vertex q)
        {
            var (other, list) = q.Edges.Count <= p.Edges.Count ? (q, p) : (p, q);
            var lo = list.Id < other.Id ? list : other;
            var hi = other.Id < list.Id ? list : other;
            foreach (var e in list.Edges)
                if (e.Lo == lo && e.Hi == hi)
                    return e;
            return null;
        }

        // The face record's normal (18060d6c0, 180610d30): zero when tiny.
        private static void SetNormal(Face f)
        {
            Vertex a = f.V[0], b = f.V[1], c = f.V[2];
            double e1x = b.X - a.X, e1y = b.Y - a.Y, e1z = b.Z - a.Z;
            double e2x = c.X - a.X, e2y = c.Y - a.Y, e2z = c.Z - a.Z;
            var nz = (e2y * e1x) - (e2x * e1y);
            var ny = (e2x * e1z) - (e2z * e1x);
            var nx = (e2z * e1y) - (e2y * e1z);
            var len2 = ((ny * ny) + (nx * nx)) + (nz * nz);
            if (!(len2 > Tiny))
            {
                (f.Nx, f.Ny, f.Nz) = (0, 0, 0);
                return;
            }
            var len = Math.Sqrt(len2);
            (f.Nx, f.Ny, f.Nz) = (nx / len, ny / len, nz / len);
        }

        // 180611130: the same normal from the current positions.
        private static (double X, double Y, double Z) Normal(Face f)
        {
            Vertex a = f.V[0], b = f.V[1], c = f.V[2];
            double e2z = c.Z - a.Z, e2x = c.X - a.X, e1x = b.X - a.X, e1y = b.Y - a.Y, e1z = b.Z - a.Z, e2y = c.Y - a.Y;
            var x = (e2z * e1y) - (e2y * e1z);
            var y = (e2x * e1z) - (e2z * e1x);
            var z = (e2y * e1x) - (e2x * e1y);
            var len2 = ((y * y) + (x * x)) + (z * z);
            if (!(len2 > Tiny))
                return (0, 0, 0);
            var len = Math.Sqrt(len2);
            return (x / len, y / len, z / len);
        }

        private static void Add(Vertex v, ReadOnlySpan<double> k, double area)
        {
            for (var i = 0; i < 10; i++)
                v.Q[i] = k[i] + v.Q[i];
            v.Area = area + v.Area;
        }

        // The face plane weighted by area. Products as the disassembly
        // groups them (18060dd10 .. 18060df66); no tiny check on the length.
        private static void AddFaceQuadric(Vertex a, Vertex b, Vertex c)
        {
            double e1x = b.X - a.X, e1y = b.Y - a.Y, e1z = b.Z - a.Z;
            double e2x = c.X - a.X, e2y = c.Y - a.Y, e2z = c.Z - a.Z;
            var nx = (e1y * e2z) - (e1z * e2y);
            var ny = (e1z * e2x) - (e1x * e2z);
            var nz = (e1x * e2y) - (e1y * e2x);
            var len = Math.Sqrt(((ny * ny) + (nx * nx)) + (nz * nz));
            var w = len * 0.5;
            double pa = nx / len, pb = ny / len, pc = nz / len;
            var nd = -((((a.Y * ny) + (a.X * nx)) + (nz * a.Z)) / len);
            Span<double> k =
            [
                (pa * pa) * w, (pb * pa) * w, w * (pc * pa), w * (pa * nd),
                (pb * pb) * w, (pc * pb) * w, (pb * nd) * w,
                (pc * pc) * w, (pc * nd) * w,
                (nd * nd) * w,
            ];
            Add(a, k, w);
            Add(b, k, w);
            Add(c, k, w);
        }

        // The plane through a boundary edge, perpendicular to its first face,
        // weighted by 1000 x the edge length squared (18060f848 .. 18060fab2).
        private static void AddBoundaryQuadric(Edge e)
        {
            var (fx, fy, fz) = Normal(e.Faces[0]);
            Vertex lo = e.Lo, hi = e.Hi;
            double ex = hi.X - lo.X, ey = hi.Y - lo.Y, ez = hi.Z - lo.Z;
            var length2 = ((ey * ey) + (ex * ex)) + (ez * ez);
            var px = (ey * fz) - (ez * fy);
            var py = (ez * fx) - (ex * fz);
            var pz = (ex * fy) - (ey * fx);
            var len = Math.Sqrt(((py * py) + (px * px)) + (pz * pz));
            double pa = px / len, pb = py / len, pc = pz / len;
            var nd = -((((lo.Y * py) + (lo.X * px)) + (lo.Z * pz)) / len);
            var w = length2 * 1000.0;
            Span<double> k =
            [
                (pa * pa) * w, (pa * pb) * w, (pc * pa) * w, (pa * nd) * w,
                (pb * pb) * w, (pc * pb) * w, (pb * nd) * w,
                (pc * pc) * w, (pc * nd) * w,
                (nd * nd) * w,
            ];
            Add(lo, k, length2);
            Add(hi, k, length2);
        }

        // 1806113f0
        private static bool IsValid(Edge e) => (uint)(e.Faces.Count - 1) < 2;

        // 180611390: one face, or two facing opposite ways.
        private static bool IsBoundary(Edge e)
        {
            if (e.Faces.Count == 1)
                return true;
            if (e.Faces.Count != 2)
                return false;
            Face f0 = e.Faces[0], f1 = e.Faces[1];
            return ((f1.Ny * f0.Ny) + (f1.Nx * f0.Nx)) + (f1.Nz * f0.Nz) < -0.99999;
        }

        // 180610960
        private static bool IsBoundary(Vertex v)
        {
            foreach (var e in v.Edges)
                if (IsBoundary(e))
                    return true;
            return false;
        }

        // 180611280: 1 when the face runs lo to hi.
        private static int Orientation(Face f, Edge e)
        {
            if ((f.V[0] == e.Lo && f.V[1] == e.Hi) || (f.V[1] == e.Lo && f.V[2] == e.Hi))
                return 1;
            return f.V[2] == e.Lo && f.V[0] == e.Hi ? 1 : -1;
        }

        // 1806108c0: an edge whose two faces run the same way.
        private static bool IsTwisted(Vertex v)
        {
            foreach (var e in v.Edges)
                if (e.Faces.Count == 2 && Orientation(e.Faces[1], e) == Orientation(e.Faces[0], e))
                    return true;
            return false;
        }

        // 180611370
        private static Vertex? Other(Edge e, Vertex v) => v == e.Lo ? e.Hi : v == e.Hi ? e.Lo : null;

        private static (double, double, double) Direction(double x, double y, double z)
        {
            var len2 = ((y * y) + (x * x)) + (z * z);
            if (!(len2 > Tiny))
                return (0, 0, 0);
            var len = Math.Sqrt(len2);
            return (x / len, y / len, z / len);
        }

        // 180610620: whether p may move onto q. Free when p is interior;
        // otherwise q must be on the boundary too and every other boundary
        // edge at p must point along q - p.
        private static bool CanMoveOnto(Vertex p, Vertex q)
        {
            if (!IsBoundary(p))
                return true;
            if (!IsBoundary(q))
                return false;
            var (dx, dy, dz) = Direction(q.X - p.X, q.Y - p.Y, q.Z - p.Z);
            foreach (var e in p.Edges)
            {
                var w = e.Lo == p ? e.Hi : e.Lo;
                if (w == q || !IsBoundary(e))
                    continue;
                var (ux, uy, uz) = Direction(p.X - w.X, p.Y - w.Y, p.Z - w.Z);
                if (((ux * dx) + (uy * dy)) + (uz * dz) < 0.99999)
                    return false;
            }
            return true;
        }

        // The quadric at a point (18060c6b9's chain).
        private static double Evaluate(ReadOnlySpan<double> q, double dd, double x, double y, double z)
            => (((((((((((x * q[0]) * x) + ((x * (q[1] + q[1])) * y)) + ((x * (q[2] + q[2])) * z)) + (x * (q[3] + q[3])))
                 + ((y * q[4]) * y)) + ((y * (q[5] + q[5])) * z)) + (y * (q[6] + q[6]))) + ((z * q[7]) * z)) + (z * (q[8] + q[8]))) + dd);

        private static void Reject(Edge e)
        {
            // The target is a static vector (DAT_180e2a018); a rejected edge
            // never collapses, so it is not read.
            (e.Tx, e.Ty, e.Tz) = (0, 0, 0);
            e.Cost = double.MaxValue;
        }

        // 18060c150
        private void Cost(Edge e)
        {
            Vertex lo = e.Lo, hi = e.Hi;
            if (!IsValid(e) || (IsBoundary(lo) && IsBoundary(hi) && !IsBoundary(e)) || IsTwisted(lo) || IsTwisted(hi))
            {
                Reject(e);
                return;
            }
            // The link condition: at most two shared neighbours.
            foreach (var x in hi.Edges)
                Other(x, hi)!.Mark = 1;
            var shared = 0;
            foreach (var x in lo.Edges)
                shared += Other(x, lo)!.Mark;
            foreach (var x in hi.Edges)
                Other(x, hi)!.Mark = 0;
            if (shared > 2)
            {
                Reject(e);
                return;
            }
            var dd = lo.Q[9] + hi.Q[9];
            Span<double> q = stackalloc double[9];
            for (var i = 0; i < 9; i++)
                q[i] = lo.Q[i] + hi.Q[i];
            var area = hi.Area + lo.Area;
            double cost;
            if (!IsBoundary(lo) && !IsBoundary(hi))
            {
                double A = q[0], B = q[1], C = q[2], D = q[3], E = q[4], F = q[5], G = q[6], H = q[7], I = q[8];
                var m00 = (H * E) - (F * F);
                var m01 = (C * F) - (H * B);
                var m02 = (F * B) - (E * C);
                var det = ((A * m00) + (m01 * B)) + (m02 * C);
                if (!(det > Tiny))
                {
                    var costLo = Evaluate(q, dd, lo.X, lo.Y, lo.Z);
                    var costHi = Evaluate(q, dd, hi.X, hi.Y, hi.Z);
                    var at = costHi > costLo ? lo : hi;
                    (e.Tx, e.Ty, e.Tz) = (at.X, at.Y, at.Z);
                    cost = costLo < costHi ? costLo : costHi;
                }
                else
                {
                    m02 /= det;
                    m01 /= det;
                    m00 /= det;
                    var m11 = ((H * A) - (C * C)) / det;
                    var m12 = ((C * B) - (F * A)) / det;
                    var m22 = ((E * A) - (B * B)) / det;
                    var nd = -D;
                    e.Tx = ((nd * m00) - (G * m01)) - (I * m02);
                    e.Ty = ((nd * m01) - (G * m11)) - (m12 * I);
                    e.Tz = ((nd * m02) - (m12 * G)) - (I * m22);
                    cost = Evaluate(q, dd, e.Tx, e.Ty, e.Tz);
                }
            }
            else
            {
                var costLo = CanMoveOnto(hi, lo) ? Evaluate(q, dd, lo.X, lo.Y, lo.Z) : double.MaxValue;
                var costHi = CanMoveOnto(lo, hi) ? Evaluate(q, dd, hi.X, hi.Y, hi.Z) : double.MaxValue;
                if (costLo == double.MaxValue && costHi == double.MaxValue)
                {
                    Reject(e);
                    return;
                }
                var at = costHi > costLo ? lo : hi;
                (e.Tx, e.Ty, e.Tz) = (at.X, at.Y, at.Z);
                cost = costLo < costHi ? costLo : costHi;
            }
            e.Cost = cost / area;
            if (Flips(e, lo, _error) || Flips(e, hi, _error))
            {
                Reject(e);
                return;
            }
            var valence = hi.Edges.Count + lo.Edges.Count + (-2 - e.Faces.Count);
            if (valence > 6)
                e.Cost = (((double)(valence - 6) * 0.05) * _error) + e.Cost;
        }

        // 180610f40: circumradius over twice the inradius, 1 for an
        // equilateral triangle.
        private static double Quality(double e12, double e01, double e20)
        {
            var s = ((e12 + e01) + e20) * 0.5;
            return ((e12 * e01) * e20) / ((((s - e01) * 8.0) * (s - e12)) * (s - e20));
        }

        private static double Distance(double ax, double ay, double az, double bx, double by, double bz)
            => Math.Sqrt((((ay - by) * (ay - by)) + ((ax - bx) * (ax - bx))) + ((az - bz) * (az - bz)));

        // 18060fe70: true when moving v to the target folds a face of v not on
        // the edge (shorter than 5e-5, or turned 60 degrees or more); each face
        // that gets worse adds 0.05 x error x the quality lost.
        private static bool Flips(Edge e, Vertex v, double error)
        {
            foreach (var f in v.Faces)
            {
                if (e.Faces.Contains(f))
                    continue;
                var (p0x, p0y, p0z) = f.V[0] == v ? (e.Tx, e.Ty, e.Tz) : (f.V[0].X, f.V[0].Y, f.V[0].Z);
                var (p1x, p1y, p1z) = f.V[1] == v ? (e.Tx, e.Ty, e.Tz) : (f.V[1].X, f.V[1].Y, f.V[1].Z);
                var (p2x, p2y, p2z) = f.V[2] == v ? (e.Tx, e.Ty, e.Tz) : (f.V[2].X, f.V[2].Y, f.V[2].Z);
                double dx = p2x - p0x, dz = p2z - p0z, dy = p2y - p0y;
                var nx = (dz * (p1y - p0y)) - (dy * (p1z - p0z));
                var ny = (dx * (p1z - p0z)) - (dz * (p1x - p0x));
                var nz = (dy * (p1x - p0x)) - (dx * (p1y - p0y));
                var len2 = ((ny * ny) + (nx * nx)) + (nz * nz);
                var len = Math.Sqrt(len2);
                if (len < 5e-05)
                    return true;
                if (!(len2 > Tiny))
                    (nx, ny, nz) = (0, 0, 0);
                else
                    (nx, ny, nz) = (nx / len, ny / len, nz / len);
                if (((ny * f.Ny) + (nx * f.Nx)) + (nz * f.Nz) < 0.5)
                    return true;
                var old = Quality(Distance(f.V[1].X, f.V[1].Y, f.V[1].Z, f.V[2].X, f.V[2].Y, f.V[2].Z),
                                  Distance(f.V[0].X, f.V[0].Y, f.V[0].Z, f.V[1].X, f.V[1].Y, f.V[1].Z),
                                  Distance(f.V[2].X, f.V[2].Y, f.V[2].Z, f.V[0].X, f.V[0].Y, f.V[0].Z));
                var now = Quality(Distance(p1x, p1y, p1z, p2x, p2y, p2z), Distance(p0x, p0y, p0z, p1x, p1y, p1z), Distance(p2x, p2y, p2z, p0x, p0y, p0z));
                if (old <= now && now != old)
                    e.Cost = (((now - old) * 0.05) * error) + e.Cost;
            }
            return false;
        }

        // Swap-with-last removal of the last occurrence (180610a10, 180610a60, 180611410).
        private static void Drop<T>(List<T> list, T item) where T : class
        {
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] != item)
                    continue;
                list[i] = list[^1];
                list.RemoveAt(list.Count - 1);
                return;
            }
        }

        // 1806112d0: the face's corner off the edge.
        private static Vertex? Opposite(Face f, Edge e)
        {
            Vertex a = f.V[0], b = f.V[1], c = f.V[2];
            if (e.Lo == (a.Id < b.Id ? a : b) && e.Hi == (b.Id < a.Id ? a : b))
                return c;
            if (e.Lo == (b.Id < c.Id ? b : c) && e.Hi == (c.Id < b.Id ? b : c))
                return a;
            if (e.Lo == (c.Id < a.Id ? c : a))
                return e.Hi == (a.Id < c.Id ? c : a) ? b : null;
            return null;
        }

        // 180610260: every edge at every neighbour of v, once.
        private static void Collect(List<Edge> into, Vertex v)
        {
            foreach (var e in v.Edges)
            {
                foreach (var x in Other(e, v)!.Edges)
                {
                    if (x.Collected)
                        continue;
                    into.Add(x);
                    x.Collected = true;
                }
            }
        }

        // 18060b940
        public void Collapse(Edge e)
        {
            var collected = new List<Edge>();
            Vertex lo = e.Lo, hi = e.Hi;
            Collect(collected, lo);
            Collect(collected, hi);
            (hi.X, hi.Y, hi.Z) = (e.Tx, e.Ty, e.Tz);
            for (var i = 0; i < 10; i++)
                hi.Q[i] = lo.Q[i] + hi.Q[i];
            hi.Area = lo.Area + hi.Area;
            foreach (var f in e.Faces)
            {
                var o = Opposite(f, e)!;
                var toLo = Find(o, lo);
                var toHi = Find(o, hi);
                Drop(lo.Faces, f);
                Drop(hi.Faces, f);
                Drop(o.Faces, f);
                if (toLo != null)
                    Drop(toLo.Faces, f);
                if (toHi != null)
                    Drop(toHi.Faces, f);
                f.Valid = false;
            }
            e.Faces.Clear();
            Drop(lo.Edges, e);
            Drop(hi.Edges, e);
            Remove(e);
            e.Alive = false;
            // 180610b20: lo's edges move to hi, ends kept in index order.
            foreach (var x in lo.Edges)
            {
                if (x.Lo == lo)
                    x.Lo = hi;
                else
                    x.Hi = hi;
                if (x.Hi.Id < x.Lo.Id)
                    (x.Lo, x.Hi) = (x.Hi, x.Lo);
            }
            hi.Edges.AddRange(lo.Edges);
            lo.Edges.Clear();
            // 180610d30: lo's faces move to hi with fresh normals; hi's own
            // faces keep the normals of hi's old place.
            foreach (var f in lo.Faces)
            {
                if (f.V[0] == lo)
                    f.V[0] = hi;
                else if (f.V[1] == lo)
                    f.V[1] = hi;
                else
                    f.V[2] = hi;
                SetNormal(f);
            }
            hi.Faces.AddRange(lo.Faces);
            lo.Faces.Clear();
            lo.Used = false;
            // 18060b020 is MSVC's std::sort on (lo, hi); a run of equal edges
            // is merged into its first, scanning back with swap removal.
            var edges = hi.Edges.ToArray();
            Maps.MsvcSort.Sort(edges, (a, b) => a.Lo.Id < b.Lo.Id || (a.Lo.Id == b.Lo.Id && a.Hi.Id < b.Hi.Id));
            hi.Edges.Clear();
            hi.Edges.AddRange(edges);
            for (var i = hi.Edges.Count - 1; i > 0; i--)
            {
                Edge prev = hi.Edges[i - 1], cur = hi.Edges[i];
                if (prev.Lo != cur.Lo || prev.Hi != cur.Hi)
                    continue;
                prev.Faces.AddRange(cur.Faces);
                cur.Faces.Clear();
                hi.Edges[i] = hi.Edges[^1];
                hi.Edges.RemoveAt(hi.Edges.Count - 1);
                Drop(Other(cur, hi)!.Edges, cur);
                Remove(cur);
                cur.Alive = false;
            }
            foreach (var x in collected)
            {
                x.Collected = false;
                if (!x.Alive)
                    continue;
                Cost(x);
                var i = x.Heap;
                if (i > 0)
                {
                    var parent = HeapList[(i - 1) >> 1];
                    if (x.Cost <= parent.Cost && parent.Cost != x.Cost)
                    {
                        SiftUp(i);
                        continue;
                    }
                }
                SiftDown(i);
            }
        }

        // The removal in 18060b940: the last entry fills the hole and moves
        // up when it costs no more than the removed one, else down.
        private void Remove(Edge e)
        {
            var i = e.Heap;
            e.Heap = -1;
            HeapList[i] = HeapList[^1];
            HeapList[i].Heap = i;
            HeapList.RemoveAt(HeapList.Count - 1);
            if (i >= HeapList.Count)
                return;
            var moved = HeapList[i];
            if (!(moved.Cost > e.Cost))
                SiftUp(i);
            else
                SiftDown(i);
        }

        private void SiftUp(int i)
        {
            var x = HeapList[i];
            while (i > 0)
            {
                var p = (i - 1) / 2;
                var parent = HeapList[p];
                if (!(parent.Cost > x.Cost))
                    break;
                HeapList[i] = parent;
                parent.Heap = i;
                i = p;
            }
            HeapList[i] = x;
            x.Heap = i;
        }

        // 18060b850
        private void SiftDown(int i)
        {
            var x = HeapList[i];
            while (i < HeapList.Count / 2)
            {
                var child = (i * 2) + 1;
                if (child + 1 < HeapList.Count)
                {
                    double l = HeapList[child].Cost, r = HeapList[child + 1].Cost;
                    if (r <= l && l != r)
                        child++;
                }
                var c = HeapList[child];
                if (x.Cost <= c.Cost)
                    break;
                HeapList[i] = c;
                c.Heap = i;
                i = child;
            }
            HeapList[i] = x;
            x.Heap = i;
        }

        // 18060c990
        public (double[] Positions, int[] Indices) Output()
        {
            var remap = new int[Vertices.Length];
            Array.Fill(remap, -1);
            var points = new List<double>();
            foreach (var v in Vertices)
            {
                if (!v.Used)
                    continue;
                remap[v.Id] = points.Count / 3;
                points.Add(v.X);
                points.Add(v.Y);
                points.Add(v.Z);
            }
            var indices = new List<int>();
            foreach (var f in Faces)
            {
                if (!f.Valid)
                    continue;
                indices.Add(remap[f.V[0].Id]);
                indices.Add(remap[f.V[1].Id]);
                indices.Add(remap[f.V[2].Id]);
            }
            return ([.. points], [.. indices]);
        }
    }
}
