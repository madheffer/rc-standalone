using System.Runtime.InteropServices;
using System.Text.Json;
using Source2.Compiler.Maps;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// s2c_ragdollprobe's settle in lockstep with Valve's (PHYSICS.md "Aggregates in
/// the settle" step 6): our world built from the .vmap, stepped at 1/90 s, and
/// before each step the capture recorded ("bodies", taken as the step's Collide
/// starts: steps 0..90, then every 10th, every step with settle_fine_steps.js)
/// every dynamic body compared with Valve's, field by field (every modelled
/// field of RnBodyState, less pointers), and the final poses after the settle.
/// Bodies turn dynamic in the capture's order (GROUND_TRUTH 52). With
/// settle_contacts.js in the capture every active contact is compared too
/// (GROUND_TRUTH 167). The first difference is reported. RAGDOLL is the
/// capture's events.jsonl (settle_bundle.js, settle_joints.js and the others).
/// </summary>
public sealed class RagdollLockstepTests(ITestOutputHelper output)
{
    private static string Vmap => Environment.GetEnvironmentVariable("RAGDOLL_VMAP")
        ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\s2c_rc_probe\maps\s2c_ragdollprobe.vmap";

    private static string AddonGame => @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe";

    // The body's modelled fields (as AggregateBuildCaptureTests compares them): every field with an offset, less pointers.
    private static readonly (string Name, int At, int Size)[] Fields = [.. typeof(RnBodyState)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
        .Where(f => f.FieldType != typeof(nint) && f.GetCustomAttributes(typeof(FieldOffsetAttribute), false).Length > 0)
        .Select(f => (f.Name, ((FieldOffsetAttribute)f.GetCustomAttributes(typeof(FieldOffsetAttribute), false)[0]).Value, Marshal.SizeOf(f.FieldType)))
        .Where(f => f.Item2 + f.Item3 <= 0x280)
        .OrderBy(f => f.Item2)];

    [Fact]
    public void SettleStepsAsValves()
    {
        if (Environment.GetEnvironmentVariable("RAGDOLL") is not { } path || !File.Exists(path)
            || CS2Fixtures.StockPak() is not { } pak || !File.Exists(Vmap))
            return;
        // The capture: world body pointers in list order, each aggregate's part pointers and
        // placement, and the dynamic bodies' bytes per step.
        List<string>? worldPtrs = null;
        List<int[]>? worldProxies = null;
        // The build log: each body wrapper's CRnBody, and the SetType calls in order.
        var rnOf = new Dictionary<string, string>();
        var setTypes = new List<(string Wrapper, int Type)>();
        var aggregates = new List<(float X, float Y, float Z, List<string> Rn)>();
        var steps = new SortedDictionary<int, List<(int Index, byte[] Body)>>();
        List<(int Index, byte[] Body)>? end = null;
        // settle_contacts.js: the active contacts at Collide's entry and exit, per step.
        var contactsIn = new Dictionary<int, List<Contact>>();
        var contactsOut = new Dictionary<int, List<Contact>>();
        // A capture of a whole compile goes on past the settle (later phases build aggregates again):
        // only what comes before "settle_done" is the settle's.
        var settleDone = false;
        foreach (var line in File.ReadLines(path))
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            var ev = e.GetProperty("ev").GetString();
            if (ev == "settle_done")
                settleDone = true;
            if (settleDone && ev is "build" or "aggregate")
                continue;
            switch (ev)
            {
                case "world" when worldPtrs == null:
                    worldPtrs = [.. e.GetProperty("bodies").EnumerateArray().Select(b => b.GetProperty("ptr").GetString()!)];
                    // Each shape's proxy (CRnShape +0x1c: tree leaf * 8 | tree) as the world dump caught it.
                    worldProxies = [.. e.GetProperty("bodies").EnumerateArray().Select(b => b.GetProperty("shapes").EnumerateArray()
                        .Select(sh => BitConverter.ToInt32(Convert.FromHexString(sh.GetProperty("head").GetString()!), 0x1c)).ToArray())];
                    break;
                case "build" when e.GetProperty("kind").GetString() == "body":
                    rnOf[e.GetProperty("wrapper").GetString()!] = e.GetProperty("rn").GetString()!;
                    break;
                case "build" when e.GetProperty("kind").GetString() == "type":
                    setTypes.Add((e.GetProperty("wrapper").GetString()!, e.GetProperty("type").GetInt32()));
                    break;
                case "aggregate":
                    foreach (var b in e.GetProperty("bodies").EnumerateArray())
                        rnOf[b.GetProperty("wrapper").GetString()!] = b.GetProperty("rn").GetString()!;
                    var xf = Convert.FromHexString(e.GetProperty("xf").GetString()!);
                    aggregates.Add((BitConverter.ToSingle(xf, 0), BitConverter.ToSingle(xf, 4), BitConverter.ToSingle(xf, 8),
                                    [.. e.GetProperty("bodies").EnumerateArray().Select(b => b.GetProperty("rn").GetString()!)]));
                    break;
                case "contacts_in" or "contacts_out":
                    (e.GetProperty("ev").GetString() == "contacts_in" ? contactsIn : contactsOut)[e.GetProperty("step").GetInt32()] =
                        [.. e.GetProperty("contacts").EnumerateArray().Select(c => new Contact(ulong.Parse(c.GetProperty("key").GetString()!),
                            c.GetProperty("a").GetInt32(), c.GetProperty("b").GetInt32(), Convert.FromHexString(c.GetProperty("raw").GetString()!),
                            Convert.FromHexString(c.GetProperty("block").GetString()!)))];
                    break;
                case "bodies_end":
                    var endList = e.GetProperty("bodies");
                    using (var parsed = JsonDocument.Parse(endList.ValueKind == JsonValueKind.String ? endList.GetString()!.Replace('\'', '"') : endList.GetRawText()))
                        end = [.. parsed.RootElement.EnumerateArray().Select(b => (b[0].GetInt32(), Convert.FromHexString(b[1].GetString()!)))];
                    break;
                case "bodies":
                    var list = e.GetProperty("bodies");
                    using (var parsed = JsonDocument.Parse(list.ValueKind == JsonValueKind.String ? list.GetString()!.Replace('\'', '"') : list.GetRawText()))
                        steps[e.GetProperty("step").GetInt32()] = [.. parsed.RootElement.EnumerateArray()
                            .Select(b => (b[0].GetInt32(), Convert.FromHexString(b[1].GetString()!)))];
                    break;
            }
        }
        Assert.NotNull(worldPtrs);

        using var models = new SettleBuildTests.PakModels(pak, AddonGame);
        var document = DmxBinary.ReadFile(Vmap);
        var schema = MapFixtures.GameSchema();
        var bodies = SettleWorld.Build(document, models, schema);
        var settled = SettleWorld.Settled(document, bodies, models, schema);
        // Valve makes bodies dynamic in its node hash table's order, over heap pointers, which varies run
        // to run (GROUND_TRUTH 52): the world here takes the capture's order (its SetType(2) calls; world
        // index = 1 + build index). RAGDOLL_OUR_ORDER=1 keeps ours.
        if (Environment.GetEnvironmentVariable("RAGDOLL_OUR_ORDER") != "1" && setTypes.Count > 0)
        {
            var valveOrder = setTypes.Where(t => t.Type == 2 && rnOf.ContainsKey(t.Wrapper))
                .Select(t => worldPtrs!.IndexOf(rnOf[t.Wrapper]) - 1).Where(settled.Contains).Distinct().ToList();
            Assert.Equal(settled.Count, valveOrder.Count);
            output.WriteLine($"made dynamic in Valve's order: {string.Join(", ", valveOrder.Select(i => i + 1))}");
            settled = valveOrder;
        }
        var world = SettleWorld.CreateWorld(bodies, settled);
        var ours = bodies.Select(b => b.Aggregate).OfType<SettleWorld.AggregateBuild>().Distinct().ToList();

        // Valve's world index -> our body, for every ragdoll part.
        var byIndex = new Dictionary<int, (RnBody Body, string Name)>();
        foreach (var (x, y, z, rn) in aggregates)
        {
            var a = ours.Single(o => o.Part.Position.X == x && o.Part.Position.Y == y && o.Part.Position.Z == z);
            for (var i = 0; i < rn.Count; i++)
                byIndex[worldPtrs!.IndexOf(rn[i])] = (world.Bodies[1 + a.Bodies[i]], $"agent at ({x}, {y}) part {i}");
        }

        output.WriteLine("Valve index -> our index: " + string.Join(", ", byIndex.OrderBy(b => b.Key).Take(6).Select(b => $"{b.Key}->{b.Value.Body.Index}")));
        var dynamic = steps.Values.First().Select(c => c.Index).ToHashSet();
        // The other dynamic bodies (props), at the same world index in ours.
        var others = dynamic.Where(i => !byIndex.ContainsKey(i)).ToDictionary(i => i, i => (world.Bodies[i],
            $"body {i} ({bodies[i - 1].Node?.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname")} {bodies[i - 1].Node?.Get<DmxBinary.Element>("entity_properties")?.Get<string>("model")} node {bodies[i - 1].NodeId})"));
        output.WriteLine($"{byIndex.Count} ragdoll bodies, {byIndex.Keys.Count(dynamic.Contains)} dynamic in Valve's settle; not: "
                         + string.Join(", ", byIndex.Where(b => !dynamic.Contains(b.Key)).Select(b => b.Value.Name).Distinct().Take(4)));
        // Valve's dynamic set is ours: a ragdoll body dynamic in one world and not the other is a difference.
        var typeMismatch = byIndex.Where(b => (b.Value.Body.State.BodyType == 2) != dynamic.Contains(b.Key)).Select(b => b.Value.Name).ToList();
        Assert.True(typeMismatch.Count == 0, "dynamic in one settle only: " + string.Join(", ", typeMismatch));
        // With a settle_contacts.js capture: every active contact, at our BeforeCollide against
        // Valve's Collide entry and at our Collided against its exit (manifolds only: Valve's exit is after
        // the flush). The first difference of each step is printed.
        var watchStep = -1;
        string? firstContact = null;
        world.Observe = (w, phase) =>
        {
            // The world dump is taken at step 0's first collide (settle_bundle.js onStep): every shape's proxy there.
            if (phase == StepPhase.BeforeCollide && watchStep == 0 && worldProxies != null)
            {
                var proxyMisses = new List<string>();
                for (var i = 0; i < worldProxies.Count && i < w.Bodies.Count; i++)
                    for (var k = 0; k < worldProxies[i].Length && k < w.Bodies[i].Shapes.Count; k++)
                        if (worldProxies[i][k] != w.Bodies[i].Shapes[k].Proxy.ProxyId)
                            proxyMisses.Add($"body {i} shape {k}: 0x{worldProxies[i][k]:x} ours 0x{w.Bodies[i].Shapes[k].Proxy.ProxyId:x}");
                output.WriteLine($"step 0 proxies: {proxyMisses.Count} differ; " + string.Join("; ", proxyMisses.Take(16)));
            }
            var theirs = phase == StepPhase.BeforeCollide ? contactsIn : contactsOut;
            if (!theirs.TryGetValue(watchStep, out var list))
                return;
            var where = $"step {watchStep} {(phase == StepPhase.BeforeCollide ? "collide in" : "collide out")}";
            var mine = w.ActiveContacts.ToDictionary(c => (c.Key, c.A.Body.Index, c.B.Body.Index, c.ChildA, c.ChildB));
            var ragdoll = list;
            var keys = ragdoll.Select(c => c.Id).ToHashSet();
            var extra = w.ActiveContacts.Where(c => !keys.Contains((c.Key, c.A.Body.Index, c.B.Body.Index, c.ChildA, c.ChildB))).ToList();
            var differences = new List<string>();
            foreach (var extraContact in extra)
                differences.Add($"ours only: key {extraContact.Key:x} (bodies {extraContact.A.Body.Index}, {extraContact.B.Body.Index}, children {extraContact.ChildA} {extraContact.ChildB}, touch {extraContact.TouchState})");
            foreach (var c in ragdoll)
            {
                if (!mine.TryGetValue(c.Id, out var o))
                {
                    differences.Add($"Valve's only: key {c.Key:x} (bodies {c.A}, {c.B}, children {c.Id.Item4} {c.Id.Item5}, touch {BitConverter.ToInt32(c.Raw, 0x48)})");
                    continue;
                }
                if (ContactDifference(c, o, phase == StepPhase.BeforeCollide) is { } d)
                    differences.Add($"key {c.Key:x} ({Name(c.A)} / {Name(c.B)}): {d}");
            }
            if (phase == StepPhase.BeforeCollide)
            {
                var order = list.Select(c => c.Id).ToList();
                var ourOrder = w.ActiveContacts.Select(c => (c.Key, c.A.Body.Index, c.B.Body.Index, c.ChildA, c.ChildB)).ToList();
                if (!order.SequenceEqual(ourOrder))
                    differences.Add($"active list order: {ourOrder.Count} ours, {order.Count} Valve's");
            }
            output.WriteLine($"{where}: {ragdoll.Count} contacts, {differences.Count} differences");
            foreach (var d in differences.OrderBy(d => d).Take(40))
                output.WriteLine("  " + d);
            if (differences.Count > 0)
                firstContact ??= $"{where}: {differences[0]}";
        };
        string Name(int index) => byIndex.TryGetValue(index, out var v) ? v.Name : $"body {index}";
        string? first = null;
        // Every step of the settle (2700 at 1/90 s), then the bodies after the last ("bodies_end").
        const int SettleSteps = 2700;
        if (end != null)
            steps[SettleSteps] = end;
        var last = steps.Keys.Max();
        for (var s = 0; s <= last && first == null; s++)
        {
            if (steps.TryGetValue(s, out var captured))
            {
                var exact = 0;
                foreach (var (index, body) in captured)
                {
                    if (!byIndex.TryGetValue(index, out var mine) && !others.TryGetValue(index, out mine))
                        continue;
                    var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<RnBodyState>(in mine.Body.State)).ToArray();
                    // After the settle Valve has torn the world down (bodies static, velocities cleared): only
                    // the pose the write-back reads is compared there.
                    var fields = s == SettleSteps ? Fields.Where(f => f.Name is "Position" or "Orientation").ToArray() : Fields;
                    var differs = fields.FirstOrDefault(f => !body.AsSpan(f.At, f.Size).SequenceEqual(bytes.AsSpan(f.At, f.Size)));
                    if (differs.Name == null)
                        exact++;
                    else if (first == null)
                    {
                        foreach (var f in fields.Where(f => !body.AsSpan(f.At, f.Size).SequenceEqual(bytes.AsSpan(f.At, f.Size))))
                            output.WriteLine($"  {mine.Name} {f.Name}: {Convert.ToHexString(body, f.At, f.Size)} ours {Convert.ToHexString(bytes, f.At, f.Size)}");
                    }
                    if (differs.Name != null)
                        first ??= $"before step {s}, {mine.Name} (Valve's body {index}) {differs.Name}: {Convert.ToHexString(body, differs.At, differs.Size)} ours {Convert.ToHexString(bytes, differs.At, differs.Size)}";
                }
                output.WriteLine($"{(s == SettleSteps ? "after the settle" : $"before step {s}")}: {exact} of {captured.Count} dynamic bodies exact");
            }
            if (s == SettleSteps)
                break;
            watchStep = s;
            world.Step(1f / 90, s == 0);
        }
        if (firstContact != null)
            output.WriteLine("first contact difference: " + firstContact);
        Assert.True(first == null, first);
    }

    private sealed record Contact(ulong Key, int A, int B, byte[] Raw, byte[] Block)
    {
        /// <summary>The sort key, the bodies' world indices and the compound children (+0x18, +0x1c); the key alone repeats.</summary>
        public (ulong, int, int, int, int) Id => (Key, A, B, BitConverter.ToInt32(Raw, 0x18), BitConverter.ToInt32(Raw, 0x1c));
    }

    /// <summary>
    /// A contact against ours: at the collide's entry the touch state (+0x48), the active index (+0x64) and
    /// the size (+0x98); always the manifolds (block +4 count, 0xe0 each from +8; the points' last two bytes
    /// are padding).
    /// </summary>
    private static string? ContactDifference(Contact c, RnContact o, bool entry)
    {
        if (entry)
        {
            if (BitConverter.ToInt32(c.Raw, 0x48) != o.TouchState)
                return $"touch state {BitConverter.ToInt32(c.Raw, 0x48)}, ours {o.TouchState}";
            if (BitConverter.ToInt32(c.Raw, 0x64) != o.ActiveIndex)
                return $"active index {BitConverter.ToInt32(c.Raw, 0x64)}, ours {o.ActiveIndex}";
            if (BitConverter.ToInt32(c.Raw, 0x98) != o.Size98)
                return $"size {BitConverter.ToInt32(c.Raw, 0x98)}, ours {o.Size98}";
        }
        var count = c.Block.Length < 8 ? 0 : BitConverter.ToInt32(c.Block, 4);
        if (count != o.Manifolds.Count)
            return $"{count} manifolds, ours {o.Manifolds.Count}";
        for (var m = 0; m < count; m++)
        {
            var mine = MemoryMarshal.AsBytes(new ReadOnlySpan<CachedManifold>(o.Manifolds[m]));
            var t = c.Block.AsSpan(8 + m * 0xe0, 0xe0);
            var length = 0x40 + o.Manifolds[m].PointCount * 0x28;
            for (var i = 0; i < length; i++)
                if ((i < 0x40 || (i - 0x40) % 0x28 < 0x26) && mine[i] != t[i])
                {
                    var at = i & ~3;
                    return $"manifold {m} +0x{i:x}: {Convert.ToHexString(t.Slice(at, 4))} ours {Convert.ToHexString(mine.Slice(at, 4))}";
                }
        }
        return null;
    }
}
