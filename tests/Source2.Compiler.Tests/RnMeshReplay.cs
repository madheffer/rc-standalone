using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.Physics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Replays every RnMeshCreate call <c>tools/physics/capture_rnmesh.py</c>
/// recorded (<c>RNMESH=&lt;capture&gt;</c>) through <see cref="RnMeshBuilder"/>
/// and compares the mesh it built with the one vphysics2 built, field by field.
/// <c>RNMESH_SHOW</c> caps the listed differences.
/// </summary>
public class RnMeshReplay(ITestOutputHelper output)
{
    private sealed record Call(int Id, int Tris, int VertexCount, bool HasMaterials, bool HasOptions, byte[] In, byte[]? Out, int[] OutSizes);

    [Fact]
    public void RnMeshCreateAsVphysicsBuildsIt()
    {
        if (Environment.GetEnvironmentVariable("RNMESH") is not { Length: > 0 } path)
            return;
        var show = int.TryParse(Environment.GetEnvironmentVariable("RNMESH_SHOW"), out var s) ? s : 10;
        var tally = new Dictionary<string, int>();
        void Count(string k) => tally[k] = tally.GetValueOrDefault(k) + 1;
        var shown = 0;
        foreach (var call in Read(path))
        {
            if (call.Out == null)
            {
                Count("valve built none");
                continue;
            }
            var at = 0;
            var indices = MemoryMarshal.Cast<byte, int>(call.In.AsSpan(at, call.Tris * 12)).ToArray();
            at += call.Tris * 12;
            byte[]? materials = null;
            if (call.HasMaterials)
            {
                materials = call.In.AsSpan(at, call.Tris).ToArray();
                at += call.Tris;
            }
            var vertices = MemoryMarshal.Cast<byte, Vector3>(call.In.AsSpan(at, call.VertexCount * 12)).ToArray();
            at += call.VertexCount * 12;
            var options = new RnMeshBuilder.Options();
            if (call.HasOptions)
            {
                var o = call.In.AsSpan(at, 16);
                options = new RnMeshBuilder.Options(o[0] != 0, BitConverter.ToSingle(o[4..8]), BitConverter.ToSingle(o[8..12]), BitConverter.ToInt32(o[12..16]));
            }
            RnMesh? ours;
            try
            {
                ours = RnMeshBuilder.Create(indices, vertices, materials, options);
            }
            catch (NotSupportedException e)
            {
                Count("unported: " + e.Message);
                continue;
            }
            var diffs = Compare(call, ours);
            Count(diffs.Count == 0 ? "exact" : diffs[0].Split(' ')[0]);
            if (diffs.Count > 0 && shown++ < show)
                output.WriteLine($"call {call.Id} ({call.Tris} tris, {call.VertexCount} verts, options {options}): {string.Join("; ", diffs.Take(6))}");
        }
        foreach (var (k, v) in tally.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            output.WriteLine($"{k}: {v}");
    }

    private static List<string> Compare(Call call, RnMesh? ours)
    {
        var d = new List<string>();
        if (ours == null)
            return ["null (ours built none)"];
        var o = call.Out!;
        var head = o.AsSpan(0, 0xc0);
        var at = 0xc0;
        var nodes = MemoryMarshal.Cast<byte, uint>(o.AsSpan(at, call.OutSizes[1])).ToArray();
        at += call.OutSizes[1];
        var verts = MemoryMarshal.Cast<byte, Vector3>(o.AsSpan(at, call.OutSizes[2])).ToArray();
        at += call.OutSizes[2];
        var tris = MemoryMarshal.Cast<byte, int>(o.AsSpan(at, call.OutSizes[3])).ToArray();
        at += call.OutSizes[3];
        var mats = o.AsSpan(at, call.OutSizes[4]).ToArray();

        static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
        var vmin = MemoryMarshal.Cast<byte, Vector3>(head[..12])[0];
        var vmax = MemoryMarshal.Cast<byte, Vector3>(head[12..24])[0];
        if (!(Same(vmin.X, ours.Min.X) && Same(vmin.Y, ours.Min.Y) && Same(vmin.Z, ours.Min.Z) && Same(vmax.X, ours.Max.X) && Same(vmax.Y, ours.Max.Y) && Same(vmax.Z, ours.Max.Z)))
            d.Add($"bounds valve {vmin}..{vmax} ours {ours.Min}..{ours.Max}");
        if (verts.Length != ours.Vertices.Length)
            d.Add($"vertices valve {verts.Length} ours {ours.Vertices.Length}");
        else
        {
            var bad = Enumerable.Range(0, verts.Length).FirstOrDefault(i => !(Same(verts[i].X, ours.Vertices[i].X) && Same(verts[i].Y, ours.Vertices[i].Y) && Same(verts[i].Z, ours.Vertices[i].Z)), -1);
            if (bad >= 0)
            {
                var set = verts.ToHashSet();
                var order = string.Join(",", ours.Vertices.Take(12).Select(v => Array.IndexOf(verts, v)));
                d.Add($"vertex {bad} valve {verts[bad]} ours {ours.Vertices[bad]}; same set {ours.Vertices.All(set.Contains)}; our first vertices at valve {order}");
            }
        }
        if (tris.Length != ours.Triangles.Length * 3)
            d.Add($"triangles valve {tris.Length / 3} ours {ours.Triangles.Length}");
        else
        {
            var bad = Enumerable.Range(0, ours.Triangles.Length).FirstOrDefault(t => (tris[t * 3], tris[(t * 3) + 1], tris[(t * 3) + 2]) != ours.Triangles[t], -1);
            if (bad >= 0)
                d.Add($"triangle {bad} valve ({tris[bad * 3]},{tris[(bad * 3) + 1]},{tris[(bad * 3) + 2]}) ours {ours.Triangles[bad]}");
        }
        if (nodes.Length != ours.Nodes.Length * 8)
            d.Add($"nodes valve {nodes.Length / 8} ours {ours.Nodes.Length}");
        else
        {
            for (var n = 0; n < ours.Nodes.Length; n++)
            {
                var node = ours.Nodes[n];
                uint[] mine = [Bits(node.Min.X), Bits(node.Min.Y), Bits(node.Min.Z), node.Children, Bits(node.Max.X), Bits(node.Max.Y), Bits(node.Max.Z), node.TriangleOffset];
                if (!mine.SequenceEqual(nodes.AsSpan(n * 8, 8).ToArray()))
                {
                    d.Add($"node {n} valve [{string.Join(" ", nodes.AsSpan(n * 8, 8).ToArray().Select(x => x.ToString("x8")))}] ours [{string.Join(" ", mine.Select(x => x.ToString("x8")))}]");
                    break;
                }
            }
        }
        if (!mats.SequenceEqual(ours.Materials))
            d.Add($"materials valve {mats.Length} ours {ours.Materials.Length}");
        var ortho = MemoryMarshal.Cast<byte, Vector3>(head[0xa8..0xb4])[0];
        if (!(Same(ortho.X, ours.OrthographicAreas.X) && Same(ortho.Y, ours.OrthographicAreas.Y) && Same(ortho.Z, ours.OrthographicAreas.Z)))
            d.Add($"ortho valve {ortho} ours {ours.OrthographicAreas}");
        var area = BitConverter.ToSingle(head[0xbc..0xc0]);
        if (!Same(area, ours.SurfaceArea))
            d.Add($"area valve {area:R} ours {ours.SurfaceArea:R}");
        var flags = BitConverter.ToUInt32(head[0xb4..0xb8]);
        var debugFlags = BitConverter.ToUInt32(head[0xb8..0xbc]);
        if (debugFlags != ours.DebugFlags)
            d.Add($"second flags valve {debugFlags} ours {ours.DebugFlags}");
        if (flags != ours.Flags)
            d.Add($"flags valve {flags} ours {ours.Flags}");
        return d;
    }

    private static uint Bits(float f) => (uint)BitConverter.SingleToInt32Bits(f);

    private static IEnumerable<Call> Read(string path)
    {
        var data = File.ReadAllBytes(path);
        var pending = new Dictionary<int, (System.Text.Json.JsonElement Head, byte[] Blob)>();
        for (var at = 0; at < data.Length;)
        {
            var n = BitConverter.ToInt32(data, at);
            var head = System.Text.Json.JsonDocument.Parse(data.AsMemory(at + 4, n)).RootElement;
            at += 4 + n;
            var m = BitConverter.ToInt32(data, at);
            var blob = data.AsSpan(at + 4, m).ToArray();
            at += 4 + m;
            var id = head.GetProperty("id").GetInt32();
            if (head.GetProperty("ev").GetString() == "in")
            {
                pending[id] = (head, blob);
                continue;
            }
            if (!pending.Remove(id, out var input))
                continue;
            var none = head.TryGetProperty("none", out _);
            yield return new Call(id, input.Head.GetProperty("tris").GetInt32(), input.Head.GetProperty("vcount").GetInt32(),
                input.Head.GetProperty("hasMaterials").GetBoolean(), input.Head.GetProperty("hasOptions").GetBoolean(), input.Blob,
                none ? null : blob, none ? [] : [.. head.GetProperty("sizes").EnumerateArray().Select(x => x.GetInt32())]);
        }
    }
}
