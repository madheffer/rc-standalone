using System.Runtime.InteropServices;
using System.Text.Json;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Replays island solves captured from a live settle (tools/settle, the island
/// probe) through <see cref="IslandSolver"/>: the same bodies and contact caches
/// in, and every body and cache byte must come out as Valve's did.
///
/// <para>Set ISLANDS to the capture's events.jsonl. Without it the test does
/// nothing: the capture is taken from the user's own compile and is not
/// committed.</para>
/// </summary>
public class IslandReplay(ITestOutputHelper output)
{
    [Fact]
    public void CapturedIslandsSolveTheSame()
    {
        if (Environment.GetEnvironmentVariable("ISLANDS") is not { Length: > 0 } path || !File.Exists(path))
            return;

        var pending = new Dictionary<string, JsonElement>();
        int islands = 0, exact = 0;
        var first = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            var kind = e.GetProperty("ev").GetString();
            if (kind == "island_in")
            {
                pending[e.GetProperty("id").GetString()!] = e.Clone();
                continue;
            }
            if (kind != "island_out")
                continue;
            var input = pending[e.GetProperty("id").GetString()!];
            islands++;
            var difference = Replay(input, e);
            if (difference is null)
                exact++;
            else if (first.Count < 10)
                first.Add($"step {input.GetProperty("step").GetInt32()}: {difference}");
        }
        output.WriteLine($"{exact} of {islands} islands exact");
        foreach (var f in first)
            output.WriteLine(f);
        Assert.True(islands > 0);
        Assert.Equal(islands, exact);
    }

    /// <summary>Solves one captured island; the first difference, or null.</summary>
    private static string? Replay(JsonElement input, JsonElement expected)
    {
        var bodies = input.GetProperty("bodies").EnumerateArray().Select(b => Struct<RnBodyState>(b.GetString()!)).ToArray();
        var dynamicPairs = input.GetProperty("ca").EnumerateArray().Select(Contact).ToList();
        var otherPairs = input.GetProperty("cb").EnumerateArray().Select(Contact).ToList();
        // NoDynamicContact comes from the body's joints (FUN_1801b6000 walks body
        // +0x70), not its contacts; the settle's props have no joints.
        var touches = new bool[bodies.Length];
        var settings = new IslandSolver.Settings(
            input.GetProperty("dt").GetSingle(), new Vec3(-0f, -0f, -360f), 1.2f,
            input.GetProperty("cvi").GetInt32(), input.GetProperty("cpi").GetInt32(),
            input.GetProperty("sleep").GetByte() != 0);
        IslandSolver.Solve(bodies, touches, dynamicPairs, otherPairs, settings);

        var want = expected.GetProperty("bodies").EnumerateArray().Select(b => Struct<RnBodyState>(b.GetString()!)).ToArray();
        for (var i = 0; i < bodies.Length; i++)
        {
            if (bodies[i].BodyType == 0)
                continue;
            foreach (var (name, offset, length) in Fields)
            {
                var ours = MemoryMarshal.AsBytes(new ReadOnlySpan<RnBodyState>(in bodies[i])).Slice(offset, length);
                var theirs = MemoryMarshal.AsBytes(new ReadOnlySpan<RnBodyState>(in want[i])).Slice(offset, length);
                if (!ours.SequenceEqual(theirs))
                    return $"body {i} {name}: ours {Floats(ours)} valve {Floats(theirs)}";
            }
        }
        var caches = expected.GetProperty("ca").EnumerateArray().Concat(expected.GetProperty("cb").EnumerateArray()).ToList();
        var contacts = dynamicPairs.Concat(otherPairs).ToList();
        for (var c = 0; c < contacts.Count; c++)
        {
            var want2 = Cache(caches[c].GetString()!);
            var ours = MemoryMarshal.AsBytes(contacts[c].Cache.AsSpan());
            var theirs = MemoryMarshal.AsBytes(want2.AsSpan());
            if (!ours.SequenceEqual(theirs))
                return $"contact {c} cache differs";
        }
        return null;
    }

    /// <summary>The body fields the solve writes.</summary>
    private static readonly (string Name, int Offset, int Length)[] Fields =
    [
        ("world inverse inertia", 0xd8, 36), ("position", 0xfc, 12), ("linear velocity", 0x108, 12),
        ("angular velocity", 0x114, 12), ("orientation", 0x120, 16), ("sleep timer", 0x1f4, 4),
    ];

    private static IslandSolver.Contact Contact(JsonElement c) => new()
    {
        BodyA = c.GetProperty("a").GetInt32(),
        BodyB = c.GetProperty("b").GetInt32(),
        Cache = Cache(c.GetProperty("cache").GetString()!),
        Setup = new ContactSolver.ContactSetup(
            Struct<ContactSolver.Material>(c.GetProperty("ma").GetString()!),
            Struct<ContactSolver.Material>(c.GetProperty("mb").GetString()!),
            c.GetProperty("slop").GetSingle(), c.GetProperty("cap").GetSingle()),
    };

    /// <summary>A cache block as captured: the manifold count, then the manifolds.</summary>
    private static CachedManifold[] Cache(string hex)
    {
        if (hex.Length == 0)
            return [];
        var bytes = Convert.FromHexString(hex);
        var count = BitConverter.ToInt32(bytes, 0);
        return MemoryMarshal.Cast<byte, CachedManifold>(bytes.AsSpan(4, count * 0xe0)).ToArray();
    }

    private static T Struct<T>(string hex) where T : unmanaged
        => MemoryMarshal.Read<T>(Convert.FromHexString(hex));

    private static string Floats(ReadOnlySpan<byte> b)
        => string.Join(" ", MemoryMarshal.Cast<byte, float>(b).ToArray().Select(f => f.ToString("R")));
}
