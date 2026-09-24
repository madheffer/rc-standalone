using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.Tests;

/// <summary>
/// Reads a live vphysics2 RnWorld into an <see cref="RnWorld"/> model: bodies,
/// shapes, contacts with their caches and list links, islands and the island
/// manager's lists. Every model object keeps its native address, so two reads
/// (or a read and a ported step) can be compared object for object.
/// </summary>
internal sealed unsafe class RnWorldReader
{
    public const ulong ConvexContactVtable = 0x1803e41c0, MeshContactVtable = 0x1803e4120;

    private readonly nint _module;
    private readonly Dictionary<nint, RnBody> _bodies = [];
    private readonly Dictionary<nint, RnShape> _shapes = [];
    private readonly Dictionary<nint, RnContact> _contacts = [];
    private readonly Dictionary<nint, RnIsland> _islands = [];
    private readonly Dictionary<nint, RnHull> _hulls;
    private readonly Dictionary<nint, RnMesh> _meshes;

    public readonly RnWorld World = new();

    private RnWorldReader(nint module, Dictionary<nint, RnHull> hulls, Dictionary<nint, RnMesh> meshes)
    {
        _module = module;
        _hulls = hulls;
        _meshes = meshes;
    }

    /// <summary>
    /// Reads the world. Hull and mesh conversions are cached across reads.
    /// With <paramref name="broadphase"/> the broadphase comes too (trees,
    /// dirty bits, fast groups, solve count, pair set), so the world can step.
    /// </summary>
    public static RnWorld Read(nint module, byte* w, Dictionary<nint, RnHull> hulls, Dictionary<nint, RnMesh> meshes,
                               bool broadphase = false)
    {
        var r = new RnWorldReader(module, hulls, meshes);
        r.ReadAll(w);
        if (broadphase)
            r.ReadBroadphase(w);
        return r.World;
    }

    private const ulong GroupTableVa = 0x18045b328, CollidingMaskVa = 0x18045b2b8;

    private void ReadBroadphase(byte* w)
    {
        var bp = *(byte**)(w + 0x110);
        var table = new ReadOnlySpan<ushort>((ushort*)Vphysics2Oracle.At(_module, GroupTableVa), 4096).ToArray();
        var port = new Broadphase(table, *(ushort*)Vphysics2Oracle.At(_module, CollidingMaskVa));
        for (var t = 0; t < 7; t++)
        {
            var v = bp + t * 0x58;
            var tree = port.Trees[t];
            var nodes = tree.Nodes;
            nodes.Root = *(int*)v;
            nodes.LeafCount = *(int*)(v + 4);
            nodes.NodeCount = *(int*)(v + 8);
            nodes.Capacity = *(int*)(v + 0xc);
            nodes.FreeHead = *(int*)(v + 0x10);
            nodes.Nodes = new ReadOnlySpan<TreeNode>(*(TreeNode**)(v + 0x18), nodes.Capacity).ToArray();
            tree.TopBits.Clear();
            tree.TopBits.AddRange(Vec<uint>(v + 0x20));
            tree.LeafBits.Clear();
            tree.LeafBits.AddRange(Vec<uint>(v + 0x30));
        }
        for (var g = 0; g < 64; g++)
            port.FastGroups[g] = bp[0x273 + g] != 0;
        port.SolveCount = *(int*)(bp + 0x2b4);
        var set = *(byte**)(bp + 0x2b8);
        var pairs = port.Pairs;
        pairs.Count = *(int*)(set + 0x10);
        pairs.Buckets = *(int*)(set + 0x14);
        pairs.MinimumSize = *(int*)(set + 0x18);
        pairs.Slots = pairs.Buckets == 0 ? [] : new ReadOnlySpan<PairSet.Slot>(*(PairSet.Slot**)(set + 8), pairs.Buckets).ToArray();
        foreach (var s in _shapes.Values)
            port.Register(s.Proxy);
        World.Broadphase = port;
    }

    // CUtlVector {int count; uint alloc; T* data} with the data pointer at +8.
    private static T[] Vec<T>(byte* at) where T : unmanaged
    {
        var count = *(int*)at;
        if (count <= 0)
            return [];
        return new ReadOnlySpan<T>(*(void**)(at + 8), count).ToArray();
    }

    // A small vector whose single element lives inline at +8 while the capacity is 1.
    private static nint[] SmallVec(byte* at)
    {
        var count = *(int*)at;
        var cap = *(uint*)(at + 4) & 0x7fffffff;
        if (count <= 0 || cap == 0)
            return [];
        var data = cap < 2 ? (nint*)(at + 8) : *(nint**)(at + 8);
        return new ReadOnlySpan<nint>(data, count).ToArray();
    }

    private void ReadAll(byte* w)
    {
        var bodies = Vec<nint>(w + 0x678);
        foreach (var b in bodies)
            Body(b);
        foreach (var b in _bodies.Values)
            World.Bodies.Add(b);
        World.Bodies.Sort((x, y) => x.Index.CompareTo(y.Index));

        foreach (var b in Vec<nint>(w + 0xa00))
            World.ActiveBodies.Add(_bodies[b]);
        for (var k = 0; k < 2; k++)
            foreach (var c in Vec<nint>(w + 0x6d8 + 0x10 * k))
                World.AllContacts[k].Add(Contact(c));
        foreach (var c in Vec<nint>(w + 0xa10))
            World.ActiveContacts.Add(Contact(c));
        foreach (var c in Vec<nint>(w + 0x758))
            World.Destroyed.Add(Contact(c));
        foreach (var b in Vec<nint>(w + 0xa58))
            World.ForcedBodies.Add(Body(b));

        // The step lists (0x38 bytes each): data at +8, count at +0x2c.
        for (var k = 0; k < 3; k++)
        {
            var list = w + 0xa78 + 0x38 * k;
            var data = *(nint**)(list + 8);
            for (var i = 0; i < *(int*)(list + 0x2c); i++)
                World.ContinuousBodies[k].Add(Body(data[i]));
        }

        var mgr = w + 0x118;
        foreach (var n in Vec<nint>(mgr))
            World.Islands.All.Add(Node(n));
        foreach (var n in Vec<nint>(mgr + 0x10))
            World.Islands.Free.Add(Node(n));
        foreach (var n in Vec<nint>(mgr + 0x20))
            World.Islands.Serial.Add(Node(n));
        foreach (var n in Vec<nint>(mgr + 0x30))
            World.Islands.Coloured.Add(Node(n));
        foreach (var n in Vec<nint>(mgr + 0x40))
            World.Islands.SplitPending.Add((RnIsland)Node(n));
        World.Islands.Colouring = mgr[0x50] != 0;

        // Links are read once every object exists: a contact's constructor reads
        // its shapes, so reading heads while making shapes would make a contact twice.
        foreach (var (p, s) in _shapes.ToArray())
            for (var k = 0; k < 3; k++)
                s.Heads[k] = Link(*(nint*)((byte*)p + 0x80 + 8 * k));
        foreach (var (p, island) in _islands.ToArray())
            FillIsland((byte*)p, island);
        var filled = new HashSet<nint>();
        while (_contacts.Keys.FirstOrDefault(k => !filled.Contains(k)) is var next && next != 0)
        {
            filled.Add(next);
            FillContact((byte*)next, _contacts[next]);
        }
        foreach (var (p, b) in _bodies)
            FillMap((byte*)p, b);

        World.StepFlags = w[0x10c];
        World.Count688 = *(int*)(w + 0x688);
        World.StepCount = *(int*)(w + 0x1d4);
        World.Threads = *(int*)(w + 0x1ac);
        World.Priority = w[0x1b0];
        World.MaxCoordinate = *(float*)(w + 0x1b4);
        World.Time = *(float*)(w + 0x1cc);
        World.FrameTime = *(float*)(w + 0x1d0);
        World.Gravity = *(Vec3*)(w + 0x190);
        World.AirDensity = *(float*)(w + 0x1a0);
        World.PositionIterations = *(int*)(w + 0x1b8);
        World.VelocityIterations = *(int*)(w + 0x1bc);
        World.Sleeping = w[0x208] != 0;
        World.Continuous = *(int*)(w + 0x1a4) == 1 && *(int*)(w + 0x1c0) > 0;

        // Proxy fat boxes (FUN_1801f17e0): tree = id & 7 (0x58 each at bp +0x18), node = id >> 3 (0x30).
        var bp = *(byte**)(w + 0x110);
        foreach (var s in _shapes.Values)
        {
            if (s.ProxyId == -1)
                continue;
            var id = (uint)s.ProxyId;
            var node = *(byte**)(bp + (id & 7) * 0x58 + 0x18) + (id >> 3) * 0x30;
            s.FatMin = *(Vec3*)node;
            s.FatMax = *(Vec3*)(node + 0x10);
        }
    }

    private RnBody Body(nint p)
    {
        if (_bodies.TryGetValue(p, out var b))
            return b;
        var m = (byte*)p;
        b = new RnBody { Native = p, Index = *(int*)(m + 0x14), Static = m[0x44] != 0 };
        _bodies[p] = b;
        var state = default(RnBodyState);
        Buffer.MemoryCopy(m, &state, 0x260, 0x260);
        b.State = state;
        b.Proxy.Id = *(uint*)m;
        if (b.State.Controller != 0)
        {
            var t = (byte*)b.State.Controller;
            b.Target = new KinematicTarget { Orientation = *(Quat*)t, Position = *(Vec3*)(t + 0x18), Time = *(float*)(t + 0x24) };
        }
        b.Node.ManagerIndex = *(int*)(m + 0x38);
        b.Node.ListIndex = *(int*)(m + 0x3c);
        foreach (var sp in SmallVec(m + 0x60))
            b.Shapes.Add(Shape(sp, b));
        return b;
    }

    private RnShape Shape(nint p, RnBody body)
    {
        if (_shapes.TryGetValue(p, out var s))
            return s;
        var m = (byte*)p;
        s = new RnShape(body, (ulong)p)
        {
            Native = p,
            Type = *(int*)(m + 0x18),
            ProxyId = *(int*)(m + 0x1c),
            Material = *(ContactSolver.Material*)(m + 0x20),
            Reports = m[0xb0] != 0,
        };
        s.Attributes = *(CollisionAttributes*)(m + 0x50);
        s.Proxy.HasProxy = m[0xae] != 0;
        if (s.Type == 3)
            s.MeshMode = *(int*)(m + 0x108);
        _shapes[p] = s;
        World.ShapesByHandle[(ulong)p] = s;
        if (s.Type == 2)
            s.SetHull(new HullRef(Hull(*(byte**)(m + 0xc0)), *(float*)(m + 0xb8)));
        else if (s.Type == 3)
            s.SetMesh(Mesh(*(byte**)(m + 0xc8)), *(Vec3*)(m + 0xb8));
        return s;
    }

    private EdgeRef? Link(nint tagged)
        => tagged == 0 ? null : new EdgeRef(Contact(tagged & ~(nint)1), (int)(tagged & 1));

    private RnContact Contact(nint p)
    {
        if (_contacts.TryGetValue(p, out var c))
            return c;
        var m = (byte*)p;
        var a = *(byte**)(m + 8);
        var b = *(byte**)(m + 0x10);
        c = new RnContact(Shape((nint)a, Body(*(nint*)(a + 0x10))), Shape((nint)b, Body(*(nint*)(b + 0x10)))) { Native = p };
        _contacts[p] = c;
        return c;
    }

    private void FillContact(byte* m, RnContact c)
    {
        for (var h = 0; h < 2; h++)
        {
            c.Next[h] = Link(*(nint*)(m + 0x20 + 8 * h));
            c.Prev[h] = Link(*(nint*)(m + 0x30 + 8 * h));
        }
        c.ChildA = *(int*)(m + 0x18);
        c.ChildB = *(int*)(m + 0x1c);
        c.Key = *(ulong*)(m + 0x40);
        c.TouchState = *(int*)(m + 0x48);
        var island = *(nint*)(m + 0x50);
        c.Island = island == 0 ? null : (RnIsland)Node(island);
        c.IslandIndex = *(int*)(m + 0x5c);
        c.ColourIndex = *(int*)(m + 0x58);
        c.Colour = m[0x60];
        c.InIsland = m[0x61] != 0;
        c.ActiveIndex = *(int*)(m + 0x64);
        c.AllIndex = *(int*)(m + 0x68);
        c.SolverA = *(int*)(m + 0x6c);
        c.SolverB = *(int*)(m + 0x70);
        c.Flags74 = m[0x74];
        c.Flags78 = *(ushort*)(m + 0x78);
        c.Group = (sbyte)m[0x7a];
        c.Size88 = *(int*)(m + 0x88);
        c.SoftCap = *(float*)(m + 0x8c);
        c.Slop = *(float*)(m + 0x94);
        c.Size98 = *(int*)(m + 0x98);
        var block = *(byte**)(m + 0x80);
        if (block != null)
            for (var k = 0; k < *(int*)(block + 4); k++)
                c.Manifolds.Add(*(CachedManifold*)(block + 8 + 0xe0 * k));
        var vtable = *(nint*)m;
        if (vtable == Vphysics2Oracle.At(_module, ConvexContactVtable))
            c.Sat = *(SatCache*)(m + 0xd4);
        else if (vtable == Vphysics2Oracle.At(_module, MeshContactVtable))
        {
            var mesh = new MeshContactState
            {
                BoxMin = *(Vec3*)(m + 0xa8),
                BoxMax = *(Vec3*)(m + 0xb4),
                Triangles = [.. Vec<int>(m + 0xc0)],
                Caches = [.. Vec<MeshTriangleCache>(m + 0xd8)],
                Manifolds = [.. c.Manifolds],
                SizeEstimate = c.Size98,
            };
            c.Mesh = mesh;
        }
    }

    private IslandNode Node(nint p)
    {
        if (_islands.TryGetValue(p, out var i))
            return i;
        var m = (byte*)p;
        if (*(int*)(m + 8) == 0)
            return Body(p - 0x38).Node;
        i = new RnIsland { Native = p };
        _islands[p] = i;
        return i;
    }

    private void FillIsland(byte* m, RnIsland i)
    {
        i.ManagerIndex = *(int*)m;
        i.ListIndex = *(int*)(m + 4);
        var count = *(int*)(m + 0x10);
        var bodies = *(nint**)(m + 0x18);
        for (var k = 0; k < count; k++)
            i.Bodies.Add(bodies[k] == 0 ? null : Body(bodies[k]));
        for (var g = 0; g < 2; g++)
            foreach (var c in SmallVec(m + 0x58 + 0x10 * g))
                i.Contacts[g].Add(Contact(c));
        i.EdgeCount = *(int*)(m + 0x78);
        i.AwakeCount = *(int*)(m + 0x7c);
        i.VelocityIterations = *(int*)(m + 0x80);
        i.PositionIterations = *(int*)(m + 0x84);
        i.SplitIndex = *(int*)(m + 0x88);
        i.Flags = *(int*)(m + 0x8c);
        i.Sizes[0] = *(int*)(m + 0x98);
        i.Sizes[1] = *(int*)(m + 0x9c);
        // The colouring: 17 buckets of 0x50, plain vectors of contacts at +0x20 / +0x30, sizes at +0x48 / +0x4c.
        var colouring = *(byte**)(m + 0xa0);
        if (colouring != null)
        {
            i.Colouring = new IslandColouring();
            for (var b = 0; b < 17; b++)
            {
                var bucket = colouring + 0x50 * b;
                if (*(int*)bucket != 0 || *(int*)(bucket + 0x10) != 0)
                    throw new NotSupportedException("a coloured island with joints or type-1 constraints");
                for (var g = 0; g < 2; g++)
                {
                    foreach (var c in Vec<nint>(bucket + 0x20 + 0x10 * g))
                        i.Colouring.Contacts[b, g].Add(Contact(c));
                    i.Colouring.Sizes[b, g] = *(int*)(bucket + 0x48 + 4 * g);
                }
            }
        }
    }

    private void FillMap(byte* m, RnBody b)
    {
        b.MapState = m[0x45];
        if (b.MapState == 1)
        {
            b.MapIsland = (RnIsland)Node(*(nint*)(m + 0x20));
            b.MapIndex = *(int*)(m + 0x28);
            b.MapRefs = *(int*)(m + 0x2c);
        }
        else if (b.MapState == 2)
        {
            // Rebuilt from the islands: the solver index and the number of edges there.
            b.MapHash = [];
            foreach (var island in _islands.Values)
            {
                var index = island.Bodies.IndexOf(b);
                if (index < 0)
                    continue;
                var refs = island.Contacts[0].Concat(island.Contacts[1]).Count(c => c.A.Body == b || c.B.Body == b);
                b.MapHash[island] = (index, refs);
            }
        }
    }

    private RnHull Hull(byte* p)
    {
        if (_hulls.TryGetValue((nint)p, out var h))
            return h;
        h = new RnHull
        {
            Centroid = *(Vector3*)p,
            MaxAngularRadius = *(float*)(p + 0xc),
            MinCentroidRadius = *(float*)(p + 0x10),
            BoundsMin = *(Vector3*)(p + 0x14),
            BoundsMax = *(Vector3*)(p + 0x20),
            VertexPositions = Vec<Vector3>(p + 0x70),
            Planes = Vec<Vector4>(p + 0x88).Select(v => (new Vector3(v.X, v.Y, v.Z), v.W)).ToArray(),
            Vertices = Vec<byte>(p + 0xb0),
            Edges = Vec<uint>(p + 0xc8).Select(e => ((byte)e, (byte)(e >> 8), (byte)(e >> 16), (byte)(e >> 24))).ToArray(),
            Faces = Vec<byte>(p + 0xe0),
            Flags = *(uint*)(p + 0xa0),
        };
        _hulls[(nint)p] = h;
        return h;
    }

    private RnMesh Mesh(byte* p)
    {
        if (_meshes.TryGetValue((nint)p, out var mesh))
            return mesh;
        var tri = new (int, int, int)[*(int*)(p + 0x48)];
        var data = *(int**)(p + 0x50);
        for (var i = 0; i < tri.Length; i++)
            tri[i] = (data[3 * i], data[3 * i + 1], data[3 * i + 2]);
        mesh = new RnMesh
        {
            Min = *(Vector3*)p,
            Max = *(Vector3*)(p + 0xc),
            Nodes = Vec<RnMesh.Node>(p + 0x18),
            Vertices = Vec<Vector3>(p + 0x30),
            Triangles = tri,
        };
        _meshes[(nint)p] = mesh;
        return mesh;
    }
}
