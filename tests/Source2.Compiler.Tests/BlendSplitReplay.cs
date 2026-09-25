using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Source2.Compiler.Physics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Replays physicsbuilder's blend splits a <c>capture_physshapes.py --blend</c>
/// capture recorded through <see cref="WorldCollision"/>'s split: each
/// captured mesh in (its positions, indices and VertexPaintBlendParams stream)
/// with the captured layer table, against the captured meshes out, vertex
/// and index bit for bit. Also reports what the material sampler did.
/// <c>BLENDREPLAY=&lt;capture json&gt;</c>.
/// </summary>
public class BlendSplitReplay(ITestOutputHelper output)
{
    [Fact]
    public void SplitsAsPhysicsbuilder()
    {
        if (Environment.GetEnvironmentVariable("BLENDREPLAY") is not { Length: > 0 } path)
            return;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var events = doc.RootElement.EnumerateArray().Where(e => e.TryGetProperty("blend", out _) || e.TryGetProperty("sampler", out _)).ToList();
        foreach (var s in events.Where(e => e.TryGetProperty("sampler", out _)))
            output.WriteLine($"sampler {s.GetProperty("sampler").GetString()}: {s}");
        int exact = 0, total = 0;
        for (var i = 0; i + 1 < events.Count; i++)
        {
            if (!events[i].TryGetProperty("blend", out var kind) || kind.GetString() != "in")
                continue;
            var outEvent = events.Skip(i + 1).First(e => e.TryGetProperty("blend", out var k) && k.GetString() == "out");
            var t = events[i].GetProperty("table");
            var mesh = events[i].GetProperty("mesh");
            var stride = mesh.GetProperty("stride").GetInt32();
            var v = MemoryMarshal.Cast<byte, float>(Convert.FromHexString(mesh.GetProperty("vdata").GetString()!)).ToArray();
            var ix = MemoryMarshal.Cast<byte, int>(Convert.FromHexString(mesh.GetProperty("idata").GetString()!)).ToArray();
            var streams = mesh.GetProperty("streams").EnumerateArray().ToList();
            int Offset(string name) => streams.First(x => x.GetProperty("name").GetString() == name).GetProperty("offset").GetInt32();
            int pos = Offset("position"), paint = Offset("VertexPaintBlendParams");
            var n = v.Length / stride;
            var points = Enumerable.Range(0, n).Select(k => new Vector3(v[(k * stride) + pos], v[(k * stride) + pos + 1], v[(k * stride) + pos + 2])).ToArray();
            var paints = Enumerable.Range(0, n).Select(k => new Vector4(v[(k * stride) + paint], v[(k * stride) + paint + 1], v[(k * stride) + paint + 2], v[(k * stride) + paint + 3])).ToArray();
            var names = t.GetProperty("names").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            var blend = new WorldCollision.BlendLayers(t.GetProperty("layers").GetInt32(), t.GetProperty("puddleChannel").GetInt32(), t.GetProperty("puddleLayer").GetInt32(),
                names, [.. t.GetProperty("remap").EnumerateArray().Select(x => x.GetInt32())], t.GetProperty("swap").GetInt32() != 0, t.GetProperty("scale1").GetSingle(), t.GetProperty("sampled").GetInt32() != 0);
            var ours = WorldCollision.SplitLayers(blend, points, ix, paints);
            var theirs = outEvent.GetProperty("meshes").EnumerateArray().ToList();
            total++;
            var same = ours.Count == theirs.Count;
            var notes = new List<string>();
            for (var m = 0; same && m < ours.Count; m++)
            {
                var tv = theirs[m].TryGetProperty("vdata", out var vd) ? MemoryMarshal.Cast<byte, Vector3>(Convert.FromHexString(vd.GetString()!)).ToArray() : [];
                var ti = theirs[m].TryGetProperty("idata", out var id) ? MemoryMarshal.Cast<byte, int>(Convert.FromHexString(id.GetString()!)).ToArray() : [];
                var ok = tv.AsSpan().SequenceEqual(ours[m].Points) && ti.AsSpan().SequenceEqual(ours[m].Indices);
                if (!ok)
                    notes.Add($"layer {m} ({names.ElementAtOrDefault(m)}): ours {ours[m].Points.Length} v {ours[m].Indices.Length / 3} t, theirs {tv.Length} v {ti.Length / 3} t");
                same &= ok;
            }
            if (same)
                exact++;
            output.WriteLine($"split {total}: {n} vertices, {ix.Length / 3} triangles, table {t}; {(same ? "exact" : "differs: " + string.Join("; ", notes))}");
        }
        output.WriteLine($"BLENDREPLAY {exact} of {total} splits exact");
        Assert.Equal(total, exact);
    }
}
