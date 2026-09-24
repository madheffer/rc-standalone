namespace Source2.Compiler.Physics;

/// <summary>
/// Simplify algorithm 0, "Quadric Error Metric" (resourcecompiler
/// <c>FUN_1813d3db0</c>), which the limits pass runs when a hull breaks a
/// vertex, edge or face limit or has two neighbouring faces within the merge
/// angle.
///
/// <para>The hull becomes a triangle mesh, a quadric edge collapse thins it
/// while it has more than 85 triangles or more vertices than the limit, and
/// what is left is hulled again. <c>CDualHullAgglomerator</c> then clusters
/// that hull's faces bottom up, keeps the clusters whose normals stay within
/// the angle, and the hull is rebuilt from their planes.</para>
///
/// <para>Modelled, not ported: the agglomerator keeps a dual half-edge mesh
/// (clusters as vertices, hull vertices as faces) in Valve's generic mesh
/// library. Only its adjacency reaches the result, through each cluster's
/// sharpness, so it is kept here as rings of clusters with the library's link
/// condition: two clusters merge in the dual only if every shared neighbour
/// tips a triangle on their common edge.</para>
/// </summary>
internal static class HullSimplifier
{
    /// <summary>How many times this thread has simplified, for tests.</summary>
    [ThreadStatic]
    internal static int Runs;

    /// <summary>The simplified hull, or null when it could not be simplified.</summary>
    public static QuickHull? Simplify(QuickHull hull, RnHullBuilder.Options o)
    {
        Runs++;
        var (positions, indices) = MeshFromHull(hull);
        var reduced = Qem.Simplify(positions, indices, o.Tolerance * o.Tolerance, o.MaxVertices);
        var qh = new QuickHull();
        qh.Build(reduced.Length / 3, reduced, 1e-6f, true);
        if (!qh.IsValid())
            return null;
        return Rebuild(qh, o);
    }

    // FUN_1813d0f90: vertices in list order, each face fanned from its first
    // half-edge (FUN_1813cfad0).
    private static (float[] Positions, int[] Indices) MeshFromHull(QuickHull qh)
    {
        var index = new Dictionary<QuickHull.Vertex, int>(ReferenceEqualityComparer.Instance);
        var positions = new List<float>();
        foreach (var v in qh.HullVertices)
        {
            index[v] = index.Count;
            positions.Add(v.X);
            positions.Add(v.Y);
            positions.Add(v.Z);
        }
        var indices = new List<int>();
        foreach (var f in qh.HullFaces)
        {
            var e0 = f.Edge!;
            var prev = e0.Next.Next;
            indices.Add(index[e0.Origin]);
            indices.Add(index[e0.Next.Origin]);
            indices.Add(index[prev.Origin]);
            for (var e = prev.Next; e != e0; e = e.Next)
            {
                indices.Add(index[e0.Origin]);
                indices.Add(index[prev.Origin]);
                indices.Add(index[e.Origin]);
                prev = e;
            }
        }
        return ([.. positions], [.. indices]);
    }

    // FUN_1813d3f00: planes from the agglomerator, fewer each time the plane
    // hull breaks a limit.
    private static QuickHull? Rebuild(QuickHull qh, RnHullBuilder.Options o)
    {
        var agg = new Agglomerator(qh, MathF.Tan(o.Angle * 0.017453292f));
        var n = o.MaxFaces;
        var tolerance = o.Tolerance > 1e-6f ? o.Tolerance : 1e-6f;
        QuickHull result;
        while (true)
        {
            result = new QuickHull();
            FromPlanes(result, agg.Planes(n), tolerance, o.RelativeTolerance);
            var faces = result.HullFaces.Count();
            if (n <= 4 || faces <= 4)
                break;
            var edges = result.HullFaces.Sum(f => QuickHull.Loop(f).Count());
            var verts = result.HullVertices.Count();
            if (faces <= o.MaxFaces && edges <= o.MaxEdges && verts <= o.MaxVertices)
                break;
            var m = n - n / 16;
            if (m - 1 <= faces)
                faces = m;
            n = faces - 1;
        }
        result.Translate(agg.CX, agg.CY, agg.CZ);
        return result.IsValid() ? result : null;
    }

    // FUN_181bfd190 with the origin at zero: each plane's dual point
    // n / d is hulled (tolerance 0, the tolerance scale 1 rather than the
    // constructor's 50), each dual face's n / d is a vertex, and those are
    // hulled at the given tolerance.
    private static void FromPlanes(QuickHull q, List<(float X, float Y, float Z, float D)> planes, float tolerance, bool relative)
    {
        if (planes.Count < 4)
        {
            q.Error = 2;
            return;
        }
        var dual = new float[planes.Count * 3];
        for (var i = 0; i < planes.Count; i++)
        {
            var (x, y, z, d) = planes[i];
            var w = ((-0f * x - y * 0f) - z * 0f) + d;
            if (w <= 0f)
            {
                q.Error = 1;
                return;
            }
            dual[i * 3] = x / w;
            dual[i * 3 + 1] = y / w;
            dual[i * 3 + 2] = z / w;
        }
        var dh = new QuickHull { ToleranceScale = 1f };
        dh.Build(planes.Count, dual, 0f, true);
        if (!dh.IsValid())
        {
            q.Error = dh.Error;
            return;
        }
        var primal = new List<float>();
        foreach (var f in dh.HullFaces)
        {
            primal.Add(f.NX / f.D + 0f);
            primal.Add(f.NY / f.D + 0f);
            primal.Add(f.NZ / f.D + 0f);
        }
        q.Build(primal.Count / 3, [.. primal], tolerance, relative);
    }

    // maxss: the first operand when it is greater, else the second.
    private static float Max(float a, float b) => a > b ? a : b;

    // FUN_18125d000: normalise in double, returning the length.
    private static float NormalizeDouble(ref float x, ref float y, ref float z)
    {
        double dy = y, dx = x, dz = z;
        var len = Math.Sqrt(dy * dy + dx * dx + dz * dz);
        var inv = 1.0 / len;
        if (3.4028234663852886e+38 <= len)
            len = 3.4028234663852886e+38;
        x = (float)(inv * dx);
        z = (float)(inv * dz);
        y = (float)(inv * dy);
        return (float)len;
    }

    // The mathlib normalise: 1/len when the length is sane, the double
    // version when it is not, zero for a zero vector.
    private static float Normalize(ref float x, ref float y, ref float z)
    {
        var len = MathF.Sqrt(((z * z) + (y * y)) + (x * x));
        if (len < 1e-17f || 1e+17f < len)
        {
            if (len != 0f)
                return NormalizeDouble(ref x, ref y, ref z);
            x = y = z = 0f;
            return 0f;
        }
        var inv = 1f / len;
        x = x * inv;
        y = y * inv;
        z = z * inv;
        return len;
    }

    /// <summary>The quadric edge collapse (FUN_1813fc5d0 and its helpers).</summary>
    private sealed class Qem
    {
        private sealed class Pair
        {
            public readonly float[] Q = new float[10];
            public float X, Y, Z;
            public float Cost;
            public float T;
            public int V0, V1;
            public int Faces;
            public short KeepSecond;
            public bool Dead;
        }

        private readonly float[] _pos;
        private readonly List<(int Next, int Prev)>[] _around;
        private readonly float[][] _quad;
        private readonly List<Pair> _pairs = [];
        private readonly Dictionary<(int, int), int> _edges = [];
        private readonly List<(float Cost, int Pair)> _heap = [];
        private int _triangles;

        private Qem(float[] positions, int[] indices)
        {
            _pos = (float[])positions.Clone();
            var v = positions.Length / 3;
            _around = new List<(int, int)>[v];
            _quad = new float[v][];
            for (var i = 0; i < v; i++)
            {
                _around[i] = [];
                _quad[i] = new float[10];
            }
            // FUN_1813f9e70: a pair per edge in first-seen order, and per
            // corner the triangle's next and previous corners.
            _triangles = indices.Length / 3;
            for (var t = 0; t + 2 < indices.Length; t += 3)
            {
                for (var k = 0; k < 3; k++)
                {
                    var a = indices[t + k];
                    var next = indices[t + (k + 1) % 3];
                    var prev = indices[t + (k + 2) % 3];
                    var key = Key(a, next);
                    if (!_edges.TryGetValue(key, out var pi))
                    {
                        pi = _pairs.Count;
                        _pairs.Add(new Pair { V0 = key.Item1, V1 = key.Item2 });
                        _edges[key] = pi;
                    }
                    _pairs[pi].Faces++;
                    _around[a].Add((next, prev));
                }
            }
        }

        private static (int, int) Key(int a, int b) => a <= b ? (a, b) : (b, a);

        private float Px(int v) => _pos[v * 3];
        private float Py(int v) => _pos[v * 3 + 1];
        private float Pz(int v) => _pos[v * 3 + 2];

        /// <summary>The simplified mesh's positions, in first-use order; empty when it gave up.</summary>
        public static float[] Simplify(float[] positions, int[] indices, float maxError, int maxVertices)
        {
            var q = new Qem(positions, indices);
            q.Quadrics(2f, 0f);
            for (var i = 0; i < q._pairs.Count; i++)
            {
                q.Cost(q._pairs[i]);
                q.Push(q._pairs[i].Cost, i);
            }
            var found = q.NextCollapse();
            var used = new bool[positions.Length / 3];
            var vertices = 0;
            foreach (var i in indices)
            {
                if (!used[i])
                {
                    used[i] = true;
                    vertices++;
                }
            }
            while (found >= 0)
            {
                var cost = q._pairs[found].Cost;
                if (!((maxError > cost) || vertices > maxVertices || q._triangles > 85))
                    break;
                if (!(vertices > 4 && q._triangles > 4))
                    break;
                q.Collapse(found);
                vertices--;
                if (vertices < 5)
                    return [];
                found = q.NextCollapse();
            }
            return q.Output();
        }

        // The pop loop: stale and dead entries are dropped, and so is a pair
        // that fails the link or flip test.
        private int NextCollapse()
        {
            while (_heap.Count != 0)
            {
                var (cost, i) = Pop();
                var p = _pairs[i];
                if (p.Cost != cost || p.Dead)
                    continue;
                if (Shared(p.V0, p.V1) >= 3)
                    continue;
                if (Keeps(p.V0, p.V1, p) && Keeps(p.V1, p.V0, p))
                    return i;
            }
            return -1;
        }

        // FUN_1813fae30: per vertex, the sum of its triangles' plane
        // quadrics weighted by half their area, or of the perpendicular
        // plane through a boundary edge.
        private void Quadrics(float boundaryWeight, float minWeight)
        {
            for (var v = 0; v < _around.Length; v++)
            {
                var q = _quad[v];
                Array.Clear(q);
                float vx = Px(v), vy = Py(v), vz = Pz(v);
                foreach (var (n, p) in _around[v])
                {
                    var boundary = _edges.TryGetValue(Key(v, n), out var pi) && _pairs[pi].Faces < 2;
                    if (boundary)
                    {
                        var e1z = Pz(n) - vz;
                        var e2z = Pz(p) - vz;
                        var e2x = Px(p) - vx;
                        var e1y = Py(n) - vy;
                        var e1x = Px(n) - vx;
                        var e2y = Py(p) - vy;
                        var cx = e2y * e1z - e2z * e1y;
                        var cy = e2z * e1x - e2x * e1z;
                        var cz = e2x * e1y - e2y * e1x;
                        var bz = cy * e1x - cx * e1y;
                        var by = e1z * cx - cz * e1x;
                        var bx = cz * e1y - e1z * cy;
                        var len = Normalize(ref bx, ref by, ref bz);
                        var w = len * 0.5f * boundaryWeight;
                        var w2 = w + w;
                        var d = -((vz * bz + vy * by) + bx * vx);
                        q[1] = w2 * (bx * by) + q[1];
                        q[2] = w2 * (bx * bz) + q[2];
                        q[3] = w2 * (d * bx) + q[3];
                        q[0] = bx * bx * w + q[0];
                        q[5] = w2 * (bz * by) + q[5];
                        q[4] = by * by * w + q[4];
                        q[8] = w2 * (d * bz) + q[8];
                        q[6] = w2 * (d * by) + q[6];
                        q[7] = bz * bz * w + q[7];
                        q[9] = d * d * w + q[9];
                    }
                    else
                    {
                        var e2x = Px(p) - vx;
                        var e1x = Px(n) - vx;
                        var e1z = Pz(n) - vz;
                        var e1y = Py(n) - vy;
                        var e2z = Pz(p) - vz;
                        var e2y = Py(p) - vy;
                        var nx = e2y * e1z - e1y * e2z;
                        var ny = e1x * e2z - e2x * e1z;
                        var nz = e2x * e1y - e2y * e1x;
                        var len = Normalize(ref nx, ref ny, ref nz);
                        var w = minWeight;
                        if (minWeight <= len * 0.5f)
                            w = len * 0.5f;
                        var w2 = w + w;
                        var d = -((vz * nz + vy * ny) + vx * nx);
                        q[0] = nx * nx * w + q[0];
                        q[1] = nx * ny * w2 + q[1];
                        q[2] = nx * nz * w2 + q[2];
                        q[3] = nx * d * w2 + q[3];
                        q[5] = nz * ny * w2 + q[5];
                        q[4] = ny * ny * w + q[4];
                        q[7] = nz * nz * w + q[7];
                        q[6] = d * ny * w2 + q[6];
                        q[8] = d * nz * w2 + q[8];
                        q[9] = d * d * w + q[9];
                    }
                }
            }
        }

        private static float Evaluate(float[] q, float x, float y, float z) =>
            x * x * q[0] + q[9] + y * x * q[1] + z * x * q[2] + q[3] * x
            + y * y * q[4] + z * y * q[5] + q[6] * y + z * z * q[7] + q[8] * z;

        // FUN_1813fd060: the cheaper endpoint, or the quadric's minimum when
        // it is cheaper still and lies nearer either endpoint than they lie
        // to each other; T is where the target projects onto the edge.
        private void Cost(Pair p)
        {
            var qa = _quad[p.V0];
            var qb = _quad[p.V1];
            var q = p.Q;
            for (var k = 0; k < 10; k++)
                q[k] = qa[k] + qb[k];
            float ax = Px(p.V0), ay = Py(p.V0), az = Pz(p.V0);
            float bx = Px(p.V1), by = Py(p.V1), bz = Pz(p.V1);
            var costA = Evaluate(q, ax, ay, az);
            p.Cost = costA;
            p.KeepSecond = 0;
            var costB = Evaluate(q, bx, by, bz);
            if (!(costA > costB))
            {
                (p.X, p.Y, p.Z) = (ax, ay, az);
                p.T = 0f;
            }
            else
            {
                (p.X, p.Y, p.Z) = (bx, by, bz);
                p.T = 1f;
                p.Cost = costB;
                p.KeepSecond = 1;
            }
            var (x0, x1, x2) = Solve(q);
            float tx = -x0, ty = -x1, tz = -x2;
            var best = p.Cost;
            var costT = Evaluate(q, tx, ty, tz);
            if (costT < best)
            {
                var dx = bx - ax;
                var dy = by - ay;
                var dz = bz - az;
                var len = Normalize(ref dx, ref dy, ref dz);
                var fromA = MathF.Sqrt((tz - az) * (tz - az) + (ty - ay) * (ty - ay) + (tx - ax) * (tx - ax));
                if (len > fromA || len > MathF.Sqrt((ty - by) * (ty - by) + (tz - bz) * (tz - bz) + (tx - bx) * (tx - bx)))
                {
                    (p.X, p.Y, p.Z) = (tx, ty, tz);
                    p.Cost = costT;
                    best = costT;
                    if (0f < len)
                    {
                        var t = (dz * (p.Z - az) + dy * (p.Y - ay) + dx * (p.X - ax)) * (1f / len);
                        p.T = 0f > t ? 0f : (1f < t ? 1f : t);
                    }
                }
            }
            p.Cost = 0f > best ? 0f : best;
        }

        // FUN_1820682a0, FUN_182068430, FUN_182068480: a clamped Cholesky
        // solve of the quadric's 3x3 block against half its linear terms.
        private static (float, float, float) Solve(float[] q)
        {
            float a = q[0], b = q[1] * 0.5f, c = q[4], d = q[2] * 0.5f, e = q[5] * 0.5f, f = q[7];
            var l00 = MathF.Sqrt(Max(a, 0f));
            var inv0 = 1f / Max(l00, 1e-08f);
            var l10 = inv0 * b;
            var l11 = MathF.Sqrt(Max(c - l10 * l10, 0f));
            var l20 = inv0 * d;
            var inv1 = 1f / Max(l11, 1e-08f);
            var l21 = (e - l20 * l10) * inv1;
            var l22 = MathF.Sqrt(Max((f - l20 * l20) - l21 * l21, 0f));
            var inv2 = 1f / Max(l22, 1e-08f);
            float r0 = q[3] * 0.5f, r1 = q[6] * 0.5f, r2 = q[8] * 0.5f;
            var y0 = inv0 * r0;
            var y1 = (r1 - y0 * l10) * inv1;
            var y2 = ((r2 - y0 * l20) - y1 * l21) * inv2;
            var x2 = inv2 * y2;
            var x1 = (y1 - x2 * l21) * inv1;
            var x0 = ((y0 - x2 * l20) - x1 * l10) * inv0;
            return (x0, x1, x2);
        }

        // Min-heap on cost; an equal parent still swaps (FUN_1813fcf10).
        private void Push(float cost, int pair)
        {
            _heap.Add((cost, pair));
            var i = _heap.Count - 1;
            while (i != 0)
            {
                var parent = (i + 1) / 2 - 1;
                var c = _heap[i].Cost;
                if (_heap[parent].Cost <= c && c != _heap[parent].Cost)
                    break;
                (_heap[parent], _heap[i]) = (_heap[i], _heap[parent]);
                i = parent;
            }
        }

        // FUN_1813fc1a0 after moving the last entry to the top.
        private (float, int) Pop()
        {
            var top = _heap[0];
            var n = _heap.Count;
            if (n > 1)
                _heap[0] = _heap[n - 1];
            _heap.RemoveAt(n - 1);
            n--;
            var i = 0;
            if (n / 2 <= i)
                return top;
            do
            {
                var best = i;
                var left = i * 2 + 1;
                if (left < n && _heap[left].Cost <= _heap[i].Cost && _heap[i].Cost != _heap[left].Cost)
                    best = left;
                var right = i * 2 + 2;
                if (right < n && _heap[right].Cost <= _heap[best].Cost && _heap[best].Cost != _heap[right].Cost)
                    best = right;
                if (best == i)
                    break;
                (_heap[i], _heap[best]) = (_heap[best], _heap[i]);
                i = best;
            } while (i < n / 2);
            return top;
        }

        // FUN_1813fbb20: a vertex's neighbours, next then previous corner.
        private List<int> Neighbours(int v)
        {
            var list = new List<int>();
            foreach (var (n, p) in _around[v])
            {
                if (!list.Contains(n))
                    list.Add(n);
                if (!list.Contains(p))
                    list.Add(p);
            }
            return list;
        }

        // FUN_1813fb800
        private int Shared(int a, int b)
        {
            var na = Neighbours(a);
            return Neighbours(b).Count(na.Contains);
        }

        // FUN_1813fbf30: no triangle of v away from the other end flips when
        // v moves to the target.
        private bool Keeps(int v, int other, Pair t)
        {
            float vx = Px(v), vy = Py(v), vz = Pz(v);
            foreach (var (n, p) in _around[v])
            {
                if (n == other || p == other)
                    continue;
                var e1x = Px(n) - vx;
                var e2x = Px(p) - vx;
                var e2z = Pz(p) - vz;
                var e1z = Pz(n) - vz;
                var f2x = Px(p) - t.X;
                var f2z = Pz(p) - t.Z;
                var f1x = Px(n) - t.X;
                var f1z = Pz(n) - t.Z;
                var f1y = Py(n) - t.Y;
                var f2y = Py(p) - t.Y;
                var e1y = Py(n) - vy;
                var e2y = Py(p) - vy;
                if ((f2z * f1x - f2x * f1z) * (e2z * e1x - e2x * e1z)
                    + (f2x * f1y - f2y * f1x) * (e2x * e1y - e2y * e1x)
                    + (f2y * f1z - f2z * f1y) * (e2y * e1z - e2z * e1y) < 0f)
                    return false;
            }
            return true;
        }

        // FUN_1813fa630
        private void Collapse(int index)
        {
            var p = _pairs[index];
            p.Dead = true;
            int a = p.V0, b = p.V1;
            var t = p.T;
            int removed = b, kept = a;
            if (p.KeepSecond == 1)
                (removed, kept) = (a, b);
            var qk = _quad[kept];
            var qr = _quad[removed];
            for (var k = 0; k < 10; k++)
                qk[k] = qr[k] + qk[k];
            for (var k = 0; k < 10; k++)
                qk[k] = 1f * qk[k];
            var ring = Neighbours(removed);
            var dropped = 0;
            foreach (var u in ring)
            {
                var list = _around[u];
                var gone = new List<int>();
                for (var j = 0; j < list.Count; j++)
                {
                    var (x, y) = list[j];
                    if (x != removed && y != removed)
                        continue;
                    if (x == removed)
                        x = kept;
                    if (y == removed)
                        y = kept;
                    list[j] = (x, y);
                    if (x == y || x == u || y == u)
                        gone.Add(j);
                }
                dropped += gone.Count;
                for (var k = gone.Count - 1; k >= 0; k--)
                {
                    if (list.Count <= 0)
                        continue;
                    if (gone[k] != list.Count - 1)
                        list[gone[k]] = list[^1];
                    list.RemoveAt(list.Count - 1);
                }
            }
            foreach (var u in ring)
                Retarget(removed, u, kept);
            var own = 0;
            foreach (var entry in _around[removed])
            {
                if (entry.Next == kept || entry.Prev == kept)
                    own++;
                else
                    _around[kept].Add(entry);
            }
            _around[removed].Clear();
            _triangles -= (own + dropped) / 3;
            for (var i = 0; i < 3; i++)
                _pos[kept * 3 + i] = (_pos[b * 3 + i] - _pos[a * 3 + i]) * t + _pos[a * 3 + i];
        }

        // FUN_1813fc310: the pair (removed, u) now ends at kept; it dies if
        // that closes it or duplicates a pair, and is re-costed either way
        // unless it closed.
        private void Retarget(int removed, int u, int kept)
        {
            if (!_edges.TryGetValue(Key(removed, u), out var pi))
                return;
            var p = _pairs[pi];
            var first = p.V0 == removed;
            if (first)
                p.V0 = kept;
            if (p.V1 == removed)
                p.V1 = kept;
            else if (!first)
                return;
            _edges.Remove(Key(removed, u));
            if (p.V0 == p.V1)
            {
                p.Dead = true;
                return;
            }
            Cost(p);
            Push(p.Cost, pi);
            var key = Key(p.V0, p.V1);
            if (_edges.ContainsKey(key))
                p.Dead = true;
            else
                _edges[key] = pi;
        }

        // Each triangle once, from its lowest corner, then vertices
        // renumbered in first-use order.
        private float[] Output()
        {
            var tris = new List<int>();
            for (var u = 0; u < _around.Length; u++)
            {
                foreach (var (x, y) in _around[u])
                {
                    if ((uint)u <= (uint)x && (uint)u <= (uint)y)
                    {
                        tris.Add(u);
                        tris.Add(x);
                        tris.Add(y);
                    }
                }
            }
            var order = new List<int>();
            var seen = new HashSet<int>();
            foreach (var v in tris)
            {
                if (seen.Add(v))
                    order.Add(v);
            }
            var remap = new int[_around.Length];
            Array.Fill(remap, -1);
            for (var i = 0; i < order.Count; i++)
                remap[order[i]] = i;
            var result = new float[order.Count * 3];
            for (var v = 0; v < _around.Length; v++)
            {
                if (remap[v] < 0)
                    continue;
                result[remap[v] * 3] = _pos[v * 3];
                result[remap[v] * 3 + 1] = _pos[v * 3 + 1];
                result[remap[v] * 3 + 2] = _pos[v * 3 + 2];
            }
            return result;
        }
    }

    /// <summary>
    /// <c>CDualHullAgglomerator</c> (FUN_1813d02e0) over Valve's agglomerative
    /// clustering (FUN_1820712f0, FUN_182071730, FUN_1820722c0): every pair
    /// of neighbouring clusters costs log(sum of area x tangent to the merged
    /// normal x sharpness), plus 90 when either side turns past the angle;
    /// the cheapest pair merges until one cluster is left.
    /// </summary>
    private sealed class Agglomerator
    {
        private sealed class Node
        {
            public int Id;
            public int Slot = -1;
            public int A = -1, B = -1;
            public readonly List<(Node Node, float Cost)> Near = [];
            public float Key => Near.Count < 1 ? float.MaxValue : Near[0].Cost;
        }

        private sealed class Rec
        {
            public float X, Y, Z, D, Area;
            public float Sharp = 1f;
            public int Dual = -1;
        }

        public readonly float CX, CY, CZ;
        private readonly float _tan;
        private readonly int _leaves;
        private readonly List<Node> _nodes = [];
        private readonly List<Rec> _recs = [];
        private readonly Dual _dual = new();

        public Agglomerator(QuickHull qh, float tan)
        {
            _tan = tan;
            var faces = qh.HullFaces.ToList();
            _leaves = faces.Count;
            float cx = 0f, cy = 0f, cz = 0f;
            var count = 0;
            foreach (var v in qh.HullVertices)
            {
                cx = cx + v.X;
                cy = cy + v.Y;
                cz = cz + v.Z;
                count++;
            }
            if (count != 0)
            {
                var fn = (float)count;
                cx = cx / fn;
                cy = cy / fn;
                cz = cz / fn;
            }
            (CX, CY, CZ) = (cx, cy, cz);
            var index = new Dictionary<QuickHull.Face, int>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < faces.Count; i++)
                index[faces[i]] = i;
            // FUN_1813d3a30: a leaf per face, its plane relative to the centroid
            // times its area, sharpened by its own edges.
            for (var i = 0; i < faces.Count; i++)
            {
                var f = faces[i];
                _nodes.Add(new Node { Id = i });
                var r = new Rec
                {
                    X = f.NX * f.Area,
                    Y = f.NY * f.Area,
                    Z = f.NZ * f.Area,
                    D = (f.D - ((f.NZ * cz + f.NY * cy) + f.NX * cx)) * f.Area,
                    Area = f.Area,
                };
                foreach (var e in QuickHull.Loop(f))
                {
                    var g = e.Twin!.Face;
                    var s = EdgeSharpness(g.NX, g.NY, g.NZ, f.NX, f.NY, f.NZ);
                    r.Sharp = r.Sharp > s ? r.Sharp : s;
                }
                _recs.Add(r);
            }
            // FUN_1813d20d0: the dual, a ring of faces around each hull vertex.
            foreach (var v in qh.HullVertices)
            {
                var ring = new List<int>();
                var e = v.Edge!;
                do
                {
                    ring.Add(index[e.Face]);
                    e = e.Twin!.Next;
                } while (e != v.Edge);
                _dual.AddFace(ring);
            }
            _dual.Leaves(faces.Count);
            for (var i = 0; i < faces.Count; i++)
                _recs[i].Dual = i;
            // FUN_1813d30f0
            for (var i = 0; i < faces.Count; i++)
            {
                foreach (var e in QuickHull.Loop(faces[i]))
                    Connect(_nodes[index[e.Twin!.Face]], _nodes[i]);
            }
            Agglomerate();
        }

        // FUN_1813d0940: 1 unless the normals are more than 90 degrees apart.
        private float EdgeSharpness(float ax, float ay, float az, float bx, float by, float bz)
        {
            var dn = -((az * bz + ay * by) + ax * bx);
            if (dn <= 0f)
                return 1f;
            var c2 = by * ax - ay * bx;
            var c0 = ay * bz - az * by;
            var c1 = az * bx - bz * ax;
            var len = MathF.Sqrt((c1 * c1 + c2 * c2) + c0 * c0);
            var t = dn * _tan;
            if (len <= t + 1e-08f)
                return 1e+12f;
            return 1f / (len - t);
        }

        // FUN_182071c00
        private void Connect(Node j, Node i)
        {
            if (j == i)
                return;
            foreach (var (n, _) in j.Near)
            {
                if (n == i)
                    return;
            }
            var cost = Cost(j, i);
            PushNear(j, i, cost);
            PushNear(i, j, cost);
        }

        // FUN_1813d06c0
        private float Cost(Node na, Node nb)
        {
            var a = Record(na.Id);
            var b = Record(nb.Id);
            var sz = a.Z + b.Z;
            var sy = a.Y + b.Y;
            var sx = a.X + b.X;
            var c0 = sz * a.Y - sy * a.Z;
            var dot = (sz * a.Z + sy * a.Y) + sx * a.X;
            var c1 = sx * a.Z - sz * a.X;
            var c2 = sy * a.X - sx * a.Y;
            var len = MathF.Sqrt((c2 * c2 + c1 * c1) + c0 * c0);
            var ta = ((len + 1e-06f) * 1e-12f < dot ? len / dot : 1e+12f) * a.Sharp;
            var dotB = (sz * b.Z + sy * b.Y) + sx * b.X;
            var d0 = sz * b.Y - sy * b.Z;
            var d1 = sx * b.Z - sz * b.X;
            var d2 = sy * b.X - sx * b.Y;
            var lenB = MathF.Sqrt((d1 * d1 + d2 * d2) + d0 * d0);
            var tb = ((lenB + 1e-06f) * 1e-12f < dotB ? lenB / dotB : 1e+12f) * b.Sharp;
            var cost = MathF.Log(b.Area * tb + a.Area * ta);
            if (_tan < ta || _tan < tb)
                cost = cost + 90f;
            return cost;
        }

        // FUN_1813d1560: a merged node's record, made in id order on first
        // use: the children's sums, the larger sharpness, and the dual merge
        // that sharpens it against its new neighbours.
        private Rec Record(int id)
        {
            while (_recs.Count <= id)
            {
                var node = _nodes[_recs.Count];
                var ra = _recs[node.A];
                var rb = _recs[node.B];
                var r = new Rec
                {
                    X = rb.X + ra.X,
                    Y = rb.Y + ra.Y,
                    Z = rb.Z + ra.Z,
                    D = rb.D + ra.D,
                    Area = rb.Area + ra.Area,
                    Sharp = ra.Sharp > rb.Sharp ? ra.Sharp : rb.Sharp,
                };
                _recs.Add(r);
                var merged = _dual.Merge(ra.Dual, rb.Dual, _recs.Count - 1);
                if (merged < 0)
                    continue;
                r.Dual = merged;
                ra.Dual = -1;
                rb.Dual = -1;
                float ux = r.X, uy = r.Y, uz = r.Z;
                Normalize(ref ux, ref uy, ref uz);
                foreach (var cluster in _dual.Around(merged))
                {
                    var other = _recs[cluster];
                    float wx = other.X, wy = other.Y, wz = other.Z;
                    Normalize(ref wx, ref wy, ref wz);
                    var dn = -((wz * uz + wy * uy) + ux * wx);
                    var s = 1f;
                    if (0f < dn)
                    {
                        var c0 = wy * uz - wz * uy;
                        var c1 = wz * ux - uz * wx;
                        var c2 = uy * wx - wy * ux;
                        var len = MathF.Sqrt((c2 * c2 + c1 * c1) + c0 * c0);
                        var t = dn * _tan;
                        s = t + 1e-08f < len ? 1f / (len - t) : 1e+12f;
                    }
                    other.Sharp = other.Sharp > s ? other.Sharp : s;
                    r.Sharp = r.Sharp > s ? r.Sharp : s;
                }
            }
            return _recs[id];
        }

        private static void PushNear(Node n, Node other, float cost)
        {
            var h = n.Near;
            h.Add((other, cost));
            SiftUpNear(h, h.Count - 1);
        }

        // FUN_1820721d0
        private static void SiftUpNear(List<(Node Node, float Cost)> h, int i)
        {
            if (i >= h.Count)
                return;
            while (i != 0)
            {
                var parent = (i + 1) / 2 - 1;
                var c = h[i].Cost;
                if (h[parent].Cost <= c && c != h[parent].Cost)
                    return;
                (h[parent], h[i]) = (h[i], h[parent]);
                i = parent;
            }
        }

        // FUN_182071ef0
        private static int SiftDownNear(List<(Node Node, float Cost)> h, int i)
        {
            var n = h.Count;
            if (n / 2 <= i)
                return i;
            do
            {
                var best = i;
                var left = i * 2 + 1;
                if (left < n && h[left].Cost <= h[i].Cost && h[i].Cost != h[left].Cost)
                    best = left;
                var right = i * 2 + 2;
                if (right < n && h[right].Cost <= h[best].Cost && h[best].Cost != h[right].Cost)
                    best = right;
                if (best == i)
                    break;
                (h[i], h[best]) = (h[best], h[i]);
                i = best;
            } while (i < n / 2);
            return i;
        }

        private static void RemoveNear(Node n, Node other)
        {
            var h = n.Near;
            for (var j = 0; j < h.Count; j++)
            {
                if (h[j].Node != other)
                    continue;
                if (j + 1 < h.Count)
                    h[j] = h[^1];
                h.RemoveAt(h.Count - 1);
                if (SiftDownNear(h, j) == j)
                    SiftUpNear(h, j);
                return;
            }
        }

        // FUN_182072070
        private static void SiftUp(List<Node> heap, int i)
        {
            if (i >= heap.Count)
                return;
            while (i != 0)
            {
                var parent = (i + 1) / 2 - 1;
                if (heap[parent].Key < heap[i].Key)
                    return;
                (heap[parent], heap[i]) = (heap[i], heap[parent]);
                heap[parent].Slot = parent;
                heap[i].Slot = i;
                i = parent;
            }
        }

        // FUN_182071cb0
        private static int SiftDown(List<Node> heap, int i)
        {
            var n = heap.Count;
            if (n / 2 <= i)
                return i;
            do
            {
                var best = i;
                var left = i * 2 + 1;
                if (left < n && heap[left].Key < heap[i].Key)
                    best = left;
                var right = i * 2 + 2;
                if (right < n && heap[right].Key < heap[best].Key)
                    best = right;
                if (best == i)
                    break;
                (heap[i], heap[best]) = (heap[best], heap[i]);
                heap[i].Slot = i;
                heap[best].Slot = best;
                i = best;
            } while (i < n / 2);
            return i;
        }

        private static void Add(List<Node> heap, Node n)
        {
            heap.Add(n);
            n.Slot = heap.Count - 1;
            SiftUp(heap, n.Slot);
        }

        private static void Remove(List<Node> heap, Node n)
        {
            var i = n.Slot;
            n.Slot = -1;
            if (i + 1 < heap.Count)
                heap[i] = heap[^1];
            heap.RemoveAt(heap.Count - 1);
            if (i < heap.Count)
                heap[i].Slot = i;
            if (SiftDown(heap, i) == i)
                SiftUp(heap, i);
        }

        // FUN_182071730, FUN_1820722c0
        private void Agglomerate()
        {
            var heap = new List<Node>();
            foreach (var n in _nodes)
                Add(heap, n);
            while (heap.Count >= 1 && heap[0].Near.Count >= 1)
            {
                var a = heap[0];
                var b = a.Near[0].Node;
                a.Slot = -1;
                if (heap.Count > 1)
                    heap[0] = heap[^1];
                heap.RemoveAt(heap.Count - 1);
                if (heap.Count > 0)
                {
                    heap[0].Slot = 0;
                    SiftDown(heap, 0);
                }
                Remove(heap, b);
                var c = new Node { Id = _nodes.Count, A = a.Id, B = b.Id };
                _nodes.Add(c);
                var marked = new HashSet<Node>(ReferenceEqualityComparer.Instance);
                var pending = new List<Node>();
                for (var k = 0; k < a.Near.Count; k++)
                {
                    var n = a.Near[k].Node;
                    if (n == b)
                        continue;
                    var before = n.Key;
                    RemoveNear(n, a);
                    var cost = Cost(n, c);
                    PushNear(n, c, cost);
                    PushNear(c, n, cost);
                    marked.Add(n);
                    if (before != n.Key)
                    {
                        pending.Add(n);
                        Remove(heap, n);
                    }
                }
                a.Near.Clear();
                for (var k = 0; k < b.Near.Count; k++)
                {
                    var n = b.Near[k].Node;
                    if (n == a)
                        continue;
                    if (n.Slot < 0)
                    {
                        RemoveNear(n, b);
                        continue;
                    }
                    var before = n.Key;
                    RemoveNear(n, b);
                    if (!marked.Contains(n))
                    {
                        var cost = Cost(n, c);
                        PushNear(n, c, cost);
                        PushNear(c, n, cost);
                    }
                    var slot = n.Slot;
                    if (before != n.Key && SiftDown(heap, slot) == slot)
                        SiftUp(heap, slot);
                }
                b.Near.Clear();
                foreach (var n in pending)
                    Add(heap, n);
                Add(heap, c);
            }
            if (heap.Count > 1)
                throw new NotSupportedException("a hull whose faces fall apart into separate clusters is not ported");
        }

        // FUN_1813d3700: merge a node whose two children are live and whose
        // normals lie within the angle, loosened by their sharpness.
        private int MergeFlat(ref int count, bool[] live)
        {
            var merged = 0;
            for (var i = _leaves; i < _nodes.Count; i++)
            {
                if (count < 5)
                    return merged;
                var node = _nodes[i];
                if (!live[node.A] || !live[node.B])
                    continue;
                var a = Record(node.A);
                var b = Record(node.B);
                var dot = (a.Z * b.Z + a.Y * b.Y) + a.X * b.X;
                if (!(0f < dot))
                    continue;
                var c0 = a.Y * b.Z - a.Z * b.Y;
                var c1 = a.X * b.Y - a.Y * b.X;
                var c2 = a.Z * b.X - a.X * b.Z;
                var len = MathF.Sqrt((c1 * c1 + c2 * c2) + c0 * c0);
                var s = a.Sharp > b.Sharp ? a.Sharp : b.Sharp;
                if (len < (_tan / (s * (_tan + 1f))) * dot)
                {
                    live[node.A] = false;
                    live[node.B] = false;
                    live[i] = true;
                    count--;
                    merged++;
                }
            }
            return merged;
        }

        /// <summary>FUN_1813d0aa0: the live clusters' planes after cutting the tree to at most <paramref name="max"/>.</summary>
        public List<(float X, float Y, float Z, float D)> Planes(int max)
        {
            var live = new bool[_nodes.Count];
            for (var i = 0; i < _leaves; i++)
                live[i] = true;
            var count = _leaves;
            MergeFlat(ref count, live);
            if ((uint)max > 3)
            {
                var merged = 0;
                for (var i = _leaves; i < _nodes.Count; i++)
                {
                    if (count <= max)
                        break;
                    var node = _nodes[i];
                    if (!live[node.A] || !live[node.B])
                        continue;
                    live[node.A] = false;
                    live[node.B] = false;
                    live[i] = true;
                    count--;
                    merged++;
                }
                if (merged != 0)
                    MergeFlat(ref count, live);
            }
            var planes = new List<(float, float, float, float)>();
            for (var i = 0; i < _nodes.Count; i++)
            {
                if (!live[i])
                    continue;
                var r = Record(i);
                var len = MathF.Sqrt((r.Z * r.Z + r.Y * r.Y) + r.X * r.X);
                if (!(1e-06f > len))
                    planes.Add((r.X / len, r.Y / len, r.Z / len, r.D / r.Area));
            }
            return planes;
        }
    }

    /// <summary>
    /// The agglomerator's dual mesh, modelled: faces are rings of dual
    /// vertices, and a merge is an edge collapse under the link condition.
    /// Dual vertex handles are numbered as made; each carries its cluster.
    /// </summary>
    private sealed class Dual
    {
        private readonly List<List<int>> _faces = [];
        private readonly List<int> _cluster = [];

        public void AddFace(List<int> ring) => _faces.Add(ring);

        /// <summary>Handles 0 to count - 1 are the leaf clusters themselves.</summary>
        public void Leaves(int count)
        {
            for (var i = 0; i < count; i++)
                _cluster.Add(i);
        }

        private static bool Adjacent(List<int> ring, int a, int b)
        {
            for (var i = 0; i < ring.Count; i++)
            {
                var j = (i + 1) % ring.Count;
                if ((ring[i] == a && ring[j] == b) || (ring[i] == b && ring[j] == a))
                    return true;
            }
            return false;
        }

        private HashSet<int> Neighbours(int h)
        {
            var set = new HashSet<int>();
            foreach (var ring in _faces)
            {
                for (var i = 0; i < ring.Count; i++)
                {
                    if (ring[i] != h)
                        continue;
                    set.Add(ring[(i + 1) % ring.Count]);
                    set.Add(ring[(i + ring.Count - 1) % ring.Count]);
                }
            }
            set.Remove(h);
            return set;
        }

        /// <summary>The clusters next to a dual vertex.</summary>
        public IEnumerable<int> Around(int h) => Neighbours(h).Select(n => _cluster[n]);

        /// <summary>The merged dual vertex, or -1 when the collapse is refused.</summary>
        public int Merge(int a, int b, int cluster)
        {
            if (a < 0 || b < 0)
                return -1;
            var sides = _faces.Where(r => Adjacent(r, a, b)).ToList();
            if (sides.Count == 0)
                return -1;
            var nb = Neighbours(b);
            foreach (var w in Neighbours(a))
            {
                if (nb.Contains(w) && !sides.Any(r => r.Count == 3 && r.Contains(w)))
                    return -1;
            }
            var c = _cluster.Count;
            _cluster.Add(cluster);
            for (var f = _faces.Count - 1; f >= 0; f--)
            {
                var ring = _faces[f];
                for (var i = 0; i < ring.Count; i++)
                {
                    if (ring[i] == a || ring[i] == b)
                        ring[i] = c;
                }
                for (var i = ring.Count - 1; i >= 0 && ring.Count > 1; i--)
                {
                    if (ring[i] == ring[(i + 1) % ring.Count])
                        ring.RemoveAt(i);
                }
                if (ring.Count < 3)
                    _faces.RemoveAt(f);
            }
            return c;
        }
    }
}
