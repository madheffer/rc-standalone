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

    /// <summary>Steps Valve's world and the port's side by side, comparing after each step; returns the steps that matched.</summary>
    private int Run(Vphysics2World w, int steps, Action<int>? between = null)
    {
        var module = Vphysics2Oracle.Load()!.Value;
        var hulls = new Dictionary<nint, RnHull>();
        var meshes = new Dictionary<nint, RnMesh>();
        var port = RnWorldReader.Read(module, w.Rn, hulls, meshes, broadphase: true);
        for (var s = 0; s < steps; s++)
        {
            if (between != null)
            {
                between(s);
                port = RnWorldReader.Read(module, w.Rn, hulls, meshes, broadphase: true);
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
            Assert.True(failure == null, $"step {s}: {failure}");
        }
        return steps;
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
}
