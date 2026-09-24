using System.Numerics;
using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Whole steps with continuous collision in lockstep with Valve's world:
/// props thrown fast at a bumpy mesh floor and at static hulls. The port
/// is read from Valve's world once and then steps alone; after every step
/// its world must equal a fresh read of Valve's line for line
/// (<see cref="WorldSignature"/>), and its broadphase Valve's node for node.
/// </summary>
[Collection(Vphysics2PatchCollection.Name)]
public sealed unsafe class ContinuousLockstepTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 90;

    private static float F(Random r, double lo, double hi) => (float)(lo + r.NextDouble() * (hi - lo));

    private static byte* Terrain(Vphysics2World w, Random r, int n, float cell, float bump)
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
                vertices.Add(new Vector3(i * cell - n * cell / 2, j * cell - n * cell / 2, F(r, -bump, bump)));
        for (var i = 0; i + 1 < n; i++)
            for (var j = 0; j + 1 < n; j++)
            {
                var a = i * n + j;
                indices.AddRange([a, a + n, a + 1, a + 1, a + n, a + n + 1]);
            }
        return w.CreateMesh([.. indices], [.. vertices]);
    }

    private static byte* Box(Vphysics2World w, float hx, float hy, float hz)
    {
        var pts = new Vector3[8];
        for (var i = 0; i < 8; i++)
            pts[i] = new Vector3((i & 1) == 0 ? -hx : hx, (i & 2) == 0 ? -hy : hy, (i & 4) == 0 ? -hz : hz);
        return w.CreateHull(pts);
    }

    private static byte* Rock(Vphysics2World w, Random r, float k)
    {
        var pts = new Vector3[10 + r.Next(16)];
        for (var i = 0; i < pts.Length; i++)
            pts[i] = new Vector3(F(r, -k, k), F(r, -k, k), F(r, -k * 0.7, k * 0.7));
        return w.CreateHull(pts);
    }

    private static Quat RandomQuat(Random r) => RnMath.Normalize(new Quat(F(r, -1, 1), F(r, -1, 1), F(r, -1, 1), F(r, -1, 1)));

    /// <summary>A scene: a bumpy floor, a few static blocks, props dropped and thrown.</summary>
    private static Vphysics2World Scene(int seed, int props, double minHalf, double maxHalf, double maxSpeed)
    {
        var w = Vphysics2World.Create()!;
        var random = new Random(seed);
        var floor = w.CreateBody(0, default, Quat.Identity);
        w.AddMesh(floor, Terrain(w, random, 11, 60, 3));
        for (var k = 0; k < 4; k++)
        {
            var block = w.CreateBody(0, new Vec3(F(random, -200, 200), F(random, -200, 200), 10), RandomQuat(random));
            w.AddHull(block, Box(w, F(random, 10, 30), F(random, 10, 30), F(random, 8, 14)));
        }
        for (var i = 0; i < props; i++)
        {
            var h = F(random, minHalf, maxHalf);
            var speed = random.NextDouble() * maxSpeed;
            var drop = (float)(speed * speed / 720.0);
            var body = w.CreateBody(2, new Vec3(F(random, -250, 250), F(random, -250, 250), 2 * h + drop + 5), RandomQuat(random));
            w.AddHull(body, random.Next(2) == 0 ? Box(w, h, h * F(random, 0.4, 1), h * F(random, 0.4, 1)) : Rock(w, random, h));
            w.UpdateMass(body);
            if (random.Next(3) == 0)
                body.State.LinearVelocity = new Vec3(F(random, -0.6, 0.6) * (float)maxSpeed, F(random, -0.6, 0.6) * (float)maxSpeed, -F(random, 0, 0.5) * (float)maxSpeed);
            if (random.Next(3) == 0)
                body.State.AngularVelocity = new Vec3(F(random, -10, 10), F(random, -10, 10), F(random, -10, 10));
        }
        return w;
    }

    private const ulong ToiSearchVa = 0x1801beac0;

    private static nint _search;
    private static byte* _world;
    private static readonly List<string> ValveCalls = [];

    /// <summary>The fields of a body a search changes, as text.</summary>
    private static string Fields(in RnBodyState b) =>
        $"p {b.Position} q {b.Orientation} p0 {b.PreviousPosition} q0 {b.PreviousOrientation} v {b.LinearVelocity} w {b.AngularVelocity} a0 {b.Cleared1E8:R} f {b.Flags249:x}/{b.Flags4A:x} sleep {b.SleepTimer:R}";

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static uint OnSearch(byte* body, byte* pass)
    {
        var mine = *(byte**)(body + 0x58) == _world;
        if (mine)
            ValveCalls.Add($"b{*(int*)(body + 0x14)} in  {Fields(*(RnBodyState*)body)}");
        var result = ((delegate* unmanaged<byte*, byte*, uint>)_search)(body, pass);
        if (mine)
            ValveCalls.Add($"b{*(int*)(body + 0x14)} out {result} {Fields(*(RnBodyState*)body)}");
        return result;
    }

    private (int Steps, int ToiSteps, int ToiBodies) Run(Vphysics2World w, int steps)
    {
        var module = Vphysics2Oracle.Load()!.Value;
        using var hooks = new Vphysics2Hooks();
        _search = hooks.Detour(Vphysics2Oracle.At(module, ToiSearchVa), (nint)(delegate* unmanaged<byte*, byte*, uint>)&OnSearch,
                               Convert.FromHexString("488bc44889501055488da8c8f7ffff"));
        _world = w.Rn;
        var portCalls = new List<string>();
        ContinuousSolve.Observe = (b, r) => portCalls.Add(r < 0 ? $"b{b.Index} in  {Fields(b.State)}" : $"b{b.Index} out {r} {Fields(b.State)}");
        var hulls = new Dictionary<nint, RnHull>();
        var meshes = new Dictionary<nint, RnMesh>();
        var port = RnWorldReader.Read(module, w.Rn, hulls, meshes, broadphase: true);
        var toiSteps = 0;
        var toiBodies = 0;
        port.SolveContinuous = (world, dt) =>
        {
            var n = world.ContinuousBodies[0].Count + world.ContinuousBodies[1].Count;
            if (n > 0)
            {
                toiSteps++;
                toiBodies += n;
            }
            ContinuousSolve.Run(world, dt);
        };
        for (var s = 0; s < steps; s++)
        {
            ValveCalls.Clear();
            portCalls.Clear();
            w.Step(Dt, s == 0);
            port.Step(Dt, s == 0);
            for (var k = 0; k < Math.Max(ValveCalls.Count, portCalls.Count); k++)
            {
                var a = k < ValveCalls.Count ? ValveCalls[k] : "-";
                var b = k < portCalls.Count ? portCalls[k] : "-";
                if (a != b)
                {
                    output.WriteLine($"step {s} search call {k}: valve {a} | ours {b}");
                    if (k > 0)
                        output.WriteLine($"before: {ValveCalls[k - 1]}");
                    break;
                }
            }
            var valve = WorldSignature.Lines(RnWorldReader.Read(module, w.Rn, hulls, meshes));
            var ours = WorldSignature.Lines(port);
            var failure = WorldSignature.Diff(valve, ours) ?? CompareTrees(w, port);
            Assert.True(failure == null, $"step {s} (TOI steps so far {toiSteps}): {failure}");
        }
        ContinuousSolve.Observe = null;
        _world = null;
        return (steps, toiSteps, toiBodies);
    }

    /// <summary>The broadphase trees node by node (a free node by its link only) and the pair set.</summary>
    private static string? CompareTrees(Vphysics2World w, RnWorld port)
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
        }
        var set = *(byte**)(bp + 0x2b8);
        var pairs = ours.Pairs;
        if (*(int*)(set + 0x10) != pairs.Count || *(int*)(set + 0x14) != pairs.Buckets)
            return $"pair set {*(int*)(set + 0x10)}/{*(int*)(set + 0x14)} vs {pairs.Count}/{pairs.Buckets}";
        return null;
    }

    [Theory]
    [InlineData(1, 40, 4.0, 24.0, 100.0, 600)]
    [InlineData(2, 40, 1.0, 4.0, 100.0, 600)]
    [InlineData(3, 40, 4.0, 24.0, 300.0, 600)]
    [InlineData(4, 60, 2.0, 12.0, 600.0, 400)]
    public void FastPropsMatch(int seed, int props, double minHalf, double maxHalf, double maxSpeed, int steps)
    {
        if (Vphysics2World.Create() is null)
            return;
        var (n, toiSteps, toiBodies) = Run(Scene(seed, props, minHalf, maxHalf, maxSpeed), steps);
        output.WriteLine($"{n} steps exact; continuous collision on {toiSteps} steps, {toiBodies} bodies");
    }
}
