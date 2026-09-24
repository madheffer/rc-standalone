using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The broadphase tree, ported, against vphysics2's own tree functions run in
/// lockstep on the same random operations: inserts, removes, moves (re-fatten
/// then relink, refit or unlink-relink), Morton rebuilds and full refits. After
/// every operation the whole tree must match: header, free list, and every
/// in-use node byte for byte. The pair queries must report the same pairs in
/// the same order; to see them, the existing-pair lookup (FUN_1801fa710) and
/// the compound child lookup (FUN_1801dbe00) are patched, while a query runs,
/// to record their arguments and report "already paired", so Valve never goes
/// on to create a contact.
/// </summary>
[Collection(Vphysics2PatchCollection.Name)]
public unsafe class DynamicTreeOracleTests(ITestOutputHelper output)
{
    private const ulong InitVa = 0x180333f20;
    private const ulong InsertVa = 0x180334130;
    private const ulong RemoveVa = 0x1803342b0;
    private const ulong UnlinkVa = 0x180335cf0;
    private const ulong RelinkVa = 0x180335ce0;
    private const ulong RebuildVa = 0x180336c20;
    private const ulong RefattenVa = 0x1802d7380;
    private const ulong SubtreesVa = 0x180334990;
    private const ulong SortVa = 0x180333af0;
    private const ulong QuerySelfVa = 0x1801deca0;
    private const ulong QueryOtherVa = 0x1801df510;
    private const ulong TreeSelfVa = 0x1801dfd80;
    private const ulong TreeOtherVa = 0x1801e07d0;
    private const ulong ExistingPairVa = 0x1801fa710;
    private const ulong CompoundChildVa = 0x1801dbe00;
    private const ulong FreeVa = 0x180085c80;
    private const ulong RotateVa = 0x180336000;
    private const ulong FindBestSiblingVa = 0x180334330;

    [StructLayout(LayoutKind.Sequential)]
    private struct Range
    {
        public int* Ptr;
        public long Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UtlVector
    {
        public int Count;
        public int Alloc;
        public int* Ptr;
    }

    /// <summary>Valve's functions, by address.</summary>
    private sealed class Dll(nint module)
    {
        public readonly delegate* unmanaged<byte*, byte*> Init = (delegate* unmanaged<byte*, byte*>)Vphysics2Oracle.At(module, InitVa);
        public readonly delegate* unmanaged<byte*, Aabb*, ulong, ushort, float, int> Insert =
            (delegate* unmanaged<byte*, Aabb*, ulong, ushort, float, int>)Vphysics2Oracle.At(module, InsertVa);
        public readonly delegate* unmanaged<byte*, int, ulong> Remove = (delegate* unmanaged<byte*, int, ulong>)Vphysics2Oracle.At(module, RemoveVa);
        public readonly delegate* unmanaged<byte*, int, void> Unlink = (delegate* unmanaged<byte*, int, void>)Vphysics2Oracle.At(module, UnlinkVa);
        public readonly delegate* unmanaged<byte*, int, void> Relink = (delegate* unmanaged<byte*, int, void>)Vphysics2Oracle.At(module, RelinkVa);
        public readonly delegate* unmanaged<byte*, int, Range*, void> Rebuild =
            (delegate* unmanaged<byte*, int, Range*, void>)Vphysics2Oracle.At(module, RebuildVa);
        public readonly delegate* unmanaged<byte*, int, Aabb*, float, byte> Refatten =
            (delegate* unmanaged<byte*, int, Aabb*, float, byte>)Vphysics2Oracle.At(module, RefattenVa);
        public readonly delegate* unmanaged<byte*, UtlVector*, int, void> Subtrees =
            (delegate* unmanaged<byte*, UtlVector*, int, void>)Vphysics2Oracle.At(module, SubtreesVa);
        public readonly delegate* unmanaged<DynamicTree.MortonRecord*, DynamicTree.MortonRecord*, long, byte, void> Sort =
            (delegate* unmanaged<DynamicTree.MortonRecord*, DynamicTree.MortonRecord*, long, byte, void>)Vphysics2Oracle.At(module, SortVa);
        public readonly delegate* unmanaged<byte*, Aabb*, long*, void> QuerySelf =
            (delegate* unmanaged<byte*, Aabb*, long*, void>)Vphysics2Oracle.At(module, QuerySelfVa);
        public readonly delegate* unmanaged<byte*, Aabb*, long*, void> QueryOther =
            (delegate* unmanaged<byte*, Aabb*, long*, void>)Vphysics2Oracle.At(module, QueryOtherVa);
        public readonly delegate* unmanaged<byte*, byte*, ulong*, long*, void> TreeSelf =
            (delegate* unmanaged<byte*, byte*, ulong*, long*, void>)Vphysics2Oracle.At(module, TreeSelfVa);
        public readonly delegate* unmanaged<byte*, byte*, ulong*, long*, void> TreeOther =
            (delegate* unmanaged<byte*, byte*, ulong*, long*, void>)Vphysics2Oracle.At(module, TreeOtherVa);
        public readonly delegate* unmanaged<void*, void> Free = (delegate* unmanaged<void*, void>)Vphysics2Oracle.At(module, FreeVa);
        public readonly delegate* unmanaged<byte*, int, void> Rotate = (delegate* unmanaged<byte*, int, void>)Vphysics2Oracle.At(module, RotateVa);
        public readonly delegate* unmanaged<byte*, Aabb*, int> FindBestSibling =
            (delegate* unmanaged<byte*, Aabb*, int>)Vphysics2Oracle.At(module, FindBestSiblingVa);
        public readonly nint ExistingPair = Vphysics2Oracle.At(module, ExistingPairVa);
        public readonly nint CompoundChild = Vphysics2Oracle.At(module, CompoundChildVa);
    }

    // ------------------------------------------------------------------ tests

    [Fact]
    public void TheMsvcSortMatchesValves()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var dll = new Dll(module);
        var random = new Random(11);
        for (var trial = 0; trial < 3000; trial++)
        {
            var n = trial % 7 == 0 ? random.Next(0, 40) : random.Next(33, 3000);
            var keys = random.Next(4) switch { 0 => 2, 1 => 16, 2 => n / 4 + 1, _ => 1 << 30 };
            var input = new DynamicTree.MortonRecord[n];
            for (var i = 0; i < n; i++)
                input[i] = new() { Code = (uint)random.Next(keys), Leaf = i };
            if (trial % 11 == 0)
                Array.Sort(input, (a, b) => b.Code.CompareTo(a.Code));
            var valve = (DynamicTree.MortonRecord[])input.Clone();
            fixed (DynamicTree.MortonRecord* p = valve)
                dll.Sort(p, p + n, n, 0);
            var ours = (DynamicTree.MortonRecord[])input.Clone();
            MsvcSort.Sort(ours.AsSpan(), new ByCode());
            for (var i = 0; i < n; i++)
                Assert.True(valve[i].Code == ours[i].Code && valve[i].Leaf == ours[i].Leaf,
                            $"trial {trial} n {n} keys {keys}: first difference at {i}");
        }
    }

    private struct ByCode : ILess<DynamicTree.MortonRecord>
    {
        public readonly bool Less(in DynamicTree.MortonRecord a, in DynamicTree.MortonRecord b) => a.Code < b.Code;
    }

    /// <summary>
    /// FUN_180336000 on hand-built three-level trees of crates in a small
    /// block, so the equal-area cases of every rotation branch come up: the
    /// same seven nodes go into Valve's node array and ours, one rotation
    /// runs on each, and the seven nodes must match.
    /// </summary>
    [Fact]
    public void RotationsMatch()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var dll = new Dll(module);
        var random = new Random(21);
        using var pair = new Pair(dll, 0f);
        var nodes = *(TreeNode**)(pair.Valve + 0x18);
        var ours = pair.Ours;
        var rotated = 0;
        for (var trial = 0; trial < 200000; trial++)
        {
            // A = 0 with B = 1, C = 2; B's children 3, 4 and C's 5, 6 when internal.
            var bLeaf = random.Next(3) == 0;
            var cLeaf = !bLeaf && random.Next(2) == 0;
            var built = new TreeNode[7];
            Aabb Crate()
            {
                var c = new Vec3(random.Next(0, 3) * 32f, random.Next(0, 3) * 32f, random.Next(0, 2) * 32f);
                var h = random.Next(4) == 0 ? new Vec3(16f, 32f, 16f) : new Vec3(16f, 16f, 16f);
                return new(new(c.X - h.X, c.Y - h.Y, c.Z - h.Z), new(c.X + h.X, c.Y + h.Y, c.Z + h.Z));
            }
            void Leaf(int i, int parent)
            {
                var box = Crate();
                built[i] = new TreeNode { Min = box.Min, Max = box.Max, Child1 = -1, Child2 = -1, Parent = parent, Shape = (ulong)(0x1000 + i) };
            }
            void Internal(int i, int parent, int c1, int c2)
            {
                var a = built[c1];
                var b = built[c2];
                built[i] = new TreeNode
                {
                    Min = new(Math.Min(a.Min.X, b.Min.X), Math.Min(a.Min.Y, b.Min.Y), Math.Min(a.Min.Z, b.Min.Z)),
                    Max = new(Math.Max(a.Max.X, b.Max.X), Math.Max(a.Max.Y, b.Max.Y), Math.Max(a.Max.Z, b.Max.Z)),
                    Child1 = c1, Child2 = c2, Parent = parent,
                };
                built[i].SetHeight(1 + Math.Max(a.Height, b.Height));
            }
            if (bLeaf)
                Leaf(1, 0);
            else
            {
                Leaf(3, 1);
                Leaf(4, 1);
                Internal(1, 0, 3, 4);
            }
            if (cLeaf)
                Leaf(2, 0);
            else
            {
                Leaf(5, 2);
                Leaf(6, 2);
                Internal(2, 0, 5, 6);
            }
            Internal(0, -1, 1, 2);
            for (var i = 0; i < 7; i++)
            {
                nodes[i] = built[i];
                ours.Nodes[i] = built[i];
            }
            dll.Rotate(pair.Valve, 0);
            ours.Rotate(0);
            for (var i = 0; i < 7; i++)
            {
                var valve = new ReadOnlySpan<byte>(&nodes[i], sizeof(TreeNode));
                var port = MemoryMarshal.AsBytes(ours.Nodes.AsSpan(i, 1));
                if (!valve.SequenceEqual(port))
                    Assert.Fail($"trial {trial}: node {i} differs after the rotation"
                                + $" valve {Convert.ToHexString(valve)} ours {Convert.ToHexString(port)}");
            }
            if (nodes[0].Child1 != 1 || nodes[0].Child2 != 2)
                rotated++;
        }
        output.WriteLine($"{rotated} of 200000 rotated");
    }

    /// <summary>
    /// FUN_180334330 alone: many crate-sized query boxes against trees of
    /// crates, so the equal lower bounds and equal centre distances the
    /// descent breaks ties on come up.
    /// </summary>
    [Fact]
    public void FindBestSiblingMatches()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var dll = new Dll(module);
        var random = new Random(22);
        for (var tree = 0; tree < 400; tree++)
        {
            using var pair = new Pair(dll, 0f);
            var size = 2 + random.Next(40);
            var extent = 2 + random.Next(4);
            Aabb Crate()
            {
                var c = new Vec3(random.Next(0, extent) * 32f, random.Next(0, extent) * 32f, random.Next(0, 2) * 32f);
                return new(new(c.X - 16f, c.Y - 16f, c.Z - 16f), new(c.X + 16f, c.Y + 16f, c.Z + 16f));
            }
            for (var i = 0; i < size; i++)
            {
                var box = Crate();
                var margin = random.Next(2) == 0 ? 0f : 4f;
                Assert.Equal(dll.Insert(pair.Valve, &box, (ulong)(0x1000 + i), 0, margin),
                             pair.Ours.Insert(box, (ulong)(0x1000 + i), false, margin));
            }
            for (var q = 0; q < 500; q++)
            {
                var box = Crate();
                if (random.Next(3) == 0)
                    box.Max.X += 32f;
                var valve = dll.FindBestSibling(pair.Valve, &box);
                var port = pair.Ours.FindBestSibling(box);
                Assert.True(valve == port, $"tree {tree} query {q}: sibling {valve} vs {port} for {box}");
            }
        }
    }

    [Fact]
    public void RandomOperationSequencesMatch()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var dll = new Dll(module);
        var total = new Counts();
        for (var seed = 1; seed <= 6; seed++)
            total += new Run(dll, seed, 1, 5000, Boxes.Mixed).Go();
        // Crates on a grid only: exact ties in every cost the tree compares.
        for (var seed = 7; seed <= 10; seed++)
            total += new Run(dll, seed, 1, 5000, Boxes.Lattice).Go();
        // Many small trees of crates in a 3x3x2 block: the symmetric
        // configurations that make the tie rules decide.
        total += new Run(dll, 11, 2000, 16, Boxes.Tiny).Go();
        output.WriteLine(total.ToString());
        Assert.True(total.Operations > 20000);
    }

    // ------------------------------------------------------------------ the run

    private record struct Counts(
        int Operations, int Inserts, int Removes, int Moves, int Refattened, int Mode0, int Mode1, int Mode3, int Mode4,
        int Immediate, int BoxQueries, int TreeQueries, int Pairs, int Compounds)
    {
        public static Counts operator +(Counts a, Counts b) => new(
            a.Operations + b.Operations, a.Inserts + b.Inserts, a.Removes + b.Removes, a.Moves + b.Moves,
            a.Refattened + b.Refattened, a.Mode0 + b.Mode0, a.Mode1 + b.Mode1, a.Mode3 + b.Mode3, a.Mode4 + b.Mode4,
            a.Immediate + b.Immediate, a.BoxQueries + b.BoxQueries, a.TreeQueries + b.TreeQueries,
            a.Pairs + b.Pairs, a.Compounds + b.Compounds);
    }

    private sealed class Leaf
    {
        public Aabb Box;
        public ulong Shape;
        public bool Compound;
    }

    /// <summary>One side-by-side tree: Valve's in native memory, ours, and what the test knows of its leaves.</summary>
    private sealed class Pair : IDisposable
    {
        public readonly byte* Valve;
        public readonly DynamicTree Ours;
        public readonly Dictionary<int, Leaf> Leaves = [];
        public readonly List<int> Order = [];
        private readonly Dll _dll;

        public Pair(Dll dll, float margin)
        {
            _dll = dll;
            Valve = (byte*)NativeMemory.AlignedAlloc(0x58, 16);
            NativeMemory.Clear(Valve, 0x58);
            dll.Init(Valve);
            *(float*)(Valve + 0x4c) = margin;
            Ours = new DynamicTree(margin);
        }

        public float Margin => Ours.Margin;

        public byte* Node(int i) => *(byte**)(Valve + 0x18) + i * 0x30;

        public void Dispose()
        {
            _dll.Free(*(void**)(Valve + 0x18));
            NativeMemory.AlignedFree(Valve);
        }
    }

    private enum Boxes { Mixed, Lattice, Tiny }

    private sealed class Run(Dll dll, int seed, int rounds, int operations, Boxes boxes)
    {
        private readonly Random _r = new(seed);
        private Counts _counts;
        private int _op;
        private ulong _nextShape = 0x10000;
        private readonly List<nint> _compounds = [];
        private readonly List<nint> _blocks = [];

        public Counts Go()
        {
            for (var i = 0; i < 8; i++)
                _compounds.Add(MakeCompound());
            try
            {
                Hooks.Install(dll);
                for (var round = 0; round < rounds; round++)
                {
                    using var a = new Pair(dll, 4f);
                    using var b = new Pair(dll, 0.04f);
                    var target = boxes == Boxes.Tiny ? 2 + _r.Next(12) : 300 + _r.Next(1500);
                    for (var i = 0; i < operations; i++, _op++)
                    {
                        if (i % 1000 == 999)
                            target = 50 + _r.Next(2500);
                        var (t, o) = _r.Next(2) == 0 ? (a, b) : (b, a);
                        Step(t, o, target);
                        _counts.Operations++;
                        Compare(t);
                    }
                }
            }
            finally
            {
                Hooks.Remove();
                foreach (var block in _blocks)
                    NativeMemory.AlignedFree((void*)block);
            }
            return _counts;
        }

        private void Step(Pair t, Pair other, int target)
        {
            var roll = _r.Next(100);
            var (inserts, removes) = t.Order.Count < target ? (30, 8) : (12, 26);
            if (roll < inserts)
                Insert(t);
            else if (roll < inserts + removes)
                Remove(t);
            else if (roll < 75)
                Move(t);
            else if (roll < 80)
                Rebuild(t, 3, []);
            else if (roll < 84)
                Rebuild(t, 4, []);
            else if (roll < 94)
                BoxQuery(t, other);
            else
                TreeQuery(t, other);
        }

        // -------------------------------------------------------------- operations

        private void Insert(Pair t)
        {
            var leaf = new Leaf { Box = RandomBox() };
            if (_r.Next(25) == 0)
            {
                leaf.Compound = true;
                leaf.Shape = (ulong)_compounds[_r.Next(_compounds.Count)];
            }
            else if (_r.Next(30) == 0 && t.Order.Count > 0)
                leaf.Shape = SharedPlainShape(t);
            else
                leaf.Shape = _nextShape += 0x10;
            if (_r.Next(200) == 0)
                leaf.Box.Min.Y = leaf.Box.Max.Y + 1f;
            var margin = _r.Next(10) == 0 ? 0f : t.Margin;

            var box = leaf.Box;
            var valve = dll.Insert(t.Valve, &box, leaf.Shape, (ushort)(leaf.Compound ? 1 : 0), margin);
            var ours = t.Ours.Insert(leaf.Box, leaf.Shape, leaf.Compound, margin);
            Assert.True(valve == ours, $"op {_op}: insert returned {valve} vs {ours}");
            _counts.Inserts++;
            if (valve < 0)
                return;
            t.Leaves.Add(valve, leaf);
            t.Order.Add(valve);
        }

        private ulong SharedPlainShape(Pair t)
        {
            var other = t.Leaves[t.Order[_r.Next(t.Order.Count)]];
            return other.Compound ? _nextShape += 0x10 : other.Shape;
        }

        private void Remove(Pair t)
        {
            if (t.Order.Count == 0)
                return;
            var k = _r.Next(t.Order.Count);
            var leaf = t.Order[k];
            var valve = dll.Remove(t.Valve, leaf);
            var ours = t.Ours.Remove(leaf);
            Assert.True(valve == ours, $"op {_op}: remove {leaf} returned {valve:x} vs {ours:x}");
            t.Leaves.Remove(leaf);
            t.Order[k] = t.Order[^1];
            t.Order.RemoveAt(t.Order.Count - 1);
            _counts.Removes++;
        }

        /// <summary>A batch of leaves gets new boxes and is re-fattened; the refattened ones are then relinked or refit.</summary>
        private void Move(Pair t)
        {
            if (t.Order.Count == 0)
                return;
            var count = 1 + _r.Next(Math.Min(t.Order.Count, _r.Next(4) == 0 ? 200 : 12));
            var moved = new List<int>();
            var chosen = new HashSet<int>();
            for (var i = 0; i < count; i++)
            {
                var leafIndex = t.Order[_r.Next(t.Order.Count)];
                if (!chosen.Add(leafIndex))
                    continue;
                var leaf = t.Leaves[leafIndex];
                leaf.Box = Moved(leaf.Box);
                var box = leaf.Box;
                var margin = t.Margin;
                var valve = dll.Refatten(t.Valve, leafIndex, &box, margin) != 0;
                var ours = t.Ours.Refatten(leafIndex, leaf.Box, margin);
                Assert.True(valve == ours, $"op {_op}: refatten {leafIndex} {valve} vs {ours}");
                _counts.Moves++;
                if (valve)
                {
                    moved.Add(leafIndex);
                    _counts.Refattened++;
                }
            }
            switch (_r.Next(3))
            {
                case 0:
                    foreach (var leaf in moved)
                    {
                        dll.Unlink(t.Valve, leaf);
                        dll.Relink(t.Valve, leaf);
                        t.Ours.Unlink(leaf);
                        t.Ours.Relink(leaf);
                        _counts.Immediate++;
                    }
                    break;
                case 1:
                    Rebuild(t, 0, moved);
                    break;
                default:
                    Rebuild(t, 1, moved);
                    break;
            }
        }

        private void Rebuild(Pair t, int mode, List<int> leaves)
        {
            var list = leaves.ToArray();
            fixed (int* p = list)
            {
                var range = new Range { Ptr = p, Count = list.Length };
                dll.Rebuild(t.Valve, mode, &range);
            }
            t.Ours.Rebuild(mode, list);
            switch (mode)
            {
                case 0: _counts.Mode0++; break;
                case 1: _counts.Mode1++; break;
                case 3: _counts.Mode3++; break;
                default: _counts.Mode4++; break;
            }
        }

        private void BoxQuery(Pair t, Pair other)
        {
            if (t.Order.Count == 0)
                return;
            var leaf = t.Order[_r.Next(t.Order.Count)];
            var ctx = stackalloc long[4];
            var args = stackalloc long[2];
            args[0] = (long)ctx;
            args[1] = (long)t.Node(leaf);
            var box = t.Ours.Nodes[leaf].Box;

            Hooks.Events.Clear();
            dll.QuerySelf(t.Valve, &box, args);
            var sink = new Recorder();
            t.Ours.Query(t.Ours.Nodes[leaf], sameTree: true, ref sink);
            Same(sink, "self box query");

            Hooks.Events.Clear();
            dll.QueryOther(other.Valve, &box, args);
            sink = new Recorder();
            other.Ours.Query(t.Ours.Nodes[leaf], sameTree: false, ref sink);
            Same(sink, "other box query");
            _counts.BoxQueries += 2;
        }

        private void TreeQuery(Pair t, Pair other)
        {
            if (t.Ours.Root == -1)
                return;
            var vector = new UtlVector();
            var maxHeight = _r.Next(3) == 0 ? _r.Next(6) : 3;
            dll.Subtrees(t.Valve, &vector, maxHeight);
            var subtrees = new List<int>();
            t.Ours.CollectSubtrees(maxHeight, subtrees);
            Assert.True(vector.Count == subtrees.Count, $"op {_op}: {vector.Count} subtrees vs {subtrees.Count}");
            for (var i = 0; i < subtrees.Count; i++)
                Assert.True(vector.Ptr[i] == subtrees[i], $"op {_op}: subtree {i} is {vector.Ptr[i]} vs {subtrees[i]}");

            var ctx = stackalloc long[4];
            var ctxRef = (long)ctx;
            foreach (var s in subtrees)
            {
                var start = (ulong)(uint)s | (ulong)(uint)t.Ours.Root << 32;
                Hooks.Events.Clear();
                dll.TreeSelf(t.Valve, t.Valve, &start, &ctxRef);
                var sink = new Recorder();
                t.Ours.QueryTree(s, t.Ours, t.Ours.Root, ref sink);
                Same(sink, $"self tree query from {s}");
                _counts.TreeQueries++;
                if (other.Ours.Root == -1)
                    continue;
                start = (ulong)(uint)s | (ulong)(uint)other.Ours.Root << 32;
                Hooks.Events.Clear();
                dll.TreeOther(t.Valve, other.Valve, &start, &ctxRef);
                sink = new Recorder();
                t.Ours.QueryTree(s, other.Ours, other.Ours.Root, ref sink);
                Same(sink, $"other tree query from {s}");
                _counts.TreeQueries++;
            }
        }

        private void Same(Recorder sink, string what)
        {
            var valve = Hooks.Events;
            var n = Math.Min(valve.Count, sink.Events.Count);
            for (var i = 0; i < n; i++)
                Assert.True(valve[i] == sink.Events[i],
                            $"op {_op}: {what}: event {i} is {valve[i]} vs {sink.Events[i]}");
            Assert.True(valve.Count == sink.Events.Count,
                        $"op {_op}: {what}: {valve.Count} events vs {sink.Events.Count}");
            foreach (var e in valve)
                if (e.Kind == 0)
                    _counts.Pairs++;
                else
                    _counts.Compounds++;
        }

        // -------------------------------------------------------------- comparing

        private void Compare(Pair t)
        {
            var v = t.Valve;
            var o = t.Ours;
            var header = (*(int*)v, *(int*)(v + 4), *(int*)(v + 8), *(int*)(v + 0xc), *(int*)(v + 0x10));
            var ours = (o.Root, o.LeafCount, o.NodeCount, o.Capacity, o.FreeHead);
            Assert.True(header == ours, $"op {_op}: header (root, leaves, nodes, capacity, free) {header} vs {ours}");
            Assert.True(o.LeafCount == t.Order.Count, $"op {_op}: {o.LeafCount} leaves, {t.Order.Count} expected");

            var free = new HashSet<int>();
            for (var i = o.FreeHead; i != -1; i = o.Nodes[i].Next)
                Assert.True(free.Add(i) && free.Count <= o.Capacity, $"op {_op}: the free list loops at {i}");
            var nodes = *(byte**)(v + 0x18);
            var mine = o.NodeBytes;
            for (var i = 0; i < o.Capacity; i++)
            {
                var valve = new ReadOnlySpan<byte>(nodes + i * 0x30, free.Contains(i) ? 4 : 0x30);
                var port = mine.Slice(i * 0x30, valve.Length);
                if (!valve.SequenceEqual(port))
                    Assert.Fail($"op {_op}: node {i} ({(free.Contains(i) ? "free" : "used")}) differs"
                                + $"\nvalve {Convert.ToHexString(valve)}\nours  {Convert.ToHexString(port)}");
            }
            Assert.True(o.Capacity - free.Count == o.NodeCount, $"op {_op}: {free.Count} free of {o.Capacity}, {o.NodeCount} used");
        }

        // -------------------------------------------------------------- boxes

        private float F(double lo, double hi) => (float)(lo + _r.NextDouble() * (hi - lo));

        /// <summary>Settle props, stacks of identical props, and big static world pieces, some on a coarse grid.</summary>
        private Aabb RandomBox()
        {
            Vec3 c, h;
            switch (boxes == Boxes.Tiny ? 13 : boxes == Boxes.Lattice ? 10 + _r.Next(3) : _r.Next(12))
            {
                case 13:
                    c = new(_r.Next(0, 3) * 32f, _r.Next(0, 3) * 32f, _r.Next(0, 2) * 32f);
                    h = new(16f, 16f, 16f);
                    break;
                case 10:
                case 11:
                    // Equal cubes on a small lattice: exact cost ties and equal Morton codes.
                    c = new(_r.Next(-3, 4) * 32f, _r.Next(-3, 4) * 32f, _r.Next(0, 3) * 32f);
                    h = new(16f, 16f, 16f);
                    break;
                case 12:
                    c = new(_r.Next(-12, 12) * 32f, _r.Next(-12, 12) * 32f, _r.Next(0, 4) * 32f);
                    h = new(16f, 16f, 16f);
                    break;
                case 0:
                case 1:
                    c = new(F(-3000, 3000), F(-3000, 3000), F(-500, 500));
                    h = new(F(64, 2048), F(64, 2048), _r.Next(3) == 0 ? 0f : F(4, 512));
                    break;
                case 2:
                    c = new(_r.Next(-8, 8) * 64f, _r.Next(-8, 8) * 64f, _r.Next(-2, 4) * 32f);
                    h = new(16f, 16f, 8f);
                    break;
                case 3:
                    c = new(_r.Next(-40, 40) * 32f, _r.Next(-40, 40) * 32f, _r.Next(-4, 8) * 16f);
                    h = new(_r.Next(1, 5) * 8f, _r.Next(1, 5) * 8f, _r.Next(1, 5) * 8f);
                    break;
                default:
                    c = new(F(-1500, 1500), F(-1500, 1500), F(-200, 600));
                    h = new(F(2, 48), F(2, 48), F(2, 48));
                    break;
            }
            return new(new(c.X - h.X, c.Y - h.Y, c.Z - h.Z), new(c.X + h.X, c.Y + h.Y, c.Z + h.Z));
        }

        private Aabb Moved(Aabb box)
        {
            Vec3 d;
            switch (boxes != Boxes.Mixed ? _r.Next(4) : _r.Next(8))
            {
                case 0: d = default; break;
                case 1: d = new(F(-400, 400), F(-400, 400), F(-200, 200)); break;
                case 2: d = new(0f, 0f, -F(0, 20)); break;
                case 3: d = new(_r.Next(-1, 2) * 32f, _r.Next(-1, 2) * 32f, _r.Next(-1, 2) * 32f); break;
                default: d = new(F(-6, 6), F(-6, 6), F(-8, 4)); break;
            }
            return new(new(box.Min.X + d.X, box.Min.Y + d.Y, box.Min.Z + d.Z),
                       new(box.Max.X + d.X, box.Max.Y + d.Y, box.Max.Z + d.Z));
        }

        /// <summary>
        /// A compound shape Valve's queries can walk: identity body, unit
        /// scale, and a child tree of one leaf (child 0), which the walk
        /// visits without a box test.
        /// </summary>
        private nint MakeCompound()
        {
            var shape = Block(0x100);
            var body = Block(0x280);
            var vector = Block(0x20);
            var node = Block(0x20);
            *(nint*)(shape + 0x10) = (nint)body;
            *(float*)(shape + 0xb8) = 1f;
            *(nint*)(shape + 0xc0) = (nint)vector;
            *(float*)(body + 0x12c) = 1f;
            *(float*)(body + 0x88) = 1f;
            *(int*)vector = 1;
            *(int*)(vector + 4) = 1;
            *(nint*)(vector + 8) = (nint)node;
            *(int*)(vector + 0x10) = 0;
            *(int*)(node + 0x18) = 3;
            return (nint)shape;
        }

        private byte* Block(int size)
        {
            var p = (byte*)NativeMemory.AlignedAlloc((nuint)size, 16);
            NativeMemory.Clear(p, (nuint)size);
            _blocks.Add((nint)p);
            return p;
        }
    }

    // ------------------------------------------------------------------ recording

    private readonly record struct Event(int Kind, ulong A, ulong B);

    private struct Recorder() : IPairSink
    {
        public readonly List<Event> Events = [];

        public readonly void Pair(ulong a, ulong b) => Events.Add(new(0, a, b));

        public readonly void Compound(ulong compound, ulong other, in Aabb otherBox) => Events.Add(new(1, compound, other));
    }

    /// <summary>
    /// Code patches on the two lookups a leaf pair ends in: a 12-byte
    /// mov rax, imm64 / jmp rax at the entry, restored afterwards.
    /// </summary>
    private static class Hooks
    {
        public static readonly List<Event> Events = [];
        private static readonly List<(nint At, byte[] Saved)> Patches = [];

        [UnmanagedCallersOnly]
        private static byte ExistingPair(nint broadphase, ulong a, ulong b)
        {
            Events.Add(new(0, a, b));
            return 1;
        }

        [UnmanagedCallersOnly]
        private static int CompoundChild(nint hash, ulong* key)
        {
            Events.Add(new(1, (ulong)(hash - 0xe8), key[0]));
            return 0;
        }

        public static void Install(Dll dll)
        {
            Patch(dll.ExistingPair, (nint)(delegate* unmanaged<nint, ulong, ulong, byte>)&ExistingPair);
            Patch(dll.CompoundChild, (nint)(delegate* unmanaged<nint, ulong*, int>)&CompoundChild);
        }

        public static void Remove()
        {
            foreach (var (at, saved) in Patches)
                Write(at, saved);
            Patches.Clear();
        }

        private static void Patch(nint at, nint target)
        {
            var saved = new byte[12];
            new ReadOnlySpan<byte>((void*)at, 12).CopyTo(saved);
            var code = new byte[12];
            code[0] = 0x48;
            code[1] = 0xb8;
            Unsafe.WriteUnaligned(ref code[2], (ulong)target);
            code[10] = 0xff;
            code[11] = 0xe0;
            Write(at, code);
            Patches.Add((at, saved));
        }

        private static void Write(nint at, byte[] bytes)
        {
            Assert.True(VirtualProtect(at, (nuint)bytes.Length, 0x40, out var old));
            bytes.CopyTo(new Span<byte>((void*)at, bytes.Length));
            VirtualProtect(at, (nuint)bytes.Length, old, out _);
            FlushInstructionCache(-1, at, (nuint)bytes.Length);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint old);

        [DllImport("kernel32.dll")]
        private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
    }
}
