namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// The Recast stage's input (1800852a0): every pool vertex (not only the used ones) converted to
/// float and through the inches-to-metres matrix, the bounds over them, and each polygon fanned
/// as (i0, i[k-1], i[k]).
/// </summary>
public sealed class RecastInput
{
    public float[] Verts = [];
    public int[] Tris = [];
    public float[] BMin = new float[3], BMax = new float[3];

    public int TriCount => Tris.Length / 3;

    // The 3x4 matrix built at rsp+0x40 in 1800852a0 (rows x, y, z; column 3 the translation):
    // recast = (-0.0254 y, 0.0254 z, -0.0254 x).
    private static readonly float[] ToRecast = [0f, -RecastConfig.Inch, 0f, 0f, 0f, 0f, RecastConfig.Inch, 0f, -RecastConfig.Inch, 0f, 0f, 0f];

    /// <summary>
    /// 1800ee160 -> 1800edb00: the point (w = 1) times each row, summed as the SSE code pairs them,
    /// (p0 m0 + p2 m2) + (p1 m1 + m3).
    /// </summary>
    public static (float X, float Y, float Z) Transform(float[] m, float x, float y, float z)
    {
        float Row(int r) => (x * m[r * 4] + z * m[r * 4 + 2]) + (y * m[r * 4 + 1] + 1f * m[r * 4 + 3]);
        return (Row(0), Row(1), Row(2));
    }

    public static RecastInput From(IReadOnlyList<(double X, double Y, double Z)> pool, IReadOnlyList<int[]> polys)
    {
        var input = new RecastInput();
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = -float.MaxValue, maxY = -float.MaxValue, maxZ = -float.MaxValue;
        var verts = new float[pool.Count * 3];
        for (var i = 0; i < pool.Count; i++)
        {
            var (x, y, z) = Transform(ToRecast, (float)pool[i].X, (float)pool[i].Y, (float)pool[i].Z);
            verts[i * 3] = x;
            verts[i * 3 + 1] = y;
            verts[i * 3 + 2] = z;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (z < minZ) minZ = z;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
            if (z > maxZ) maxZ = z;
        }
        input.Verts = verts;
        input.BMin = [minX, minY, minZ];
        input.BMax = [maxX, maxY, maxZ];
        var tris = new List<int>();
        foreach (var p in polys)
        {
            for (var k = 2; k < p.Length; k++)
            {
                tris.Add(p[0]);
                tris.Add(p[k - 1]);
                tris.Add(p[k]);
            }
        }
        input.Tris = [.. tris];
        return input;
    }

    /// <summary>The detail vertices back to the pool through the inverted matrix (180082280).</summary>
    public static (float X, float Y, float Z) FromRecast(float x, float y, float z)
        => Transform(FromRecastMatrix, x, y, z);

    public static readonly float[] FromRecastMatrix = Invert(ToRecast);

    /// <summary>
    /// 1800ed380: the transpose of the 3x3 part, the translation -(R^T t), then, when the first
    /// column's squared length s = m4^2 + m0^2 + m8^2 is 0.001 or more from 1, everything times 1 / s.
    /// </summary>
    public static float[] Invert(float[] m)
    {
        var o = new float[12];
        o[0] = m[0];
        o[1] = m[4];
        o[2] = m[8];
        o[4] = m[1];
        o[5] = m[5];
        o[6] = m[9];
        o[8] = m[2];
        o[9] = m[6];
        o[10] = m[10];
        float t0 = m[3], t1 = m[7], t2 = m[11];
        o[3] = -(t2 * o[2] + t1 * o[1] + t0 * o[0]);
        o[7] = -(o[6] * t2 + o[5] * t1 + t0 * o[4]);
        o[11] = -(o[10] * t2 + o[9] * t1 + t0 * o[8]);
        var s = m[4] * m[4] + m[0] * m[0] + m[8] * m[8];
        if (MathF.Abs(s - 1.0f) >= 0.001f)
        {
            var f = s != 0f ? 1.0f / s : 1.0f;
            for (var i = 0; i < 12; i++)
                o[i] *= f;
        }
        return o;
    }
}

/// <summary>
/// rcChunkyTriMesh (rcCreateChunkyTriMesh 1800bedb0, subdivide 1800bf0f0, query 1800bf020): items
/// split at the middle after a CRT qsort on the longer axis' minimum, 256 triangles a leaf.
/// </summary>
public sealed class ChunkyTriMesh
{
    public struct Node
    {
        public float MinX, MinY, MaxX, MaxY;
        public int I, N;
    }

    public Node[] Nodes = [];
    public int NodeCount;
    public int[] Tris = [];
    public int MaxTrisPerChunk;

    private struct Item
    {
        public float MinX, MinY, MaxX, MaxY;
        public int I;
    }

    public static ChunkyTriMesh Create(float[] verts, int[] tris, int ntris, int trisPerChunk)
    {
        var nchunks = (ntris + trisPerChunk - 1) / trisPerChunk;
        var cm = new ChunkyTriMesh { Nodes = new Node[nchunks * 4], Tris = new int[ntris * 3] };
        var items = new Item[ntris];
        for (var i = 0; i < ntris; i++)
        {
            var it = new Item { I = i };
            int a = tris[i * 3] * 3, b = tris[i * 3 + 1] * 3, c = tris[i * 3 + 2] * 3;
            it.MinX = it.MaxX = verts[a];
            it.MinY = it.MaxY = verts[a + 2];
            foreach (var v in new[] { b, c })
            {
                if (verts[v] < it.MinX) it.MinX = verts[v];
                if (verts[v + 2] < it.MinY) it.MinY = verts[v + 2];
                if (it.MaxX < verts[v]) it.MaxX = verts[v];
                if (it.MaxY < verts[v + 2]) it.MaxY = verts[v + 2];
            }
            items[i] = it;
        }
        int curNode = 0, curTri = 0;
        Subdivide(items, 0, ntris, trisPerChunk, ref curNode, cm.Nodes, nchunks * 4, ref curTri, cm.Tris, tris);
        cm.NodeCount = curNode;
        for (var i = 0; i < curNode; i++)
            if (cm.Nodes[i].I >= 0 && cm.Nodes[i].N > cm.MaxTrisPerChunk)
                cm.MaxTrisPerChunk = cm.Nodes[i].N;
        return cm;
    }

    private static void CalcExtends(Item[] items, int imin, int imax, ref Node node)
    {
        node.MinX = items[imin].MinX;
        node.MinY = items[imin].MinY;
        node.MaxX = items[imin].MaxX;
        node.MaxY = items[imin].MaxY;
        for (var i = imin + 1; i < imax; i++)
        {
            if (items[i].MinX < node.MinX) node.MinX = items[i].MinX;
            if (items[i].MinY < node.MinY) node.MinY = items[i].MinY;
            if (items[i].MaxX > node.MaxX) node.MaxX = items[i].MaxX;
            if (items[i].MaxY > node.MaxY) node.MaxY = items[i].MaxY;
        }
    }

    private static void Subdivide(Item[] items, int imin, int imax, int trisPerChunk, ref int curNode, Node[] nodes, int maxNodes,
                                  ref int curTri, int[] outTris, int[] inTris)
    {
        var inum = imax - imin;
        var icur = curNode;
        // Valve returns only past the last node (upstream at >=).
        if (maxNodes < curNode)
            return;
        curNode++;
        ref var node = ref nodes[icur];
        if (inum <= trisPerChunk)
        {
            CalcExtends(items, imin, imax, ref node);
            node.I = curTri;
            node.N = inum;
            for (var i = imin; i < imax; i++)
            {
                var src = items[i].I * 3;
                var dst = curTri * 3;
                curTri++;
                outTris[dst] = inTris[src];
                outTris[dst + 1] = inTris[src + 1];
                outTris[dst + 2] = inTris[src + 2];
            }
            return;
        }
        CalcExtends(items, imin, imax, ref node);
        var segment = new ArraySegment<Item>(items, imin, inum);
        if (node.MaxX - node.MinX < node.MaxY - node.MinY)
            CrtQsort.Sort(segment, (a, b) => a.MinY < b.MinY ? -1 : a.MinY > b.MinY ? 1 : 0);
        else
            CrtQsort.Sort(segment, (a, b) => a.MinX < b.MinX ? -1 : a.MinX > b.MinX ? 1 : 0);
        var isplit = imin + inum / 2;
        Subdivide(items, imin, isplit, trisPerChunk, ref curNode, nodes, maxNodes, ref curTri, outTris, inTris);
        Subdivide(items, isplit, imax, trisPerChunk, ref curNode, nodes, maxNodes, ref curTri, outTris, inTris);
        nodes[icur].I = icur - curNode;
    }

    /// <summary>rcGetChunksOverlappingRect (1800bf020): leaf ids in node order.</summary>
    public List<int> ChunksOverlappingRect(float minX, float minY, float maxX, float maxY, int maxIds)
    {
        var ids = new List<int>();
        var i = 0;
        while (i < NodeCount)
        {
            var node = Nodes[i];
            var overlap = !(node.MaxX < minX || maxX < node.MinX) && !(node.MaxY < minY || maxY < node.MinY);
            var leaf = node.I >= 0;
            if (leaf && overlap && ids.Count < maxIds)
                ids.Add(i);
            if (overlap || leaf)
                i++;
            else
                i -= node.I;
        }
        return ids;
    }
}
