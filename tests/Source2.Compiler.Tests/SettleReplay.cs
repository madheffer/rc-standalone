using System.Runtime.InteropServices;
using System.Text.Json;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// atixref's settle, replayed by the port: the world Valve built (captured at
/// its first step, <see cref="SettleCapture"/>) stepped 2700 times at 1/90 s.
/// At every Collide of steps 0 to 40 the dynamic bodies and the active
/// contacts' order must be as captured, and after each collide worker every
/// captured contact's state; at the end the bodies are compared with those
/// of another run of the same compile.
///
/// <para>Set SETTLE to the capture's events.jsonl; without it the test does
/// nothing. A bundle capture brings the build sequence and body snapshots
/// (before steps, and after the last), a narrowphase capture the collide
/// events; for the latter SETTLE_END names the directory with b0.bin and
/// bend.bin (another run) for the final state, and for the former
/// SETTLE_CONTACTS the narrowphase capture the group table is read from.</para>
/// </summary>
public sealed class SettleReplay(ITestOutputHelper output)
{
    private const float Dt = 1f / 90;
    private const int Steps = 2700;

    [Fact]
    public void AtixrefSettleMatches()
    {
        if (Environment.GetEnvironmentVariable("SETTLE") is not { Length: > 0 } path || !File.Exists(path))
            return;
        // The collision group table (DAT_18045b328) is filled when the
        // physics interface starts up, which creating a world does.
        if (Vphysics2Oracle.Load() is not { } module || Vphysics2World.Create() is null)
            return;
        var capture = SettleCapture.Read(path);
        var world = Build(capture, module);
        var events = Events(path).GetEnumerator();
        var step = 0;
        var bodyExact = new List<(int Step, int Exact, int Of, bool Order, int Contacts, int ContactsExact)>();
        string? firstDifference = null;
        var dynamicBodies = world.Bodies.Where(b => b.State.BodyType == 2).ToList();
        world.Observe = (w, phase) =>
        {
            if (step > 40 || !events.MoveNext())
                return;
            var e = events.Current;
            if (phase == StepPhase.BeforeCollide)
            {
                Assert.Equal("collide_in", e.GetProperty("ev").GetString());
                var exact = 0;
                foreach (var b in e.GetProperty("bodies").EnumerateArray())
                {
                    var body = w.Bodies[b[0].GetInt32()];
                    var diff = BodyDifference(body.State, Convert.FromHexString(b[1].GetString()!));
                    if (diff == null)
                        exact++;
                    else
                        firstDifference ??= $"step {step} body {body.Index}: {diff}";
                }
                var order = e.GetProperty("order").EnumerateArray().Select(p => (p[0].GetInt32(), p[1].GetInt32())).ToList();
                var ours = w.ActiveContacts.Select(c => (Id(c.A), Id(c.B))).ToList();
                var sameOrder = order.SequenceEqual(ours);
                if (!sameOrder)
                    firstDifference ??= $"step {step}: active contacts {ours.Count}, captured {order.Count}; first differing at {Enumerable.Range(0, Math.Min(ours.Count, order.Count)).FirstOrDefault(i => ours[i] != order[i], -1)}";
                bodyExact.Add((step, exact, dynamicBodies.Count, sameOrder, 0, 0));
            }
            else
            {
                Assert.Equal("collide_out", e.GetProperty("ev").GetString());
                var byPair = w.ActiveContacts.ToDictionary(c => (Id(c.A), Id(c.B)));
                int count = 0, exact = 0;
                foreach (var c in e.GetProperty("contacts").EnumerateArray())
                {
                    count++;
                    var key = (c.GetProperty("s1").GetInt32(), c.GetProperty("s2").GetInt32());
                    var diff = byPair.TryGetValue(key, out var ours) ? ContactDifference(ours, c) : "missing";
                    if (diff == null)
                        exact++;
                    else
                        firstDifference ??= $"step {step} contact {key}: {diff}";
                }
                var last = bodyExact[^1];
                bodyExact[^1] = last with { Contacts = count, ContactsExact = exact };
                step++;
            }
        };

        var stepsDone = 0;
        var snapshots = new List<string>();
        try
        {
            for (var s = 0; s < Steps; s++)
            {
                if (s > 40)
                    world.Observe = null;
                if (capture.BodiesAt.TryGetValue(s, out var at))
                    snapshots.Add(Snapshot($"before step {s}", world, at, ref firstDifference));
                world.Step(Dt, s == 0);
                stepsDone++;
            }
        }
        catch (NotSupportedException e)
        {
            output.WriteLine($"stopped at step {stepsDone}: {e.Message}");
        }
        foreach (var (s, exact, of, order, contacts, contactsExact) in bodyExact)
            output.WriteLine($"step {s}: {exact}/{of} bodies exact, contact order {(order ? "same" : "differs")}, {contactsExact}/{contacts} contact updates exact");
        foreach (var line in snapshots)
            output.WriteLine(line);
        output.WriteLine(firstDifference == null ? "no difference in what was compared" : $"first difference: {firstDifference}");
        if (stepsDone == Steps)
        {
            if (capture.BodiesEnd is { } end)
            {
                output.WriteLine(Snapshot("after the last step", world, end, ref firstDifference));
                output.WriteLine($"  (first: {firstDifference})");
                // The settle zeroes velocities after its last step: the end
                // state is compared on position and orientation.
                var placed = 0;
                foreach (var (index, state) in end)
                {
                    var ours = MemoryMarshal.AsBytes(new ReadOnlySpan<RnBodyState>(in world.Bodies[index].State));
                    if (ours.Slice(0xfc, 12).SequenceEqual(state.AsSpan(0xfc, 12)) && ours.Slice(0x120, 16).SequenceEqual(state.AsSpan(0x120, 16)))
                        placed++;
                    else
                        output.WriteLine($"  body {index}: ours {world.Bodies[index].State.Position} {world.Bodies[index].State.Orientation}, captured {MemoryMarshal.Read<Vec3>(state.AsSpan(0xfc))} {MemoryMarshal.Read<Quat>(state.AsSpan(0x120))}");
                }
                output.WriteLine($"after the last step: {placed}/{end.Count} bodies at the captured position and orientation");
                Assert.Equal(end.Count, placed);
            }
            else
                CompareFinal(capture, world);
        }
    }

    /// <summary>The captured bodies against the port's: how many are exact (less +0x20..+0x47), and the first that is not.</summary>
    private static string Snapshot(string when, RnWorld world, List<(int Index, byte[] State)> captured, ref string? firstDifference)
    {
        var exact = 0;
        foreach (var (index, state) in captured)
        {
            var diff = BodyDifference(world.Bodies[index].State, state);
            if (diff == null)
                exact++;
            else
                firstDifference ??= $"{when}, body {index}: {diff}";
        }
        return $"{when}: {exact}/{captured.Count} bodies exact";
    }

    private RnWorld Build(SettleCapture capture, nint module)
    {
        unsafe
        {
            var table = new ReadOnlySpan<ushort>((ushort*)Vphysics2Oracle.At(module, 0x18045b328), 4096).ToArray();
            foreach (var ((a, b), flags) in GroupFlags(capture))
            {
                output.WriteLine($"collision groups {a} and {b}: contacts carry flags {flags:x}; the table here had {table[a * 64 + b]:x}");
                table[a * 64 + b] = table[b * 64 + a] = flags;
            }
            var world = capture.Build(table, *(ushort*)Vphysics2Oracle.At(module, 0x18045b2b8), out var report);
            output.WriteLine($"{world.Bodies.Count} bodies, {report.Shapes} shapes; {report.Mismatches} proxy ids or body indices differ from the capture{(report.FirstMismatch is { } f ? $" (first: {f})" : "")}; {report.Capsules} capsules; build sequence {(capture.Spawn.Count > 0 ? $"captured ({report.DerivedOriginsOff} spawn origins not given back by centre minus mass centre)" : "inferred")}");
            return world;
        }
    }

    /// <summary>
    /// The collision group table is filled by the compile's collision rules,
    /// which the capture does not hold. Its entries for the groups that met
    /// are read back from the contacts: a contact's +0x78 flags are the entry
    /// (less bits 2-4 when both shapes have flag 4 and the same entity id, as
    /// every shape here has). A pair of groups that met with no manifold in
    /// the capture is taken as solid (1). A capture without contacts (the
    /// bundle) takes them from SETTLE_CONTACTS, a capture of the same compile.
    /// </summary>
    internal static Dictionary<(int, int), ushort> GroupFlags(SettleCapture capture)
    {
        var source = Environment.GetEnvironmentVariable("SETTLE_CONTACTS") is { Length: > 0 } other ? other : Environment.GetEnvironmentVariable("SETTLE")!;
        if (source != Environment.GetEnvironmentVariable("SETTLE"))
            capture = SettleCapture.Read(source);
        var group = capture.Bodies.SelectMany(b => b.Shapes).ToDictionary(s => s.Id, s => (int)s.Head[0x76]);
        var found = new Dictionary<(int, int), ushort>();
        var met = new HashSet<(int, int)>();
        foreach (var line in File.ReadLines(source))
        {
            if (line.Contains("\"collide_in\""))
            {
                using var ind = JsonDocument.Parse(line);
                foreach (var p in ind.RootElement.GetProperty("order").EnumerateArray())
                    met.Add((group[p[0].GetInt32()], group[p[1].GetInt32()]));
                continue;
            }
            if (!line.Contains("\"collide_out\""))
                continue;
            using var doc = JsonDocument.Parse(line);
            foreach (var c in doc.RootElement.GetProperty("contacts").EnumerateArray())
            {
                var raw = Convert.FromHexString(c.GetProperty("raw").GetString()!);
                var key = (group[c.GetProperty("s1").GetInt32()], group[c.GetProperty("s2").GetInt32()]);
                var flags = BitConverter.ToUInt16(raw, 0x78);
                if (found.TryGetValue(key, out var seen) && seen != flags)
                    throw new InvalidDataException($"groups {key}: flags {seen:x} and {flags:x}");
                found[key] = flags;
            }
        }
        // Groups whose contacts never carried a manifold within the capture
        // show only that they meet: they get 1 (solid), the value every
        // other group pair here shows. Nothing in steps 0-40 depends on it.
        foreach (var key in met)
            found.TryAdd(key, 1);
        return found;
    }

    private static int Id(RnShape s) => (int)(s.Proxy.Handle >> 4) - 1;

    /// <summary>The body's bytes, less the island map and node (+0x20..+0x47), which the port keeps as fields.</summary>
    private static string? BodyDifference(in RnBodyState ours, byte[] captured)
    {
        var mine = MemoryMarshal.AsBytes(new ReadOnlySpan<RnBodyState>(in ours));
        for (var i = 0; i < 0x280; i++)
        {
            if (i is >= 0x20 and < 0x48)
                continue;
            if (mine[i] != captured[i])
                return $"+0x{i:x}";
        }
        return null;
    }

    /// <summary>The contact's manifolds, SAT cache or mesh state after the collide worker.</summary>
    private static string? ContactDifference(RnContact c, JsonElement captured)
    {
        var raw = Convert.FromHexString(captured.GetProperty("raw").GetString()!);
        var cache = Convert.FromHexString(captured.GetProperty("cache").GetString()!);
        var count = cache.Length == 0 ? 0 : BitConverter.ToInt32(cache, 0);
        if (count != c.Manifolds.Count)
            return $"{c.Manifolds.Count} manifolds, captured {count}";
        for (var m = 0; m < count; m++)
        {
            var o = MemoryMarshal.AsBytes(new ReadOnlySpan<CachedManifold>(c.Manifolds[m]));
            var t = cache.AsSpan(4 + m * 0xe0, 0xe0);
            var length = 0x40 + c.Manifolds[m].PointCount * 0x28;
            for (var i = 0; i < length; i++)
                if ((i < 0x40 || (i - 0x40) % 0x28 < 0x26) && o[i] != t[i])
                    return $"manifold {m} at +0x{i:x}";
        }
        if (BitConverter.ToInt32(raw, 0x98) != c.Size98)
            return "size";
        if (c.Mesh is { } mesh)
        {
            var tris = Convert.FromHexString(captured.GetProperty("tris").GetString()!);
            if (!MemoryMarshal.AsBytes(mesh.Triangles.ToArray().AsSpan()).SequenceEqual(tris))
                return "candidate triangles";
            var caches = Convert.FromHexString(captured.GetProperty("tricache").GetString()!);
            if (!MemoryMarshal.AsBytes(mesh.Caches.ToArray().AsSpan()).SequenceEqual(caches))
                return "triangle caches";
        }
        else
        {
            var sat = c.Sat;
            if (!MemoryMarshal.AsBytes(new ReadOnlySpan<SatCache>(in sat)).SequenceEqual(raw.AsSpan(0xd4, Marshal.SizeOf<SatCache>())))
                return "SAT cache";
        }
        return null;
    }

    /// <summary>The collide events after the world, in file order.</summary>
    private static IEnumerable<JsonElement> Events(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (!line.Contains("\"collide_"))
                continue;
            using var doc = JsonDocument.Parse(line);
            yield return doc.RootElement.Clone();
        }
    }

    /// <summary>
    /// The settled bodies against bend.bin (0x400-byte records, the body in
    /// the first 0x280), matched through b0.bin by the body's position at
    /// the start: the same compile, another run.
    /// </summary>
    private void CompareFinal(SettleCapture capture, RnWorld world)
    {
        if (Environment.GetEnvironmentVariable("SETTLE_END") is not { Length: > 0 } dir || !File.Exists(Path.Combine(dir, "bend.bin")))
            return;
        var start = File.ReadAllBytes(Path.Combine(dir, "b0.bin"));
        var end = File.ReadAllBytes(Path.Combine(dir, "bend.bin"));
        var records = start.Length / 0x400;
        int matched = 0, exact = 0;
        foreach (var cb in capture.Bodies.Where(b => BitConverter.ToInt32(b.State, 0x54) == 2))
        {
            var position = cb.State[0xfc..0x108];
            var index = Enumerable.Range(0, records).FirstOrDefault(r => start.AsSpan(r * 0x400 + 0xfc, 12).SequenceEqual(position.AsSpan()), -1);
            if (index < 0)
                continue;
            matched++;
            var body = world.Bodies[BitConverter.ToInt32(cb.State, 0x14)];
            var ours = MemoryMarshal.AsBytes(new ReadOnlySpan<RnBodyState>(in body.State)).ToArray();
            var theirs = end.AsSpan(index * 0x400, 0x280).ToArray();
            var fields = new (string Name, int Offset, int Length)[] { ("position", 0xfc, 12), ("orientation", 0x120, 16), ("velocity", 0x108, 24), ("flags", 0x249, 1) };
            var differing = fields.Where(f => !ours.AsSpan(f.Offset, f.Length).SequenceEqual(theirs.AsSpan(f.Offset, f.Length))).Select(f => f.Name).ToList();
            if (differing.Count == 0)
                exact++;
            else
                output.WriteLine($"final body {body.Index}: {string.Join(", ", differing)} differ; ours at {body.State.Position}, Valve at {MemoryMarshal.Read<Vec3>(theirs.AsSpan(0xfc))}");
        }
        output.WriteLine($"final state: {exact}/{matched} bodies exact (position, orientation, velocities, flags)");
    }
}
