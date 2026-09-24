using System.Runtime.InteropServices;

namespace Source2.Compiler.Simulation;

/// <summary>
/// A shape's collision attributes (CRnShape+0x50, 0x28 bytes). Names are by
/// use in the broadphase: the pair filter (FUN_1802faf00), the tree choice
/// (FUN_1802d59b0) and the body veto (FUN_1801bdea0).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x28)]
public struct CollisionAttributes
{
    /// <summary>+0x50: interaction layers this shape is.</summary>
    [FieldOffset(0x00)] public ulong InteractsAs;

    /// <summary>+0x58: layers it interacts with.</summary>
    [FieldOffset(0x08)] public ulong InteractsWith;

    /// <summary>+0x60: layers it never interacts with.</summary>
    [FieldOffset(0x10)] public ulong InteractsExclude;

    /// <summary>+0x68: the owning body's id (written when the shape joins a body).</summary>
    [FieldOffset(0x18)] public int EntityId;

    /// <summary>+0x6C: an id whose shapes it ignores.</summary>
    [FieldOffset(0x1c)] public int OwnerId;

    /// <summary>+0x70: a hierarchy id; 0 and 0xFFFF mean none.</summary>
    [FieldOffset(0x20)] public ushort HierarchyId;

    /// <summary>+0x72: the collision function mask, inverted unless <see cref="MaskIsDirect"/> is 1.</summary>
    [FieldOffset(0x22)] public ushort FunctionMask;

    [FieldOffset(0x24)] public byte MaskIsDirect;

    /// <summary>+0x75: the bit this shape tests in the other's function mask.</summary>
    [FieldOffset(0x25)] public byte FunctionIndex;

    /// <summary>+0x76: the collision group (row and column of the 64x64 group table).</summary>
    [FieldOffset(0x26)] public byte Group;

    /// <summary>+0x77: flag bits (1, 2, 4, 0x10, 0x20, 0x40, 0x48, 0x80 are read).</summary>
    [FieldOffset(0x27)] public byte Flags;
}

/// <summary>What the broadphase reads of a body (CRnBody): its state, id and shapes.</summary>
public sealed class BroadphaseBody
{
    /// <summary>The body's simulated state, in Valve's layout.</summary>
    public RnBodyState State;

    /// <summary>CRnBody+0: the id disabled-pair keys use.</summary>
    public uint Id;

    public readonly List<BroadphaseShape> Shapes = [];

    /// <summary>
    /// The body's joints for the veto (CRnBody+0x70 walk): the body at the
    /// other end and the joint's flag word (+0x28). A joint whose bits 0 and
    /// 1 are both clear keeps the two bodies from colliding.
    /// </summary>
    public readonly List<(BroadphaseBody Other, ushort Flags)> Joints = [];

    /// <summary>The CRnBody+0x240 records that are live and point at another body: no contact with it.</summary>
    public readonly List<BroadphaseBody> NoCollide = [];
}

/// <summary>
/// A convex hull or triangle mesh shape as the broadphase sees it: the body
/// it is on, its type, proxy id, collision attributes, and what its AABB is
/// computed from (shape vfn 0x80).
/// </summary>
public sealed class BroadphaseShape
{
    public const int HullType = 2;
    public const int MeshType = 3;

    /// <summary>The shape's identity in the tree and the pair set (a CRnShape pointer in Valve's world).</summary>
    public ulong Handle;

    public required BroadphaseBody Body;

    /// <summary>CRnShape+0x18: 2 hull, 3 mesh (4, compound, is not supported).</summary>
    public int Type;

    /// <summary>CRnShape+0x1C: tree leaf * 8 | tree, or -1.</summary>
    public int ProxyId = -1;

    public CollisionAttributes Attributes;

    /// <summary>CRnShape+0xAE: whether the shape gets a proxy at all.</summary>
    public bool HasProxy = true;

    /// <summary>The hull's bounds (RnHull_t+0x14 / +0x20) or the mesh's (RnMesh_t+0 / +0xC).</summary>
    public Vec3 LocalMin, LocalMax;

    /// <summary>The mesh's vertices (RnMesh_t+0x30); a static body's mesh box is taken over them.</summary>
    public Vec3[] Vertices = [];

    /// <summary>A hull's uniform scale (+0xB8) as (s, s, s), or a mesh's per-axis scale.</summary>
    public Vec3 Scale = new(1f, 1f, 1f);

    /// <summary>
    /// Shape vfn 0x80, the fat-less box at a transform, grown by 1/16 each
    /// way: FUN_180250100 for a hull, FUN_180241b10 for a mesh.
    /// </summary>
    public Aabb ComputeAabb(in RnTransform xf) => Type == HullType ? HullAabb(xf) : MeshAabb(xf);

    /// <summary>FUN_180250100: the scaled bounds' centre and half extents through the frame.</summary>
    private Aabb HullAabb(in RnTransform xf)
    {
        var s = Scale.X;
        var min = new Vec3(LocalMin.X * s, LocalMin.Y * s, LocalMin.Z * s);
        var max = new Vec3(LocalMax.X * s, LocalMax.Y * s, LocalMax.Z * s);
        return Pad(Oriented(xf, min, max));
    }

    /// <summary>
    /// FUN_180241b10: an identity rotation (m0 + m4 + m8 == 3) offsets the
    /// scaled bounds; a static body's mesh is boxed vertex by vertex;
    /// otherwise the scaled bounds are turned like a hull's.
    /// </summary>
    private Aabb MeshAabb(in RnTransform xf)
    {
        ref readonly var r = ref xf.R;
        if ((r.M0 + r.M4) + r.M8 == 3f)
        {
            var box = new Aabb(
                new(Scale.X * LocalMin.X + xf.T.X, Scale.Y * LocalMin.Y + xf.T.Y, Scale.Z * LocalMin.Z + xf.T.Z),
                new(Scale.X * LocalMax.X + xf.T.X, Scale.Y * LocalMax.Y + xf.T.Y, Scale.Z * LocalMax.Z + xf.T.Z));
            return Pad(box);
        }
        if (Body.State.BodyType == 0)
        {
            var box = EmptyBox;
            foreach (var v in Vertices)
            {
                var px = v.X * Scale.X;
                var py = v.Y * Scale.Y;
                var pz = v.Z * Scale.Z;
                var wx = ((px * r.M0) + (py * r.M3)) + (pz * r.M6) + xf.T.X;
                var wy = ((py * r.M4) + (px * r.M1)) + (pz * r.M7) + xf.T.Y;
                var wz = ((px * r.M2) + (py * r.M5)) + (pz * r.M8) + xf.T.Z;
                box.Min = new(MinSs(box.Min.X, wx), MinSs(box.Min.Y, wy), MinSs(box.Min.Z, wz));
                box.Max = new(MaxSs(box.Max.X, wx), MaxSs(box.Max.Y, wy), MaxSs(box.Max.Z, wz));
            }
            return Pad(box);
        }
        var min = new Vec3(Scale.X * LocalMin.X, Scale.Y * LocalMin.Y, Scale.Z * LocalMin.Z);
        var max = new Vec3(Scale.X * LocalMax.X, Scale.Y * LocalMax.Y, Scale.Z * LocalMax.Z);
        return Pad(Oriented(xf, min, max));
    }

    /// <summary>The box both shapes' oriented path computes: centre through the frame, half extents through |R|.</summary>
    private static Aabb Oriented(in RnTransform xf, Vec3 min, Vec3 max)
    {
        ref readonly var r = ref xf.R;
        var cx = (max.X + min.X) * 0.5f;
        var cy = (max.Y + min.Y) * 0.5f;
        var cz = (max.Z + min.Z) * 0.5f;
        var wx = ((r.M3 * cy) + (r.M0 * cx)) + (r.M6 * cz) + xf.T.X;
        var wy = ((r.M4 * cy) + (r.M1 * cx)) + (r.M7 * cz) + xf.T.Y;
        var wz = ((r.M5 * cy) + (r.M2 * cx)) + (r.M8 * cz) + xf.T.Z;
        var ex = (max.X - min.X) * 0.5f;
        var ey = (max.Y - min.Y) * 0.5f;
        var ez = (max.Z - min.Z) * 0.5f;
        var rx = ((MathF.Abs(r.M3) * ey) + (MathF.Abs(r.M0) * ex)) + (MathF.Abs(r.M6) * ez);
        var ry = ((MathF.Abs(r.M4) * ey) + (MathF.Abs(r.M1) * ex)) + (MathF.Abs(r.M7) * ez);
        var rz = ((MathF.Abs(r.M2) * ex) + (MathF.Abs(r.M5) * ey)) + (MathF.Abs(r.M8) * ez);
        return new(new(wx - rx, wy - ry, wz - rz), new(rx + wx, ry + wy, rz + wz));
    }

    private static Aabb Pad(Aabb b) => new(
        new(b.Min.X - 0.0625f, b.Min.Y - 0.0625f, b.Min.Z - 0.0625f),
        new(b.Max.X + 0.0625f, b.Max.Y + 0.0625f, b.Max.Z + 0.0625f));

    /// <summary>DAT_18055E910: the empty box, +FLT_MAX min and -FLT_MAX max.</summary>
    public static Aabb EmptyBox => new(
        new(float.MaxValue, float.MaxValue, float.MaxValue), new(-float.MaxValue, -float.MaxValue, -float.MaxValue));

    private static float MinSs(float a, float b) => a < b ? a : b;

    private static float MaxSs(float a, float b) => a > b ? a : b;
}
