using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Source2.Compiler.Maps;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The aggregate build (SettleWorld.Aggregate.cs) against Valve's settle of
/// s2c_ragdollprobe: per CT agent's part, vphysics2's placement (the body as
/// the aggregate event caught it), resourcecompiler's pose (the SetTransform
/// it logged) and the body and its shapes at step 0 (the world dump), field
/// by field. RAGDOLL is the capture's events.jsonl (RagdollIslandsCaptureTests);
/// the map is read from the content addon s2c_rc_probe.
/// </summary>
public sealed class AggregateBuildCaptureTests(ITestOutputHelper output)
{
    private static string Vmap => Environment.GetEnvironmentVariable("RAGDOLL_VMAP")
        ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\s2c_rc_probe\maps\s2c_ragdollprobe.vmap";

    private static string AddonGame => @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe";

    private sealed record Aggregate(byte[] Xf, List<(string Wrapper, string Rn, byte[] Built)> Bodies);

    [Fact]
    public void BodiesEqualTheCapture()
    {
        if (Environment.GetEnvironmentVariable("RAGDOLL") is not { } path || !File.Exists(path)
            || CS2Fixtures.StockPak() is not { } pak || !File.Exists(Vmap))
            return;
        var aggregates = new List<Aggregate>();
        var xfs = new Dictionary<string, byte[]>();
        var states = new Dictionary<string, byte[]>();
        List<byte[]>? joints = null;
        var shapes = new Dictionary<string, List<byte[]>>();
        foreach (var line in File.ReadLines(path))
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            var ev = e.GetProperty("ev").GetString();
            if (ev == "world" && joints == null)
            {
                // The world dump comes before the first step's joint list: keep reading for it.
            }
            if (ev == "aggregate")
                aggregates.Add(new Aggregate(Convert.FromHexString(e.GetProperty("xf").GetString()!),
                    [.. e.GetProperty("bodies").EnumerateArray().Select(b => (b.GetProperty("wrapper").GetString()!, b.GetProperty("rn").GetString()!,
                                                                             Convert.FromHexString(b.GetProperty("body").GetString()!)))]));
            else if (ev == "build" && e.GetProperty("kind").GetString() == "xf")
                xfs[e.GetProperty("wrapper").GetString()!] = Convert.FromHexString(e.GetProperty("xf").GetString()!);
            else if (ev == "joints" && joints == null)
                joints = [.. e.GetProperty("joints").EnumerateArray().Select(j => Convert.FromHexString(j.GetProperty("raw").GetString()!))];
            else if (ev == "world")
            {
                foreach (var b in e.GetProperty("bodies").EnumerateArray())
                {
                    var ptr = b.GetProperty("ptr").GetString()!;
                    states[ptr] = Convert.FromHexString(b.GetProperty("body").GetString()!);
                    shapes[ptr] = [.. b.GetProperty("shapes").EnumerateArray().Select(s => Convert.FromHexString(s.GetProperty("head").GetString()!))];
                }
            }
            if (states.Count > 0 && joints != null)
                break;
        }

        using var models = new SettleBuildTests.PakModels(pak, AddonGame);
        var document = DmxBinary.ReadFile(Vmap);
        var schema = MapFixtures.GameSchema();
        var bodies = SettleWorld.Build(document, models, schema);
        var world = SettleWorld.CreateWorld(bodies, SettleWorld.Settled(document, bodies, models, schema));
        var ours = bodies.Select(b => b.Aggregate).OfType<SettleWorld.AggregateBuild>().Distinct().ToList();
        Assert.Equal(aggregates.Count, ours.Count);

        // RAGDOLL_SKELETON=<file>: the agent's skeleton as JSON (names, parents, positions, rotations), for offline checks.
        if (Environment.GetEnvironmentVariable("RAGDOLL_SKELETON") is { Length: > 0 } dump
            && models.ModelSkeleton("agents/models/ctm_sas/ctm_sas.vmdl") is { } bones)
            File.WriteAllText(dump, JsonSerializer.Serialize(new
            {
                names = bones.Names, parents = bones.Parents,
                pos = bones.Positions.Select(v => new[] { v.X, v.Y, v.Z }), rot = bones.Rotations.Select(q => new[] { q.X, q.Y, q.Z, q.W }),
                physBones = ours[0].Phys.BoneNames,
            }));
        var counts = new Dictionary<string, int>();
        var examples = new Dictionary<string, string>();
        void Miss(string what, string text)
        {
            counts[what] = counts.GetValueOrDefault(what) + 1;
            examples.TryAdd(what, text);
        }
        var fields = typeof(RnBodyState).GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.FieldType != typeof(nint) && f.GetCustomAttribute<FieldOffsetAttribute>() != null).ToList();
        var compared = 0;
        foreach (var theirs in aggregates)
        {
            var px = BitConverter.ToSingle(theirs.Xf, 0);
            var py = BitConverter.ToSingle(theirs.Xf, 4);
            var a = ours.Single(o => o.Part.Position.X == px && o.Part.Position.Y == py);
            var partXf = new byte[32];
            MemoryMarshal.Write(partXf, a.Part.Position);
            BitConverter.TryWriteBytes(partXf.AsSpan(12), a.Part.Scale);
            MemoryMarshal.Write(partXf.AsSpan(16), a.Part.Rotation);
            if (!partXf.AsSpan().SequenceEqual(theirs.Xf))
                Miss("part xf", $"{Convert.ToHexString(theirs.Xf)} ours {Convert.ToHexString(partXf)}");
            for (var i = 0; i < theirs.Bodies.Count; i++)
            {
                var (wrapper, rn, built) = theirs.Bodies[i];
                var (_, _, q) = SettleWorld.VphysicsPlacement(a, i);
                if (BitConverter.ToSingle(built, 0x120) != q.X || BitConverter.ToSingle(built, 0x12c) != q.W)
                    Miss("vphysics2 placement", $"part {i}: {BitConverter.ToSingle(built, 0x120):R} ours {q.X:R}");
                var xf = SettleWorld.RcPlacement(a, i);
                var mine = new byte[32];
                MemoryMarshal.Write(mine, xf.Position);
                BitConverter.TryWriteBytes(mine.AsSpan(12), xf.Scale);
                MemoryMarshal.Write(mine.AsSpan(16), xf.Rotation);
                if (xfs.TryGetValue(wrapper, out var logged) && !logged.AsSpan().SequenceEqual(mine))
                    Miss("rc pose", $"part {i}: {Convert.ToHexString(logged)} ours {Convert.ToHexString(mine)}");
                var body = world.Bodies[1 + a.Bodies[i]];
                var state = MemoryMarshal.AsBytes(new ReadOnlySpan<RnBodyState>(in body.State)).ToArray();
                var captured = states[rn];
                foreach (var f in fields)
                {
                    var at = f.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
                    var size = Marshal.SizeOf(f.FieldType);
                    if (at + size > captured.Length || at + size > state.Length)
                        continue;
                    if (!captured.AsSpan(at, size).SequenceEqual(state.AsSpan(at, size)))
                        Miss($"body {f.Name}", $"part {i}: {Convert.ToHexString(captured, at, size)} ours {Convert.ToHexString(state, at, size)}");
                }
                for (var k = 0; k < body.Shapes.Count && k < shapes[rn].Count; k++)
                {
                    var attributes = MemoryMarshal.AsBytes(new ReadOnlySpan<CollisionAttributes>(in body.Shapes[k].Proxy.Attributes)).ToArray();
                    var head = shapes[rn][k];
                    for (var o = 0; o < 0x28; o += 8)
                        if (!head.AsSpan(0x50 + o, 8).SequenceEqual(attributes.AsSpan(o, 8)))
                            Miss($"shape attributes +0x{0x50 + o:x}", $"part {i} shape {k}: {Convert.ToHexString(head, 0x50 + o, 8)} ours {Convert.ToHexString(attributes, o, 8)}");
                }
                compared++;
            }
        }
        // The joints at step 0, world order, every byte but the pointers (vtable, world, bodies,
        // island, edge links, the wrapper at +0xc8, the name at +0xd8).
        Assert.NotNull(joints);
        Assert.Equal(joints!.Count, world.Joints[0].Count);
        // Pointers, and padding no constructor writes: after the +0x2c byte, +0x42..+0x47 (FUN_18006c020 returns
        // its argument), +0xb4, after the bools at +0x100 / +0x114 / +0x120, past the conical joint's last field.
        bool Pointer(int o) => o < 0x8 || (o >= 0x10 && o < 0x28) || (o >= 0x30 && o < 0x38) || (o >= 0x48 && o < 0x68)
                              || (o >= 0xc8 && o < 0xe0) || (o >= 0x2d && o < 0x30) || (o >= 0x42 && o < 0x48) || (o >= 0xb4 && o < 0xb8)
                              || (o >= 0x101 && o < 0x104) || (o >= 0x115 && o < 0x118) || (o >= 0x121 && o < 0x124) || o >= 0x14c;
        for (var k = 0; k < joints.Count; k++)
        {
            var j = world.Joints[0][k];
            var mine = j.Type == RnJoint.ConicalType ? MemoryMarshal.AsBytes(new ReadOnlySpan<ConicalJointState>(in j.Conical)).ToArray()
                                                     : MemoryMarshal.AsBytes(new ReadOnlySpan<RevoluteJointState>(in j.Revolute)).ToArray();
            for (var o = 0; o < mine.Length; o++)
                if (!Pointer(o) && joints[k][o] != mine[o])
                {
                    var at = o & ~3;
                    Miss($"joint type {j.Type} +0x{at:x}", $"joint {k}: {Convert.ToHexString(joints[k], at, 4)} ours {Convert.ToHexString(mine, at, 4)}");
                    o = at + 3;
                }
        }
        foreach (var (what, n) in counts.OrderBy(c => c.Key))
            output.WriteLine($"{what}: {n} ({examples[what]})");
        output.WriteLine($"{compared} bodies compared");
        Assert.Empty(counts);
    }
}
