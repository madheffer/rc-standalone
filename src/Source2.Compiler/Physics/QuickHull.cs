namespace Source2.Compiler.Physics;

/// <summary>
/// vphysics2's quickhull (the object <c>RnHullCreate</c> builds on), ported
/// operation for operation from the 2026-09-24 binary. Build is
/// <c>FUN_1803c5650</c>; the helpers carry their addresses.
///
/// <para>Two things are reproduced that a textbook quickhull would not need.
/// The intrusive lists insert a node in front of the sentinel's first link
/// (<see cref="Insert"/>), so list order is Valve's. And vertices, half-edges
/// and faces come from fixed pools with index free lists, because a later
/// pass (<see cref="Sharpen"/>) visits each edge pair once by comparing the
/// two half-edges' addresses, which is pool order.</para>
///
/// <para>Floats follow the decompile's association; addition and
/// multiplication are commutative in IEEE, grouping is not.</para>
/// </summary>
internal sealed class QuickHull
{
    internal class Link
    {
        public Link F0 = null!;
        public Link F1 = null!;
    }

    internal sealed class Vertex : Link
    {
        public int Index;
        public int FreeNext;
        public int Mark;
        public float X, Y, Z;
        public HalfEdge? Edge;
        public Face? ConflictFace;
        public int Stamp = -1;
    }

    internal sealed class HalfEdge
    {
        public int Index;
        public int FreeNext;
        public HalfEdge Prev = null!;
        public HalfEdge Next = null!;
        public Vertex Origin = null!;
        public Face Face = null!;
        public HalfEdge? Twin;
    }

    internal sealed class Face : Link
    {
        public int Index;
        public int FreeNext;
        public HalfEdge? Edge;
        public int Mark;
        public float Area;
        public float CX, CY, CZ;
        public float NX, NY, NZ, D;
        public bool Flipped;
        public readonly Link Conflict = new();
    }

    /// <summary>tol[0], tol[1], tol[2] at +0, +4, +8; the scale 50 at +0xc.</summary>
    public readonly float[] Tol = new float[3];
    private const float ToleranceScale = 50f;
    public float IX, IY, IZ;
    public readonly Link Orphans = new();
    public readonly Link Vertices = new();
    public readonly Link Faces = new();
    public int Error;
    private int _stamp;

    private Vertex[] _vertexPool = [];
    private int _vertexFree;
    private HalfEdge[] _edgePool = [];
    private int _edgeFree;
    private Face[] _facePool = [];
    private int _faceFree;

    public QuickHull()
    {
        foreach (var s in new[] { Orphans, Vertices, Faces })
        {
            s.F0 = s;
            s.F1 = s;
        }
    }

    // Valve's insert: in front of the sentinel's F0 node, not at an end.
    internal static void Insert(Link node, Link sentinel)
    {
        var where = sentinel.F0;
        node.F0 = where.F0;
        node.F1 = where;
        node.F0.F1 = node;
        where.F0 = node;
    }

    internal static void Unlink(Link node)
    {
        node.F0.F1 = node.F1;
        node.F1.F0 = node.F0;
        node.F0 = null!;
        node.F1 = null!;
    }

    internal static IEnumerable<T> Walk<T>(Link sentinel) where T : Link
    {
        for (var n = sentinel.F1; n != sentinel; n = n.F1)
            yield return (T)n;
    }

    public IEnumerable<Vertex> HullVertices => Walk<Vertex>(Vertices);
    public IEnumerable<Face> HullFaces => Walk<Face>(Faces);

    public static IEnumerable<HalfEdge> Loop(Face f)
    {
        var e = f.Edge!;
        do
        {
            yield return e;
            e = e.Next;
        } while (e != f.Edge);
    }

    // FUN_1803c3d40
    private void Reserve(int n)
    {
        _vertexPool = new Vertex[n * 2];
        for (var i = 0; i < _vertexPool.Length; i++)
            _vertexPool[i] = new Vertex { Index = i, FreeNext = i + 1 < _vertexPool.Length ? i + 1 : -1 };
        _vertexFree = 0;
        _edgePool = new HalfEdge[(n - 2) * 12];
        for (var i = 0; i < _edgePool.Length; i++)
            _edgePool[i] = new HalfEdge { Index = i, FreeNext = i + 1 < _edgePool.Length ? i + 1 : -1 };
        _edgeFree = 0;
        _facePool = new Face[n * 4 - 8];
        for (var i = 0; i < _facePool.Length; i++)
            _facePool[i] = new Face { Index = i, FreeNext = i + 1 < _facePool.Length ? i + 1 : -1 };
        _faceFree = 0;
    }

    private Vertex NewVertex()
    {
        var v = _vertexPool[_vertexFree];
        _vertexFree = v.FreeNext;
        return v;
    }

    private void FreeVertex(Vertex v)
    {
        v.FreeNext = _vertexFree;
        _vertexFree = v.Index;
    }

    private HalfEdge NewEdge()
    {
        var e = _edgePool[_edgeFree];
        _edgeFree = e.FreeNext;
        return e;
    }

    private void FreeEdge(HalfEdge e)
    {
        e.FreeNext = _edgeFree;
        _edgeFree = e.Index;
    }

    private void FreeFace(Face f)
    {
        f.FreeNext = _faceFree;
        _faceFree = f.Index;
    }

    // FUN_1803c5650
    public void Build(int count, float[] points, float tolerance, bool relative)
    {
        if (count < 4 || points.Length == 0)
        {
            Error = 2;
            return;
        }
        float mx = 0f, my = 0f, mz = 0f;
        for (var i = 0; i < count; i++)
        {
            mx = mx + points[i * 3];
            my = my + points[i * 3 + 1];
            mz = mz + points[i * 3 + 2];
        }
        var fn = (float)count;
        mx = mx / fn;
        my = my / fn;
        mz = mz / fn;
        var pts = new List<float>(count * 3);
        for (var i = 0; i < count; i++)
        {
            pts.Add(-mx + points[i * 3]);
            pts.Add(-my + points[i * 3 + 1]);
            pts.Add(-mz + points[i * 3 + 2]);
        }
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = -float.MaxValue, maxY = -float.MaxValue, maxZ = -float.MaxValue;
        for (var i = 0; i < count; i++)
        {
            float x = pts[i * 3], y = pts[i * 3 + 1], z = pts[i * 3 + 2];
            if (x <= minX) minX = x;
            if (maxX <= x) maxX = x;
            if (y <= minY) minY = y;
            if (maxY <= y) maxY = y;
            if (z <= minZ) minZ = z;
            if (maxZ <= z) maxZ = z;
        }
        float tx = 1f, ty = 1f, tz = 1f;
        if (relative)
        {
            tx = maxX - minX;
            ty = maxY - minY;
            tz = maxZ - minZ;
        }
        Weld(pts, tx * tolerance, ty * tolerance, tz * tolerance);
        var n = pts.Count / 3;
        if (n < 4)
        {
            Error = 2;
            return;
        }
        var p = pts.ToArray();
        Reserve(n);
        ComputeTolerance(p, n);
        if (!InitialHull(n, p))
            return;
        for (var eye = NextConflictVertex(); eye != null && Error == 0; eye = NextConflictVertex())
        {
            var face = eye.ConflictFace!;
            eye.ConflictFace = null;
            Unlink(eye);
            Insert(eye, Vertices);
            var horizon = new List<HalfEdge>();
            BuildHorizon(horizon, eye, face, null);
            var created = new List<Face>();
            AddNewFaces(created, horizon, eye);
            MergeFaces(created);
            ResolveOrphans(created);
            CleanFaces(created);
        }
        foreach (var f in HullFaces)
        {
            foreach (var e in Loop(f))
            {
                e.Origin.Mark = 0;
                e.Origin.Edge ??= e;
            }
        }
        for (var node = Vertices.F1; node != Vertices;)
        {
            var next = node.F1;
            var v = (Vertex)node;
            if (v.Mark != 0)
            {
                Unlink(v);
                FreeVertex(v);
            }
            node = next;
        }
        if (mx != 0f || my != 0f || mz != 0f)
        {
            foreach (var v in HullVertices)
            {
                v.X = mx + v.X;
                v.Y = my + v.Y;
                v.Z = mz + v.Z;
            }
            foreach (var f in HullFaces)
                f.D = ((my * f.NY) + (mx * f.NX)) + (mz * f.NZ) + f.D;
            IX = mx + IX;
            IY = my + IY;
            IZ = mz + IZ;
        }
    }

    // FUN_1803c7f80: drop a later point within the tolerance of an earlier one,
    // replacing it with the last point.
    private static void Weld(List<float> p, float tx, float ty, float tz)
    {
        if (!(0f < tx || 0f < ty || 0f < tz))
            return;
        for (var i = 0; i < p.Count / 3; i++)
        {
            for (var j = p.Count / 3 - 1; j > i; j--)
            {
                if (MathF.Abs(p[i * 3] - p[j * 3]) < tx && MathF.Abs(p[i * 3 + 1] - p[j * 3 + 1]) < ty
                    && MathF.Abs(p[i * 3 + 2] - p[j * 3 + 2]) < tz)
                {
                    var last = p.Count - 3;
                    p[j * 3] = p[last];
                    p[j * 3 + 1] = p[last + 1];
                    p[j * 3 + 2] = p[last + 2];
                    p.RemoveRange(last, 3);
                }
            }
        }
    }

    // FUN_1803c4aa0
    private void ComputeTolerance(float[] p, int n)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = -float.MaxValue, maxY = -float.MaxValue, maxZ = -float.MaxValue;
        for (var i = 0; i < n; i++)
        {
            float x = p[i * 3], y = p[i * 3 + 1], z = p[i * 3 + 2];
            if (x <= minX) minX = x;
            if (y <= minY) minY = y;
            if (maxX <= x) maxX = x;
            if (maxY <= y) maxY = y;
            if (z <= minZ) minZ = z;
            if (maxZ <= z) maxZ = z;
        }
        var ay = MathF.Abs(minY) <= MathF.Abs(maxY) ? MathF.Abs(maxY) : MathF.Abs(minY);
        var ax = MathF.Abs(minX) <= MathF.Abs(maxX) ? MathF.Abs(maxX) : MathF.Abs(minX);
        var az = MathF.Abs(minZ) <= MathF.Abs(maxZ) ? MathF.Abs(maxZ) : MathF.Abs(minZ);
        var m = ay <= az ? az : ay;
        var sum = (ay + ax) + az;
        if (ax <= m) ax = m;
        var t = ax * 1.7320508f;
        if (sum <= t) t = sum;
        var v = t * 3f * 1.01f + ax;
        var e = 1f <= v ? v : 1f;
        e = e * ToleranceScale * 1.1920929e-07f;
        Tol[0] = e;
        e = e * 4f;
        Tol[1] = e;
        Tol[2] = e + e;
    }

    // FUN_1803c42f0
    private bool InitialHull(int n, float[] p)
    {
        ExtremePair(n, p, out var i0, out var i1);
        if (i0 < 0 || i1 < 0)
        {
            Error = 3;
            return false;
        }
        var i2 = FarthestFromLine(i0, i1, n, p);
        if (i2 < 0)
        {
            Error = 4;
            return false;
        }
        var i3 = FarthestFromPlane(i0, i1, i2, n, p);
        if (i3 < 0)
        {
            Error = 5;
            return false;
        }
        IX = 0f;
        IY = 0f;
        IZ = 0f;
        IX = ((((IX + p[i0 * 3]) + p[i1 * 3]) + p[i2 * 3]) + p[i3 * 3]) * 0.25f;
        IY = ((((IY + p[i0 * 3 + 1]) + p[i1 * 3 + 1]) + p[i2 * 3 + 1]) + p[i3 * 3 + 1]) * 0.25f;
        IZ = ((((IZ + p[i0 * 3 + 2]) + p[i1 * 3 + 2]) + p[i2 * 3 + 2]) + p[i3 * 3 + 2]) * 0.25f;
        float dx = p[i3 * 3], dy = p[i3 * 3 + 1], dz = p[i3 * 3 + 2];
        float ax = p[i0 * 3] - dx, ay = p[i0 * 3 + 1] - dy, az = p[i0 * 3 + 2] - dz;
        float bx = p[i1 * 3] - dx, by = p[i1 * 3 + 1] - dy, bz = p[i1 * 3 + 2] - dz;
        float cx = p[i2 * 3] - dx, cy = p[i2 * 3 + 1] - dy, cz = p[i2 * 3 + 2] - dz;
        var det = (((cz * by) - (cy * bz)) * ax + ((cx * bz) - (cz * bx)) * ay) + ((cy * bx) - (cx * by)) * az;
        int b = i1, c = i2;
        if (det < 0f)
        {
            b = i2;
            c = i1;
        }
        var va = AddHullVertex(p, i0);
        var vb = AddHullVertex(p, b);
        var vc = AddHullVertex(p, c);
        var vd = AddHullVertex(p, i3);
        var f1 = CreateFace(va, vb, vc);
        Insert(f1, Faces);
        var f2 = CreateFace(vd, vb, va);
        Insert(f2, Faces);
        var f3 = CreateFace(vd, vc, vb);
        Insert(f3, Faces);
        var f4 = CreateFace(vd, va, vc);
        Insert(f4, Faces);
        Twin(f1, 0, f2, 1);
        Twin(f1, 1, f3, 1);
        Twin(f1, 2, f4, 1);
        Twin(f2, 0, f3, 2);
        Twin(f3, 0, f4, 2);
        Twin(f4, 0, f2, 2);
        for (var i = 0; i < n; i++)
        {
            if (i == i0 || i == b || i == c || i == i3)
                continue;
            Face? best = null;
            var bestD = Tol[2];
            foreach (var f in HullFaces)
            {
                var d = ((p[i * 3 + 1] * f.NY) + (p[i * 3] * f.NX)) + (p[i * 3 + 2] * f.NZ) - f.D;
                if (bestD < d)
                {
                    bestD = d;
                    best = f;
                }
            }
            if (best == null)
                continue;
            var v = NewVertex();
            v.Mark = 3;
            v.X = p[i * 3];
            v.Y = p[i * 3 + 1];
            v.Z = p[i * 3 + 2];
            v.ConflictFace = best;
            v.Edge = null;
            v.Stamp = -1;
            Insert(v, best.Conflict);
        }
        return true;
    }

    private Vertex AddHullVertex(float[] p, int i)
    {
        var v = NewVertex();
        v.Mark = 3;
        v.X = p[i * 3];
        v.Y = p[i * 3 + 1];
        v.Z = p[i * 3 + 2];
        v.Edge = null;
        v.ConflictFace = null;
        v.Stamp = -1;
        Insert(v, Vertices);
        return v;
    }

    // FUN_1803c7b60: the axis of largest extent, if it beats 100 tol[0].
    private void ExtremePair(int n, float[] p, out int lo, out int hi)
    {
        lo = -1;
        hi = -1;
        int[] minI = [0, 0, 0], maxI = [0, 0, 0];
        float[] min = [p[0], p[1], p[2]], max = [p[0], p[1], p[2]];
        for (var i = 1; i < n; i++)
        {
            for (var a = 0; a < 3; a++)
            {
                var x = p[i * 3 + a];
                if (min[a] <= x)
                {
                    if (max[a] < x)
                    {
                        max[a] = x;
                        maxI[a] = i;
                    }
                }
                else
                {
                    min[a] = x;
                    minI[a] = i;
                }
            }
        }
        float[] ext = [max[0] - min[0], max[1] - min[1], max[2] - min[2]];
        int axis;
        if (ext[1] <= ext[0])
            axis = ext[0] < ext[2] ? 2 : 0;
        else
            axis = ext[1] < ext[2] ? 2 : 1;
        if (Tol[0] * 100f < ext[axis])
        {
            lo = minI[axis];
            hi = maxI[axis];
        }
    }

    // FUN_1803c71c0
    private int FarthestFromLine(int a, int b, int n, float[] p)
    {
        var best = Tol[0] * 100f;
        var index = -1;
        float ax = p[a * 3], ay = p[a * 3 + 1], az = p[a * 3 + 2];
        float dx = p[b * 3] - ax, dy = p[b * 3 + 1] - ay, dz = p[b * 3 + 2] - az;
        for (var i = 0; i < n; i++)
        {
            if (i == a || i == b)
                continue;
            float px = p[i * 3], py = p[i * 3 + 1], pz = p[i * 3 + 2];
            var t = (((py - ay) * dy) + ((px - ax) * dx) + ((pz - az) * dz)) / (((dx * dx) + (dy * dy)) + (dz * dz));
            var qx = px - ((dx * t) + ax);
            var qy = py - ((dy * t) + ay);
            var qz = pz - ((dz * t) + az);
            var dist = MathF.Sqrt(((qy * qy) + (qx * qx)) + (qz * qz));
            if (best < dist)
            {
                index = i;
                best = dist;
            }
        }
        return index;
    }

    // FUN_1803c77e0
    private int FarthestFromPlane(int a, int b, int c, int n, float[] p)
    {
        float ax = p[a * 3], ay = p[a * 3 + 1], az = p[a * 3 + 2];
        float cy = p[c * 3 + 1] - ay, cz = p[c * 3 + 2] - az, cx = p[c * 3] - ax;
        float bz = p[b * 3 + 2] - az, bx = p[b * 3] - ax, by = p[b * 3 + 1] - ay;
        var ny = (cx * bz) - (cz * bx);
        var nx = (cz * by) - (cy * bz);
        var nz = (cy * bx) - (cx * by);
        var len = MathF.Sqrt(((nx * nx) + (ny * ny)) + (nz * nz));
        var best = Tol[0] * 100f;
        var index = -1;
        var ux = nx / len;
        var uy = ny / len;
        var d = (((ay * ny) + (nx * ax)) + (az * nz)) / len;
        var uz = nz / len;
        for (var i = 0; i < n; i++)
        {
            if (i == a || i == b || i == c)
                continue;
            var dist = MathF.Abs((((ux * p[i * 3]) + (uy * p[i * 3 + 1])) + (uz * p[i * 3 + 2])) - d);
            if (best < dist)
            {
                index = i;
                best = dist;
            }
        }
        return index;
    }

    // FUN_1803c6010
    private Face CreateFace(Vertex a, Vertex b, Vertex c)
    {
        var f = _facePool[_faceFree];
        _faceFree = f.FreeNext;
        f.Conflict.F0 = f.Conflict;
        f.Conflict.F1 = f.Conflict;
        var e1 = NewEdge();
        var e2 = NewEdge();
        var e3 = NewEdge();
        float ux = c.X - a.X, uy = c.Y - a.Y, uz = c.Z - a.Z;
        float wx = b.X - a.X, wy = b.Y - a.Y, wz = b.Z - a.Z;
        var ny = (ux * wz) - (uz * wx);
        var nx = (uz * wy) - (uy * wz);
        var nz = (uy * wx) - (ux * wy);
        var len = MathF.Sqrt(((nx * nx) + (ny * ny)) + (nz * nz));
        f.Edge = e1;
        f.F0 = null!;
        f.F1 = null!;
        f.Mark = 0;
        var d = (((a.Y * ny) + (a.X * nx)) + (a.Z * nz)) / len;
        f.Area = len * 0.5f;
        f.CX = ((b.X + a.X) + c.X) / 3f;
        f.CY = ((a.Y + b.Y) + c.Y) / 3f;
        f.CZ = ((b.Z + a.Z) + c.Z) / 3f;
        f.NX = nx / len;
        f.NY = ny / len;
        f.NZ = nz / len;
        f.D = d;
        f.Flipped = 0f < ((((ny / len) * IY) + ((nx / len) * IX)) + ((nz / len) * IZ)) - d;
        e1.Next = e2;
        e1.Face = f;
        e1.Prev = e3;
        e1.Origin = a;
        e1.Twin = null;
        e2.Face = f;
        e2.Prev = e1;
        e2.Next = e3;
        e2.Origin = b;
        e2.Twin = null;
        e3.Prev = e2;
        e3.Face = f;
        e3.Next = e1;
        e3.Origin = c;
        e3.Twin = null;
        return f;
    }

    // FUN_1803c8480
    private static void Twin(Face f, int i, Face g, int j)
    {
        var a = f.Edge!;
        for (; i > 0; i--)
            a = a.Next;
        var b = g.Edge!;
        for (; j > 0; j--)
            b = b.Next;
        a.Twin = b;
        b.Twin = a;
    }

    // FUN_1803c67f0
    private Vertex? NextConflictVertex()
    {
        Vertex? best = null;
        var bestD = Tol[2];
        foreach (var f in HullFaces)
        {
            foreach (var v in Walk<Vertex>(f.Conflict))
            {
                var d = ((f.NY * v.Y) + (f.NX * v.X)) + (f.NZ * v.Z) - f.D;
                if (bestD < d)
                {
                    bestD = d;
                    best = v;
                }
            }
        }
        return best;
    }

    // FUN_1803c4120
    private void BuildHorizon(List<HalfEdge> horizon, Vertex eye, Face face, HalfEdge? start)
    {
        face.Mark = 1;
        for (var node = face.Conflict.F1; node != face.Conflict;)
        {
            var next = node.F1;
            var v = (Vertex)node;
            v.ConflictFace = null;
            Unlink(v);
            Insert(v, Orphans);
            node = next;
        }
        HalfEdge e;
        if (start == null)
        {
            e = face.Edge!;
            start = e;
        }
        else
        {
            e = start.Next;
        }
        do
        {
            var other = e.Twin!.Face;
            if (other.Mark == 0)
            {
                var d = ((other.NY * eye.Y) + (eye.X * other.NX)) + (other.NZ * eye.Z) - other.D;
                if (d < Tol[1] || d == Tol[1])
                    horizon.Add(e);
                else
                    BuildHorizon(horizon, eye, other, e.Twin);
            }
            e = e.Next;
        } while (e != start);
    }

    // FUN_1803c3ee0
    private void AddNewFaces(List<Face> created, List<HalfEdge> horizon, Vertex eye)
    {
        _stamp++;
        var n = horizon.Count;
        if (n == 0)
            return;
        for (var i = 0; i < n; i++)
        {
            var v = horizon[i].Next.Origin;
            if (v != horizon[(i + 1) % n].Origin || v.Stamp == _stamp)
            {
                Error = 11;
                return;
            }
            v.Stamp = _stamp;
        }
        foreach (var e in horizon)
        {
            var f = CreateFace(eye, e.Origin, e.Twin!.Origin);
            created.Add(f);
            var a = f.Edge!.Next;
            a.Twin = e.Twin;
            e.Twin.Twin = a;
        }
        var prev = created[^1];
        foreach (var f in created)
        {
            Twin(prev, 2, f, 0);
            prev = f;
        }
    }

    // FUN_1803c8380: the neighbour's centroid is below this face by more than tol[1].
    private bool Convex(HalfEdge e)
    {
        var f = e.Face;
        var g = e.Twin!.Face;
        return ((f.NY * g.CY) + (g.CX * f.NX)) + (f.NZ * g.CZ) - f.D < -Tol[1];
    }

    // FUN_1803c6580
    private void MergeFaces(List<Face> created)
    {
        for (var i = 0; i < created.Count; i++)
        {
            var f = created[i];
            if (f.Mark != 0 || !f.Flipped)
                continue;
            HalfEdge? best = null;
            var area = 0f;
            foreach (var e in Loop(f))
            {
                var a = e.Twin!.Face.Area;
                if (area < a)
                {
                    area = a;
                    best = e;
                }
            }
            if (!Merge(best!))
                break;
            f.Flipped = false;
        }
        for (var i = 0; i < created.Count; i++)
        {
            var f = created[i];
            if (f.Mark != 0)
                continue;
        Restart:
            var e = f.Edge!;
            var flag = false;
            while (true)
            {
                var t = e.Twin!;
                bool merge;
                if (f.Area < t.Face.Area || f.Area == t.Face.Area)
                {
                    merge = !Convex(t);
                    if (!merge && !Convex(e))
                        flag = true;
                }
                else
                {
                    merge = !Convex(e);
                    if (!merge && !Convex(t))
                        flag = true;
                }
                if (merge)
                {
                    if (Merge(e))
                        goto Restart;
                    break;
                }
                e = e.Next;
                if (e == f.Edge)
                {
                    if (flag)
                        f.Mark = 2;
                    break;
                }
            }
        }
        for (var i = 0; i < created.Count; i++)
        {
            var f = created[i];
            if (f.Mark != 2)
                continue;
            f.Mark = 0;
            bool merged;
            do
            {
                var e = f.Edge!;
                while (true)
                {
                    var t = e.Twin!;
                    if (!Convex(e) || !Convex(t))
                        break;
                    e = e.Next;
                    if (e == f.Edge)
                        goto Next;
                }
                merged = Merge(e);
            } while (merged);
        Next:;
        }
    }

    // FUN_1803c4e30: fold the face across e into e's face.
    private bool Merge(HalfEdge edge)
    {
        if (Error != 0)
            return false;
        var f = edge.Face;
        var t = edge.Twin!;
        var ePrev = edge.Prev;
        var b = edge.Next;
        var c = t.Next;
        var d = t.Prev;
        var a = ePrev;
        if (ePrev.Twin == c)
        {
            do
            {
                a = a.Prev;
                c = c.Next;
                if (a == ePrev)
                {
                    Error = 11;
                    return false;
                }
            } while (a.Twin == c);
        }
        if (b.Twin == d)
        {
            do
            {
                b = b.Next;
                d = d.Prev;
            } while (b.Twin == d);
        }
        if (c.Prev == d || b.Prev == a)
        {
            Error = 11;
            return false;
        }
        if (!((a != b || d != c) && (a.Twin!.Face != c.Twin!.Face || d.Twin!.Face != b.Twin!.Face || b.Next != a || c.Next != d)))
        {
            Error = 11;
            return false;
        }
        for (var x = c; x != d.Next; x = x.Next)
        {
            if (x.Twin!.Face == f)
            {
                Error = 11;
                return false;
            }
        }
        f.Edge = a;
        var absorbed = new List<Face> { t.Face };
        t.Face.Mark = 1;
        t.Face.Edge = null;
        for (var x = c; x != d.Next; x = x.Next)
            x.Face = f;
        for (var x = a.Next; x != b;)
        {
            var next = x.Next;
            FreeEdge(x);
            x = next;
        }
        for (var x = d.Next; x != c;)
        {
            var next = x.Next;
            FreeEdge(x);
            x = next;
        }
        a.Next = c;
        c.Prev = a;
        d.Next = b;
        b.Prev = d;
        if (a != b && d != c)
        {
            FixTopology(a, c, absorbed);
            FixTopology(d, b, absorbed);
        }
        RebuildFace(f);
        for (var i = 0; i < absorbed.Count; i++)
        {
            var g = absorbed[i];
            for (var node = g.Conflict.F1; node != g.Conflict;)
            {
                var next = node.F1;
                var v = (Vertex)node;
                Unlink(v);
                var dist = ((v.Y * f.NY) + (f.NX * v.X)) + (v.Z * f.NZ) - f.D;
                if (dist < Tol[2] || dist == Tol[2])
                {
                    Insert(v, Orphans);
                }
                else
                {
                    Insert(v, f.Conflict);
                    v.ConflictFace = f;
                }
                node = next;
            }
        }
        return true;
    }

    // FUN_1803c69a0: a and b = a.Next share a neighbour on both sides.
    private void FixTopology(HalfEdge a, HalfEdge b, List<Face> absorbed)
    {
        if (a.Twin!.Face != b.Twin!.Face)
            return;
        if (a.Twin.Prev != b.Twin)
        {
            Error = 11;
            return;
        }
        if (b.Face.Edge == b)
            b.Face.Edge = a;
        var g = a.Twin.Face;
        HalfEdge nt;
        if (Loop(g).Count() == 3)
        {
            nt = b.Twin.Prev.Twin!;
            if (nt.Face == a.Face)
            {
                Error = 11;
                return;
            }
            g.Mark = 1;
            absorbed.Add(g);
        }
        else
        {
            nt = b.Twin;
            if (nt.Face.Edge == a.Twin)
                nt.Face.Edge = nt;
            var x = a.Twin.Next;
            nt.Next = x;
            x.Prev = nt;
            FreeEdge(a.Twin);
        }
        var bn = b.Next;
        a.Next = bn;
        bn.Prev = a;
        a.Twin = nt;
        nt.Twin = a;
        Unlink(b.Origin);
        FreeVertex(b.Origin);
        FreeEdge(b);
        RebuildFace(nt.Face);
    }

    // FUN_1803c84c0: Newell normal, vertex mean, area.
    private static void RebuildFace(Face f)
    {
        var count = 0;
        var e = f.Edge!;
        float cx = 0f, cy = 0f, cz = 0f, nx = 0f, ny = 0f, nz = 0f;
        do
        {
            var v = e.Origin;
            count++;
            var w = e.Twin!.Origin;
            e = e.Next;
            cx = v.X + cx;
            cy = v.Y + cy;
            cz = v.Z + cz;
            nx = (w.Z + v.Z) * (v.Y - w.Y) + nx;
            ny = (v.Z - w.Z) * (w.X + v.X) + ny;
            nz = (v.X - w.X) * (w.Y + v.Y) + nz;
        } while (e != f.Edge);
        var n = (float)count;
        f.CX = cx / n;
        f.CY = cy / n;
        f.CZ = cz / n;
        var len = MathF.Sqrt(((nx * nx) + (ny * ny)) + (nz * nz));
        f.Area = len * 0.5f;
        f.NX = nx / len;
        f.NY = ny / len;
        f.NZ = nz / len;
        f.D = (((cy / n) * (ny / len)) + ((cx / n) * (nx / len))) + ((cz / n) * (nz / len));
    }

    // FUN_1803c6d60
    private void ResolveOrphans(List<Face> created)
    {
        for (var node = Orphans.F1; node != Orphans;)
        {
            var next = node.F1;
            var v = (Vertex)node;
            Unlink(v);
            Face? best = null;
            var bestD = Tol[2];
            foreach (var f in created)
            {
                if (f.Mark != 0)
                    continue;
                var d = ((f.NY * v.Y) + (v.X * f.NX)) + (f.NZ * v.Z) - f.D;
                if (bestD < d)
                {
                    best = f;
                    bestD = d;
                }
            }
            if (best == null)
            {
                FreeVertex(v);
            }
            else
            {
                Insert(v, best.Conflict);
                v.ConflictFace = best;
            }
            node = next;
        }
    }

    // FUN_1803c6b60
    private void CleanFaces(List<Face> created)
    {
        for (var node = Faces.F1; node != Faces;)
        {
            var next = node.F1;
            var f = (Face)node;
            if (f.Mark == 1)
            {
                Unlink(f);
                FreeLoop(f);
                FreeFace(f);
            }
            node = next;
        }
        foreach (var f in created)
        {
            if (f.Mark == 1)
            {
                FreeLoop(f);
                FreeFace(f);
            }
            else
            {
                Insert(f, Faces);
            }
        }
    }

    private void FreeLoop(Face f)
    {
        if (f.Edge == null)
            return;
        var e = f.Edge;
        HalfEdge next;
        do
        {
            next = e.Next;
            FreeEdge(e);
            e = next;
        } while (next != f.Edge);
    }

    // FUN_1803c6300
    public bool IsValid()
    {
        if (Error != 0)
            return false;
        var v = HullVertices.Count();
        var e = HullFaces.Sum(f => Loop(f).Count());
        var fc = HullFaces.Count();
        if ((fc - e / 2) + v != 2)
        {
            Error = 6;
            return false;
        }
        foreach (var f in HullFaces)
        {
            if (f.Edge!.Face != f)
            {
                Error = 7;
                return false;
            }
            if (0f < ((f.NY * IY) + (IX * f.NX)) + (f.NZ * IZ) - f.D)
            {
                Error = 10;
                return false;
            }
            if (!FaceIsConsistent(f) || f.Mark != 0)
            {
                Error = 7;
                return false;
            }
            foreach (var x in Loop(f))
            {
                var n = x.Next;
                var end = x.Twin!.Origin;
                if (n.Origin != end || x.Prev.Next != x || n.Prev != x || x.Twin.Twin != x || x.Face != f)
                {
                    Error = 8;
                    return false;
                }
                var dz = x.Origin.Z - end.Z;
                var dy = x.Origin.Y - end.Y;
                var dx = x.Origin.X - end.X;
                if (MathF.Sqrt(((dy * dy) + (dx * dx)) + (dz * dz)) < 1.17549435e-35f)
                {
                    Error = 9;
                    return false;
                }
            }
        }
        return true;
    }

    // FUN_1803c83d0
    private static bool FaceIsConsistent(Face f)
    {
        if (f.Mark == 1)
            return false;
        if (Loop(f).Count() <= 2)
            return false;
        var e = f.Edge!;
        while (true)
        {
            var t = e.Twin;
            if (t == null || t.Face == null || t.Face == f || t.Face.Mark == 1 || t.Twin != e
                || e.Origin != t.Next.Origin || t.Origin != e.Next.Origin || e.Face != f)
                return false;
            e = e.Next;
            if (e == f.Edge)
                return true;
        }
    }

    // FUN_1803c3710: nudge each edge's ends toward the line where its two
    // planes meet. Planes are not recomputed.
    public void Sharpen(float angle, float maxMove)
    {
        foreach (var f in HullFaces)
        {
            float n1x = f.NX, n1y = f.NY, n1z = f.NZ;
            foreach (var e in Loop(f))
            {
                var t = e.Twin!;
                if (e.Index >= t.Index)
                    continue;
                var v = e.Origin;
                var w = t.Origin;
                float x0 = v.X, y0 = v.Y, z0 = v.Z, x1 = w.X, y1 = w.Y, z1 = w.Z;
                var dx = x0 - x1;
                var dy = y0 - y1;
                var dz = z0 - z1;
                var len = MathF.Sqrt(((dy * dy) + (dx * dx)) + (dz * dz));
                var ux = dx / len;
                var uy = dy / len;
                var uz = dz / len;
                var g = t.Face;
                float n2x = g.NX, n2y = g.NY, n2z = g.NZ;
                var cx = (n2z * n1y) - (n2y * n1z);
                var cy = (n2x * n1z) - (n1x * n2z);
                var cz = (n1x * n2y) - (n2x * n1y);
                var c2 = ((cy * cy) + (cx * cx)) + (cz * cz);
                float align;
                if (c2 <= 1.1920929e-05f)
                {
                    var ax = (n1y * uz) - (n1z * uy);
                    var ay = (n1z * ux) - (n1x * uz);
                    var az = (n1x * uy) - (n1y * ux);
                    var bx = (n2y * uz) - (n2z * uy);
                    var s1 = MathF.Sqrt(((ay * ay) + (ax * ax)) + (az * az));
                    var by = (n2z * ux) - (n2x * uz);
                    var bz = (n2x * uy) - (n2y * ux);
                    var s2 = MathF.Sqrt(((by * by) + (bx * bx)) + (bz * bz));
                    align = s2 <= s1 ? s2 : s1;
                    var hx = (n1x + n2x) * 0.5f;
                    var hy = (n2y + n1y) * 0.5f;
                    var hz = (n1z + n2z) * 0.5f;
                    var hl = MathF.Sqrt(((hy * hy) + (hx * hx)) + (hz * hz));
                    var dot = ((dy * (hy / hl)) + (dx * (hx / hl))) + (dz * (hz / hl));
                    dx = dot * (hx / hl);
                    dy = dot * (hy / hl);
                    dz = dot * (hz / hl);
                }
                else
                {
                    var cl = MathF.Sqrt(c2);
                    cz = cz / cl;
                    cy = cy / cl;
                    cx = cx / cl;
                    align = MathF.Abs(((cy * uy) + (cx * ux)) + (cz * uz));
                    var dot = ((dy * cy) + (dx * cx)) + (dz * cz);
                    dx = dx - (dot * cx);
                    dy = dy - (dot * cy);
                    dz = dz - (dot * cz);
                }
                if (angle < 1f - align)
                {
                    var m = MathF.Sqrt(((dy * dy) + (dx * dx)) + (dz * dz));
                    if (maxMove < m)
                    {
                        dx = (dx / m) * maxMove;
                        dy = (dy / m) * maxMove;
                        dz = (dz / m) * maxMove;
                    }
                    v.X = x0 - (dx * 0.5f);
                    v.Y = y0 - (dy * 0.5f);
                    v.Z = z0 - (dz * 0.5f);
                    w.X = x1 + (dx * 0.5f);
                    w.Y = y1 + (dy * 0.5f);
                    w.Z = z1 + (dz * 0.5f);
                }
            }
        }
    }

    // FUN_1803c7020
    public void Scale(float s)
    {
        if (s == 1f)
            return;
        foreach (var v in HullVertices)
        {
            v.X = s * v.X;
            v.Y = s * v.Y;
            v.Z = s * v.Z;
        }
        foreach (var f in HullFaces)
            f.D = s * f.D;
        IX = s * IX;
        IY = s * IY;
        IZ = s * IZ;
    }

    // FUN_1803c70c0
    public void Translate(float x, float y, float z)
    {
        if (x == 0f && y == 0f && z == 0f)
            return;
        foreach (var v in HullVertices)
        {
            v.X = v.X + x;
            v.Y = y + v.Y;
            v.Z = z + v.Z;
        }
        foreach (var f in HullFaces)
            f.D = ((f.NY * y) + (x * f.NX)) + (f.NZ * z) + f.D;
        IX = x + IX;
        IY = y + IY;
        IZ = z + IZ;
    }
}
