using System.Numerics;
using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Whole steps in lockstep with Valve's world: the scene is built in Valve's
/// world in this process, read once (bodies, shapes, islands, broadphase)
/// into an <see cref="RnWorld"/>, and from then on both step on their own.
/// After every step the port's world must equal a fresh read of Valve's, line
/// for line (<see cref="WorldSignature"/>), and its broadphase must equal
/// Valve's: every tree node, the dirty bits, the pair set and proxy ids.
/// </summary>
[Collection(Vphysics2PatchCollection.Name)]
public sealed unsafe class WorldStepLockstepTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 90;

    private static byte* Box(Vphysics2World w, float hx, float hy, float hz)
    {
        var pts = new Vector3[8];
        for (var i = 0; i < 8; i++)
            pts[i] = new Vector3((i & 1) == 0 ? -hx : hx, (i & 2) == 0 ? -hy : hy, (i & 4) == 0 ? -hz : hz);
        return w.CreateHull(pts);
    }

    private static Quat Axis(float x, float y, float z, float angle)
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(x, y, z)), angle);
        return new Quat(q.X, q.Y, q.Z, q.W);
    }

    /// <summary>
    /// Steps Valve's world and the port's side by side, comparing after each
    /// step; returns the steps that matched. <paramref name="between"/> may
    /// change Valve's scene before a step and returns true when it did, and
    /// the port's world is then read again. <paramref name="ties"/>, when
    /// given, collects the steps where Valve's active contacts came out in
    /// another order that an equal-key tie explains (see <see cref="TieReorder"/>).
    /// </summary>
    private int Run(Vphysics2World w, int steps, Func<int, bool>? between = null,
                    Action<RnWorld>? configure = null, Action<RnWorld, int>? inspect = null, List<int>? ties = null)
    {
        var module = Vphysics2Oracle.Load()!.Value;
        var hulls = new Dictionary<nint, RnHull>();
        var meshes = new Dictionary<nint, RnMesh>();
        var port = RnWorldReader.Read(module, w.Rn, hulls, meshes, broadphase: true);
        configure?.Invoke(port);
        for (var s = 0; s < steps; s++)
        {
            if (between != null && between(s))
            {
                port = RnWorldReader.Read(module, w.Rn, hulls, meshes, broadphase: true);
                configure?.Invoke(port);
            }
            w.StepPhased(Dt, s == 0, _ => { });
            try
            {
                port.Step(Dt, s == 0);
            }
            catch (NotSupportedException e)
            {
                foreach (var b in port.ContinuousBodies[0])
                    output.WriteLine($"b{b.Index} pos {b.State.Position} prev {b.State.PreviousPosition} q {b.State.Orientation} pq {b.State.PreviousOrientation} v {b.State.LinearVelocity} w {b.State.AngularVelocity} r {b.State.InnerRadius}/{b.State.OuterRadius}");
                output.WriteLine($"step {s}: {e.Message}; continuous {string.Join(",", port.ContinuousBodies[0].Select(b => b.Index))} | {string.Join(",", port.ContinuousBodies[1].Select(b => b.Index))}");
                throw;
            }
            var valve = WorldSignature.Lines(RnWorldReader.Read(module, w.Rn, hulls, meshes));
            var ours = WorldSignature.Lines(port);
            var failure = CompareBroadphase(w, port) ?? WorldSignature.Diff(valve, ours);
            if (failure != null && ties != null && TieReorder(RnWorldReader.Read(module, w.Rn, hulls, meshes), port) is { } tie)
            {
                output.WriteLine($"step {s}: {tie}");
                ties.Add(s);
                failure = CompareBroadphase(w, port) ?? WorldSignature.Diff(valve, WorldSignature.Lines(port));
            }
            Assert.True(failure == null, $"step {s}: {failure}");
            inspect?.Invoke(port, s);
        }
        return steps;
    }

    /// <summary>
    /// Valve's multithreaded collide can order the active contacts in either
    /// of two ways when two contacts share a key. The key (+0x40) is the two
    /// proxy ids when the contact was made, and it is not unique: here two
    /// contacts with the floor both carry 16c00000000. The workers (FUN_1801ecfb0) append
    /// to the collide lists in thread order (FUN_1801f0ef0), the flush
    /// (FUN_1801dceb0) sorts each list by key with std::sort (FUN_1801e6a70),
    /// which is not stable, so two equal keys leave the sort in an order that
    /// depends on the threads, and the swap-removals from the active contacts
    /// that follow then move the list's tail in another order. Measured: at
    /// step 644 both are in the BothAsleep list, and Valve's active order came
    /// out the other way exactly in the runs where the sorted pair did. When the port's active contacts are Valve's in another order and
    /// two of the port's contacts share a key, the port takes Valve's order
    /// and the returned note says so; otherwise null, and nothing changes.
    /// </summary>
    private static string? TieReorder(RnWorld valve, RnWorld port)
    {
        static (ulong, int, int) Id(RnContact c) => (c.Key, c.A.Body.Index, c.B.Body.Index);
        var byId = port.ActiveContacts.GroupBy(Id).ToDictionary(g => g.Key, g => g.ToList());
        if (byId.Values.Any(g => g.Count > 1) || valve.ActiveContacts.Count != port.ActiveContacts.Count
            || !valve.ActiveContacts.All(c => byId.ContainsKey(Id(c))))
            return null;
        var keys = port.AllContacts[0].Concat(port.AllContacts[1]).GroupBy(c => c.Key).Where(g => g.Count() > 1).Select(g => $"{g.Key:x}").ToList();
        if (keys.Count == 0)
            return null;
        var moved = new List<string>();
        for (var i = 0; i < valve.ActiveContacts.Count; i++)
        {
            var c = byId[Id(valve.ActiveContacts[i])][0];
            if (port.ActiveContacts[i] != c)
                moved.Add($"c{c.Key:x}@{i}");
            port.ActiveContacts[i] = c;
            c.ActiveIndex = i;
        }
        return $"Valve's active contacts in the other tie order ({string.Join(' ', moved)}; shared keys {string.Join(' ', keys)})";
    }

    /// <summary>The trees node by node (a free node by its next link only), dirty bits, proxy ids, solve count and pair set.</summary>
    private static string? CompareBroadphase(Vphysics2World w, RnWorld port)
    {
        var bp = *(byte**)(w.Rn + 0x110);
        var ours = port.Broadphase!;
        for (var t = 0; t < 7; t++)
        {
            var v = bp + t * 0x58;
            var o = ours.Trees[t].Nodes;
            var header = (*(int*)v, *(int*)(v + 4), *(int*)(v + 8), *(int*)(v + 0xc), *(int*)(v + 0x10));
            if (header != (o.Root, o.LeafCount, o.NodeCount, o.Capacity, o.FreeHead))
                return $"tree {t} header {header} vs {(o.Root, o.LeafCount, o.NodeCount, o.Capacity, o.FreeHead)}";
            var free = new HashSet<int>();
            for (var i = o.FreeHead; i != -1; i = o.Nodes[i].Next)
                free.Add(i);
            var nodes = *(byte**)(v + 0x18);
            var mine = o.NodeBytes;
            for (var i = 0; i < o.Capacity; i++)
            {
                var valve = new ReadOnlySpan<byte>(nodes + i * 0x30, free.Contains(i) ? 4 : 0x30);
                if (!valve.SequenceEqual(mine.Slice(i * 0x30, valve.Length)))
                    return $"tree {t} node {i}\nvalve {Convert.ToHexString(valve)}\nours  {Convert.ToHexString(mine.Slice(i * 0x30, valve.Length))}";
            }
            if (Bits(v + 0x20, ours.Trees[t].TopBits) is { } top)
                return $"tree {t} top bits: {top}";
            if (Bits(v + 0x30, ours.Trees[t].LeafBits) is { } leaf)
                return $"tree {t} leaf bits: {leaf}";
        }
        foreach (var shape in port.ShapesByHandle.Values)
            if (*(int*)((byte*)shape.Proxy.Handle + 0x1c) != shape.ProxyId)
                return $"shape {shape.Proxy.Handle:x} proxy {*(int*)((byte*)shape.Proxy.Handle + 0x1c):x} vs {shape.ProxyId:x}";
        if (*(int*)(bp + 0x2b4) != ours.SolveCount)
            return $"solve count {*(int*)(bp + 0x2b4)} vs {ours.SolveCount}";
        var set = *(byte**)(bp + 0x2b8);
        var pairs = ours.Pairs;
        if (*(int*)(set + 0x10) != pairs.Count || *(int*)(set + 0x14) != pairs.Buckets)
            return $"pair set {*(int*)(set + 0x10)}/{*(int*)(set + 0x14)} vs {pairs.Count}/{pairs.Buckets}";
        var slots = *(PairSet.Slot**)(set + 8);
        for (var i = 0; i < pairs.Buckets; i++)
        {
            var a = slots[i];
            var b = pairs.Slots[i];
            if (a.Word != b.Word || ((int)a.Word >= 0 && (a.A != b.A || a.B != b.B)))
                return $"pair slot {i}: {a.Word:x} {a.A:x} {a.B:x} vs {b.Word:x} {b.A:x} {b.B:x}";
        }
        return null;
    }

    private static string? Bits(byte* vector, List<uint> ours)
    {
        var count = *(int*)vector;
        if (count != ours.Count)
            return $"{count} words vs {ours.Count}";
        for (var i = 0; i < count; i++)
            if ((*(uint**)(vector + 8))[i] != ours[i])
                return $"word {i}";
        return null;
    }

    private static Vphysics2World? Floor()
    {
        var w = Vphysics2World.Create();
        if (w == null)
            return null;
        var floor = w.CreateBody(0, new Vec3(0, 0, 0), Quat.Identity);
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        const int n = 9;
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
                vertices.Add(new Vector3(i * 80 - 320, j * 80 - 320, ((i * 7 + j * 3) % 5) * 1.5f));
        for (var i = 0; i + 1 < n; i++)
            for (var j = 0; j + 1 < n; j++)
            {
                var a = i * n + j;
                indices.AddRange([a, a + n, a + 1, a + 1, a + n, a + n + 1]);
            }
        w.AddMesh(floor, w.CreateMesh([.. indices], [.. vertices]));
        return w;
    }

    private static Vphysics2World.Body Drop(Vphysics2World w, byte* hull, Vec3 at, Quat q)
    {
        var b = w.CreateBody(2, at, q);
        w.AddHull(b, hull);
        w.UpdateMass(b);
        return b;
    }

    [Fact]
    public void StacksAndPilesMatch()
    {
        if (Floor() is not { } w)
            return;
        var box = Box(w, 8, 8, 8);
        var slab = Box(w, 10, 9, 7);
        var block = w.CreateBody(0, new Vec3(60, 60, 10), Axis(0, 0, 1, 0.4f));
        w.AddHull(block, Box(w, 16, 16, 10));
        for (var k = 0; k < 4; k++)
            Drop(w, box, new Vec3(0.4f * k, -0.3f * k, 12 + 17 * k), Axis(0, 0, 1, 0.1f * k));
        for (var k = 0; k < 10; k++)
            Drop(w, k % 3 == 0 ? slab : box, new Vec3(-120 + 45 * (k % 5), -60 - 50 * (k / 5), 24 + 2 * k), Axis(1, k, 0.5f, 0.3f * k));
        Drop(w, box, new Vec3(60, 60, 40), Axis(1, 1, 0, 0.6f));
        output.WriteLine($"{Run(w, 500)} steps exact");
    }

    /// <summary>Wakes a body the way the game does (FUN_1801c0ff0 with the timer reset) and gives it a velocity.</summary>
    private static void Kick(Vphysics2World w, Vphysics2World.Body b, Vec3 velocity)
    {
        ((delegate* unmanaged<byte*, byte, void>)w.At(0x1801c0ff0))(b.Rn, 1);
        b.State.LinearVelocity = velocity;
    }

    /// <summary>A grid of boxes, <paramref name="nx"/> by <paramref name="ny"/> by <paramref name="layers"/>, nearly touching.</summary>
    private static List<Vphysics2World.Body> Pile(Vphysics2World w, byte* box, Vec3 at, int nx, int ny, int layers, float pitch)
    {
        var bodies = new List<Vphysics2World.Body>();
        for (var k = 0; k < layers; k++)
            for (var i = 0; i < nx; i++)
                for (var j = 0; j < ny; j++)
                    bodies.Add(Drop(w, box, new Vec3(at.X + pitch * i + 0.3f * k, at.Y + pitch * j - 0.2f * k, at.Z + 17.5f * k),
                                    Axis(0, 0, 1, 0.02f * (i - j + k))));
        return bodies;
    }

    /// <summary>
    /// What the coloured steps exercised, gathered from the port's world after
    /// each matching step: colourings made and dropped, merges and splits that
    /// involve a coloured island (read from which islands the bodies were in
    /// the step before), sleeping, and the kinds of buckets solved.
    /// </summary>
    private sealed class ColouringCoverage
    {
        public int ColouredSteps, MaxContacts, Created, Dropped, Merged, Split, Asleep, Overflow, Wide, Small, Colours;
        private Dictionary<RnBody, RnIsland> _was = [];
        private HashSet<RnIsland> _coloured = [];
        private bool _reread;

        /// <summary>The port's world was read again: its islands are new objects, and the next step is not compared with this one.</summary>
        public void Reset() => _reread = true;

        public void See(RnWorld w)
        {
            if (_reread)
            {
                _was = [];
                _coloured = [];
            }
            var now = new Dictionary<RnBody, RnIsland>();
            foreach (var b in w.Bodies)
                if (b.Island is { } i)
                    now[b] = i;
            var coloured = new HashSet<RnIsland>();
            foreach (var n in w.Islands.All)
            {
                if (n is not RnIsland { Colouring: { } colouring } island)
                    continue;
                coloured.Add(island);
                if (!_coloured.Contains(island) && !_reread)
                    Created++;
                if (island.ListIndex == -1)
                    Asleep++;
                var from = island.Bodies.Where(b => b != null && _was.ContainsKey(b)).Select(b => _was[b!]).Distinct().Count();
                if (from > 1)
                    Merged++;
                MaxContacts = Math.Max(MaxContacts, Math.Max(island.Contacts[0].Count, island.Contacts[1].Count));
                for (var b = 0; b < 17; b++)
                    for (var g = 0; g < 2; g++)
                    {
                        var count = colouring.Contacts[b, g].Count;
                        if (count == 0)
                            continue;
                        if (b == IslandColouring.Overflow)
                            Overflow++;
                        else
                        {
                            Colours = Math.Max(Colours, b + 1);
                            if (count > 4)
                                Wide++;
                            else
                                Small++;
                        }
                    }
            }
            foreach (var old in _coloured)
            {
                if (!coloured.Contains(old))
                    Dropped++;
                var parts = _was.Where(kv => kv.Value == old).Select(kv => now.GetValueOrDefault(kv.Key)).Distinct().Count();
                if (parts > 1)
                    Split++;
            }
            if (coloured.Count > 0)
                ColouredSteps++;
            _was = now;
            _coloured = coloured;
            _reread = false;
        }

        public override string ToString()
            => $"steps with a coloured island {ColouredSteps}, colourings made {Created} dropped {Dropped}, merges into one {Merged}, splits of one {Split}, " +
               $"coloured island-steps asleep {Asleep}, most contacts in a group {MaxContacts}, colours used {Colours}, bucket-steps wide {Wide} small {Small} overflow {Overflow}";
    }

    /// <summary>
    /// Two piles big enough for a graph colouring (100 or more contacts in a
    /// group), grown as their layers land, then knocked into each other
    /// (merge), broken up (splits below and above the threshold) and left to
    /// sleep, compared with Valve after every step. <paramref name="reorder"/>
    /// takes each colour's contacts in another order, as Valve's threads may.
    /// </summary>
    private void ColouredScene(Func<List<RnContact>, IEnumerable<RnContact>>? reorder, List<int>? ties = null)
    {
        if (Floor() is not { } w)
            return;
        var box = Box(w, 8, 8, 8);
        var big = Pile(w, box, new Vec3(-120, -60, 12), 5, 5, 3, 16.25f);
        var small = Pile(w, box, new Vec3(60, -30, 12), 3, 3, 3, 16.25f);
        var coverage = new ColouringCoverage();
        var steps = Run(w, 900, s =>
        {
            if (s == 300)
                foreach (var b in small.Skip(18))
                    Kick(w, b, new Vec3(-260, 0, 90));
            else if (s == 520)
                for (var k = 0; k < big.Count; k += 6)
                    Kick(w, big[k], new Vec3(k % 12 == 0 ? 300 : -300, 150, 200));
            else
                return false;
            return true;
        }, port =>
        {
            port.ReorderColour = reorder;
            coverage.Reset();
        }, (port, _) => coverage.See(port), ties);
        output.WriteLine($"{steps} steps exact; {coverage}");
    }

    [Fact]
    public void ColouredPilesMatch() => ColouredScene(null);

    /// <summary>
    /// A slab on four static pillars under two layers of boxes: the slab
    /// touches 36 boxes and the four pillars, more than either group has
    /// colours, so both overflow buckets fill; then the pillars' side of the
    /// slab is knocked away.
    /// </summary>
    [Fact]
    public void ColouredSlabOverflowMatches()
    {
        if (Floor() is not { } w)
            return;
        var pillar = Box(w, 10, 10, 12);
        for (var k = 0; k < 4; k++)
        {
            var p = w.CreateBody(0, new Vec3(k % 2 == 0 ? -40 : 40, k < 2 ? -40 : 40, 14), Quat.Identity);
            w.AddHull(p, pillar);
        }
        var slab = Drop(w, Box(w, 58, 58, 4), new Vec3(0, 0, 31), Axis(0, 0, 1, 0.01f));
        var boxes = Pile(w, Box(w, 8, 8, 8), new Vec3(-41, -41, 44), 6, 6, 2, 16.4f);
        var coverage = new ColouringCoverage();
        var steps = Run(w, 700, s =>
        {
            if (s != 400)
                return false;
            Kick(w, slab, new Vec3(0, 0, 250));
            return true;
        }, _ => coverage.Reset(), (port, _) => coverage.See(port));
        output.WriteLine($"{steps} steps exact; {coverage}");
    }

    /// <summary>The same scene with every colour taken backwards and shuffled: the floats must not move.</summary>
    [Fact]
    public void ColouredPilesMatchInAnyColourOrder()
    {
        var random = new Random(0x5eed);
        ColouredScene(bucket => bucket.OrderBy(_ => random.Next()).Reverse().ToList());
    }

    /// <summary>
    /// The colouring functions called directly on Valve's islands: every 20
    /// steps of the pile scene, each coloured island's colouring is dropped
    /// (FUN_1803331e0) and made again from its contact lists (FUN_180333110),
    /// in Valve's world and in a read of it, and the two must agree after
    /// each call: every bucket, contact colour and body mask.
    /// </summary>
    [Fact]
    public void ColouringFunctionsMatch()
    {
        if (Floor() is not { } w)
            return;
        var module = Vphysics2Oracle.Load()!.Value;
        var box = Box(w, 8, 8, 8);
        Pile(w, box, new Vec3(-120, -60, 12), 5, 5, 3, 16.25f);
        var hulls = new Dictionary<nint, RnHull>();
        var meshes = new Dictionary<nint, RnMesh>();
        var destroy = (delegate* unmanaged<byte*, void>)w.At(0x1803331e0);
        var create = (delegate* unmanaged<byte*, void>)w.At(0x180333110);
        var checkedIslands = 0;
        for (var s = 0; s < 400; s++)
        {
            w.Step(Dt, s == 0);
            if (s % 20 != 19)
                continue;
            var port = RnWorldReader.Read(module, w.Rn, hulls, meshes);
            foreach (var island in port.Islands.All.OfType<RnIsland>().Where(i => i.Coloured).ToList())
            {
                destroy((byte*)island.Native);
                IslandManagerOps.DestroyColouring(island);
                var failure = WorldSignature.Diff(WorldSignature.Lines(RnWorldReader.Read(module, w.Rn, hulls, meshes)), WorldSignature.Lines(port));
                Assert.True(failure == null, $"step {s}, dropped: {failure}");
                create((byte*)island.Native);
                IslandManagerOps.CreateColouring(island);
                failure = WorldSignature.Diff(WorldSignature.Lines(RnWorldReader.Read(module, w.Rn, hulls, meshes)), WorldSignature.Lines(port));
                Assert.True(failure == null, $"step {s}, made: {failure}");
                checkedIslands++;
            }
        }
        Assert.True(checkedIslands > 0, "the scene made no coloured island");
        output.WriteLine($"{checkedIslands} colourings dropped and made again, both exact");
    }

    /// <summary>
    /// The pile scene with Valve's solve on seven threads: tier0's job pool
    /// (g_pThreadPool) has none in a test process, so it is started here
    /// (CThreadPool::Start, vtable slot 0x18, with {mode 0, 7 threads, at
    /// most 7}) and stopped after (slot 0x20). CRnWorld::Solve then runs the
    /// coloured island's job chain on up to eight threads (FUN_1801ff820 at
    /// 0x1801ffbaa), and the port, on one thread in bucket order, must still
    /// match every step. Valve itself is not deterministic here in one way:
    /// at step 644 two contacts share a key, and in 13 of 81 measured runs
    /// its active contacts came out in the other tie order (see
    /// <see cref="TieReorder"/>). That step is let through with the port put
    /// in Valve's order, and logged; every other line must still match.
    /// </summary>
    [Fact]
    public void ColouredPilesMatchValveOnSevenThreads()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return;
        var pool = *(nint**)Vphysics2Oracle.Tier0("g_pThreadPool");
        var vt = *(nint**)pool;
        var numThreads = (delegate* unmanaged<nint*, int>)vt[0x38 / 8];
        Assert.Equal(0, numThreads(pool));
        Assert.Equal(1, *(byte*)Vphysics2Oracle.At(module, 0x18045d328));
        var parameters = stackalloc int[6] { 0, 7, 7, 0, 0, 0 };
        fixed (byte* name = "SettlePool\0"u8)
            ((delegate* unmanaged<nint*, int*, byte*, nint, byte>)vt[0x18 / 8])(pool, parameters, name, 0);
        try
        {
            output.WriteLine($"pool threads {numThreads(pool)}");
            Assert.True(numThreads(pool) > 0);
            var ties = new List<int>();
            ColouredScene(null, ties);
            output.WriteLine(ties.Count == 0 ? "Valve took the single-thread tie order" : $"Valve took the other tie order at steps {string.Join(',', ties)}");
        }
        finally
        {
            ((delegate* unmanaged<nint*, int, byte>)vt[0x20 / 8])(pool, -1);
        }
        Assert.Equal(0, numThreads(pool));
    }
}
