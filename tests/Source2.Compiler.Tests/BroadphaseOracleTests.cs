using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The broadphase, ported, against Valve's own CBroadphase in a live
/// vphysics2 world: shape boxes, body frames, the pair filter and the pair set
/// call by call, then the whole broadphase in lockstep (proxies, immediate
/// moves, hierarchy updates, pre-step queries and new contacts), comparing
/// the trees, dirty bits, proxy ids, query input, pair set and the contacts
/// the world lists after every operation.
/// </summary>
[Collection(Vphysics2PatchCollection.Name)]
public unsafe class BroadphaseOracleTests(ITestOutputHelper output)
{
    // ------------------------------------------------------------------ Valve's functions

    private const ulong FramesVa = 0x1801b2690;
    private const ulong FilterVa = 0x1802faf00;
    private const ulong HashVa = 0x1801c6540;
    private const ulong SetInsertVa = 0x1801db2e0;
    private const ulong SetEraseVa = 0x1801db8e0;
    private const ulong SetFindVa = 0x1801fa710;
    private const ulong BeginVa = 0x1802d4ed0;
    private const ulong UpdateBodyVa = 0x1801b9270;
    private const ulong FinalizeVa = 0x1802d6050;
    private const ulong BuildContactsVa = 0x1801f1dc0;
    private const ulong PreStepVa = 0x1802d5740;
    private const ulong DestroyProxyVa = 0x1802d5c30;
    private const ulong ReselectVa = 0x1802d7560;
    private const ulong GroupTableVa = 0x18045b328;
    private const ulong CollidingMaskVa = 0x18045b2b8;
    private const ulong ToiBodyVa = 0x1801be330;
    private const ulong VetoVa = 0x1801bdea0;
    private const ulong ImmediateVa = 0x1801b9100;
    private const ulong PairEraseVa = 0x180203980;

    // ------------------------------------------------------------------ small oracles

    [Fact]
    public void ShapeBoxesMatch()
    {
        if (Vphysics2World.Create() is not { } w)
            return;
        var random = new Random(31);
        var shapes = new List<(nint Shape, BroadphaseShape Port)>();
        foreach (var type in new[] { 0, 1, 2 })
            for (var k = 0; k < 6; k++)
            {
                var body = w.CreateBody(type, default, Quat.Identity);
                var hull = w.CreateHull(RandomPoints(random, k % 2 == 0 ? 8 : 20));
                shapes.Add(Mirror(w.AddHull(body, hull, k % 3 == 0 ? F(random, 0.3, 3) : 1f), Port(body)));
                var mesh = RandomMesh(w, random);
                var scale = k % 3 == 0 ? new Vector3(F(random, 0.5, 2), F(random, 0.5, 2), F(random, 0.5, 2)) : Vector3.One;
                shapes.Add(Mirror(body.AddMesh(mesh, scale), Port(body)));
            }
        var box = stackalloc float[6];
        var trials = 0;
        foreach (var (shape, port) in shapes)
        {
            var rn = Vphysics2WorldExtensions.RnShape(shape);
            var compute = (delegate* unmanaged<byte*, float*, RnTransform*, float*>)(*(nint**)rn)[0x80 / 8];
            for (var i = 0; i < 2000; i++, trials++)
            {
                var xf = RandomFrame(random, identity: i % 7 == 0);
                compute(rn, box, &xf);
                var ours = port.ComputeAabb(xf);
                var valve = new ReadOnlySpan<float>(box, 6);
                var mine = new ReadOnlySpan<float>(&ours, 6);
                for (var c = 0; c < 6; c++)
                    Assert.True(Bits(valve[c]) == Bits(mine[c]),
                                $"type {port.Type} body {port.Body.State.BodyType} trial {i} component {c}: {valve[c]:R} vs {mine[c]:R}");
            }
        }
        output.WriteLine($"{trials} boxes over {shapes.Count} shapes");
    }

    [Fact]
    public void BodyFramesMatch()
    {
        if (Vphysics2World.Create() is not { } w)
            return;
        var frames = (delegate* unmanaged<byte*, RnTransform*, RnTransform*, void>)w.At(FramesVa);
        var random = new Random(32);
        var body = w.CreateBody(2, default, Quat.Identity);
        var x = stackalloc RnTransform[2];
        for (var i = 0; i < 50000; i++)
        {
            ref var s = ref body.State;
            s.Position = V(random, 3000);
            s.Orientation = Rotation(random);
            s.PreviousPosition = V(random, 3000);
            s.PreviousOrientation = Rotation(random);
            s.Scale = random.Next(3) == 0 ? F(random, 0.2, 3) : 1f;
            s.LocalMassCenter = V(random, 30);
            s.Flags249 = (byte)((s.Flags249 & ~0x40) | (random.Next(2) == 0 ? 0x40 : 0));
            frames(body.Rn, &x[0], &x[1]);
            var (start, end) = Broadphase.Frames(s);
            Assert.True(Same(x[0], start) && Same(x[1], end), $"trial {i}");
        }
    }

    [Fact]
    public void ThePairFilterMatches()
    {
        if (Vphysics2World.Create() is not { } w)
            return;
        var filter = (delegate* unmanaged<ushort*, CollisionAttributes*, CollisionAttributes*, ushort>)w.At(FilterVa);
        var table = GroupTable(w);
        var random = new Random(33);
        var hits = 0;
        fixed (ushort* t = table)
            for (var i = 0; i < 400000; i++)
            {
                var a = RandomAttributes(random);
                var b = random.Next(4) == 0 ? a : RandomAttributes(random);
                if (random.Next(3) == 0)
                    b.HierarchyId = a.HierarchyId;
                if (random.Next(4) == 0)
                    b.OwnerId = a.EntityId;
                var valve = filter(t, &a, &b);
                Assert.Equal(valve, CollisionFilter.Flags(table, a, b));
                if (valve != 0)
                    hits++;
            }
        output.WriteLine($"{hits} of 400000 pairs pass");
    }

    /// <summary>
    /// FUN_1801bdea0 on made-up bodies: random types and shape counts,
    /// attributes, joint lists (both edge tags) and no-collide records, the
    /// joint walk depending on which body Valve picks.
    /// </summary>
    [Fact]
    public void TheBodyVetoMatches()
    {
        if (Vphysics2World.Create() is not { } w)
            return;
        var veto = (delegate* unmanaged<byte*, byte*, byte*, byte*, ushort, byte>)w.At(VetoVa);
        var random = new Random(35);
        var memory = (byte*)NativeMemory.AllocZeroed(0x10000);
        var refused = 0;
        for (var i = 0; i < 100000; i++)
        {
            NativeMemory.Clear(memory, 0x10000);
            var bodies = new[] { memory, memory + 0x300 };
            var shapes = new[] { memory + 0x600, memory + 0x700 };
            var ports = new BroadphaseBody[2];
            for (var k = 0; k < 2; k++)
            {
                var type = random.Next(3);
                *(int*)(bodies[k] + 0x54) = type;
                *(int*)(bodies[k] + 0x60) = 1 + random.Next(3);
                ports[k] = new BroadphaseBody { State = new RnBodyState { BodyType = type } };
                for (var n = 0; n < *(int*)(bodies[k] + 0x60); n++)
                    ports[k].Shapes.Add(new BroadphaseShape { Body = ports[k] });
            }
            var same = random.Next(20) == 0;
            var portShapes = new BroadphaseShape[2];
            for (var k = 0; k < 2; k++)
            {
                var attributes = RandomAttributes(random);
                *(CollisionAttributes*)(shapes[k] + 0x50) = attributes;
                var body = same ? 0 : k;
                *(byte**)(shapes[k] + 0x10) = bodies[body];
                portShapes[k] = new BroadphaseShape { Body = ports[body], Attributes = attributes };
            }
            var next = memory + 0x800;
            for (var k = 0; k < 2; k++)
            {
                var other = 1 - k;
                ulong link = 0;
                for (var j = random.Next(3); j > 0; j--)
                {
                    var joint = next;
                    next += 0x60;
                    var tag = (ulong)random.Next(2);
                    var target = random.Next(3) == 0 ? memory + 0x2000 : bodies[other];
                    var flags = (ushort)random.Next(4);
                    *(byte**)(joint + (tag == 0 ? 0x20 : 0x18)) = target;
                    *(ushort*)(joint + 0x28) = flags;
                    *(ulong*)(joint + (tag == 0 ? 0x48 : 0x50)) = link;
                    link = (ulong)joint | tag;
                    if (target == bodies[other])
                        ports[k].Joints.Add((ports[other], flags));
                }
                *(ulong*)(bodies[k] + 0x70) = link;
                byte* records = null;
                for (var j = random.Next(8) == 0 ? 1 + random.Next(2) : 0; j > 0; j--)
                {
                    var record = next;
                    var p = next + 0x20;
                    var q = next + 0x70;
                    next += 0xa0;
                    *(byte**)record = p;
                    *(byte**)(record + 0x18) = records;
                    records = record;
                    p[0x44] = (byte)(random.Next(3) == 0 ? 1 : 0);
                    *(byte**)(p + 0x10) = random.Next(4) == 0 ? null : q;
                    *(byte**)(q + 0x18) = random.Next(3) == 0 ? memory + 0x2000 : bodies[other];
                    if (p[0x44] == 0 && *(byte**)(p + 0x10) != null && *(byte**)(q + 0x18) == bodies[other])
                        ports[k].NoCollide.Add(ports[other]);
                }
                *(byte**)(bodies[k] + 0x240) = records;
            }
            var filterFlags = (ushort)random.Next(0x40);
            var valve = veto(*(byte**)(shapes[0] + 0x10), shapes[0], *(byte**)(shapes[1] + 0x10), shapes[1], filterFlags) != 0;
            var ours = CollisionFilter.BodiesMayCollide(portShapes[0], portShapes[1], filterFlags);
            Assert.True(valve == ours, $"trial {i}: {valve} vs {ours}");
            if (!valve)
                refused++;
        }
        NativeMemory.Free(memory);
        output.WriteLine($"{refused} of 100000 refused");
    }

    [Fact]
    public void ThePairSetMatches()
    {
        if (Vphysics2World.Create() is not { } w)
            return;
        var hash = (delegate* unmanaged<void*, ulong*, uint>)w.At(HashVa);
        var insert = (delegate* unmanaged<byte*, ulong*, uint, byte*, uint>)w.At(SetInsertVa);
        var erase = (delegate* unmanaged<byte*, ulong*, uint, uint>)w.At(SetEraseVa);
        var find = (delegate* unmanaged<byte*, ulong, ulong, byte>)w.At(SetFindVa);
        var random = new Random(34);
        var pair = stackalloc ulong[2];
        var operations = 0;
        for (var run = 0; run < 20; run++)
        {
            var set = (byte*)NativeMemory.AllocZeroed(0x20);
            *(int*)(set + 0x18) = 32;
            var bp = (byte*)NativeMemory.AllocZeroed(0x2c0);
            *(byte**)(bp + 0x2b8) = set;
            var ours = new PairSet();
            var live = new List<(ulong, ulong)>();
            var handles = Enumerable.Range(0, 20 + random.Next(300)).Select(_ => (ulong)random.NextInt64(0x10000, 0x7fffffffffff) & ~7ul).ToArray();
            for (var i = 0; i < 6000; i++, operations++)
            {
                var roll = random.Next(10);
                if (roll < 5 || live.Count == 0)
                {
                    pair[0] = handles[random.Next(handles.Length)];
                    pair[1] = handles[random.Next(handles.Length)];
                    if (random.Next(2) == 0 && live.Count > 0)
                        (pair[1], pair[0]) = live[random.Next(live.Count)];
                    var h = hash(null, pair);
                    Assert.Equal(h, PairSet.Hash(pair[0], pair[1]));
                    byte added;
                    insert(set, pair, h, &added);
                    Assert.Equal(added != 0, ours.Insert(pair[0], pair[1]));
                    if (added != 0)
                        live.Add((pair[0], pair[1]));
                }
                else if (roll < 8)
                {
                    var k = random.Next(live.Count);
                    (pair[0], pair[1]) = live[k];
                    if (random.Next(2) == 0)
                        (pair[0], pair[1]) = (pair[1], pair[0]);
                    erase(set, pair, hash(null, pair));
                    Assert.True(ours.Erase(pair[0], pair[1]));
                    live.RemoveAt(k);
                }
                else
                {
                    var a = handles[random.Next(handles.Length)];
                    var b = random.Next(2) == 0 && live.Count > 0 ? live[random.Next(live.Count)].Item1 : handles[random.Next(handles.Length)];
                    Assert.Equal(find(bp, a, b) != 0, ours.Contains(a, b));
                }
                ComparePairSet(set, ours, $"run {run} op {i}");
            }
        }
        output.WriteLine($"{operations} pair set operations");
    }

    // ------------------------------------------------------------------ lockstep

    [Fact]
    public void TheBroadphaseRunsInLockstep()
    {
        if (Vphysics2World.Create() is null)
            return;
        var total = new LockstepCounts();
        for (var seed = 1; seed <= 6; seed++)
            total += new Lockstep(seed, 1500).Run();
        output.WriteLine(total.ToString());
    }

    /// <summary>
    /// Valve's own steps, with the broadphase port following every call the
    /// step makes into the broadphase (detoured: body moves in the solve and
    /// the TOI passes, immediate moves, Begin, Finalize, the pre-step query,
    /// the new contacts and the pair erases of destroyed contacts), compared
    /// at each Finalize, query and new-contact list and after every step.
    /// </summary>
    [Fact]
    public void TheBroadphaseFollowsValveSteps()
    {
        if (Vphysics2World.Create() is null)
            return;
        var total = new LockstepCounts();
        for (var seed = 1; seed <= 3; seed++)
            total += new Lockstep(seed, 0).Follow(400);
        output.WriteLine(total.ToString());
    }

    private record struct LockstepCounts(
        int Operations, int Bodies, int Shapes, int Immediate, int Updates, int BodyMoves, int PreSteps,
        int Contacts, int Destroyed, int Reselected, int Erased)
    {
        public static LockstepCounts operator +(LockstepCounts a, LockstepCounts b) => new(
            a.Operations + b.Operations, a.Bodies + b.Bodies, a.Shapes + b.Shapes, a.Immediate + b.Immediate,
            a.Updates + b.Updates, a.BodyMoves + b.BodyMoves, a.PreSteps + b.PreSteps, a.Contacts + b.Contacts,
            a.Destroyed + b.Destroyed, a.Reselected + b.Reselected, a.Erased + b.Erased);
    }

    private sealed class Lockstep(int seed, int operations)
    {
        private readonly Random _r = new(seed);
        private readonly Vphysics2World _w = Vphysics2World.Create()!;
        private Broadphase _bp = null!;
        private readonly List<(Vphysics2World.Body Valve, BroadphaseBody Port)> _bodies = [];
        private readonly Dictionary<nint, BroadphaseBody> _byRn = [];
        private readonly List<BroadphaseShape> _shapes = [];
        private readonly HierarchyUpdate _update = new();
        private readonly PairQuery _query = new();
        private LockstepCounts _counts;
        private int _op;
        private byte* Bp => _w.Broadphase();

        public LockstepCounts Run()
        {
            Start();
            for (var i = 0; i < 12; i++)
                AddBody();
            for (_op = 0; _op < operations; _op++)
            {
                var roll = _r.Next(100);
                if (roll < 6)
                    AddBody();
                else if (roll < 30)
                    MoveImmediately();
                else if (roll < 60)
                    HierarchyUpdate();
                else if (roll < 75)
                    PreStep();
                else if (roll < 80)
                    DestroyProxy();
                else if (roll < 88)
                    Reselect();
                else
                    ErasePair();
                _counts.Operations++;
                CompareAll();
            }
            return _counts;
        }

        /// <summary>The port's CBroadphase, checked against Valve's ctor: trees, query lists, group table.</summary>
        private void Start()
        {
            _bp = new Broadphase(GroupTable(_w), *(ushort*)_w.At(CollidingMaskVa));
            Assert.Equal(7, *(int*)(Bp + 0x268));
            for (var t = 0; t < 7; t++)
            {
                var v = Bp + t * 0x58;
                var tree = _bp.Trees[t];
                Assert.Equal(Bits(*(float*)(v + 0x4c)), Bits(tree.Margin));
                Assert.Equal((v[0x50], v[0x51], v[0x52], v[0x53] != 0), (tree.Kind, tree.Index, tree.Flags, tree.SelfCollide));
                Assert.Equal(new ReadOnlySpan<byte>(v + 0x44, *(int*)(v + 0x40)).ToArray(), tree.Targets.ToArray());
                Assert.Equal(Bp[0x26c + t], _bp.Roles[t]);
            }
            for (var g = 0; g < 64; g++)
                Assert.True((Bp[0x273 + g] != 0) == _bp.FastGroups[g], $"fast group {g}");
        }

        // -------------------------------------------------------------- following steps

        private static Lockstep? _current;
        private static string? _failure;
        private static readonly object Gate = new();
        private static nint _updateBody, _toiBody, _immediate, _begin, _finalize, _preStep, _build, _erase;

        /// <summary>A floor, a few static blocks and a pile of dropped boxes, stepped with the port following.</summary>
        public LockstepCounts Follow(int steps)
        {
            using var hooks = new Vphysics2Hooks();
            _current = this;
            _failure = null;
            try
            {
                Start();
                Install(hooks);
                BuildScene();
                for (var i = 0; i < steps; i++)
                {
                    _op = i;
                    _w.Step(1f / 90f, i == 0);
                    Assert.True(_failure == null, _failure);
                    CompareAll();
                    _counts.Operations++;
                }
            }
            finally
            {
                hooks.Dispose();
                _current = null;
            }
            return _counts;
        }

        private void BuildScene()
        {
            var floor = _w.CreateBody(0, new Vec3(0, 0, 0), Quat.Identity);
            Register(floor, body => body.AddMesh(Floor(_w), Vector3.One));
            for (var i = 0; i < 4; i++)
            {
                var block = _w.CreateBody(0, new Vec3(F(_r, -150, 150), F(_r, -150, 150), 8), Rotation(_r));
                Register(block, body => _w.AddHull(body, _w.CreateHull(RandomPoints(_r, 8)), 1f));
            }
            for (var i = 0; i < 40; i++)
            {
                var box = _w.CreateBody(2, new Vec3(F(_r, -120, 120), F(_r, -120, 120), F(_r, 40, 400)), Rotation(_r));
                Register(box, body => _w.AddHull(body, _w.CreateHull(RandomPoints(_r, _r.Next(2) == 0 ? 8 : 12)), _r.Next(4) == 0 ? F(_r, 0.6, 1.5) : 1f));
                if (_r.Next(3) == 0)
                    box.State.Flags249 &= unchecked((byte)~8);
                _w.UpdateMass(box);
                Copy(box, _byRn[(nint)box.Rn]);
            }
        }

        private static byte* Floor(Vphysics2World w)
        {
            var vertices = new List<Vector3>();
            var indices = new List<int>();
            const int n = 9;
            for (var i = 0; i < n; i++)
                for (var j = 0; j < n; j++)
                    vertices.Add(new Vector3(i * 80 - 320, j * 80 - 320, ((i * 7 + j * 3) % 5) * 2f));
            for (var i = 0; i + 1 < n; i++)
                for (var j = 0; j + 1 < n; j++)
                {
                    var a = i * n + j;
                    indices.AddRange([a, a + n, a + 1, a + 1, a + n, a + n + 1]);
                }
            return w.CreateMesh([.. indices], [.. vertices]);
        }

        private void Register(Vphysics2World.Body body, Func<Vphysics2World.Body, nint> add)
        {
            var port = new BroadphaseBody();
            Copy(body, port);
            _bodies.Add((body, port));
            _byRn[(nint)body.Rn] = port;
            _counts.Bodies++;
            var shape = PortShape(Vphysics2WorldExtensions.RnShape(add(body)), port);
            port.Shapes.Add(shape);
            Copy(body, port);
            _bp.AddShape(shape);
            _shapes.Add(shape);
            _counts.Shapes++;
        }

        private void Install(Vphysics2Hooks hooks)
        {
            _updateBody = hooks.Detour(_w.At(UpdateBodyVa), (nint)(delegate* unmanaged<byte*, byte, byte*, void>)&OnUpdateBody,
                                       Convert.FromHexString("488bc4488958084889681048897018"));
            _toiBody = hooks.Detour(_w.At(ToiBodyVa), (nint)(delegate* unmanaged<byte*, byte*, ulong>)&OnToiBody,
                                    Convert.FromHexString("48895c240848895424105556574154"));
            _immediate = hooks.Detour(_w.At(ImmediateVa), (nint)(delegate* unmanaged<byte*, void>)&OnImmediate,
                                      Convert.FromHexString("488bc4534881ecd0000000f6814902000001"));
            _begin = hooks.Detour(_w.At(BeginVa), (nint)(delegate* unmanaged<byte*, byte*, ulong, int, byte, void>)&OnBegin,
                                  Convert.FromHexString("48896c241848897c24204156"));
            _finalize = hooks.Detour(_w.At(FinalizeVa), (nint)(delegate* unmanaged<byte*, byte*, byte*, void>)&OnFinalize,
                                     Convert.FromHexString("48895c242055565741544155"));
            _preStep = hooks.Detour(_w.At(PreStepVa), (nint)(delegate* unmanaged<byte*, byte*, int, byte, void>)&OnPreStep,
                                    Convert.FromHexString("48895c24084889742410574883ec30"));
            _build = hooks.Detour(_w.At(BuildContactsVa), (nint)(delegate* unmanaged<byte*, byte*, void>)&OnBuild,
                                  Convert.FromHexString("488bc44889501055564154488d68a1"));
            _erase = hooks.Detour(_w.At(PairEraseVa), (nint)(delegate* unmanaged<ulong, byte*, void>)&OnErase,
                                  Convert.FromHexString("48895c240848896c2410488974241857"));
        }

        /// <summary>Runs a port step under the lock, keeping the first failure for the test thread.</summary>
        private static void Mirror(Action<Lockstep> action)
        {
            lock (Gate)
            {
                if (_current is not { } m || _failure != null)
                    return;
                try
                {
                    action(m);
                }
                catch (Exception e)
                {
                    _failure = $"step {m._op}: {e.Message}";
                }
            }
        }

        [UnmanagedCallersOnly]
        private static void OnUpdateBody(byte* body, byte check, byte* hu)
        {
            Mirror(m =>
            {
                if (m._byRn.TryGetValue((nint)body, out var port))
                {
                    port.State = *(RnBodyState*)body;
                    m._bp.UpdateBody(port, check != 0, m._update);
                    m._counts.BodyMoves++;
                }
            });
            ((delegate* unmanaged<byte*, byte, byte*, void>)_updateBody)(body, check, hu);
        }

        [UnmanagedCallersOnly]
        private static ulong OnToiBody(byte* body, byte* parameters)
        {
            var result = ((delegate* unmanaged<byte*, byte*, ulong>)_toiBody)(body, parameters);
            if ((uint)result != 2)
                Mirror(m =>
                {
                    if (m._byRn.TryGetValue((nint)body, out var port))
                    {
                        port.State = *(RnBodyState*)body;
                        m._bp.UpdateBody(port, false, m._update);
                        m._counts.BodyMoves++;
                    }
                });
            return result;
        }

        [UnmanagedCallersOnly]
        private static void OnImmediate(byte* body)
        {
            ((delegate* unmanaged<byte*, void>)_immediate)(body);
            Mirror(m =>
            {
                if (m._byRn.TryGetValue((nint)body, out var port))
                {
                    port.State = *(RnBodyState*)body;
                    m._bp.MoveBodyImmediate(port);
                    m._counts.Immediate++;
                }
            });
        }

        [UnmanagedCallersOnly]
        private static void OnBegin(byte* hu, byte* bp, ulong mode, int threads, byte priority)
        {
            ((delegate* unmanaged<byte*, byte*, ulong, int, byte, void>)_begin)(hu, bp, mode, threads, priority);
            Mirror(m =>
            {
                if (bp == m.Bp)
                    m._bp.BeginHierarchyUpdate(m._update, (int)mode, threads, priority);
            });
        }

        [UnmanagedCallersOnly]
        private static void OnFinalize(byte* bp, byte* hu, byte* pq)
        {
            ((delegate* unmanaged<byte*, byte*, byte*, void>)_finalize)(bp, hu, pq);
            Mirror(m =>
            {
                if (bp != m.Bp)
                    return;
                m._bp.FinalizeHierarchyUpdate(m._update, m._query);
                m.CompareQuery(pq, "after finalize");
                m.CompareAll();
                m._counts.Updates++;
            });
        }

        [UnmanagedCallersOnly]
        private static void OnPreStep(byte* bp, byte* pq, int threads, byte priority)
        {
            ((delegate* unmanaged<byte*, byte*, int, byte, void>)_preStep)(bp, pq, threads, priority);
            Mirror(m =>
            {
                if (bp != m.Bp)
                    return;
                m._bp.PreStepQuery(m._query, threads, priority);
                m.CompareQuery(pq, "after the pre-step query");
                m._counts.PreSteps++;
            });
        }

        [UnmanagedCallersOnly]
        private static void OnBuild(byte* world, byte* pq)
        {
            var before = *(int*)(world + 0xa10);
            ((delegate* unmanaged<byte*, byte*, void>)_build)(world, pq);
            Mirror(m =>
            {
                if (world != m._w.Rn)
                    return;
                m.CompareNewContacts(before, m._bp.BuildNewContacts(m._query));
                m.CompareQuery(pq, "after the contacts");
                m.CompareAll();
            });
        }

        [UnmanagedCallersOnly]
        private static void OnErase(ulong contact, byte* bp)
        {
            var c = (byte*)(contact & ~1ul);
            var (a, b) = (*(ulong*)(c + 8), *(ulong*)(c + 0x10));
            ((delegate* unmanaged<ulong, byte*, void>)_erase)(contact, bp);
            Mirror(m =>
            {
                if (bp != m.Bp)
                    return;
                Assert.True(m._bp.Pairs.Erase(a, b), $"the pair {a:x},{b:x} was not in the port's set");
                m._counts.Erased++;
            });
        }

        // -------------------------------------------------------------- building

        private void AddBody()
        {
            var type = _r.Next(10) switch { < 3 => 0, < 5 => 1, _ => 2 };
            var body = _w.CreateBody(type, new Vec3(F(_r, -600, 600), F(_r, -600, 600), F(_r, -100, 300)), Rotation(_r));
            if (_r.Next(4) == 0)
                body.SetTransform(At(body), Q(body.State.Orientation), F(_r, 0.5, 2));
            var port = new BroadphaseBody();
            Copy(body, port);
            _bodies.Add((body, port));
            _byRn[(nint)body.Rn] = port;
            _counts.Bodies++;
            var count = 1 + _r.Next(type == 0 ? 3 : 2);
            for (var i = 0; i < count; i++)
            {
                nint shape;
                if (type == 0 && _r.Next(2) == 0)
                    shape = body.AddMesh(RandomMesh(_w, _r), _r.Next(3) == 0 ? new Vector3(F(_r, 0.5, 2), F(_r, 0.5, 2), 1f) : Vector3.One);
                else
                    shape = _w.AddHull(body, _w.CreateHull(RandomPoints(_r, _r.Next(2) == 0 ? 8 : 16)), _r.Next(4) == 0 ? F(_r, 0.5, 2) : 1f);
                var rn = Vphysics2WorldExtensions.RnShape(shape);
                var ours = PortShape(rn, port);
                port.Shapes.Add(ours);
                Copy(body, port);
                _bp.AddShape(ours);
                _shapes.Add(ours);
                _counts.Shapes++;
            }
        }

        private static Vector3 At(Vphysics2World.Body b)
        {
            var p = b.State.Position;
            return new Vector3(p.X, p.Y, p.Z);
        }

        private static Quaternion Q(Quat q) => new(q.X, q.Y, q.Z, q.W);

        // -------------------------------------------------------------- operations

        /// <summary>SetTransform outside the step: FUN_1801b9100 on Valve's side.</summary>
        private void MoveImmediately()
        {
            var (body, port) = _bodies[_r.Next(_bodies.Count)];
            var p = At(body);
            var d = _r.Next(3) switch
            {
                0 => new Vector3(F(_r, -2, 2), F(_r, -2, 2), F(_r, -2, 2)),
                1 => new Vector3(F(_r, -40, 40), F(_r, -40, 40), F(_r, -20, 20)),
                _ => new Vector3(F(_r, -400, 400), F(_r, -400, 400), F(_r, -100, 100)),
            };
            var q = _r.Next(2) == 0 ? Q(body.State.Orientation) : Q(Rotation(_r));
            body.SetTransform(p + d, q, body.State.Scale);
            Copy(body, port);
            _bp.MoveBodyImmediate(port);
            _counts.Immediate++;
        }

        /// <summary>Begin, moves of random bodies through FUN_1801b9270, Finalize, then the new contacts.</summary>
        private void HierarchyUpdate()
        {
            var scratch = _w.Scratch();
            var mode = _r.Next(3) == 0 ? 1 : 0;
            var threads = _r.Next(4) == 0 ? 1 : 0x7fffffff;
            var begin = (delegate* unmanaged<byte*, byte*, ulong, int, byte, void>)_w.At(BeginVa);
            begin(scratch, Bp, (ulong)mode, threads, 2);
            _bp.BeginHierarchyUpdate(_update, mode, threads, 2);
            var update = (delegate* unmanaged<byte*, byte, byte*, void>)_w.At(UpdateBodyVa);
            var moves = 1 + _r.Next(Math.Min(_bodies.Count, 12));
            var chosen = new HashSet<int>();
            for (var i = 0; i < moves; i++)
            {
                var k = _r.Next(_bodies.Count);
                if (!chosen.Add(k))
                    continue;
                var (body, port) = _bodies[k];
                var check = Wiggle(body);
                update(body.Rn, (byte)(check ? 1 : 0), scratch);
                Copy(body, port);
                _bp.UpdateBody(port, check, _update);
                _counts.BodyMoves++;
            }
            var finalize = (delegate* unmanaged<byte*, byte*, byte*, void>)_w.At(FinalizeVa);
            finalize(Bp, scratch, scratch + 0x4e8);
            _bp.FinalizeHierarchyUpdate(_update, _query);
            CompareQuery(scratch + 0x4e8, "after finalize");
            BuildContacts();
            _counts.Updates++;
        }

        /// <summary>
        /// A step's motion written into the body: a new centre and orientation,
        /// the old ones kept as the sweep start (bit 6) at times, and the fast
        /// bit (7) toggled for moving bodies, which then checks the tree.
        /// </summary>
        private bool Wiggle(Vphysics2World.Body body)
        {
            ref var s = ref body.State;
            var oldPosition = s.Position;
            var oldOrientation = s.Orientation;
            var scale = _r.Next(4) == 0 ? 60.0 : _r.Next(3) == 0 ? 0.0 : 6.0;
            s.Position = new Vec3(s.Position.X + F(_r, -scale, scale), s.Position.Y + F(_r, -scale, scale), s.Position.Z + F(_r, -scale, scale));
            if (_r.Next(3) == 0)
                s.Orientation = Rotation(_r);
            var flags = s.Flags249 & ~0x40;
            if (_r.Next(3) == 0)
            {
                flags |= 0x40;
                s.PreviousPosition = oldPosition;
                s.PreviousOrientation = oldOrientation;
            }
            var check = false;
            if (s.BodyType != 0 && _r.Next(4) == 0)
            {
                flags ^= 0x80;
                check = true;
            }
            s.Flags249 = (byte)flags;
            return check || _r.Next(8) == 0;
        }

        private void PreStep()
        {
            var scratch = _w.Scratch();
            var threads = _r.Next(4) == 0 ? 1 : 0x7fffffff;
            var prestep = (delegate* unmanaged<byte*, byte*, int, byte, void>)_w.At(PreStepVa);
            prestep(Bp, scratch + 0x4e8, threads, 2);
            _bp.PreStepQuery(_query, threads, 2);
            CompareQuery(scratch + 0x4e8, "after the pre-step query");
            BuildContacts();
            _counts.PreSteps++;
        }

        private void BuildContacts()
        {
            var before = _w.ActiveContacts().Length;
            var build = (delegate* unmanaged<byte*, byte*, void>)_w.At(BuildContactsVa);
            build(_w.Rn, _w.Scratch() + 0x4e8);
            CompareNewContacts(before, _bp.BuildNewContacts(_query));
            CompareQuery(_w.Scratch() + 0x4e8, "after the contacts");
        }

        private void CompareNewContacts(int before, List<Broadphase.NewContact> ours)
        {
            var all = _w.ActiveContacts();
            Assert.True(all.Length - before == ours.Count, $"op {_op}: {all.Length - before} new contacts vs {ours.Count}");
            for (var i = 0; i < ours.Count; i++)
            {
                var c = (byte*)all[before + i];
                var o = ours[i];
                Assert.True(*(ulong*)(c + 8) == o.A.Handle && *(ulong*)(c + 0x10) == o.B.Handle,
                            $"op {_op}: contact {i} shapes {*(ulong*)(c + 8):x},{*(ulong*)(c + 0x10):x} vs {o.A.Handle:x},{o.B.Handle:x}");
                Assert.True(*(ulong*)(c + 0x40) == o.Key, $"op {_op}: contact {i} key {*(ulong*)(c + 0x40):x} vs {o.Key:x}");
                Assert.True(*(ushort*)(c + 0x78) == o.Flags, $"op {_op}: contact {i} flags {*(ushort*)(c + 0x78):x} vs {o.Flags:x}");
            }
            _counts.Contacts += ours.Count;
        }

        private void DestroyProxy()
        {
            var shape = _shapes[_r.Next(_shapes.Count)];
            var destroy = (delegate* unmanaged<byte*, ulong, void>)_w.At(DestroyProxyVa);
            destroy(Bp, shape.Handle);
            _bp.DestroyProxy(shape);
            _counts.Destroyed++;
        }

        /// <summary>New attribute flags and mask, then FUN_1802d7560 moves the proxy if its tree changed.</summary>
        private void Reselect()
        {
            var shape = _shapes[_r.Next(_shapes.Count)];
            if (shape.ProxyId == -1)
                return;
            var rn = (byte*)shape.Handle;
            ref var a = ref *(CollisionAttributes*)(rn + 0x50);
            a.Flags = (byte)(_r.Next(3) == 0 ? a.Flags ^ 0x25 : a.Flags);
            a.FunctionMask = (ushort)(_r.Next(3) == 0 ? 0 : a.FunctionMask);
            a.MaskIsDirect = (byte)(_r.Next(3) == 0 ? 1 - a.MaskIsDirect : a.MaskIsDirect);
            a.Group = (byte)(_r.Next(3) == 0 ? _r.Next(64) : a.Group);
            shape.Attributes = a;
            var reselect = (delegate* unmanaged<byte*, ulong, void>)_w.At(ReselectVa);
            reselect(Bp, shape.Handle);
            _bp.ReselectTree(shape);
            _counts.Reselected++;
        }

        /// <summary>A pair leaves the set (as when Collide destroys its contact), so it can be found again.</summary>
        private void ErasePair()
        {
            var set = *(byte**)(Bp + 0x2b8);
            if (*(int*)(set + 0x10) == 0)
                return;
            var slots = *(PairSet.Slot**)(set + 8);
            var buckets = *(int*)(set + 0x14);
            var start = _r.Next(buckets);
            for (var i = 0; i < buckets; i++)
            {
                var s = slots[(start + i) % buckets];
                if ((int)s.Word < 0)
                    continue;
                var pair = stackalloc ulong[2] { s.A, s.B };
                var hash = (delegate* unmanaged<void*, ulong*, uint>)_w.At(HashVa);
                var erase = (delegate* unmanaged<byte*, ulong*, uint, uint>)_w.At(SetEraseVa);
                erase(set, pair, hash(null, pair));
                Assert.True(_bp.Pairs.Erase(s.A, s.B));
                _counts.Erased++;
                return;
            }
        }

        // -------------------------------------------------------------- comparing

        private void CompareAll()
        {
            for (var t = 0; t < 7; t++)
                CompareTree(t);
            foreach (var shape in _shapes)
            {
                var id = *(int*)((byte*)shape.Handle + 0x1c);
                Assert.True(id == shape.ProxyId, $"op {_op}: shape {shape.Handle:x} proxy {id:x} vs {shape.ProxyId:x}");
            }
            ComparePairSet(*(byte**)(Bp + 0x2b8), _bp.Pairs, $"op {_op}");
            Assert.True(*(int*)(Bp + 0x2b4) == _bp.SolveCount, $"op {_op}: solve count");
        }

        private void CompareTree(int t)
        {
            var v = Bp + t * 0x58;
            var tree = _bp.Trees[t];
            var o = tree.Nodes;
            var header = (*(int*)v, *(int*)(v + 4), *(int*)(v + 8), *(int*)(v + 0xc), *(int*)(v + 0x10));
            var ours = (o.Root, o.LeafCount, o.NodeCount, o.Capacity, o.FreeHead);
            Assert.True(header == ours, $"op {_op}: tree {t} header {header} vs {ours}");
            var free = new HashSet<int>();
            for (var i = o.FreeHead; i != -1; i = o.Nodes[i].Next)
                free.Add(i);
            var nodes = *(byte**)(v + 0x18);
            var mine = o.NodeBytes;
            for (var i = 0; i < o.Capacity; i++)
            {
                var valve = new ReadOnlySpan<byte>(nodes + i * 0x30, free.Contains(i) ? 4 : 0x30);
                if (!valve.SequenceEqual(mine.Slice(i * 0x30, valve.Length)))
                    Assert.Fail($"op {_op}: tree {t} node {i} differs\nvalve {Convert.ToHexString(valve)}\nours  {Convert.ToHexString(mine.Slice(i * 0x30, valve.Length))}");
            }
            CompareBits(v + 0x20, tree.TopBits, $"tree {t} top bits");
            CompareBits(v + 0x30, tree.LeafBits, $"tree {t} leaf bits");
        }

        private void CompareBits(byte* vector, List<uint> ours, string what)
        {
            var count = *(int*)vector;
            Assert.True(count == ours.Count, $"op {_op}: {what} count {count} vs {ours.Count}");
            if (count == 0)
                return;
            var words = *(uint**)(vector + 8);
            for (var i = 0; i < count; i++)
                Assert.True(words[i] == ours[i], $"op {_op}: {what} word {i} {words[i]:x} vs {ours[i]:x}");
        }

        /// <summary>The pair query's records, jobs and counts (FUN_1802d49f0's layout).</summary>
        private void CompareQuery(byte* pq, string when)
        {
            Assert.True(*(int*)(pq + 0x270) == _query.Mode, $"op {_op} {when}: query mode {*(int*)(pq + 0x270)} vs {_query.Mode}");
            for (var t = 0; t < 7; t++)
            {
                var r = pq + 8 + t * 0x58;
                var q = _query.Records[t];
                var count = *(int*)r;
                Assert.True(count == q.Items.Count, $"op {_op} {when}: tree {t} {count} items vs {q.Items.Count}");
                for (var i = 0; i < count; i++)
                    Assert.True((*(int**)(r + 8))[i] == q.Items[i], $"op {_op} {when}: tree {t} item {i}");
                var targets = *(int*)(r + 0x10);
                Assert.True(targets == q.Targets.Count, $"op {_op} {when}: tree {t} {targets} targets vs {q.Targets.Count}");
                for (var i = 0; i < targets; i++)
                    Assert.True(*(int*)(r + 0x14 + i * 8) == q.Targets[i], $"op {_op} {when}: tree {t} target {i}");
                if (count == 0)
                    continue;
                var flags = (r[0x50] != 0, r[0x51] != 0, r[0x52] != 0, r[0x53] != 0);
                var mine = (q.Self, q.AllMoved, q.NonEmpty, q.TreeVsTree);
                Assert.True(flags == mine, $"op {_op} {when}: tree {t} flags {flags} vs {mine}");
            }
            var jobs = *(int*)(pq + 0x274);
            Assert.True(jobs == _query.Jobs.Count, $"op {_op} {when}: {jobs} jobs vs {_query.Jobs.Count}");
            for (var i = 0; i < jobs; i++)
                Assert.True(*(int*)(pq + 0x278 + i * 4) == _query.Jobs[i], $"op {_op} {when}: job {i}");
            var stats = (*(int*)(pq + 0x294), *(int*)(pq + 0x298), *(int*)(pq + 0x29c));
            Assert.True(stats == (_query.Work, _query.Moved, _query.Searches), $"op {_op} {when}: work {stats} vs {(_query.Work, _query.Moved, _query.Searches)}");
        }

        // -------------------------------------------------------------- mirrors

        private static void Copy(Vphysics2World.Body valve, BroadphaseBody port)
        {
            port.State = valve.State;
            port.Id = *(uint*)valve.Rn;
        }
    }

    // ------------------------------------------------------------------ helpers

    private static (nint, BroadphaseShape) Mirror(nint shape, BroadphaseBody body)
    {
        var port = PortShape(Vphysics2WorldExtensions.RnShape(shape), body);
        body.Shapes.Add(port);
        return (shape, port);
    }

    private static BroadphaseBody Port(Vphysics2World.Body body) => new() { State = body.State, Id = *(uint*)body.Rn };

    /// <summary>A port shape with everything the broadphase reads copied from Valve's CRnShape.</summary>
    private static BroadphaseShape PortShape(byte* rn, BroadphaseBody body)
    {
        var type = *(int*)(rn + 0x18);
        var shape = new BroadphaseShape
        {
            Handle = (ulong)rn,
            Body = body,
            Type = type,
            ProxyId = *(int*)(rn + 0x1c),
            Attributes = *(CollisionAttributes*)(rn + 0x50),
            HasProxy = rn[0xae] != 0,
        };
        if (type == BroadphaseShape.HullType)
        {
            var hull = *(byte**)(rn + 0xc0);
            var s = *(float*)(rn + 0xb8);
            shape.Scale = new Vec3(s, s, s);
            shape.LocalMin = *(Vec3*)(hull + 0x14);
            shape.LocalMax = *(Vec3*)(hull + 0x20);
        }
        else
        {
            var mesh = *(byte**)(rn + 0xc8);
            shape.Scale = *(Vec3*)(rn + 0xb8);
            shape.LocalMin = *(Vec3*)mesh;
            shape.LocalMax = *(Vec3*)(mesh + 0xc);
            shape.Vertices = new ReadOnlySpan<Vec3>(*(Vec3**)(mesh + 0x38), *(int*)(mesh + 0x30)).ToArray();
        }
        return shape;
    }

    private static ushort[] GroupTable(Vphysics2World w)
        => new ReadOnlySpan<ushort>((ushort*)w.At(GroupTableVa), 4096).ToArray();

    private static void ComparePairSet(byte* set, PairSet ours, string where)
    {
        var count = *(int*)(set + 0x10);
        var buckets = *(int*)(set + 0x14);
        Assert.True(count == ours.Count && buckets == ours.Buckets, $"{where}: pair set count/buckets {count}/{buckets} vs {ours.Count}/{ours.Buckets}");
        if (buckets == 0)
            return;
        var slots = *(PairSet.Slot**)(set + 8);
        for (var i = 0; i < buckets; i++)
        {
            var v = slots[i];
            var o = ours.Slots[i];
            var same = v.Word == o.Word && ((int)v.Word < 0 || (v.A == o.A && v.B == o.B));
            Assert.True(same, $"{where}: pair slot {i} {v.Word:x} {v.A:x} {v.B:x} vs {o.Word:x} {o.A:x} {o.B:x}");
        }
    }

    private static float F(Random r, double lo, double hi) => (float)(lo + r.NextDouble() * (hi - lo));

    private static Vec3 V(Random r, double s) => new(F(r, -s, s), F(r, -s, s), F(r, -s, s));

    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    private static Quat Rotation(Random r)
        => RnMath.Normalize(new Quat(F(r, -1, 1), F(r, -1, 1), F(r, -1, 1), F(r, -1, 1)));

    private static RnTransform RandomFrame(Random r, bool identity)
    {
        var q = identity ? Quat.Identity : Rotation(r);
        return new RnTransform { R = RnMath.Matrix(q), T = V(r, 2000) };
    }

    private static bool Same(in RnTransform a, in RnTransform b)
    {
        fixed (RnTransform* pa = &a)
        fixed (RnTransform* pb = &b)
            return new ReadOnlySpan<byte>(pa, sizeof(RnTransform)).SequenceEqual(new ReadOnlySpan<byte>(pb, sizeof(RnTransform)));
    }

    private static Vector3[] RandomPoints(Random r, int n)
    {
        var h = new Vector3(F(r, 2, 40), F(r, 2, 40), F(r, 2, 40));
        var c = new Vector3(F(r, -10, 10), F(r, -10, 10), F(r, -10, 10));
        var points = new Vector3[n];
        for (var i = 0; i < n; i++)
            points[i] = i < 8
                ? c + new Vector3((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z)
                : c + new Vector3(F(r, -1, 1) * h.X, F(r, -1, 1) * h.Y, F(r, -1, 1) * h.Z);
        return points;
    }

    private static byte* RandomMesh(Vphysics2World w, Random r)
    {
        var n = 2 + r.Next(6);
        var size = F(r, 50, 800);
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
                vertices.Add(new Vector3(i * size / n - size / 2, j * size / n - size / 2, F(r, -20, 20)));
        for (var i = 0; i + 1 < n; i++)
            for (var j = 0; j + 1 < n; j++)
            {
                var a = i * n + j;
                indices.AddRange([a, a + n, a + 1, a + 1, a + n, a + n + 1]);
            }
        return w.CreateMesh([.. indices], [.. vertices]);
    }

    private static CollisionAttributes RandomAttributes(Random r)
    {
        ulong Layers() => r.Next(3) == 0 ? 0 : (ulong)r.NextInt64() & (r.Next(2) == 0 ? 0xff : ulong.MaxValue);
        return new CollisionAttributes
        {
            InteractsAs = Layers(),
            InteractsWith = Layers(),
            InteractsExclude = r.Next(3) == 0 ? Layers() : 0,
            EntityId = r.Next(4) == 0 ? -1 : r.Next(8),
            OwnerId = r.Next(4) == 0 ? -1 : r.Next(8),
            HierarchyId = (ushort)(r.Next(4) switch { 0 => 0, 1 => 0xffff, _ => r.Next(4) }),
            FunctionMask = (ushort)r.Next(0x10000),
            MaskIsDirect = (byte)r.Next(2),
            FunctionIndex = (byte)r.Next(40),
            Group = (byte)(r.Next(3) == 0 ? 0 : r.Next(64)),
            Flags = (byte)r.Next(256),
        };
    }
}
