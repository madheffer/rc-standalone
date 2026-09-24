using System.Runtime.InteropServices;
using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// atixref's settle in Valve's world in this process (<see cref="SettleValveWorld"/>):
/// does Valve, given the captured world through the interface, reproduce the
/// capture; does the creation order of the static bodies change the result;
/// and how far does the port stay exact following Valve's world step by step.
/// Set SETTLE to a bundle capture and SETTLE_CONTACTS to a narrowphase capture
/// of the same compile (for the collision group table).
/// </summary>
[Collection(Vphysics2PatchCollection.Name)]
public sealed unsafe class SettleValveReplay(ITestOutputHelper output)
{
    private const float Dt = 1f / 90;
    private const int Steps = 2700;

    private static (SettleCapture, ushort[], nint)? Load()
    {
        if (Environment.GetEnvironmentVariable("SETTLE") is not { Length: > 0 } path || !File.Exists(path))
            return null;
        if (Vphysics2Oracle.Load() is not { } module || Vphysics2World.Create() is null)
            return null;
        var capture = SettleCapture.Read(path);
        var table = new ReadOnlySpan<ushort>((ushort*)Vphysics2Oracle.At(module, 0x18045b328), 4096).ToArray();
        foreach (var ((a, b), flags) in SettleReplay.GroupFlags(capture))
            table[a * 64 + b] = table[b * 64 + a] = flags;
        return (capture, table, module);
    }

    /// <summary>The fields a settle hands back: position, velocities, orientation, sleep timer and flags.</summary>
    private static readonly (string Name, int Offset, int Length)[] Physical =
    [
        ("position", 0xfc, 12), ("velocity", 0x108, 12), ("spin", 0x114, 12), ("orientation", 0x120, 16),
        ("sleep timer", 0x1f4, 4), ("flags", 0x249, 1),
    ];

    private static string? PhysicalDifference(ReadOnlySpan<byte> ours, ReadOnlySpan<byte> theirs)
    {
        foreach (var (name, offset, length) in Physical)
            if (!ours.Slice(offset, length).SequenceEqual(theirs.Slice(offset, length)))
                return name;
        return null;
    }

    /// <summary>
    /// After the settle: position and orientation. The final capture is taken
    /// after the settle turned the bodies static again (SetType 0 zeroes the
    /// velocities, FUN_1801bd3e0), which this world's step does not do.
    /// </summary>
    private static string? FinalDifference(ReadOnlySpan<byte> ours, ReadOnlySpan<byte> theirs)
    {
        if (!ours.Slice(0xfc, 12).SequenceEqual(theirs.Slice(0xfc, 12)))
            return "position";
        return ours.Slice(0x120, 16).SequenceEqual(theirs.Slice(0x120, 16)) ? null : "orientation";
    }

    /// <summary>The body's state bytes from +0x88 on, and its index, awake index, flags and type.</summary>
    private static string? StateDifference(ReadOnlySpan<byte> ours, ReadOnlySpan<byte> theirs)
    {
        foreach (var (offset, length) in (ReadOnlySpan<(int, int)>)[(0x14, 8), (0x4a, 2), (0x54, 4), (0x88, 0x260 - 0x88)])
            for (var i = offset; i < offset + length; i++)
                if (ours[i] != theirs[i])
                    return $"+0x{i:x}";
        return null;
    }

    [Fact]
    public void ValveReproducesTheCapture()
    {
        if (Load() is not var (capture, table, module))
            return;
        var v = SettleValveWorld.Build(capture, table, module);
        output.WriteLine($"hulls rebuilt exactly {v.HullsRebuiltExactly}, made to match {v.HullsCopied}; meshes {v.MeshesRebuiltExactly}, {v.MeshesCopied}");
        var proxyOff = capture.Bodies.SelectMany(b => b.Shapes).Count(s => *(int*)(v.Shapes[s.Id] + 0x1c) != BitConverter.ToInt32(s.Head, 0x1c));
        var stateOff = Enumerable.Range(0, capture.Bodies.Count)
            .Count(i => StateDifference(new ReadOnlySpan<byte>(v.Bodies[i].Rn, 0x260), capture.Bodies[i].State) != null);
        output.WriteLine($"built: {proxyOff} proxy ids and {stateOff} body states differ from the capture");
        foreach (var i in Enumerable.Range(0, capture.Bodies.Count).Where(i => StateDifference(new ReadOnlySpan<byte>(v.Bodies[i].Rn, 0x260), capture.Bodies[i].State) != null).Take(3))
            output.WriteLine($"  body {i}: {StateDifference(new ReadOnlySpan<byte>(v.Bodies[i].Rn, 0x260), capture.Bodies[i].State)}");

        var rw = v.World.Rn;
        output.WriteLine($"awake {*(int*)(rw + 0xa00)}, list A {*(int*)(rw + 0x118 + 0x10)}, nodes {*(int*)(rw + 0x118)}; capture awake {BitConverter.ToInt32(capture.Head, 0xa00)} list A {BitConverter.ToInt32(capture.Head, 0x128)} nodes {BitConverter.ToInt32(capture.Head, 0x118)}");
        string? first = null;
        for (var s = 0; s <= Steps; s++)
        {
            var at = s == Steps ? capture.BodiesEnd : capture.BodiesAt.GetValueOrDefault(s);
            if (at != null)
            {
                var exact = 0;
                foreach (var (index, state) in at)
                {
                    var diff = s == Steps
                        ? FinalDifference(new ReadOnlySpan<byte>(v.Bodies[index].Rn, 0x260), state)
                        : PhysicalDifference(new ReadOnlySpan<byte>(v.Bodies[index].Rn, 0x260), state);
                    if (diff == null)
                        exact++;
                    else if (first == null)
                    {
                        var ours = MemoryMarshal.Read<RnBodyState>(new ReadOnlySpan<byte>(v.Bodies[index].Rn, 0x260 + 0x20));
                        var theirs = MemoryMarshal.Read<RnBodyState>(state);
                        first = $"step {s}, body {index}: {diff}; ours pos {ours.Position} v {ours.LinearVelocity} active {ours.ActiveIndex} flags {ours.Flags249:x}, captured pos {theirs.Position} v {theirs.LinearVelocity} active {theirs.ActiveIndex} flags {theirs.Flags249:x}";
                    }
                }
                if (s <= 20 || s % 100 == 0 || s == Steps)
                    output.WriteLine($"{(s == Steps ? "end" : $"before step {s}")}: {exact}/{at.Count} bodies exact");
            }
            if (s < Steps)
                v.World.StepPhased(Dt, s == 0, _ => { });
        }
        output.WriteLine(first == null ? "Valve reproduces every snapshot" : $"first difference: {first}");
    }

    [Fact]
    public void StaticCreationOrderEffect()
    {
        if (Load() is not var (capture, table, module))
            return;
        var statics = Enumerable.Range(1, capture.Bodies.Count - 1).Where(i => BitConverter.ToInt32(capture.Bodies[i].State, 0x54) == 0).ToList();
        var dynamicBodies = Enumerable.Range(1, capture.Bodies.Count - 1).Except(statics).ToList();
        var orders = new (string Name, List<int> Order)[]
        {
            ("captured", Enumerable.Range(1, capture.Bodies.Count - 1).ToList()),
            ("statics reversed", [.. statics.AsEnumerable().Reverse(), .. dynamicBodies]),
            ("mesh bodies first", [.. statics.OrderBy(i => capture.Bodies[i].Shapes.Any(s => s.Type == 3) ? 0 : 1).ThenBy(i => i), .. dynamicBodies]),
        };
        var finals = new List<byte[][]>();
        foreach (var (name, order) in orders)
        {
            var v = SettleValveWorld.Build(capture, table, module, order);
            for (var s = 0; s < Steps; s++)
                v.World.StepPhased(Dt, s == 0, _ => { });
            finals.Add(capture.BodiesEnd!.Select(b => new ReadOnlySpan<byte>(v.Bodies[b.Index].Rn, 0x260).ToArray()).ToArray());
            var exact = capture.BodiesEnd!.Select((b, k) => FinalDifference(finals[^1][k], b.State)).Count(d => d == null);
            output.WriteLine($"{name}: {exact}/{capture.BodiesEnd!.Count} bodies end as captured");
        }
        for (var o = 1; o < orders.Length; o++)
        {
            var same = 0;
            var largest = 0f;
            for (var k = 0; k < finals[0].Length; k++)
            {
                if (PhysicalDifference(finals[o][k], finals[0][k]) == null)
                {
                    same++;
                    continue;
                }
                var a = MemoryMarshal.Read<Vec3>(finals[0][k].AsSpan(0xfc));
                var b = MemoryMarshal.Read<Vec3>(finals[o][k].AsSpan(0xfc));
                var dx = a.X - b.X;
                var dy = a.Y - b.Y;
                var dz = a.Z - b.Z;
                largest = MathF.Max(largest, MathF.Sqrt(dx * dx + dy * dy + dz * dz));
            }
            output.WriteLine($"{orders[o].Name} vs captured order: {same}/{finals[0].Length} bodies identical, largest position difference {largest}");
        }
    }

    /// <summary>
    /// The port following Valve's world: read once, then both step; each
    /// step's awake bodies, contacts and islands must agree. A step where the
    /// port cannot go on or disagrees is read again from Valve so the rest can
    /// still be counted, and fails the test.
    /// </summary>
    [Fact]
    public void PortFollowsValve()
    {
        if (Load() is not var (capture, table, module))
            return;
        var v = SettleValveWorld.Build(capture, table, module);
        var hulls = new Dictionary<nint, RnHull>();
        var meshes = new Dictionary<nint, RnMesh>();
        var port = RnWorldReader.Read(module, v.World.Rn, hulls, meshes, broadphase: true);
        int exact = 0, resynced = 0, diverged = 0;
        var reasons = new Dictionary<string, int>();
        var readSteps = new List<int>();
        string? firstDivergence = null;
        for (var s = 0; s < Steps; s++)
        {
            v.World.StepPhased(Dt, s == 0, _ => { });
            string? problem;
            try
            {
                port.Step(Dt, s == 0);
                problem = Compare(v, port, s % 100 == 0 || s == Steps - 1 ? (hulls, meshes) : null);
                if (problem == null)
                {
                    exact++;
                    continue;
                }
                diverged++;
                firstDivergence ??= $"step {s}: {problem}";
                problem = "diverged";
            }
            catch (NotSupportedException e)
            {
                problem = e.Message;
                readSteps.Add(s);
            }
            reasons[problem] = reasons.GetValueOrDefault(problem) + 1;
            resynced++;
            port = RnWorldReader.Read(module, v.World.Rn, hulls, meshes, broadphase: true);
        }
        output.WriteLine($"{exact}/{Steps} steps exact from the previous step's state; {resynced} read again from Valve");
        foreach (var (reason, count) in reasons)
            output.WriteLine($"  {count} steps: {reason}");
        output.WriteLine($"  at steps {string.Join(", ", readSteps)}");
        output.WriteLine(firstDivergence == null ? "no divergence" : $"first divergence: {firstDivergence}");
        Assert.True(resynced == 0, $"{resynced} steps read again from Valve; first divergence: {firstDivergence}");
    }

    /// <summary>Awake bodies (state bytes), active contacts (keys, in order) and islands; the whole world line by line when <paramref name="full"/> (the reader's caches) is given.</summary>
    private static string? Compare(SettleValveWorld v, RnWorld port, (Dictionary<nint, RnHull>, Dictionary<nint, RnMesh>)? full)
    {
        var w = v.World.Rn;
        if (*(int*)(w + 0xa00) != port.ActiveBodies.Count)
            return $"{*(int*)(w + 0xa00)} awake bodies, ours {port.ActiveBodies.Count}";
        var awake = *(byte***)(w + 0xa08);
        for (var i = 0; i < port.ActiveBodies.Count; i++)
        {
            var body = port.ActiveBodies[i];
            if ((nint)awake[i] != body.Native)
                return $"awake body {i}";
            var ours = MemoryMarshal.AsBytes(new ReadOnlySpan<RnBodyState>(in body.State));
            if (StateDifference(ours, new ReadOnlySpan<byte>(awake[i], 0x260)) is { } d)
                return $"body {body.Index} at {d}";
        }
        if (*(int*)(w + 0xa10) != port.ActiveContacts.Count)
            return $"{*(int*)(w + 0xa10)} active contacts, ours {port.ActiveContacts.Count}";
        var contacts = *(byte***)(w + 0xa18);
        for (var i = 0; i < port.ActiveContacts.Count; i++)
            if (*(ulong*)(contacts[i] + 0x40) != port.ActiveContacts[i].Key)
                return $"active contact {i}";
        if (*(int*)(w + 0x118 + 0x20) != port.Islands.Serial.Count)
            return "serial islands";
        if (full is not var (hulls, meshes))
            return null;
        var module = Vphysics2Oracle.Load()!.Value;
        var valve = WorldSignature.Lines(RnWorldReader.Read(module, w, hulls, meshes));
        return WorldSignature.Diff(valve, WorldSignature.Lines(port));
    }

    /// <summary>
    /// The port's world built through its own creation calls
    /// (<see cref="SettleCapture.Build"/>) against Valve's built through the
    /// interface: every tree, node by node (a leaf's shape by the capture's
    /// shape id), and the dirty bits.
    /// </summary>
    [Fact]
    public void PortBuildsValvesBroadphase()
    {
        if (Load() is not var (capture, table, module))
            return;
        var v = SettleValveWorld.Build(capture, table, module);
        var port = capture.Build(table, *(ushort*)Vphysics2Oracle.At(module, 0x18045b2b8), out _);
        var valveShape = v.Shapes.ToDictionary(kv => (ulong)(kv.Key + 1) << 4, kv => (ulong)kv.Value);
        var bp = *(byte**)(v.World.Rn + 0x110);
        int nodes = 0, differing = 0;
        string? first = null;
        for (var t = 0; t < 7; t++)
        {
            var tree = port.Broadphase!.Trees[t];
            var o = tree.Nodes;
            var vt = bp + t * 0x58;
            var header = (*(int*)vt, *(int*)(vt + 4), *(int*)(vt + 8), *(int*)(vt + 0xc), *(int*)(vt + 0x10));
            if (header != (o.Root, o.LeafCount, o.NodeCount, o.Capacity, o.FreeHead))
            {
                output.WriteLine($"tree {t}: header {header} vs {(o.Root, o.LeafCount, o.NodeCount, o.Capacity, o.FreeHead)}");
                continue;
            }
            var free = new HashSet<int>();
            for (var i = o.FreeHead; i != -1; i = o.Nodes[i].Next)
                free.Add(i);
            var valveNodes = *(TreeNode**)(vt + 0x18);
            for (var i = 0; i < o.Capacity; i++)
            {
                if (free.Contains(i))
                    continue;
                nodes++;
                var ours = o.Nodes[i];
                if (ours.Child1 == -1)
                    ours.Shape = valveShape[ours.Shape];
                var theirs = valveNodes[i];
                if (MemoryMarshal.AsBytes(new ReadOnlySpan<TreeNode>(in ours)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<TreeNode>(in theirs))))
                    continue;
                differing++;
                first ??= $"tree {t} node {i}: ours {ours.Box} valve {theirs.Box}";
            }
            if (!tree.LeafBits.SequenceEqual(new ReadOnlySpan<uint>(*(uint**)(vt + 0x38), *(int*)(vt + 0x30)).ToArray()))
                output.WriteLine($"tree {t}: dirty bits differ");
        }
        output.WriteLine($"{nodes} nodes, {differing} differ{(first != null ? $"; first: {first}" : "")}");
    }
}
