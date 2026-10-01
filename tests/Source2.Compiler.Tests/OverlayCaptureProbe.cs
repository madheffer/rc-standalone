using System.Numerics;
using Source2.Compiler.Maps;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Reads tools/vis/capture_overlays.py's file (<c>OVERLAYS</c>): each node's
/// overlay descriptors as WRBNode_GenerateOverlayMeshes finds them, and each
/// COverlayProjector_ProjectOntoTarget call's target record and resulting
/// mesh.
/// </summary>
public class OverlayCaptureProbe(ITestOutputHelper output)
{
    internal sealed record Face(int Count, Vector3[] Positions, Vector2[] Texcoords, Vector4[] A, Vector4[] B, byte[] Raw);

    internal sealed record Overlay(int Node, int Index, string Material, string S70, string Name, byte[] Raw, byte[] Slots, Face[] Faces)
    {
        public int Mode => BitConverter.ToInt32(Raw, 0);
        public float Far => BitConverter.ToSingle(Raw, 4);
        public int RenderOrder => BitConverter.ToInt32(Raw, 8);
        public bool BackFaces => Raw[0xc] != 0;
        public float Angle => BitConverter.ToSingle(Raw, 0x10);
        public uint Tint => BitConverter.ToUInt32(Raw, 0x18);
    }

    internal sealed record Projected(int Node, string Target, bool Empty, int Stride, float[] Vertices, int[] Indices, (string Name, int First, int Count, int Type)[] Streams, byte[] TargetRecord);

    internal static (List<Overlay> Overlays, List<Projected> Projected, List<string[]> NodeEntries) Read(string path)
    {
        var overlays = new List<Overlay>();
        var projected = new List<Projected>();
        var nodes = new List<string[]>();
        byte[]? pendingTarget = null;
        using var f = File.OpenRead(path);
        using var r = new BinaryReader(f);
        while (f.Position < f.Length)
        {
            var head = JsonDocument.Parse(r.ReadBytes(r.ReadInt32())).RootElement;
            var blob = r.ReadBytes(r.ReadInt32());
            switch (head.GetProperty("ev").GetString())
            {
                case "node":
                    nodes.Add([.. head.GetProperty("entries").EnumerateArray().Select(x => x.GetString()!)]);
                    break;
                case "overlay":
                {
                    var at = 0xc0;
                    var slots = head.GetProperty("slots").GetInt32();
                    var slotBytes = blob[at..(at + Math.Max(slots, 0) * 8)];
                    at += Math.Max(slots, 0) * 8;
                    var faces = new List<Face>();
                    foreach (var c in head.GetProperty("faces").EnumerateArray())
                    {
                        var counts = c.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                        var raw = blob[at..(at + 0x78)];
                        at += 0x78;
                        Vector3[] V3(int n) { var v = new Vector3[n]; for (var i = 0; i < n; i++) v[i] = new(BitConverter.ToSingle(blob, at + i * 12), BitConverter.ToSingle(blob, at + i * 12 + 4), BitConverter.ToSingle(blob, at + i * 12 + 8)); at += n * 12; return v; }
                        Vector2[] V2(int n) { var v = new Vector2[n]; for (var i = 0; i < n; i++) v[i] = new(BitConverter.ToSingle(blob, at + i * 8), BitConverter.ToSingle(blob, at + i * 8 + 4)); at += n * 8; return v; }
                        Vector4[] V4(int n) { var v = new Vector4[n]; for (var i = 0; i < n; i++) v[i] = new(BitConverter.ToSingle(blob, at + i * 16), BitConverter.ToSingle(blob, at + i * 16 + 4), BitConverter.ToSingle(blob, at + i * 16 + 8), BitConverter.ToSingle(blob, at + i * 16 + 12)); at += n * 16; return v; }
                        var pos = V3(counts[0]);
                        var uv = V2(counts[1]);
                        var a = V4(counts[2]);
                        var b = V4(counts[3]);
                        faces.Add(new Face(counts[0], pos, uv, a, b, raw));
                    }
                    overlays.Add(new Overlay(head.GetProperty("node").GetInt32(), head.GetProperty("i").GetInt32(), head.GetProperty("material").GetString()!,
                                             head.GetProperty("s70").GetString()!, head.GetProperty("name").GetString()!, blob[..0xc0], slotBytes, [.. faces]));
                    break;
                }
                case "target":
                    pendingTarget = blob;
                    break;
                case "projected":
                {
                    var empty = head.GetProperty("empty").GetBoolean();
                    if (empty)
                    {
                        projected.Add(new Projected(head.GetProperty("node").GetInt32(), head.GetProperty("target").GetString()!, true, 0, [], [], [], pendingTarget ?? []));
                        break;
                    }
                    int nv = head.GetProperty("nv").GetInt32(), stride = head.GetProperty("stride").GetInt32(), ni = head.GetProperty("ni").GetInt32();
                    var v = new float[nv * stride];
                    Buffer.BlockCopy(blob, 0, v, 0, v.Length * 4);
                    var idx = new int[ni];
                    Buffer.BlockCopy(blob, v.Length * 4, idx, 0, ni * 4);
                    projected.Add(new Projected(head.GetProperty("node").GetInt32(), head.GetProperty("target").GetString()!, false, stride, v, idx,
                        [.. head.GetProperty("streams").EnumerateArray().Select(x => (x.GetProperty("name").GetString()!, x.GetProperty("first").GetInt32(), x.GetProperty("count").GetInt32(), x.GetProperty("type").GetInt32()))],
                        pendingTarget ?? []));
                    break;
                }
            }
        }
        return (overlays, projected, nodes);
    }

    [Fact]
    public void Summary()
    {
        if (Environment.GetEnvironmentVariable("OVERLAYS") is not { } path)
            return;
        var (overlays, projected, nodes) = Read(path);
        output.WriteLine($"{nodes.Count} nodes, {overlays.Count} overlays, {projected.Count} projections ({projected.Count(p => !p.Empty)} with a mesh)");
        foreach (var g in overlays.GroupBy(o => (o.Mode, o.Faces.Length)))
            output.WriteLine($"  mode {g.Key.Mode}, {g.Key.Length} face(s): {g.Count()}");
        string F(float x) => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var o in overlays.Take(2))
        {
            output.WriteLine($"overlay {o.Index} {o.Material} far {F(o.Far)} order {o.RenderOrder} back {o.BackFaces} angle {F(o.Angle)} tint {o.Tint:X8} s70 '{o.S70}' name '{o.Name}'");
            output.WriteLine($"  raw {Convert.ToHexString(o.Raw)}");
            foreach (var f in o.Faces)
            {
                output.WriteLine($"  face {f.Count}: raw {Convert.ToHexString(f.Raw)}");
                for (var i = 0; i < f.Count; i++)
                    output.WriteLine($"    {f.Positions[i]} uv {(i < f.Texcoords.Length ? f.Texcoords[i] : default)} a {(i < f.A.Length ? f.A[i] : default)} b {(i < f.B.Length ? f.B[i] : default)}");
            }
        }
        foreach (var p in projected.Where(p => !p.Empty).Take(2))
            output.WriteLine($"projected {p.Vertices.Length / Math.Max(p.Stride, 1)} vertices {p.Indices.Length} indices streams {string.Join(" ", p.Streams.Select(s => $"{s.Name}@{s.First}x{s.Count}:{s.Type}"))}; first {string.Join(" ", p.Vertices.Take(p.Stride).Select(F))}");
    }

    /// <summary><see cref="NodeOverlays.FromWorld"/> against the captured descriptors (<c>OVERLAYS</c>, <c>OVERLAYS_VMAP</c>).</summary>
    [Fact]
    public void DescriptorsFromVmap()
    {
        if (Environment.GetEnvironmentVariable("OVERLAYS") is not { } path || Environment.GetEnvironmentVariable("OVERLAYS_VMAP") is not { } vmap)
            return;
        var (valve, _, _) = Read(path);
        var ours = NodeOverlays.FromWorld(DmxBinary.ReadFile(vmap));
        output.WriteLine($"{ours.Count} ours, {valve.Count} valve");
        var tally = new SortedDictionary<string, int>();
        void Count(string k, bool ok) { if (!ok) tally[k] = tally.GetValueOrDefault(k) + 1; }
        var shown = 0;
        for (var i = 0; i < Math.Min(ours.Count, valve.Count); i++)
        {
            var (o, v) = (ours[i], valve[i]);
            Count("material", string.Equals(o.Material, v.Material, StringComparison.OrdinalIgnoreCase));
            Count("mode", o.Mode == v.Mode);
            Count("far", o.Far == v.Far);
            Count("order", o.RenderOrder == v.RenderOrder);
            Count("back", o.BackFaces == v.BackFaces);
            Count("angle", o.BackFacingAngle == v.Angle);
            Count("faces", o.Faces.Length == v.Faces.Length);
            for (var f = 0; f < Math.Min(o.Faces.Length, v.Faces.Length); f++)
            {
                var pos = o.Faces[f].Positions.SequenceEqual(v.Faces[f].Positions);
                var uv = o.Faces[f].Texcoords.SequenceEqual(v.Faces[f].Texcoords);
                Count("positions", pos);
                Count("texcoords", uv);
                if (!uv && Environment.GetEnvironmentVariable("OVERLAYS_UVS") == "1")
                {
                    var ou = o.Faces[f].Texcoords;
                    var vu = v.Faces[f].Texcoords;
                    output.WriteLine($"  uv node {o.NodeId}: ours u [{ou.Min(x => x.X)},{ou.Max(x => x.X)}] v [{ou.Min(x => x.Y)},{ou.Max(x => x.Y)}] shift {ou[0] - vu[0]} same-shift {ou.Zip(vu).All(z => z.First - z.Second == ou[0] - vu[0])}");
                }
                if ((!pos || !uv) && shown++ < 4)
                    output.WriteLine($"  {i} node {o.NodeId} {Path.GetFileName(o.Material)}: ours {string.Join(" ", o.Faces[f].Positions)} / {string.Join(" ", o.Faces[f].Texcoords)}; valve {string.Join(" ", v.Faces[f].Positions)} / {string.Join(" ", v.Faces[f].Texcoords)}");
            }
        }
        output.WriteLine($"differences: {string.Join(", ", tally.Select(kv => $"{kv.Key} {kv.Value}"))}");
    }
}
