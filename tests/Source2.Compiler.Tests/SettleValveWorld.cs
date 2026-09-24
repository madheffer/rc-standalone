using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.Tests;

/// <summary>
/// A captured settle world (<see cref="SettleCapture"/>) rebuilt in Valve's
/// world in this process, through the same interface calls the settle made:
/// bodies created static at their origin, hulls and meshes made by
/// RnHullCreate / RnMeshCreate from the captured geometry and added in order,
/// then the settled bodies turned dynamic. The captured state, attributes and
/// materials are written over what Valve computed (the mass update is not
/// what is under test). The collision group table is written first, since
/// the capture's comes from the compile's rules.
/// </summary>
internal sealed unsafe class SettleValveWorld
{
    public readonly Vphysics2World World;
    public readonly List<Vphysics2World.Body> Bodies = [];

    /// <summary>Per captured shape id: Valve's CRnShape.</summary>
    public readonly Dictionary<int, nint> Shapes = [];

    public int HullsRebuiltExactly, HullsCopied, MeshesRebuiltExactly, MeshesCopied;

    private SettleValveWorld(Vphysics2World world) => World = world;

    /// <summary>
    /// Builds the world; <paramref name="table"/> is written to the group
    /// table before the world exists. <paramref name="order"/> is the order
    /// the bodies are created in (default: the capture's); the world's own
    /// first body comes first either way.
    /// </summary>
    public static SettleValveWorld Build(SettleCapture capture, ushort[] table, nint module, IReadOnlyList<int>? order = null)
    {
        var global = new Span<ushort>((ushort*)Vphysics2Oracle.At(module, 0x18045b328), 4096);
        table.CopyTo(global);
        var world = Vphysics2World.Create() ?? throw new InvalidOperationException("no world");
        var v = new SettleValveWorld(world);
        var byCapture = new Dictionary<int, Vphysics2World.Body>();
        order ??= Enumerable.Range(1, capture.Bodies.Count - 1).ToList();
        // The world's own body (index 0) is the one the settle's PhysDoc made
        // first, with no shapes (world vfn 0x1e8, vfn 0x630).
        byCapture[0] = world.CreateBody(0, new Vec3(0, 0, 0), Quat.Identity);
        foreach (var i in order)
        {
            var cb = capture.Bodies[i];
            var spawn = capture.Spawn.Count > 0 ? capture.Spawn[i]!.Value : Derived(cb.State);
            var body = world.CreateBody(0, spawn.Position, spawn.Orientation);
            if (spawn.Scale != 1f)
                SetTransform(body, spawn.Position, spawn.Scale, spawn.Orientation);
            byCapture[i] = body;
            foreach (var cs in cb.Shapes)
            {
                nint shape = cs.Type switch
                {
                    2 => world.AddHull(body, v.Hull(cs), BitConverter.ToSingle(cs.Head, 0xb8)),
                    3 => body.AddMesh(v.Mesh(cs), MemoryMarshal.Read<Vector3>(cs.Head.AsSpan(0xb8))),
                    1 => AddCapsule(body, cs.Head),
                    _ => throw new NotSupportedException($"shape type {cs.Type}"),
                };
                var rn = *(byte**)(shape + 8);
                cs.Head.AsSpan(0x20, 0x58).CopyTo(new Span<byte>(rn + 0x20, 0x58));
                v.Shapes[cs.Id] = (nint)rn;
            }
            Overwrite(body, cb.State);
        }
        for (var i = 0; i < capture.Bodies.Count; i++)
            v.Bodies.Add(byCapture[i]);
        var dynamicBodies = capture.MadeDynamic.Count > 0
            ? capture.MadeDynamic
            : Enumerable.Range(0, capture.Bodies.Count).Where(i => BitConverter.ToInt32(capture.Bodies[i].State, 0x54) == 2).ToList();
        foreach (var i in dynamicBodies)
        {
            var body = v.Bodies[i];
            ((delegate* unmanaged<nint*, int, void>)(*(nint**)body.Wrapper)[0x18 / 8])(body.Wrapper, 2);
            Overwrite(body, capture.Bodies[i].State);
        }
        // The settle's bodies are awake at the first step, in the order their
        // awake index (+0x18) shows; SetType leaves them out of the awake
        // list here, so they join it through FUN_1801eefd0 in that order.
        var addAwake = (delegate* unmanaged<byte*, byte*, void>)world.At(0x1801eefd0);
        foreach (var i in dynamicBodies.OrderBy(i => BitConverter.ToInt32(capture.Bodies[i].State, 0x18)))
            if (*(int*)(v.Bodies[i].Rn + 0x18) < 0)
                addAwake(world.Rn, v.Bodies[i].Rn);
        return v;
    }

    private static (Vec3 Position, float Scale, Quat Orientation) Derived(byte[] state)
    {
        var s = MemoryMarshal.Read<RnBodyState>(state);
        return (RnTransform.Of(s).T, s.Scale, s.Orientation);
    }

    /// <summary>Body vfn 0x1b8 with the scale in the fourth float.</summary>
    private static void SetTransform(Vphysics2World.Body body, Vec3 position, float scale, Quat q)
    {
        var xf = (float*)NativeMemory.AlignedAlloc(0x20, 0x10);
        xf[0] = position.X;
        xf[1] = position.Y;
        xf[2] = position.Z;
        xf[3] = scale;
        xf[4] = q.X;
        xf[5] = q.Y;
        xf[6] = q.Z;
        xf[7] = q.W;
        ((delegate* unmanaged<nint*, float*, void>)(*(nint**)body.Wrapper)[0x1b8 / 8])(body.Wrapper, xf);
        NativeMemory.AlignedFree(xf);
    }

    /// <summary>Body vfn 0x60 (FUN_1801b07f0): a capsule from its two centres and radius (header +0xd4) at its scale (+0xf0).</summary>
    private static nint AddCapsule(Vphysics2World.Body body, byte[] head)
    {
        var capsule = stackalloc float[7];
        for (var k = 0; k < 7; k++)
            capsule[k] = BitConverter.ToSingle(head, 0xd4 + 4 * k);
        var add = (delegate* unmanaged<nint*, float*, float, byte, nint>)(*(nint**)body.Wrapper)[0x60 / 8];
        return add(body.Wrapper, capsule, BitConverter.ToSingle(head, 0xf0), 0);
    }

    /// <summary>The physical part of the body (+0x4a flags and everything from +0x88) as captured.</summary>
    private static void Overwrite(Vphysics2World.Body body, byte[] captured)
    {
        captured.AsSpan(0x4a, 2).CopyTo(new Span<byte>(body.Rn + 0x4a, 2));
        captured.AsSpan(0x88, 0x260 - 0x88).CopyTo(new Span<byte>(body.Rn + 0x88, 0x260 - 0x88));
    }

    private static readonly (int Offset, int Stride, string Name)[] HullArrays =
        [(0x70, 12, "pos"), (0x88, 16, "planes"), (0xb0, 1, "verts"), (0xc8, 4, "edges"), (0xe0, 1, "faces")];

    private static readonly (int Offset, int Stride, string Name)[] MeshArrays =
        [(0x18, 32, "nodes"), (0x30, 12, "mverts"), (0x48, 12, "tris"), (0x90, 1, "mats")];

    /// <summary>RnHullCreate over the captured points, then made to equal the capture (header scalars, flags, arrays) where it does not.</summary>
    private byte* Hull(SettleCapture.Shape cs)
    {
        var hull = World.CreateHull(cs.Hull!.VertexPositions);
        var header = cs.Raw["hull"];
        var exact = Conform(hull, header, 0x70, HullArrays, cs.Raw);
        exact &= Copy(header.AsSpan(0xa0, 4), hull + 0xa0);
        if (exact)
            HullsRebuiltExactly++;
        else
            HullsCopied++;
        return hull;
    }

    /// <summary>RnMeshCreate over the captured triangles, then made to equal the capture where it does not.</summary>
    private byte* Mesh(SettleCapture.Shape cs)
    {
        var m = cs.Mesh!;
        var indices = m.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
        var mesh = World.CreateMesh(indices, m.Vertices);
        if (Conform(mesh, cs.Raw["mesh"], 0x18, MeshArrays, cs.Raw))
            MeshesRebuiltExactly++;
        else
            MeshesCopied++;
        return mesh;
    }

    /// <summary>
    /// The object's leading scalars and listed arrays ({count, ?, data} at each
    /// offset) set to the captured bytes. True when nothing had to change.
    /// </summary>
    private static bool Conform(byte* obj, byte[] header, int scalars, (int Offset, int Stride, string Name)[] arrays,
                                Dictionary<string, byte[]> raw)
    {
        var exact = Copy(header.AsSpan(0, scalars), obj);
        foreach (var (offset, stride, name) in arrays)
        {
            var want = raw[name];
            var count = *(int*)(obj + offset);
            var data = *(byte**)(obj + offset + 8);
            if (count * stride == want.Length && new ReadOnlySpan<byte>(data, want.Length).SequenceEqual(want))
                continue;
            exact = false;
            if (count * stride != want.Length)
            {
                data = (byte*)NativeMemory.AlignedAlloc((nuint)Math.Max(want.Length, 16), 16);
                *(byte**)(obj + offset + 8) = data;
                *(int*)(obj + offset) = want.Length / stride;
                *(int*)(obj + offset + 0x10) = want.Length / stride;
            }
            want.CopyTo(new Span<byte>(data, want.Length));
        }
        return exact;
    }

    private static bool Copy(ReadOnlySpan<byte> from, byte* to)
    {
        var target = new Span<byte>(to, from.Length);
        if (target.SequenceEqual(from))
            return true;
        from.CopyTo(target);
        return false;
    }
}
