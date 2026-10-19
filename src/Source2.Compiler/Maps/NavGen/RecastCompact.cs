namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// rcCompactHeightfield as navsystem (20261015) lays it out and builds it: rcBuildCompactHeightfield
/// 1800a7ff0 (Valve's connects neighbours at the smallest of the three hull heights) and Valve's
/// own erosion 1800aa900 in place of rcErodeWalkableArea.
/// </summary>
public sealed partial class RecastCompact
{
    public const int NotConnected = 0x3f;
    public static readonly int[] DirX = [-1, 0, 1, 0], DirZ = [0, 1, 0, -1];

    public int Width, Height, SpanCount, WalkableHeight, WalkableHeight2, WalkableHeight3, WalkableClimb, BorderSize;
    public ushort MaxDistance, MaxRegions;
    public float[] BMin = new float[3], BMax = new float[3];
    public float Cs, Ch;

    /// <summary>Cells: first span index (24 bits) and count (high byte).</summary>
    public uint[] Cells = [];
    public ushort[] Y = [], Reg = [];
    /// <summary>Connections (6 bits a direction) and the clearance h in the high byte, as rcCompactSpan's second word.</summary>
    public uint[] Con = [];
    public byte[] Areas = [], DbgFlags = [];
    public ushort[] Dist = [];

    public int CellIndex(int c) => (int)(Cells[c] & 0xffffff);
    public int CellCount(int c) => (int)(Cells[c] >> 24);
    public int H(int i) => (int)(Con[i] >> 24);
    public int GetCon(int i, int dir) => (int)((Con[i] >> (dir * 6)) & 0x3f);

    private void SetCon(int i, int dir, int v)
    {
        var shift = dir * 6;
        Con[i] = (Con[i] & ~(0x3fu << shift)) | ((uint)(v & 0x3f) << shift);
    }

    /// <summary>The neighbour span through a connection.</summary>
    public int Neighbour(int x, int z, int i, int dir) => CellIndex(x + DirX[dir] + (z + DirZ[dir]) * Width) + GetCon(i, dir);

    /// <summary>rcBuildCompactHeightfield (1800a7ff0).</summary>
    public static RecastCompact Build(RecastHeightfield hf, int wh1, int wh2, int wh3, int climb)
    {
        var w = hf.Width;
        var h = hf.Height;
        var spanCount = hf.WalkableSpanCount();
        var c = new RecastCompact
        {
            Width = w,
            Height = h,
            SpanCount = spanCount,
            WalkableHeight = wh1,
            WalkableHeight2 = wh2,
            WalkableHeight3 = wh3,
            WalkableClimb = climb,
            BMin = [hf.BMinX, hf.BMinY, hf.BMinZ],
            BMax = [hf.BMaxX, (float)wh1 * hf.Ch + hf.BMaxY, hf.BMaxZ],
            Cs = hf.Cs,
            Ch = hf.Ch,
            Cells = new uint[w * h],
            Y = new ushort[spanCount],
            Reg = new ushort[spanCount],
            Con = new uint[spanCount],
            Areas = new byte[spanCount],
            DbgFlags = new byte[spanCount],
        };
        var idx = 0;
        for (var z = 0; z < h; z++)
        {
            for (var x = 0; x < w; x++)
            {
                var s = hf.Spans[x + z * w];
                if (s is null)
                    continue;
                var count = 0;
                var start = idx;
                for (; s is not null; s = s.Next)
                {
                    if (s.Area == 0)
                        continue;
                    var top = s.Next is null ? RecastHeightfield.MaxHeight : s.Next.SMin;
                    c.Y[idx] = s.SMax;
                    c.Con[idx] = (uint)Math.Clamp(top - s.SMax, 0, 0xff) << 24;
                    c.Areas[idx] = (byte)s.Area;
                    idx++;
                    count++;
                }
                c.Cells[x + z * w] = (uint)start | ((uint)count << 24);
            }
        }
        var minH = Math.Min(wh1, Math.Min(wh2, wh3));
        for (var z = 0; z < h; z++)
        {
            for (var x = 0; x < w; x++)
            {
                var cell = x + z * w;
                for (int i = c.CellIndex(cell), ni = i + c.CellCount(cell); i < ni; i++)
                {
                    for (var dir = 0; dir < 4; dir++)
                    {
                        c.SetCon(i, dir, NotConnected);
                        int nx = x + DirX[dir], nz = z + DirZ[dir];
                        if (nx < 0 || nz < 0 || nx >= w || nz >= h)
                            continue;
                        var nc = nx + nz * w;
                        for (int k = c.CellIndex(nc), nk = k + c.CellCount(nc); k < nk; k++)
                        {
                            var bot = Math.Max(c.Y[i], c.Y[k]);
                            var top = Math.Min(c.Y[i] + c.H(i), c.Y[k] + c.H(k));
                            if (top - bot >= minH && Math.Abs(c.Y[k] - c.Y[i]) <= climb)
                            {
                                var lidx = k - c.CellIndex(nc);
                                if (lidx < NotConnected)
                                {
                                    c.SetCon(i, dir, lidx);
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }
        return c;
    }

    /// <summary>The three arrays the erosion's distance transforms carry: squared distance, and the offset to the source.</summary>
    public sealed class DistanceVectors(int n)
    {
        public uint[] D = new uint[n];
        public int[] Dx = new int[n], Dz = new int[n];

        public void Reset()
        {
            Array.Fill(D, uint.MaxValue);
            Array.Clear(Dx);
            Array.Clear(Dz);
        }
    }

    // 1800aa060: the forward sweep (x-1, then x-1,z-1 through it; z-1, then x+1,z-1 through it).
    private void SweepForward(DistanceVectors v)
    {
        for (var z = 0; z < Height; z++)
        {
            for (var x = 0; x < Width; x++)
            {
                var cell = x + z * Width;
                for (int i = CellIndex(cell), ni = i + CellCount(cell); i < ni; i++)
                {
                    if (v.D[i] == 0)
                        continue;
                    if (GetCon(i, 0) != NotConnected)
                    {
                        var ai = CellIndex(cell - 1) + GetCon(i, 0);
                        if (v.D[ai] != uint.MaxValue)
                        {
                            var nd = (uint)(v.D[ai] + Math.Abs(v.Dx[ai]) * 2 + 1);
                            if (nd < v.D[i])
                            {
                                v.D[i] = nd;
                                v.Dx[i] = v.Dx[ai] - 1;
                                v.Dz[i] = v.Dz[ai];
                            }
                        }
                        if (GetCon(ai, 3) != NotConnected)
                        {
                            var aai = CellIndex((z - 1) * Width + x - 1) + GetCon(ai, 3);
                            if (v.D[aai] != uint.MaxValue)
                            {
                                var nd = (uint)(v.D[aai] + (Math.Abs(v.Dz[aai]) + 1 + Math.Abs(v.Dx[aai])) * 2);
                                if (nd < v.D[i])
                                {
                                    v.D[i] = nd;
                                    v.Dx[i] = v.Dx[aai] - 1;
                                    v.Dz[i] = v.Dz[aai] - 1;
                                }
                            }
                        }
                    }
                    if (GetCon(i, 3) != NotConnected)
                    {
                        var ai = CellIndex((z - 1) * Width + x) + GetCon(i, 3);
                        if (v.D[ai] != uint.MaxValue)
                        {
                            var nd = (uint)(v.D[ai] + Math.Abs(v.Dz[ai]) * 2 + 1);
                            if (nd < v.D[i])
                            {
                                v.D[i] = nd;
                                v.Dx[i] = v.Dx[ai];
                                v.Dz[i] = v.Dz[ai] - 1;
                            }
                        }
                        if (GetCon(ai, 2) != NotConnected)
                        {
                            var aai = CellIndex((z - 1) * Width + x + 1) + GetCon(ai, 2);
                            if (v.D[aai] != uint.MaxValue)
                            {
                                var nd = (uint)(v.D[aai] + (Math.Abs(v.Dx[aai]) + 1 + Math.Abs(v.Dz[aai])) * 2);
                                if (nd < v.D[i])
                                {
                                    v.D[i] = nd;
                                    v.Dx[i] = v.Dx[aai] + 1;
                                    v.Dz[i] = v.Dz[aai] - 1;
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    // 1800aa390: the backward sweep (x+1, then x+1,z+1 through it; z+1, then x-1,z+1 through it).
    private void SweepBackward(DistanceVectors v)
    {
        for (var z = Height - 1; z >= 0; z--)
        {
            for (var x = Width - 1; x >= 0; x--)
            {
                var cell = x + z * Width;
                for (int i = CellIndex(cell), ni = i + CellCount(cell); i < ni; i++)
                {
                    if (v.D[i] == 0)
                        continue;
                    if (GetCon(i, 2) != NotConnected)
                    {
                        var ai = CellIndex(cell + 1) + GetCon(i, 2);
                        if (v.D[ai] != uint.MaxValue)
                        {
                            var nd = (uint)(v.D[ai] + Math.Abs(v.Dx[ai]) * 2 + 1);
                            if (nd < v.D[i])
                            {
                                v.D[i] = nd;
                                v.Dx[i] = v.Dx[ai] + 1;
                                v.Dz[i] = v.Dz[ai];
                            }
                        }
                        if (GetCon(ai, 1) != NotConnected)
                        {
                            var aai = CellIndex((z + 1) * Width + x + 1) + GetCon(ai, 1);
                            if (v.D[aai] != uint.MaxValue)
                            {
                                var nd = (uint)(v.D[aai] + (Math.Abs(v.Dz[aai]) + 1 + Math.Abs(v.Dx[aai])) * 2);
                                if (nd < v.D[i])
                                {
                                    v.D[i] = nd;
                                    v.Dx[i] = v.Dx[aai] + 1;
                                    v.Dz[i] = v.Dz[aai] + 1;
                                }
                            }
                        }
                    }
                    if (GetCon(i, 1) != NotConnected)
                    {
                        var ai = CellIndex((z + 1) * Width + x) + GetCon(i, 1);
                        if (v.D[ai] != uint.MaxValue)
                        {
                            var nd = (uint)(v.D[ai] + Math.Abs(v.Dz[ai]) * 2 + 1);
                            if (nd < v.D[i])
                            {
                                v.D[i] = nd;
                                v.Dx[i] = v.Dx[ai];
                                v.Dz[i] = v.Dz[ai] + 1;
                            }
                        }
                        if (GetCon(ai, 0) != NotConnected)
                        {
                            var aai = CellIndex((z + 1) * Width + x - 1) + GetCon(ai, 0);
                            if (v.D[aai] != uint.MaxValue)
                            {
                                var nd = (uint)(v.D[aai] + (Math.Abs(v.Dz[aai]) + 1 + Math.Abs(v.Dx[aai])) * 2);
                                if (nd < v.D[i])
                                {
                                    v.D[i] = nd;
                                    v.Dx[i] = v.Dx[aai] - 1;
                                    v.Dz[i] = v.Dz[aai] + 1;
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private void Sweep2(DistanceVectors v)
    {
        for (var k = 0; k < 2; k++)
        {
            SweepForward(v);
            SweepBackward(v);
        }
    }

    // How many in-bounds neighbours there are and how many are connected spans with an area
    // (1800ab1d0, 1800ab460: a span short of all is a boundary; the heightfield's edge does not count).
    private (int InBounds, int Walkable) NeighbourCounts(int x, int z, int i)
    {
        int inBounds = 0, walkable = 0;
        for (var dir = 0; dir < 4; dir++)
        {
            int nx = x + DirX[dir], nz = z + DirZ[dir];
            if (nx < 0 || nz < 0 || nx >= Width || nz >= Height)
                continue;
            inBounds++;
            var con = GetCon(i, dir);
            if (con != NotConnected && Areas[CellIndex(nx + nz * Width) + con] != 0)
                walkable++;
        }
        return (inBounds, walkable);
    }

    private int HullHeight(byte area) => area == 0x3e ? WalkableHeight3 : area == 0x3d ? WalkableHeight2 : WalkableHeight;

    /// <summary>
    /// 1800aaff0: the nearest column (squared 2D distance under r^2, z outer, x inner, first found on
    /// ties) holding a solid span that reaches into the capsule of radius r from y + r up to
    /// y + h - r; its offset goes to (dx, dz).
    /// </summary>
    public static bool Blocked(RecastHeightfield solid, int x, int z, int y, int r, int h, out int dx, out int dz)
    {
        dx = dz = 0;
        var best = uint.MaxValue;
        var x0 = Math.Max(0, x - r);
        var x1 = x + r < solid.Width ? x + r : solid.Width - 1;
        var z1 = z + r < solid.Height ? z + r : solid.Height - 1;
        var capTop = y - r + h;
        var r2 = (uint)(r * r);
        for (var nz = Math.Max(0, z - r); nz <= z1; nz++)
        {
            var oz = nz - z;
            for (var nx = x0; nx <= x1; nx++)
            {
                var ox = nx - x;
                var d2 = (uint)(ox * ox + oz * oz);
                if (d2 >= r2 || d2 >= best)
                    continue;
                var more = true;
                for (var s = solid.Spans[nx + nz * solid.Width]; s is not null && more; s = s.Next)
                {
                    more = false;
                    int smin = s.SMin, smax = s.SMax;
                    if (capTop < smin && r2 < (uint)((smin - capTop) * (smin - capTop)) + d2)
                        more = true;
                    var below = smax - (y + r);
                    if (smax < y + r && r2 < (uint)(below * below) + d2)
                        more = true;
                    else if (!more)
                    {
                        dx = ox;
                        dz = oz;
                        best = d2;
                    }
                }
            }
        }
        return best != uint.MaxValue;
    }

    /// <summary>
    /// Valve's erosion (1800aa900, flag byte tile + 0xcc, radius, solid, minimum tile + 0xc8):
    /// <list type="number">
    /// <item>1800aab30: area-1 spans (merged under a walkable span) at or beyond (radius / 2)^2 from
    /// any span with an area above 1 lose their area.</item>
    /// <item>A distance field from the boundary spans, then (the minimum being 0) no cut.</item>
    /// <item>1800abb50: a classed span beside an area-1 span is cleared when a cylinder probe at its
    /// height finds the solid; then each area-1 span is classed by the first hull height that fits.</item>
    /// <item>1800aace0: spans within radius + 1 of a 0x3e span become 0x3e, else of a 0x3d span 0x3d.</item>
    /// <item>1800ab460 + 1800aa740: boundary spans probe for the nearest blocking column; that offset
    /// spreads, and a span within radius + 1 whose obstacle column has solid across its height is
    /// cleared.</item>
    /// </list>
    /// </summary>
    public void Erode(byte flag, int radius, RecastHeightfield solid, int minimum)
    {
        ClearFarArea1(radius);
        var main = new DistanceVectors(SpanCount);
        if (minimum < 0)
        {
            minimum = radius;
            if (flag == 0)
                minimum = radius / 2;
        }
        InitBoundaries(main);
        Sweep2(main);
        for (var i = 0; i < SpanCount; i++)
        {
            if (main.D[i] < (uint)(minimum * minimum) && Areas[i] != 0)
            {
                Areas[i] = 0;
                if (DbgFlags[i] == 0)
                    DbgFlags[i] = 3;
            }
        }
        ClassifyArea1(radius, solid);
        SpreadHeightClasses(main, radius + 1, minimum);
        ProbeBoundaries(radius, main, solid);
        CutNearObstacles(main, solid, radius + 1);
    }

    // 1800aab30.
    private void ClearFarArea1(int radius)
    {
        var v = new DistanceVectors(SpanCount);
        v.Reset();
        for (var i = 0; i < SpanCount; i++)
            if (Areas[i] > 1)
                v.D[i] = 0;
        Sweep2(v);
        var half = radius / 2;
        for (var i = 0; i < SpanCount; i++)
            if (Areas[i] == 1 && (uint)(half * half) <= v.D[i])
                Areas[i] = 0;
    }

    // 1800ab1d0.
    private void InitBoundaries(DistanceVectors v)
    {
        v.Reset();
        for (var z = 0; z < Height; z++)
        {
            for (var x = 0; x < Width; x++)
            {
                var cell = x + z * Width;
                for (int i = CellIndex(cell), ni = i + CellCount(cell); i < ni; i++)
                {
                    if (Areas[i] == 0)
                    {
                        v.D[i] = 0;
                        continue;
                    }
                    var (inBounds, walkable) = NeighbourCounts(x, z, i);
                    if (walkable != inBounds)
                        v.D[i] = 0;
                }
            }
        }
    }

    // 1800abb50.
    private void ClassifyArea1(int radius, RecastHeightfield solid)
    {
        for (var z = 0; z < Height; z++)
        {
            for (var x = 0; x < Width; x++)
            {
                var cell = x + z * Width;
                for (int i = CellIndex(cell), ni = i + CellCount(cell); i < ni; i++)
                {
                    var a = Areas[i];
                    if ((byte)(a - 0x3c) >= 3)
                        continue;
                    for (var dir = 0; dir < 4; dir++)
                    {
                        int nx = x + DirX[dir], nz = z + DirZ[dir];
                        if (nx < 0 || nz < 0 || nx >= Width || nz >= Height)
                            continue;
                        var con = GetCon(i, dir);
                        if (con == NotConnected || Areas[CellIndex(nx + nz * Width) + con] != 1)
                            continue;
                        if (Blocked(solid, x, z, Y[i] + WalkableClimb, radius, HullHeight(a) - WalkableClimb, out _, out _))
                            Areas[i] = 0;
                        break;
                    }
                }
            }
        }
        for (var z = 0; z < Height; z++)
        {
            for (var x = 0; x < Width; x++)
            {
                var cell = x + z * Width;
                for (int i = CellIndex(cell), ni = i + CellCount(cell); i < ni; i++)
                {
                    if (Areas[i] != 1)
                        continue;
                    var y = Y[i] + WalkableClimb;
                    if (!Blocked(solid, x, z, y, radius, WalkableHeight - WalkableClimb, out _, out _))
                        Areas[i] = 0x3c;
                    else if (WalkableHeight2 != int.MaxValue && !Blocked(solid, x, z, y, radius, WalkableHeight2 - WalkableClimb, out _, out _))
                        Areas[i] = 0x3d;
                    else if (WalkableHeight3 != int.MaxValue && !Blocked(solid, x, z, y, radius, WalkableHeight3 - WalkableClimb, out _, out _))
                        Areas[i] = 0x3e;
                    else
                        Areas[i] = 0;
                }
            }
        }
    }

    // 1800aace0.
    private void SpreadHeightClasses(DistanceVectors main, int reach, int minimum)
    {
        var to3d = new DistanceVectors(SpanCount);
        var to3e = new DistanceVectors(SpanCount);
        to3d.Reset();
        to3e.Reset();
        for (var i = 0; i < SpanCount; i++)
        {
            if (Areas[i] == 0x3e)
                to3e.D[i] = 0;
            else if (Areas[i] == 0x3d)
                to3d.D[i] = 0;
        }
        Sweep2(to3d);
        Sweep2(to3e);
        Sweep2(main);
        for (var i = 0; i < SpanCount; i++)
        {
            if ((uint)(minimum * minimum) > main.D[i])
                continue;
            if (to3e.D[i] < (uint)(reach * reach))
                Areas[i] = 0x3e;
            else if (to3d.D[i] < (uint)(reach * reach))
                Areas[i] = 0x3d;
        }
    }

    // 1800ab460.
    private void ProbeBoundaries(int radius, DistanceVectors v, RecastHeightfield solid)
    {
        v.Reset();
        for (var z = 0; z < Height; z++)
        {
            for (var x = 0; x < Width; x++)
            {
                var cell = x + z * Width;
                for (int i = CellIndex(cell), ni = i + CellCount(cell); i < ni; i++)
                {
                    var a = Areas[i];
                    if (a == 0)
                        continue;
                    var (inBounds, walkable) = NeighbourCounts(x, z, i);
                    if (walkable == inBounds)
                        continue;
                    if (Blocked(solid, x, z, Y[i] + WalkableClimb, radius, HullHeight(a) - WalkableClimb, out var dx, out var dz))
                    {
                        v.D[i] = (uint)(dz * dz + dx * dx);
                        v.Dx[i] = dx;
                        v.Dz[i] = dz;
                        DbgFlags[i] = 1;
                    }
                }
            }
        }
    }

    // 1800aa740.
    private void CutNearObstacles(DistanceVectors v, RecastHeightfield solid, int reach)
    {
        Sweep2(v);
        for (var z = 0; z < Height; z++)
        {
            for (var x = 0; x < Width; x++)
            {
                var cell = x + z * Width;
                for (int i = CellIndex(cell), ni = i + CellCount(cell); i < ni; i++)
                {
                    if (v.D[i] >= (uint)(reach * reach))
                        continue;
                    var a = Areas[i];
                    if (a == 0)
                        continue;
                    int ox = v.Dx[i] + x, oz = v.Dz[i] + z;
                    if (ox < 0 || ox >= Width || oz < 0 || oz >= Height)
                        continue;
                    var y = Y[i] + WalkableClimb;
                    var top = HullHeight(a) - WalkableClimb + y;
                    for (var s = solid.Spans[oz * solid.Width + ox]; s is not null; s = s.Next)
                    {
                        if (s.SMin <= top && y <= s.SMax)
                        {
                            Areas[i] = 0;
                            if (DbgFlags[i] == 0)
                                DbgFlags[i] = 2;
                            break;
                        }
                    }
                }
            }
        }
    }
}
