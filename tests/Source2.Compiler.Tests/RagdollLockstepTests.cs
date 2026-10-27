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
/// starts: steps 0..90, then every 10th) every ragdoll body compared with
/// Valve's bytes, field by field (every modelled field of RnBodyState, less
/// pointers). The first difference is reported. RAGDOLL is the bundle
/// capture's events.jsonl (AggregateBuildCaptureTests).
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
        var aggregates = new List<(float X, float Y, List<string> Rn)>();
        var steps = new SortedDictionary<int, List<(int Index, byte[] Body)>>();
        foreach (var line in File.ReadLines(path))
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            switch (e.GetProperty("ev").GetString())
            {
                case "world" when worldPtrs == null:
                    worldPtrs = [.. e.GetProperty("bodies").EnumerateArray().Select(b => b.GetProperty("ptr").GetString()!)];
                    break;
                case "aggregate":
                    var xf = Convert.FromHexString(e.GetProperty("xf").GetString()!);
                    aggregates.Add((BitConverter.ToSingle(xf, 0), BitConverter.ToSingle(xf, 4),
                                    [.. e.GetProperty("bodies").EnumerateArray().Select(b => b.GetProperty("rn").GetString()!)]));
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
        var world = SettleWorld.CreateWorld(bodies, SettleWorld.Settled(document, bodies, models, schema));
        var ours = bodies.Select(b => b.Aggregate).OfType<SettleWorld.AggregateBuild>().Distinct().ToList();

        // Valve's world index -> our body, for every ragdoll part.
        var byIndex = new Dictionary<int, (RnBody Body, string Name)>();
        foreach (var (x, y, rn) in aggregates)
        {
            var a = ours.Single(o => o.Part.Position.X == x && o.Part.Position.Y == y);
            for (var i = 0; i < rn.Count; i++)
                byIndex[worldPtrs!.IndexOf(rn[i])] = (world.Bodies[1 + a.Bodies[i]], $"agent at ({x}, {y}) part {i}");
        }

        var dynamic = steps.Values.First().Select(c => c.Index).ToHashSet();
        output.WriteLine($"{byIndex.Count} ragdoll bodies, {byIndex.Keys.Count(dynamic.Contains)} dynamic in Valve's settle; not: "
                         + string.Join(", ", byIndex.Where(b => !dynamic.Contains(b.Key)).Select(b => b.Value.Name).Distinct().Take(4)));
        // Valve's dynamic set is ours: a ragdoll body dynamic in one world and not the other is a difference.
        var typeMismatch = byIndex.Where(b => (b.Value.Body.State.BodyType == 2) != dynamic.Contains(b.Key)).Select(b => b.Value.Name).ToList();
        Assert.True(typeMismatch.Count == 0, "dynamic in one settle only: " + string.Join(", ", typeMismatch));
        // RAGDOLL_CONTACTS=1: our active contacts touching a ragdoll body before each collide of steps 90..100.
        var watchStep = -1;
        if (Environment.GetEnvironmentVariable("RAGDOLL_CONTACTS") == "1")
        {
            var names = byIndex.Values.ToDictionary(v => v.Body, v => v.Name);
            world.Observe = (w, phase) =>
            {
                if (phase != StepPhase.BeforeCollide || watchStep < 90 || watchStep > 100)
                    return;
                var touching = w.ActiveContacts.Where(c => names.ContainsKey(c.A.Body) || names.ContainsKey(c.B.Body))
                    .Select(c => $"{(names.TryGetValue(c.A.Body, out var na) ? na : $"body {c.A.Body.Index}")} / {(names.TryGetValue(c.B.Body, out var nb) ? nb : $"body {c.B.Body.Index}")} t{c.TouchState}");
                output.WriteLine($"step {watchStep} contacts: {string.Join("; ", touching)}");
            };
        }
        string? first = null;
        var last = steps.Keys.Max();
        for (var s = 0; s <= last && first == null; s++)
        {
            if (steps.TryGetValue(s, out var captured))
            {
                var exact = 0;
                foreach (var (index, body) in captured)
                {
                    if (!byIndex.TryGetValue(index, out var mine))
                        continue;
                    var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<RnBodyState>(in mine.Body.State)).ToArray();
                    var differs = Fields.FirstOrDefault(f => !body.AsSpan(f.At, f.Size).SequenceEqual(bytes.AsSpan(f.At, f.Size)));
                    if (differs.Name == null)
                        exact++;
                    else if (first == null)
                    {
                        foreach (var f in Fields.Where(f => !body.AsSpan(f.At, f.Size).SequenceEqual(bytes.AsSpan(f.At, f.Size))))
                            output.WriteLine($"  {mine.Name} {f.Name}: {Convert.ToHexString(body, f.At, f.Size)} ours {Convert.ToHexString(bytes, f.At, f.Size)}");
                    }
                    if (differs.Name != null)
                        first ??= $"before step {s}, {mine.Name} (Valve's body {index}) {differs.Name}: {Convert.ToHexString(body, differs.At, differs.Size)} ours {Convert.ToHexString(bytes, differs.At, differs.Size)}";
                }
                output.WriteLine($"before step {s}: {exact} of {captured.Count(c => byIndex.ContainsKey(c.Index))} dynamic ragdoll bodies exact");
            }
            watchStep = s;
            world.Step(1f / 90, s == 0);
        }
        Assert.True(first == null, first);
    }
}
