namespace Source2.Compiler.Simulation;

/// <summary>
/// Rubikon's CBroadphase (vphysics2, CS2 2026-09-24): seven dynamic trees
/// chosen by body type and the per-body "fast" bit, proxies kept in them for
/// every shape, the hierarchy update that flushes a step's moves into the
/// trees, the queries that turn moved proxies into overlapping pairs, the pair
/// filter, the set of pairs that already have contacts, and the sorted list of
/// new contacts. Ported function by function and checked against the DLL in
/// BroadphaseOracleTests.
///
/// <para>Single-threaded: Valve's jobs only reorder work whose results are
/// sorted or independent per tree. Compound (type 4) shapes and the wheel
/// contact path are not supported.</para>
/// </summary>
public sealed class Broadphase
{
    /// <summary>One tree with the CBroadphase fields around it (0x58 bytes in Valve).</summary>
    public sealed class Tree
    {
        public required DynamicTree Nodes;

        /// <summary>+0x50: 1 static, 2 kinematic, 4 dynamic, 6 non-colliding.</summary>
        public byte Kind;

        /// <summary>+0x51: the tree's own index.</summary>
        public byte Index;

        /// <summary>+0x52: bit 0 rebuilt every solve, bit 1 no pairs with another bit-1 tree, bit 2 no pairs at all.</summary>
        public byte Flags;

        /// <summary>+0x53: pairs within the tree.</summary>
        public bool SelfCollide;

        /// <summary>+0x40/+0x44: the trees this one queries.</summary>
        public readonly List<byte> Targets = [];

        /// <summary>+0x20: one bit per word of <see cref="LeafBits"/> that may hold a dirty leaf.</summary>
        public readonly List<uint> TopBits = [];

        /// <summary>+0x30: one bit per leaf touched since the last pre-step query.</summary>
        public readonly List<uint> LeafBits = [];

        public float Margin => Nodes.Margin;

        /// <summary>Grows the dirty bitsets to cover <paramref name="leaf"/> (FUN_1802d5a90, FUN_1802d63e0).</summary>
        public void Cover(int leaf)
        {
            var words = ((leaf & 0x1fffffff) >> 5) + 1;
            if (LeafBits.Count >= words)
                return;
            while (LeafBits.Count < words)
                LeafBits.Add(0);
            var top = ((leaf & 0x1fffffff) >> 10) + 1;
            while (TopBits.Count < top)
                TopBits.Add(0);
        }

        /// <summary>FUN_1802d6c90: marks a leaf for the pre-step query.</summary>
        public void Touch(int leaf)
        {
            LeafBits[leaf >> 5] |= 1u << (leaf & 31);
            TopBits[leaf >> 10] |= 1u << ((leaf >> 5) & 31);
        }
    }

    public readonly Tree[] Trees = new Tree[7];

    /// <summary>+0x26C: tree index for roles 0..6 (identity).</summary>
    public readonly byte[] Roles = [0, 1, 2, 3, 4, 5, 6];

    /// <summary>+0x273: collision groups whose fast kinematic bodies go to tree 3.</summary>
    public readonly bool[] FastGroups = new bool[64];

    /// <summary>+0x2B4: solves so far; every fourth rebuilds the fast trees, the others refit.</summary>
    public int SolveCount;

    /// <summary>+0x2B8: pairs that have a contact.</summary>
    public readonly PairSet Pairs = new();

    /// <summary>DAT_18045B328: the 64x64 collision group table the pair filter reads.</summary>
    public readonly ushort[] GroupTable;

    /// <summary>DAT_18045B2B8: the function-mask bits that still count as colliding.</summary>
    public readonly ushort CollidingMask;

    /// <summary>The world's disabled body pairs (CRnWorld+0x790), as (lower id, higher id).</summary>
    public readonly HashSet<(uint, uint)> DisabledPairs = [];

    private readonly Dictionary<ulong, BroadphaseShape> _shapes = [];

    private static readonly (byte Kind, byte Flags, float Margin)[] Layout =
    [
        (1, 0, 0f), (2, 0, 4f), (4, 0, 4f), (2, 3, 0.04f), (4, 1, 0.04f), (6, 4, 4f), (6, 5, 0.04f),
    ];

    /// <summary>
    /// FUN_1802d3dd0: the seven trees, their query lists (for each i, j with
    /// the interaction rule, i == j sets self-collide, else each gets the
    /// other once, j's list first), then the group table (FUN_1802d57c0).
    /// </summary>
    public Broadphase(ushort[] groupTable, ushort collidingMask)
    {
        GroupTable = groupTable;
        CollidingMask = collidingMask;
        for (var i = 0; i < 7; i++)
            Trees[i] = new Tree
            {
                Nodes = new DynamicTree(Layout[i].Margin),
                Kind = Layout[i].Kind,
                Index = (byte)i,
                Flags = Layout[i].Flags,
            };
        for (var i = 0; i < 7; i++)
            for (var j = 0; j < 7; j++)
            {
                var a = Trees[i];
                var b = Trees[j];
                if (a.Kind == 1 || (b.Kind == 1 && (a.Kind & 4) == 0) || (a.Flags & 4) != 0 || (b.Flags & 4) != 0
                    || ((a.Flags & 2) != 0 && (b.Flags & 2) != 0))
                    continue;
                if (i == j)
                {
                    a.SelfCollide = true;
                    continue;
                }
                if (!b.Targets.Contains((byte)i))
                    b.Targets.Add((byte)i);
                if (!a.Targets.Contains((byte)j))
                    a.Targets.Add((byte)j);
            }
        SetGroupTable();
    }

    private struct ByCountDescending : ILess<(int Group, int Count)>
    {
        public readonly bool Less(in (int Group, int Count) a, in (int Group, int Count) b) => a.Count > b.Count;
    }

    /// <summary>
    /// FUN_1802d57c0: groups, most self-colliding (bit 0) first, are fast
    /// unless they meet a group already fast, or themselves, with a nonzero
    /// entry lacking bit 0.
    /// </summary>
    private void SetGroupTable()
    {
        Array.Clear(FastGroups);
        var order = new (int Group, int Count)[64];
        for (var i = 0; i < 64; i++)
        {
            var count = 0;
            for (var j = 0; j < 64; j++)
                if ((GroupTable[i * 64 + j] & 1) != 0)
                    count++;
            order[i] = (i, count);
        }
        MsvcSort.Sort(order.AsSpan(), new ByCountDescending());
        foreach (var (group, _) in order)
        {
            var fast = true;
            for (var j = 0; j < 64; j++)
            {
                var entry = GroupTable[group * 64 + j];
                if ((FastGroups[j] || j == group) && entry != 0 && (entry & 1) == 0)
                {
                    fast = false;
                    break;
                }
            }
            FastGroups[group] = fast;
        }
    }

    public BroadphaseShape? Shape(ulong handle) => _shapes.GetValueOrDefault(handle);

    // ---------------------------------------------------------------- proxies

    /// <summary>
    /// FUN_1802d59b0's choice (also inlined in MoveProxy and FUN_1802d7560):
    /// the body type's tree, the non-colliding pair (5, 6) for a shape whose
    /// flags 0x25 are clear or whose mask falls inside the colliding bits, and
    /// the fast trees (3 for a kinematic body of a fast group, 4 for a dynamic
    /// one) when the body's bit 7 is set.
    /// </summary>
    public int SelectTree(BroadphaseShape shape)
    {
        ref readonly var body = ref shape.Body.State;
        var type = body.BodyType;
        if (shape.Type == 4)
            return type;
        var fastBit = (body.Flags249 & 0x80) != 0;
        byte tree;
        var nonColliding = type != 0;
        if (nonColliding && (shape.Attributes.Flags & 0x25) != 0)
        {
            var mask = shape.Attributes.MaskIsDirect == 1 ? shape.Attributes.FunctionMask : (ushort)~shape.Attributes.FunctionMask;
            if ((mask & CollidingMask) != mask)
                nonColliding = false;
        }
        if (nonColliding)
            tree = fastBit ? Roles[6] : Roles[5];
        else
        {
            if (!fastBit)
                return type;
            if (type == 1)
            {
                if (!FastGroups[shape.Attributes.Group])
                    return type;
                tree = Roles[3];
            }
            else if (type == 2)
                tree = Roles[4];
            else
                return type;
        }
        return tree != 0xff ? tree : type;
    }

    /// <summary>
    /// FUN_1802d59b0 / FUN_1802d5a90: a leaf for <paramref name="aabb"/> in
    /// the chosen tree, the dirty bitsets grown and the leaf touched.
    /// </summary>
    public void CreateProxy(BroadphaseShape shape, in Aabb aabb)
    {
        _shapes[shape.Handle] = shape;
        shape.ProxyId = Insert(SelectTree(shape), shape, aabb, touch: true);
    }

    private int Insert(int t, BroadphaseShape shape, in Aabb aabb, bool touch)
    {
        var tree = Trees[t];
        var leaf = tree.Nodes.Insert(aabb, shape.Handle, shape.Type == 4, tree.Margin);
        if (leaf == -1)
            return -1;
        tree.Cover(leaf);
        if (touch)
            tree.Touch(leaf);
        return leaf * 8 | (t & 7);
    }

    /// <summary>FUN_1802d5c30: removes the leaf (clearing its dirty bit, not the word's top bit).</summary>
    public void DestroyProxy(BroadphaseShape shape)
    {
        var id = shape.ProxyId;
        if (id == -1)
            return;
        var tree = Trees[id & 7];
        tree.LeafBits[(int)((uint)id >> 8)] &= ~(1u << ((id >> 3) & 31));
        tree.Nodes.Remove(id >> 3);
        shape.ProxyId = -1;
    }

    /// <summary>
    /// FUN_1802d7560: when the shape now belongs in another tree, its fat box
    /// moves there as a new proxy (fattened again). Only shapes that have a
    /// proxy are handled.
    /// </summary>
    public void ReselectTree(BroadphaseShape shape)
    {
        var t = SelectTree(shape);
        var id = shape.ProxyId;
        if (id == -1 || (id & 7) == t)
            return;
        var old = Trees[id & 7];
        var leaf = id >> 3;
        var box = old.Nodes.Nodes[leaf].Box;
        old.LeafBits[(int)((uint)id >> 8)] &= ~(1u << (leaf & 31));
        old.Nodes.Remove(leaf);
        shape.ProxyId = Insert(t, shape, box, touch: true);
    }

    /// <summary>FUN_1802d6c90.</summary>
    public void Touch(int id)
    {
        if (id != -1)
            Trees[id & 7].Touch(id >> 3);
    }

    /// <summary>
    /// FUN_1802d6d20, a move outside the step: re-fatten, and when the box
    /// was rewritten unlink and relink the leaf (no rotations); then touch it.
    /// A compound shape is touched either way.
    /// </summary>
    public void MoveImmediate(BroadphaseShape shape, in Aabb aabb)
    {
        var id = shape.ProxyId;
        if (id == -1)
            return;
        var tree = Trees[id & 7];
        var leaf = id >> 3;
        if (tree.Nodes.Refatten(leaf, aabb, tree.Margin))
        {
            tree.Nodes.Unlink(leaf);
            tree.Nodes.Relink(leaf);
        }
        else if (shape.Type != 4)
            return;
        tree.Touch(leaf);
    }

    /// <summary>
    /// FUN_1802d6de0, a move inside a hierarchy update. When the tree stays,
    /// a rebuilt tree gets the box written straight in, the others re-fatten
    /// and list the leaf. When the tree changes, the leaf is listed for
    /// removal and the shape with its box (or its fat box, for an empty box)
    /// is listed for the new tree.
    /// </summary>
    public void MoveProxy(BroadphaseShape shape, in Aabb aabb, bool checkTreeChange, HierarchyUpdate update)
    {
        var id = shape.ProxyId;
        if (id == -1)
            return;
        var t = id & 7;
        var tree = Trees[t];
        var leaf = id >> 3;
        var target = checkTreeChange ? SelectTree(shape) : t;
        if (target == t)
        {
            var record = update.Records[t];
            if (!record.Refatten)
            {
                if (aabb.Min.X <= aabb.Max.X && aabb.Min.Y <= aabb.Max.Y && aabb.Min.Z <= aabb.Max.Z)
                {
                    ref var n = ref tree.Nodes.Nodes[leaf];
                    var m = tree.Margin;
                    n.Max = new(aabb.Max.X + m, m + aabb.Max.Y, aabb.Max.Z + m);
                    n.Min = new(aabb.Min.X - m, aabb.Min.Y - m, aabb.Min.Z - m);
                }
            }
            else if (tree.Nodes.Refatten(leaf, aabb, tree.Margin) || shape.Type == 4)
                record.Moved.Add(leaf);
            return;
        }
        var box = aabb;
        if (aabb.Max.X < aabb.Min.X || aabb.Max.Y < aabb.Min.Y || aabb.Max.Z < aabb.Min.Z)
            box = tree.Nodes.Nodes[leaf].Box;
        update.Records[t].Removed.Add(leaf);
        update.Records[target].Added.Add((shape, box));
    }

    /// <summary>
    /// FUN_1801b9270: every shape's box at the step's start and end transforms
    /// (the same unless the body is swept, +0x249 bit 6), their union, and
    /// MoveProxy.
    /// </summary>
    public void UpdateBody(BroadphaseBody body, bool checkTreeChange, HierarchyUpdate update)
    {
        var (start, end) = Frames(body.State);
        foreach (var shape in body.Shapes)
            MoveProxy(shape, Union(shape.ComputeAabb(start), shape.ComputeAabb(end)), checkTreeChange, update);
    }

    /// <summary>FUN_1801b9100: the same boxes for an enabled body, moved immediately.</summary>
    public void MoveBodyImmediate(BroadphaseBody body)
    {
        if ((body.State.Flags249 & 1) == 0)
            return;
        var (start, end) = Frames(body.State);
        foreach (var shape in body.Shapes)
            MoveImmediate(shape, Union(shape.ComputeAabb(start), shape.ComputeAabb(end)));
    }

    /// <summary>
    /// FUN_1801b9650's proxy step for a shape joining an enabled body: its box
    /// at the body's current frame, when the shape takes a proxy.
    /// </summary>
    public void AddShape(BroadphaseShape shape)
    {
        _shapes[shape.Handle] = shape;
        if ((shape.Body.State.Flags249 & 1) != 0 && shape.HasProxy)
            CreateProxy(shape, shape.ComputeAabb(RnTransform.Of(shape.Body.State)));
    }

    /// <summary>
    /// FUN_1801b2690: the end frame from the orientation and centre of mass,
    /// the start frame from the previous ones when the body is swept. The
    /// vehicle offset (+0x4A bit 3) is not supported.
    /// </summary>
    public static (RnTransform Start, RnTransform End) Frames(in RnBodyState b)
    {
        if ((b.Flags4A & 8) != 0)
            throw new NotSupportedException("the +0x4A bit 3 body offset");
        var end = RnTransform.Of(b);
        if ((b.Flags249 & 0x40) == 0)
            return (end, end);
        var scaled = new Vec3(b.Scale * b.LocalMassCenter.X, b.Scale * b.LocalMassCenter.Y, b.Scale * b.LocalMassCenter.Z);
        return (RnTransform.Of(b.PreviousOrientation, b.PreviousPosition, scaled), end);
    }

    private static Aabb Union(in Aabb a, in Aabb b) => new(
        new(a.Min.X < b.Min.X ? a.Min.X : b.Min.X, a.Min.Y < b.Min.Y ? a.Min.Y : b.Min.Y, a.Min.Z < b.Min.Z ? a.Min.Z : b.Min.Z),
        new(a.Max.X > b.Max.X ? a.Max.X : b.Max.X, a.Max.Y > b.Max.Y ? a.Max.Y : b.Max.Y, a.Max.Z > b.Max.Z ? a.Max.Z : b.Max.Z));

    // ---------------------------------------------------------------- hierarchy update

    /// <summary>
    /// FUN_1802d4ed0: every tree refits the listed leaves (mode 1) with the
    /// containment rule, except in a solve (<paramref name="mode"/> 0) the
    /// fast trees (flags bit 0) take their boxes directly and are rebuilt
    /// (mode 3) on every fourth solve and refit (mode 4) otherwise.
    /// </summary>
    public void BeginHierarchyUpdate(HierarchyUpdate update, int mode, int threads, byte priority)
    {
        update.Priority = priority;
        update.Mode = mode;
        update.Threads = threads;
        for (var i = 0; i < 7; i++)
        {
            var record = update.Records[i];
            record.RebuildMode = 1;
            record.Refatten = true;
            if ((Trees[i].Flags & 1) != 0 && mode == 0)
            {
                record.RebuildMode = 3 + ((SolveCount & 3) != 0 ? 1 : 0);
                record.Refatten = false;
            }
            record.Moved.Marker = record.Moved.Count;
            record.Removed.Marker = record.Removed.Count;
            record.Added.Marker = record.Added.Count;
        }
    }

    /// <summary>
    /// FUN_1802d6050: counts the solve, resets the pair query, flushes each
    /// tree (FUN_1802d63e0), empties the lists and prunes the query jobs.
    /// </summary>
    public void FinalizeHierarchyUpdate(HierarchyUpdate update, PairQuery query)
    {
        if (update.Mode == 0)
            SolveCount++;
        query.Reset(update.Mode, update.Threads, update.Priority);
        for (var t = 0; t < 7; t++)
            FlushTree(t, update, query);
        update.Mode = 3;
        foreach (var record in update.Records)
        {
            record.Moved.Clear();
            record.Removed.Clear();
            record.Added.Clear();
        }
        Prune(query);
    }

    private struct ByOldProxy : ILess<(BroadphaseShape Shape, Aabb Box)>
    {
        public readonly bool Less(in (BroadphaseShape Shape, Aabb Box) a, in (BroadphaseShape Shape, Aabb Box) b)
            => (uint)a.Shape.ProxyId < (uint)b.Shape.ProxyId;
    }

    private struct Ascending : ILess<int>
    {
        public readonly bool Less(in int a, in int b) => a < b;
    }

    /// <summary>
    /// FUN_1802d63e0: sorts what each list gained since Begin when threaded
    /// (leaves ascending, adds by their old proxy id), removes the leaves that
    /// changed tree, rebuilds or refits, inserts the arrivals (grown bitsets,
    /// untouched), and builds this tree's query input.
    /// </summary>
    private void FlushTree(int t, HierarchyUpdate update, PairQuery query)
    {
        var tree = Trees[t];
        var record = update.Records[t];
        if (update.Threads > 1)
        {
            record.Moved.SortNew(new Ascending());
            record.Added.SortNew(new ByOldProxy());
            record.Removed.SortNew(new Ascending());
        }
        foreach (var leaf in record.Removed.Items)
            tree.Nodes.Remove(leaf);
        if (record.RebuildMode > 1 || record.Moved.Count != 0)
            tree.Nodes.Rebuild(record.RebuildMode, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(record.Moved.Items));
        foreach (var (shape, box) in record.Added.Items)
            shape.ProxyId = Insert(t, shape, box, touch: false);
        BuildQueryInput(query.Records[t], tree, record, update.Mode);
    }

    /// <summary>
    /// FUN_1802d4d60: the moved leaves then the arrivals, or, for a fast tree
    /// in a solve, the roots of its subtrees of height 3 or less for a
    /// tree-against-tree query.
    /// </summary>
    private static void BuildQueryInput(PairQuery.Record q, Tree tree, HierarchyUpdate.Record record, int mode)
    {
        if ((tree.Flags & 1) == 0 || mode != 0)
        {
            q.Items.AddRange(record.Moved.Items);
            record.Moved.Clear();
            foreach (var (shape, _) in record.Added.Items)
                if (shape.ProxyId != -1)
                    q.Items.Add((int)((uint)shape.ProxyId >> 3));
            q.TreeVsTree = false;
            q.AllMoved = q.Items.Count == tree.Nodes.LeafCount;
        }
        else
        {
            record.Moved.Clear();
            tree.Nodes.CollectSubtrees(3, q.Items);
            q.AllMoved = true;
            q.TreeVsTree = true;
        }
        Describe(q, tree);
    }

    private static void Describe(PairQuery.Record q, Tree tree)
    {
        q.NonEmpty = tree.Nodes.LeafCount > 0;
        q.Self = tree.SelfCollide;
        if (q.Items.Count > 0)
            q.Targets.AddRange(tree.Targets);
    }

    /// <summary>
    /// FUN_1802d5740 with FUN_1802d4b00: every leaf touched since the last
    /// call, ascending, becomes a moved leaf; the bitsets are cleared.
    /// </summary>
    public void PreStepQuery(PairQuery query, int threads, byte priority)
    {
        query.Reset(2, threads, priority);
        for (var t = 0; t < 7; t++)
        {
            var tree = Trees[t];
            var q = query.Records[t];
            for (var w = 0; w < tree.TopBits.Count; w++)
                for (var top = tree.TopBits[w]; top != 0;)
                {
                    var b = System.Numerics.BitOperations.TrailingZeroCount(top);
                    top ^= 1u << b;
                    var word = b + w * 32;
                    for (var bits = tree.LeafBits[word]; bits != 0;)
                    {
                        var c = System.Numerics.BitOperations.TrailingZeroCount(bits);
                        bits ^= 1u << c;
                        q.Items.Add(c + word * 32);
                    }
                }
            for (var i = 0; i < tree.LeafBits.Count; i++)
                tree.LeafBits[i] = 0;
            for (var i = 0; i < tree.TopBits.Count; i++)
                tree.TopBits[i] = 0;
            q.TreeVsTree = false;
            q.AllMoved = q.Items.Count == tree.Nodes.LeafCount;
            Describe(q, tree);
        }
        Prune(query);
    }

    /// <summary>
    /// FUN_1802d6290: a target whose every leaf moved is dropped when it will
    /// query this tree itself (it is not empty, this one's leaves all moved
    /// too, and it has more moved leaves, or as many and a higher index).
    /// Trees with work become jobs, in index order.
    /// </summary>
    private static void Prune(PairQuery query)
    {
        for (var i = 0; i < 7; i++)
        {
            var q = query.Records[i];
            if (q.Items.Count != 0)
                for (var k = q.Targets.Count - 1; k >= 0; k--)
                {
                    var j = q.Targets[k];
                    var o = query.Records[j];
                    if (o.AllMoved && (!o.NonEmpty || !q.AllMoved || o.Items.Count < q.Items.Count
                                       || (q.Items.Count == o.Items.Count && i < j)))
                        q.Targets.RemoveAt(k);
                }
            var searches = q.Targets.Count + (q.Self ? 1 : 0);
            var work = q.Items.Count * searches;
            if (work > 0)
            {
                query.Searches += searches;
                query.Moved += q.Items.Count;
                query.Work += work;
                query.Jobs.Add(i);
            }
        }
    }

    // ---------------------------------------------------------------- pairs

    /// <summary>A contact the pair query made, with its sort key (contact+0x40).</summary>
    public readonly record struct NewContact(BroadphaseShape A, BroadphaseShape B, int SubA, int SubB, ushort Flags)
    {
        public ulong Key => (ulong)(uint)A.ProxyId << 32 | (uint)B.ProxyId;
    }

    private struct ByKey : ILess<NewContact>
    {
        public readonly bool Less(in NewContact a, in NewContact b) => a.Key < b.Key;
    }

    /// <summary>
    /// CRnWorld::BuildNewContactsFromOverlappingPairQuery (FUN_1801f1dc0):
    /// runs the query jobs, sorts the new contacts by key (FUN_1801f8d90 /
    /// FUN_1801e6a70) and keeps each whose pair enters the pair set, in key
    /// order: the order they join the world's lists. Then the query resets.
    /// </summary>
    public List<NewContact> BuildNewContacts(PairQuery query)
    {
        var kept = new List<NewContact>();
        if (query.Work > 0)
        {
            var found = new List<NewContact>();
            var sink = new Sink(this, found);
            RunQuery(query, ref sink);
            if (found.Count > 1)
                MsvcSort.Sort(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(found), new ByKey());
            foreach (var c in found)
                if (Pairs.Insert(c.A.Handle, c.B.Handle))
                    kept.Add(c);
        }
        query.Clear();
        return kept;
    }

    /// <summary>
    /// FUN_1801eacc0 / FUN_1801e2bf0, single-threaded: per job tree, its
    /// moved leaves against itself, then against each target in list order;
    /// or its subtree roots against its root and each target's root.
    /// </summary>
    public void RunQuery<TSink>(PairQuery query, ref TSink sink) where TSink : IPairSink
    {
        foreach (var t in query.Jobs)
        {
            var q = query.Records[t];
            var tree = Trees[t].Nodes;
            if (!q.TreeVsTree)
            {
                if (q.Self)
                    foreach (var leaf in q.Items)
                        tree.Query(tree.Nodes[leaf], sameTree: true, ref sink);
                foreach (var j in q.Targets)
                    foreach (var leaf in q.Items)
                        Trees[j].Nodes.Query(tree.Nodes[leaf], sameTree: false, ref sink);
            }
            else
            {
                if (q.Self)
                    foreach (var s in q.Items)
                        tree.QueryTree(s, tree, tree.Root, ref sink);
                foreach (var j in q.Targets)
                    foreach (var s in q.Items)
                        tree.QueryTree(s, Trees[j].Nodes, Trees[j].Nodes.Root, ref sink);
            }
        }
    }

    private readonly struct Sink(Broadphase broadphase, List<NewContact> found) : IPairSink
    {
        public void Pair(ulong a, ulong b)
        {
            if (broadphase.Pairs.Contains(a, b))
                return;
            if (broadphase.CreateContact(broadphase._shapes[a], broadphase._shapes[b], -1, -1) is { } c)
                found.Add(c);
        }

        public void Compound(ulong compound, ulong other, in Aabb otherBox)
            => throw new NotSupportedException("compound shapes");
    }

    /// <summary>
    /// FUN_1802038b0: the attribute filter, the body veto, the world's
    /// disabled body pairs, then the contact's shape order (FUN_1801d1390):
    /// two convex shapes put the lower type first (the first argument on a
    /// tie); a mesh goes second; two meshes make nothing.
    /// </summary>
    public NewContact? CreateContact(BroadphaseShape a, BroadphaseShape b, int subA, int subB)
    {
        var flags = CollisionFilter.Flags(GroupTable, a.Attributes, b.Attributes);
        if (flags == 0 || !CollisionFilter.BodiesMayCollide(a, b, flags))
            return null;
        if (DisabledPairs.Count != 0)
        {
            var (x, y) = (a.Body.Id, b.Body.Id);
            if (DisabledPairs.Contains(x <= y ? (x, y) : (y, x)))
                return null;
        }
        if (((a.Body.State.Flags4A ^ b.Body.State.Flags4A) & 8) != 0)
            throw new NotSupportedException("the +0x4A bit 3 contact");
        var meshA = a.Type == BroadphaseShape.MeshType;
        var meshB = b.Type == BroadphaseShape.MeshType;
        if (!meshA && !meshB)
            return a.Type <= b.Type ? new NewContact(a, b, subA, subB, flags) : new NewContact(b, a, subB, subA, flags);
        if (meshA && meshB)
            return null;
        return meshA ? new NewContact(b, a, subB, subA, flags) : new NewContact(a, b, subA, subB, flags);
    }
}

/// <summary>A list the hierarchy update fills during a step, with the Begin marker its sort starts from.</summary>
public sealed class StepList<T>
{
    public readonly List<T> Items = [];

    /// <summary>The count at Begin (the atomic vector's +0x30).</summary>
    public int Marker;

    public int Count => Items.Count;

    public void Add(T item) => Items.Add(item);

    public void Clear()
    {
        Items.Clear();
        Marker = 0;
    }

    /// <summary>Sorts what came after the marker, when more than one item did.</summary>
    public void SortNew<TLess>(TLess less) where TLess : struct, ILess<T>
    {
        if (Items.Count - Marker > 1)
            MsvcSort.Sort(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Items)[Marker..], less);
    }
}

/// <summary>The per-step hierarchy update (the scratch's +0 record set).</summary>
public sealed class HierarchyUpdate
{
    /// <summary>One tree's record (0xB0 bytes in Valve).</summary>
    public sealed class Record
    {
        /// <summary>+0x00: leaves moved in place.</summary>
        public readonly StepList<int> Moved = new();

        /// <summary>+0x38: leaves leaving the tree.</summary>
        public readonly StepList<int> Removed = new();

        /// <summary>+0x70: shapes arriving, with their boxes.</summary>
        public readonly StepList<(BroadphaseShape Shape, Aabb Box)> Added = new();

        /// <summary>+0xA8: the rebuild mode Finalize applies.</summary>
        public int RebuildMode;

        /// <summary>+0xAC: moves re-fatten and list (true) or write boxes straight in (false).</summary>
        public bool Refatten;
    }

    public readonly Record[] Records = [new(), new(), new(), new(), new(), new(), new()];

    /// <summary>+0x4D8: 0 solve, 1 TOI pass, 3 after Finalize.</summary>
    public int Mode;

    /// <summary>+0x4DC: the world's thread setting; above 1 the lists are sorted.</summary>
    public int Threads;

    public byte Priority;
}

/// <summary>The pair query input (the scratch's +0x4E8).</summary>
public sealed class PairQuery
{
    /// <summary>One tree's query (0x58 bytes in Valve).</summary>
    public sealed class Record
    {
        /// <summary>+0x00: moved leaves, or subtree roots.</summary>
        public readonly List<int> Items = [];

        /// <summary>+0x10: the trees to query, after pruning.</summary>
        public readonly List<byte> Targets = [];

        public bool Self, AllMoved, NonEmpty, TreeVsTree;

        public void Clear()
        {
            Items.Clear();
            Targets.Clear();
            AllMoved = false;
            NonEmpty = false;
        }
    }

    public readonly Record[] Records = [new(), new(), new(), new(), new(), new(), new()];

    /// <summary>+0x270: 0 solve, 1 TOI, 2 pre-step, 3 done.</summary>
    public int Mode = 3;

    /// <summary>+0x274/+0x278: the trees with work, in index order.</summary>
    public readonly List<int> Jobs = [];

    /// <summary>+0x294, +0x298, +0x29C: moved leaves times searches, moved leaves, searches.</summary>
    public int Work, Moved, Searches;

    public int Threads;
    public byte Priority;

    /// <summary>FUN_1802d49f0: the mode, threads and priority (the records were cleared after the last query).</summary>
    public void Reset(int mode, int threads, byte priority)
    {
        Mode = mode;
        Threads = threads;
        Priority = priority;
    }

    /// <summary>FUN_1802d5d10.</summary>
    public void Clear()
    {
        Mode = 3;
        foreach (var r in Records)
            r.Clear();
        Jobs.Clear();
        Work = Moved = Searches = 0;
    }
}
