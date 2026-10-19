namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// rcBuildDistanceField (1800afdc0: calculateDistanceField 1800ac5d0, boxBlur 1800ac260) and the
/// watershed rcBuildRegions (1800b05a0: sortCellsByLevel 1800b1460, expandRegions 1800acd10,
/// floodRegion 1800ad3d0, mergeAndFilterRegions 1800ae7a0 with walkContour 1800b1720,
/// canMergeWithRegion 1800acbd0, mergeRegions 1800af6d0). navsystem's copies are Recast 1.5's.
/// </summary>
public sealed partial class RecastCompact
{
    public const ushort BorderReg = 0x8000;

    /// <summary>rcBuildDistanceField.</summary>
    public void BuildDistanceField()
    {
        var src = new ushort[SpanCount];
        var dst = new ushort[SpanCount];
        Array.Fill(src, (ushort)0xffff);
        for (var z = 0; z < Height; z++)
        {
            for (var x = 0; x < Width; x++)
            {
                var cell = x + z * Width;
                for (int i = CellIndex(cell), ni = i + CellCount(cell); i < ni; i++)
                {
                    var area = Areas[i];
                    var nc = 0;
                    for (var dir = 0; dir < 4; dir++)
                        if (GetCon(i, dir) != NotConnected && Areas[Neighbour(x, z, i, dir)] == area)
                            nc++;
                    if (nc != 4)
                        src[i] = 0;
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
                    if (GetCon(i, 0) != NotConnected)
                    {
                        var ai = Neighbour(x, z, i, 0);
                        if (src[ai] + 2 < src[i])
                            src[i] = (ushort)(src[ai] + 2);
                        if (GetCon(ai, 3) != NotConnected)
                        {
                            var aai = Neighbour(x - 1, z, ai, 3);
                            if (src[aai] + 3 < src[i])
                                src[i] = (ushort)(src[aai] + 3);
                        }
                    }
                    if (GetCon(i, 3) != NotConnected)
                    {
                        var ai = Neighbour(x, z, i, 3);
                        if (src[ai] + 2 < src[i])
                            src[i] = (ushort)(src[ai] + 2);
                        if (GetCon(ai, 2) != NotConnected)
                        {
                            var aai = Neighbour(x, z - 1, ai, 2);
                            if (src[aai] + 3 < src[i])
                                src[i] = (ushort)(src[aai] + 3);
                        }
                    }
                }
            }
        }
        for (var z = Height - 1; z >= 0; z--)
        {
            for (var x = Width - 1; x >= 0; x--)
            {
                var cell = x + z * Width;
                for (int i = CellIndex(cell), ni = i + CellCount(cell); i < ni; i++)
                {
                    if (GetCon(i, 2) != NotConnected)
                    {
                        var ai = Neighbour(x, z, i, 2);
                        if (src[ai] + 2 < src[i])
                            src[i] = (ushort)(src[ai] + 2);
                        if (GetCon(ai, 1) != NotConnected)
                        {
                            var aai = Neighbour(x + 1, z, ai, 1);
                            if (src[aai] + 3 < src[i])
                                src[i] = (ushort)(src[aai] + 3);
                        }
                    }
                    if (GetCon(i, 1) != NotConnected)
                    {
                        var ai = Neighbour(x, z, i, 1);
                        if (src[ai] + 2 < src[i])
                            src[i] = (ushort)(src[ai] + 2);
                        if (GetCon(ai, 0) != NotConnected)
                        {
                            var aai = Neighbour(x, z + 1, ai, 0);
                            if (src[aai] + 3 < src[i])
                                src[i] = (ushort)(src[aai] + 3);
                        }
                    }
                }
            }
        }
        ushort maxDist = 0;
        foreach (var d in src)
            if (d > maxDist)
                maxDist = d;
        MaxDistance = maxDist;
        // boxBlur, threshold 1.
        const int thr = 2;
        for (var z = 0; z < Height; z++)
        {
            for (var x = 0; x < Width; x++)
            {
                var cell = x + z * Width;
                for (int i = CellIndex(cell), ni = i + CellCount(cell); i < ni; i++)
                {
                    var cd = src[i];
                    if (cd <= thr)
                    {
                        dst[i] = cd;
                        continue;
                    }
                    int d = cd;
                    for (var dir = 0; dir < 4; dir++)
                    {
                        if (GetCon(i, dir) != NotConnected)
                        {
                            var ai = Neighbour(x, z, i, dir);
                            d += src[ai];
                            var dir2 = (dir + 1) & 3;
                            if (GetCon(ai, dir2) != NotConnected)
                                d += src[Neighbour(x + DirX[dir], z + DirZ[dir], ai, dir2)];
                            else
                                d += cd;
                        }
                        else
                            d += cd * 2;
                    }
                    dst[i] = (ushort)((uint)(d + 5) / 9);
                }
            }
        }
        Dist = dst;
    }

    private readonly record struct LevelEntry(int X, int Z, int Index);

    private sealed class Region
    {
        public int SpanCount;
        public ushort Id;
        public byte AreaType;
        public bool Remap, Visited, Overlap;
        public List<int> Connections = [], Floors = [];
    }

    /// <summary>rcBuildRegions (watershed).</summary>
    public void BuildRegions(int borderSize, int minRegionArea, int mergeRegionArea)
    {
        var srcReg = new ushort[SpanCount];
        var srcDist = new ushort[SpanCount];
        const int nbStacks = 8;
        var lvlStacks = new List<LevelEntry>[nbStacks];
        for (var i = 0; i < nbStacks; i++)
            lvlStacks[i] = [];
        var stack = new List<LevelEntry>();
        ushort regionId = 1;
        var level = (ushort)((MaxDistance + 1) & ~1);
        const int expandIters = 8;
        if (borderSize > 0)
        {
            var bw = Math.Min(Width, borderSize);
            var bh = Math.Min(Height, borderSize);
            PaintRect(0, bw, 0, Height, (ushort)(regionId++ | BorderReg), srcReg);
            PaintRect(Width - bw, Width, 0, Height, (ushort)(regionId++ | BorderReg), srcReg);
            PaintRect(0, Width, 0, bh, (ushort)(regionId++ | BorderReg), srcReg);
            PaintRect(0, Width, Height - bh, Height, (ushort)(regionId++ | BorderReg), srcReg);
        }
        BorderSize = borderSize;
        var sId = -1;
        while (level > 0)
        {
            level = (ushort)(level >= 2 ? level - 2 : 0);
            sId = (sId + 1) & (nbStacks - 1);
            if (sId == 0)
                SortCellsByLevel(level, srcReg, nbStacks, lvlStacks, 1);
            else
                foreach (var e in lvlStacks[sId - 1])
                    if (e.Index >= 0 && srcReg[e.Index] == 0)
                        lvlStacks[sId].Add(e);
            ExpandRegions(expandIters, level, srcReg, srcDist, lvlStacks[sId], false);
            foreach (var e in lvlStacks[sId])
            {
                if (e.Index >= 0 && srcReg[e.Index] == 0)
                {
                    if (FloodRegion(e.X, e.Z, e.Index, level, regionId, srcReg, srcDist, stack))
                        regionId++;
                }
            }
        }
        ExpandRegions(expandIters * 8, 0, srcReg, srcDist, stack, true);
        MaxRegions = regionId;
        MergeAndFilterRegions(minRegionArea, mergeRegionArea, srcReg);
        Reg = srcReg;
    }

    private void PaintRect(int minx, int maxx, int minz, int maxz, ushort regId, ushort[] srcReg)
    {
        for (var z = minz; z < maxz; z++)
        {
            for (var x = minx; x < maxx; x++)
            {
                var cell = x + z * Width;
                for (int i = CellIndex(cell), ni = i + CellCount(cell); i < ni; i++)
                    if (Areas[i] != 0)
                        srcReg[i] = regId;
            }
        }
    }

    private void SortCellsByLevel(ushort startLevel, ushort[] srcReg, int nbStacks, List<LevelEntry>[] stacks, int logLevelsPerStack)
    {
        var start = startLevel >> logLevelsPerStack;
        foreach (var s in stacks)
            s.Clear();
        for (var z = 0; z < Height; z++)
        {
            for (var x = 0; x < Width; x++)
            {
                var cell = x + z * Width;
                for (int i = CellIndex(cell), ni = i + CellCount(cell); i < ni; i++)
                {
                    if (Areas[i] == 0 || srcReg[i] != 0)
                        continue;
                    var lvl = Dist[i] >> logLevelsPerStack;
                    var id = start - lvl;
                    if (id >= nbStacks)
                        continue;
                    if (id < 0)
                        id = 0;
                    stacks[id].Add(new LevelEntry(x, z, i));
                }
            }
        }
    }

    private void ExpandRegions(int maxIter, ushort level, ushort[] srcReg, ushort[] srcDist, List<LevelEntry> stack, bool fillStack)
    {
        if (fillStack)
        {
            stack.Clear();
            for (var z = 0; z < Height; z++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var cell = x + z * Width;
                    for (int i = CellIndex(cell), ni = i + CellCount(cell); i < ni; i++)
                        if (Dist[i] >= level && srcReg[i] == 0 && Areas[i] != 0)
                            stack.Add(new LevelEntry(x, z, i));
                }
            }
        }
        else
        {
            for (var j = 0; j < stack.Count; j++)
                if (srcReg[stack[j].Index] != 0)
                    stack[j] = stack[j] with { Index = -1 };
        }
        var dirty = new List<(int Index, ushort Region, ushort Distance)>();
        var iter = 0;
        while (stack.Count > 0)
        {
            var failed = 0;
            dirty.Clear();
            for (var j = 0; j < stack.Count; j++)
            {
                var (x, z, i) = (stack[j].X, stack[j].Z, stack[j].Index);
                if (i < 0)
                {
                    failed++;
                    continue;
                }
                var r = srcReg[i];
                ushort d2 = 0xffff;
                var area = Areas[i];
                for (var dir = 0; dir < 4; dir++)
                {
                    if (GetCon(i, dir) == NotConnected)
                        continue;
                    var ai = Neighbour(x, z, i, dir);
                    if (Areas[ai] != area)
                        continue;
                    if (srcReg[ai] > 0 && (srcReg[ai] & BorderReg) == 0 && srcDist[ai] + 2 < d2)
                    {
                        r = srcReg[ai];
                        d2 = (ushort)(srcDist[ai] + 2);
                    }
                }
                if (r != 0)
                {
                    stack[j] = stack[j] with { Index = -1 };
                    dirty.Add((i, r, d2));
                }
                else
                    failed++;
            }
            foreach (var (idx, region, distance) in dirty)
            {
                srcReg[idx] = region;
                srcDist[idx] = distance;
            }
            if (failed == stack.Count)
                break;
            if (level > 0)
            {
                iter++;
                if (iter >= maxIter)
                    break;
            }
        }
    }

    private bool FloodRegion(int x, int z, int i, ushort level, ushort r, ushort[] srcReg, ushort[] srcDist, List<LevelEntry> stack)
    {
        var area = Areas[i];
        stack.Clear();
        stack.Add(new LevelEntry(x, z, i));
        srcReg[i] = r;
        srcDist[i] = 0;
        var lev = level >= 2 ? level - 2 : 0;
        var count = 0;
        while (stack.Count > 0)
        {
            var back = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            var (cx, cz, ci) = (back.X, back.Z, back.Index);
            ushort ar = 0;
            for (var dir = 0; dir < 4; dir++)
            {
                if (GetCon(ci, dir) == NotConnected)
                    continue;
                int ax = cx + DirX[dir], az = cz + DirZ[dir];
                var ai = Neighbour(cx, cz, ci, dir);
                if (Areas[ai] != area)
                    continue;
                var nr = srcReg[ai];
                if ((nr & BorderReg) != 0)
                    continue;
                if (nr != 0 && nr != r)
                {
                    ar = nr;
                    break;
                }
                var dir2 = (dir + 1) & 3;
                if (GetCon(ai, dir2) != NotConnected)
                {
                    var ai2 = Neighbour(ax, az, ai, dir2);
                    if (Areas[ai2] != area)
                        continue;
                    var nr2 = srcReg[ai2];
                    if (nr2 != 0 && nr2 != r)
                    {
                        ar = nr2;
                        break;
                    }
                }
            }
            if (ar != 0)
            {
                srcReg[ci] = 0;
                continue;
            }
            count++;
            for (var dir = 0; dir < 4; dir++)
            {
                if (GetCon(ci, dir) == NotConnected)
                    continue;
                int ax = cx + DirX[dir], az = cz + DirZ[dir];
                var ai = Neighbour(cx, cz, ci, dir);
                if (Areas[ai] != area)
                    continue;
                if (Dist[ai] >= lev && srcReg[ai] == 0)
                {
                    srcReg[ai] = r;
                    srcDist[ai] = 0;
                    stack.Add(new LevelEntry(ax, az, ai));
                }
            }
        }
        return count > 0;
    }

    private bool IsSolidEdge(ushort[] srcReg, int x, int z, int i, int dir)
    {
        ushort r = 0;
        if (GetCon(i, dir) != NotConnected)
            r = srcReg[Neighbour(x, z, i, dir)];
        return r != srcReg[i];
    }

    private static void RemoveAdjacentDuplicates(List<int> cont)
    {
        if (cont.Count <= 1)
            return;
        for (var j = 0; j < cont.Count;)
        {
            var nj = (j + 1) % cont.Count;
            if (cont[j] == cont[nj])
                cont.RemoveAt(j);
            else
                j++;
        }
    }

    private void WalkContour(int x, int z, int i, int dir, ushort[] srcReg, List<int> cont)
    {
        var startDir = dir;
        var starti = i;
        ushort curReg = 0;
        if (GetCon(i, dir) != NotConnected)
            curReg = srcReg[Neighbour(x, z, i, dir)];
        cont.Add(curReg);
        var iter = 0;
        while (++iter < 40000)
        {
            if (IsSolidEdge(srcReg, x, z, i, dir))
            {
                ushort r = 0;
                if (GetCon(i, dir) != NotConnected)
                    r = srcReg[Neighbour(x, z, i, dir)];
                if (r != curReg)
                {
                    curReg = r;
                    cont.Add(curReg);
                }
                dir = (dir + 1) & 3;
            }
            else
            {
                if (GetCon(i, dir) == NotConnected)
                    return;
                var ni = Neighbour(x, z, i, dir);
                x += DirX[dir];
                z += DirZ[dir];
                i = ni;
                dir = (dir + 3) & 3;
            }
            if (starti == i && startDir == dir)
                break;
        }
        RemoveAdjacentDuplicates(cont);
    }

    private static bool CanMergeWithRegion(Region a, Region b)
    {
        if (a.AreaType != b.AreaType)
            return false;
        var n = 0;
        foreach (var c in a.Connections)
            if (c == b.Id)
                n++;
        if (n > 1)
            return false;
        foreach (var f in a.Floors)
            if (f == b.Id)
                return false;
        return true;
    }

    private static void AddUniqueFloor(Region reg, int n)
    {
        if (!reg.Floors.Contains(n))
            reg.Floors.Add(n);
    }

    private static bool MergeRegions(Region a, Region b)
    {
        int aid = a.Id, bid = b.Id;
        var acon = new List<int>(a.Connections);
        var bcon = b.Connections;
        var insa = acon.IndexOf(bid);
        if (insa == -1)
            return false;
        var insb = bcon.IndexOf(aid);
        if (insb == -1)
            return false;
        a.Connections.Clear();
        for (int i = 0, ni = acon.Count; i < ni - 1; i++)
            a.Connections.Add(acon[(insa + 1 + i) % ni]);
        for (int i = 0, ni = bcon.Count; i < ni - 1; i++)
            a.Connections.Add(bcon[(insb + 1 + i) % ni]);
        RemoveAdjacentNeighbours(a.Connections);
        foreach (var f in b.Floors)
            AddUniqueFloor(a, f);
        a.SpanCount += b.SpanCount;
        b.SpanCount = 0;
        b.Connections.Clear();
        return true;
    }

    // removeAdjacentNeighbours: as the duplicate removal, but stops at one entry.
    private static void RemoveAdjacentNeighbours(List<int> c)
    {
        for (var i = 0; i < c.Count && c.Count > 1;)
        {
            var ni = (i + 1) % c.Count;
            if (c[i] == c[ni])
                c.RemoveAt(i);
            else
                i++;
        }
    }

    private void MergeAndFilterRegions(int minRegionArea, int mergeRegionSize, ushort[] srcReg)
    {
        var nreg = MaxRegions + 1;
        var regions = new Region[nreg];
        for (var i = 0; i < nreg; i++)
            regions[i] = new Region { Id = (ushort)i };
        for (var z = 0; z < Height; z++)
        {
            for (var x = 0; x < Width; x++)
            {
                var cell = x + z * Width;
                int start = CellIndex(cell), end = start + CellCount(cell);
                for (var i = start; i < end; i++)
                {
                    var r = srcReg[i];
                    if (r == 0 || r >= nreg)
                        continue;
                    var reg = regions[r];
                    reg.SpanCount++;
                    for (var j = start; j < end; j++)
                    {
                        if (i == j)
                            continue;
                        var floorId = srcReg[j];
                        if (floorId == 0 || floorId >= nreg)
                            continue;
                        if (floorId == r)
                            reg.Overlap = true;
                        AddUniqueFloor(reg, floorId);
                    }
                    if (reg.Connections.Count > 0)
                        continue;
                    reg.AreaType = Areas[i];
                    for (var dir = 0; dir < 4; dir++)
                    {
                        if (IsSolidEdge(srcReg, x, z, i, dir))
                        {
                            WalkContour(x, z, i, dir, srcReg, reg.Connections);
                            break;
                        }
                    }
                }
            }
        }
        var stack = new List<int>();
        var trace = new List<int>();
        for (var i = 0; i < nreg; i++)
        {
            var reg = regions[i];
            if (reg.Id == 0 || (reg.Id & BorderReg) != 0 || reg.SpanCount == 0 || reg.Visited)
                continue;
            var connectsToBorder = false;
            var spanCount = 0;
            stack.Clear();
            trace.Clear();
            reg.Visited = true;
            stack.Add(i);
            while (stack.Count > 0)
            {
                var ri = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                var creg = regions[ri];
                spanCount += creg.SpanCount;
                trace.Add(ri);
                foreach (var c in creg.Connections)
                {
                    if ((c & BorderReg) != 0)
                    {
                        connectsToBorder = true;
                        continue;
                    }
                    var nei = regions[c];
                    if (nei.Visited || nei.Id == 0 || (nei.Id & BorderReg) != 0)
                        continue;
                    stack.Add(nei.Id);
                    nei.Visited = true;
                }
            }
            if (spanCount < minRegionArea && !connectsToBorder)
            {
                foreach (var t in trace)
                {
                    regions[t].SpanCount = 0;
                    regions[t].Id = 0;
                }
            }
        }
        int mergeCount;
        do
        {
            mergeCount = 0;
            for (var i = 0; i < nreg; i++)
            {
                var reg = regions[i];
                if (reg.Id == 0 || (reg.Id & BorderReg) != 0 || reg.Overlap || reg.SpanCount == 0)
                    continue;
                if (reg.SpanCount > mergeRegionSize && reg.Connections.Contains(0))
                    continue;
                var smallest = 0xfffffff;
                var mergeId = reg.Id;
                foreach (var c in reg.Connections)
                {
                    if ((c & BorderReg) != 0)
                        continue;
                    var m = regions[c];
                    if (m.Id == 0 || (m.Id & BorderReg) != 0 || m.Overlap)
                        continue;
                    if (m.SpanCount < smallest && CanMergeWithRegion(reg, m) && CanMergeWithRegion(m, reg))
                    {
                        smallest = m.SpanCount;
                        mergeId = m.Id;
                    }
                }
                if (mergeId == reg.Id)
                    continue;
                var oldId = reg.Id;
                if (!MergeRegions(regions[mergeId], reg))
                    continue;
                for (var j = 0; j < nreg; j++)
                {
                    var rj = regions[j];
                    if (rj.Id == 0 || (rj.Id & BorderReg) != 0)
                        continue;
                    if (rj.Id == oldId)
                        rj.Id = mergeId;
                    var changed = false;
                    for (var k = 0; k < rj.Connections.Count; k++)
                    {
                        if (rj.Connections[k] == oldId)
                        {
                            rj.Connections[k] = mergeId;
                            changed = true;
                        }
                    }
                    for (var k = 0; k < rj.Floors.Count; k++)
                        if (rj.Floors[k] == oldId)
                            rj.Floors[k] = mergeId;
                    if (changed)
                        RemoveAdjacentNeighbours(rj.Connections);
                }
                mergeCount++;
            }
        }
        while (mergeCount > 0);
        foreach (var reg in regions)
            reg.Remap = reg.Id != 0 && (reg.Id & BorderReg) == 0;
        ushort regIdGen = 0;
        for (var i = 0; i < nreg; i++)
        {
            if (!regions[i].Remap)
                continue;
            var oldId = regions[i].Id;
            var newId = ++regIdGen;
            for (var j = i; j < nreg; j++)
            {
                if (regions[j].Id == oldId)
                {
                    regions[j].Id = newId;
                    regions[j].Remap = false;
                }
            }
        }
        MaxRegions = regIdGen;
        for (var i = 0; i < SpanCount; i++)
            if ((srcReg[i] & BorderReg) == 0)
                srcReg[i] = regions[srcReg[i]].Id;
    }
}
