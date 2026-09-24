using Source2.Compiler.Physics;

namespace Source2.Compiler.Simulation;

/// <summary>
/// Something the island manager keeps in its lists: a body's own single-body
/// node (kind 0, embedded at CRnBody +0x38) or a heap island (kind 1, 0xb8
/// bytes).
/// </summary>
public abstract class IslandNode
{
    /// <summary>Index in the manager's list of all nodes (node +0x00).</summary>
    public int ManagerIndex = -1;

    /// <summary>Index in the solve list it sits in (node +0x04), -1 for none.</summary>
    public int ListIndex = -1;

    /// <summary>Node +0x08: 0 body node, 1 island.</summary>
    public abstract int Kind { get; }

    /// <summary>Whether the node has something awake to solve.</summary>
    public abstract bool Awake { get; }
}

/// <summary>A body's own node, used while the body is in no heap island.</summary>
public sealed class BodyNode(RnBody body) : IslandNode
{
    public readonly RnBody Body = body;

    public override int Kind => 0;

    /// <summary>The body is in the world's awake list (body +0x18 &gt;= 0).</summary>
    public override bool Awake => Body.ActiveIndex >= 0;
}

/// <summary>
/// A heap island (FUN_1802cdc70): its bodies (a removed body leaves a null
/// until the next split), its contacts by group, the counts and the iteration
/// maxima the solve uses.
/// </summary>
public sealed class RnIsland : IslandNode
{
    public override int Kind => 1;

    public override bool Awake => AwakeCount > 0;

    /// <summary>+0x10/+0x18: solver body index = position here.</summary>
    public List<RnBody?> Bodies = [];

    /// <summary>+0x58 (contact +0x7a == 0) and +0x68 (== 1).</summary>
    public readonly List<RnContact>[] Contacts = [[], []];

    /// <summary>+0x78 edges, +0x7c awake bodies.</summary>
    public int EdgeCount, AwakeCount;

    /// <summary>+0x80 / +0x84, the maxima of the bodies' minimum iterations.</summary>
    public int VelocityIterations, PositionIterations;

    /// <summary>+0x88: index in the manager's pending-split list.</summary>
    public int SplitIndex = -1;

    /// <summary>+0x8c: bit 0 split pending, bit 1 recompute the iteration maxima.</summary>
    public int Flags;

    /// <summary>+0x98 / +0x9c: the contacts' solver size estimates, per group.</summary>
    public readonly int[] Sizes = new int[2];

    /// <summary>+0xa0 != 0: the island has a graph colouring (solve list C).</summary>
    public bool Coloured;

    /// <summary>The island's address when read from a live world, for tests.</summary>
    public nint Native;
}

/// <summary>An intrusive list link: a contact and which of its halves (0 shape A, 1 shape B).</summary>
public readonly record struct EdgeRef(RnContact Contact, int Half);

/// <summary>
/// A collision shape of a body (the fields the world step reads). What the
/// broadphase reads (type, proxy, collision attributes, bounds) lives in its
/// <see cref="BroadphaseShape"/>, whose handle names the shape in the pair set.
/// </summary>
public sealed class RnShape
{
    public readonly RnBody Body;

    public readonly BroadphaseShape Proxy;

    public RnShape(RnBody body, ulong handle)
    {
        Body = body;
        Proxy = new BroadphaseShape { Body = body.Proxy, Handle = handle };
        body.Proxy.Shapes.Add(Proxy);
    }

    /// <summary>+0x18: 0 sphere, 1 capsule, 2 hull, 3 mesh, 4 compound.</summary>
    public int Type
    {
        get => Proxy.Type;
        set => Proxy.Type = value;
    }

    /// <summary>+0x1c: the broadphase proxy (tree in bits 0..2, leaf above).</summary>
    public int ProxyId
    {
        get => Proxy.ProxyId;
        set => Proxy.ProxyId = value;
    }

    /// <summary>+0x50: the collision attributes.</summary>
    public ref CollisionAttributes Attributes => ref Proxy.Attributes;

    public HullRef Hull;
    public RnMesh? Mesh;
    public Vec3 MeshScale = new(1, 1, 1);

    /// <summary>+0x20: the surface material.</summary>
    public ContactSolver.Material Material;

    /// <summary>+0x80/+0x88/+0x90: the contact lists by touch state (0 touching, 1 not).</summary>
    public readonly EdgeRef?[] Heads = new EdgeRef?[3];

    /// <summary>+0x108: a mesh shape's mode; anything but 3 gets per-step work on a moving body (not ported).</summary>
    public int MeshMode = 3;

    /// <summary>+0xb0: the shape reports contacts to a listener (FUN_1801fc050, not ported).</summary>
    public bool Reports;

    /// <summary>The proxy's fat box, for a world read without its broadphase.</summary>
    public Vec3 FatMin, FatMax;

    public nint Native;

    /// <summary>A hull at a uniform scale (+0xc0, +0xb8); the broadphase boxes its bounds.</summary>
    public void SetHull(HullRef hull)
    {
        Type = BroadphaseShape.HullType;
        Hull = hull;
        Proxy.Scale = new Vec3(hull.Scale, hull.Scale, hull.Scale);
        Proxy.LocalMin = new Vec3(hull.Hull.BoundsMin.X, hull.Hull.BoundsMin.Y, hull.Hull.BoundsMin.Z);
        Proxy.LocalMax = new Vec3(hull.Hull.BoundsMax.X, hull.Hull.BoundsMax.Y, hull.Hull.BoundsMax.Z);
    }

    /// <summary>A triangle mesh at a per-axis scale (+0xc8, +0xb8).</summary>
    public void SetMesh(RnMesh mesh, Vec3 scale)
    {
        Type = BroadphaseShape.MeshType;
        Mesh = mesh;
        MeshScale = scale;
        Proxy.Scale = scale;
        Proxy.LocalMin = new Vec3(mesh.Min.X, mesh.Min.Y, mesh.Min.Z);
        Proxy.LocalMax = new Vec3(mesh.Max.X, mesh.Max.Y, mesh.Max.Z);
        Proxy.Vertices = mesh.Vertices.Select(v => new Vec3(v.X, v.Y, v.Z)).ToArray();
    }
}

/// <summary>A contact (pair) between two shapes (CRnContact and its subclasses).</summary>
public sealed class RnContact(RnShape a, RnShape b)
{
    public readonly RnShape A = a, B = b;

    /// <summary>+0x18 / +0x1c: the compound child of shape A / B, -1 for none.</summary>
    public int ChildA = -1, ChildB = -1;

    /// <summary>+0x40: (proxy A &lt;&lt; 32) | proxy B, the flush sort key.</summary>
    public ulong Key;

    /// <summary>+0x48: the touch state and list the contact hangs in on its shapes (0 touching).</summary>
    public int TouchState = 1;

    /// <summary>+0x50 / +0x5c: the island and the index in its contact group.</summary>
    public RnIsland? Island;
    public int IslandIndex = -1;

    /// <summary>+0x61: the contact is an edge of an island.</summary>
    public bool InIsland;

    /// <summary>+0x64: index in the world's active contacts (+0xa10); +0x68 in its all-contacts list.</summary>
    public int ActiveIndex = -1, AllIndex = -1;

    /// <summary>+0x6c / +0x70: the bodies' solver indices in the island.</summary>
    public int SolverA, SolverB;

    /// <summary>+0x74 flags (bit 1 touch events, bit 2 ...), +0x78 flags (bit 0 generates contacts).</summary>
    public byte Flags74;
    public ushort Flags78;

    /// <summary>+0x7a: 0 when both bodies are dynamic, else 1.</summary>
    public int Group;

    /// <summary>+0x88 the size estimate the island counted, +0x98 the current one.</summary>
    public int Size88, Size98;

    /// <summary>+0x8c / +0x94: the solver's bias clamp and margin.</summary>
    public float SoftCap, Slop;

    /// <summary>The manifold block at +0x80 (empty for none).</summary>
    public List<CachedManifold> Manifolds = [];

    /// <summary>A convex contact's SAT cache (+0xd4).</summary>
    public SatCache Sat;

    /// <summary>A mesh contact's state.</summary>
    public MeshContactState? Mesh;

    /// <summary>Links in the shapes' lists, per half: +0x20/+0x28 next, +0x30/+0x38 prev.</summary>
    public readonly EdgeRef?[] Next = new EdgeRef?[2], Prev = new EdgeRef?[2];

    public nint Native;

    public RnShape Shape(int half) => half == 0 ? A : B;
}

/// <summary>
/// A rigid body with the island bookkeeping around it: the state vphysics2
/// keeps in CRnBody, its shapes, its own island node, and the island map
/// (body +0x20): the island(s) it is in with its solver index and edge count
/// in each (a static body can be in many; the others in one).
/// </summary>
public sealed class RnBody
{
    /// <summary>What the broadphase reads of the body; it holds the body's state.</summary>
    public readonly BroadphaseBody Proxy = new();

    /// <summary>The CRnBody state, in Valve's layout.</summary>
    public ref RnBodyState State => ref Proxy.State;

    /// <summary>+0x14: index in the world's list of all bodies, the solve lists' sort key.</summary>
    public int Index;

    public readonly List<RnShape> Shapes = [];

    /// <summary>A kinematic body's controller (+0x80), null without one.</summary>
    public KinematicTarget? Target;

    /// <summary>+0x44: the node's static flag (never merged into an island).</summary>
    public bool Static;

    public readonly BodyNode Node;

    /// <summary>
    /// The island map (FUN_1802ce2d0): state 0 empty, 1 one inline entry, 2 a
    /// hash map (a static body in several islands). The hash map's order is
    /// never walked by the step, so a dictionary stands in for it.
    /// </summary>
    public byte MapState;
    public RnIsland? MapIsland;
    public int MapIndex = -1, MapRefs;
    public Dictionary<RnIsland, (int Index, int Refs)>? MapHash;

    public nint Native;

    public RnBody() => Node = new BodyNode(this);

    public int ActiveIndex
    {
        get => State.ActiveIndex;
        set => State.ActiveIndex = value;
    }

    /// <summary>The island of a body in one island (body +0x20).</summary>
    public RnIsland? Island => MapState == 1 ? MapIsland : null;
}

/// <summary>The island manager (RnWorld +0x118).</summary>
public sealed class IslandManager
{
    /// <summary>+0x00: every node.</summary>
    public readonly List<IslandNode> All = [];

    /// <summary>+0x10 (A, body nodes), +0x20 (B, serial islands), +0x30 (C, coloured islands).</summary>
    public readonly List<IslandNode> Free = [], Serial = [], Coloured = [];

    /// <summary>+0x40: islands that lost an edge and wait for a split.</summary>
    public readonly List<RnIsland> SplitPending = [];

    /// <summary>+0x50: whether big islands get a graph colouring (threshold 25, else never).</summary>
    public bool Colouring;
}

/// <summary>Where <see cref="RnWorld.Observe"/> is called.</summary>
public enum StepPhase
{
    /// <summary>After the new contacts, before the collide worker.</summary>
    BeforeCollide,

    /// <summary>After the collide worker, before its post-pass (FUN_1801dceb0).</summary>
    Collided,
}

/// <summary>
/// A Rubikon world (CRnWorld): its bodies and contacts, the island manager,
/// the broadphase, and the step (FUN_180200840) that drives them.
/// </summary>
public sealed class RnWorld
{
    public readonly List<RnBody> Bodies = [];

    /// <summary>+0xa00: the awake bodies (body +0x18 is the index).</summary>
    public readonly List<RnBody> ActiveBodies = [];

    /// <summary>+0xa10: the contacts the collide pass runs (contact +0x64).</summary>
    public readonly List<RnContact> ActiveContacts = [];

    /// <summary>+0x6d8 (solid) / +0x6e8 (sensor): every contact (contact +0x68).</summary>
    public readonly List<RnContact>[] AllContacts = [[], []];

    /// <summary>+0x758: contacts destroyed and not yet freed (<see cref="FreeDestroyed"/>).</summary>
    public readonly List<RnContact> Destroyed = [];

    public readonly IslandManager Islands = new();

    /// <summary>
    /// Bodies that moved far enough for continuous collision (FUN_1801b8f40),
    /// filled by the Solve pass: +0xa78 dynamic, +0xab0 dynamic with body
    /// +0x24b == 1, +0xae8 kinematic. The first two keep the order Valve's
    /// threads push in; the third is sorted by body index.
    /// </summary>
    public readonly List<RnBody>[] ContinuousBodies = [[], [], []];

    /// <summary>+0xa58: bodies whose applied forces may wake them at the next step (WakeBodiesFromAppliedForces).</summary>
    public readonly List<RnBody> ForcedBodies = [];

    /// <summary>+0x688: the count of a world list that, like awake bodies, marks a Solve as moving (+0x10c bit 1).</summary>
    public int Count688;

    /// <summary>+0x10c: bit 0 stepped, bit 1 something moved or an island was split.</summary>
    public int StepFlags;

    /// <summary>+0x1d4: steps taken; splits run when it is a multiple of 10.</summary>
    public int StepCount;

    /// <summary>+0x1cc: simulated time; +0x1d0 the time at the frame's first step.</summary>
    public float Time, FrameTime;

    /// <summary>+0x1ac: the thread setting; above 1 the flushed lists are sorted.</summary>
    public int Threads = int.MaxValue;

    /// <summary>+0x1b0: the job priority the passes pass on.</summary>
    public byte Priority = 2;

    /// <summary>Whether the flushed lists are sorted (by key or body index).</summary>
    public bool SortLists => Threads > 1;

    public Vec3 Gravity = new(-0f, -0f, -360f);
    public float AirDensity = 1.2f;
    public int VelocityIterations = 8, PositionIterations = 2;

    /// <summary>+0x208: bodies may fall asleep.</summary>
    public bool Sleeping = true;

    /// <summary>ctx +0x45: world +0x1a4 == 1 and +0x1c0 &gt; 0, continuous collision on.</summary>
    public bool Continuous = true;

    /// <summary>+0x1c0: the passes of the TOI solve (FUN_1802001b0).</summary>
    public int ContinuousPasses = 4;

    /// <summary>+0x1b4: the largest coordinate (g_flConfigMaxCoord); the world bounds check keeps inside it.</summary>
    public float MaxCoordinate = 16384f;

    /// <summary>+0x110: the broadphase; null for a world read without it (the passes then use <see cref="RnShape.FatMin"/>).</summary>
    public Broadphase? Broadphase;

    /// <summary>The scratch's hierarchy update (+0) and pair query (+0x4e8).</summary>
    public readonly HierarchyUpdate Hierarchy = new();
    public readonly PairQuery Query = new();

    /// <summary>Shapes by broadphase handle, for the contacts the pair query makes.</summary>
    public readonly Dictionary<ulong, RnShape> ShapesByHandle = [];

    // Passes of CRnWorld::Step that live elsewhere: set, they replace the
    // step's own (StepGlue's applied-force wake and bounds check); the TOI
    // solve has none yet, so unset the step throws when it would run.

    /// <summary>Called at points inside <see cref="Step"/>, for tests and tools that follow a step.</summary>
    public Action<RnWorld, StepPhase>? Observe;

    /// <summary>FUN_180203d70, CRnWorld::WakeBodiesFromAppliedForces; unset, <see cref="StepGlue.WakeBodiesFromAppliedForces"/>.</summary>
    public Action<RnWorld>? WakeBodiesFromAppliedForces;

    /// <summary>The TOI solve of FUN_1801ffe80 (FUN_1802001b0 over +0xa78, then +0xab0), after <see cref="WorldSolver.GatherContinuous"/>.</summary>
    public Action<RnWorld, float>? SolveContinuous;

    /// <summary>FUN_1801faf80, the world bounds check; unset, <see cref="StepGlue.ShapesOutsideBounds"/>.</summary>
    public Action<RnWorld>? ClampToWorldBounds;

    private ulong _nextHandle = 0x10;

    /// <summary>
    /// A new body (world vfn 0x1e8): the next index, its node in the island
    /// manager. The state is the caller's: Valve's RnBodyDesc_t defaults and
    /// the mass update (FUN_1801c0880) are not ported, so a body arrives with
    /// its mass properties already in its state.
    /// </summary>
    public RnBody AddBody(in RnBodyState state)
    {
        var b = new RnBody { Index = Bodies.Count };
        b.State = state;
        b.State.ActiveIndex = -1;
        b.Static = state.BodyType == 0;
        Bodies.Add(b);
        IslandManagerOps.AddNode(Islands, b.Node);
        return b;
    }

    /// <summary>
    /// A hull (body vfn 0x68) or mesh (0x70) shape joins the body with its
    /// collision attributes and material; an enabled body gives it a proxy
    /// at its current frame (FUN_1801b9650). <paramref name="handle"/> names
    /// the shape in the pair set; by default the world numbers its shapes.
    /// </summary>
    public RnShape AddShape(RnBody body, int type, HullRef hull, RnMesh? mesh, Vec3 meshScale,
                            in CollisionAttributes attributes, in ContactSolver.Material material, ulong? handle = null)
    {
        var s = new RnShape(body, handle ?? _nextHandle);
        _nextHandle = Math.Max(_nextHandle, s.Proxy.Handle) + 0x10;
        if (type == BroadphaseShape.HullType)
            s.SetHull(hull);
        else if (type == BroadphaseShape.MeshType)
            s.SetMesh(mesh!, meshScale);
        else
            throw new NotSupportedException($"shape type {type}");
        s.Attributes = attributes;
        s.Material = material;
        body.Shapes.Add(s);
        ShapesByHandle[s.Proxy.Handle] = s;
        Broadphase?.AddShape(s.Proxy);
        return s;
    }

    /// <summary>
    /// The part of SetType(2) (FUN_1801bd3e0) a body without contacts needs:
    /// its node leaves the manager and joins again at the end (FUN_1802ccbb0,
    /// FUN_1802ce270), and its proxies move to the dynamic tree
    /// (FUN_1802d7560). The mass update it also runs is not ported: the
    /// caller sets the body's mass properties.
    /// </summary>
    public void MakeDynamic(RnBody body)
    {
        if (body.Shapes.Any(s => s.Heads.Any(h => h != null)))
            throw new NotSupportedException("SetType on a body with contacts");
        IslandManagerOps.RemoveNode(Islands, body.Node);
        body.State.BodyType = 2;
        body.Static = false;
        foreach (var s in body.Shapes)
            Broadphase?.ReselectTree(s.Proxy);
        IslandManagerOps.AddNode(Islands, body.Node);
    }

    /// <summary>
    /// CRnWorld::Step (FUN_180200840). A plain world has no step callbacks,
    /// controllers, joints to break or soft bodies, so those parts of the
    /// step are empty here.
    /// </summary>
    public void Step(float dt, bool first)
    {
        if (!(dt > 1e-6f))
            return;
        if (first)
            FrameTime = Time;
        if (WakeBodiesFromAppliedForces != null)
            WakeBodiesFromAppliedForces(this);
        else
            StepGlue.WakeBodiesFromAppliedForces(this, ForcedBodies);
        StepCount++;
        Time = dt + Time;
        var broadphase = Broadphase ?? throw new InvalidOperationException("stepping needs the broadphase");
        broadphase.PreStepQuery(Query, Threads, Priority);
        BuildNewContacts();
        Observe?.Invoke(this, StepPhase.BeforeCollide);
        var collide = new CollideLists();
        ContactLifecycle.CollideContacts(this, collide);
        Observe?.Invoke(this, StepPhase.Collided);
        ContactLifecycle.Flush(this, collide);
        WorldSolver.Solve(this, dt, first, new SolveLists());
        BuildNewContacts();
        WorldSolver.GatherContinuous(this);
        (SolveContinuous ?? ContinuousSolve.Run)(this, dt);
        if (ClampToWorldBounds != null)
            ClampToWorldBounds(this);
        else
            StepGlue.ShapesOutsideBounds(broadphase, MaxCoordinate);
        StepFlags |= 1;
    }

    /// <summary>
    /// CRnWorld::BuildNewContactsFromOverlappingPairQuery (FUN_1801f1dc0): the
    /// pairs the query found, in key order, that entered the pair set become
    /// contacts: linked at the front of both shapes' not-touching lists and
    /// appended to the all-contacts list and the active contacts.
    /// </summary>
    public void BuildNewContacts()
    {
        foreach (var found in Broadphase!.BuildNewContacts(Query))
        {
            var c = CreateContact(ShapesByHandle[found.A.Handle], ShapesByHandle[found.B.Handle], found.SubA, found.SubB, found.Flags);
            for (var h = 0; h < 2; h++)
            {
                var shape = c.Shape(h);
                if (shape.Heads[c.TouchState] is { } head)
                {
                    c.Next[h] = head;
                    head.Contact.Prev[head.Half] = new EdgeRef(c, h);
                }
                shape.Heads[c.TouchState] = new EdgeRef(c, h);
            }
            var all = AllContacts[(c.Flags78 & 1) == 0 ? 1 : 0];
            c.AllIndex = all.Count;
            all.Add(c);
            ContactLifecycle.Reactivate(this, c);
        }
    }

    /// <summary>
    /// A contact as FUN_1801d1390 builds it (the pair ctor FUN_180306710, the
    /// base FUN_1801d1170, then the convex FUN_180306900 or mesh FUN_1802fd130
    /// ctor): not touching, in no list, group 0 only between two dynamic
    /// bodies, and a slop of 1/16 for each hull or mesh side.
    /// </summary>
    private static RnContact CreateContact(RnShape a, RnShape b, int subA, int subB, ushort flags)
    {
        var c = new RnContact(a, b)
        {
            ChildA = subA,
            ChildB = subB,
            SolverA = -1,
            SolverB = -1,
            Key = (ulong)(uint)a.ProxyId << 32 | (uint)b.ProxyId,
            Flags78 = flags,
            Flags74 = (byte)((flags & 0x1c) != 0 ? 2 : 0),
            Group = a.Body.State.BodyType == 2 && b.Body.State.BodyType == 2 ? 0 : 1,
        };
        if (a.Type is 2 or 3)
            c.Slop += 0.0625f;
        if (b.Type is 2 or 3)
            c.Slop += 0.0625f;
        if (a.Type == 4 || b.Type == 4)
            throw new NotSupportedException("compound shapes");
        if (b.Type == BroadphaseShape.MeshType)
            c.Mesh = new MeshContactState();
        return c;
    }

    /// <summary>
    /// FUN_1801f8a10's part for contacts: the destroyed contacts are freed.
    /// Valve does it outside the step (world vfn 0x28 at the end of a
    /// simulation, and when the world is emptied); nothing reads them.
    /// </summary>
    public void FreeDestroyed() => Destroyed.Clear();
}
