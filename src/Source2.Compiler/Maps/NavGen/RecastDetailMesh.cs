namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// rcPolyMeshDetail and rcBuildPolyMeshDetail (1800bd2e0): getHeightData 1800bb7a0
/// (Recast 1.4's seeding 1800bc070), buildPolyDetail 1800b84c0 (polyMinExtent 1800bce00,
/// getHeight 1800bb450, triangulateHull 1800bdf70, delaunayHull 1800ba500 with completeFacet
/// 1800b9f50, circumCircle 1800b9d40, overlapEdges 1800bcc50, distToTriMesh 1800bb000, distToPoly
/// 1800baa30) and the triangle edge flags 1800bb260. Recast 1.5.0's, with its first
/// triangulateHull (dmin starts at 0, so the hull is fanned from vertex 0), the 8-neighbour
/// getHeight, no setTriFlags, and no multiple-region guard on the height data.
/// </summary>
public sealed class RecastDetailMesh
{
    public uint[] Meshes = [];
    public List<float> Verts = [];
    public List<byte> Tris = [];
    public int NMeshes;

    public int NVerts => Verts.Count / 3;
    public int NTris => Tris.Count / 4;

    private const ushort UnsetHeight = 0xffff;
    private const int MaxVerts = 127, MaxTris = 255, MaxVertsPerEdge = 32;
    private const int EvUndef = -1, EvHull = -2;

    private sealed class HeightPatch
    {
        public ushort[] Data = [];
        public int XMin, YMin, Width, Height;
    }

    private static float Sqrt(float v) => MathF.Sqrt(v);

    private static float Vdist2(float[] p, int pi, float[] q, int qi)
    {
        var dx = q[qi] - p[pi];
        var dz = q[qi + 2] - p[pi + 2];
        return Sqrt(dx * dx + dz * dz);
    }

    private static float Vcross2(float[] a, int ai, float[] b, int bi, float[] c, int ci)
    {
        var u1 = b[bi] - a[ai];
        var v1 = b[bi + 2] - a[ai + 2];
        var u2 = c[ci] - a[ai];
        var v2 = c[ci + 2] - a[ai + 2];
        return u1 * v2 - v1 * u2;
    }

    // 1800b9d40 (Recast 1.5: relative to p1).
    private static bool CircumCircle(float[] pts, int p1, int p2, int p3, float[] c, out float r)
    {
        float v1x = 0f, v1z = 0f;
        float v2x = pts[p2] - pts[p1], v2z = pts[p2 + 2] - pts[p1 + 2];
        float v3x = pts[p3] - pts[p1], v3z = pts[p3 + 2] - pts[p1 + 2];
        var cp = (v2x - v1x) * (v3z - v1z) - (v2z - v1z) * (v3x - v1x);
        if (MathF.Abs(cp) > 1e-6f)
        {
            var v1Sq = v1x * v1x + v1z * v1z;
            var v2Sq = v2x * v2x + v2z * v2z;
            var v3Sq = v3x * v3x + v3z * v3z;
            c[0] = (v1Sq * (v2z - v3z) + v2Sq * (v3z - v1z) + v3Sq * (v1z - v2z)) / (2 * cp);
            c[1] = 0;
            c[2] = (v1Sq * (v3x - v2x) + v2Sq * (v1x - v3x) + v3Sq * (v2x - v1x)) / (2 * cp);
            var dx = v1x - c[0];
            var dz = v1z - c[2];
            r = Sqrt(dx * dx + dz * dz);
            c[0] += pts[p1];
            c[1] += pts[p1 + 1];
            c[2] += pts[p1 + 2];
            return true;
        }
        c[0] = pts[p1];
        c[1] = pts[p1 + 1];
        c[2] = pts[p1 + 2];
        r = 0;
        return false;
    }

    private static float DistPtTri(float[] p, float[] v, int a, int b, int c)
    {
        float v0x = v[c] - v[a], v0y = v[c + 1] - v[a + 1], v0z = v[c + 2] - v[a + 2];
        float v1x = v[b] - v[a], v1y = v[b + 1] - v[a + 1], v1z = v[b + 2] - v[a + 2];
        float v2x = p[0] - v[a], v2z = p[2] - v[a + 2];
        var dot00 = v0x * v0x + v0z * v0z;
        var dot01 = v0x * v1x + v0z * v1z;
        var dot02 = v0x * v2x + v0z * v2z;
        var dot11 = v1x * v1x + v1z * v1z;
        var dot12 = v1x * v2x + v1z * v2z;
        var invDenom = 1.0f / (dot00 * dot11 - dot01 * dot01);
        var u = (dot11 * dot02 - dot01 * dot12) * invDenom;
        var w = (dot00 * dot12 - dot01 * dot02) * invDenom;
        if (u >= -1e-4f && w >= -1e-4f && u + w <= 1 + 1e-4f)
        {
            var y = v[a + 1] + v0y * u + v1y * w;
            return MathF.Abs(y - p[1]);
        }
        return float.MaxValue;
    }

    // 1800bb000.
    private static float DistToTriMesh(float[] p, float[] verts, List<int> tris)
    {
        var dmin = float.MaxValue;
        for (var i = 0; i < tris.Count / 4; i++)
        {
            var d = DistPtTri(p, verts, tris[i * 4] * 3, tris[i * 4 + 1] * 3, tris[i * 4 + 2] * 3);
            if (d < dmin)
                dmin = d;
        }
        return dmin == float.MaxValue ? -1 : dmin;
    }

    private static float DistancePtSeg2d(float ptx, float ptz, float[] v, int p, int q)
    {
        var pqx = v[q] - v[p];
        var pqz = v[q + 2] - v[p + 2];
        var dx = ptx - v[p];
        var dz = ptz - v[p + 2];
        var d = pqx * pqx + pqz * pqz;
        var t = pqx * dx + pqz * dz;
        if (d > 0)
            t /= d;
        if (t < 0)
            t = 0;
        else if (t > 1)
            t = 1;
        dx = v[p] + t * pqx - ptx;
        dz = v[p + 2] + t * pqz - ptz;
        return dx * dx + dz * dz;
    }

    // 1800baa30.
    private static float DistToPoly(int nvert, float[] verts, float[] p)
    {
        var dmin = float.MaxValue;
        var c = false;
        for (int i = 0, j = nvert - 1; i < nvert; j = i++)
        {
            int vi = i * 3, vj = j * 3;
            if ((verts[vi + 2] > p[2]) != (verts[vj + 2] > p[2])
                && p[0] < (verts[vj] - verts[vi]) * (p[2] - verts[vi + 2]) / (verts[vj + 2] - verts[vi + 2]) + verts[vi])
                c = !c;
            dmin = MathF.Min(dmin, DistancePtSeg2d(p[0], p[2], verts, vj, vi));
        }
        return c ? -dmin : dmin;
    }

    // 1800bb450: Recast 1.4's lookup, the 8 neighbours when the cell is unset.
    private static ushort GetHeight(float fx, float fy, float fz, float ics, float ch, HeightPatch hp)
    {
        var ix = (int)MathF.Floor(fx * ics + 0.01f);
        var iz = (int)MathF.Floor(fz * ics + 0.01f);
        ix = Math.Clamp(ix - hp.XMin, 0, hp.Width - 1);
        iz = Math.Clamp(iz - hp.YMin, 0, hp.Height - 1);
        var h = hp.Data[ix + iz * hp.Width];
        if (h == UnsetHeight)
        {
            ReadOnlySpan<int> off = [-1, 0, -1, -1, 0, -1, 1, -1, 1, 0, 1, 1, 0, 1, -1, 1];
            var dmin = float.MaxValue;
            for (var i = 0; i < 8; i++)
            {
                var nx = ix + off[i * 2];
                var nz = iz + off[i * 2 + 1];
                if (nx < 0 || nz < 0 || nx >= hp.Width || nz >= hp.Height)
                    continue;
                var nh = hp.Data[nx + nz * hp.Width];
                if (nh == UnsetHeight)
                    continue;
                var d = MathF.Abs((float)nh * ch - fy);
                if (d < dmin)
                {
                    h = nh;
                    dmin = d;
                }
            }
        }
        return h;
    }

    // 1800bce00.
    private static float PolyMinExtent(float[] verts, int nverts)
    {
        var minDist = float.MaxValue;
        for (var i = 0; i < nverts; i++)
        {
            var ni = (i + 1) % nverts;
            var maxEdgeDist = 0f;
            for (var j = 0; j < nverts; j++)
            {
                if (j == i || j == ni)
                    continue;
                var d = DistancePtSeg2d(verts[j * 3], verts[j * 3 + 2], verts, i * 3, ni * 3);
                if (maxEdgeDist <= d)
                    maxEdgeDist = d;
            }
            if (maxEdgeDist <= minDist)
                minDist = maxEdgeDist;
        }
        return Sqrt(minDist);
    }

    // 1800bdf70: Recast 1.5.0's, dmin starting at 0 (never beaten), so the fan starts at hull[0].
    private static void TriangulateHull(float[] verts, int nhull, int[] hull, List<int> tris)
    {
        int start = 0, left = 1, right = nhull - 1;
        tris.Add(hull[start]);
        tris.Add(hull[left]);
        tris.Add(hull[right]);
        tris.Add(0);
        while ((left + 1 < nhull ? left + 1 : 0) != right)
        {
            var nleft = left + 1 < nhull ? left + 1 : 0;
            var nright = right - 1 >= 0 ? right - 1 : nhull - 1;
            int cvleft = hull[left] * 3, nvleft = hull[nleft] * 3, cvright = hull[right] * 3, nvright = hull[nright] * 3;
            var dleft = Vdist2(verts, cvleft, verts, nvleft) + Vdist2(verts, nvleft, verts, cvright);
            var dright = Vdist2(verts, cvright, verts, nvright) + Vdist2(verts, cvleft, verts, nvright);
            if (dleft < dright)
            {
                tris.Add(hull[left]);
                tris.Add(hull[nleft]);
                tris.Add(hull[right]);
                tris.Add(0);
                left = nleft;
            }
            else
            {
                tris.Add(hull[left]);
                tris.Add(hull[nright]);
                tris.Add(hull[right]);
                tris.Add(0);
                right = nright;
            }
        }
    }

    private static int FindEdge(List<int> edges, int nedges, int s, int t)
    {
        for (var i = 0; i < nedges; i++)
        {
            if ((edges[i * 4] == s && edges[i * 4 + 1] == t) || (edges[i * 4] == t && edges[i * 4 + 1] == s))
                return i;
        }
        return EvUndef;
    }

    private static void AddEdge(List<int> edges, ref int nedges, int maxEdges, int s, int t, int l, int r)
    {
        if (nedges >= maxEdges)
            return;
        if (FindEdge(edges, nedges, s, t) != EvUndef)
            return;
        edges[nedges * 4] = s;
        edges[nedges * 4 + 1] = t;
        edges[nedges * 4 + 2] = l;
        edges[nedges * 4 + 3] = r;
        nedges++;
    }

    private static void UpdateLeftFace(List<int> edges, int e, int s, int t, int f)
    {
        var i = e * 4;
        if (edges[i] == s && edges[i + 1] == t && edges[i + 2] == EvUndef)
            edges[i + 2] = f;
        else if (edges[i + 1] == s && edges[i] == t && edges[i + 3] == EvUndef)
            edges[i + 3] = f;
    }

    private static bool OverlapEdges(float[] pts, List<int> edges, int nedges, int s1, int t1)
    {
        for (var i = 0; i < nedges; i++)
        {
            int s0 = edges[i * 4], t0 = edges[i * 4 + 1];
            if (s0 == s1 || s0 == t1 || t0 == s1 || t0 == t1)
                continue;
            int a = s0 * 3, b = t0 * 3, c = s1 * 3, d = t1 * 3;
            var a1 = Vcross2(pts, a, pts, b, pts, d);
            var a2 = Vcross2(pts, a, pts, b, pts, c);
            if (a1 * a2 < 0.0f)
            {
                var a3 = Vcross2(pts, c, pts, d, pts, a);
                var a4 = a3 + a2 - a1;
                if (a3 * a4 < 0.0f)
                    return true;
            }
        }
        return false;
    }

    // 1800b9f50.
    private static void CompleteFacet(float[] pts, int npts, List<int> edges, ref int nedges, int maxEdges, ref int nfaces, int e)
    {
        int s, t;
        if (edges[e * 4 + 2] == EvUndef)
        {
            s = edges[e * 4];
            t = edges[e * 4 + 1];
        }
        else if (edges[e * 4 + 3] == EvUndef)
        {
            s = edges[e * 4 + 1];
            t = edges[e * 4];
        }
        else
            return;
        var pt = npts;
        var c = new float[3];
        var r = -1f;
        for (var u = 0; u < npts; u++)
        {
            if (u == s || u == t)
                continue;
            if (Vcross2(pts, s * 3, pts, t * 3, pts, u * 3) > 1e-5f)
            {
                if (r < 0)
                {
                    pt = u;
                    CircumCircle(pts, s * 3, t * 3, u * 3, c, out r);
                    continue;
                }
                var d = Vdist2(c, 0, pts, u * 3);
                if (d > r * 1.001f)
                    continue;
                if (d < r * 0.999f)
                {
                    pt = u;
                    CircumCircle(pts, s * 3, t * 3, u * 3, c, out r);
                }
                else
                {
                    if (OverlapEdges(pts, edges, nedges, s, u))
                        continue;
                    if (OverlapEdges(pts, edges, nedges, t, u))
                        continue;
                    pt = u;
                    CircumCircle(pts, s * 3, t * 3, u * 3, c, out r);
                }
            }
        }
        if (pt < npts)
        {
            UpdateLeftFace(edges, e, s, t, nfaces);
            var e2 = FindEdge(edges, nedges, pt, s);
            if (e2 == EvUndef)
                AddEdge(edges, ref nedges, maxEdges, pt, s, nfaces, EvUndef);
            else
                UpdateLeftFace(edges, e2, pt, s, nfaces);
            e2 = FindEdge(edges, nedges, t, pt);
            if (e2 == EvUndef)
                AddEdge(edges, ref nedges, maxEdges, t, pt, nfaces, EvUndef);
            else
                UpdateLeftFace(edges, e2, t, pt, nfaces);
            nfaces++;
        }
        else
            UpdateLeftFace(edges, e, s, t, EvHull);
    }

    // 1800ba500.
    private static void DelaunayHull(int npts, float[] pts, int nhull, int[] hull, List<int> tris, List<int> edges)
    {
        var nfaces = 0;
        var nedges = 0;
        var maxEdges = npts * 10;
        edges.Clear();
        edges.AddRange(new int[maxEdges * 4]);
        for (int i = 0, j = nhull - 1; i < nhull; j = i++)
            AddEdge(edges, ref nedges, maxEdges, hull[j], hull[i], EvHull, EvUndef);
        var currentEdge = 0;
        while (currentEdge < nedges)
        {
            if (edges[currentEdge * 4 + 2] == EvUndef)
                CompleteFacet(pts, npts, edges, ref nedges, maxEdges, ref nfaces, currentEdge);
            if (edges[currentEdge * 4 + 3] == EvUndef)
                CompleteFacet(pts, npts, edges, ref nedges, maxEdges, ref nfaces, currentEdge);
            currentEdge++;
        }
        tris.Clear();
        for (var i = 0; i < nfaces * 4; i++)
            tris.Add(-1);
        for (var i = 0; i < nedges; i++)
        {
            int e0 = edges[i * 4], e1 = edges[i * 4 + 1], e2 = edges[i * 4 + 2], e3 = edges[i * 4 + 3];
            if (e3 >= 0)
            {
                var t = e3 * 4;
                if (tris[t] == -1)
                {
                    tris[t] = e0;
                    tris[t + 1] = e1;
                }
                else if (tris[t] == e1)
                    tris[t + 2] = e0;
                else if (tris[t + 1] == e0)
                    tris[t + 2] = e1;
            }
            if (e2 >= 0)
            {
                var t = e2 * 4;
                if (tris[t] == -1)
                {
                    tris[t] = e1;
                    tris[t + 1] = e0;
                }
                else if (tris[t] == e0)
                    tris[t + 2] = e1;
                else if (tris[t + 1] == e1)
                    tris[t + 2] = e0;
            }
        }
        for (var i = 0; i < tris.Count / 4; i++)
        {
            var t = i * 4;
            if (tris[t] == -1 || tris[t + 1] == -1 || tris[t + 2] == -1)
            {
                var last = tris.Count - 4;
                tris[t] = tris[last];
                tris[t + 1] = tris[last + 1];
                tris[t + 2] = tris[last + 2];
                tris[t + 3] = tris[last + 3];
                tris.RemoveRange(last, 4);
                i--;
            }
        }
    }

    private static float DistancePtSeg3(float[] e, int pt, int p, int q)
    {
        var pqx = e[q] - e[p];
        var pqy = e[q + 1] - e[p + 1];
        var pqz = e[q + 2] - e[p + 2];
        var dx = e[pt] - e[p];
        var dy = e[pt + 1] - e[p + 1];
        var dz = e[pt + 2] - e[p + 2];
        var d = pqx * pqx + pqy * pqy + pqz * pqz;
        var t = pqx * dx + pqy * dy + pqz * dz;
        if (d > 0)
            t /= d;
        if (t < 0)
            t = 0;
        else if (t > 1)
            t = 1;
        dx = e[p] + t * pqx - e[pt];
        dy = e[p + 1] + t * pqy - e[pt + 1];
        dz = e[p + 2] + t * pqz - e[pt + 2];
        return dx * dx + dy * dy + dz * dz;
    }

    // 1800b84c0.
    private static void BuildPolyDetail(float[] input, int nin, float sampleDist, float sampleMaxError, RecastCompact chf, HeightPatch hp,
                                        float[] verts, out int nverts, List<int> tris, List<int> edges, List<int> samples)
    {
        var edge = new float[(MaxVertsPerEdge + 1) * 3];
        var hull = new int[MaxVerts];
        var nhull = 0;
        nverts = nin;
        Array.Copy(input, verts, nin * 3);
        edges.Clear();
        tris.Clear();
        var cs = chf.Cs;
        var ics = 1.0f / cs;
        var minExtent = PolyMinExtent(verts, nverts);
        if (sampleDist > 0)
        {
            for (int i = 0, j = nin - 1; i < nin; j = i++)
            {
                int vj = j * 3, vi = i * 3;
                var swapped = false;
                if (MathF.Abs(input[vj] - input[vi]) < 1e-6f)
                {
                    if (input[vj + 2] > input[vi + 2])
                    {
                        (vj, vi) = (vi, vj);
                        swapped = true;
                    }
                }
                else if (input[vj] > input[vi])
                {
                    (vj, vi) = (vi, vj);
                    swapped = true;
                }
                var dx = input[vi] - input[vj];
                var dy = input[vi + 1] - input[vj + 1];
                var dz = input[vi + 2] - input[vj + 2];
                var d = Sqrt(dx * dx + dz * dz);
                var nn = 1 + (int)MathF.Floor(d / sampleDist);
                if (nn >= MaxVertsPerEdge)
                    nn = MaxVertsPerEdge - 1;
                if (nverts + nn >= MaxVerts)
                    nn = MaxVerts - 1 - nverts;
                for (var k = 0; k <= nn; k++)
                {
                    var u = (float)k / (float)nn;
                    var px = input[vj] + dx * u;
                    var py = input[vj + 1] + dy * u;
                    var pz = input[vj + 2] + dz * u;
                    edge[k * 3] = px;
                    edge[k * 3 + 2] = pz;
                    edge[k * 3 + 1] = (float)GetHeight(px, py, pz, ics, chf.Ch, hp) * chf.Ch;
                }
                var idx = new int[MaxVertsPerEdge];
                idx[0] = 0;
                idx[1] = nn;
                var nidx = 2;
                for (var k = 0; k < nidx - 1;)
                {
                    int a = idx[k], b = idx[k + 1];
                    var maxd = 0f;
                    var maxi = -1;
                    for (var m = a + 1; m < b; m++)
                    {
                        var dev = DistancePtSeg3(edge, m * 3, a * 3, b * 3);
                        if (dev > maxd)
                        {
                            maxd = dev;
                            maxi = m;
                        }
                    }
                    if (maxi != -1 && maxd > sampleMaxError * sampleMaxError)
                    {
                        for (var m = nidx; m > k; m--)
                            idx[m] = idx[m - 1];
                        idx[k + 1] = maxi;
                        nidx++;
                    }
                    else
                        k++;
                }
                hull[nhull++] = j;
                if (swapped)
                {
                    for (var k = nidx - 2; k > 0; k--)
                    {
                        Array.Copy(edge, idx[k] * 3, verts, nverts * 3, 3);
                        hull[nhull++] = nverts;
                        nverts++;
                    }
                }
                else
                {
                    for (var k = 1; k < nidx - 1; k++)
                    {
                        Array.Copy(edge, idx[k] * 3, verts, nverts * 3, 3);
                        hull[nhull++] = nverts;
                        nverts++;
                    }
                }
            }
        }
        if (minExtent < sampleDist + sampleDist)
        {
            TriangulateHull(verts, nhull, hull, tris);
            return;
        }
        TriangulateHull(verts, nhull, hull, tris);
        if (tris.Count == 0)
            return;
        if (sampleDist > 0)
        {
            float bminx = input[0], bminy = input[1], bminz = input[2], bmaxx = input[0], bmaxy = input[1], bmaxz = input[2];
            for (var i = 1; i < nin; i++)
            {
                bminx = Math.Min(bminx, input[i * 3]);
                bminy = Math.Min(bminy, input[i * 3 + 1]);
                bminz = Math.Min(bminz, input[i * 3 + 2]);
                bmaxx = Math.Max(bmaxx, input[i * 3]);
                bmaxy = Math.Max(bmaxy, input[i * 3 + 1]);
                bmaxz = Math.Max(bmaxz, input[i * 3 + 2]);
            }
            var x0 = (int)MathF.Floor(bminx / sampleDist);
            var x1 = (int)MathF.Ceiling(bmaxx / sampleDist);
            var z0 = (int)MathF.Floor(bminz / sampleDist);
            var z1 = (int)MathF.Ceiling(bmaxz / sampleDist);
            samples.Clear();
            var pt = new float[3];
            for (var z = z0; z < z1; z++)
            {
                for (var x = x0; x < x1; x++)
                {
                    pt[0] = (float)x * sampleDist;
                    pt[1] = (bmaxy + bminy) * 0.5f;
                    pt[2] = (float)z * sampleDist;
                    if (DistToPoly(nin, input, pt) > -sampleDist * 0.5f)
                        continue;
                    samples.Add(x);
                    samples.Add(GetHeight(pt[0], pt[1], pt[2], ics, chf.Ch, hp));
                    samples.Add(z);
                    samples.Add(0);
                }
            }
            var nsamples = samples.Count / 4;
            for (var iter = 0; iter < nsamples; iter++)
            {
                if (nverts >= MaxVerts)
                    break;
                float bestx = 0, besty = 0, bestz = 0, bestd = 0;
                var besti = -1;
                for (var i = 0; i < nsamples; i++)
                {
                    if (samples[i * 4 + 3] != 0)
                        continue;
                    var jx = (float)(uint)((uint)i * 0x8da6b343u & 0xffff) / 65535.0f;
                    var jz = (float)(uint)((uint)i * 0xd8163841u & 0xffff) / 65535.0f;
                    pt[0] = (float)samples[i * 4] * sampleDist + (jx + jx - 1.0f) * cs * 0.1f;
                    pt[1] = (float)samples[i * 4 + 1] * chf.Ch;
                    pt[2] = (float)samples[i * 4 + 2] * sampleDist + (jz + jz - 1.0f) * cs * 0.1f;
                    var d = DistToTriMesh(pt, verts, tris);
                    if (d < 0)
                        continue;
                    if (d > bestd)
                    {
                        bestd = d;
                        besti = i;
                        bestx = pt[0];
                        besty = pt[1];
                        bestz = pt[2];
                    }
                }
                if (bestd <= sampleMaxError || besti == -1)
                    break;
                samples[besti * 4 + 3] = 1;
                verts[nverts * 3] = bestx;
                verts[nverts * 3 + 1] = besty;
                verts[nverts * 3 + 2] = bestz;
                nverts++;
                edges.Clear();
                tris.Clear();
                DelaunayHull(nverts, verts, nhull, hull, tris, edges);
            }
        }
        if (tris.Count / 4 > MaxTris)
            tris.RemoveRange(MaxTris * 4, tris.Count - MaxTris * 4);
    }

    // 1800bc070: Recast 1.4's seeding. Each polygon vertex seeds from the nearest span (height
    // difference) among its cell and the 8 around it; a depth-first walk over the patch then stops at
    // the first span within one cell of the polygon's centre, which becomes the only seed.
    private static void SeedArrayWithPolyCenter(RecastCompact chf, ushort[] polys, int p, int npoly, ushort[] verts, int bs, HeightPatch hp, List<int> stack)
    {
        ReadOnlySpan<int> offset = [0, 0, -1, -1, 0, -1, 1, -1, 1, 0, 1, 1, 0, 1, -1, 1, -1, 0];
        Array.Clear(hp.Data, 0, hp.Width * hp.Height);
        stack.Clear();
        for (var j = 0; j < npoly; j++)
        {
            int cx = 0, cz = 0, ci = -1;
            var dmin = (int)UnsetHeight;
            var v = polys[p + j] * 3;
            for (var k = 0; k < 9; k++)
            {
                var ax = verts[v] + offset[k * 2];
                int ay = verts[v + 1];
                var az = verts[v + 2] + offset[k * 2 + 1];
                if (ax < hp.XMin || ax >= hp.XMin + hp.Width || az < hp.YMin || az >= hp.YMin + hp.Height)
                    continue;
                var cell = ax + bs + (az + bs) * chf.Width;
                for (int i = chf.CellIndex(cell), ni = i + chf.CellCount(cell); i < ni; i++)
                {
                    var d = Math.Abs(ay - chf.Y[i]);
                    if (d < dmin)
                    {
                        cx = ax;
                        cz = az;
                        ci = i;
                        dmin = d;
                    }
                }
            }
            if (ci != -1)
            {
                stack.Add(cx);
                stack.Add(cz);
                stack.Add(ci);
            }
        }
        int sumx = 0, sumz = 0;
        for (var j = 0; j < npoly; j++)
        {
            sumx += verts[polys[p + j] * 3];
            sumz += verts[polys[p + j] * 3 + 2];
        }
        for (var i = 0; i < stack.Count; i += 3)
            hp.Data[stack[i] - hp.XMin + (stack[i + 1] - hp.YMin) * hp.Width] = 1;
        while (stack.Count > 0)
        {
            var ci = stack[^1];
            var cy = stack[^2];
            var cx = stack[^3];
            stack.RemoveRange(stack.Count - 3, 3);
            if (Math.Abs(cx - sumx / npoly) <= 1 && Math.Abs(cy - sumz / npoly) <= 1)
            {
                stack.Clear();
                stack.Add(cx);
                stack.Add(cy);
                stack.Add(ci);
                break;
            }
            for (var dir = 0; dir < 4; dir++)
            {
                if (chf.GetCon(ci, dir) == RecastCompact.NotConnected)
                    continue;
                var ax = cx + RecastCompact.DirX[dir];
                var ay = cy + RecastCompact.DirZ[dir];
                if (ax < hp.XMin || ax >= hp.XMin + hp.Width || ay < hp.YMin || ay >= hp.YMin + hp.Height)
                    continue;
                var idx = ax - hp.XMin + (ay - hp.YMin) * hp.Width;
                if (hp.Data[idx] != 0)
                    continue;
                var ai = chf.CellIndex(ax + bs + (ay + bs) * chf.Width) + chf.GetCon(ci, dir);
                hp.Data[idx] = 1;
                stack.Add(ax);
                stack.Add(ay);
                stack.Add(ai);
            }
        }
        Array.Fill(hp.Data, UnsetHeight, 0, hp.Width * hp.Height);
        for (var i = 0; i < stack.Count; i += 3)
        {
            hp.Data[stack[i] - hp.XMin + (stack[i + 1] - hp.YMin) * hp.Width] = chf.Y[stack[i + 2]];
            stack[i] += bs;
            stack[i + 1] += bs;
        }
    }

    // 1800bb7a0.
    private static void GetHeightData(RecastCompact chf, ushort[] polys, int p, int npoly, ushort[] verts, int bs, HeightPatch hp, List<int> queue, int region)
    {
        queue.Clear();
        Array.Fill(hp.Data, UnsetHeight, 0, hp.Width * hp.Height);
        var empty = true;
        for (var hy = 0; hy < hp.Height; hy++)
        {
            var y = hp.YMin + hy + bs;
            for (var hx = 0; hx < hp.Width; hx++)
            {
                var x = hp.XMin + hx + bs;
                var cell = x + y * chf.Width;
                for (int i = chf.CellIndex(cell), ni = i + chf.CellCount(cell); i < ni; i++)
                {
                    if (chf.Reg[i] != region)
                        continue;
                    hp.Data[hx + hy * hp.Width] = chf.Y[i];
                    empty = false;
                    var border = false;
                    for (var dir = 0; dir < 4; dir++)
                    {
                        if (chf.GetCon(i, dir) == RecastCompact.NotConnected)
                            continue;
                        if (chf.Reg[chf.Neighbour(x, y, i, dir)] != region)
                        {
                            border = true;
                            break;
                        }
                    }
                    if (border)
                    {
                        queue.Add(x);
                        queue.Add(y);
                        queue.Add(i);
                    }
                    break;
                }
            }
        }
        if (empty)
            SeedArrayWithPolyCenter(chf, polys, p, npoly, verts, bs, hp, queue);
        const int retract = 256;
        var head = 0;
        while (head * 3 < queue.Count)
        {
            int cx = queue[head * 3], cy = queue[head * 3 + 1], ci = queue[head * 3 + 2];
            head++;
            if (head >= retract)
            {
                head = 0;
                queue.RemoveRange(0, retract * 3);
            }
            for (var dir = 0; dir < 4; dir++)
            {
                if (chf.GetCon(ci, dir) == RecastCompact.NotConnected)
                    continue;
                var ax = cx + RecastCompact.DirX[dir];
                var ay = cy + RecastCompact.DirZ[dir];
                var hx = ax - hp.XMin - bs;
                var hy = ay - hp.YMin - bs;
                if ((uint)hx >= (uint)hp.Width || (uint)hy >= (uint)hp.Height)
                    continue;
                if (hp.Data[hx + hy * hp.Width] != UnsetHeight)
                    continue;
                var ai = chf.CellIndex(ax + ay * chf.Width) + chf.GetCon(ci, dir);
                hp.Data[hx + hy * hp.Width] = chf.Y[ai];
                queue.Add(ax);
                queue.Add(ay);
                queue.Add(ai);
            }
        }
    }

    // 1800bb260.
    private static byte GetEdgeFlags(float[] v, int va, int vb, float[] vpoly, int npoly)
    {
        var thrSqr = 0.001f * 0.001f;
        for (int i = 0, j = npoly - 1; i < npoly; j = i++)
        {
            if (DistancePtSeg2d(v[va], v[va + 2], vpoly, j * 3, i * 3) < thrSqr && DistancePtSeg2d(v[vb], v[vb + 2], vpoly, j * 3, i * 3) < thrSqr)
                return 1;
        }
        return 0;
    }

    public static RecastDetailMesh Build(RecastPolyMesh mesh, RecastCompact chf, float sampleDist, float sampleMaxError)
    {
        var dmesh = new RecastDetailMesh();
        if (mesh.NVerts == 0 || mesh.NPolys == 0)
            return dmesh;
        var nvp = mesh.Nvp;
        var cs = mesh.Cs;
        var ch = mesh.Ch;
        float origx = mesh.BMin[0], origy = mesh.BMin[1], origz = mesh.BMin[2];
        var borderSize = mesh.BorderSize;
        var edges = new List<int>(64);
        var tris = new List<int>(512);
        var arr = new List<int>(512);
        var samples = new List<int>(512);
        var verts = new float[256 * 3];
        var bounds = new int[mesh.NPolys * 4];
        int maxhw = 0, maxhh = 0;
        for (var i = 0; i < mesh.NPolys; i++)
        {
            var p = i * nvp * 2;
            ref var xmin = ref bounds[i * 4];
            ref var xmax = ref bounds[i * 4 + 1];
            ref var ymin = ref bounds[i * 4 + 2];
            ref var ymax = ref bounds[i * 4 + 3];
            xmin = chf.Width;
            xmax = 0;
            ymin = chf.Height;
            ymax = 0;
            for (var j = 0; j < nvp; j++)
            {
                if (mesh.Polys[p + j] == RecastPolyMesh.NullIdx)
                    break;
                var v = mesh.Polys[p + j] * 3;
                xmin = Math.Min(xmin, mesh.Verts[v]);
                xmax = Math.Max(xmax, mesh.Verts[v]);
                ymin = Math.Min(ymin, mesh.Verts[v + 2]);
                ymax = Math.Max(ymax, mesh.Verts[v + 2]);
            }
            xmin = Math.Max(0, xmin - 1);
            xmax = Math.Min(chf.Width, xmax + 1);
            ymin = Math.Max(0, ymin - 1);
            ymax = Math.Min(chf.Height, ymax + 1);
            if (xmin >= xmax || ymin >= ymax)
                continue;
            maxhw = Math.Max(maxhw, xmax - xmin);
            maxhh = Math.Max(maxhh, ymax - ymin);
        }
        var hp = new HeightPatch { Data = new ushort[maxhw * maxhh] };
        dmesh.NMeshes = mesh.NPolys;
        dmesh.Meshes = new uint[dmesh.NMeshes * 4];
        var poly = new float[nvp * 3];
        for (var i = 0; i < mesh.NPolys; i++)
        {
            var p = i * nvp * 2;
            var npoly = 0;
            for (var j = 0; j < nvp; j++)
            {
                if (mesh.Polys[p + j] == RecastPolyMesh.NullIdx)
                    break;
                var v = mesh.Polys[p + j] * 3;
                poly[j * 3] = (float)mesh.Verts[v] * cs;
                poly[j * 3 + 1] = (float)mesh.Verts[v + 1] * ch;
                poly[j * 3 + 2] = (float)mesh.Verts[v + 2] * cs;
                npoly++;
            }
            hp.XMin = bounds[i * 4];
            hp.YMin = bounds[i * 4 + 2];
            hp.Width = bounds[i * 4 + 1] - bounds[i * 4];
            hp.Height = bounds[i * 4 + 3] - bounds[i * 4 + 2];
            GetHeightData(chf, mesh.Polys, p, npoly, mesh.Verts, borderSize, hp, arr, mesh.Regs[i]);
            BuildPolyDetail(poly, npoly, sampleDist, sampleMaxError, chf, hp, verts, out var nverts, tris, edges, samples);
            for (var j = 0; j < nverts; j++)
            {
                verts[j * 3] += origx;
                verts[j * 3 + 1] += origy + chf.Ch;
                verts[j * 3 + 2] += origz;
            }
            for (var j = 0; j < npoly; j++)
            {
                poly[j * 3] += origx;
                poly[j * 3 + 1] += origy;
                poly[j * 3 + 2] += origz;
            }
            var ntris = tris.Count / 4;
            dmesh.Meshes[i * 4] = (uint)dmesh.NVerts;
            dmesh.Meshes[i * 4 + 1] = (uint)nverts;
            dmesh.Meshes[i * 4 + 2] = (uint)dmesh.NTris;
            dmesh.Meshes[i * 4 + 3] = (uint)ntris;
            for (var j = 0; j < nverts; j++)
            {
                dmesh.Verts.Add(verts[j * 3]);
                dmesh.Verts.Add(verts[j * 3 + 1]);
                dmesh.Verts.Add(verts[j * 3 + 2]);
            }
            for (var j = 0; j < ntris; j++)
            {
                int t0 = tris[j * 4], t1 = tris[j * 4 + 1], t2 = tris[j * 4 + 2];
                dmesh.Tris.Add((byte)t0);
                dmesh.Tris.Add((byte)t1);
                dmesh.Tris.Add((byte)t2);
                var flags = GetEdgeFlags(verts, t0 * 3, t1 * 3, poly, npoly)
                            | GetEdgeFlags(verts, t1 * 3, t2 * 3, poly, npoly) << 2
                            | GetEdgeFlags(verts, t2 * 3, t0 * 3, poly, npoly) << 4;
                dmesh.Tris.Add((byte)flags);
            }
        }
        return dmesh;
    }
}
