using System.Runtime.InteropServices;
using System.Numerics;
using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The step's bookkeeping in lockstep with Valve's worlds: each pass of
/// CRnWorld::Step is run by Valve, the world is read before and after, the
/// port runs the same pass on the before-read, and the two must agree line
/// for line (<see cref="WorldSignature"/>): island membership and order, the
/// manager's lists, the contact lists and links, manifolds, body states and
/// awake indices.
/// </summary>
[Collection(Vphysics2PatchCollection.Name)]
public sealed unsafe class WorldLockstepTests(ITestOutputHelper output)
{
    /// <summary>The time of impact solve (FUN_1802001b0), detoured so the continuous pre-pass can be read before it.</summary>
    private const ulong SolveToi = 0x1802001b0;
    private static readonly byte[] SolveToiPrologue = [0x48, 0x8b, 0xc4, 0x48, 0x89, 0x50, 0x10, 0x48, 0x89, 0x48, 0x08, 0x55];
    private static delegate* unmanaged<byte*, void*, byte*, void> s_toiOriginal;
    private static Action<nint, nint>? s_onToi;

    [UnmanagedCallersOnly]
    private static void ToiHook(byte* world, void* context, byte* list)
    {
        try
        {
            s_onToi?.Invoke((nint)world, (nint)list);
        }
        catch
        {
            // Reported by the test through its own state; nothing may cross into Valve's frames.
        }
        s_toiOriginal(world, context, list);
    }

    private const float Dt = 1f / 64;

    /// <summary>A world with a static mesh floor, or null without the oracle build.</summary>
    private static (Vphysics2World World, nint Module)? Floor()
    {
        var w = Vphysics2World.Create();
        if (w == null || Vphysics2Oracle.Load() is not { } module)
            return null;
        var floor = w.CreateBody(0, new Vec3(0, 0, 0), Quat.Identity);
        var mesh = w.CreateMesh([0, 1, 2, 0, 2, 3], [new(-400, -400, 0), new(400, -400, 0), new(400, 400, 0), new(-400, 400, 0)]);
        w.AddMesh(floor, mesh);
        return (w, module);
    }

    private static byte* Box(Vphysics2World w, float hx, float hy, float hz)
    {
        var pts = new Vector3[8];
        for (var i = 0; i < 8; i++)
            pts[i] = new Vector3((i & 1) == 0 ? -hx : hx, (i & 2) == 0 ? -hy : hy, (i & 4) == 0 ? -hz : hz);
        return w.CreateHull(pts);
    }

    private static Vphysics2World.Body Drop(Vphysics2World w, byte* hull, Vec3 at, Quat q)
    {
        var b = w.CreateBody(2, at, q);
        w.AddHull(b, hull);
        w.UpdateMass(b);
        return b;
    }

    private static Quat Axis(float x, float y, float z, float angle)
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(x, y, z)), angle);
        return new Quat(q.X, q.Y, q.Z, q.W);
    }

    /// <summary>Lost, both asleep, began, ended, resized, splits: what the checked passes exercised.</summary>
    private readonly int[] Counts = new int[15];

    private void Report(int passes)
        => output.WriteLine($"{passes} collide passes; lost {Counts[0]}, both asleep {Counts[1]}, began {Counts[2]}, ended {Counts[3]}, resized {Counts[4]}, islands split {Counts[5]}, island-steps {Counts[6]}; solved serial islands {Counts[7]}, free bodies {Counts[8]}, woken {Counts[9]}, slept {Counts[10]}, contacts back {Counts[11]}, continuous {Counts[12]}, gathered by kinematic {Counts[13]} from {Counts[14]} fast kinematic");

    /// <summary>Runs steps, checking the collide pass; the scene can change between steps.</summary>
    private int Run(Vphysics2World w, nint module, int steps, Action<int>? between = null)
    {
        var hulls = new Dictionary<nint, RnHull>();
        var meshes = new Dictionary<nint, RnMesh>();
        var checkedPasses = 0;
        using var hooks = new Vphysics2Hooks();
        s_toiOriginal = (delegate* unmanaged<byte*, void*, byte*, void>)hooks.Detour(
            w.At(SolveToi), (nint)(delegate* unmanaged<byte*, void*, byte*, void>)&ToiHook, SolveToiPrologue);
        RnWorld? beforeContinuous = null;
        string? continuousFailure = null;
        s_onToi = (world, list) =>
        {
            if (list != world + 0xa78 || beforeContinuous == null || continuousFailure != null)
                return;
            var valve = WorldSignature.Lines(RnWorldReader.Read(module, (byte*)world, hulls, meshes));
            var gathered = beforeContinuous.ContinuousBodies[0].Count;
            WorldSolver.GatherContinuous(beforeContinuous);
            Counts[13] += beforeContinuous.ContinuousBodies[0].Count - gathered;
            continuousFailure = WorldSignature.Diff(valve, WorldSignature.Lines(beforeContinuous));
            beforeContinuous = null;
        };
        for (var s = 0; s < steps; s++)
        {
            between?.Invoke(s);
            RnWorld? before = null, beforeSolve = null;
            string? failure = null;
            var first = s == 0;
            w.StepPhased(Dt, first, phase =>
            {
                if (failure != null)
                    return;
                if (phase == Vphysics2World.Phase.BeforeCollide)
                {
                    before = RnWorldReader.Read(module, w.Rn, hulls, meshes);
                }
                else if (phase == Vphysics2World.Phase.AfterCollide)
                {
                    beforeSolve = RnWorldReader.Read(module, w.Rn, hulls, meshes);
                    var valve = WorldSignature.Lines(beforeSolve);
                    var lists = new CollideLists();
                    var pending = before!.Islands.SplitPending.Count;
                    ContactLifecycle.CollideContacts(before!, lists);
                    Counts[0] += lists.Lost.Count;
                    Counts[1] += lists.BothAsleep.Count;
                    Counts[2] += lists.Began.Count;
                    Counts[3] += lists.Ended.Count;
                    Counts[4] += lists.Resized.Count;
                    Counts[5] += pending - before.Islands.SplitPending.Count;
                    Counts[6] += before.Islands.All.Count(n => n is RnIsland);
                    ContactLifecycle.Flush(before, lists);
                    var ours = WorldSignature.Lines(before!);
                    failure = WorldSignature.Diff(valve, ours);
                    if (failure != null)
                    {
                        failure = "collide pass: " + failure;
                        output.WriteLine("valve:");
                        foreach (var l in valve.Where(l => !l.Contains(" state ")))
                            output.WriteLine(l);
                        output.WriteLine("ours:");
                        foreach (var l in ours.Where(l => !l.Contains(" state ")))
                            output.WriteLine(l);
                    }
                    checkedPasses++;
                }
                else if (phase == Vphysics2World.Phase.BeforeContinuous)
                {
                    beforeContinuous = RnWorldReader.Read(module, w.Rn, hulls, meshes);
                    Counts[14] += beforeContinuous.ContinuousBodies[2].Count;
                }
                else if (phase == Vphysics2World.Phase.AfterSolve)
                {
                    var valve = WorldSignature.Lines(RnWorldReader.Read(module, w.Rn, hulls, meshes));
                    var awake = beforeSolve!.ActiveBodies.Count;
                    var solveLists = new SolveLists();
                    var continuous = beforeSolve.ContinuousBodies.Sum(l => l.Count);
                    WorldSolver.Solve(beforeSolve, Dt, first, solveLists);
                    Counts[9] += solveLists.Woken.Count;
                    Counts[10] += solveLists.Slept.Count;
                    Counts[11] += solveLists.Reactivated.Count;
                    Counts[12] += beforeSolve.ContinuousBodies.Sum(l => l.Count) - continuous;
                    Counts[7] += beforeSolve.Islands.Serial.Count;
                    Counts[8] += beforeSolve.Islands.Free.Count;
                    var ours = WorldSignature.Lines(beforeSolve);
                    failure = WorldSignature.Diff(valve, ours);
                    if (failure != null)
                    {
                        failure = "solve pass: " + failure;
                        output.WriteLine("valve:");
                        foreach (var l in valve.Where(l => !l.Contains(" state ")))
                            output.WriteLine(l);
                        output.WriteLine("ours:");
                        foreach (var l in ours.Where(l => !l.Contains(" state ")))
                            output.WriteLine(l);
                    }
                }
            });
            Assert.True(failure == null, $"step {s}: {failure}");
            Assert.True(continuousFailure == null, $"step {s}: continuous pre-pass: {continuousFailure}");
        }
        return checkedPasses;
    }

    [Fact]
    public void StackOnMeshFloorMatches()
    {
        if (Floor() is not var (w, module))
            return;
        var box = Box(w, 8, 8, 8);
        Drop(w, box, new Vec3(0, 0, 10), Quat.Identity);
        Drop(w, box, new Vec3(1, 0, 28), Quat.Identity);
        Drop(w, box, new Vec3(-1, 1, 46), Axis(0, 0, 1, 0.3f));
        Drop(w, box, new Vec3(60, 0, 20), Axis(1, 1, 0, 0.5f));
        Report(Run(w, module, 400));
    }

    /// <summary>Wakes a body the way the game does (FUN_1801c0ff0 with the timer reset) and gives it a velocity.</summary>
    private static void Kick(Vphysics2World w, Vphysics2World.Body b, Vec3 velocity)
    {
        ((delegate* unmanaged<byte*, byte, void>)w.At(0x1801c0ff0))(b.Rn, 1);
        b.State.LinearVelocity = velocity;
    }

    [Fact]
    public void TopplingBoxesMatch()
    {
        if (Floor() is not var (w, module))
            return;
        var box = Box(w, 8, 8, 8);
        var plank = Box(w, 24, 4, 2);
        Drop(w, box, new Vec3(0, 0, 9), Quat.Identity);
        Drop(w, plank, new Vec3(10, 0, 22), Axis(0, 1, 0, 0.1f));
        Drop(w, box, new Vec3(26, 2, 40), Axis(1, 0, 1, 0.7f));
        Drop(w, box, new Vec3(-12, -3, 44), Axis(1, 2, 0, 0.4f));
        Drop(w, plank, new Vec3(0, 20, 30), Axis(1, 0, 0, 1.2f));
        Report(Run(w, module, 500));
    }

    [Fact]
    public void SleepingStackWokenByADropMatches()
    {
        if (Floor() is not var (w, module))
            return;
        var box = Box(w, 8, 8, 8);
        for (var k = 0; k < 4; k++)
            Drop(w, box, new Vec3(0.5f * k, 0, 8.5f + 17 * k), Quat.Identity);
        Drop(w, box, new Vec3(40, 0, 9), Quat.Identity);
        Report(Run(w, module, 520, s =>
        {
            if (s == 300)
                Drop(w, box, new Vec3(3, 1, 90), Axis(1, 1, 1, 0.5f));
        }));
    }

    [Fact]
    public void KickedOutOfASleepingStackMatches()
    {
        if (Floor() is not var (w, module))
            return;
        var box = Box(w, 8, 8, 8);
        var bodies = new List<Vphysics2World.Body>();
        for (var k = 0; k < 4; k++)
            bodies.Add(Drop(w, box, new Vec3(0, 0.25f * k, 8.5f + 17 * k), Quat.Identity));
        Report(Run(w, module, 560, s =>
        {
            if (s == 300)
                Kick(w, bodies[1], new Vec3(400, 0, 60));
            if (s == 420)
                Kick(w, bodies[0], new Vec3(0, -300, 0));
        }));
    }

    [Fact]
    public void ManyContactsMatch()
    {
        if (Floor() is not var (w, module))
            return;
        var box = Box(w, 6, 6, 6);
        for (var row = 0; row < 4; row++)
            for (var k = 0; k < 5 - row; k++)
                Drop(w, box, new Vec3(13 * k + 6.5f * row, 0.3f * row, 6.5f + 12.5f * row), Axis(0, 0, 1, 0.05f * (k - row)));
        for (var k = 0; k < 6; k++)
            Drop(w, box, new Vec3(-60 + 3 * k, 40, 10 + 14 * k), Axis(1, k, 0.5f, 0.3f * k));
        Report(Run(w, module, 500));
    }

    /// <summary>Wakes a kinematic body and gives its controller (+0x80) a new target and time.</summary>
    private static void MoveTo(Vphysics2World w, Vphysics2World.Body b, Vec3 position, Quat orientation, float time)
    {
        ((delegate* unmanaged<byte*, byte, void>)w.At(0x1801c0ff0))(b.Rn, 1);
        var target = (byte*)b.State.Controller;
        *(Quat*)target = orientation;
        *(Vec3*)(target + 0x18) = position;
        *(float*)(target + 0x24) = time;
    }

    [Fact]
    public void KinematicPusherMatches()
    {
        if (Floor() is not var (w, module))
            return;
        var box = Box(w, 6, 6, 6);
        for (var k = 0; k < 5; k++)
            Drop(w, box, new Vec3(40 + 14 * k, 0, 6.5f), Quat.Identity);
        var pusher = w.CreateBody(1, new Vec3(-40, 0, 10), Quat.Identity);
        w.AddHull(pusher, Box(w, 10, 20, 8));
        w.UpdateMass(pusher);
        Report(Run(w, module, 300, s =>
        {
            if (s == 60)
                MoveTo(w, pusher, new Vec3(80, 0, 10), Quat.Identity, 0.25f);
            if (s == 120)
                MoveTo(w, pusher, new Vec3(-40, 30, 12), Axis(0, 0, 1, 0.8f), 0.4f);
        }));
    }
}
