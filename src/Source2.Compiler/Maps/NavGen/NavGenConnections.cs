namespace Source2.Compiler.Maps.NavGen;

/// <summary>GenerateConnections (navsystem 20261015, 18006ea50) and what it needs.</summary>
public sealed partial class NavGenPolys
{
    /// <summary>
    /// 1800802b0: the point of triangle a, b, c nearest p (Ericson's regions: a, b, c, the edges, the
    /// face), in doubles.
    /// </summary>
    public static (double X, double Y, double Z) ClosestOnTriangle((double X, double Y, double Z) p, (double X, double Y, double Z) a,
        (double X, double Y, double Z) b, (double X, double Y, double Z) c)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
        double acx = c.X - a.X, acy = c.Y - a.Y, acz = c.Z - a.Z;
        double apx = p.X - a.X, apy = p.Y - a.Y, apz = p.Z - a.Z;
        var d2 = apy * acy + apx * acx + apz * acz;
        var d1 = apy * aby + apx * abx + apz * abz;
        if (!(0.0 < d1) && !(0.0 < d2))
            return a;
        double bpx = p.X - b.X, bpy = p.Y - b.Y, bpz = p.Z - b.Z;
        var d4 = bpy * acy + bpx * acx + bpz * acz;
        var d3 = bpy * aby + bpx * abx + bpz * abz;
        if (!(d3 < 0.0) && !(d3 < d4))
            return b;
        var vc = d4 * d1 - d3 * d2;
        if (!(0.0 < vc) && !(d1 < 0.0) && !(0.0 < d3))
        {
            var v = d1 / (d1 - d3);
            return (v * abx + a.X, v * aby + a.Y, v * abz + a.Z);
        }
        double cpx = p.X - c.X, cpy = p.Y - c.Y, cpz = p.Z - c.Z;
        var d5 = cpy * aby + cpx * abx + cpz * abz;
        var d6 = cpy * acy + cpx * acx + cpz * acz;
        if (0.0 <= d6 && d5 <= d6)
            return c;
        var vb = d5 * d2 - d6 * d1;
        if (!(0.0 < vb) && !(d2 < 0.0) && !(0.0 < d6))
        {
            var w = d2 / (d2 - d6);
            return (w * acx + a.X, w * acy + a.Y, w * acz + a.Z);
        }
        var va = d6 * d3 - d5 * d4;
        if (va <= 0.0 && 0.0 <= d4 - d3 && 0.0 <= d5 - d6)
        {
            var w = (d4 - d3) / ((d5 - d6) + (d4 - d3));
            return ((c.X - b.X) * w + b.X, (c.Y - b.Y) * w + b.Y, (c.Z - b.Z) * w + b.Z);
        }
        var denom = 1.0 / (va + vb + vc);
        var vv = denom * vb;
        var ww = denom * vc;
        return (vv * abx + a.X + ww * acx, vv * aby + a.Y + ww * acy, vv * abz + a.Z + ww * acz);
    }

    private static double Length((double X, double Y, double Z) v) => Math.Sqrt(v.Y * v.Y + v.X * v.X + v.Z * v.Z);

    /// <summary>
    /// 180077130: each polygon's +0x18 (the largest |corner|, from -FLT_MAX) and +0x10 (|nearest
    /// point of triangle 0-1-2 to the origin|, for a quad the smaller with triangle 0-2-3).
    /// </summary>
    public void ComputeSortKeys()
    {
        var origin = (0.0, 0.0, 0.0);
        foreach (var p in Polys)
        {
            var reach = (double)-float.MaxValue;
            // Corners in the four-wide loop sum x² + y² + z², the rest y² + x² + z².
            var n = p.Indices.Count;
            var wide = n >= 4 ? (((n - 4) >> 2) + 1) * 4 : 0;
            for (var k = 0; k < n; k++)
            {
                var q = Pool[p.Indices[k]];
                var d = k < wide ? Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z) : Math.Sqrt(q.Y * q.Y + q.X * q.X + q.Z * q.Z);
                if (!(d <= reach))
                    reach = d;
            }
            p.Reach = reach;
            var c = p.Indices.Select(i => Pool[i]).ToArray();
            var key = Length(ClosestOnTriangle(origin, c[0], c[1], c[2]));
            if (c.Length == 4)
            {
                var k2 = Length(ClosestOnTriangle(origin, c[0], c[2], c[3]));
                if (!(key <= k2))
                    key = k2;
            }
            p.SortKey = key;
        }
    }

    /// <summary>GenerateConnections' first steps: the keys (180077130), then MSVC std::sort on +0x10 (180067920).</summary>
    public void SortForConnections()
    {
        ComputeSortKeys();
        var arr = Polys.ToArray();
        MsvcSort.Sort(arr, (a, b) => a.SortKey < b.SortKey);
        Polys = [.. arr];
    }

    /// <summary>GenerateConnections' settings (the stage list's convars through its invoker 180027ea0).</summary>
    public sealed record ConnectSettings(double DistA = 1.0, double DistB = 1.5, double ZMult = 0.5, double Angle = 0.75,
        bool IgnoreZ = true, double Overlap = 0.5, bool AllowMultiple = true);

    /// <summary>
    /// GenerateConnections (18006ea50): the sort, the overlapping pairs in order (18006f350 per chunk of
    /// i: j after i until j's key passes i's reach by 2), then each pair's connection both ways
    /// (180053170); a pair joins when both are valid, each polygon's block taking its side (18004c780).
    /// </summary>
    public void GenerateConnections(ConnectSettings s)
    {
        SortForConnections();
        var cos = CosDegrees(s.Angle);
        var pairs = new List<(int P0, int P1, int E0, int E1)>();
        for (var i = 0; i < Polys.Count; i++)
            for (var j = i + 1; j < Polys.Count; j++)
            {
                if (Polys[i].Reach < Polys[j].SortKey - 2.0)
                    break;
                PairEdges(pairs, i, j, s, cos);
            }
        foreach (var p in Polys)
            p.Links = new NavLinks();
        foreach (var (p0, p1, e0, e1) in pairs)
        {
            var c1 = Connect(p0, e0, p1, e1, s.Overlap);
            var c2 = Connect(p1, e1, p0, e0, s.Overlap);
            if (c1.OverlapA != 0.0 && c2.OverlapA != 0.0)
            {
                Polys[p0].Links!.Add(c1);
                Polys[p1].Links!.Add(c2);
            }
        }
    }

    private (double X, double Y, double Z) Corner(NavPoly p, int k) => Pool[p.Indices[k % p.Indices.Count]];

    // 18006d260: no edge normal of either polygon (XY, corners with z 0) separates them by more than margin (18006d550).
    private bool OverlapXY(NavPoly a, NavPoly b, double margin)
    {
        foreach (var p in new[] { a, b })
        {
            for (var k = 0; k < p.Indices.Count; k++)
            {
                var c0 = Corner(p, k);
                var c1 = Corner(p, k + 1);
                var ey = c1.Y - c0.Y;
                var ex = c1.X - c0.X;
                double nx = -ey, ny = ex, nz = 0.0;
                var len = Math.Sqrt(ex * ex + -ey * -ey + 0.0);
                if (1.1920928955078125e-07 < len)
                {
                    var inv = 1.0 / len;
                    ny = ex * inv;
                    nz = inv * 0.0;
                    nx = inv * -ey;
                }
                if (!ProjectionsMeet(a, b, nx, ny, nz, margin))
                    return false;
            }
        }
        return true;
    }

    private bool ProjectionsMeet(NavPoly a, NavPoly b, double nx, double ny, double nz, double margin)
    {
        (double Min, double Max) Range(NavPoly p)
        {
            var c = Corner(p, 0);
            var first = c.Y * ny + nx * c.X + 0.0 * nz;
            double min = first, max = first;
            for (var k = 1; k < p.Indices.Count; k++)
            {
                c = Corner(p, k);
                var d = c.Y * ny + c.X * nx + 0.0 * nz;
                if (!(d <= max))
                    max = d;
                if (!(min <= d))
                    min = d;
            }
            return (min, max);
        }
        var (aMin, aMax) = Range(a);
        var (bMin, bMax) = Range(b);
        return bMin - margin <= aMax && aMin <= bMax + margin;
    }

    // 1800369e0: the closest points of segments p1-p2 and p3-p4 (Sunday's clamped parameters).
    private static ((double X, double Y, double Z) A, (double X, double Y, double Z) B) ClosestOnSegments(
        (double X, double Y, double Z) p1, (double X, double Y, double Z) p2, (double X, double Y, double Z) p3, (double X, double Y, double Z) p4)
    {
        var vz = p4.Z - p3.Z;
        double ux = p2.X - p1.X, uy = p2.Y - p1.Y, uz = p2.Z - p1.Z;
        double vx = p4.X - p3.X, vy = p4.Y - p3.Y;
        var wz = p1.Z - p3.Z;
        var a = uy * uy + ux * ux + uz * uz;
        var b = vy * uy + vx * ux + vz * uz;
        var c = vy * vy + vx * vx + vz * vz;
        var d = (p1.Y - p3.Y) * uy + (p1.X - p3.X) * ux + wz * uz;
        var e = (p1.Y - p3.Y) * vy + (p1.X - p3.X) * vx + wz * vz;
        var tc = 0.0;
        var den = c * a - b * b;
        var tN = e;
        var tD = c;
        double sN, sD;
        if (1.1920928955078125e-05 < den)
        {
            sN = e * b - d * c;
            if (sN < 0.0)
                sN = 0.0;
            else
            {
                tN = e * a - d * b;
                tD = den;
                if (den < sN)
                {
                    tN = e + b;
                    tD = c;
                    sN = den;
                }
            }
        }
        else
        {
            den = 1.0;
            sN = 0.0;
        }
        sD = den;
        var done = false;
        if (0.0 <= tN)
        {
            if (tN <= tD)
                done = true;
            else
            {
                sN = b - d;
                tN = tD;
            }
        }
        else
        {
            sN = -d;
            tN = 0.0;
        }
        if (!done)
        {
            if (0.0 <= sN)
            {
                sD = a;
                if (a < sN)
                {
                    sD = den;
                    sN = den;
                }
            }
            else
                sN = 0.0;
        }
        var sc = 1.1920928955078125e-07 <= Math.Abs(sN) ? sN / sD : 0.0;
        if (1.1920928955078125e-07 <= Math.Abs(tN))
            tc = tN / tD;
        var bz = vz * tc + p3.Z;
        var ax = ux * sc + p1.X;
        var ay = uy * sc + p1.Y;
        var az = uz * sc + p1.Z;
        var bx = vx * tc + p3.X;
        var by = vy * tc + p3.Y;
        return ((ax, ay, az), (bx, by, bz));
    }

    // 18006d750 (from the disassembly; Ghidra drops its tail): how far edge a runs along edge b, 0 when
    // not opposite enough, apart, or off in height.
    private static double EdgeOverlap((double X, double Y, double Z) a0, (double X, double Y, double Z) a1,
        (double X, double Y, double Z) b0, (double X, double Y, double Z) b1, double distA, double distB, double zMult, double cos, bool flat)
    {
        double daz = a1.Z - a0.Z, dbx = b1.X - b0.X, dax = a1.X - a0.X, dby = b1.Y - b0.Y, dbz = b1.Z - b0.Z, day = a1.Y - a0.Y;
        if (!(dby * day + dbx * dax + dbz * daz < 0.0))
            return 0.0;
        var a2xy = day * day + dax * dax;
        var lenA = Math.Sqrt(daz * daz + a2xy);
        double uax = dax, uay = day, uaz = daz;
        if (lenA > 1.1920928955078125e-07)
        {
            var inv = 1.0 / lenA;
            uax = dax * inv;
            uay = day * inv;
            uaz = daz * inv;
        }
        var b2xy = dby * dby + dbx * dbx;
        var lenB = Math.Sqrt(dbz * dbz + b2xy);
        double ubx = dbx, uby = dby, ubz = dbz;
        if (lenB > 1.1920928955078125e-07)
        {
            var inv = 1.0 / lenB;
            ubx = dbx * inv;
            uby = dby * inv;
            ubz = dbz * inv;
        }
        double c;
        if (flat)
        {
            double ax2 = dax, ay2 = day;
            var la = Math.Sqrt(a2xy);
            if (la > 1.1920928955078125e-07)
            {
                var inv = 1.0 / la;
                ax2 = dax * inv;
                ay2 = day * inv;
            }
            double bx2 = dbx, by2 = dby;
            var lb = Math.Sqrt(b2xy);
            if (lb > 1.1920928955078125e-07)
            {
                var inv = 1.0 / lb;
                bx2 = dbx * inv;
                by2 = dby * inv;
            }
            c = -by2 * ay2 - ax2 * bx2;
        }
        else
            c = (-uby * uay - uax * ubx) - uaz * ubz;
        if (cos > Math.Abs(c))
            return 0.0;
        var (pa, pb) = ClosestOnSegments(a0, a1, b0, b1);
        double cy = pa.Y - pb.Y, cx = pa.X - pb.X, cz = (pa.Z - pb.Z) * zMult;
        if (Math.Sqrt(cy * cy + cx * cx + cz * cz) > distA)
            return 0.0;
        var t0 = (b0.Y - a0.Y) * uay + (b0.X - a0.X) * uax + (b0.Z - a0.Z) * uaz;
        var t1 = (b1.Y - a0.Y) * uay + (b1.X - a0.X) * uax + (b1.Z - a0.Z) * uaz;
        double lo = t0, hi = t1;
        if (t0 > t1)
        {
            lo = t1;
            hi = t0;
        }
        lo = lo > 0.001 ? lo : 0.001;
        var top = lenA - 0.001;
        hi = hi < top ? hi : top;
        double lox = uax * lo + a0.X, loy = uay * lo + a0.Y, loz = uaz * lo + a0.Z;
        double hix = uax * hi + a0.X, hiy = uay * hi + a0.Y, hiz = uaz * hi + a0.Z;
        var lenB2 = dbx * dbx + dby * dby + dbz * dbz;
        var bDot0 = b0.X * dbx + b0.Y * dby + dbz * b0.Z;
        double Param(double px, double py, double pz) =>
            1.1920928955078125e-07 > lenB2 ? 0.0 : (dbx * px + py * dby + dbz * pz - bDot0) / lenB2;
        static double Clamp01(double t) => 0.0 > t ? 0.0 : (1.0 < t ? 1.0 : t);
        var tlo = Clamp01(Param(lox, loy, loz));
        double qlz = dbz * tlo + b0.Z, qly = tlo * dby + b0.Y, qlx = dbx * tlo + b0.X;
        var thi = Clamp01(Param(hix, hiy, hiz));
        double qhx = dbx * thi + b0.X, qhz = dbz * thi + b0.Z, qhy = thi * dby + b0.Y;
        var r2 = distB * distB;
        var dzl = Math.Abs(loz - qlz) - 6.0;
        dzl = 0.0 > dzl ? 0.0 : dzl;
        double dyl = loy - qly, dxl = lox - qlx;
        if (dyl * dyl + dxl * dxl + dzl * dzl > r2)
            return 0.0;
        var dzh = Math.Abs(hiz - qhz) - 6.0;
        dzh = 0.0 > dzh ? 0.0 : dzh;
        double dyh = hiy - qhy, dxh = hix - qhx;
        if (dyh * dyh + dxh * dxh + dzh * dzh > r2)
            return 0.0;
        var len = hi - lo;
        return 0.0 > len ? 0.0 : len;
    }

    private static (double X, double Y, double Z) Unit3((double X, double Y, double Z) v)
    {
        var len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        if (!(1.1920928955078125e-07 < len))
            return v;
        var inv = 1.0 / len;
        return (v.X * inv, v.Y * inv, v.Z * inv);
    }

    // 18006a6c0: the edge pairs of polygons i and j that overlap (18006d750); the best, and with
    // AllowMultiple a second when both run at least 1 and their edges are adjacent and within 60
    // degrees on one polygon (180077760 directions), recorded through 18006aec0.
    private void PairEdges(List<(int, int, int, int)> pairs, int i, int j, ConnectSettings s, double cos)
    {
        var a = Polys[i];
        var b = Polys[j];
        if (s.IgnoreZ && !OverlapXY(a, b, s.DistA))
            return;
        var found = new List<(int Ea, int Eb, double D)>();
        for (var ea = 0; ea < a.Indices.Count; ea++)
            for (var eb = 0; eb < b.Indices.Count; eb++)
            {
                var d = EdgeOverlap(Corner(a, ea), Corner(a, ea + 1), Corner(b, eb), Corner(b, eb + 1), s.DistA, s.DistB, s.ZMult, cos, s.IgnoreZ);
                if (0.0 < d)
                    found.Add((ea, eb, d));
            }
        int Best(int skip)
        {
            var at = -1;
            var top = (double)-float.MaxValue;
            for (var k = 0; k < found.Count; k++)
                if (k != skip && top < found[k].D)
                {
                    top = found[k].D;
                    at = k;
                }
            return at;
        }
        void Single()
        {
            var k = Best(-1);
            if (k >= 0)
                Record(pairs, i, j, found[k].Ea, found[k].Eb);
        }
        if (!s.AllowMultiple)
        {
            Single();
            return;
        }
        var first = Best(-1);
        var second = Best(first);
        if (first < 0 || second < 0 || found[first].D < 1.0 || found[second].D < 1.0)
        {
            Single();
            return;
        }
        var f = found[first];
        var g = found[second];
        var adjA = (f.Ea + 1) % a.Indices.Count == g.Ea || (g.Ea + 1) % a.Indices.Count == f.Ea;
        var adjB = (f.Eb + 1) % b.Indices.Count == g.Eb || (g.Eb + 1) % b.Indices.Count == f.Eb;
        (double X, double Y, double Z) Dir(NavPoly p, int e)
        {
            var c0 = Corner(p, e);
            var c1 = Corner(p, e + 1);
            return Unit3((c1.X - c0.X, c1.Y - c0.Y, c1.Z - c0.Z));
        }
        double dot;
        if (adjA)
        {
            var u = Dir(a, f.Ea);
            var w = Dir(a, g.Ea);
            dot = w.Y * u.Y + w.X * u.X + w.Z * u.Z;
        }
        else if (adjB)
        {
            var u = Dir(b, f.Eb);
            var w = Dir(b, g.Eb);
            dot = w.Y * u.Y + w.X * u.X + w.Z * u.Z;
        }
        else
        {
            Single();
            return;
        }
        if (!(0.5 < dot))
        {
            Single();
            return;
        }
        Record(pairs, i, j, f.Ea, f.Eb);
        Record(pairs, i, j, g.Ea, g.Eb);
    }

    // 18006aec0: the pair with the shorter edge's polygon first (b's when not longer).
    private void Record(List<(int, int, int, int)> pairs, int i, int j, int ea, int eb)
    {
        var a0 = Corner(Polys[i], ea);
        var a1 = Corner(Polys[i], ea + 1);
        var b0 = Corner(Polys[j], eb);
        var b1 = Corner(Polys[j], eb + 1);
        var lenA = Math.Sqrt((a1.X - a0.X) * (a1.X - a0.X) + (a1.Y - a0.Y) * (a1.Y - a0.Y) + (a1.Z - a0.Z) * (a1.Z - a0.Z));
        var lenB = Math.Sqrt((b1.Y - b0.Y) * (b1.Y - b0.Y) + (b1.X - b0.X) * (b1.X - b0.X) + (b1.Z - b0.Z) * (b1.Z - b0.Z));
        pairs.Add(lenB <= lenA ? (j, i, eb, ea) : (i, j, ea, eb));
    }

    // 1800787d0: q0-q1 projected onto p0-p1 in XY, clamped to it, as parameters lo <= hi and their points.
    private static (float Lo, float Hi, (float X, float Y, float Z) Pl, (float X, float Y, float Z) Ph) ProjectXY(
        (float X, float Y, float Z) p0, (float X, float Y, float Z) p1, (float X, float Y, float Z) q0, (float X, float Y, float Z) q1)
    {
        float dx = p1.X - p0.X, dy = p1.Y - p0.Y;
        var inv = 0f;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        float ux, uy;
        if (len <= 1.1920929e-07f)
        {
            ux = 1f;
            uy = 0f;
        }
        else
        {
            inv = 1f / len;
            ux = dx * inv;
            uy = dy * inv;
        }
        var t0 = (q0.X - p0.X) * ux + (q0.Y - p0.Y) * uy;
        var t1 = (q1.Y - p0.Y) * uy + (q1.X - p0.X) * ux;
        if (t0 <= 0f) t0 = 0f;
        if (t1 <= 0f) t1 = 0f;
        if (len <= t0) t0 = len;
        if (len <= t1) t1 = len;
        t0 *= inv;
        t1 *= inv;
        float lo = t0, hi = t1;
        if (t1 < t0)
        {
            hi = t0;
            lo = t1;
        }
        var pl = ((p1.X - p0.X) * lo + p0.X, (p1.Y - p0.Y) * lo + p0.Y, (p1.Z - p0.Z) * lo + p0.Z);
        var ph = ((p1.X - p0.X) * hi + p0.X, (p1.Y - p0.Y) * hi + p0.Y, (p1.Z - p0.Z) * hi + p0.Z);
        return (lo, hi, pl, ph);
    }

    // 180053170 (map input: no vertex attributes): edge ea of a against edge eb of b, opposite within
    // 0.001; each projected on the other (floats); both overlaps at least `overlap`; midpoints within 1
    // in XY or b's to the left of a.
    private NavConnection Connect(int pa, int ea, int pb, int eb, double overlap)
    {
        var c = new NavConnection { PolyA = pa, EdgeA = ea, PolyB = pb, EdgeB = eb };
        var a0 = Corner(Polys[pa], ea);
        var a1 = Corner(Polys[pa], ea + 1);
        var b0 = Corner(Polys[pb], eb);
        var b1 = Corner(Polys[pb], eb + 1);
        double dax = a1.X - a0.X, day = a1.Y - a0.Y, daz = a1.Z - a0.Z;
        double dbx = b1.X - b0.X, dby = b1.Y - b0.Y, dbz = b1.Z - b0.Z;
        var lenA = Math.Sqrt(day * day + dax * dax + daz * daz);
        double uax = dax, uay = day, uaz = daz;
        if (1.1920928955078125e-07 < lenA)
        {
            var inv = 1.0 / lenA;
            uax = dax * inv;
            uay = day * inv;
            uaz = daz * inv;
        }
        var lenB = Math.Sqrt(dby * dby + dbx * dbx + dbz * dbz);
        double ubx = dbx, uby = dby, ubz = dbz;
        if (1.1920928955078125e-07 < lenB)
        {
            var inv = 1.0 / lenB;
            ubx = dbx * inv;
            uby = dby * inv;
            ubz = dbz * inv;
        }
        if (-0.001 < uax * ubx + uay * uby + uaz * ubz)
            return c;
        static (float, float, float) F((double X, double Y, double Z) v) => ((float)v.X, (float)v.Y, (float)v.Z);
        var (lo, _, pl, ph) = ProjectXY(F(a0), F(a1), F(b0), F(b1));
        var dist = (double)MathF.Sqrt((ph.Z - pl.Z) * (ph.Z - pl.Z) + (ph.Y - pl.Y) * (ph.Y - pl.Y) + (ph.X - pl.X) * (ph.X - pl.X));
        if (!(overlap <= dist))
            return c;
        var la = Math.Sqrt(day * day + dax * dax + daz * daz);
        c.OverlapA = dist;
        c.StartA = la * lo;
        var mid = dist * 0.5 + la * lo;
        c.MidA = (mid * uax + a0.X, mid * uay + a0.Y, mid * uaz + a0.Z);
        var (lo2, _, ql, qh) = ProjectXY(F(b0), F(b1), F(a0), F(a1));
        var dist2 = (double)MathF.Sqrt((qh.Y - ql.Y) * (qh.Y - ql.Y) + (qh.Z - ql.Z) * (qh.Z - ql.Z) + (qh.X - ql.X) * (qh.X - ql.X));
        if (overlap <= dist2)
        {
            var lb = Math.Sqrt(dbx * dbx + dby * dby + dbz * dbz);
            c.OverlapB = dist2;
            c.StartB = lb * lo2;
            var mid2 = dist2 * 0.5 + lb * lo2;
            c.MidB = (mid2 * ubx + b0.X, mid2 * uby + b0.Y, mid2 * ubz + b0.Z);
            var dx = c.MidB.X - c.MidA.X;
            var dy = c.MidB.Y - c.MidA.Y;
            if (Math.Sqrt(dy * dy + dx * dx + 0.0) <= 1.0)
                return c;
            if (0.0 <= dx * uay - dy * uax)
                return c;
        }
        c.OverlapA = 0.0;
        c.OverlapB = 0.0;
        return c;
    }
}

/// <summary>A connection record (0x68 bytes): this polygon's edge against the other's, the overlap on each side, where it starts and its midpoint.</summary>
public sealed class NavConnection
{
    public int PolyA, EdgeA, PolyB, EdgeB;
    public double OverlapA, StartA, OverlapB, StartB;
    public (double X, double Y, double Z) MidA, MidB;
}

/// <summary>
/// A polygon's connection block (0x4a8 bytes at +0x80, 18004c780): the connections, each one's slot in
/// its edge's list (+0xd6 ints), and the per-edge lists of connection indices (+0xe4 + 0xe ints an edge).
/// </summary>
public sealed class NavLinks
{
    public List<NavConnection> Connections = [];
    public List<int> Slots = [];
    public List<int>[] PerEdge = [[], [], [], [], []];

    public int Add(NavConnection c)
    {
        var at = Connections.Count;
        Connections.Add(c);
        var list = PerEdge[c.EdgeA];
        Slots.Add(list.Count);
        list.Add(at);
        return at;
    }
}
