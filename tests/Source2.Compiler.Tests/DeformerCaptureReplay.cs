using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="LatticeDeformer"/> against PropDeformer_Transform as a compile
/// called it (<c>DEFORMCAP=&lt;tools/physics/capture_deformer.py output&gt;</c>):
/// each call's deformer rebuilt from its captured struct, the captured points
/// deformed with the captured matrix, and compared bit for bit with Valve's.
/// </summary>
public class DeformerCaptureReplay(ITestOutputHelper output)
{
    internal sealed record Call(LatticeDeformer Deformer, float[] Matrix, Vector3[] In, Vector3[] Out, JsonElement Meta)
    {
        /// <summary>Normals and tangents in and out, empty when the call had none (or the capture predates them).</summary>
        public Vector3[] NormalsIn { get; init; } = [];
        public Vector3[] TangentsIn { get; init; } = [];
        public Vector3[] NormalsOut { get; init; } = [];
        public Vector3[] TangentsOut { get; init; } = [];
    }

    internal static List<Call> Read(string path)
    {
        var data = File.ReadAllBytes(path);
        var calls = new List<Call>();
        for (var at = 0; at < data.Length;)
        {
            var n = BitConverter.ToInt32(data, at);
            var head = JsonDocument.Parse(data.AsMemory(at + 4, n)).RootElement.Clone();
            at += 4 + n;
            var m = BitConverter.ToInt32(data, at);
            var blob = data.AsSpan(at + 4, m).ToArray();
            at += 4 + m;
            var sizes = head.GetProperty("sizes").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var offsets = new int[sizes.Length];
            for (var i = 1; i < sizes.Length; i++)
                offsets[i] = offsets[i - 1] + sizes[i - 1];
            float F(int o) => BitConverter.ToSingle(blob, o);
            Vector3[] V(int part) => [.. Enumerable.Range(0, sizes[part] / 12).Select(i => new Vector3(F(offsets[part] + i * 12), F(offsets[part] + i * 12 + 4), F(offsets[part] + i * 12 + 8)))];
            var d = new LatticeDeformer
            {
                Transform = new CTransform(new Vector3(F(0), F(4), F(8)), F(12), new Quaternion(F(16), F(20), F(24), F(28))),
                Size = new Vector3(F(32), F(36), F(40)),
                Segments = BitConverter.ToInt32(blob, 0x2c),
                DivisionsY = BitConverter.ToInt32(blob, 0x30),
                DivisionsZ = BitConverter.ToInt32(blob, 0x34),
                Mode = BitConverter.ToInt32(blob, 0x38),
                Mirror = blob[0x70] != 0,
                Points = V(1),
                Handles = V(2),
            };
            var matrix = Enumerable.Range(0, 12).Select(i => F(offsets[3] + i * 4)).ToArray();
            calls.Add(new Call(d, matrix, V(4), V(5), head)
            {
                NormalsIn = sizes.Length > 6 ? V(6) : [],
                TangentsIn = sizes.Length > 7 ? V(7) : [],
                NormalsOut = sizes.Length > 8 ? V(8) : [],
                TangentsOut = sizes.Length > 9 ? V(9) : [],
            });
        }
        return calls;
    }

    [Fact]
    public void PointsAgainstCapture()
    {
        if (Environment.GetEnvironmentVariable("DEFORMCAP") is not { } path)
            return;
        var calls = Read(path);
        int exact = 0, points = 0, pointsExact = 0, shown = 0;
        foreach (var c in calls)
        {
            var e = c.Deformer.For(c.Matrix);
            var same = 0;
            for (var i = 0; i < c.In.Length; i++)
            {
                var got = e.Deform(c.In[i]);
                var want = c.Out[i];
                if (BitConverter.SingleToInt32Bits(got.X) == BitConverter.SingleToInt32Bits(want.X)
                    && BitConverter.SingleToInt32Bits(got.Y) == BitConverter.SingleToInt32Bits(want.Y)
                    && BitConverter.SingleToInt32Bits(got.Z) == BitConverter.SingleToInt32Bits(want.Z))
                    same++;
                else if (shown++ < 6)
                    output.WriteLine($"mode {c.Deformer.Mode} segs {c.Deformer.Segments}: in {c.In[i]} ours {got:R} valve {want:R} (diff {got - want})");
            }
            points += c.In.Length;
            pointsExact += same;
            if (same == c.In.Length)
                exact++;
            else if (Environment.GetEnvironmentVariable("DEFORMCAP_LIST") == "1")
                output.WriteLine($"inexact: {c.In.Length - same}/{c.In.Length} points, mode {c.Deformer.Mode}, at {c.Deformer.Transform.Position:R} {c.Deformer.Transform.Rotation:R}, matrix {string.Join(" ", c.Matrix.Select(x => x.ToString("R", null)))}");
        }
        output.WriteLine($"{calls.Count} calls, {exact} exact; {pointsExact}/{points} points");
        if (Environment.GetEnvironmentVariable("DEFORMCAP_MATRICES") == "1")
            foreach (var g in calls.GroupBy(c => $"{c.Deformer.Transform.Position:R} | {string.Join(" ", c.Matrix.Select(x => x.ToString("R", null)))}"))
                output.WriteLine($"matrices: {g.Count()} calls, {g.Sum(c => c.In.Length)} points, normals {g.Sum(c => c.NormalsIn.Length)}: {g.Key}; first in {g.First().In[0]:R}");
        // Normals and tangents, where the call carried them.
        int normals = 0, normalsExact = 0, tangents = 0, tangentsExact = 0;
        static bool Same(Vector3 a, Vector3 b)
            => BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
               && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y)
               && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);
        foreach (var c in calls)
        {
            var e = c.Deformer.For(c.Matrix);
            for (var i = 0; i < c.NormalsIn.Length; i++, normals++)
                if (Same(e.DeformNormal(c.In[i], c.NormalsIn[i]), c.NormalsOut[i]))
                    normalsExact++;
                else if (shown++ < 12)
                    output.WriteLine($"normal at {c.In[i]}: in {c.NormalsIn[i]:R} ours {e.DeformNormal(c.In[i], c.NormalsIn[i]):R} valve {c.NormalsOut[i]:R}");
            for (var i = 0; i < c.TangentsIn.Length; i++, tangents++)
                if (Same(e.DeformTangent(c.In[i], c.TangentsIn[i]), c.TangentsOut[i]))
                    tangentsExact++;
                else if (shown++ < 12)
                    output.WriteLine($"tangent at {c.In[i]}: in {c.TangentsIn[i]:R} ours {e.DeformTangent(c.In[i], c.TangentsIn[i]):R} valve {c.TangentsOut[i]:R}");
        }
        output.WriteLine($"normals {normalsExact}/{normals}, tangents {tangentsExact}/{tangents}");
        Assert.Equal(calls.Count, exact);
        Assert.Equal(normals, normalsExact);
        Assert.Equal(tangents, tangentsExact);
    }

    /// <summary>
    /// <see cref="LatticeDeformer.FromNode"/> for every deformer node of the
    /// captured map (<c>DEFORMVMAP</c>) against the struct the compile handed
    /// over for it (matched by the transform's position): transform, size,
    /// counts, mode, mirror, control points and handles bit for bit.
    /// </summary>
    [Fact]
    public void NodesAgainstCapture()
    {
        if (Environment.GetEnvironmentVariable("DEFORMCAP") is not { } path || Environment.GetEnvironmentVariable("DEFORMVMAP") is not { } vmap)
            return;
        var captured = Read(path).Select(c => c.Deformer).ToList();
        var doc = DmxBinary.ReadFile(vmap);
        PathDeformer.Resolve(doc, MapFixtures.GameSchema());
        static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
        static bool SameV(Vector3 a, Vector3 b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);
        var bad = 0;
        foreach (var node in doc.Elements.Where(e => e.Type.StartsWith("CMapDeformer", StringComparison.Ordinal)))
        {
            var ours = LatticeDeformer.FromNode(node);
            var want = captured.FirstOrDefault(c => SameV(c.Transform.Position, ours.Transform.Position));
            if (want == null)
            {
                output.WriteLine($"{node.Type} {node.GetValue<int>("nodeID")}: no captured call");
                bad++;
                continue;
            }
            var diffs = new List<string>();
            if (!SameV(ours.Size, want.Size))
                diffs.Add($"size {ours.Size:R} vs {want.Size:R}");
            var (q, w) = (ours.Transform.Rotation, want.Transform.Rotation);
            if (!Same(q.X, w.X) || !Same(q.Y, w.Y) || !Same(q.Z, w.Z) || !Same(q.W, w.W) || !Same(ours.Transform.Scale, want.Transform.Scale))
                diffs.Add($"rotation {q} vs {w}");
            if ((ours.Segments, ours.DivisionsY, ours.DivisionsZ, ours.Mode, ours.Mirror) != (want.Segments, want.DivisionsY, want.DivisionsZ, want.Mode, want.Mirror))
                diffs.Add($"counts {(ours.Segments, ours.DivisionsY, ours.DivisionsZ, ours.Mode, ours.Mirror)} vs {(want.Segments, want.DivisionsY, want.DivisionsZ, want.Mode, want.Mirror)}");
            for (var i = 0; i < ours.Points.Length; i++)
                if (i >= want.Points.Length || !SameV(ours.Points[i], want.Points[i]))
                    diffs.Add($"point {i}: {ours.Points[i]:R} vs {(i < want.Points.Length ? want.Points[i].ToString("R", null) : "-")}");
            for (var i = 0; i < ours.Handles.Length; i++)
                if (i >= want.Handles.Length || !SameV(ours.Handles[i], want.Handles[i]))
                    diffs.Add($"handle {i}: {ours.Handles[i]:R} vs {(i < want.Handles.Length ? want.Handles[i].ToString("R", null) : "-")}");
            output.WriteLine($"{node.Type} {node.GetValue<int>("nodeID")}: {(diffs.Count == 0 ? "exact" : string.Join("; ", diffs.Take(8)))}");
            if (diffs.Count > 0)
                bad++;
        }
        Assert.Equal(0, bad);
    }
    /// <summary>
    /// Deformers moved by a collapse (DEFORMVMAP with instances or prefabs):
    /// each placed deformer's transform as the port computes it
    /// (<see cref="SettleWorld.PlacedDeformer"/>) against the captured transform
    /// nearest it, bit for bit.
    /// </summary>
    [Fact]
    public void PlacedDeformersAgainstCapture()
    {
        if (Environment.GetEnvironmentVariable("DEFORMCAP") is not { } path || Environment.GetEnvironmentVariable("DEFORMVMAP") is not { } vmap)
            return;
        var structs = Read(path).Select(c => c.Deformer).ToList();
        var captured = structs.Select(c => c.Transform).ToList();
        var doc = MapSource.Read(vmap);
        var (_, entities) = MapMeshes.ReadWithEntities(doc);
        var seen = new HashSet<string>();
        var bad = 0;
        foreach (var entity in entities.Where(e => e.Deformer != null && (e.Through.Count > 0 || e.PrefabChain.Count > 0)))
        {
            var d = entity.Deformer!;
            // A path deformer's struct holds the lattice's transform (PathDeformer.LatticeTransform), not the node's.
            var ours = d.Type == "CMapDeformerPath" ? LatticeDeformer.FromNode(d, entity.Through, entity.PrefabChain).Transform
                                                     : SettleWorld.PlacedDeformer(d, entity.Through, entity.PrefabChain);
            var key = $"{ours.Position:R} {ours.Rotation}";
            if (!seen.Add(key))
                continue;
            var want = captured.OrderBy(c => Vector3.Distance(c.Position, ours.Position)).First();
            output.WriteLine($"deformer {d.GetValue<int>("nodeID")} via {entity.Through.Count} instances, {entity.PrefabChain.Count} prefabs: ours {ours.Position:R} {ours.Rotation:R} valve {want.Position:R} {want.Rotation:R}");
            if (ours.Position != want.Position || ours.Rotation != want.Rotation)
                bad++;
            var full = structs.First(c => c.Transform.Position == want.Position && c.Transform.Rotation == want.Rotation);
            var mine = LatticeDeformer.FromNode(d, entity.Through, entity.PrefabChain);
            // The capture reads twice as many handles as the port keeps (as NodesAgainstCapture compares: the port's).
            if (mine.Size != full.Size || !mine.Points.SequenceEqual(full.Points) || !mine.Handles.SequenceEqual(full.Handles.Take(mine.Handles.Length)))
            {
                var first = Enumerable.Range(0, Math.Min(mine.Points.Length, full.Points.Length)).FirstOrDefault(i => mine.Points[i] != full.Points[i], -1);
                var h = Enumerable.Range(0, Math.Min(mine.Handles.Length, full.Handles.Length)).FirstOrDefault(i => mine.Handles[i] != full.Handles[i], -1);
                output.WriteLine($"  struct differs: size {mine.Size:R} vs {full.Size:R}; point {first}: {(first >= 0 ? $"{mine.Points[first]:R} vs {full.Points[first]:R}" : "-")}; "
                                 + $"handles {mine.Handles.Length} vs {full.Handles.Length}, first {h}: {(h >= 0 ? $"{mine.Handles[h]:R} vs {full.Handles[h]:R}" : "-")}");
                bad++;
            }
        }
        Assert.Equal(0, bad);
    }
    /// <summary>
    /// PROPMATRIXCAP=&lt;capture_propmatrix.py output&gt; with DEFORMVMAP: each
    /// prop_static under a prefab, the angles and origin the port hands
    /// physicsbuilder against the captured call nearest it, bit for bit.
    /// </summary>
    [Fact]
    public void PrefabPropTransformsAgainstCapture()
    {
        if (Environment.GetEnvironmentVariable("PROPMATRIXCAP") is not { } path || Environment.GetEnvironmentVariable("DEFORMVMAP") is not { } vmap)
            return;
        static float F(System.Text.Json.JsonElement e, int i) => BitConverter.Int32BitsToSingle(unchecked((int)e[i].GetUInt32()));
        var captured = File.ReadLines(path).Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
            .Where(r => r.GetProperty("ev").GetString() == "prop")
            .Select(r => (Origin: new Vector3(F(r.GetProperty("origin"), 0), F(r.GetProperty("origin"), 1), F(r.GetProperty("origin"), 2)),
                          Angles: new Vector3(F(r.GetProperty("angles"), 0), F(r.GetProperty("angles"), 1), F(r.GetProperty("angles"), 2)))).ToList();
        var (_, entities) = MapMeshes.ReadWithEntities(MapSource.Read(vmap));
        int bad = 0, shown = 0;
        foreach (var entity in entities.Where(e => (e.PrefabChain.Count > 0 || e.Through.Count > 0)
                                                   && e.Element.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname") == "prop_static"))
        {
            var o = entity.Element.GetValue<Vector3>("origin") ?? Vector3.Zero;
            var a = entity.Element.GetValue<Vector3>("angles") ?? Vector3.Zero;
            var placed = Physics.WorldCollision.PropOf(entity) is var prop ? (Origin: prop.Origin, Angles: prop.Angles) : default;
            var want = captured.OrderBy(c => Vector3.Distance(c.Origin, placed.Origin)).First();
            static bool Bits(Vector3 x, Vector3 y) => BitConverter.SingleToInt32Bits(x.X) == BitConverter.SingleToInt32Bits(y.X)
                && BitConverter.SingleToInt32Bits(x.Y) == BitConverter.SingleToInt32Bits(y.Y) && BitConverter.SingleToInt32Bits(x.Z) == BitConverter.SingleToInt32Bits(y.Z);
            var ok = Bits(placed.Origin, want.Origin) && Bits(placed.Angles, want.Angles);
            if (!ok)
                bad++;
            if (!ok && shown++ < 12)
                output.WriteLine($"prop {entity.Element.GetValue<int>("nodeID")} angles {a:R}: ours {placed.Origin:R} {placed.Angles:R}; valve {want.Origin:R} {want.Angles:R}");
        }
        output.WriteLine($"{bad} prefab props differ");
        Assert.Equal(0, bad);
    }
    /// <summary>
    /// PBMESHCAP=&lt;capture_pbmeshnode.py output&gt; with DEFORMVMAP: each of
    /// physicsbuilder's deformed mesh nodes replayed from its captured points
    /// and matrix through the port's deformer for that placement
    /// (<see cref="SettleWorld.PlacedDeformer"/>, matched by position), bit for bit.
    /// </summary>
    [Fact]
    public void PhysicsBuilderMeshNodesAgainstCapture()
    {
        if (Environment.GetEnvironmentVariable("PBMESHCAP") is not { } path || Environment.GetEnvironmentVariable("DEFORMVMAP") is not { } vmap)
            return;
        static float F(System.Text.Json.JsonElement e, int i) => BitConverter.Int32BitsToSingle(unchecked((int)e[i].GetUInt32()));
        var doc = MapSource.Read(vmap);
        var (_, entities) = MapMeshes.ReadWithEntities(doc);
        var deformers = entities.Where(e => e.Deformer != null).Select(e => (Node: e.Deformer!, e.Through, e.PrefabChain)).ToList();
        int calls = 0, exact = 0, shown = 0;
        foreach (var line in File.ReadLines(path))
        {
            var r = System.Text.Json.JsonDocument.Parse(line).RootElement;
            if (r.GetProperty("ev").GetString() != "node")
                continue;
            calls++;
            var m = Enumerable.Range(0, 12).Select(i => F(r.GetProperty("matrix"), i)).ToArray();
            var d = r.GetProperty("deformer");
            var at = new Vector3(F(d, 0), F(d, 1), F(d, 2));
            var match = deformers.Select(x => (x, T: SettleWorld.PlacedDeformer(x.Node, x.Through, x.PrefabChain))).FirstOrDefault(x => x.T.Position == at);
            if (match.x.Node == null)
            {
                output.WriteLine($"no deformer at {at:R}");
                continue;
            }
            var deformer = LatticeDeformer.FromNode(match.x.Node, match.x.Through, match.x.PrefabChain);
            var evaluator = deformer.For([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0]);
            var input = r.GetProperty("in");
            var outPts = r.GetProperty("out");
            var ok = true;
            for (var v = 0; v * 3 < input.GetArrayLength(); v++)
            {
                var moved = MapMeshes.Transform(m, new Vector3(F(input, v * 3), F(input, (v * 3) + 1), F(input, (v * 3) + 2)));
                var got = evaluator.Deform(moved);
                var want = new Vector3(F(outPts, v * 3), F(outPts, (v * 3) + 1), F(outPts, (v * 3) + 2));
                if (got != want)
                {
                    ok = false;
                    if (shown++ < 6)
                        output.WriteLine($"deformer at {at:R} mode {deformer.Mode}: moved {moved:R} ours {got:R} valve {want:R}");
                }
            }
            if (ok)
                exact++;
        }
        output.WriteLine($"{calls} calls, {exact} exact");
        Assert.Equal(calls, exact);
    }
}
