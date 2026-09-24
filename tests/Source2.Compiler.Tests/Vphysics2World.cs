using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.Tests;

/// <summary>
/// Valve's physics world in this process: CreateInterface("VPhysics2_Interface_001"),
/// CreateWorld (interface slot 0x60), bodies and shapes through the IPhysicsWorld and
/// IPhysicsBody vtables, and Simulate (world slot 0).
/// </summary>
internal sealed unsafe class Vphysics2World
{
    private readonly nint _module;
    private readonly nint* _world;

    /// <summary>The CRnWorld (IPhysicsWorld+0x30).</summary>
    public readonly byte* Rn;

    private Vphysics2World(nint module, nint* world)
    {
        _module = module;
        _world = world;
        Rn = *(byte**)((byte*)world + 0x30);
    }

    /// <summary>A new world with vphysics' defaults, or null without the oracle build.</summary>
    public static Vphysics2World? Create()
    {
        if (Vphysics2Oracle.Load() is not { } module)
            return null;
        var createInterface = (delegate* unmanaged<byte*, int*, nint*>)NativeLibrary.GetExport(module, "CreateInterface");
        nint* iface;
        fixed (byte* name = "VPhysics2_Interface_001\0"u8)
            iface = createInterface(name, null);
        if (iface == null)
            return null;
        var createWorld = (delegate* unmanaged<nint*, int, nint*>)Slot(iface, 0x60);
        var world = createWorld(iface, 0);
        return world == null ? null : new Vphysics2World(module, world);
    }

    private static nint Slot(nint* obj, int offset) => *(nint*)((byte*)*obj + offset);

    /// <summary>A function Ghidra calls FUN_&lt;va&gt;.</summary>
    public nint At(ulong va) => Vphysics2Oracle.At(_module, va);

    /// <summary>World slot 0x1e8 creates a body, body slot 0x18 sets its type, body slot 0x1b8 its transform (scale 1).</summary>
    public Body CreateBody(int type, Vec3 position, Quat orientation)
    {
        var wrapper = (nint*)((delegate* unmanaged<nint*, nint>)Slot(_world, 0x1e8))(_world);
        ((delegate* unmanaged<nint*, int, void>)Slot(wrapper, 0x18))(wrapper, type);
        var xf = (float*)NativeMemory.AlignedAlloc(0x20, 0x10);
        xf[0] = position.X;
        xf[1] = position.Y;
        xf[2] = position.Z;
        xf[3] = 1f;
        xf[4] = orientation.X;
        xf[5] = orientation.Y;
        xf[6] = orientation.Z;
        xf[7] = orientation.W;
        ((delegate* unmanaged<nint*, float*, void>)Slot(wrapper, 0x1b8))(wrapper, xf);
        NativeMemory.AlignedFree(xf);
        return new Body(wrapper, *(byte**)((byte*)wrapper + 8));
    }

    /// <summary>RnHullCreate over the points, default options.</summary>
    public byte* CreateHull(ReadOnlySpan<Vector3> points)
    {
        var create = (delegate* unmanaged<int, Vector3*, void*, int*, byte*>)NativeLibrary.GetExport(_module, "RnHullCreate");
        var options = 0;
        fixed (Vector3* p = points)
            return create(points.Length, p, null, &options);
    }

    /// <summary>RnMeshCreate over a triangle list, no materials, default options.</summary>
    public byte* CreateMesh(int[] indices, Vector3[] vertices)
    {
        var create = (delegate* unmanaged<int, int*, byte*, int, Vector3*, void*, void*, byte*>)NativeLibrary.GetExport(_module, "RnMeshCreate");
        fixed (int* pi = indices)
        fixed (Vector3* pv = vertices)
            return create(indices.Length / 3, pi, null, vertices.Length, pv, null, null);
    }

    /// <summary>Body slot 0x68: a hull shape; returns the IPhysicsShape.</summary>
    public nint AddHull(Body body, byte* hull, float scale = 1f)
    {
        var add = (delegate* unmanaged<nint*, byte*, float, byte, nint>)Slot(body.Wrapper, 0x68);
        return add(body.Wrapper, hull, scale, 0);
    }

    /// <summary>Body slot 0x70: a triangle mesh shape at scale (1, 1, 1).</summary>
    public nint AddMesh(Body body, byte* mesh)
    {
        var scale = stackalloc float[3] { 1f, 1f, 1f };
        var add = (delegate* unmanaged<nint*, byte*, float*, byte, void*, nint>)Slot(body.Wrapper, 0x70);
        return add(body.Wrapper, mesh, scale, 0, null);
    }

    /// <summary>The mass helper's vt 0xb8 (0x180008940): mass, inertia and centre from the shapes.</summary>
    public void UpdateMass(Body body)
    {
        var helper = stackalloc nint[2];
        helper[1] = (nint)body.Rn;
        var update = (delegate* unmanaged<nint*, byte, void>)At(0x180008940);
        update(helper, 0);
    }

    /// <summary>IPhysicsWorld::Simulate (world slot 0).</summary>
    public void Step(float dt, bool first)
    {
        var simulate = (delegate* unmanaged<nint*, float, byte, void>)Slot(_world, 0);
        simulate(_world, dt, (byte)(first ? 1 : 0));
    }

    /// <summary>Where <see cref="StepPhased"/> stops to let a test look.</summary>
    public enum Phase
    {
        BeforeCollide,
        AfterCollide,
        AfterSolve,
        BeforeContinuous,
        AfterStep,
    }

    /// <summary>
    /// CRnWorld::Step (FUN_180200840) call by call, so a test can read the
    /// world between the passes. The world must have no step callbacks and no
    /// controllers (a bare world has none).
    /// </summary>
    public void StepPhased(float dt, bool first, Action<Phase> hook)
    {
        var w = Rn;
        if (!(dt > 1e-6f))
            return;
        if (*(int*)(w + 0x9b0) != 0 || *(int*)(w + 0x698) != 0 || *(int*)(w + 0x9c8) != 0)
            throw new InvalidOperationException("step callbacks or controllers are not driven here");
        if (first)
        {
            *(int*)(w + 0x1d0) = *(int*)(w + 0x1cc);
            *(int*)(w + 0x48) = 0;
            *(int*)(w + 0x94) = 0;
        }
        ((delegate* unmanaged<byte*, void>)At(0x180203d70))(w);
        var scratch = *(byte**)(w + 0x220);
        *(int*)(w + 0x1d4) += 1;
        *(float*)(w + 0x1cc) = dt + *(float*)(w + 0x1cc);
        ((delegate* unmanaged<void*, byte*, int, byte, void>)At(0x1802d5740))(
            *(void**)(w + 0x110), scratch + 0x4e8, *(int*)(w + 0x1ac), w[0x1b0]);
        var buildContacts = (delegate* unmanaged<byte*, byte*, void>)At(0x1801f1dc0);
        buildContacts(w, scratch + 0x4e8);
        hook(Phase.BeforeCollide);
        ((delegate* unmanaged<byte*, float, void>)At(0x1801f6980))(w, dt);
        hook(Phase.AfterCollide);
        ((delegate* unmanaged<byte*, float, byte, byte*, void>)At(0x1801ff820))(
            w, dt, (byte)(first ? 1 : 0), *(byte**)(w + 0x220) + 0x4e8);
        hook(Phase.AfterSolve);
        buildContacts(w, *(byte**)(w + 0x220) + 0x4e8);
        hook(Phase.BeforeContinuous);
        ((delegate* unmanaged<byte*, float, void>)At(0x1801ffe80))(w, dt);
        var broken = *(uint*)(w + 0xa24) & 0x7fffffff;
        if (*(int*)(w + 0xa4c) != 0)
        {
            var list = broken == 0 ? null : *(nint**)(w + 0xa28);
            var breakJoint = (delegate* unmanaged<nint, void>)At(0x1802044c0);
            for (var i = 0; i < *(int*)(w + 0xa4c); i++)
                breakJoint(list[i]);
        }
        *(int*)(w + 0xa4c) = 0;
        ((delegate* unmanaged<byte*, void>)At(0x1801faf80))(w);
        ((delegate* unmanaged<byte*, float, void>)At(0x180200b40))(w, dt);
        w[0x10c] |= 1;
        hook(Phase.AfterStep);
    }

    /// <summary>An IPhysicsBody and its CRnBody (+8).</summary>
    public readonly struct Body(nint* wrapper, byte* rn)
    {
        public readonly nint* Wrapper = wrapper;
        public readonly byte* Rn = rn;

        public ref RnBodyState State => ref *(RnBodyState*)Rn;
    }
}
