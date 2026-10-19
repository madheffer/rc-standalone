namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// rcPolyMesh and rcBuildPolyMesh (1800b5ce0): triangulate 1800b8130, getPolyMergeValue 1800b5780,
/// canRemoveVertex 1800b5120, removeVertex 1800b6b50, buildMeshAdjacency 1800b4e50. Recast 1.5's,
/// except the vertex bucket: Valve hashes (x * 0x343 + z * 799) &amp; 0xfff.
/// </summary>
public sealed class RecastPolyMesh
{
    public const ushort NullIdx = 0xffff;

    public ushort[] Verts = [];
    public ushort[] Polys = [];
    public ushort[] Regs = [];
    public ushort[] Flags = [];
    public byte[] Areas = [];
    public int NVerts, NPolys, MaxPolys, Nvp;
    public float[] BMin = new float[3], BMax = new float[3];
    public float Cs, Ch, MaxEdgeError;
    public int BorderSize;

    private static int Next(int i, int n) => i + 1 < n ? i + 1 : 0;

    private static int Prev(int i, int n) => i - 1 >= 0 ? i - 1 : n - 1;

    private static int Area2(int[] v, int a, int b, int c)
        => (v[b] - v[a]) * (v[c + 2] - v[a + 2]) - (v[c] - v[a]) * (v[b + 2] - v[a + 2]);

    private static bool Left(int[] v, int a, int b, int c) => Area2(v, a, b, c) < 0;

    private static bool LeftOn(int[] v, int a, int b, int c) => Area2(v, a, b, c) <= 0;

    private static bool Collinear(int[] v, int a, int b, int c) => Area2(v, a, b, c) == 0;

    private static bool IntersectProp(int[] v, int a, int b, int c, int d)
    {
        if (Collinear(v, a, b, c) || Collinear(v, a, b, d) || Collinear(v, c, d, a) || Collinear(v, c, d, b))
            return false;
        return (Left(v, a, b, c) ^ Left(v, a, b, d)) && (Left(v, c, d, a) ^ Left(v, c, d, b));
    }

    private static bool Between(int[] v, int a, int b, int c)
    {
        if (!Collinear(v, a, b, c))
            return false;
        if (v[a] != v[b])
            return (v[a] <= v[c] && v[c] <= v[b]) || (v[a] >= v[c] && v[c] >= v[b]);
        return (v[a + 2] <= v[c + 2] && v[c + 2] <= v[b + 2]) || (v[a + 2] >= v[c + 2] && v[c + 2] >= v[b + 2]);
    }

    private static bool Intersect(int[] v, int a, int b, int c, int d)
        => IntersectProp(v, a, b, c, d) || Between(v, a, b, c) || Between(v, a, b, d) || Between(v, c, d, a) || Between(v, c, d, b);

    private static bool Vequal(int[] v, int a, int b) => v[a] == v[b] && v[a + 2] == v[b + 2];

    private static int V(int[] indices, int i) => (indices[i] & 0x0fffffff) * 4;

    private static bool Diagonalie(int i, int j, int n, int[] verts, int[] indices)
    {
        int d0 = V(indices, i), d1 = V(indices, j);
        for (var k = 0; k < n; k++)
        {
            var k1 = Next(k, n);
            if (!(k == i || k1 == i || k == j || k1 == j))
            {
                int p0 = V(indices, k), p1 = V(indices, k1);
                if (Vequal(verts, d0, p0) || Vequal(verts, d1, p0) || Vequal(verts, d0, p1) || Vequal(verts, d1, p1))
                    continue;
                if (Intersect(verts, d0, d1, p0, p1))
                    return false;
            }
        }
        return true;
    }

    private static bool InCone(int i, int j, int n, int[] verts, int[] indices)
    {
        int pi = V(indices, i), pj = V(indices, j), pi1 = V(indices, Next(i, n)), pin1 = V(indices, Prev(i, n));
        if (LeftOn(verts, pin1, pi, pi1))
            return Left(verts, pi, pj, pin1) && Left(verts, pj, pi, pi1);
        return !(LeftOn(verts, pi, pj, pi1) && LeftOn(verts, pj, pi, pin1));
    }

    private static bool Diagonal(int i, int j, int n, int[] verts, int[] indices)
        => InCone(i, j, n, verts, indices) && Diagonalie(i, j, n, verts, indices);

    private static bool DiagonalieLoose(int i, int j, int n, int[] verts, int[] indices)
    {
        int d0 = V(indices, i), d1 = V(indices, j);
        for (var k = 0; k < n; k++)
        {
            var k1 = Next(k, n);
            if (!(k == i || k1 == i || k == j || k1 == j))
            {
                int p0 = V(indices, k), p1 = V(indices, k1);
                if (Vequal(verts, d0, p0) || Vequal(verts, d1, p0) || Vequal(verts, d0, p1) || Vequal(verts, d1, p1))
                    continue;
                if (IntersectProp(verts, d0, d1, p0, p1))
                    return false;
            }
        }
        return true;
    }

    private static bool InConeLoose(int i, int j, int n, int[] verts, int[] indices)
    {
        int pi = V(indices, i), pj = V(indices, j), pi1 = V(indices, Next(i, n)), pin1 = V(indices, Prev(i, n));
        if (LeftOn(verts, pin1, pi, pi1))
            return LeftOn(verts, pi, pj, pin1) && LeftOn(verts, pj, pi, pi1);
        return !(LeftOn(verts, pi, pj, pi1) && LeftOn(verts, pj, pi, pin1));
    }

    private static bool DiagonalLoose(int i, int j, int n, int[] verts, int[] indices)
        => InConeLoose(i, j, n, verts, indices) && DiagonalieLoose(i, j, n, verts, indices);

    // 1800b8130.
    internal static int Triangulate(int n, int[] verts, int[] indices, int[] tris)
    {
        var ntris = 0;
        var dst = 0;
        for (var i = 0; i < n; i++)
        {
            var i1 = Next(i, n);
            var i2 = Next(i1, n);
            if (Diagonal(i, i2, n, verts, indices))
                indices[i1] |= unchecked((int)0x80000000);
        }
        while (n > 3)
        {
            var minLen = -1;
            var mini = -1;
            for (var i = 0; i < n; i++)
            {
                var i1 = Next(i, n);
                if ((indices[i1] & unchecked((int)0x80000000)) != 0)
                {
                    int p0 = V(indices, i), p2 = V(indices, Next(i1, n));
                    var dx = verts[p2] - verts[p0];
                    var dz = verts[p2 + 2] - verts[p0 + 2];
                    var len = dx * dx + dz * dz;
                    if (minLen < 0 || len < minLen)
                    {
                        minLen = len;
                        mini = i;
                    }
                }
            }
            if (mini == -1)
            {
                minLen = -1;
                mini = -1;
                for (var i = 0; i < n; i++)
                {
                    var i1 = Next(i, n);
                    var i2 = Next(i1, n);
                    if (DiagonalLoose(i, i2, n, verts, indices))
                    {
                        // Recast 1.5's slip: the far end is next(i2), not i2.
                        int p0 = V(indices, i), p2 = V(indices, Next(i2, n));
                        var dx = verts[p2] - verts[p0];
                        var dz = verts[p2 + 2] - verts[p0 + 2];
                        var len = dx * dx + dz * dz;
                        if (minLen < 0 || len < minLen)
                        {
                            minLen = len;
                            mini = i;
                        }
                    }
                }
                if (mini == -1)
                    return -ntris;
            }
            {
                var i = mini;
                var i1 = Next(i, n);
                var i2 = Next(i1, n);
                tris[dst++] = indices[i] & 0x0fffffff;
                tris[dst++] = indices[i1] & 0x0fffffff;
                tris[dst++] = indices[i2] & 0x0fffffff;
                ntris++;
                n--;
                for (var k = i1; k < n; k++)
                    indices[k] = indices[k + 1];
                if (i1 >= n)
                    i1 = 0;
                i = Prev(i1, n);
                if (Diagonal(Prev(i, n), i1, n, verts, indices))
                    indices[i] |= unchecked((int)0x80000000);
                else
                    indices[i] &= 0x0fffffff;
                if (Diagonal(i, Next(i1, n), n, verts, indices))
                    indices[i1] |= unchecked((int)0x80000000);
                else
                    indices[i1] &= 0x0fffffff;
            }
        }
        tris[dst++] = indices[0] & 0x0fffffff;
        tris[dst++] = indices[1] & 0x0fffffff;
        tris[dst] = indices[2] & 0x0fffffff;
        ntris++;
        return ntris;
    }

    private static int CountPolyVerts(ushort[] p, int at, int nvp)
    {
        for (var i = 0; i < nvp; i++)
            if (p[at + i] == NullIdx)
                return i;
        return nvp;
    }

    private static bool ULeft(ushort[] v, int a, int b, int c)
        => (v[b] - v[a]) * (v[c + 2] - v[a + 2]) - (v[c] - v[a]) * (v[b + 2] - v[a + 2]) < 0;

    // 1800b5780.
    private static int GetPolyMergeValue(ushort[] polys, int pa, int pb, ushort[] verts, out int ea, out int eb, int nvp)
    {
        var na = CountPolyVerts(polys, pa, nvp);
        var nb = CountPolyVerts(polys, pb, nvp);
        ea = -1;
        eb = -1;
        if (na + nb - 2 > nvp)
            return -1;
        for (var i = 0; i < na; i++)
        {
            int va0 = polys[pa + i], va1 = polys[pa + (i + 1) % na];
            if (va0 > va1)
                (va0, va1) = (va1, va0);
            for (var j = 0; j < nb; j++)
            {
                int vb0 = polys[pb + j], vb1 = polys[pb + (j + 1) % nb];
                if (vb0 > vb1)
                    (vb0, vb1) = (vb1, vb0);
                if (va0 == vb0 && va1 == vb1)
                {
                    ea = i;
                    eb = j;
                    break;
                }
            }
        }
        if (ea == -1 || eb == -1)
            return -1;
        int va = polys[pa + (ea + na - 1) % na], vb = polys[pa + ea], vc = polys[pb + (eb + 2) % nb];
        if (!ULeft(verts, va * 3, vb * 3, vc * 3))
            return -1;
        va = polys[pb + (eb + nb - 1) % nb];
        vb = polys[pb + eb];
        vc = polys[pa + (ea + 2) % na];
        if (!ULeft(verts, va * 3, vb * 3, vc * 3))
            return -1;
        va = polys[pa + ea];
        vb = polys[pa + (ea + 1) % na];
        var dx = verts[va * 3] - verts[vb * 3];
        var dz = verts[va * 3 + 2] - verts[vb * 3 + 2];
        return dx * dx + dz * dz;
    }

    private static void MergePolyVerts(ushort[] polys, int pa, int pb, int ea, int eb, ushort[] tmp, int nvp)
    {
        var na = CountPolyVerts(polys, pa, nvp);
        var nb = CountPolyVerts(polys, pb, nvp);
        Array.Fill(tmp, NullIdx);
        var n = 0;
        for (var i = 0; i < na - 1; i++)
            tmp[n++] = polys[pa + (ea + 1 + i) % na];
        for (var i = 0; i < nb - 1; i++)
            tmp[n++] = polys[pb + (eb + 1 + i) % nb];
        Array.Copy(tmp, 0, polys, pa, nvp);
    }

    private static ushort AddVertex(ushort x, ushort y, ushort z, ushort[] verts, int[] firstVert, int[] nextVert, ref int nv)
    {
        var bucket = (int)((uint)(x * 0x343 + z * 799) & 0xfff);
        var i = firstVert[bucket];
        while (i != -1)
        {
            if (verts[i * 3] == x && Math.Abs(verts[i * 3 + 1] - y) <= 2 && verts[i * 3 + 2] == z)
                return (ushort)i;
            i = nextVert[i];
        }
        i = nv++;
        verts[i * 3] = x;
        verts[i * 3 + 1] = y;
        verts[i * 3 + 2] = z;
        nextVert[i] = firstVert[bucket];
        firstVert[bucket] = i;
        return (ushort)i;
    }

    public static RecastPolyMesh Build(RecastContourSet cset, int nvp)
    {
        var mesh = new RecastPolyMesh
        {
            BMin = [cset.BMin[0], cset.BMin[1], cset.BMin[2]],
            BMax = [cset.BMax[0], cset.BMax[1], cset.BMax[2]],
            Cs = cset.Cs,
            Ch = cset.Ch,
            BorderSize = cset.BorderSize,
            MaxEdgeError = cset.MaxError,
        };
        int maxVertices = 0, maxTris = 0, maxVertsPerCont = 0;
        foreach (var c in cset.Contours)
        {
            if (c.NVerts < 3)
                continue;
            maxVertices += c.NVerts;
            maxTris += c.NVerts - 2;
            maxVertsPerCont = Math.Max(maxVertsPerCont, c.NVerts);
        }
        if (maxVertices >= 0xfffe)
            throw new InvalidOperationException($"rcBuildPolyMesh: too many vertices {maxVertices}");
        var vflags = new byte[maxVertices + 1];
        mesh.Verts = new ushort[maxVertices * 3];
        mesh.Polys = new ushort[maxTris * nvp * 2];
        Array.Fill(mesh.Polys, NullIdx);
        mesh.Regs = new ushort[maxTris];
        mesh.Areas = new byte[maxTris];
        mesh.NVerts = 0;
        mesh.NPolys = 0;
        mesh.Nvp = nvp;
        mesh.MaxPolys = maxTris;
        var nextVert = new int[maxVertices];
        var firstVert = new int[0x1000];
        Array.Fill(firstVert, -1);
        var indices = new int[maxVertsPerCont];
        var tris = new int[maxVertsPerCont * 3];
        var polys = new ushort[(maxVertsPerCont + 1) * nvp];
        var tmp = new ushort[nvp];
        var nv = 0;
        foreach (var cont in cset.Contours)
        {
            if (cont.NVerts < 3)
                continue;
            for (var j = 0; j < cont.NVerts; j++)
                indices[j] = j;
            var ntris = Triangulate(cont.NVerts, cont.Verts, indices, tris);
            if (ntris <= 0)
                ntris = -ntris;
            for (var j = 0; j < cont.NVerts; j++)
            {
                var v = j * 4;
                indices[j] = AddVertex((ushort)cont.Verts[v], (ushort)cont.Verts[v + 1], (ushort)cont.Verts[v + 2], mesh.Verts, firstVert, nextVert, ref nv);
                if ((cont.Verts[v + 3] & RecastContourSet.BorderVertex) != 0)
                    vflags[indices[j]] = 1;
            }
            var npolys = 0;
            Array.Fill(polys, NullIdx, 0, maxVertsPerCont * nvp);
            for (var j = 0; j < ntris; j++)
            {
                int t0 = tris[j * 3], t1 = tris[j * 3 + 1], t2 = tris[j * 3 + 2];
                if (t0 != t1 && t0 != t2 && t1 != t2)
                {
                    polys[npolys * nvp] = (ushort)indices[t0];
                    polys[npolys * nvp + 1] = (ushort)indices[t1];
                    polys[npolys * nvp + 2] = (ushort)indices[t2];
                    npolys++;
                }
            }
            if (npolys == 0)
                continue;
            if (nvp > 3)
            {
                while (true)
                {
                    int bestMergeVal = 0, bestPa = 0, bestPb = 0, bestEa = 0, bestEb = 0;
                    for (var j = 0; j < npolys - 1; j++)
                    {
                        for (var k = j + 1; k < npolys; k++)
                        {
                            var v = GetPolyMergeValue(polys, j * nvp, k * nvp, mesh.Verts, out var ea, out var eb, nvp);
                            if (v > bestMergeVal)
                            {
                                bestMergeVal = v;
                                bestPa = j;
                                bestPb = k;
                                bestEa = ea;
                                bestEb = eb;
                            }
                        }
                    }
                    if (bestMergeVal <= 0)
                        break;
                    MergePolyVerts(polys, bestPa * nvp, bestPb * nvp, bestEa, bestEb, tmp, nvp);
                    var last = (npolys - 1) * nvp;
                    if (bestPb * nvp != last)
                        Array.Copy(polys, last, polys, bestPb * nvp, nvp);
                    npolys--;
                }
            }
            for (var j = 0; j < npolys; j++)
            {
                Array.Copy(polys, j * nvp, mesh.Polys, mesh.NPolys * nvp * 2, nvp);
                mesh.Regs[mesh.NPolys] = cont.Reg;
                mesh.Areas[mesh.NPolys] = cont.Area;
                mesh.NPolys++;
                if (mesh.NPolys > maxTris)
                    throw new InvalidOperationException("rcBuildPolyMesh: too many polygons");
            }
        }
        mesh.NVerts = nv;
        for (var i = 0; i < mesh.NVerts; i++)
        {
            if (vflags[i] == 0)
                continue;
            if (!mesh.CanRemoveVertex((ushort)i))
                continue;
            if (!mesh.RemoveVertex((ushort)i, maxTris))
                throw new InvalidOperationException($"rcBuildPolyMesh: failed to remove edge vertex {i}");
            for (var j = i; j < mesh.NVerts; j++)
                vflags[j] = vflags[j + 1];
            i--;
        }
        mesh.BuildMeshAdjacency();
        if (mesh.BorderSize > 0)
        {
            int w = cset.Width, h = cset.Height;
            for (var i = 0; i < mesh.NPolys; i++)
            {
                var p = i * 2 * nvp;
                for (var j = 0; j < nvp; j++)
                {
                    if (mesh.Polys[p + j] == NullIdx)
                        break;
                    if (mesh.Polys[p + nvp + j] != NullIdx)
                        continue;
                    var nj = j + 1;
                    if (nj >= nvp || mesh.Polys[p + nj] == NullIdx)
                        nj = 0;
                    int va = mesh.Polys[p + j] * 3, vb = mesh.Polys[p + nj] * 3;
                    if (mesh.Verts[va] == 0 && mesh.Verts[vb] == 0)
                        mesh.Polys[p + nvp + j] = 0x8000;
                    else if (mesh.Verts[va + 2] == h && mesh.Verts[vb + 2] == h)
                        mesh.Polys[p + nvp + j] = 0x8001;
                    else if (mesh.Verts[va] == w && mesh.Verts[vb] == w)
                        mesh.Polys[p + nvp + j] = 0x8002;
                    else if (mesh.Verts[va + 2] == 0 && mesh.Verts[vb + 2] == 0)
                        mesh.Polys[p + nvp + j] = 0x8003;
                }
            }
        }
        mesh.Flags = new ushort[mesh.NPolys];
        return mesh;
    }

    // 1800b5120.
    private bool CanRemoveVertex(ushort rem)
    {
        int numTouchedVerts = 0, numRemainingEdges = 0;
        for (var i = 0; i < NPolys; i++)
        {
            var p = i * Nvp * 2;
            var nv = CountPolyVerts(Polys, p, Nvp);
            int numRemoved = 0, numVerts = 0;
            for (var j = 0; j < nv; j++)
            {
                if (Polys[p + j] == rem)
                {
                    numTouchedVerts++;
                    numRemoved++;
                }
                numVerts++;
            }
            if (numRemoved != 0)
                numRemainingEdges += numVerts - (numRemoved + 1);
        }
        if (numRemainingEdges <= 2)
            return false;
        var edges = new List<(int A, int B, int Count)>();
        for (var i = 0; i < NPolys; i++)
        {
            var p = i * Nvp * 2;
            var nv = CountPolyVerts(Polys, p, Nvp);
            for (int j = 0, k = nv - 1; j < nv; k = j++)
            {
                if (Polys[p + j] != rem && Polys[p + k] != rem)
                    continue;
                int a = Polys[p + j], b = Polys[p + k];
                if (b == rem)
                    (a, b) = (b, a);
                var exists = false;
                for (var m = 0; m < edges.Count; m++)
                {
                    if (edges[m].B == b)
                    {
                        edges[m] = edges[m] with { Count = edges[m].Count + 1 };
                        exists = true;
                    }
                }
                if (!exists)
                    edges.Add((a, b, 1));
            }
        }
        var numOpenEdges = 0;
        foreach (var e in edges)
            if (e.Count < 2)
                numOpenEdges++;
        return numOpenEdges <= 2;
    }

    // 1800b6b50.
    private bool RemoveVertex(ushort rem, int maxTris)
    {
        var nvp = Nvp;
        var edges = new List<int[]>();
        for (var i = 0; i < NPolys; i++)
        {
            var p = i * nvp * 2;
            var nv = CountPolyVerts(Polys, p, nvp);
            var hasRem = false;
            for (var j = 0; j < nv; j++)
                if (Polys[p + j] == rem)
                    hasRem = true;
            if (!hasRem)
                continue;
            for (int j = 0, k = nv - 1; j < nv; k = j++)
                if (Polys[p + j] != rem && Polys[p + k] != rem)
                    edges.Add([Polys[p + k], Polys[p + j], Regs[i], Areas[i]]);
            var p2 = (NPolys - 1) * nvp * 2;
            if (p != p2)
                Array.Copy(Polys, p2, Polys, p, nvp);
            Array.Fill(Polys, NullIdx, p + nvp, nvp);
            Regs[i] = Regs[NPolys - 1];
            Areas[i] = Areas[NPolys - 1];
            NPolys--;
            i--;
        }
        for (var i = (int)rem; i < NVerts - 1; i++)
        {
            Verts[i * 3] = Verts[(i + 1) * 3];
            Verts[i * 3 + 1] = Verts[(i + 1) * 3 + 1];
            Verts[i * 3 + 2] = Verts[(i + 1) * 3 + 2];
        }
        NVerts--;
        for (var i = 0; i < NPolys; i++)
        {
            var p = i * nvp * 2;
            var nv = CountPolyVerts(Polys, p, nvp);
            for (var j = 0; j < nv; j++)
                if (Polys[p + j] > rem)
                    Polys[p + j]--;
        }
        foreach (var e in edges)
        {
            if (e[0] > rem)
                e[0]--;
            if (e[1] > rem)
                e[1]--;
        }
        if (edges.Count == 0)
            return true;
        var hole = new List<int> { edges[0][0] };
        var hreg = new List<int> { edges[0][2] };
        var harea = new List<int> { edges[0][3] };
        while (edges.Count > 0)
        {
            var match = false;
            for (var i = 0; i < edges.Count; i++)
            {
                int ea = edges[i][0], eb = edges[i][1], r = edges[i][2], a = edges[i][3];
                var add = false;
                if (hole[0] == eb)
                {
                    hole.Insert(0, ea);
                    hreg.Insert(0, r);
                    harea.Insert(0, a);
                    add = true;
                }
                else if (hole[^1] == ea)
                {
                    hole.Add(eb);
                    hreg.Add(r);
                    harea.Add(a);
                    add = true;
                }
                if (add)
                {
                    edges[i] = edges[^1];
                    edges.RemoveAt(edges.Count - 1);
                    match = true;
                    i--;
                }
            }
            if (!match)
                break;
        }
        var nhole = hole.Count;
        var tris = new int[nhole * 3];
        var tverts = new int[nhole * 4];
        var thole = new int[nhole];
        for (var i = 0; i < nhole; i++)
        {
            var pi = hole[i];
            tverts[i * 4] = Verts[pi * 3];
            tverts[i * 4 + 1] = Verts[pi * 3 + 1];
            tverts[i * 4 + 2] = Verts[pi * 3 + 2];
            thole[i] = i;
        }
        var ntris = Triangulate(nhole, tverts, thole, tris);
        if (ntris < 0)
            ntris = -ntris;
        var polys = new ushort[(ntris + 1) * nvp];
        var pregs = new ushort[ntris];
        var pareas = new byte[ntris];
        var tmp = new ushort[nvp];
        var npolys = 0;
        Array.Fill(polys, NullIdx, 0, ntris * nvp);
        for (var j = 0; j < ntris; j++)
        {
            int t0 = tris[j * 3], t1 = tris[j * 3 + 1], t2 = tris[j * 3 + 2];
            if (t0 != t1 && t0 != t2 && t1 != t2)
            {
                polys[npolys * nvp] = (ushort)hole[t0];
                polys[npolys * nvp + 1] = (ushort)hole[t1];
                polys[npolys * nvp + 2] = (ushort)hole[t2];
                // Valve's (Recast 1.4's): the first hole vertex's region, no multiple-region mark.
                pregs[npolys] = (ushort)hreg[t0];
                pareas[npolys] = (byte)harea[t0];
                npolys++;
            }
        }
        if (npolys == 0)
            return true;
        if (nvp > 3)
        {
            while (true)
            {
                int bestMergeVal = 0, bestPa = 0, bestPb = 0, bestEa = 0, bestEb = 0;
                for (var j = 0; j < npolys - 1; j++)
                {
                    for (var k = j + 1; k < npolys; k++)
                    {
                        var v = GetPolyMergeValue(polys, j * nvp, k * nvp, Verts, out var ea, out var eb, nvp);
                        if (v > bestMergeVal)
                        {
                            bestMergeVal = v;
                            bestPa = j;
                            bestPb = k;
                            bestEa = ea;
                            bestEb = eb;
                        }
                    }
                }
                if (bestMergeVal <= 0)
                    break;
                MergePolyVerts(polys, bestPa * nvp, bestPb * nvp, bestEa, bestEb, tmp, nvp);
                var last = (npolys - 1) * nvp;
                if (bestPb * nvp != last)
                    Array.Copy(polys, last, polys, bestPb * nvp, nvp);
                pregs[bestPb] = pregs[npolys - 1];
                pareas[bestPb] = pareas[npolys - 1];
                npolys--;
            }
        }
        for (var i = 0; i < npolys; i++)
        {
            if (NPolys >= maxTris)
                break;
            var p = NPolys * nvp * 2;
            Array.Fill(Polys, NullIdx, p, nvp * 2);
            Array.Copy(polys, i * nvp, Polys, p, nvp);
            Regs[NPolys] = pregs[i];
            Areas[NPolys] = pareas[i];
            NPolys++;
            if (NPolys > maxTris)
                return false;
        }
        return true;
    }

    // 1800b4e50.
    private void BuildMeshAdjacency()
    {
        var nvp = Nvp;
        var maxEdgeCount = NPolys * nvp;
        var firstEdge = new ushort[NVerts + maxEdgeCount];
        Array.Fill(firstEdge, NullIdx, 0, NVerts);
        var edges = new (ushort V0, ushort V1, ushort E0, ushort E1, ushort P0, ushort P1)[maxEdgeCount];
        var edgeCount = 0;
        for (var i = 0; i < NPolys; i++)
        {
            var t = i * nvp * 2;
            for (var j = 0; j < nvp; j++)
            {
                if (Polys[t + j] == NullIdx)
                    break;
                var v0 = Polys[t + j];
                var v1 = j + 1 >= nvp || Polys[t + j + 1] == NullIdx ? Polys[t] : Polys[t + j + 1];
                if (v0 < v1)
                {
                    edges[edgeCount] = (v0, v1, (ushort)j, 0, (ushort)i, (ushort)i);
                    firstEdge[NVerts + edgeCount] = firstEdge[v0];
                    firstEdge[v0] = (ushort)edgeCount;
                    edgeCount++;
                }
            }
        }
        for (var i = 0; i < NPolys; i++)
        {
            var t = i * nvp * 2;
            for (var j = 0; j < nvp; j++)
            {
                if (Polys[t + j] == NullIdx)
                    break;
                var v0 = Polys[t + j];
                var v1 = j + 1 >= nvp || Polys[t + j + 1] == NullIdx ? Polys[t] : Polys[t + j + 1];
                if (v0 > v1)
                {
                    for (var e = firstEdge[v1]; e != NullIdx; e = firstEdge[NVerts + e])
                    {
                        if (edges[e].V1 == v0 && edges[e].P0 == edges[e].P1)
                        {
                            edges[e].P1 = (ushort)i;
                            edges[e].E1 = (ushort)j;
                            break;
                        }
                    }
                }
            }
        }
        for (var i = 0; i < edgeCount; i++)
        {
            var e = edges[i];
            if (e.P0 != e.P1)
            {
                Polys[e.P0 * nvp * 2 + nvp + e.E0] = e.P1;
                Polys[e.P1 * nvp * 2 + nvp + e.E1] = e.P0;
            }
        }
    }
}
