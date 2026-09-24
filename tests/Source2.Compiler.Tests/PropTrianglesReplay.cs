using System.Buffers.Binary;
using System.Numerics;
using Source2.Compiler.Maps;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Serialization.KeyValues;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: each entity with a model placed by its node matrix, its first
/// lod's render triangles pushed through the compile's point transform, and
/// each triangle's tracer record looked up in the .rte of the same compile.
/// On atixref and Mako none is found, and with <c>PROPTRI_NEAR</c> set almost
/// no placed vertex has an .rte vertex within 0.05: those compiles trace no
/// props. <c>PROPTRI=&lt;addon&gt;;&lt;map&gt;;&lt;rte&gt;</c>.
/// </summary>
public class PropTrianglesReplay(ITestOutputHelper output)
{
    [Fact]
    public void ThePropTriangles()
    {
        if (Environment.GetEnvironmentVariable("PROPTRI") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split(';');
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var game = Path.Combine(cs2, "game");
        var doc = DmxBinary.ReadFile(Path.Combine(cs2, "content", "csgo_addons", parts[0], "maps", parts[1] + ".vmap"));
        var packages = new List<Package>();
        foreach (var dir in new[] { "csgo", "core" })
        {
            var package = new Package();
            package.Read(Path.Combine(game, dir, "pak01_dir.vpk"));
            packages.Add(package);
        }
        var addon = Path.Combine(game, "csgo_addons", parts[0]);
        byte[]? Load(string name)
        {
            var compiled = name.Replace('\\', '/') + "_c";
            var loose = Path.Combine(addon, compiled);
            if (File.Exists(loose))
                return File.ReadAllBytes(loose);
            foreach (var package in packages)
            {
                if (package.FindEntry(compiled) is { } entry)
                {
                    package.ReadEntry(entry, out var bytes);
                    return bytes;
                }
            }
            return null;
        }

        var rte = RayTraceEnvironment.ReadFile(parts[2]);
        var file = new HashSet<string>();
        for (var i = 0; i < rte.TriangleCount; i++)
            file.Add(Key(rte.FileRecord(i)));

        var models = new Dictionary<string, (Vector3[] Positions, int[] Triangles)?>();
        var byClass = new SortedDictionary<string, (int Found, int Total)>();
        var byModel = new Dictionary<string, (int Found, int Total, int Props)>();
        var c = new float[9];
        var r = new float[13];
        foreach (var (entity, path) in Entities(doc))
        {
            var properties = entity.Get<DmxBinary.Element>("entity_properties");
            var className = properties?.Get<string>("classname") ?? "";
            if (properties?.Get<string>("model") is not { Length: > 0 } model)
                continue;
            if (!models.TryGetValue(model, out var mesh))
                models[model] = mesh = Triangles(Load, model);
            if (mesh is not { } m)
                continue;
            var angles = entity.GetValue<Vector3>("angles") ?? Vector3.Zero;
            var origin = entity.GetValue<Vector3>("origin") ?? Vector3.Zero;
            var scales = entity.GetValue<Vector3>("scales") ?? Vector3.One;
            var world = Concat(path, Local(angles, origin, scales));
            var placed = m.Positions.Select(p => Transform(world, p)).ToArray();
            int found = 0, total = 0;
            for (var t = 0; t < m.Triangles.Length; t += 3)
            {
                for (var k = 0; k < 3; k++)
                {
                    var p = placed[m.Triangles[t + k]];
                    c[k * 3] = p.X; c[(k * 3) + 1] = p.Y; c[(k * 3) + 2] = p.Z;
                }
                if (!RayTraceEnvironment.RecordFromCorners(c, r))
                    continue;
                total++;
                if (file.Contains(Key(r)))
                    found++;
            }
            var (f0, t0) = byClass.GetValueOrDefault(className);
            byClass[className] = (f0 + found, t0 + total);
            var (f1, t1, n1) = byModel.GetValueOrDefault(model);
            byModel[model] = (f1 + found, t1 + total, n1 + 1);
        }
        if (Environment.GetEnvironmentVariable("PROPTRI_NEAR") is not null)
        {
            // rte vertices on a grid, then per model the share of placed vertices with an rte vertex within 0.05
            var grid = new HashSet<(int, int, int)>();
            for (var i = 0; i < rte.TriangleCount; i++)
            {
                if (rte.Vertices(i) is not { } v)
                    continue;
                foreach (var q in v)
                    grid.Add(((int)MathF.Floor(q.X * 10), (int)MathF.Floor(q.Y * 10), (int)MathF.Floor(q.Z * 10)));
            }
            bool Near(Vector3 q)
            {
                int x = (int)MathF.Floor(q.X * 10), y = (int)MathF.Floor(q.Y * 10), z = (int)MathF.Floor(q.Z * 10);
                for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dz = -1; dz <= 1; dz++)
                            if (grid.Contains((x + dx, y + dy, z + dz)))
                                return true;
                return false;
            }
            var near = new Dictionary<string, (int Near, int All, int Props)>();
            foreach (var (entity, path) in Entities(doc))
            {
                var props = entity.Get<DmxBinary.Element>("entity_properties");
                if (props?.Get<string>("model") is not { Length: > 0 } mdl || models.GetValueOrDefault(mdl) is not { } mm)
                    continue;
                var world = Concat(path, Local(entity.GetValue<Vector3>("angles") ?? Vector3.Zero, entity.GetValue<Vector3>("origin") ?? Vector3.Zero, entity.GetValue<Vector3>("scales") ?? Vector3.One));
                var hits = mm.Positions.Count(q => Near(Transform(world, q)));
                var key = $"{props.Get<string>("classname")} {mdl}";
                var (a, b, n) = near.GetValueOrDefault(key);
                near[key] = (a + hits, b + mm.Positions.Length, n + 1);
            }
            foreach (var (k, (a, b, n)) in near.OrderByDescending(kv => (double)kv.Value.Near / kv.Value.All).Take(30))
                output.WriteLine($"  near {a}/{b} x{n}: {k}");
        }
        foreach (var (name, mesh) in models.Where(kv => kv.Value is null).Take(30))
            output.WriteLine($"  unloaded {name}");
        output.WriteLine($"  models {models.Count}, unloaded {models.Count(kv => kv.Value is null)}");
        foreach (var (name, (found, total)) in byClass)
            output.WriteLine($"{name}: {found}/{total}");
        foreach (var (name, (found, total, props)) in byModel.OrderByDescending(kv => kv.Value.Total - kv.Value.Found).Take(25))
            output.WriteLine($"  {name} x{props}: {found}/{total}");
        output.WriteLine($"rte {rte.TriangleCount}");
    }

    // Entities in world space: the ones under the world and groups, and each
    // instance's copy of its target's, with the instance path's matrix.
    private static IEnumerable<(DmxBinary.Element Entity, float[] Path)> Entities(DmxBinary.Document doc)
    {
        var targets = new HashSet<DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        foreach (var instance in doc.OfType("CMapInstance"))
        {
            if (instance.Get<DmxBinary.Element>("target") is { } target)
                targets.Add(target);
        }
        var found = new List<(DmxBinary.Element, float[])>();
        void Walk(DmxBinary.Element node, float[] path)
        {
            foreach (var child in node.GetElements("children"))
            {
                if (child.Type == "CMapEntity")
                    found.Add((child, path));
                if (child.Type == "CMapInstance")
                {
                    if (child.Get<DmxBinary.Element>("target") is { } target)
                    {
                        var step = Concat(Local(child.GetValue<Vector3>("angles") ?? Vector3.Zero, child.GetValue<Vector3>("origin") ?? Vector3.Zero, Vector3.One),
                                          Invert(Local(target.GetValue<Vector3>("angles") ?? Vector3.Zero, target.GetValue<Vector3>("origin") ?? Vector3.Zero, Vector3.One)));
                        Walk(target, Concat(path, step));
                    }
                }
                else if (!targets.Contains(child))
                    Walk(child, path);
            }
        }
        foreach (var world in doc.OfType("CMapWorld"))
            Walk(world, [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0]);
        return found;
    }

    private static (Vector3[] Positions, int[] Triangles)? Triangles(Func<string, byte[]?> load, string modelName)
    {
        if (load(modelName) is not { } bytes)
            return null;
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        if (resource.DataBlock is not Model model)
            return null;
        var positions = new List<Vector3>();
        var triangles = new List<int>();
        void AddMesh(Mesh mesh)
        {
            var vbib = mesh.VBIB;
            var starts = new Dictionary<int, int>();
            foreach (var sceneObject in mesh.Data.GetArray("m_sceneObjects"))
            {
                foreach (var drawCall in sceneObject.GetArray("m_drawCalls"))
                {
                    var vb = drawCall.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer");
                    if (!starts.TryGetValue(vb, out var start))
                    {
                        var buffer = vbib.VertexBuffers[vb];
                        var position = buffer.InputLayoutFields.FirstOrDefault(a => a.SemanticName == "POSITION");
                        if (position.SemanticName != "POSITION")
                        {
                            starts[vb] = -1;
                            continue;
                        }
                        start = positions.Count;
                        starts[vb] = start;
                        positions.AddRange(VBIB.GetVector3AttributeArray(buffer, position));
                    }
                    if (start < 0)
                        continue;
                    var indexBuffer = vbib.IndexBuffers[drawCall.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                    foreach (var index in ValveResourceFormat.IO.GltfModelExporter.ReadIndices(indexBuffer, drawCall.GetInt32Property("m_nStartIndex"),
                                                                                             drawCall.GetInt32Property("m_nIndexCount"), drawCall.GetInt32Property("m_nBaseVertex")))
                        triangles.Add(start + index);
                }
            }
        }
        foreach (var embedded in model.GetEmbeddedMeshesAndLoD())
        {
            if ((embedded.LoDMask & 1) != 0)
                AddMesh(embedded.Mesh);
        }
        foreach (var reference in model.GetReferenceMeshNamesAndLoD())
        {
            if ((reference.LoDMask & 1) == 0 || load(reference.MeshName) is not { } meshBytes)
                continue;
            using var meshResource = new Resource();
            meshResource.Read(new MemoryStream(meshBytes));
            if (meshResource.DataBlock is not Mesh mesh)
                continue;
            model.SetExternalMeshData(mesh);
            AddMesh(mesh);
        }
        return positions.Count > 0 ? (positions.ToArray(), triangles.ToArray()) : null;
    }

    private static Vector3 Transform(float[] m, Vector3 v)
        => new(((m[0] * v.X) + (m[2] * v.Z)) + ((m[1] * v.Y) + m[3]),
               ((m[4] * v.X) + (m[6] * v.Z)) + ((m[5] * v.Y) + m[7]),
               ((m[8] * v.X) + (m[10] * v.Z)) + ((m[9] * v.Y) + m[11]));

    // AngleMatrix with each column times its scale (FUN_181255ee0), the origin as translation.
    private static float[] Local(Vector3 angles, Vector3 origin, Vector3 scales)
    {
        const float Radians = 0.017453292f;
        float sp = MathF.Sin(angles.X * Radians), cp = MathF.Cos(angles.X * Radians);
        float sy = MathF.Sin(angles.Y * Radians), cy = MathF.Cos(angles.Y * Radians);
        float sr = MathF.Sin(angles.Z * Radians), cr = MathF.Cos(angles.Z * Radians);
        float[] m = [cy * cp, (sr * sp * cy) - (cr * sy), (cr * sp * cy) - (-sy * sr), origin.X,
                     sy * cp, (sr * sp * sy) + (cr * cy), (cr * sp * sy) - (sr * cy), origin.Y,
                     -sp, sr * cp, cr * cp, origin.Z];
        if (scales != Vector3.One)
        {
            m[0] = scales.X * m[0]; m[4] = scales.X * m[4]; m[8] = scales.X * m[8];
            m[1] = scales.Y * m[1]; m[5] = scales.Y * m[5]; m[9] = scales.Y * m[9];
            m[2] = scales.Z * m[2]; m[6] = scales.Z * m[6]; m[10] = scales.Z * m[10];
        }
        return m;
    }

    private static float[] Concat(float[] a, float[] b)
    {
        var o = new float[12];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 4; j++)
                o[(i * 4) + j] = (((a[(i * 4) + 2] * b[8 + j]) + (a[(i * 4) + 1] * b[4 + j])) + (a[i * 4] * b[j])) + (j == 3 ? a[(i * 4) + 3] : 0f);
        }
        return o;
    }

    private static float[] Invert(float[] m)
    {
        float[] o = [m[0], m[4], m[8], 0, m[1], m[5], m[9], 0, m[2], m[6], m[10], 0];
        for (var i = 0; i < 3; i++)
            o[(i * 4) + 3] = -(((m[11] * o[(i * 4) + 2]) + (m[7] * o[(i * 4) + 1])) + (m[3] * o[i * 4]));
        return o;
    }

    private static string Key(ReadOnlySpan<byte> r)
    {
        Span<float> f = stackalloc float[13];
        for (var k = 0; k < 11; k++)
            f[k] = BinaryPrimitives.ReadSingleLittleEndian(r[(k * 4)..]);
        f[11] = r[0x2c];
        f[12] = r[0x2d];
        return Key(f);
    }

    private static string Key(ReadOnlySpan<float> f)
    {
        var parts = new List<string>(12);
        foreach (var k in new[] { 0, 1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12 })
            parts.Add(BitConverter.SingleToInt32Bits(f[k]).ToString("x8"));
        return string.Join(",", parts);
    }
}
