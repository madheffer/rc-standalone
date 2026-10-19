namespace Source2.Compiler.Maps.NavGen;

/// <summary>A region outline (rcContour): x, y, z and flags per vertex, raw and simplified.</summary>
public sealed class RecastContour
{
    public int[] Verts = [];
    public int NVerts;
    public int[] RVerts = [];
    public int NRVerts;
    public ushort Reg;
    public byte Area;
}

/// <summary>
/// rcBuildContours (1800b2a10) with walkContour 1800b4930, getCornerHeight 1800b1ba0,
/// simplifyContour 1800b3880, removeDegenerateSegments 1800b3700 and mergeRegionHoles 1800b20f0
/// (intersect 1800b1ec0, CRT qsort with compareHoles 1800b1b60 and compareDiagDist 1800b1b40):
/// Recast 1.5's, the diags[i] slip in the hole merge included.
/// </summary>
public sealed class RecastContourSet
{
    public const int BorderVertex = 0x10000, AreaBorder = 0x20000, RegMask = 0xffff;

    public List<RecastContour> Contours = [];
    public float[] BMin = new float[3], BMax = new float[3];
    public float Cs, Ch, MaxError;
    public int Width, Height, BorderSize;

    public static RecastContourSet Build(RecastCompact chf, float maxError, int maxEdgeLen, int buildFlags)
    {
        var w = chf.Width;
        var h = chf.Height;
        var borderSize = chf.BorderSize;
        var cset = new RecastContourSet
        {
            BMin = [chf.BMin[0], chf.BMin[1], chf.BMin[2]],
            BMax = [chf.BMax[0], chf.BMax[1], chf.BMax[2]],
        };
        if (borderSize > 0)
        {
            var pad = (float)borderSize * chf.Cs;
            cset.BMin[0] += pad;
            cset.BMin[2] += pad;
            cset.BMax[0] -= pad;
            cset.BMax[2] -= pad;
        }
        cset.Cs = chf.Cs;
        cset.Ch = chf.Ch;
        cset.Width = chf.Width - borderSize * 2;
        cset.Height = chf.Height - borderSize * 2;
        cset.BorderSize = borderSize;
        cset.MaxError = maxError;

        var flags = new byte[chf.SpanCount];
        for (var z = 0; z < h; z++)
        {
            for (var x = 0; x < w; x++)
            {
                var cell = x + z * w;
                for (int i = chf.CellIndex(cell), ni = i + chf.CellCount(cell); i < ni; i++)
                {
                    byte res = 0;
                    var reg = chf.Reg[i];
                    if (reg != 0 && (reg & RecastCompact.BorderReg) == 0)
                    {
                        for (var dir = 0; dir < 4; dir++)
                        {
                            ushort r = 0;
                            if (chf.GetCon(i, dir) != RecastCompact.NotConnected)
                                r = chf.Reg[chf.Neighbour(x, z, i, dir)];
                            if (r == reg)
                                res |= (byte)(1 << dir);
                        }
                        res ^= 0xf;
                    }
                    flags[i] = res;
                }
            }
        }

        var verts = new List<int>(256);
        var simplified = new List<int>(64);
        for (var z = 0; z < h; z++)
        {
            for (var x = 0; x < w; x++)
            {
                var cell = x + z * w;
                for (int i = chf.CellIndex(cell), ni = i + chf.CellCount(cell); i < ni; i++)
                {
                    if (flags[i] == 0 || flags[i] == 0xf)
                    {
                        flags[i] = 0;
                        continue;
                    }
                    var reg = chf.Reg[i];
                    if (reg == 0 || (reg & RecastCompact.BorderReg) != 0)
                        continue;
                    var area = chf.Areas[i];
                    verts.Clear();
                    simplified.Clear();
                    WalkContour(x, z, i, chf, flags, verts);
                    SimplifyContour(verts, simplified, maxError, maxEdgeLen, buildFlags);
                    RemoveDegenerateSegments(simplified);
                    if (simplified.Count / 4 < 3)
                        continue;
                    var c = new RecastContour
                    {
                        NVerts = simplified.Count / 4,
                        Verts = [.. simplified],
                        NRVerts = verts.Count / 4,
                        RVerts = [.. verts],
                        Reg = reg,
                        Area = area,
                    };
                    if (borderSize > 0)
                    {
                        for (var j = 0; j < c.NVerts; j++)
                        {
                            c.Verts[j * 4] -= borderSize;
                            c.Verts[j * 4 + 2] -= borderSize;
                        }
                        for (var j = 0; j < c.NRVerts; j++)
                        {
                            c.RVerts[j * 4] -= borderSize;
                            c.RVerts[j * 4 + 2] -= borderSize;
                        }
                    }
                    cset.Contours.Add(c);
                }
            }
        }

        if (cset.Contours.Count > 0)
        {
            var winding = new sbyte[cset.Contours.Count];
            var nholes = 0;
            for (var i = 0; i < cset.Contours.Count; i++)
            {
                var c = cset.Contours[i];
                winding[i] = (sbyte)(AreaOfPolygon2D(c.Verts, c.NVerts) < 0 ? -1 : 1);
                if (winding[i] < 0)
                    nholes++;
            }
            if (nholes > 0)
            {
                var nregions = chf.MaxRegions + 1;
                var outlines = new RecastContour?[nregions];
                var holes = new List<RecastContour>[nregions];
                for (var i = 0; i < nregions; i++)
                    holes[i] = [];
                for (var i = 0; i < cset.Contours.Count; i++)
                {
                    var c = cset.Contours[i];
                    if (winding[i] > 0)
                        outlines[c.Reg] = c;
                    else
                        holes[c.Reg].Add(c);
                }
                for (var i = 0; i < nregions; i++)
                    if (holes[i].Count > 0 && outlines[i] is { } outline)
                        MergeRegionHoles(outline, holes[i]);
            }
        }
        return cset;
    }

    private static int AreaOfPolygon2D(int[] verts, int nverts)
    {
        var area = 0;
        for (int i = 0, j = nverts - 1; i < nverts; j = i++)
            area += verts[j * 4 + 2] * verts[i * 4] - verts[i * 4 + 2] * verts[j * 4];
        return (area + 1) / 2;
    }

    // 1800b1ba0.
    private static int GetCornerHeight(int x, int z, int i, int dir, RecastCompact chf, out bool isBorderVertex)
    {
        isBorderVertex = false;
        int ch = chf.Y[i];
        var dirp = (dir + 1) & 3;
        Span<uint> regs = stackalloc uint[4];
        regs[0] = chf.Reg[i] | ((uint)chf.Areas[i] << 16);
        if (chf.GetCon(i, dir) != RecastCompact.NotConnected)
        {
            int ax = x + RecastCompact.DirX[dir], az = z + RecastCompact.DirZ[dir];
            var ai = chf.Neighbour(x, z, i, dir);
            ch = Math.Max(ch, chf.Y[ai]);
            regs[1] = chf.Reg[ai] | ((uint)chf.Areas[ai] << 16);
            if (chf.GetCon(ai, dirp) != RecastCompact.NotConnected)
            {
                var ai2 = chf.Neighbour(ax, az, ai, dirp);
                ch = Math.Max(ch, chf.Y[ai2]);
                regs[2] = chf.Reg[ai2] | ((uint)chf.Areas[ai2] << 16);
            }
        }
        if (chf.GetCon(i, dirp) != RecastCompact.NotConnected)
        {
            int ax = x + RecastCompact.DirX[dirp], az = z + RecastCompact.DirZ[dirp];
            var ai = chf.Neighbour(x, z, i, dirp);
            ch = Math.Max(ch, chf.Y[ai]);
            regs[3] = chf.Reg[ai] | ((uint)chf.Areas[ai] << 16);
            if (chf.GetCon(ai, dir) != RecastCompact.NotConnected)
            {
                var ai2 = chf.Neighbour(ax, az, ai, dir);
                ch = Math.Max(ch, chf.Y[ai2]);
                regs[2] = chf.Reg[ai2] | ((uint)chf.Areas[ai2] << 16);
            }
        }
        for (var j = 0; j < 4; j++)
        {
            uint a = regs[j], b = regs[(j + 1) & 3], c = regs[(j + 2) & 3], d = regs[(j + 3) & 3];
            var twoSameExts = (a & b & RecastCompact.BorderReg) != 0 && a == b;
            var twoInts = ((c | d) & RecastCompact.BorderReg) == 0;
            var intsSameArea = c >> 16 == d >> 16;
            var noZeros = a != 0 && b != 0 && c != 0 && d != 0;
            if (twoSameExts && twoInts && intsSameArea && noZeros)
            {
                isBorderVertex = true;
                break;
            }
        }
        return ch;
    }

    // 1800b4930.
    private static void WalkContour(int x, int z, int i, RecastCompact chf, byte[] flags, List<int> points)
    {
        var dir = 0;
        while ((flags[i] & (1 << dir)) == 0)
            dir++;
        var startDir = dir;
        var starti = i;
        var area = chf.Areas[i];
        var iter = 0;
        while (++iter < 40000)
        {
            if ((flags[i] & (1 << dir)) != 0)
            {
                var isAreaBorder = false;
                var px = x;
                var py = GetCornerHeight(x, z, i, dir, chf, out var isBorderVertex);
                var pz = z;
                switch (dir)
                {
                    case 0: pz++; break;
                    case 1: px++; pz++; break;
                    case 2: px++; break;
                }
                var r = 0;
                if (chf.GetCon(i, dir) != RecastCompact.NotConnected)
                {
                    var ai = chf.Neighbour(x, z, i, dir);
                    r = chf.Reg[ai];
                    if (area != chf.Areas[ai])
                        isAreaBorder = true;
                }
                if (isBorderVertex)
                    r |= BorderVertex;
                if (isAreaBorder)
                    r |= AreaBorder;
                points.Add(px);
                points.Add(py);
                points.Add(pz);
                points.Add(r);
                flags[i] &= (byte)~(1 << dir);
                dir = (dir + 1) & 3;
            }
            else
            {
                if (chf.GetCon(i, dir) == RecastCompact.NotConnected)
                    return;
                var ni = chf.Neighbour(x, z, i, dir);
                x += RecastCompact.DirX[dir];
                z += RecastCompact.DirZ[dir];
                i = ni;
                dir = (dir + 3) & 3;
            }
            if (starti == i && startDir == dir)
                break;
        }
    }

    private static float DistancePtSeg(int x, int z, int px, int pz, int qx, int qz)
    {
        var pqx = (float)(qx - px);
        var pqz = (float)(qz - pz);
        var dx = (float)(x - px);
        var dz = (float)(z - pz);
        var d = pqx * pqx + pqz * pqz;
        var t = pqx * dx + pqz * dz;
        if (d > 0)
            t /= d;
        if (t < 0)
            t = 0;
        else if (t > 1)
            t = 1;
        dx = (float)px + t * pqx - (float)x;
        dz = (float)pz + t * pqz - (float)z;
        return dx * dx + dz * dz;
    }

    private static void InsertAfter(List<int> simplified, int i, List<int> points, int pi)
    {
        simplified.InsertRange((i + 1) * 4, [points[pi * 4], points[pi * 4 + 1], points[pi * 4 + 2], pi]);
    }

    // 1800b3880.
    private static void SimplifyContour(List<int> points, List<int> simplified, float maxError, int maxEdgeLen, int buildFlags)
    {
        var hasConnections = false;
        for (var i = 0; i < points.Count; i += 4)
        {
            if ((points[i + 3] & RegMask) != 0)
            {
                hasConnections = true;
                break;
            }
        }
        if (hasConnections)
        {
            for (int i = 0, ni = points.Count / 4; i < ni; i++)
            {
                var ii = (i + 1) % ni;
                var differentRegs = (points[i * 4 + 3] & RegMask) != (points[ii * 4 + 3] & RegMask);
                var areaBorders = (points[i * 4 + 3] & AreaBorder) != (points[ii * 4 + 3] & AreaBorder);
                if (differentRegs || areaBorders)
                {
                    simplified.Add(points[i * 4]);
                    simplified.Add(points[i * 4 + 1]);
                    simplified.Add(points[i * 4 + 2]);
                    simplified.Add(i);
                }
            }
        }
        if (simplified.Count == 0)
        {
            int llx = points[0], lly = points[1], llz = points[2], lli = 0;
            int urx = points[0], ury = points[1], urz = points[2], uri = 0;
            for (var i = 0; i < points.Count; i += 4)
            {
                int x = points[i], y = points[i + 1], z = points[i + 2];
                if (x < llx || (x == llx && z < llz))
                {
                    llx = x;
                    lly = y;
                    llz = z;
                    lli = i / 4;
                }
                if (x > urx || (x == urx && z > urz))
                {
                    urx = x;
                    ury = y;
                    urz = z;
                    uri = i / 4;
                }
            }
            simplified.AddRange([llx, lly, llz, lli, urx, ury, urz, uri]);
        }
        var pn = points.Count / 4;
        for (var i = 0; i < simplified.Count / 4;)
        {
            var ii = (i + 1) % (simplified.Count / 4);
            int ax = simplified[i * 4], az = simplified[i * 4 + 2], ai = simplified[i * 4 + 3];
            int bx = simplified[ii * 4], bz = simplified[ii * 4 + 2], bi = simplified[ii * 4 + 3];
            var maxd = 0f;
            var maxi = -1;
            int ci, cinc, endi;
            if (bx > ax || (bx == ax && bz > az))
            {
                cinc = 1;
                ci = (ai + cinc) % pn;
                endi = bi;
            }
            else
            {
                cinc = pn - 1;
                ci = (bi + cinc) % pn;
                endi = ai;
                (ax, bx) = (bx, ax);
                (az, bz) = (bz, az);
            }
            if ((points[ci * 4 + 3] & RegMask) == 0 || (points[ci * 4 + 3] & AreaBorder) != 0)
            {
                while (ci != endi)
                {
                    var d = DistancePtSeg(points[ci * 4], points[ci * 4 + 2], ax, az, bx, bz);
                    if (d > maxd)
                    {
                        maxd = d;
                        maxi = ci;
                    }
                    ci = (ci + cinc) % pn;
                }
            }
            if (maxi != -1 && maxd > maxError * maxError)
                InsertAfter(simplified, i, points, maxi);
            else
                i++;
        }
        if (maxEdgeLen > 0 && (buildFlags & 3) != 0)
        {
            for (var i = 0; i < simplified.Count / 4;)
            {
                var ii = (i + 1) % (simplified.Count / 4);
                int ax = simplified[i * 4], az = simplified[i * 4 + 2], ai = simplified[i * 4 + 3];
                int bx = simplified[ii * 4], bz = simplified[ii * 4 + 2], bi = simplified[ii * 4 + 3];
                var maxi = -1;
                var ci = (ai + 1) % pn;
                var tess = false;
                if ((buildFlags & 1) != 0 && (points[ci * 4 + 3] & RegMask) == 0)
                    tess = true;
                if ((buildFlags & 2) != 0 && (points[ci * 4 + 3] & AreaBorder) != 0)
                    tess = true;
                if (tess)
                {
                    int dx = bx - ax, dz = bz - az;
                    if (dx * dx + dz * dz > maxEdgeLen * maxEdgeLen)
                    {
                        var n = bi < ai ? bi + pn - ai : bi - ai;
                        if (n > 1)
                        {
                            if (bx > ax || (bx == ax && bz > az))
                                maxi = (ai + n / 2) % pn;
                            else
                                maxi = (ai + (n + 1) / 2) % pn;
                        }
                    }
                }
                if (maxi != -1)
                    InsertAfter(simplified, i, points, maxi);
                else
                    i++;
            }
        }
        for (var i = 0; i < simplified.Count / 4; i++)
        {
            var ai = (simplified[i * 4 + 3] + 1) % pn;
            var bi = simplified[i * 4 + 3];
            simplified[i * 4 + 3] = (points[ai * 4 + 3] & (RegMask | AreaBorder)) | (points[bi * 4 + 3] & BorderVertex);
        }
    }

    // 1800b3700.
    private static void RemoveDegenerateSegments(List<int> simplified)
    {
        var npts = simplified.Count / 4;
        for (var i = 0; i < npts; i++)
        {
            var ni = i + 1 < npts ? i + 1 : 0;
            if (simplified[i * 4] == simplified[ni * 4] && simplified[i * 4 + 2] == simplified[ni * 4 + 2])
            {
                simplified.RemoveRange(i * 4, 4);
                npts--;
            }
        }
    }

    private static int Area2(int[] v, int a, int b, int c)
        => (v[b] - v[a]) * (v[c + 2] - v[a + 2]) - (v[c] - v[a]) * (v[b + 2] - v[a + 2]);

    private static int Area2(int ax, int az, int bx, int bz, int cx, int cz) => (bx - ax) * (cz - az) - (cx - ax) * (bz - az);

    private static bool Collinear(int ax, int az, int bx, int bz, int cx, int cz) => Area2(ax, az, bx, bz, cx, cz) == 0;

    private static bool Left(int ax, int az, int bx, int bz, int cx, int cz) => Area2(ax, az, bx, bz, cx, cz) < 0;

    private static bool LeftOn(int ax, int az, int bx, int bz, int cx, int cz) => Area2(ax, az, bx, bz, cx, cz) <= 0;

    private static bool Between(int ax, int az, int bx, int bz, int cx, int cz)
    {
        if (!Collinear(ax, az, bx, bz, cx, cz))
            return false;
        if (ax != bx)
            return (ax <= cx && cx <= bx) || (ax >= cx && cx >= bx);
        return (az <= cz && cz <= bz) || (az >= cz && cz >= bz);
    }

    private static bool Intersect(int ax, int az, int bx, int bz, int cx, int cz, int dx, int dz)
    {
        if (!(Collinear(ax, az, bx, bz, cx, cz) || Collinear(ax, az, bx, bz, dx, dz) || Collinear(cx, cz, dx, dz, ax, az) || Collinear(cx, cz, dx, dz, bx, bz))
            && (Left(ax, az, bx, bz, cx, cz) ^ Left(ax, az, bx, bz, dx, dz)) && (Left(cx, cz, dx, dz, ax, az) ^ Left(cx, cz, dx, dz, bx, bz)))
            return true;
        return Between(ax, az, bx, bz, cx, cz) || Between(ax, az, bx, bz, dx, dz) || Between(cx, cz, dx, dz, ax, az) || Between(cx, cz, dx, dz, bx, bz);
    }

    private static bool IntersectSegContour(int d0x, int d0z, int d1x, int d1z, int i, int n, int[] verts)
    {
        for (var k = 0; k < n; k++)
        {
            var k1 = k + 1 < n ? k + 1 : 0;
            if (i == k || i == k1)
                continue;
            int p0x = verts[k * 4], p0z = verts[k * 4 + 2], p1x = verts[k1 * 4], p1z = verts[k1 * 4 + 2];
            if ((d0x == p0x && d0z == p0z) || (d1x == p0x && d1z == p0z) || (d0x == p1x && d0z == p1z) || (d1x == p1x && d1z == p1z))
                continue;
            if (Intersect(d0x, d0z, d1x, d1z, p0x, p0z, p1x, p1z))
                return true;
        }
        return false;
    }

    private static bool InCone(int i, int n, int[] verts, int pjx, int pjz)
    {
        var pi = i * 4;
        var pi1 = (i + 1 < n ? i + 1 : 0) * 4;
        var pin1 = (i - 1 >= 0 ? i - 1 : n - 1) * 4;
        if (LeftOn(verts[pin1], verts[pin1 + 2], verts[pi], verts[pi + 2], verts[pi1], verts[pi1 + 2]))
            return Left(verts[pi], verts[pi + 2], pjx, pjz, verts[pin1], verts[pin1 + 2]) && Left(pjx, pjz, verts[pi], verts[pi + 2], verts[pi1], verts[pi1 + 2]);
        return !(LeftOn(verts[pi], verts[pi + 2], pjx, pjz, verts[pi1], verts[pi1 + 2]) && LeftOn(pjx, pjz, verts[pi], verts[pi + 2], verts[pin1], verts[pin1 + 2]));
    }

    private sealed class Hole
    {
        public required RecastContour Contour;
        public int MinX, MinZ, Leftmost;
    }

    // 1800b20f0.
    private static void MergeRegionHoles(RecastContour outline, List<RecastContour> holeContours)
    {
        var holes = new List<Hole>();
        foreach (var c in holeContours)
        {
            var h = new Hole { Contour = c, MinX = c.Verts[0], MinZ = c.Verts[2], Leftmost = 0 };
            for (var i = 1; i < c.NVerts; i++)
            {
                int x = c.Verts[i * 4], z = c.Verts[i * 4 + 2];
                if (x < h.MinX || (x == h.MinX && z < h.MinZ))
                {
                    h.MinX = x;
                    h.MinZ = z;
                    h.Leftmost = i;
                }
            }
            holes.Add(h);
        }
        CrtQsort.Sort(holes, (a, b) => a.MinX == b.MinX ? (a.MinZ < b.MinZ ? -1 : a.MinZ > b.MinZ ? 1 : 0) : a.MinX < b.MinX ? -1 : a.MinX > b.MinX ? 1 : 0);
        var maxVerts = outline.NVerts;
        foreach (var h in holes)
            maxVerts += h.Contour.NVerts;
        // One buffer for every hole, as Valve allocates it: the diags[i] read sees whatever an
        // earlier hole left there.
        var diags = new (int Vert, int Dist)[maxVerts];
        for (var i = 0; i < holes.Count; i++)
        {
            var hole = holes[i].Contour;
            var index = -1;
            var bestVertex = holes[i].Leftmost;
            for (var iter = 0; iter < hole.NVerts; iter++)
            {
                var ndiags = 0;
                int cx = hole.Verts[bestVertex * 4], cz = hole.Verts[bestVertex * 4 + 2];
                for (var j = 0; j < outline.NVerts; j++)
                {
                    if (InCone(j, outline.NVerts, outline.Verts, cx, cz))
                    {
                        var dx = outline.Verts[j * 4] - cx;
                        var dz = outline.Verts[j * 4 + 2] - cz;
                        diags[ndiags++] = (j, dx * dx + dz * dz);
                    }
                }
                CrtQsort.Sort(new ArraySegment<(int Vert, int Dist)>(diags, 0, ndiags), (a, b) => a.Dist < b.Dist ? -1 : a.Dist > b.Dist ? 1 : 0);
                index = -1;
                for (var j = 0; j < ndiags; j++)
                {
                    int ptx = outline.Verts[diags[j].Vert * 4], ptz = outline.Verts[diags[j].Vert * 4 + 2];
                    var intersect = IntersectSegContour(ptx, ptz, cx, cz, diags[i].Vert, outline.NVerts, outline.Verts);
                    for (var k = i; k < holes.Count && !intersect; k++)
                        intersect |= IntersectSegContour(ptx, ptz, cx, cz, -1, holes[k].Contour.NVerts, holes[k].Contour.Verts);
                    if (!intersect)
                    {
                        index = diags[j].Vert;
                        break;
                    }
                }
                if (index != -1)
                    break;
                bestVertex = (bestVertex + 1) % hole.NVerts;
            }
            if (index == -1)
                continue;
            var merged = new int[(outline.NVerts + hole.NVerts + 2) * 4];
            var nv = 0;
            for (var k = 0; k <= outline.NVerts; k++, nv++)
                Array.Copy(outline.Verts, (index + k) % outline.NVerts * 4, merged, nv * 4, 4);
            for (var k = 0; k <= hole.NVerts; k++, nv++)
                Array.Copy(hole.Verts, (bestVertex + k) % hole.NVerts * 4, merged, nv * 4, 4);
            outline.Verts = merged;
            outline.NVerts = nv;
            hole.Verts = [];
            hole.NVerts = 0;
        }
    }
}
