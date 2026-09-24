using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Replays every contact update captured from a live settle (tools/settle, the
/// world probe) through <see cref="HullCollision"/> and <see cref="MeshCollision"/>:
/// the world's own hulls and meshes, the bodies where Valve had them, each
/// contact's cached state from before the collide, and it must come out as the
/// state Valve left after it.
///
/// <para>Set NARROWPHASE to the capture's events.jsonl; without it the test
/// does nothing.</para>
/// </summary>
public class NarrowphaseReplay(ITestOutputHelper output)
{
    private sealed record Shape(int Type, int Body, RnHull? Hull, float HullScale, RnMesh? Mesh, Vec3 MeshScale);

    [Fact]
    public void CapturedContactUpdatesMatch()
    {
        if (Environment.GetEnvironmentVariable("NARROWPHASE") is not { Length: > 0 } path || !File.Exists(path))
            return;

        Shape[]? shapes = null;
        RnBodyState[]? bodies = null;
        Dictionary<(int, int), JsonElement>? before = null;
        int updates = 0, exact = 0, convex = 0, mesh = 0;
        var first = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            switch (e.GetProperty("ev").GetString())
            {
                case "world":
                    (shapes, bodies) = World(e);
                    break;
                case "collide_in":
                    foreach (var b in e.GetProperty("bodies").EnumerateArray())
                        bodies![b[0].GetInt32()] = Struct<RnBodyState>(b[1].GetString()!);
                    before = e.GetProperty("contacts").EnumerateArray()
                        .ToDictionary(c => (c.GetProperty("s1").GetInt32(), c.GetProperty("s2").GetInt32()), c => c.Clone());
                    break;
                case "collide_out":
                    foreach (var after in e.GetProperty("contacts").EnumerateArray())
                    {
                        var key = (after.GetProperty("s1").GetInt32(), after.GetProperty("s2").GetInt32());
                        before!.TryGetValue(key, out var prior);
                        var a = shapes![key.Item1];
                        var b = shapes[key.Item2];
                        updates++;
                        string? difference;
                        if (b.Type == 3)
                        {
                            mesh++;
                            difference = ReplayMesh(prior, after, a, b, bodies!);
                        }
                        else
                        {
                            convex++;
                            difference = ReplayConvex(prior, after, a, b, bodies!);
                        }
                        if (difference is null)
                            exact++;
                        else if (first.Count < 12)
                            first.Add($"step {e.GetProperty("step").GetInt32()} shapes {key}: {difference}");
                    }
                    break;
            }
        }
        output.WriteLine($"{exact} of {updates} contact updates exact ({convex} hull pairs, {mesh} hull-mesh)");
        foreach (var f in first)
            output.WriteLine(f);
        Assert.True(updates > 0);
        Assert.Equal(updates, exact);
    }

    private static string? ReplayConvex(JsonElement prior, JsonElement after, Shape a, Shape b, RnBodyState[] bodies)
    {
        var raw = prior.ValueKind == JsonValueKind.Undefined ? new byte[0xf0] : Convert.FromHexString(prior.GetProperty("raw").GetString()!);
        var old = prior.ValueKind == JsonValueKind.Undefined ? [] : Cache(prior.GetProperty("cache").GetString()!);
        var cache = MemoryMarshal.Read<SatCache>(raw.AsSpan(0xd4));
        var result = default(CachedManifold);
        var hit = HullCollision.Collide(old, ref result, RnTransform.Of(bodies[a.Body]), new HullRef(a.Hull!, a.HullScale),
                                        RnTransform.Of(bodies[b.Body]), new HullRef(b.Hull!, b.HullScale), ref cache);
        var want = Cache(after.GetProperty("cache").GetString()!);
        var wantCache = MemoryMarshal.Read<SatCache>(Convert.FromHexString(after.GetProperty("raw").GetString()!).AsSpan(0xd4));
        if (!MemoryMarshal.AsBytes(new ReadOnlySpan<SatCache>(in cache)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<SatCache>(in wantCache))))
            return "SAT cache differs";
        return Same(hit ? [result] : [], want);
    }

    private static string? ReplayMesh(JsonElement prior, JsonElement after, Shape a, Shape b, RnBodyState[] bodies)
    {
        var state = new MeshContactState();
        if (prior.ValueKind != JsonValueKind.Undefined)
        {
            var raw = Convert.FromHexString(prior.GetProperty("raw").GetString()!);
            state.BoxMin = MemoryMarshal.Read<Vec3>(raw.AsSpan(0xa8));
            state.BoxMax = MemoryMarshal.Read<Vec3>(raw.AsSpan(0xb4));
            state.Triangles = MemoryMarshal.Cast<byte, int>(Convert.FromHexString(prior.GetProperty("tris").GetString()!)).ToArray().ToList();
            state.Caches = MemoryMarshal.Cast<byte, MeshTriangleCache>(Convert.FromHexString(prior.GetProperty("tricache").GetString()!)).ToArray().ToList();
            state.Manifolds = Cache(prior.GetProperty("cache").GetString()!).ToList();
        }
        MeshCollision.Update(state, RnTransform.Of(bodies[a.Body]), new HullRef(a.Hull!, a.HullScale),
                             RnTransform.Of(bodies[b.Body]), b.Mesh!, b.MeshScale);

        var rawAfter = Convert.FromHexString(after.GetProperty("raw").GetString()!);
        if (!Bytes(state.BoxMin).SequenceEqual(rawAfter.AsSpan(0xa8, 12)) || !Bytes(state.BoxMax).SequenceEqual(rawAfter.AsSpan(0xb4, 12)))
            return "cached box differs";
        var tris = Convert.FromHexString(after.GetProperty("tris").GetString()!);
        if (!MemoryMarshal.AsBytes(state.Triangles.ToArray().AsSpan()).SequenceEqual(tris))
            return $"candidate triangles differ ({state.Triangles.Count} vs {tris.Length / 4})";
        var caches = Convert.FromHexString(after.GetProperty("tricache").GetString()!);
        if (!MemoryMarshal.AsBytes(state.Caches.ToArray().AsSpan()).SequenceEqual(caches))
            return "triangle caches differ";
        if (BitConverter.ToInt32(rawAfter, 0x98) != state.SizeEstimate)
            return "size estimate differs";
        return Same(state.Manifolds, Cache(after.GetProperty("cache").GetString()!));
    }

    /// <summary>Manifolds compared byte for byte, less each point's two never-written bytes (+0x26).</summary>
    private static string? Same(IReadOnlyList<CachedManifold> ours, CachedManifold[] theirs)
    {
        if (ours.Count != theirs.Length)
            return $"{ours.Count} manifolds, Valve {theirs.Length}";
        for (var m = 0; m < ours.Count; m++)
        {
            var o = MemoryMarshal.AsBytes(new ReadOnlySpan<CachedManifold>(ours[m])).ToArray();
            var t = MemoryMarshal.AsBytes(new ReadOnlySpan<CachedManifold>(in theirs[m])).ToArray();
            for (var p = 0; p < 4; p++)
                for (var k = 0x26; k < 0x28; k++)
                    o[0x40 + p * 0x28 + k] = t[0x40 + p * 0x28 + k];
            var count = ours[m].PointCount;
            var length = 0x40 + count * 0x28;
            for (var i = 0; i < length; i++)
                if (o[i] != t[i])
                    return $"manifold {m} differs at +0x{i:x}";
        }
        return null;
    }

    /// <summary>The captured world: every shape, with the body it hangs off; every body.</summary>
    private static (Shape[], RnBodyState[]) World(JsonElement e)
    {
        var shapes = new List<Shape>();
        var bodies = new List<RnBodyState>();
        var index = 0;
        foreach (var b in e.GetProperty("bodies").EnumerateArray())
        {
            bodies.Add(Struct<RnBodyState>(b.GetProperty("body").GetString()!));
            foreach (var s in b.GetProperty("shapes").EnumerateArray())
            {
                var head = Convert.FromHexString(s.GetProperty("head").GetString()!);
                var type = s.GetProperty("type").GetInt32();
                var shape = type switch
                {
                    2 => new Shape(type, index, Hull(s), BitConverter.ToSingle(head, 0xb8), null, default),
                    3 => new Shape(type, index, null, 0, Mesh(s), MemoryMarshal.Read<Vec3>(head.AsSpan(0xb8))),
                    _ => new Shape(type, index, null, 0, null, default),
                };
                while (shapes.Count <= s.GetProperty("id").GetInt32())
                    shapes.Add(shape);
            }
            index++;
        }
        return (shapes.ToArray(), bodies.ToArray());
    }

    private static RnHull Hull(JsonElement s)
    {
        var h = Convert.FromHexString(s.GetProperty("hull").GetString()!);
        var planes = Convert.FromHexString(s.GetProperty("planes").GetString()!);
        var edges = Convert.FromHexString(s.GetProperty("edges").GetString()!);
        return new RnHull
        {
            Centroid = MemoryMarshal.Read<Vector3>(h),
            MaxAngularRadius = BitConverter.ToSingle(h, 0xc),
            MinCentroidRadius = BitConverter.ToSingle(h, 0x10),
            BoundsMin = MemoryMarshal.Read<Vector3>(h.AsSpan(0x14)),
            BoundsMax = MemoryMarshal.Read<Vector3>(h.AsSpan(0x20)),
            VertexPositions = MemoryMarshal.Cast<byte, Vector3>(Convert.FromHexString(s.GetProperty("pos").GetString()!)).ToArray(),
            Planes = Enumerable.Range(0, planes.Length / 16)
                .Select(i => (MemoryMarshal.Read<Vector3>(planes.AsSpan(i * 16)), BitConverter.ToSingle(planes, i * 16 + 12))).ToArray(),
            Vertices = Convert.FromHexString(s.GetProperty("verts").GetString()!),
            Edges = Enumerable.Range(0, edges.Length / 4)
                .Select(i => (edges[i * 4], edges[i * 4 + 1], edges[i * 4 + 2], edges[i * 4 + 3])).ToArray(),
            Faces = Convert.FromHexString(s.GetProperty("faces").GetString()!),
            Flags = BitConverter.ToUInt32(h, 0xa0),
        };
    }

    private static RnMesh Mesh(JsonElement s)
    {
        var m = Convert.FromHexString(s.GetProperty("mesh").GetString()!);
        var nodes = Convert.FromHexString(s.GetProperty("nodes").GetString()!);
        var tris = MemoryMarshal.Cast<byte, int>(Convert.FromHexString(s.GetProperty("tris").GetString()!)).ToArray();
        return new RnMesh
        {
            Min = MemoryMarshal.Read<Vector3>(m),
            Max = MemoryMarshal.Read<Vector3>(m.AsSpan(0xc)),
            Nodes = Enumerable.Range(0, nodes.Length / 32).Select(i => new RnMesh.Node(
                MemoryMarshal.Read<Vector3>(nodes.AsSpan(i * 32)), BitConverter.ToUInt32(nodes, i * 32 + 12),
                MemoryMarshal.Read<Vector3>(nodes.AsSpan(i * 32 + 16)), BitConverter.ToUInt32(nodes, i * 32 + 28))).ToArray(),
            Vertices = MemoryMarshal.Cast<byte, Vector3>(Convert.FromHexString(s.GetProperty("mverts").GetString()!)).ToArray(),
            Triangles = Enumerable.Range(0, tris.Length / 3).Select(i => (tris[i * 3], tris[i * 3 + 1], tris[i * 3 + 2])).ToArray(),
            Materials = Convert.FromHexString(s.GetProperty("mats").GetString()!),
        };
    }

    private static CachedManifold[] Cache(string hex)
    {
        if (hex.Length == 0)
            return [];
        var bytes = Convert.FromHexString(hex);
        var count = BitConverter.ToInt32(bytes, 0);
        return MemoryMarshal.Cast<byte, CachedManifold>(bytes.AsSpan(4, count * 0xe0)).ToArray();
    }

    private static byte[] Bytes(Vec3 v) => MemoryMarshal.AsBytes(new ReadOnlySpan<Vec3>(in v)).ToArray();

    private static T Struct<T>(string hex) where T : unmanaged
        => MemoryMarshal.Read<T>(Convert.FromHexString(hex));
}
