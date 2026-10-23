using System.Numerics;
using Source2.Compiler.Maps;
using Source2.Compiler.Physics;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Replays the cable collision tubes captured by <c>tools/physics/capture_cable.py</c>
/// (<c>CABLE_CAPTURE=&lt;capture&gt;</c>, with <c>CABLE_VMAP=&lt;the map&gt;</c> for the
/// control points): every vf277 call's control points must be some cable's
/// <see cref="EntityLumpAuthor.PathControls"/>, and from them
/// <see cref="CableCollision"/> must give the samples, the tube and the mesh
/// vf277 returned, bit for bit. The probe's nodes carry no radius scale. Each
/// DmeMesh CMapCable::vf200 builds ('dme') must be some cable's render tube
/// (<see cref="CableCollision.RenderTube"/>) or its collision tube moved by
/// its node matrix (a cable_static's world export).
/// </summary>
public class CableReplay(ITestOutputHelper output)
{
    [Fact]
    public void TubesMatchValve()
    {
        if (Environment.GetEnvironmentVariable("CABLE_CAPTURE") is not { } path)
            return;
        var known = new List<float[]>();
        var exports = new List<(float[] Points, int[] Indices)>();
        if (Environment.GetEnvironmentVariable("CABLE_VMAP") is { } vmap)
        {
            var schema = MapFixtures.GameSchema();
            foreach (var element in DmxBinary.ReadFile(vmap).OfType("CMapCable"))
            {
                if (MapEntities.ReadNode(element) is not { } node)
                    continue;
                var (c, sc) = EntityLumpAuthor.PathControls(node, schema);
                var settings = CableCollision.SettingsOf(element);
                var render = CableCollision.RenderTube(c, sc, settings);
                exports.Add((Flat(render.Points), render.Indices));
                var physics = CableCollision.Build(c, sc, settings);
                var m = CableCollision.NodeMatrix(node);
                exports.Add((Flat([.. physics.Points.Select(x => MapMeshes.Transform(m, x))]), physics.Indices));
                // A cable_static's render export goes to the world's render, in world space too.
                exports.Add((Flat([.. render.Points.Select(x => MapMeshes.Transform(m, x))]), render.Indices));
            }
            foreach (var entity in MapEntities.From(DmxBinary.ReadFile(vmap)).Where(e => e.PathNodes != null && e.Tint != null))
            {
                known.Add(EntityLumpAuthor.PathControls(entity, schema).Controls);
                if (Environment.GetEnvironmentVariable("CABLE_DUMP") != null)
                    output.WriteLine($"node {entity.NodeId} type {entity.InterpolationType} loop {entity.ClosedLoop}: " + string.Join(" ", known[^1].Take(27).Select(x => x.ToString("R"))));
            }
        }

        var records = SimplifyReplay.Records(path).ToList();
        int cables = 0, controls = 0, samples = 0, tubes = 0, outs = 0, dmes = 0, dmesExact = 0;
        CableCollision.Settings? cable = null;
        float[] points = [];
        foreach (var (head, blob) in records)
        {
            if (!head.TryGetProperty("id", out var idElement))
                continue;
            var id = idElement.GetInt32();
            switch (head.GetProperty("ev").GetString())
            {
                case "cable":
                    cables++;
                    cable = new CableCollision.Settings(head.GetProperty("sides").GetInt32(), head.GetProperty("spacing").GetSingle(),
                        head.GetProperty("radius").GetSingle(), head.GetProperty("flip").GetInt32() != 0,
                        head.GetProperty("closed").GetInt32() != 0, head.GetProperty("error").GetSingle());
                    break;
                case "controls":
                    points = Floats(blob, 0, blob.Length / 4);
                    if (known.Count == 0 || known.Any(k => Bits(k, points)))
                        controls++;
                    else
                    {
                        var near = known.Where(k => k.Length == points.Length)
                            .MinBy(k => k.Zip(points, (a, b) => MathF.Abs(a - b)).Sum());
                        output.WriteLine($"cable {id}: control points are no cable's; nearest{FirstDifference(near ?? [], points)}");
                        if (near != null)
                            output.WriteLine("  ours  " + string.Join(" ", near.Take(27).Select(x => x.ToString("R"))) + " | valve " + string.Join(" ", points.Take(27).Select(x => x.ToString("R"))));
                    }
                    break;
                case "samples":
                {
                    var (ours, _) = CableCollision.Samples(points, cable!.Spacing);
                    if (Bits(Flat(ours), Floats(blob, 0, blob.Length / 4)))
                        samples++;
                    else
                        output.WriteLine($"cable {id}: {ours.Length} samples, Valve {blob.Length / 12}{FirstDifference(Flat(ours), Floats(blob, 0, blob.Length / 4))}");
                    break;
                }
                case "dme":
                {
                    dmes++;
                    var nv = head.GetProperty("nv").GetInt32();
                    var ni = head.GetProperty("ni").GetInt32();
                    var valve = Floats(blob, 0, nv * 3);
                    var valveIndices = Enumerable.Range(0, ni).Select(i => BitConverter.ToInt32(blob, nv * 12 + i * 4)).ToArray();
                    if (exports.Count == 0 || exports.Any(e => Bits(e.Points, valve) && e.Indices.AsSpan().SequenceEqual(valveIndices)))
                        dmesExact++;
                    else
                    {
                        var near = exports.Where(e => e.Points.Length == valve.Length).MinBy(e => e.Points.Zip(valve, (x, y) => MathF.Abs(x - y)).Sum());
                        output.WriteLine($"dme after cable {id}: {nv} points {ni} indices are no cable's export; nearest{FirstDifference(near.Points ?? [], valve)}"
                                         + (near.Indices != null && near.Indices.AsSpan().SequenceEqual(valveIndices) ? "" : " (indices differ)"));
                    }
                    break;
                }
                case "tube":
                case "out":
                {
                    var nv = head.GetProperty("nv").GetInt32();
                    var ni = head.GetProperty("ni").GetInt32();
                    var valve = Floats(blob, 0, nv * 3);
                    var valveIndices = Enumerable.Range(0, ni).Select(i => BitConverter.ToInt32(blob, nv * 12 + i * 4)).ToArray();
                    var scales = Enumerable.Repeat((1f, 1f), points.Length / 9).ToArray();
                    var tube = head.GetProperty("ev").GetString() == "tube";
                    var (ourPoints, ourIndices) = tube ? CableCollision.Tube(points, scales, cable!) : CableCollision.Build(points, scales, cable!);
                    if (Bits(Flat(ourPoints), valve) && ourIndices.AsSpan().SequenceEqual(valveIndices))
                    {
                        if (tube) tubes++; else outs++;
                    }
                    else
                        output.WriteLine($"cable {id} {(tube ? "tube" : "out")}: {ourPoints.Length} points {ourIndices.Length} indices, Valve {nv} and {ni}"
                                         + FirstDifference(Flat(ourPoints), valve)
                                         + (ourIndices.AsSpan().SequenceEqual(valveIndices) ? "" : "; indices differ"));
                    break;
                }
            }
        }
        output.WriteLine($"{cables} cables: controls {controls}, samples {samples}, tubes {tubes}, meshes {outs}; {dmes} exports, {dmesExact} exact");
        Assert.Equal(dmes, dmesExact);
        Assert.Equal(cables, controls);
        Assert.Equal(cables, samples);
        Assert.Equal(cables, tubes);
        Assert.Equal(cables, outs);
    }

    /// <summary><c>CABLE_PHYS=&lt;vmdl_c&gt;</c>: the model's PHYS tree, arrays cut short (a look at Valve's cable models).</summary>
    [Fact]
    public void DumpsPhys()
    {
        if (Environment.GetEnvironmentVariable("CABLE_PHYS") is not { } path)
            return;
        foreach (var file in path.Split(';'))
        {
            output.WriteLine("== " + file);
            using (var res = new ValveResourceFormat.Resource())
            {
                res.Read(file);
                var model = (ValveResourceFormat.ResourceTypes.Model)res.DataBlock!;
                foreach (var (mesh, _, _, _) in model.GetEmbeddedMeshesAndLoD())
                    foreach (var vb in mesh.VBIB.VertexBuffers)
                    {
                        var field = vb.InputLayoutFields.First(f => f.SemanticName == "POSITION");
                        var positions = ValveResourceFormat.Blocks.VBIB.GetVector3AttributeArray(vb, field);
                        output.WriteLine($"render vb {positions.Length} positions ({field.Format}): " + string.Join(" ", positions.Take(16).Select(x => $"({x.X:R},{x.Y:R},{x.Z:R})")));
                    }
                var phys = model.GetEmbeddedPhys();
                foreach (var mesh in phys?.Parts[0].Shape.Meshes ?? [])
                {
                    var v = mesh.Shape.GetVertices().ToArray();
                    var t = mesh.Shape.GetTriangles().ToArray();
                    output.WriteLine($"mesh: {v.Length} vertices {t.Length} triangles");
                    output.WriteLine("  v " + string.Join(" ", v.Take(30).Select(x => $"({x.X:R},{x.Y:R},{x.Z:R})")));
                    output.WriteLine("  t " + string.Join(" ", t.Take(30).Select(x => $"{x.X},{x.Y},{x.Z}")));
                }
            }
            foreach (var (name, tree) in WorldPhysicsAuthorTests.Trees(File.ReadAllBytes(file)))
                if (name is "PHYS" or "DATA")
                    foreach (var line in KvTreeDiff.Typed(tree, name, 0, 3))
                        output.WriteLine(line);
        }
    }

    /// <summary><c>CABLE_RENDER=&lt;capture&gt;|&lt;id&gt;</c>: the render tube of a captured cable beside the map builder's points (capture_mapsimplify.py).</summary>
    [Fact]
    public void RenderTubeBesideMapBuilder()
    {
        if (Environment.GetEnvironmentVariable("CABLE_RENDER") is not { } spec || Environment.GetEnvironmentVariable("CABLE_CAPTURE") is not { } path)
            return;
        var parts = spec.Split('|');
        var records = SimplifyReplay.Records(path).ToList();
        var head = records.First(r => r.Head.GetProperty("ev").GetString() == "cable" && r.Head.GetProperty("id").GetInt32() == int.Parse(parts[1])).Head;
        var controls = records.First(r => r.Head.GetProperty("ev").GetString() == "controls" && r.Head.GetProperty("id").GetInt32() == int.Parse(parts[1])).Blob;
        var points = Floats(controls, 0, controls.Length / 4);
        var cable = new CableCollision.Settings(head.GetProperty("sides").GetInt32(), head.GetProperty("spacing").GetSingle(),
            head.GetProperty("radius").GetSingle(), head.GetProperty("flip").GetInt32() != 0, head.GetProperty("closed").GetInt32() != 0, head.GetProperty("error").GetSingle());
        var (tube, indices) = CableCollision.RenderTube(points, Enumerable.Repeat((1f, 1f), points.Length / 9).ToArray(), cable);
        output.WriteLine($"render tube {tube.Length} points {indices.Length / 3} triangles");
        output.WriteLine("  " + string.Join(" ", tube.Take(15).Select(x => $"({x.X:R},{x.Y:R},{x.Z:R})")));
        if (parts.Length > 2)
            File.WriteAllBytes(parts[2], tube.SelectMany(x => BitConverter.GetBytes(x.X).Concat(BitConverter.GetBytes(x.Y)).Concat(BitConverter.GetBytes(x.Z))).ToArray());
    }

    private static float[] Floats(byte[] blob, int first, int count)
        => Enumerable.Range(0, count).Select(i => BitConverter.ToSingle(blob, (first + i) * 4)).ToArray();

    private static float[] Flat(Vector3[] v) => v.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();

    private static bool Bits(float[] a, float[] b)
        => a.Length == b.Length && a.Select(BitConverter.SingleToInt32Bits).SequenceEqual(b.Select(BitConverter.SingleToInt32Bits));

    private static string FirstDifference(float[] a, float[] b)
    {
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i]))
                return $"; first float {i}: {a[i]:R} vs {b[i]:R}";
        return "";
    }
}
