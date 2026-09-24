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

/// <summary>A collision shape of a body (the fields the world step reads).</summary>
public sealed class RnShape(RnBody body)
{
    public readonly RnBody Body = body;

    /// <summary>+0x18: 0 sphere, 1 capsule, 2 hull, 3 mesh, 4 compound.</summary>
    public int Type;

    /// <summary>+0x1c: the broadphase proxy (tree in bits 0..2, leaf above).</summary>
    public int ProxyId = -1;

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

    /// <summary>The proxy's fat box in the broadphase.</summary>
    public Vec3 FatMin, FatMax;

    public nint Native;
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
    public RnBodyState State;

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

/// <summary>A Rubikon world as the step's bookkeeping sees it.</summary>
public sealed class RnWorld
{
    public readonly List<RnBody> Bodies = [];

    /// <summary>+0xa00: the awake bodies (body +0x18 is the index).</summary>
    public readonly List<RnBody> ActiveBodies = [];

    /// <summary>+0xa10: the contacts the collide pass runs (contact +0x64).</summary>
    public readonly List<RnContact> ActiveContacts = [];

    /// <summary>+0x6d8 (solid) / +0x6e8 (sensor): every contact (contact +0x68).</summary>
    public readonly List<RnContact>[] AllContacts = [[], []];

    /// <summary>+0x758: contacts destroyed this step, freed later.</summary>
    public readonly List<RnContact> Destroyed = [];

    public readonly IslandManager Islands = new();

    /// <summary>
    /// Bodies that moved far enough for continuous collision (FUN_1801b8f40),
    /// filled by the Solve pass: +0xa78 dynamic, +0xab0 dynamic with body
    /// +0x24b == 1, +0xae8 kinematic. The first two keep the order Valve's
    /// threads push in; the third is sorted by body index.
    /// </summary>
    public readonly List<RnBody>[] ContinuousBodies = [[], [], []];

    /// <summary>+0x688: the count of a world list that, like awake bodies, marks a Solve as moving (+0x10c bit 1).</summary>
    public int Count688;

    /// <summary>+0x10c: bit 0 stepped, bit 1 an island was split this step.</summary>
    public int StepFlags;

    /// <summary>+0x1d4: steps taken; splits run when it is a multiple of 10.</summary>
    public int StepCount;

    /// <summary>+0x1ac &gt; 1: the flushed lists are sorted (by key or body index).</summary>
    public bool SortLists = true;

    public Vec3 Gravity = new(-0f, -0f, -360f);
    public float AirDensity = 1.2f;
    public int VelocityIterations = 8, PositionIterations = 2;

    /// <summary>+0x208: bodies may fall asleep.</summary>
    public bool Sleeping = true;

    /// <summary>ctx +0x45: world +0x1a4 == 1 and +0x1c0 &gt; 0, continuous collision on.</summary>
    public bool Continuous = true;
}
