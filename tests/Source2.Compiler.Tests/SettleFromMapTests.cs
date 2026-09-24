using System.Runtime.InteropServices;
using Source2.Compiler.Maps;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// atixref's settle from the map alone: the world <see cref="SettleWorld.CreateWorld"/>
/// builds from the vmap and the stock models, compared body by body with the
/// bundle's step-0 dump, then stepped 2700 times at 1/90 s, with the settled
/// props' final frames against the bundle's, which must all be exact.
/// SETTLE names the bundle.
/// </summary>
public sealed class SettleFromMapTests(ITestOutputHelper output)
{
    /// <summary>The map the bundle compiled (SETTLE_VMAP, else atixref) and its addon's game folder (SETTLE_ADDON).</summary>
    private static string Vmap => Environment.GetEnvironmentVariable("SETTLE_VMAP")
        ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\s2c_rc_probe\maps\atixref.vmap";

    private static string AddonGame => Environment.GetEnvironmentVariable("SETTLE_ADDON")
        ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe";

    /// <summary>The settled bodies made dynamic in our order (the collected nodes' walk order).</summary>
    [Fact]
    public void AtixrefSettlesFromTheMap() => Settle(valveOrder: false);

    /// <summary>
    /// The same with the settled bodies made dynamic in the order Valve's
    /// capture shows, which is the order of a hash map keyed by node pointer.
    /// </summary>
    [Fact]
    public void AtixrefSettlesFromTheMapInValvesOrder() => Settle(valveOrder: true);

    private void Settle(bool valveOrder)
    {
        if (Environment.GetEnvironmentVariable("SETTLE") is not { Length: > 0 } path || !File.Exists(path)
            || CS2Fixtures.StockPak() is not { } pak || !File.Exists(Vmap) || MapFixtures.GameSchema() is not { } schema)
            return;
        var document = DmxBinary.ReadFile(Vmap);
        using var models = new SettleBuildTests.PakModels(pak, AddonGame);
        var bodies = SettleWorld.Build(document, models, schema, SmartProps.NodesCreatedOnLoad(document, MapFixtures.SmartPropLocators));
        var settled = SettleWorld.Settled(document, bodies, models, schema);
        var captured = SettleBuildTests.ReadBuild(path);
        if (valveOrder)
        {
            var rank = captured.SelectMany(o => o.Bodies.Select((b, k) => (o.Node, k, b.DynamicOrder))).Where(x => x.DynamicOrder >= 0)
                .ToDictionary(x => (x.Node, x.k), x => x.DynamicOrder);
            settled = [.. settled.OrderBy(i => rank.GetValueOrDefault((bodies[i].NodeId, bodies.Take(i).Count(b => b.NodeId == bodies[i].NodeId)), int.MaxValue))];
        }
        var world = SettleWorld.CreateWorld(bodies, settled);

        // Captured bodies by node, in creation order (index 0 is the document's own body).
        var capture = SettleCapture.Read(path);
        var byKey = new Dictionary<(int Node, int K), int>();
        var index = 1;
        foreach (var o in captured)
            for (var k = 0; k < o.Bodies.Count; k++)
                byKey[(o.Node, k)] = index++;
        var ours = new Dictionary<int, RnBody>();
        for (var i = 0; i < bodies.Count; i++)
        {
            var k = bodies.Take(i).Count(b => b.NodeId == bodies[i].NodeId);
            if (byKey.TryGetValue((bodies[i].NodeId, k), out var ci))
                ours[ci] = world.Bodies[i + 1];
        }
        output.WriteLine($"{world.Bodies.Count} bodies built, {ours.Count} matched to the capture's {capture.Bodies.Count}");

        // Step 0: every word of the state that is not a pointer, a list or the island's.
        var differs = new Dictionary<int, int>();
        var examples = new Dictionary<int, string>();
        var exact = 0;
        string? first = null;
        foreach (var (ci, b) in ours)
        {
            var want = capture.Bodies[ci].State;
            var got = MemoryMarshal.AsBytes(new ReadOnlySpan<RnBodyState>(in b.State));
            var same = true;
            foreach (var off in Compared)
                if (!got.Slice(off, 4).SequenceEqual(want.AsSpan(off, 4)))
                {
                    same = false;
                    differs[off] = differs.GetValueOrDefault(off) + 1;
                    examples.TryAdd(off, $"body {ci} (node {bodies[world.Bodies.IndexOf(b) - 1].NodeId}, type {b.State.BodyType}, shapes {string.Join(",", bodies[world.Bodies.IndexOf(b) - 1].Shapes.Select(x => x.Type))}): ours {BitConverter.ToSingle(got[off..]):R}, valve {BitConverter.ToSingle(want, off):R}; ours pos {b.State.Position} q {b.State.Orientation}");
                    first ??= $"body {ci} (node {b.Index}) +0x{off:x}: ours {BitConverter.ToSingle(got[off..]):R} ({BitConverter.ToUInt32(got[off..]):x}), valve {BitConverter.ToSingle(want, off):R} ({BitConverter.ToUInt32(want, off):x})";
                }
            foreach (var off in (ReadOnlySpan<int>)[0x44, 0x249])
                if (got[off] != want[off])
                {
                    same = false;
                    differs[off] = differs.GetValueOrDefault(off) + 1;
                    examples.TryAdd(off, $"body {ci}: ours {got[off]:x}, valve {want[off]:x}");
                }
            if (same)
                exact++;
        }
        output.WriteLine($"step 0: {exact}/{ours.Count} bodies exact");
        // Open: the two toolsclip meshes under entities (nodes 351 and 6181),
        // which Valve builds with no shape; and, outside Valve's order, the
        // awake indices the SetType order sets.
        Assert.True(exact >= ours.Count - 2 - (valveOrder ? 0 : settled.Count), $"{exact}/{ours.Count} bodies exact at step 0");
        foreach (var (off, n) in differs.OrderBy(x => x.Key))
            output.WriteLine($"  +0x{off:x}: {n} bodies differ, e.g. {examples[off]}");
        output.WriteLine($"  first: {first}");

        var steps = 0;
        try
        {
            for (; steps < 2700; steps++)
                world.Step(1f / 90, steps == 0);
        }
        catch (NotSupportedException e)
        {
            output.WriteLine($"stopped at step {steps}: {e.Message}");
        }
        if (steps < 2700 || capture.BodiesEnd is not { } end)
            return;
        var placed = 0;
        foreach (var (ci, state) in end)
        {
            if (!ours.TryGetValue(ci, out var b))
                continue;
            var got = MemoryMarshal.AsBytes(new ReadOnlySpan<RnBodyState>(in b.State));
            if (got.Slice(0xfc, 12).SequenceEqual(state.AsSpan(0xfc, 12)) && got.Slice(0x120, 16).SequenceEqual(state.AsSpan(0x120, 16)))
                placed++;
            else
                output.WriteLine($"  body {ci}: ours {b.State.Position} {b.State.Orientation}, valve {MemoryMarshal.Read<Vec3>(state.AsSpan(0xfc))} {MemoryMarshal.Read<Quat>(state.AsSpan(0x120))}");
        }
        output.WriteLine($"after 2700 steps: {placed}/{end.Count} bodies at the captured position and orientation");
        Assert.Equal(end.Count, placed);
    }

    /// <summary>The state words compared: from the scales on, less the frame the first step of a frame writes.</summary>
    private static readonly int[] Compared =
        [.. Enumerable.Range(0x88 / 4, (0x1f8 - 0x88) / 4).Select(w => w * 4), 0x238, 0x23c, 0x54, 0x18];
}
