using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

/// <summary>An axis-aligned box, min then max, as Rubikon passes one (6 floats).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Aabb
{
    public Vec3 Min;
    public Vec3 Max;

    public Aabb(Vec3 min, Vec3 max) { Min = min; Max = max; }

    public readonly override string ToString() => $"[{Min} {Max}]";
}

/// <summary>A broadphase tree node, laid out as vphysics2 keeps it (0x30 bytes).</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x30)]
public struct TreeNode
{
    [FieldOffset(0x00)] public Vec3 Min;

    /// <summary>The free list's link, over Min.X while the node is free.</summary>
    [FieldOffset(0x00)] public int Next;

    /// <summary>-1 on a leaf.</summary>
    [FieldOffset(0x0c)] public int Child1;

    [FieldOffset(0x10)] public Vec3 Max;
    [FieldOffset(0x1c)] public int Child2;
    [FieldOffset(0x20)] public int Parent;

    /// <summary>Bits 0-30 the height, bit 31 set when the shape is a compound (type 4).</summary>
    [FieldOffset(0x24)] public uint Bits;

    /// <summary>The shape handle (a CRnShape pointer in Valve's tree; 0 on internal nodes).</summary>
    [FieldOffset(0x28)] public ulong Shape;

    /// <summary>The height, read the way Valve reads it: ((int)(bits * 2)) >> 1.</summary>
    public readonly int Height => (int)(Bits << 1) >> 1;

    /// <summary>Whether bit 31 (compound shape) is set.</summary>
    public readonly bool Compound => (Bits & 0x80000000) != 0;

    /// <summary>Writes a height and keeps bit 31: ((h ^ old) &amp; 0x80000000) ^ h.</summary>
    public void SetHeight(int h) => Bits = (((uint)h ^ Bits) & 0x80000000) ^ (uint)h;

    public readonly Aabb Box => new(Min, Max);
}

/// <summary>Receives the leaf pairs a query finds, in the order it finds them.</summary>
public interface IPairSink
{
    /// <summary>
    /// Two plain shapes whose fat boxes overlap; Valve checks the existing-pair
    /// set with (a, b) and then creates the contact (FUN_1801fa710, FUN_1802038b0).
    /// </summary>
    void Pair(ulong a, ulong b);

    /// <summary>
    /// A compound shape and a plain one; Valve then walks the compound's own
    /// child tree against <paramref name="otherBox"/> (the plain shape's fat box).
    /// That walk belongs to the shape, not to the broadphase, and is the caller's.
    /// </summary>
    void Compound(ulong compound, ulong other, in Aabb otherBox);
}

/// <summary>
/// One of the broadphase's dynamic AABB trees (Box2D v3's b2DynamicTree in
/// 3-D), ported from vphysics2 (CS2 2026-09-24) with every float operation in
/// Valve's order, and checked byte for byte against the DLL in
/// DynamicTreeOracleTests.
///
/// <para>Leaf indices matter beyond the tree: a proxy id is leaf * 8 | tree,
/// and new contacts are sorted by proxy ids, so the free list is kept exactly
/// (grow threads new nodes ascending; a remove pushes the parent, then the
/// leaf).</para>
/// </summary>
public sealed class DynamicTree
{
    /// <summary>The 0x58-byte header's fields (+0x00 .. +0x10).</summary>
    public int Root = -1;
    public int LeafCount;
    public int NodeCount;
    public int Capacity;
    public int FreeHead = -1;

    public TreeNode[] Nodes = [];

    /// <summary>The tree's fattening margin (tree+0x4C).</summary>
    public float Margin;

    /// <summary>FUN_180333f20: 32 nodes, all free, threaded ascending.</summary>
    public DynamicTree(float margin = 0f)
    {
        Margin = margin;
        Nodes = new TreeNode[32];
        for (var i = 0; i < 31; i++)
            Nodes[i].Next = i + 1;
        Nodes[31].Next = -1;
        FreeHead = 0;
        Capacity = 32;
    }

    // ---------------------------------------------------------------- primitives

    /// <summary>minss a, b: a when a &lt; b, else b.</summary>
    private static float MinSs(float a, float b) => a < b ? a : b;

    /// <summary>maxss a, b: a when a &gt; b, else b.</summary>
    private static float MaxSs(float a, float b) => a > b ? a : b;

    /// <summary>Per-lane minss/maxss with <paramref name="a"/> first.</summary>
    private static Aabb Union(in Aabb a, in Aabb b) => new(
        new(MinSs(a.Min.X, b.Min.X), MinSs(a.Min.Y, b.Min.Y), MinSs(a.Min.Z, b.Min.Z)),
        new(MaxSs(a.Max.X, b.Max.X), MaxSs(a.Max.Y, b.Max.Y), MaxSs(a.Max.Z, b.Max.Z)));

    /// <summary>
    /// Surface area as every site computes it: t = (dx*dy + dy*dz) + dx*dz,
    /// then t + t. The sites differ only in the order of the first sum and of
    /// each product, which IEEE makes exact; the x*z term is always last.
    /// </summary>
    private static float Area(float dx, float dy, float dz)
    {
        var t = ((dx * dy) + (dy * dz)) + (dx * dz);
        return t + t;
    }

    private static float Area(in Aabb b) => Area(b.Max.X - b.Min.X, b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z);

    /// <summary>FUN_180335f20: grows to <paramref name="capacity"/>; the new nodes become the free list, ascending.</summary>
    private void Grow(int capacity)
    {
        if (Capacity >= capacity)
            return;
        var nodes = new TreeNode[capacity];
        Nodes.AsSpan(0, Capacity).CopyTo(nodes);
        Nodes = nodes;
        for (var i = Capacity; i < capacity - 1; i++)
            Nodes[i].Next = i + 1;
        Nodes[capacity - 1].Next = -1;
        FreeHead = Capacity;
        Capacity = capacity;
    }

    /// <summary>Pops the free head, growing to max(2, 2 * capacity) first when the list is empty.</summary>
    private int Allocate()
    {
        if (FreeHead < 0)
            Grow(Math.Max(2, Capacity * 2));
        var index = FreeHead;
        FreeHead = Nodes[index].Next;
        return index;
    }

    // ---------------------------------------------------------------- insert / remove

    /// <summary>
    /// FUN_180334130: inserts a leaf for <paramref name="aabb"/> fattened by
    /// <paramref name="margin"/> and returns its index, or -1 when the box is
    /// empty or NaN (any min &gt; max).
    /// </summary>
    public int Insert(in Aabb aabb, ulong shape, bool compound, float margin)
    {
        if (!(aabb.Min.X <= aabb.Max.X && aabb.Min.Y <= aabb.Max.Y && aabb.Min.Z <= aabb.Max.Z))
            return -1;
        NodeCount++;
        var leaf = Allocate();
        ref var n = ref Nodes[leaf];
        n.Min = new(aabb.Min.X - margin, aabb.Min.Y - margin, aabb.Min.Z - margin);
        n.Child1 = -1;
        n.Max = new(margin + aabb.Max.X, margin + aabb.Max.Y, margin + aabb.Max.Z);
        n.Child2 = -1;
        n.Parent = -1;
        n.Bits &= 0x80000000;
        n.Shape = shape;
        n.Bits &= 0x7fffffff;
        n.Bits |= (compound ? 1u : 0u) << 31;
        InsertLeaf(leaf, rotate: true);
        LeafCount++;
        return leaf;
    }

    /// <summary>FUN_1803342b0: removes a leaf and returns its shape (0 when it is not a leaf).</summary>
    public ulong Remove(int leaf)
    {
        if (leaf == -1 || Nodes[leaf].Child1 != -1)
            return 0;
        var shape = Nodes[leaf].Shape;
        RemoveLeaf(leaf);
        NodeCount--;
        Nodes[leaf].Next = FreeHead;
        LeafCount--;
        FreeHead = leaf;
        return shape;
    }

    /// <summary>FUN_180335cf0: takes a leaf out of the tree but keeps it allocated (for a move).</summary>
    public void Unlink(int leaf)
    {
        RemoveLeaf(leaf);
        ref var n = ref Nodes[leaf];
        n.Child1 = -1;
        n.Child2 = -1;
        n.Parent = -1;
        n.Bits &= 0x80000000;
    }

    /// <summary>FUN_180335ce0: puts an unlinked leaf back, without rotations.</summary>
    public void Relink(int leaf) => InsertLeaf(leaf, rotate: false);

    /// <summary>
    /// FUN_180334b50: links an allocated leaf in as the sibling of the best
    /// node, with a new parent popped from the free list, then refits upward.
    /// </summary>
    private void InsertLeaf(int leaf, bool rotate)
    {
        if (Root == -1)
        {
            Root = leaf;
            Nodes[leaf].Parent = -1;
            return;
        }
        var box = Nodes[leaf].Box;
        var sibling = FindBestSibling(box);
        var union = Union(Nodes[sibling].Box, box);
        NodeCount++;
        var parent = Allocate();
        ref var p = ref Nodes[parent];
        ref var s = ref Nodes[sibling];
        p.Min = union.Min;
        p.Child1 = sibling;
        p.Max = union.Max;
        p.Child2 = leaf;
        p.Parent = s.Parent;
        p.SetHeight(s.Height + 1);
        p.Shape = 0;
        p.Bits &= 0x7fffffff;
        var grand = s.Parent;
        if (grand == -1)
            Root = parent;
        else if (Nodes[grand].Child1 == sibling)
            Nodes[grand].Child1 = parent;
        else
            Nodes[grand].Child2 = parent;
        s.Parent = parent;
        Nodes[leaf].Parent = parent;
        Refit(Nodes[leaf].Parent, rotate);
    }

    /// <summary>
    /// FUN_180335d50: the sibling takes the parent's place, the parent goes to
    /// the free list and the ancestors are refit (no rotations). A root leaf
    /// just empties the tree.
    /// </summary>
    private void RemoveLeaf(int leaf)
    {
        if (leaf == Root)
        {
            Root = -1;
            return;
        }
        var parent = Nodes[leaf].Parent;
        var sibling = Nodes[parent].Child1;
        if (sibling == leaf)
            sibling = Nodes[parent].Child2;
        var grand = Nodes[parent].Parent;
        if (grand == -1)
        {
            Nodes[sibling].Parent = -1;
            Root = sibling;
        }
        else
        {
            Nodes[sibling].Parent = grand;
            if (Nodes[grand].Child1 == parent)
                Nodes[grand].Child1 = sibling;
            else
                Nodes[grand].Child2 = sibling;
            Refit(grand, rotate: false);
        }
        NodeCount--;
        Nodes[parent].Next = FreeHead;
        FreeHead = parent;
    }

    /// <summary>FUN_180333fd0: from <paramref name="index"/> to the root, box = union of the children, height = 1 + max, then rotate.</summary>
    private void Refit(int index, bool rotate)
    {
        while (index != -1)
        {
            ref var n = ref Nodes[index];
            ref var c1 = ref Nodes[n.Child1];
            ref var c2 = ref Nodes[n.Child2];
            var box = Union(c1.Box, c2.Box);
            n.Min = box.Min;
            n.Max = box.Max;
            n.SetHeight(1 + Math.Max(c1.Height, c2.Height));
            if (rotate)
                Rotate(index);
            index = Nodes[index].Parent;
        }
    }

    // ---------------------------------------------------------------- the insertion heuristics

    /// <summary>
    /// FUN_180334330, b2FindBestSibling: branch and bound on the surface area
    /// cost, descending into the child with the lower bound (on a tie, the one
    /// whose centre is nearer the new box's).
    /// </summary>
    internal int FindBestSibling(in Aabb d)
    {
        var cx = (d.Min.X + d.Max.X) * 0.5f;
        var cy = (d.Min.Y + d.Max.Y) * 0.5f;
        var cz = (d.Min.Z + d.Max.Z) * 0.5f;
        var areaD = Area(d);

        var index = Root;
        var areaBase = Area(Nodes[index].Box);
        var direct = Area(Union(Nodes[index].Box, d));
        var inherited = 0f;
        var best = direct;
        var bestIndex = index;
        if (Nodes[index].Child1 == -1)
            return bestIndex;
        for (;;)
        {
            ref var n = ref Nodes[index];
            var cost = inherited + direct;
            if (best > cost)
            {
                best = cost;
                bestIndex = index;
            }
            inherited = inherited + (direct - areaBase);

            var i1 = n.Child1;
            var i2 = n.Child2;
            ref var c1 = ref Nodes[i1];
            ref var c2 = ref Nodes[i2];
            var leaf1 = c1.Child1 == -1;
            var leaf2 = c2.Child1 == -1;

            var direct1 = Area(Union(c1.Box, d));
            var cost1 = direct1 + inherited;
            var lower1 = float.MaxValue;
            var area1 = 0f;
            if (leaf1)
            {
                if (best > cost1)
                {
                    best = cost1;
                    bestIndex = i1;
                }
            }
            else
            {
                area1 = Area(c1.Box);
                lower1 = MinSs(areaD - area1, 0f) + cost1;
            }

            var direct2 = Area(Union(c2.Box, d));
            var cost2 = direct2 + inherited;
            var lower2 = float.MaxValue;
            var area2 = 0f;
            if (leaf2)
            {
                if (best > cost2)
                {
                    best = cost2;
                    bestIndex = i2;
                }
            }
            else
            {
                area2 = Area(c2.Box);
                lower2 = MinSs(areaD - area2, 0f) + cost2;
            }

            if (leaf1 && leaf2)
                return bestIndex;
            if (lower1 >= best && lower2 >= best)
                return bestIndex;

            if (lower1 == lower2 && !leaf1)
            {
                var x1 = (c1.Max.X + c1.Min.X) * 0.5f - cx;
                var y1 = (c1.Max.Y + c1.Min.Y) * 0.5f - cy;
                var z1 = (c1.Max.Z + c1.Min.Z) * 0.5f - cz;
                var x2 = (c2.Max.X + c2.Min.X) * 0.5f - cx;
                var y2 = (c2.Max.Y + c2.Min.Y) * 0.5f - cy;
                var z2 = (c2.Max.Z + c2.Min.Z) * 0.5f - cz;
                lower1 = ((y1 * y1) + (x1 * x1)) + (z1 * z1);
                lower2 = ((y2 * y2) + (x2 * x2)) + (z2 * z2);
            }

            if (lower2 > lower1 && !leaf1)
            {
                index = i1;
                areaBase = area1;
                direct = direct1;
            }
            else
            {
                index = i2;
                areaBase = area2;
                direct = direct2;
            }
            if (Nodes[index].Child1 == -1)
                return bestIndex;
        }
    }

    /// <summary>
    /// FUN_180336000, b2RotateNodes: swaps a grandchild with its uncle when
    /// that lowers the surface area. A = <paramref name="a"/>, B and C its
    /// children, D E the children of B, F G those of C. Only strictly better
    /// rotations are taken, first found wins among equals.
    /// </summary>
    internal void Rotate(int a)
    {
        ref var A = ref Nodes[a];
        if ((int)(A.Bits << 1) < 4)
            return;
        var ib = A.Child1;
        var ic = A.Child2;
        ref var B = ref Nodes[ib];
        ref var C = ref Nodes[ic];

        if ((B.Bits & 0x7fffffff) == 0)
        {
            // B is a leaf: B trades places with F or G.
            var iF = C.Child1;
            var iG = C.Child2;
            ref var F = ref Nodes[iF];
            ref var G = ref Nodes[iG];
            var areaC = Area(C.Box);
            var bg = Union(B.Box, G.Box);
            var bf = Union(B.Box, F.Box);
            var areaBG = Area(bg);
            var areaBF = Area(bf);
            if (areaBG > areaC && areaBF > areaC)
                return;
            if (areaBF > areaBG)
            {
                A.Child1 = iF;
                C.Child1 = ib;
                B.Parent = ic;
                F.Parent = a;
                C.Min = bg.Min;
                C.Max = bg.Max;
                C.SetHeight(1 + Math.Max(B.Height, G.Height));
                A.SetHeight(1 + Math.Max(C.Height, F.Height));
            }
            else
            {
                A.Child1 = iG;
                C.Child2 = ib;
                B.Parent = ic;
                G.Parent = a;
                C.Min = bf.Min;
                C.Max = bf.Max;
                C.SetHeight(1 + Math.Max(B.Height, F.Height));
                A.SetHeight(1 + Math.Max(C.Height, G.Height));
            }
            return;
        }

        var iD = B.Child1;
        var iE = B.Child2;
        ref var D = ref Nodes[iD];
        ref var E = ref Nodes[iE];
        var areaB = Area(B.Box);

        if ((C.Bits & 0x7fffffff) == 0)
        {
            // C is a leaf: C trades places with D or E.
            var ce = Union(C.Box, E.Box);
            var cd = Union(C.Box, D.Box);
            var areaCE = Area(ce);
            var areaCD = Area(cd);
            if (areaCE > areaB && areaCD > areaB)
                return;
            if (areaCD > areaCE)
            {
                A.Child2 = iD;
                B.Child1 = ic;
                C.Parent = ib;
                D.Parent = a;
                B.Min = ce.Min;
                B.Max = ce.Max;
                B.SetHeight(1 + Math.Max(C.Height, E.Height));
                A.SetHeight(1 + Math.Max(B.Height, D.Height));
            }
            else
            {
                A.Child2 = iE;
                B.Child2 = ic;
                C.Parent = ib;
                E.Parent = a;
                B.Min = cd.Min;
                B.Max = cd.Max;
                B.SetHeight(1 + Math.Max(C.Height, D.Height));
                A.SetHeight(1 + Math.Max(B.Height, E.Height));
            }
            return;
        }

        // Both internal: four candidate swaps against the current cost.
        var jF = C.Child1;
        var jG = C.Child2;
        ref var F2 = ref Nodes[jF];
        ref var G2 = ref Nodes[jG];
        var areaC2 = Area(C.Box);
        var best = areaC2 + areaB;
        var rotation = 0;
        var ug = Union(B.Box, G2.Box);
        var cost = Area(ug) + areaB;
        if (best > cost)
        {
            rotation = 1;
            best = cost;
        }
        var uf = Union(B.Box, F2.Box);
        cost = Area(uf) + areaB;
        if (best > cost)
        {
            rotation = 2;
            best = cost;
        }
        var ue = Union(C.Box, E.Box);
        cost = Area(ue) + areaC2;
        if (best > cost)
        {
            rotation = 3;
            best = cost;
        }
        var ud = Union(C.Box, D.Box);
        cost = Area(ud) + areaC2;
        if (best > cost)
            rotation = 4;

        switch (rotation)
        {
            case 1: // B <-> F
                A.Child1 = jF;
                C.Child1 = ib;
                B.Parent = ic;
                F2.Parent = a;
                C.Min = ug.Min;
                C.Max = ug.Max;
                C.SetHeight(1 + Math.Max(B.Height, G2.Height));
                A.SetHeight(1 + Math.Max(C.Height, F2.Height));
                break;
            case 2: // B <-> G
                A.Child1 = jG;
                C.Child2 = ib;
                B.Parent = ic;
                G2.Parent = a;
                C.Min = uf.Min;
                C.Max = uf.Max;
                C.SetHeight(1 + Math.Max(B.Height, F2.Height));
                A.SetHeight(1 + Math.Max(C.Height, G2.Height));
                break;
            case 3: // C <-> D
                A.Child2 = iD;
                B.Child1 = ic;
                C.Parent = ib;
                D.Parent = a;
                B.Min = ue.Min;
                B.Max = ue.Max;
                B.SetHeight(1 + Math.Max(C.Height, E.Height));
                A.SetHeight(1 + Math.Max(B.Height, D.Height));
                break;
            case 4: // C <-> E
                A.Child2 = iE;
                B.Child2 = ic;
                C.Parent = ib;
                E.Parent = a;
                B.Min = ud.Min;
                B.Max = ud.Max;
                B.SetHeight(1 + Math.Max(C.Height, D.Height));
                A.SetHeight(1 + Math.Max(B.Height, E.Height));
                break;
        }
    }

    // ---------------------------------------------------------------- moves

    /// <summary>
    /// FUN_1802d7380: re-fattens a leaf when its box no longer contains
    /// <paramref name="aabb"/>, or when every side has at least 16 units of
    /// slack. Returns whether the fat box was rewritten.
    /// </summary>
    public bool Refatten(int leaf, in Aabb aabb, float margin)
    {
        ref var n = ref Nodes[leaf];
        if (n.Min.X <= aabb.Min.X && aabb.Max.X <= n.Max.X
            && n.Min.Y <= aabb.Min.Y && aabb.Max.Y <= n.Max.Y
            && n.Min.Z <= aabb.Min.Z && aabb.Max.Z <= n.Max.Z)
        {
            if (aabb.Min.X - 16f < n.Min.X || n.Max.X < aabb.Max.X + 16f
                || aabb.Min.Y - 16f < n.Min.Y || n.Max.Y < aabb.Max.Y + 16f
                || aabb.Min.Z - 16f < n.Min.Z || n.Max.Z < aabb.Max.Z + 16f)
                return false;
        }
        if (!(aabb.Min.X <= aabb.Max.X && aabb.Min.Y <= aabb.Max.Y && aabb.Min.Z <= aabb.Max.Z))
            return false;
        n.Max = new(aabb.Max.X + margin, aabb.Max.Y + margin, aabb.Max.Z + margin);
        n.Min = new(aabb.Min.X - margin, aabb.Min.Y - margin, aabb.Min.Z - margin);
        return true;
    }

    /// <summary>
    /// FUN_180336c20 with the modes the settle uses: 0 refits the ancestors of
    /// the listed leaves, 1 unlinks every listed leaf and then relinks them in
    /// list order, 3 rebuilds the whole tree from Morton codes, 4 refits every
    /// node bottom up. Nothing happens with fewer than two leaves. Mode 2
    /// (relink every leaf) is not used by the settle and is not ported.
    /// </summary>
    public void Rebuild(int mode, ReadOnlySpan<int> leaves = default)
    {
        if (LeafCount <= 1)
            return;
        switch (mode)
        {
            case 0:
                RefitLeaves(leaves);
                break;
            case 1:
                foreach (var leaf in leaves)
                    Unlink(leaf);
                foreach (var leaf in leaves)
                    Relink(leaf);
                break;
            case 3:
                MortonRebuild();
                break;
            case 4:
                RefitAll();
                break;
            default:
                throw new NotSupportedException($"rebuild mode {mode}");
        }
    }

    /// <summary>FUN_180334de0 with a list: each leaf's ancestors get the union of their children (minps/maxps, child1 first).</summary>
    private void RefitLeaves(ReadOnlySpan<int> leaves)
    {
        foreach (var leaf in leaves)
            for (var i = Nodes[leaf].Parent; i != -1; i = Nodes[i].Parent)
                RefitBox(i);
    }

    private void RefitBox(int i)
    {
        ref var n = ref Nodes[i];
        var box = Union(Nodes[n.Child1].Box, Nodes[n.Child2].Box);
        n.Min = box.Min;
        n.Max = box.Max;
    }

    /// <summary>FUN_180334de0 without a list, FUN_180334fc0: post-order refit of every internal node.</summary>
    private void RefitAll()
    {
        if (Nodes[Root].Child1 == -1)
            return;
        RefitSubtree(Root);
    }

    private void RefitSubtree(int i)
    {
        if (Nodes[i].Child1 == -1)
            return;
        RefitSubtree(Nodes[i].Child1);
        RefitSubtree(Nodes[i].Child2);
        RefitBox(i);
    }

    /// <summary>A leaf and its 30-bit Morton code, as the rebuild sorts them (8 bytes: code, leaf).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MortonRecord
    {
        public uint Code;
        public int Leaf;
    }

    private struct ByCode : ILess<MortonRecord>
    {
        public readonly bool Less(in MortonRecord a, in MortonRecord b) => a.Code < b.Code;
    }

    /// <summary>
    /// FUN_180335290: an LBVH rebuild. Leaves are coded by the fat box's min
    /// corner, quantised to 10 bits per axis in the root box as it stands,
    /// sorted with MSVC's std::sort, then paired level by level (the first
    /// level pairs only as many as bring the count to a power of two). The
    /// internal nodes are reused, taken from the end of their DFS order, so
    /// the old root stays the root. Leaves and the free list do not change.
    /// </summary>
    private void MortonRebuild()
    {
        ref var root = ref Nodes[Root];
        var rootMin = root.Min;
        var sx = 1023f / (root.Max.X - rootMin.X);
        var sy = 1023f / (root.Max.Y - rootMin.Y);
        var sz = 1023f / (root.Max.Z - rootMin.Z);

        // FUN_180333310: DFS, child1 first; internal nodes to the pool, leaves coded.
        var pool = new List<int>(LeafCount);
        var records = new List<MortonRecord>(LeafCount);
        var stack = new Stack<int>();
        var i = Root;
        for (;;)
        {
            while (Nodes[i].Child1 != -1)
            {
                pool.Add(i);
                stack.Push(Nodes[i].Child2);
                i = Nodes[i].Child1;
            }
            ref var leaf = ref Nodes[i];
            var qz = Spread(Quantise((leaf.Min.Z - rootMin.Z) * sz));
            var qy = Spread(Quantise((leaf.Min.Y - rootMin.Y) * sy));
            var qx = Spread(Quantise((leaf.Min.X - rootMin.X) * sx));
            records.Add(new MortonRecord { Code = qx + (qy + qz * 2) * 2, Leaf = i });
            if (stack.Count == 0)
                break;
            i = stack.Pop();
        }

        var sorted = CollectionsMarshal.AsSpan(records);
        MsvcSort.Sort(sorted, new ByCode());

        var n = sorted.Length;
        var poolCount = pool.Count;
        var list = new List<int>(2 * n);
        var smear = (uint)n - 1;
        smear |= smear >> 1;
        smear |= smear >> 2;
        smear |= smear >> 4;
        smear |= smear >> 8;
        var pow2 = (int)((smear >> 16 | smear) + 1);
        var paired = 0;
        if (n != pow2)
        {
            paired = n * 2 - pow2;
            for (var j = 0; j < paired; j += 2)
                list.Add(Join(pool[--poolCount], sorted[j].Leaf, sorted[j + 1].Leaf));
        }
        for (var j = paired; j < n; j++)
            list.Add(sorted[j].Leaf);

        var start = 0;
        var levelSize = list.Count;
        while (levelSize > 1)
        {
            var end = list.Count;
            for (var j = start; j + 1 < end; j += 2)
                list.Add(Join(pool[--poolCount], list[j], list[j + 1]));
            levelSize = list.Count - end;
            start = end;
        }
        Root = list[^1];
    }

    /// <summary>Makes <paramref name="parent"/> the parent of first and second (the rebuild's pairing step).</summary>
    private int Join(int parent, int first, int second)
    {
        ref var p = ref Nodes[parent];
        ref var a = ref Nodes[first];
        ref var b = ref Nodes[second];
        var box = Union(a.Box, b.Box);
        p.Min = box.Min;
        p.Child1 = first;
        p.Max = box.Max;
        p.Child2 = second;
        p.SetHeight(1 + Math.Max(a.Height, b.Height));
        a.Parent = parent;
        b.Parent = parent;
        return parent;
    }

    /// <summary>cvttss2si(maxss(0, minss(f, 1023))) &amp; 0x3ff: NaN goes to 1023.</summary>
    private static uint Quantise(float f)
    {
        var m = MinSs(f, 1023f);
        m = 0f > m ? 0f : m;
        return (uint)(int)m & 0x3ff;
    }

    /// <summary>Spreads 10 bits to every third bit.</summary>
    private static uint Spread(uint x)
    {
        x = ((x << 16) ^ x) & 0xff0000ff;
        x = ((x << 8) ^ x) & 0x0300f00f;
        x = ((x << 4) ^ x) & 0x030c30c3;
        x = ((x << 2) ^ x) & 0x09249249;
        return x;
    }

    // ---------------------------------------------------------------- queries

    /// <summary>
    /// FUN_180334990 with FUN_180334a40: the roots of the subtrees of height at
    /// most <paramref name="maxHeight"/>, child1 first. The tree-vs-tree pair
    /// query starts one descent per entry.
    /// </summary>
    public void CollectSubtrees(int maxHeight, List<int> output)
    {
        if (Root == -1)
            return;
        if (maxHeight < Nodes[Root].Height)
        {
            CollectSubtrees(maxHeight, Nodes[Root].Child1, output);
            CollectSubtrees(maxHeight, Nodes[Root].Child2, output);
        }
        else
            output.Add(Root);
    }

    private void CollectSubtrees(int maxHeight, int node, List<int> output)
    {
        while (maxHeight < Nodes[node].Height)
        {
            CollectSubtrees(maxHeight, Nodes[node].Child1, output);
            node = Nodes[node].Child2;
        }
        output.Add(node);
    }

    /// <summary>Strict overlap on x, y, z: rejected when a.max &lt; b.min or b.max &lt; a.min.</summary>
    private static bool Overlaps(in TreeNode a, in Aabb b)
        => !(a.Max.X < b.Min.X || a.Max.Y < b.Min.Y || a.Max.Z < b.Min.Z
             || b.Max.X < a.Min.X || b.Max.Y < a.Min.Y || b.Max.Z < a.Min.Z);

    private static bool Overlaps(in TreeNode a, in TreeNode b)
        => !(a.Max.X < b.Min.X || a.Max.Y < b.Min.Y || a.Max.Z < b.Min.Z
             || b.Max.X < a.Min.X || b.Max.Y < a.Min.Y || b.Max.Z < a.Min.Z);

    /// <summary>
    /// FUN_1801deca0 (<paramref name="sameTree"/>) and FUN_1801df510: a moved
    /// leaf's fat box against this tree, depth first with child1 first. The
    /// query leaf is <paramref name="query"/>; against its own tree a leaf of
    /// the same shape is skipped.
    /// </summary>
    public void Query<TSink>(in TreeNode query, bool sameTree, ref TSink sink) where TSink : IPairSink
    {
        if (Root < 0)
            return;
        var box = query.Box;
        var stack = new Stack<int>();
        var i = Root;
        for (;;)
        {
            ref var n = ref Nodes[i];
            if (Overlaps(n, box))
            {
                if (n.Child1 != -1)
                {
                    stack.Push(n.Child2);
                    i = n.Child1;
                    continue;
                }
                if (!sameTree || query.Shape != n.Shape)
                    LeafPair(query, n, ref sink);
            }
            if (stack.Count == 0)
                return;
            i = stack.Pop();
        }
    }

    /// <summary>
    /// The leaf-leaf step all four queries share: two plain shapes make a
    /// pair (a, b); a compound and a plain one hand the compound's child walk
    /// over; two compounds make nothing.
    /// </summary>
    private static void LeafPair<TSink>(in TreeNode a, in TreeNode b, ref TSink sink) where TSink : IPairSink
    {
        if (!a.Compound && !b.Compound)
            sink.Pair(a.Shape, b.Shape);
        else if (!a.Compound)
            sink.Compound(b.Shape, a.Shape, a.Box);
        else if (!b.Compound)
            sink.Compound(a.Shape, b.Shape, b.Box);
    }

    /// <summary>
    /// FUN_1801dfd80 (<paramref name="other"/> is this tree) and FUN_1801e07d0:
    /// a simultaneous descent from (<paramref name="a"/> in this tree,
    /// <paramref name="b"/> in <paramref name="other"/>), splitting the node
    /// of greater height (b on a tie). Within one tree a node met with itself
    /// yields (c1,c1), (c2,c2), (c1,c2) only, and a leaf is not paired with
    /// its own shape.
    /// </summary>
    public void QueryTree<TSink>(int a, DynamicTree other, int b, ref TSink sink) where TSink : IPairSink
    {
        var self = ReferenceEquals(other, this);
        var stack = new Stack<(int, int)>();
        for (;;)
        {
            ref var na = ref Nodes[a];
            ref var nb = ref other.Nodes[b];
            if (Overlaps(na, nb))
            {
                var a1 = na.Child1;
                var b1 = nb.Child1;
                if (a1 == -1)
                {
                    if (b1 == -1)
                    {
                        if (!self || na.Shape != nb.Shape)
                            LeafPair(na, nb, ref sink);
                    }
                    else
                    {
                        stack.Push((a, nb.Child2));
                        b = b1;
                        continue;
                    }
                }
                else if (b1 == -1)
                {
                    stack.Push((na.Child2, b));
                    a = a1;
                    continue;
                }
                else if (self && a == b)
                {
                    stack.Push((a1, nb.Child2));
                    stack.Push((na.Child2, nb.Child2));
                    b = b1;
                    a = a1;
                    continue;
                }
                else if ((int)(nb.Bits << 1) < (int)(na.Bits << 1))
                {
                    stack.Push((na.Child2, b));
                    a = a1;
                    continue;
                }
                else
                {
                    stack.Push((a, nb.Child2));
                    b = b1;
                    continue;
                }
            }
            if (stack.Count == 0)
                return;
            (a, b) = stack.Pop();
        }
    }

    // ---------------------------------------------------------------- layout

    /// <summary>The node array as Valve's bytes (0x30 per node).</summary>
    public ReadOnlySpan<byte> NodeBytes => MemoryMarshal.AsBytes(Nodes.AsSpan(0, Capacity));
}
