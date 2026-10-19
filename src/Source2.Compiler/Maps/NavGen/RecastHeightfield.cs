namespace Source2.Compiler.Maps.NavGen;

/// <summary>A solid span: heights in cells (16 bits in Valve's Recast), the area in the low six flag bits.</summary>
public sealed class RecastSpan
{
    public ushort SMin, SMax;
    public uint Flags;
    public RecastSpan? Next;

    public int Area
    {
        get => (int)(Flags & 0x3f);
        set => Flags = (Flags & ~0x3fu) | ((uint)value & 0x3f);
    }
}

/// <summary>
/// rcHeightfield and the steps navsystem (20261015) runs on it: rcCreateHeightfield 1800a88b0,
/// rcMarkWalkableTriangles 1800a8b70, rcRasterizeTriangles 1800a9780 (rasterizeTri 1800a9230,
/// dividePoly 1800a8fc0, addSpan 1800a8d60) and the three filters 1800a9f50, 1800a9a70, 1800a9910.
/// Valve's Recast is the 1.5 clip-and-span rasteriser with 16-bit heights; where Valve changed the
/// code (span merging, the filters) the port follows the DLL.
/// </summary>
public sealed class RecastHeightfield
{
    public const int MaxHeight = 0xffff;
    public const int WalkableArea = 0x3f;

    public int Width, Height;
    public float BMinX, BMinY, BMinZ, BMaxX, BMaxY, BMaxZ, Cs, Ch;
    public RecastSpan?[] Spans;

    public RecastHeightfield(int width, int height, float[] bmin, float[] bmax, float cs, float ch)
    {
        Width = width;
        Height = height;
        (BMinX, BMinY, BMinZ) = (bmin[0], bmin[1], bmin[2]);
        (BMaxX, BMaxY, BMaxZ) = (bmax[0], bmax[1], bmax[2]);
        Cs = cs;
        Ch = ch;
        Spans = new RecastSpan?[width * height];
    }

    /// <summary>
    /// rcMarkWalkableTriangles (1800a8b70): a triangle whose unit normal's y exceeds
    /// cosf(slope / 180 * pi) is walkable (0x3f).
    /// </summary>
    public static void MarkWalkableTriangles(float slope, float[] verts, int[] tris, int triStart, int count, byte[] areas)
    {
        var thr = Simulation.CrtMath.Cos(slope / 180f * 3.14159274f);
        for (var i = 0; i < count; i++)
        {
            var t = (triStart + i) * 3;
            int a = tris[t] * 3, b = tris[t + 1] * 3, c = tris[t + 2] * 3;
            float e1z = verts[c + 2] - verts[a + 2], e1y = verts[c + 1] - verts[a + 1], e1x = verts[c] - verts[a];
            float e0x = verts[b] - verts[a], e0z = verts[b + 2] - verts[a + 2], e0y = verts[b + 1] - verts[a + 1];
            var ny = e1x * e0z - e1z * e0x;
            var nx = e1z * e0y - e1y * e0z;
            var nz = e1y * e0x - e1x * e0y;
            var len2 = ny * ny + nx * nx + nz * nz;
            var len = MathF.Sqrt(len2);
            if ((1f / len) * ny > thr)
                areas[i] = WalkableArea;
        }
    }

    /// <summary>rcRasterizeTriangles (1800a9780): every triangle, flagMergeThr the walkable climb.</summary>
    public void RasterizeTriangles(float[] verts, int[] tris, int triStart, byte[] areas, int count, int flagMergeThr)
    {
        var ics = 1f / Cs;
        var ich = 1f / Ch;
        for (var i = 0; i < count; i++)
        {
            var t = (triStart + i) * 3;
            RasterizeTri(verts, tris[t] * 3, tris[t + 1] * 3, tris[t + 2] * 3, areas[i], ics, ich, flagMergeThr);
        }
    }

    // 1800a8fc0: clip a polygon against the plane axis = x; out1 the side d >= 0, out2 the rest.
    private static void DividePoly(float[] input, int nin, float[] out1, out int nout1, float[] out2, out int nout2, float x, int axis)
    {
        Span<float> d = stackalloc float[12];
        for (var i = 0; i < nin; i++)
            d[i] = x - input[i * 3 + axis];
        int m = 0, n = 0;
        for (int i = 0, j = nin - 1; i < nin; j = i, i++)
        {
            var ina = d[j] >= 0;
            var inb = d[i] >= 0;
            if (ina != inb)
            {
                var s = d[j] / (d[j] - d[i]);
                out1[m * 3] = (input[i * 3] - input[j * 3]) * s + input[j * 3];
                out1[m * 3 + 1] = (input[i * 3 + 1] - input[j * 3 + 1]) * s + input[j * 3 + 1];
                out1[m * 3 + 2] = (input[i * 3 + 2] - input[j * 3 + 2]) * s + input[j * 3 + 2];
                out2[n * 3] = out1[m * 3];
                out2[n * 3 + 1] = out1[m * 3 + 1];
                out2[n * 3 + 2] = out1[m * 3 + 2];
                m++;
                n++;
                if (d[i] > 0)
                {
                    Copy(out1, m, input, i);
                    m++;
                }
                else if (d[i] < 0)
                {
                    Copy(out2, n, input, i);
                    n++;
                }
            }
            else
            {
                if (d[i] >= 0)
                {
                    Copy(out1, m, input, i);
                    m++;
                    if (d[i] != 0)
                        continue;
                }
                Copy(out2, n, input, i);
                n++;
            }
        }
        nout1 = m;
        nout2 = n;
    }

    private static void Copy(float[] dst, int di, float[] src, int si)
    {
        dst[di * 3] = src[si * 3];
        dst[di * 3 + 1] = src[si * 3 + 1];
        dst[di * 3 + 2] = src[si * 3 + 2];
    }

    // 1800a9230.
    private void RasterizeTri(float[] verts, int a, int b, int c, byte area, float ics, float ich, int flagMergeThr)
    {
        var by = BMaxY - BMinY;
        float tminX = MathF.Min(MathF.Min(verts[a], verts[b]), verts[c]), tmaxX = MathF.Max(MathF.Max(verts[a], verts[b]), verts[c]);
        float tminY = MathF.Min(MathF.Min(verts[a + 1], verts[b + 1]), verts[c + 1]), tmaxY = MathF.Max(MathF.Max(verts[a + 1], verts[b + 1]), verts[c + 1]);
        float tminZ = MathF.Min(MathF.Min(verts[a + 2], verts[b + 2]), verts[c + 2]), tmaxZ = MathF.Max(MathF.Max(verts[a + 2], verts[b + 2]), verts[c + 2]);
        if (!(BMinX <= tmaxX && tminX <= BMaxX))
            return;
        if (tmaxY < BMinY || BMaxY < tminY)
            return;
        if (!(BMinZ <= tmaxZ && tminZ <= BMaxZ))
            return;

        var buf = new float[7 * 3 * 4];
        var bufs = new[] { new float[21], new float[21], new float[21], new float[21] };
        float[] input = bufs[0], inrow = bufs[1], p1 = bufs[2], p2 = bufs[3];
        input[0] = verts[a];
        input[1] = verts[a + 1];
        input[2] = verts[a + 2];
        input[3] = verts[b];
        input[4] = verts[b + 1];
        input[5] = verts[b + 2];
        input[6] = verts[c];
        input[7] = verts[c + 1];
        input[8] = verts[c + 2];
        var nvIn = 3;
        var z0 = Clamp((int)((tminZ - BMinZ) * ics), 0, Height - 1);
        var z1 = Clamp((int)((tmaxZ - BMinZ) * ics), 0, Height - 1);
        for (var z = z0; z <= z1; z++)
        {
            var cz = (float)z * Cs + BMinZ;
            DividePoly(input, nvIn, inrow, out var nvrow, p1, out nvIn, cz + Cs, 2);
            (input, p1) = (p1, input);
            if (nvrow < 3)
                continue;
            float minX = inrow[0], maxX = inrow[0];
            for (var i = 1; i < nvrow; i++)
            {
                minX = MathF.Min(minX, inrow[i * 3]);
                maxX = MathF.Max(maxX, inrow[i * 3]);
            }
            var x0 = Clamp((int)((minX - BMinX) * ics), 0, Width - 1);
            var x1 = Clamp((int)((maxX - BMinX) * ics), 0, Width - 1);
            var nv2 = nvrow;
            for (var x = x0; x <= x1; x++)
            {
                var cx = (float)x * Cs + BMinX;
                DividePoly(inrow, nv2, p1, out var nv, p2, out nv2, cx + Cs, 0);
                (inrow, p2) = (p2, inrow);
                if (nv < 3)
                    continue;
                float smin = p1[1], smax = p1[1];
                for (var i = 1; i < nv; i++)
                {
                    smin = MathF.Min(smin, p1[i * 3 + 1]);
                    smax = MathF.Max(smax, p1[i * 3 + 1]);
                }
                smin -= BMinY;
                smax -= BMinY;
                if (smax < 0f || smin > by)
                    continue;
                if (smin < 0f)
                    smin = 0f;
                if (smax > by)
                    smax = by;
                var ismin = Clamp((int)MathF.Floor(smin * ich), 0, MaxHeight);
                var ismax = ismin + 1;
                var ceil = (int)MathF.Ceiling(smax * ich);
                if (ismax <= ceil)
                    ismax = Math.Min(ceil, MaxHeight);
                AddSpan(x, z, (ushort)ismin, (ushort)ismax, area, flagMergeThr);
            }
        }
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>
    /// addSpan (1800a8d60). Overlapping spans merge into the new one; the merged area is Valve's rule,
    /// decided from the new span's top (before the merge) against each overlapped span's top:
    /// equal tops take the larger area; an overlapped span reaching higher gives its area when the tops
    /// differ by more than the climb, area 1 when it is unwalkable under a walkable new span, else its
    /// area; a lower overlapped span within the climb that is walkable under an unwalkable new span
    /// makes it area 1.
    /// </summary>
    public void AddSpan(int x, int z, ushort smin, ushort smax, byte area, int flagMergeThr)
    {
        var idx = x + z * Width;
        var s = new RecastSpan { SMin = smin, SMax = smax, Flags = (uint)(area & 0x3f) };
        RecastSpan? prev = null;
        var cur = Spans[idx];
        if (cur is null)
        {
            Spans[idx] = s;
            return;
        }
        while (cur is not null)
        {
            var top = s.SMax;
            if (top < cur.SMin)
                break;
            RecastSpan? next;
            if (cur.SMax < s.SMin)
            {
                prev = cur;
                next = cur.Next;
            }
            else
            {
                if (cur.SMin < s.SMin)
                    s.SMin = cur.SMin;
                if (top < cur.SMax)
                    s.SMax = cur.SMax;
                if (top <= cur.SMax)
                {
                    if (top == cur.SMax)
                        s.Area = Math.Max(s.Area, cur.Area);
                    else if (Math.Abs(top - cur.SMax) > flagMergeThr)
                        s.Area = cur.Area;
                    else if (cur.Area == 0 && s.Area != 0)
                        s.Area = 1;
                    else
                        s.Area = cur.Area;
                }
                else if (Math.Abs(top - cur.SMax) <= flagMergeThr && cur.Area != 0 && s.Area == 0)
                    s.Area = 1;
                next = cur.Next;
                if (prev is null)
                    Spans[idx] = next;
                else
                    prev.Next = next;
            }
            cur = next;
        }
        if (prev is null)
        {
            s.Next = Spans[idx];
            Spans[idx] = s;
        }
        else
        {
            s.Next = prev.Next;
            prev.Next = s;
        }
    }

    /// <summary>
    /// rcFilterLowHangingWalkableObstacles (1800a9f50), Valve's: an unwalkable span whose top is within
    /// the climb of the last walkable span below it in the column becomes area 1.
    /// </summary>
    public void FilterLowHangingWalkableObstacles(int walkableClimb)
    {
        for (var i = 0; i < Width * Height; i++)
        {
            RecastSpan? lastWalkable = null;
            for (var s = Spans[i]; s is not null; s = s.Next)
            {
                if (s.Area != 0)
                {
                    lastWalkable = s;
                    continue;
                }
                if (lastWalkable is not null && Math.Abs(s.SMax - lastWalkable.SMax) <= walkableClimb)
                    s.Area = 1;
            }
        }
    }

    /// <summary>
    /// rcFilterLedgeSpans (1800a9a70), Valve's: as Recast, except a neighbour column outside the
    /// heightfield is skipped instead of counting as a drop.
    /// </summary>
    public void FilterLedgeSpans(int walkableHeight, int walkableClimb)
    {
        ReadOnlySpan<int> dxs = [-1, 0, 1, 0], dzs = [0, 1, 0, -1];
        for (var z = 0; z < Height; z++)
        {
            for (var x = 0; x < Width; x++)
            {
                for (var s = Spans[x + z * Width]; s is not null; s = s.Next)
                {
                    if (s.Area == 0)
                        continue;
                    int bot = s.SMax;
                    var top = s.Next is null ? MaxHeight : s.Next.SMin;
                    var minh = MaxHeight;
                    int asmin = s.SMax, asmax = s.SMax;
                    for (var dir = 0; dir < 4; dir++)
                    {
                        int dx = x + dxs[dir], dz = z + dzs[dir];
                        if (dx < 0 || dz < 0 || dx >= Width || dz >= Height)
                            continue;
                        var ns = Spans[dx + dz * Width];
                        var nbot = -walkableClimb;
                        var ntop = ns is null ? MaxHeight : ns.SMin;
                        if (Math.Min(top, ntop) - Math.Max(bot, nbot) > walkableHeight)
                            minh = Math.Min(minh, nbot - bot);
                        for (; ns is not null; ns = ns.Next)
                        {
                            nbot = ns.SMax;
                            ntop = ns.Next is null ? MaxHeight : ns.Next.SMin;
                            if (Math.Min(top, ntop) - Math.Max(bot, nbot) > walkableHeight)
                            {
                                minh = Math.Min(minh, nbot - bot);
                                if (Math.Abs(nbot - bot) <= walkableClimb)
                                {
                                    asmin = Math.Min(asmin, nbot);
                                    asmax = Math.Max(asmax, nbot);
                                }
                            }
                        }
                    }
                    if (minh < -walkableClimb || asmax - asmin > walkableClimb)
                        s.Area = 0;
                }
            }
        }
    }

    /// <summary>
    /// rcFilterWalkableLowHeightSpans (1800a9910), Valve's: the clearance above a walkable span must
    /// exceed one of the three hull heights, else the span is unwalkable; a default-walkable (0x3f)
    /// span is then classed by the tallest height its clearance exceeds: 0x3c, 0x3d or 0x3e.
    /// </summary>
    public void FilterWalkableLowHeightSpans(int height1, int height2, int height3)
    {
        for (var i = 0; i < Width * Height; i++)
        {
            for (var s = Spans[i]; s is not null; s = s.Next)
            {
                if (s.Area == 0)
                    continue;
                var top = s.Next is null ? MaxHeight : s.Next.SMin;
                var clearance = top - s.SMax;
                if (height1 < clearance || height2 < clearance || height3 < clearance)
                {
                    if (s.Area != WalkableArea)
                        continue;
                    s.Area = height1 < clearance ? 0x3c : height2 < clearance ? 0x3d : 0x3e;
                }
                else
                    s.Area = 0;
            }
        }
    }

    /// <summary>rcGetHeightFieldSpanCount (1800a8af0): walkable spans.</summary>
    public int WalkableSpanCount()
    {
        var n = 0;
        foreach (var col in Spans)
            for (var s = col; s is not null; s = s.Next)
                if (s.Area != 0)
                    n++;
        return n;
    }
}
